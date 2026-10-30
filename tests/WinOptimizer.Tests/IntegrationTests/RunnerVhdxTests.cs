using System.Diagnostics;
using System.Text;
using WinOptimizer.Services.DiskSpace;
using WinOptimizer.Tests.Fakes;
using Xunit;
using Xunit.Abstractions;

namespace WinOptimizer.Tests.IntegrationTests;

/// <summary>
/// The product runner (DiskMoveRunner + WindowsDiskMoveBackend + DiskMove.dll) on a real attached VHDX, steps 0-7:
/// DLIKE gives 512 MiB to CLIKE ("C-like" target), RLIKE (Recovery-like) moves along. manageWinRe is false. Every
/// backend call goes through <see cref="GuardedBackend"/>, which refuses any disk but the fixture's VHDX (re-checked
/// on every call), any volume but DLIKE/RLIKE, any journal outside the scratch folder, and reagentc.
/// </summary>
[Trait("Category", "Integration")]
[Collection(VhdxCollection.Name)]
public sealed class RunnerVhdxTests(ITestOutputHelper output)
{
    const long Amount = 512 * VhdxFixture.MiB;
    const int FileCount = 1000;
    const int FileSeed = 20261029;

    readonly StringBuilder _log = new();

    void Log(string line)
    {
        string l = $"{DateTime.Now:HH:mm:ss.fff} {line}";
        lock (_log) _log.AppendLine(l);
        try { output.WriteLine(l); } catch (InvalidOperationException) { }
    }

    void SaveLog(string name)
    {
        lock (_log) File.WriteAllText(Path.Combine(VhdxFixture.ScratchDir, name + ".log"), _log.ToString());
    }

    sealed class LogProgress(Action<string> log) : IProgress<RunProgress>
    {
        string _last = "";
        long _lastPct = -1;

        public void Report(RunProgress p)
        {
            long pct = p.BytesTotal > 0 ? p.BytesDone * 100 / p.BytesTotal : -1;
            string key = $"{p.Step}|{p.Message}";
            if (key == _last && (pct < 0 || pct / 25 == _lastPct / 25)) return;
            _last = key;
            _lastPct = pct;
            log($"progress step {(int)p.Step} {p.Step}: {p.Message} {(pct >= 0 ? $"{pct}% of {p.BytesTotal}" : "")}");
        }
    }

    sealed record Case(VhdxFixture Vhdx, string Name, string Journal, DiskSnapshot Disk, PartitionSnapshot Target,
        PartitionSnapshot Source, PartitionSnapshot Recovery, MovePlan Plan, char Letter, uint Serial, char CLetter,
        ulong SourceAttrs, ulong RecoveryAttrs, IReadOnlyDictionary<string, string> Hashes);

    // ------------------------------------------------------------------ tests

    [AdminFact]
    public async Task FullRun_OnVhdx()
    {
        const string name = "runner-full";
        try
        {
            using var vhdx = VhdxFixture.Create(name, Log);
            var c = Prepare(vhdx, name);
            var runner = new DiskMoveRunner(new GuardedBackend(new WindowsDiskMoveBackend(), c, Log), c.Journal, manageWinRe: false);
            var sw = Stopwatch.StartNew();
            var result = await runner.RunAsync(c.Disk, c.Target, c.Source, c.Plan, () => [], new LogProgress(Log), () => false, CancellationToken.None);
            Log($"RunAsync took {sw.Elapsed.TotalSeconds:F1} s: {result.Outcome}, letter {result.FinalSourceLetter}, warnings [{string.Join(" | ", result.Warnings)}]");
            Assert.Equal(DiskMoveOutcome.Completed, result.Outcome);
            Assert.Equal(c.Letter, result.FinalSourceLetter);
            Assert.Empty(result.Warnings);
            AssertMoved(c);
        }
        finally
        {
            SaveLog(name);
        }
    }

    /// <summary>The run "crashes" (an exception from inside the progress callback, so DiskMove.dll stops between two
    /// chunks) part-way through the source (move 0) or Recovery (move 1) copy; a new runner with a new backend then
    /// resumes from the journal: the half-moved partition is hidden, has no volume device and is not locked.</summary>
    [AdminTheory]
    [InlineData(0, 30)]
    [InlineData(1, 50)]
    public async Task CrashMidMove_ResumeCompletes_OnVhdx(int moveIndex, int percent)
    {
        string name = $"runner-crash-m{moveIndex}";
        try
        {
            using var vhdx = VhdxFixture.Create(name, Log);
            var c = Prepare(vhdx, name);
            var crashing = new GuardedBackend(new WindowsDiskMoveBackend(), c, Log) { CrashInMove = (moveIndex, percent) };
            var runner = new DiskMoveRunner(crashing, c.Journal, manageWinRe: false);
            await Assert.ThrowsAsync<SimulatedCrashException>(() =>
                runner.RunAsync(c.Disk, c.Target, c.Source, c.Plan, () => [], new LogProgress(Log), () => false, CancellationToken.None));
            Assert.True(crashing.Crashed, "the crash point was not reached");
            Assert.True(DiskMoveRunner.HasUnfinishedJournal(c.Journal));

            var moving = moveIndex == 0 ? vhdx.DLike : vhdx.RLike;
            Log($"after crash: {moving} (volume path {moving.VolumePath ?? "none"})");
            ulong attrs = new WindowsDiskMoveBackend().GetPartitionAttributes(vhdx.DiskNumber, moving.Guid);
            Assert.True((attrs & DiskMoveRunner.GptHidden) != 0, $"the half-moved partition must stay hidden (0x{attrs:X16})");
            if (moveIndex == 0) Assert.False(Directory.Exists($@"{c.Letter}:\data"), "the half-moved source must not be mounted");

            var resumed = new GuardedBackend(new WindowsDiskMoveBackend(), c, Log);
            var sw = Stopwatch.StartNew();
            var result = await new DiskMoveRunner(resumed, c.Journal, manageWinRe: false).ResumeAsync(new LogProgress(Log), () => false);
            Log($"ResumeAsync took {sw.Elapsed.TotalSeconds:F1} s: {result.Outcome}, letter {result.FinalSourceLetter}, warnings [{string.Join(" | ", result.Warnings)}]");
            Assert.Equal(DiskMoveOutcome.Completed, result.Outcome);
            Assert.Equal(c.Letter, result.FinalSourceLetter);
            Assert.Empty(result.Warnings);
            Assert.DoesNotContain(resumed.Calls, x => x.StartsWith($"LockVolume({(moveIndex == 0 ? c.Source : c.Recovery).VolumePath}"));
            AssertMoved(c);
        }
        finally
        {
            SaveLog(name);
        }
    }

    /// <summary>
    /// Regression of the hash-mismatch investigation for the product runner: the run crashes at 50 % of the source
    /// copy; while the resume runs (no lock of ours: the source is hidden) a SECOND VHDX's layout changes 3 times.
    /// DiskMove.dll must lock the old-offset volume device of the half-moved source for the copy, and its old boot
    /// sectors are already zero, so nothing can be written into the source: every hash must match.
    /// </summary>
    [AdminTheory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task CrashMidMove_LayoutChangeOnOtherDisk_ResumeCompletes_OnVhdx(int run)
    {
        string name = $"bootfix-runner-{run}";
        VhdxFixture? other = null;
        Thread? activity = null;
        try
        {
            other = VhdxFixture.Create(name + "-other", Log);
            using var vhdx = VhdxFixture.Create(name, Log);
            var c = Prepare(vhdx, name);
            var crashing = new GuardedBackend(new WindowsDiskMoveBackend(), c, Log) { CrashInMove = (0, 50) };
            await Assert.ThrowsAsync<SimulatedCrashException>(() =>
                new DiskMoveRunner(crashing, c.Journal, manageWinRe: false)
                    .RunAsync(c.Disk, c.Target, c.Source, c.Plan, () => [], new LogProgress(Log), () => false, CancellationToken.None));
            Assert.True(crashing.Crashed, "the crash point was not reached");
            long shrunk = c.Source.Size - Amount;
            Log("old-offset volume device after the crash: " + (vhdx.FindVolumeDevice(c.Source.Offset, shrunk) ?? "none"));

            activity = other.StartHideUnhide(Log);
            var resumed = new GuardedBackend(new WindowsDiskMoveBackend(), c, Log);
            var sw = Stopwatch.StartNew();
            var result = await new DiskMoveRunner(resumed, c.Journal, manageWinRe: false).ResumeAsync(new LogProgress(Log), () => false);
            Log($"ResumeAsync took {sw.Elapsed.TotalSeconds:F1} s (activity still running: {activity.IsAlive}): {result.Outcome}, " +
                $"letter {result.FinalSourceLetter}, warnings [{string.Join(" | ", result.Warnings)}]");
            activity.Join();
            Assert.Equal(DiskMoveOutcome.Completed, result.Outcome);
            Assert.Equal(c.Letter, result.FinalSourceLetter);
            Assert.Empty(result.Warnings);
            Assert.DoesNotContain(resumed.Calls, x => x.StartsWith($"LockVolume({c.Source.VolumePath}"));
            AssertMoved(c);
        }
        finally
        {
            activity?.Join();
            other?.Dispose();
            SaveLog(name);
        }
    }

    // ------------------------------------------------------------------ helpers

    Case Prepare(VhdxFixture vhdx, string name)
    {
        var d = vhdx.DLike;
        char letter = d.Letter, cLetter = vhdx.CLike.Letter;
        var hashes = vhdx.PopulateFiles(letter, FileCount, FileSeed, 600 * VhdxFixture.MiB);
        uint serial = VhdxFixture.VolumeSerial(letter);

        string dir = Path.Combine(VhdxFixture.ScratchDir, name);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        string journal = Path.Combine(dir, "journal.bin");

        var disk = new DiskLayoutService().GetDisks().Single(x => x.Guid == vhdx.DiskGuid);
        Assert.Equal(vhdx.DiskNumber, disk.Number);   // DiskNumber re-checks that it is the VHDX
        PartitionSnapshot P(Guid g, string? volumeFallback)
        {
            var s = disk.Partitions.Single(p => p.Guid == g);
            return s.VolumePath is null ? s with { VolumePath = volumeFallback } : s;
        }
        var target = P(vhdx.CLikeGuid, vhdx.CLike.VolumePath) with { IsWindowsPartition = true };   // the C-like target
        var source = P(vhdx.DLikeGuid, vhdx.DLike.VolumePath);
        var recovery = P(vhdx.RLikeGuid, vhdx.RLike.VolumePath);
        foreach (var p in new[] { target, source, recovery }) Log($"snapshot {p}");
        var native = new WindowsDiskMoveBackend();
        ulong srcAttrs = native.GetPartitionAttributes(vhdx.DiskNumber, source.Guid);
        ulong recAttrs = native.GetPartitionAttributes(vhdx.DiskNumber, recovery.Guid);
        Log($"exact attributes: source 0x{srcAttrs:X16}, recovery 0x{recAttrs:X16}");
        Assert.Equal(0x8000000000000001UL, recAttrs);

        var plan = new MovePlan(Amount, [source, recovery], true);
        return new Case(vhdx, name, journal, disk, target, source, recovery, plan, letter, serial, cLetter, srcAttrs, recAttrs, hashes);
    }

    void AssertMoved(Case c)
    {
        var vhdx = c.Vhdx;
        VhdxPartition? d = null;
        for (int i = 0; i < 40; i++)   // the letter comes back asynchronously
        {
            d = vhdx.DLike;
            if (d.Letter == c.Letter && Directory.Exists($@"{c.Letter}:\data")) break;
            Thread.Sleep(500);
        }
        var cl = vhdx.CLike;
        var rl = vhdx.RLike;
        Log($"after: CLIKE {cl}");
        Log($"after: RLIKE {rl}");
        Log($"after: DLIKE {d}");
        Assert.NotNull(d);

        // C-like grew by exactly X; the Recovery-like partition sits right after it, moved by X, same GUID/size.
        Assert.Equal(c.Target.Offset, cl.Offset);
        Assert.Equal(c.Target.Size + Amount, cl.Size);
        Assert.Equal(c.Recovery.Offset + Amount, rl.Offset);
        Assert.Equal(cl.Offset + cl.Size, rl.Offset);
        Assert.Equal(c.Recovery.Size, rl.Size);
        Assert.Equal(VhdxFixture.RecoveryType, rl.GptType);

        // D-like: moved by X, X smaller, same GUID, letter, volume GUID path, serial, exact attributes.
        Assert.Equal(c.Source.Offset + Amount, d.Offset);
        Assert.Equal(c.Source.Size - Amount, d.Size);
        Assert.Equal(c.Letter, d.Letter);
        Assert.Contains(d.AccessPaths, a => string.Equals(a, c.Source.VolumePath, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(c.Serial, VhdxFixture.VolumeSerial(c.Letter));
        var native = new WindowsDiskMoveBackend();
        Assert.Equal(c.SourceAttrs, native.GetPartitionAttributes(vhdx.DiskNumber, d.Guid));
        Assert.Equal(c.RecoveryAttrs, native.GetPartitionAttributes(vhdx.DiskNumber, rl.Guid));

        vhdx.AssertHashes(c.Letter, c.Hashes);
        Assert.Equal(0, vhdx.Chkdsk(c.Letter));
        Assert.Equal(0, vhdx.Chkdsk(c.CLetter));

        // Journal gone; the layout backups of step 0 are kept.
        Assert.False(DiskMoveRunner.HasUnfinishedJournal(c.Journal));
        string dir = Path.GetDirectoryName(c.Journal)!;
        Assert.False(DiskMoveRunner.HasUnfinishedMove(dir), "journal or pending marker left");
        Assert.Single(Directory.GetFiles(dir, "layout-*.json"));
        var gpt = File.ReadAllBytes(Assert.Single(Directory.GetFiles(dir, "gpt-*.bin")));
        int sector = (int)vhdx.LogicalSectorSize;
        Assert.Equal((34 + 33) * sector, gpt.Length);
        Assert.Equal("EFI PART", Encoding.ASCII.GetString(gpt, sector, 8));                  // primary header (LBA 1)
        Assert.Equal("EFI PART", Encoding.ASCII.GetString(gpt, gpt.Length - sector, 8));     // backup header (last LBA)
        Log("RESULT moved: sizes, positions, identity, attributes, hashes and chkdsk all as planned");
    }

    /// <summary>
    /// Delegates to the real backend but only for the fixture's VHDX: every disk number is checked against
    /// <see cref="VhdxFixture.DiskNumber"/> (which re-checks MSFT_Disk: not boot/system, virtual, Location = VHDX),
    /// volumes must be DLIKE/RLIKE, the journal must be in the scratch folder, and WinRE is never touched.
    /// Optionally throws from inside the progress callback of one move to simulate a crash.
    /// </summary>
    sealed class GuardedBackend(IDiskMoveBackend inner, Case c, Action<string> log) : IDiskMoveBackend
    {
        public (int Move, int Percent)? CrashInMove { get; init; }
        public bool Crashed { get; private set; }
        public List<string> Calls { get; } = [];

        readonly HashSet<string> _volumes = new(StringComparer.OrdinalIgnoreCase)
        {
            c.Source.VolumePath ?? "-", c.Recovery.VolumePath ?? "-",
        };

        int Disk(int disk, string call)
        {
            Calls.Add(call);
            int vhdx = c.Vhdx.DiskNumber;   // AssertSafe
            if (disk != vhdx) throw new InvalidOperationException($"SAFETY: {call} on disk {disk}, the VHDX is disk {vhdx}");
            return disk;
        }

        string Volume(string v, string call)
        {
            Calls.Add($"{call}({v})");
            c.Vhdx.AssertSafe();
            if (!_volumes.Contains(v)) throw new InvalidOperationException($"SAFETY: {call} on volume {v}");
            return v;
        }

        string Journal(string path)
        {
            if (!Path.GetFullPath(path).StartsWith(VhdxFixture.ScratchDir, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"SAFETY: journal {path} outside the scratch folder");
            return path;
        }

        static T Never<T>(string what) => throw new InvalidOperationException($"SAFETY: {what} must not be called (manageWinRe: false)");

        public int FindDiskNumber(Guid diskGuid)
        {
            int n = inner.FindDiskNumber(diskGuid);
            log($"FindDiskNumber({diskGuid}) = {n}");
            return Disk(n, "FindDiskNumber");
        }

        public long PartitionStart(int disk, Guid part) => inner.PartitionStart(Disk(disk, "PartitionStart"), part);
        public long PartitionSize(int disk, Guid part) => inner.PartitionSize(Disk(disk, "PartitionSize"), part);

        public void ResizePartition(int disk, Guid part, long newSize)
        {
            log($"ResizePartition({part}, {newSize})");
            inner.ResizePartition(Disk(disk, "ResizePartition"), part, newSize);
        }

        public ulong GetPartitionAttributes(int disk, Guid part) => inner.GetPartitionAttributes(Disk(disk, "GetAttributes"), part);

        public void SetPartitionAttributes(int disk, Guid part, ulong attributes)
        {
            log($"SetPartitionAttributes({part}, 0x{attributes:X16})");
            inner.SetPartitionAttributes(Disk(disk, "SetAttributes"), part, attributes);
        }

        public void RemoveLetter(string volumePath, char letter) { log($"RemoveLetter({volumePath}, {letter})"); inner.RemoveLetter(Volume(volumePath, "RemoveLetter"), letter); }
        public char AssignLetter(string volumePath, char letter)
        {
            char got = inner.AssignLetter(Volume(volumePath, "AssignLetter"), letter);
            log($"AssignLetter({volumePath}, {letter}) = {got}");
            return got;
        }

        public char? GetLetter(string volumePath) => inner.GetLetter(Volume(volumePath, "GetLetter"));

        public IDisposable LockVolume(string volumePath)
        {
            var sw = Stopwatch.StartNew();
            var v = inner.LockVolume(Volume(volumePath, "LockVolume"));
            log($"LockVolume({volumePath}) in {sw.Elapsed.TotalSeconds:F1} s");
            return v;
        }

        public bool VerifyVolume(IDisposable volume, int disk, long start, long length)
        {
            bool ok = inner.VerifyVolume(volume, Disk(disk, "VerifyVolume"), start, length);
            log($"VerifyVolume({start}, {length}) = {ok}");
            return ok;
        }

        public void Dismount(IDisposable volume) { log("Dismount"); Calls.Add("Dismount"); inner.Dismount(volume); }
        public bool? WinReEnabled() => Never<bool?>("reagentc /info");
        public void SetWinRe(bool enabled) => Never<bool>("reagentc");
        public byte[] ReadRawGpt(int disk, int sectorSize) => inner.ReadRawGpt(Disk(disk, "ReadRawGpt"), sectorSize);

        public void CreateJournal(string path, IReadOnlyList<NativeMovePlan> moves, RestoreState state)
        {
            log($"CreateJournal: {string.Join("; ", moves)}; state {state}");
            Calls.Add("CreateJournal");
            inner.CreateJournal(Journal(path), moves, state);
        }

        public JournalInfo? ReadJournal(string path, out RestoreState? state)
        {
            var info = inner.ReadJournal(Journal(path), out state);
            log($"ReadJournal: {info} chunks [{string.Join(", ", info?.ChunkCount ?? [])}]; state {state}");
            return info;
        }

        public void SetJournalStep(string path, MoveStep step) { log($"SetJournalStep({step})"); Calls.Add($"Step({step})"); inner.SetJournalStep(Journal(path), step); }
        public void DeleteJournal(string path) { log("DeleteJournal"); Calls.Add("DeleteJournal"); inner.DeleteJournal(Journal(path)); }

        public (byte[] First, byte[] Backup) ReadBootSectors(int disk, long start, long length, int sectorSize)
        {
            log($"ReadBootSectors({start}, {length}, {sectorSize})");
            return inner.ReadBootSectors(Disk(disk, "ReadBootSectors"), start, length, sectorSize);
        }

        public bool RunMove(int disk, string journal, int moveIndex, Func<long, long, bool> progress, IDisposable? heldLock)
        {
            var sw = Stopwatch.StartNew();
            log($"RunMove({moveIndex}) with {(heldLock is null ? "no lock of ours (the DLL locks the old-offset volume)" : "our lock")}");
            bool done = inner.RunMove(Disk(disk, $"RunMove({moveIndex})"), Journal(journal), moveIndex, (d, t) =>
            {
                if (CrashInMove is { } cm && cm.Move == moveIndex && !Crashed && d > 0 && d * 100 >= t * cm.Percent)
                {
                    Crashed = true;
                    log($"CRASH in move {moveIndex} at {d}/{t}");
                    throw new SimulatedCrashException();
                }
                return progress(d, t);
            }, heldLock);
            log($"RunMove({moveIndex}) = {done} in {sw.Elapsed.TotalSeconds:F1} s");
            return done;
        }

        public void SetPartitionStart(int disk, Guid part, long newStart)
        {
            log($"SetPartitionStart({part}, {newStart})");
            inner.SetPartitionStart(Disk(disk, "SetPartitionStart"), part, newStart);
        }

        public void PatchNtfsHiddenSectors(int disk, long start, long length, int sectorSize)
        {
            log($"PatchNtfsHiddenSectors({start}, {length}, {sectorSize})");
            inner.PatchNtfsHiddenSectors(Disk(disk, "Patch"), start, length, sectorSize);
        }

        public int Chkdsk(char letter)
        {
            if (char.ToUpperInvariant(letter) != c.Letter) throw new InvalidOperationException($"SAFETY: chkdsk {letter}:");
            c.Vhdx.AssertSafe();
            int rc = inner.Chkdsk(letter);
            log($"Chkdsk({letter}) = {rc}");
            return rc;
        }
    }
}
