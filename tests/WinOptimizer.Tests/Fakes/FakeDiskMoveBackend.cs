using System.Text.Json;
using WinOptimizer.Services.DiskSpace;

namespace WinOptimizer.Tests.Fakes;

/// <summary>Thrown by the fake to simulate a process crash; afterwards every call fails until <see cref="FakeDiskMoveBackend.Revive"/>.</summary>
public sealed class SimulatedCrashException() : Exception("simulated crash");

/// <summary>
/// Models one GPT disk (EFI, MSR, C:, Recovery, D:) plus the Windows behaviour the runner depends on, records every
/// state-changing call, and fails loudly on anything that would corrupt a real disk:
/// writing (copy / NTFS patch) under a mounted volume, moving a GPT entry to where its data is not, overlapping
/// partitions, moving the Recovery before the source was committed, locking a hidden volume, resizing a hidden one,
/// reading the journal's boot sectors from a mounted volume or after data moved, a journal whose boot sectors were not
/// read from that partition, and an NTFS patch before the move restored the saved boot sectors (journal v2).
/// The journal is a real JSON file so <see cref="DiskMoveRunner.HasUnfinishedJournal"/> sees it.
/// </summary>
public sealed class FakeDiskMoveBackend : IDiskMoveBackend
{
    public const long MiB = 1L << 20, GiB = 1L << 30;
    public const ulong Hidden = 0x4000000000000000UL, NoLetter = 0x8000000000000000UL;
    public const int Sector = 512;

    public sealed class Part
    {
        public required string Name;
        public Guid Guid = Guid.NewGuid();
        public Guid Type;
        public long Start, Size;
        public ulong Attrs;
        public char Letter;
        public char RememberedLetter;   // what Windows gives back by itself after an unhide
        public string? VolumePath;
        public bool IsNtfs;
        // Physical state of the file system bytes.
        public long DataStart;           // where the file system currently is on the disk
        public long HiddenSectors;       // NTFS boot sector field
        public bool Locked, Dismounted;
        public bool InUse;
        /// <summary>The bytes ReadBootSectors returned for it (journal v2), and whether a finished move wrote them
        /// to the destination.</summary>
        public byte[]? BootRead;
        public bool BootRestored;
        /// <summary>Review I1: the volume exists but the mount manager never registered it (no letter can be assigned
        /// until an arrival nudge or a hide/unhide re-triggers it).</summary>
        public bool Unregistered;
        /// <summary>Registered, but no letter can be assigned (e.g. none free).</summary>
        public bool NoLetterPossible;
        public bool IsHidden => (Attrs & Hidden) != 0;
        /// <summary>Windows can write to it: it has a volume (not hidden) and we do not hold it locked + dismounted.</summary>
        public bool Mounted => !IsHidden && !(Locked && Dismounted);
    }

    public Guid DiskGuid = Guid.NewGuid();
    public int DiskNumber;
    public long DiskSize;
    public readonly List<Part> Parts = [];
    public Part C, R, D;
    public bool? WinRe = true;
    public int EnableFailures;
    /// <summary>reagentc /disable fails this many times (WinRE stays as it is).</summary>
    public int DisableFailures;
    public int ChkdskResult;
    public bool WindowsRestoresLetter = true;
    /// <summary>Whether IOCTL_MOUNTMGR_VOLUME_ARRIVAL_NOTIFICATION / a hide + unhide registers an unregistered volume.</summary>
    public bool NudgeHelps = true, ToggleHelps = true;
    /// <summary>Letters held by volumes outside this disk (e.g. a USB drive plugged in during the move).</summary>
    public readonly HashSet<char> ForeignLetters = [];
    public int ChunksPerMove = 4;
    public readonly Dictionary<int, int> ChunkCopies = new() { [0] = 0, [1] = 0 };
    public readonly List<string> Calls = [];
    public Action<string>? OnCall;
    /// <summary>Crash right after the state-changing call with this 1-based number has been applied.</summary>
    public int? CrashAtCall;
    public bool Dead { get; private set; }
    /// <summary>Crash BEFORE a resize to this size is applied (a crash "during" the resize).</summary>
    public long? CrashBeforeResizeTo;
    public int CallCount { get; private set; }

    public FakeDiskMoveBackend(bool withRecovery = true)
    {
        long off = MiB;
        Part Add(string name, Guid type, long size, ulong attrs = 0, char letter = '\0', bool ntfs = false)
        {
            var p = new Part
            {
                Name = name, Type = type, Start = off, Size = size, Attrs = attrs, Letter = letter, RememberedLetter = letter,
                IsNtfs = ntfs, DataStart = off, HiddenSectors = off / Sector,
            };
            if (ntfs) p.VolumePath = $@"\\?\Volume{{{p.Guid}}}\";
            Parts.Add(p);
            off += size;
            return p;
        }
        Add("EFI", LayoutBuilder.EfiType, 100 * MiB, 0x8000000000000001UL);
        Add("MSR", LayoutBuilder.MsrType, 16 * MiB);
        C = Add("C", PartitionTypes.BasicData, 40 * GiB, 0, 'C', true);
        R = withRecovery ? Add("R", PartitionTypes.Recovery, 900 * MiB, 0x8000000000000001UL, '\0', true) : null!;
        D = Add("D", PartitionTypes.BasicData, 20 * GiB, 0, 'D', true);
        DiskSize = off + MiB;
    }

    /// <summary>The layout as DiskLayoutService would report it.</summary>
    public DiskSnapshot Snapshot() => new(DiskNumber, DiskGuid, "Fake Disk", "GPT", false, true, true, "Healthy", Sector,
        DiskSize, Parts.Select((p, i) => new PartitionSnapshot(i + 1, p.Guid, p.Type, p.Start, p.Size,
            p.Letter == '\0' ? null : p.Letter, p.VolumePath, p.IsNtfs ? "NTFS" : null, p.Size / 2, 0,
            p == C, p.Type == PartitionTypes.Recovery)).ToList());

    public PartitionSnapshot Snap(Part p) => Snapshot().Partitions.Single(s => s.Guid == p.Guid);

    public void Revive()
    {
        Dead = false;
        CrashAtCall = null;
        CrashBeforeResizeTo = null;
        OnCall = null;
        foreach (var p in Parts) p.Locked = p.Dismounted = false;   // the process died: its handles are gone
    }

    // ------------------------------------------------------------------ helpers

    void Alive()
    {
        if (Dead) throw new SimulatedCrashException();
    }

    /// <summary>Records a state-changing call that has just been applied, then crashes if asked to.</summary>
    void Did(string call)
    {
        CallCount++;
        Calls.Add(call);
        OnCall?.Invoke(call);
        if (CrashAtCall == CallCount)
        {
            Dead = true;
            foreach (var p in Parts) p.Locked = p.Dismounted = false;
            throw new SimulatedCrashException();
        }
    }

    void Disk(int disk)
    {
        Alive();
        if (disk != DiskNumber) throw new DiskMoveException($"fake: disk {disk} does not exist (it is {DiskNumber})");
    }

    Part P(Guid g) => Parts.SingleOrDefault(p => p.Guid == g) ?? throw new DiskMoveException($"fake: no partition {g}");

    Part ByVolume(string volumePath) =>
        Parts.SingleOrDefault(p => string.Equals(p.VolumePath, volumePath, StringComparison.OrdinalIgnoreCase))
        ?? throw new DiskMoveException($"fake: no volume {volumePath}");

    void NoOverlap(Part self, long start, long size)
    {
        foreach (var o in Parts.Where(o => o != self))
            if (start < o.Start + o.Size && o.Start < start + size)
                throw new InvalidOperationException($"fake: {self.Name} [{start}, {start + size}) would overlap {o.Name}");
        if (start + size > DiskSize - MiB) throw new InvalidOperationException($"fake: {self.Name} would leave the disk");
    }

    public string Name(Guid g) => P(g).Name;

    // ------------------------------------------------------------------ IDiskMoveBackend

    public int FindDiskNumber(Guid diskGuid)
    {
        Alive();
        return diskGuid == DiskGuid ? DiskNumber : throw new DiskMoveException("fake: no disk with that GUID");
    }

    public long PartitionStart(int disk, Guid part) { Disk(disk); return P(part).Start; }
    public long PartitionSize(int disk, Guid part) { Disk(disk); return P(part).Size; }

    public void ResizePartition(int disk, Guid part, long newSize)
    {
        Disk(disk);
        var p = P(part);
        if (p.Size == newSize) return;
        if (CrashBeforeResizeTo == newSize)
        {
            Dead = true;
            foreach (var x in Parts) x.Locked = x.Dismounted = false;
            throw new SimulatedCrashException();
        }
        if (p.IsHidden || p.Locked) throw new InvalidOperationException($"fake: cannot resize hidden/locked {p.Name}");
        if (p.DataStart != p.Start) throw new InvalidOperationException($"fake: resize of {p.Name} whose data is elsewhere");
        NoOverlap(p, p.Start, newSize);
        p.Size = newSize;
        Did($"Resize({p.Name},{newSize})");
    }

    public ulong GetPartitionAttributes(int disk, Guid part) { Disk(disk); return P(part).Attrs; }

    public void SetPartitionAttributes(int disk, Guid part, ulong attributes)
    {
        Disk(disk);
        var p = P(part);
        if (p.Attrs == attributes) return;
        bool wasHidden = p.IsHidden;
        p.Attrs = attributes;
        if (!wasHidden && p.IsHidden) p.Letter = '\0';                         // Windows drops the letter itself
        if (wasHidden && !p.IsHidden && p.Unregistered && ToggleHelps) p.Unregistered = false;   // a new arrival
        if (wasHidden && !p.IsHidden && WindowsRestoresLetter && p.RememberedLetter != '\0')
            p.Letter = Taken(p.RememberedLetter, p) ? FirstFree() : p.RememberedLetter;   // ...and often gives one back
        Did($"SetAttributes({p.Name},{(p.IsHidden ? "hidden" : "visible")},0x{attributes:X16})");
    }

    public void RemoveLetter(string volumePath, char letter)
    {
        Alive();
        var p = ByVolume(volumePath);
        if (p.Letter == letter) p.Letter = '\0';
        p.RememberedLetter = '\0';
        Did($"RemoveLetter({p.Name},{letter})");
    }

    bool Taken(char letter, Part self) => ForeignLetters.Contains(letter) || Parts.Any(o => o != self && o.Letter == letter);

    char FirstFree()
    {
        for (char c = 'D'; c <= 'Z'; c++)
            if (!ForeignLetters.Contains(c) && Parts.All(o => o.Letter != c)) return c;
        throw new DiskMoveException("fake: no free letter");
    }

    /// <summary>Like the real backend: the wanted letter if free; if taken by another volume, the letter the volume
    /// already has (auto-assigned), else the first free one from D.</summary>
    public char AssignLetter(string volumePath, char letter)
    {
        Alive();
        var p = ByVolume(volumePath);
        if (p.IsHidden) throw new DiskMoveException($"fake: {p.Name} has no volume while hidden");
        if (p.Unregistered || p.NoLetterPossible)
            throw new DiskMoveException($"fake: cannot give {p.Name} a drive letter: The parameter is incorrect (87)");
        if (!Taken(letter, p)) p.Letter = letter;
        else if (p.Letter == '\0') p.Letter = FirstFree();
        p.RememberedLetter = p.Letter;
        Did($"AssignLetter({p.Name},{p.Letter})");
        return p.Letter;
    }

    public char? GetLetter(string volumePath)
    {
        Alive();
        var p = ByVolume(volumePath);
        return p.Letter == '\0' || p.IsHidden || p.Unregistered || p.NoLetterPossible ? null : p.Letter;
    }

    public IDisposable? LockOldOffset(int disk, long start, long length)
    {
        Disk(disk);
        var p = Parts.SingleOrDefault(x => x.Start == start && x.Size == length && x.IsHidden);
        if (p is null) return null;
        if (p.InUse) throw new DiskMoveNativeException(11, "fake: old-offset volume lock (DM_E_LOCKED)");
        if (p.Locked) throw new InvalidOperationException($"fake: {p.Name} is already locked");
        p.Locked = p.Dismounted = true;
        Did($"LockOldOffset({p.Name})");
        return new Vol(this, p);
    }

    public bool NudgeVolumeArrival(int disk, long start, long length)
    {
        Disk(disk);
        var p = Parts.SingleOrDefault(x => x.Start == start && x.Size == length && !x.IsHidden);
        Calls.Add($"Nudge({p?.Name ?? "none"})");
        if (p is null) return false;
        if (NudgeHelps) p.Unregistered = false;
        return true;
    }

    public bool VolumeRegistered(string volumePath)
    {
        Alive();
        var p = ByVolume(volumePath);
        return !p.Unregistered && !p.IsHidden;
    }

    sealed class Vol(FakeDiskMoveBackend owner, Part p) : IDisposable
    {
        public readonly Part Part = p;
        bool _closed;

        public void Dispose()
        {
            if (_closed) return;
            _closed = true;
            if (owner.Dead) return;   // released by the crash already
            Part.Locked = Part.Dismounted = false;
            owner.Calls.Add($"Unlock({Part.Name})");
        }
    }

    public IDisposable LockVolume(string volumePath)
    {
        Alive();
        var p = ByVolume(volumePath);
        if (p.IsHidden) throw new DiskMoveNativeException(9, "fake: volume lock (hidden volume has no device)");
        if (p.InUse || p.Locked) throw new DiskMoveNativeException(11, "fake: volume lock");
        p.Locked = true;
        Did($"Lock({p.Name})");
        return new Vol(this, p);
    }

    public bool VerifyVolume(IDisposable volume, int disk, long start, long length)
    {
        Alive();
        var p = ((Vol)volume).Part;
        Calls.Add($"Verify({p.Name})");
        return disk == DiskNumber && p.Start == start && p.Size == length;
    }

    public void Dismount(IDisposable volume)
    {
        Alive();
        var p = ((Vol)volume).Part;
        if (p.Dismounted) throw new DiskMoveNativeException(3, "fake: dismount of an already dismounted volume handle (Windows: DM_E_IO)");
        p.Dismounted = true;
        Did($"Dismount({p.Name})");
    }

    public bool? WinReEnabled() { Alive(); return WinRe; }

    public void SetWinRe(bool enabled)
    {
        Alive();
        if (enabled && R is not null && Parts.Contains(R) && R.IsHidden)
            throw new InvalidOperationException("fake: reagentc /enable with a hidden Recovery partition");
        if (!enabled && DisableFailures > 0)
        {
            DisableFailures--;
            Calls.Add("SetWinRe(False) failed");
            throw new DiskMoveException("fake: reagentc /disable failed");
        }
        if (enabled && EnableFailures > 0)
        {
            EnableFailures--;
            Calls.Add("SetWinRe(True) failed");
            throw new DiskMoveException("fake: reagentc /enable failed");
        }
        WinRe = enabled;
        Did($"SetWinRe({enabled})");
    }

    public byte[] ReadRawGpt(int disk, int sectorSize)
    {
        Disk(disk);
        Calls.Add("ReadRawGpt");
        return new byte[(34 + 33) * sectorSize];
    }

    // ------------------------------------------------------------------ journal (JSON file)

    sealed class JFile
    {
        public List<NativeMovePlanDto> Moves { get; set; } = [];
        public List<byte[]> Boot { get; set; } = [];
        public int Step { get; set; }
        public int MoveIndex { get; set; }
        public long ChunksDone { get; set; }
        public long[] ChunkCount { get; set; } = [0, 0];
        public byte[] App { get; set; } = [];
    }

    public sealed record NativeMovePlanDto(Guid DiskGuid, Guid PartitionGuid, int SectorSize, long OldStart, long Length, long Shift);

    static JFile Load(string path) => JsonSerializer.Deserialize<JFile>(File.ReadAllBytes(path))!;
    static void Save(string path, JFile j) => File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(j));

    public void CreateJournal(string path, IReadOnlyList<NativeMovePlan> moves, RestoreState state)
    {
        Alive();
        foreach (var m in moves)
        {
            if (m.Extents.Count != 1 || m.Extents[0] != (0L, m.Length))
                throw new InvalidOperationException("fake: every move must copy the whole partition (one extent)");
            var part = P(m.PartitionGuid);
            if (part.BootRead is null || m.BootFirst is null || m.BootBackup is null ||
                !m.BootFirst.Concat(m.BootBackup).SequenceEqual(part.BootRead))
                throw new InvalidOperationException($"fake: the journal's boot sectors of {part.Name} were not read from it");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var j = new JFile
        {
            Moves = moves.Select(m => new NativeMovePlanDto(m.DiskGuid, m.PartitionGuid, m.SectorSize, m.OldStart, m.Length, m.Shift)).ToList(),
            ChunkCount = [ChunksPerMove, moves.Count > 1 ? ChunksPerMove : 0],
            Boot = moves.Select(m => m.BootFirst.Concat(m.BootBackup).ToArray()).ToList(),
            App = state.ToJsonBytes(),
        };
        Save(path, j);
        Did("CreateJournal");
    }

    public JournalInfo? ReadJournal(string path, out RestoreState? state)
    {
        Alive();
        state = null;
        if (!File.Exists(path)) return null;
        JFile j;
        try
        {
            j = Load(path);
        }
        catch (JsonException ex)
        {
            throw new DiskMoveException("fake: DM_E_JOURNAL_CORRUPT", ex);
        }
        state = RestoreState.FromJsonBytes(j.App);
        return new JournalInfo(j.Moves.Count, (MoveStep)j.Step, j.MoveIndex, j.ChunksDone, j.ChunkCount);
    }

    public NativeMovePlanDto JournalMove(string path, int i) => Load(path).Moves[i];

    /// <summary>Read-only. Models dmw_read_boot_sectors: bytes unique to the partition and its current data position.</summary>
    public (byte[] First, byte[] Backup) ReadBootSectors(int disk, long start, long length, int sectorSize)
    {
        Disk(disk);
        var p = Parts.SingleOrDefault(x => x.Start == start && x.Size == length)
                ?? throw new InvalidOperationException($"fake: no partition at {start} (+{length}) to read boot sectors from");
        if (p.Mounted) throw new InvalidOperationException($"fake: boot sectors of the mounted volume {p.Name} read (lock + dismount first)");
        if (p.DataStart != start) throw new InvalidOperationException($"fake: boot sectors of {p.Name} read after its data moved");
        if (sectorSize != Sector) throw new InvalidOperationException($"fake: sector size {sectorSize}");
        var bytes = new byte[2 * sectorSize];
        new Random(p.Name.GetHashCode() ^ (int)(start / MiB)).NextBytes(bytes);
        p.BootRead = bytes;
        Calls.Add($"ReadBoot({p.Name})");
        return (bytes[..sectorSize], bytes[sectorSize..]);
    }

    /// <summary>The volumes DiskMove.dll locked by itself at the old offset (RunMove without a held lock).</summary>
    public readonly List<string> OldOffsetLocks = [];

    public void SetJournalStep(string path, MoveStep step)
    {
        Alive();
        var j = Load(path);
        j.Step = (int)step;
        Save(path, j);
        Did($"Step({step})");
    }

    public void DeleteJournal(string path)
    {
        Alive();
        File.Delete(path);
        Did("DeleteJournal");
    }

    public bool RunMove(int disk, string journal, int moveIndex, Func<long, long, bool> progress, IDisposable? heldLock)
    {
        Disk(disk);
        var j = Load(journal);
        var m = j.Moves[moveIndex];
        // Identity check of dmw_run_move: disk GUID, partition, length, start = old or old + shift.
        var p = Parts.SingleOrDefault(x => x.Guid == m.PartitionGuid);
        if (m.DiskGuid != DiskGuid || p is null || p.Size != m.Length || (p.Start != m.OldStart && p.Start != m.OldStart + m.Shift))
            throw new DiskMoveStateMismatchException("fake: DM_E_JOURNAL_MISMATCH");
        Calls.Add($"RunMove({moveIndex})");
        if (heldLock is not null && (heldLock is not Vol { Part: var hp } || hp != p || !p.Locked))
            throw new InvalidOperationException($"fake: RunMove({moveIndex}) got a lock that is not {p.Name}'s");
        bool oldOffsetLock = false;
        if (heldLock is null && p.Start == m.OldStart && p.IsHidden && !p.Locked)
        {
            // dmw_run_move finds the volume device still at the old offset and locks + dismounts it for the run.
            if (p.InUse) throw new DiskMoveNativeException(11, "fake: old-offset volume lock (DM_E_LOCKED)");
            p.Locked = p.Dismounted = oldOffsetLock = true;
            OldOffsetLocks.Add(p.Name);
            Calls.Add($"LockOld({p.Name})");
        }
        try
        {
            return CopyChunks(j, journal, m, p, moveIndex, progress);
        }
        finally
        {
            if (oldOffsetLock && !Dead) p.Locked = p.Dismounted = false;
        }
    }

    bool CopyChunks(JFile j, string journal, NativeMovePlanDto m, Part p, int moveIndex, Func<long, long, bool> progress)
    {
        if (moveIndex < j.MoveIndex) return true;
        if (moveIndex > j.MoveIndex)
        {
            if (moveIndex != j.MoveIndex + 1 || j.ChunksDone != j.ChunkCount[j.MoveIndex])
                throw new DiskMoveStateMismatchException("fake: move started before the previous one finished");
            j.MoveIndex = moveIndex;
            j.ChunksDone = 0;
        }
        long n = j.ChunkCount[moveIndex];
        if (j.ChunksDone == n) return true;
        if (p.Mounted) throw new InvalidOperationException($"fake: raw copy over the mounted volume {p.Name}");
        NoOverlap(p, m.OldStart + m.Shift, m.Length);   // the destination must be free (no other GPT entry there)
        long chunk = m.Length / n, total = m.Length;
        if (progress(j.ChunksDone * chunk, total)) return false;
        for (long i = j.ChunksDone; i < n; i++)
        {
            j.ChunksDone = i + 1;
            if (j.ChunksDone == n)
            {
                p.DataStart = m.OldStart + m.Shift;
                p.BootRestored = true;   // the core writes the saved boot sectors after the last chunk
            }
            Save(journal, j);
            ChunkCopies[moveIndex]++;
            Did($"Chunk({moveIndex},{i})");
            if (progress((i + 1) * chunk, total) && i + 1 < n) return false;
        }
        return true;
    }

    public void SetPartitionStart(int disk, Guid part, long newStart)
    {
        Disk(disk);
        var p = P(part);
        if (p.Start == newStart) return;
        if (p.DataStart != newStart) throw new InvalidOperationException($"fake: GPT start of {p.Name} moved to where its data is not");
        NoOverlap(p, newStart, p.Size);
        p.Start = newStart;
        Did($"SetStart({p.Name},{newStart})");
    }

    public void PatchNtfsHiddenSectors(int disk, long start, long length, int sectorSize)
    {
        Disk(disk);
        var p = Parts.SingleOrDefault(x => x.DataStart == start && x.Size == length)
                ?? throw new InvalidOperationException($"fake: NTFS patch at {start} where no file system is");
        if (p.Mounted) throw new InvalidOperationException($"fake: raw write into the mounted volume {p.Name}");
        if (!p.BootRestored) throw new InvalidOperationException($"fake: NTFS patch of {p.Name} before its boot sectors were restored");
        p.HiddenSectors = start / sectorSize;
        Did($"Patch({p.Name})");
    }

    public int Chkdsk(char letter)
    {
        Alive();
        var p = Parts.SingleOrDefault(x => x.Letter == letter && x.Mounted)
                ?? throw new InvalidOperationException($"fake: chkdsk {letter}: no such mounted volume");
        Calls.Add($"Chkdsk({p.Name})");
        return ChkdskResult;
    }
}
