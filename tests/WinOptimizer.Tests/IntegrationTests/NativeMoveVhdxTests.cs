using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Xunit;
using Xunit.Abstractions;

namespace WinOptimizer.Tests.IntegrationTests;

/// <summary>
/// Layer 3: diskmove-cli (DiskMove.dll) against a real, attached VHDX. DLIKE is shrunk by 512 MiB and then moved
/// right by 512 MiB; afterwards it must have the same letter, partition GUID, volume GUID path and serial, every
/// file hash must match and chkdsk must be clean. Needs admin; never touches a disk other than the VHDX.
/// </summary>
[Trait("Category", "Integration")]
public sealed class NativeMoveVhdxTests(ITestOutputHelper output)
{
    const long Shift = 512 * VhdxFixture.MiB;
    const int FileCount = 2000;
    const int FileSeed = 20261027;
    const int KillSeed = 7_20261029;

    static readonly string CliPath = typeof(NativeMoveVhdxTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "DiskMoveCliPath").Value!;

    readonly StringBuilder _log = new();

    void Log(string line)
    {
        string l = $"{DateTime.Now:HH:mm:ss.fff} {line}";
        lock (_log) _log.AppendLine(l);
        try { output.WriteLine(l); } catch (InvalidOperationException) { /* after the test ended */ }
    }

    void SaveLog(string name)
    {
        lock (_log) File.WriteAllText(Path.Combine(VhdxFixture.ScratchDir, name + ".log"), _log.ToString());
    }

    // ------------------------------------------------------------------ tests

    [AdminTheory]
    [InlineData(false)]   // one "move" command: copy + commit + restore
    [InlineData(true)]    // "move --no-commit", then "commit --journal"
    public void Move_DataPartition_PreservesFilesAndIdentity(bool separateCommit)
    {
        string name = separateCommit ? "move-split" : "move-single";
        try
        {
            using var vhdx = VhdxFixture.Create(name, Log);
            var m = Prepare(vhdx, name);

            var sw = Stopwatch.StartNew();
            var (rc, outp) = Move(m.MoveArgs(separateCommit ? ["--no-commit"] : []));
            Log($"move{(separateCommit ? " --no-commit" : "")} took {sw.Elapsed.TotalSeconds:F1} s");
            Assert.True(rc == 0, $"move failed ({rc}): {outp}");
            Assert.Contains("partition hidden", outp);
            Assert.Contains("volume dismounted", outp);   // the lock handle still works after the attribute change

            if (separateCommit)
            {
                Assert.Contains("volume left hidden", outp);
                Assert.True(File.Exists(m.Sidecar), "the sidecar must stay until commit");
                Log("after move --no-commit: " + VolumeProbe(m.VolumePath));
                Assert.False(Directory.Exists($@"{m.Letter}:\"), $"{m.Letter}: must stay unmounted until commit");
                sw.Restart();
                (rc, outp) = Cli(m.CommitArgs());
                Log($"commit took {sw.Elapsed.TotalSeconds:F1} s");
                Assert.True(rc == 0, $"commit failed ({rc}): {outp}");
            }

            AssertMoved(vhdx, m);
        }
        finally
        {
            SaveLog(name);
        }
    }

    [AdminFact]
    public void Move_KilledRandomly_ResumesCleanly()
    {
        var rng = new Random(KillSeed);
        var delays = Enumerable.Range(0, 5).Select(_ => rng.Next(200, 3001)).ToArray();
        Log("kill delays (ms): " + string.Join(", ", delays));
        try
        {
            for (int i = 0; i < delays.Length; i++)
            {
                string name = $"kill-{i}";
                Log($"===== iteration {i}: kill after {delays[i]} ms");
                using var vhdx = VhdxFixture.Create(name, Log);
                var m = Prepare(vhdx, name);
                KillAndRecover(m, delays[i]);
                AssertMoved(vhdx, m);
            }
        }
        finally
        {
            SaveLog("kill");
        }
    }

    /// <summary>
    /// What the review asked (N1): can a volume that is HIDDEN|NO_DRIVE_LETTER (as "move" leaves it) still be
    /// locked through its "\\?\Volume{...}\" path by "resume --volume"? Uses an intact (not moved) DLIKE, so the
    /// forced NTFS mount that a lock needs cannot damage anything. Hides with the DLL exactly like "move" does
    /// (one attribute write, then the mount points are removed), runs "resume --volume" against a journal that
    /// does not exist (so nothing is copied), then restores and checks the data.
    /// </summary>
    [AdminFact]
    public void HiddenVolume_ResumeVolumeLock_Probe()
    {
        const string name = "hidden-probe";
        try
        {
            using var vhdx = VhdxFixture.Create(name, Log);
            var d = vhdx.DLike;
            char letter = d.Letter;
            string volume = d.VolumePath ?? throw new InvalidOperationException("DLIKE has no volume GUID path");
            var hashes = vhdx.PopulateFiles(letter, 50, FileSeed, 16 * VhdxFixture.MiB);
            string disk = vhdx.DiskPath;
            byte[] guid = d.Guid.ToByteArray();
            Native.Load();

            Assert.Equal(0, Native.dmw_get_partition_attributes(disk, guid, out ulong original));
            Assert.Equal(0, Native.dmw_set_partition_attributes(disk, guid, original | Native.Hidden | Native.NoDriveLetter, out _));
            // Windows already drops the letter of a volume that becomes HIDDEN (seen in run 2), so this may fail.
            bool deleted = Native.DeleteVolumeMountPointW($@"{letter}:\");
            Log($"DeleteVolumeMountPoint({letter}:) after hide: {(deleted ? "ok" : $"error {Marshal.GetLastWin32Error()}")}");
            Thread.Sleep(2000);
            Assert.False(Directory.Exists($@"{letter}:\"), $"{letter}: must be gone after hiding");
            Log("hidden (not locked): " + VolumeProbe(volume));

            string journal = Path.Combine(VhdxFixture.ScratchDir, name + ".missing.journal");
            File.Delete(journal);
            var (rc, outp) = Cli(["resume", "--disk", disk, "--journal", journal, "--move", "0",
                                  "--volume", volume, "--start", d.Offset.ToString(), "--length", d.Size.ToString()]);
            bool locked = outp.Contains("volume locked");
            Log($"RESULT resume --volume on a hidden volume: rc {rc}, lock {(locked ? "SUCCEEDED" : "FAILED")}, " +
                $"verified {outp.Contains("volume verified")}, dismounted {outp.Contains("volume dismounted")}");
            Log("after resume --volume: " + VolumeProbe(volume));
            // Lock fails (no device: 9, files open: 11) or it works and the missing journal stops the copy (9).
            Assert.True(rc is 9 or 11, $"unexpected resume result ({rc}): {outp}");

            Assert.Equal(0, Native.dmw_set_partition_attributes(disk, guid, original, out _));
            Remount(letter, volume);
            vhdx.AssertHashes(letter, hashes);
            Assert.Equal(0, vhdx.Chkdsk(letter));
        }
        finally
        {
            SaveLog(name);
        }
    }

    // ------------------------------------------------------------------ move setup / checks

    sealed record MoveCase(string Disk, Guid DiskGuid, Guid PartGuid, long OldStart, long Length, uint Sector,
                           char Letter, uint Serial, string VolumePath, string Journal,
                           IReadOnlyDictionary<string, string> Hashes)
    {
        public long NewStart => OldStart + Shift;
        public string Sidecar => Journal + ".cli-restore";

        public string[] MoveArgs(string[] extra) =>
        [
            "move", "--disk", Disk, "--volume", VolumePath, "--disk-guid", DiskGuid.ToString(), "--part-guid", PartGuid.ToString(),
            "--start", OldStart.ToString(), "--length", Length.ToString(), "--shift", Shift.ToString(),
            "--sector", Sector.ToString(), "--journal", Journal, .. extra,
        ];

        public string[] ResumeArgs() => ["resume", "--disk", Disk, "--journal", Journal, "--move", "0"];

        public string[] CommitArgs() =>
        [
            "commit", "--disk", Disk, "--part-guid", PartGuid.ToString(), "--start", NewStart.ToString(),
            "--length", Length.ToString(), "--sector", Sector.ToString(), "--journal", Journal,
        ];

        /// <summary>Undo before any copy: GPT start stays OLD, sidecar attributes + mount points restored.</summary>
        public string[] AbortArgs() =>
            ["commit", "--disk", Disk, "--part-guid", PartGuid.ToString(), "--start", OldStart.ToString(), "--journal", Journal];
    }

    /// <summary>Fills DLIKE with 2000 files, shrinks it by 512 MiB and collects what the move needs.</summary>
    MoveCase Prepare(VhdxFixture vhdx, string name)
    {
        var d = vhdx.DLike;
        char letter = d.Letter;
        var hashes = vhdx.PopulateFiles(letter, FileCount, FileSeed);
        var sw = Stopwatch.StartNew();
        d = vhdx.Shrink(d.Guid, Shift);
        Log($"shrink took {sw.Elapsed.TotalSeconds:F1} s; DLIKE {d}");
        string journal = Path.Combine(VhdxFixture.ScratchDir, name + ".journal");
        File.Delete(journal);
        File.Delete(journal + ".cli-restore");
        var m = new MoveCase(vhdx.DiskPath, vhdx.DiskGuid, d.Guid, d.Offset, d.Size, vhdx.LogicalSectorSize, letter,
                             VhdxFixture.VolumeSerial(letter), d.VolumePath ?? throw new InvalidOperationException("no volume path"),
                             journal, hashes);
        Log($"move case: {m with { Hashes = new Dictionary<string, string>() }}");
        return m;
    }

    void AssertMoved(VhdxFixture vhdx, MoveCase m)
    {
        Assert.False(File.Exists(m.Sidecar), "the sidecar must be gone after a committed move");
        VhdxPartition? d = null;
        for (int i = 0; i < 20; i++)   // the letter comes back asynchronously
        {
            d = vhdx.Partition(m.PartGuid);
            if (d.Letter == m.Letter && Directory.Exists($@"{m.Letter}:\data")) break;
            Thread.Sleep(500);
        }
        Log($"after move: DLIKE {d}");
        Assert.NotNull(d);
        Assert.Equal(m.NewStart, d.Offset);
        Assert.Equal(m.Length, d.Size);
        Assert.Equal(m.Letter, d.Letter);
        Assert.False(d.IsHidden, "GPT HIDDEN must be cleared again");
        Assert.False(d.NoDefaultDriveLetter, "GPT NO_DRIVE_LETTER must be cleared again");
        Assert.Contains(d.AccessPaths, a => string.Equals(a, m.VolumePath, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(m.Serial, VhdxFixture.VolumeSerial(m.Letter));
        // The other partitions did not move.
        Assert.Equal(VhdxFixture.RecoveryType, vhdx.RLike.GptType);
        Assert.True(vhdx.RLike.Offset + vhdx.RLike.Size <= m.NewStart);
        vhdx.AssertHashes(m.Letter, m.Hashes);
        Assert.Equal(0, vhdx.Chkdsk(m.Letter));
    }

    // ------------------------------------------------------------------ kill + recover

    void KillAndRecover(MoveCase m, int delayMs, int attempt = 0)
    {
        var outBuf = new StringBuilder();
        var psi = new ProcessStartInfo(CliPath)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in m.MoveArgs([])) psi.ArgumentList.Add(a);
        var sw = Stopwatch.StartNew();
        using var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (outBuf) outBuf.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (outBuf) outBuf.AppendLine("ERR " + e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        bool exited = p.WaitForExit(delayMs);
        if (!exited)
        {
            p.Kill(true);
            p.WaitForExit();
        }
        p.WaitForExit();   // flush the async readers
        string moveOut;
        lock (outBuf) moveOut = outBuf.ToString();
        string last = moveOut.TrimEnd().Split('\n').LastOrDefault()?.Trim() ?? "";
        Log($"move {(exited ? $"EXITED by itself (rc {p.ExitCode})" : "KILLED")} after {sw.ElapsedMilliseconds} ms; " +
            $"progress lines {moveOut.Split('\n').Count(l => l.StartsWith("progress"))}; last line '{last}'\n{Tail(moveOut, 12)}");
        if (exited && IsLockBusy(p.ExitCode, moveOut) && attempt < 5)
        {
            Thread.Sleep(2000);   // nothing was changed: try the same kill again
            KillAndRecover(m, delayMs, attempt + 1);
            return;
        }
        if (exited)
        {
            Assert.True(p.ExitCode == 0, $"move failed by itself ({p.ExitCode}): {moveOut}");
            return;
        }

        // Crash state, read without opening the (possibly hidden, half-moved) volume: journal + sidecar only.
        bool sidecar = File.Exists(m.Sidecar);
        var (statusRc, statusOut) = Cli(["status", "--journal", m.Journal]);
        Log($"after kill: sidecar {sidecar}, journal status rc {statusRc}; " + VolumeProbe(m.VolumePath));

        if (statusRc == 0)
        {
            // The journal exists, so copying may have started: finish it (resume), then commit + restore.
            Log("RECOVERY resume + commit (journal valid)");
            var (rc, outp) = Cli(m.ResumeArgs());
            Assert.True(rc == 0, $"resume failed ({rc}): {outp}");
            (rc, outp) = Cli(m.CommitArgs());
            Assert.True(rc == 0, $"commit failed ({rc}): {outp}");
            return;
        }

        // No valid journal: no byte was copied yet (the copy only starts after the journal is created + flushed).
        if (sidecar)
        {
            Log("RECOVERY abort (sidecar, no journal) + move again");
            var (rc, outp) = Cli(m.AbortArgs());
            Assert.True(rc == 0, $"abort (commit at the old start) failed ({rc}): {outp}");
            if (File.Exists(m.Sidecar))
            {
                // Only possible if the kill hit the sidecar write itself: the attributes were not changed yet.
                Assert.Contains("no restore info", outp);
                File.Delete(m.Sidecar);
            }
        }
        else
        {
            Log("RECOVERY move again (killed before anything was changed)");
        }
        File.Delete(m.Journal);
        WaitForLetter(m.Letter);
        var (rc2, out2) = Move(m.MoveArgs([]));
        Assert.True(rc2 == 0, $"second move failed ({rc2}): {out2}");
    }

    static string Tail(string s, int lines) => string.Join('\n', s.TrimEnd().Split('\n').TakeLast(lines));

    void WaitForLetter(char letter)
    {
        for (int i = 0; i < 40 && !Directory.Exists($@"{letter}:\data"); i++) Thread.Sleep(500);
        Assert.True(Directory.Exists($@"{letter}:\data"), $"{letter}: did not come back");
    }

    void Remount(char letter, string volume)
    {
        bool ok = false;
        var now = new StringBuilder(260);
        for (int i = 0; i < 20 && !ok; i++)
        {
            // Windows may give the letter back by itself when the volume is no longer hidden.
            ok = (Native.GetVolumeNameForVolumeMountPointW($@"{letter}:\", now, now.Capacity) &&
                  string.Equals(now.ToString(), volume, StringComparison.OrdinalIgnoreCase)) ||
                 Native.SetVolumeMountPointW($@"{letter}:\", volume);
            if (!ok) Thread.Sleep(500);
        }
        Assert.True(ok, $"SetVolumeMountPoint({letter}:) failed: {Marshal.GetLastWin32Error()}");
        WaitForLetter(letter);
    }

    // ------------------------------------------------------------------ CLI + probes

    (int rc, string output) Cli(string[] args)
    {
        Assert.True(File.Exists(CliPath), $"{CliPath} not built");
        return VhdxFixture.Run(CliPath, args, Log);
    }

    /// <summary>"move" failed only because another process (indexer, AV) had a file open: nothing was changed.</summary>
    static bool IsLockBusy(int rc, string output) => rc == 11 && output.Contains("during volume lock");

    /// <summary>"move", retried while the volume lock is refused because files are open.</summary>
    (int rc, string output) Move(string[] args)
    {
        for (int attempt = 0; ; attempt++)
        {
            var r = Cli(args);
            if (!IsLockBusy(r.rc, r.output) || attempt >= 5) return r;
            Log("volume busy, retrying the move in 2 s");
            Thread.Sleep(2000);
        }
    }

    /// <summary>Describes how Windows sees a volume WITHOUT mounting it: mount manager list (FindFirstVolume),
    /// its mount points, and a zero-access open of the device (a direct device open never mounts a file system).</summary>
    static string VolumeProbe(string volumePath)
    {
        var sb = new StringBuilder();
        bool listed = false;
        var name = new StringBuilder(260);
        var h = Native.FindFirstVolumeW(name, name.Capacity);
        if (h != IntPtr.Zero && h != new IntPtr(-1))
        {
            do listed |= string.Equals(name.ToString(), volumePath, StringComparison.OrdinalIgnoreCase);
            while (Native.FindNextVolumeW(h, name, name.Capacity));
            Native.FindVolumeClose(h);
        }
        sb.Append($"listed by FindFirstVolume={listed}");
        var paths = new char[4096];
        if (Native.GetVolumePathNamesForVolumeNameW(volumePath, paths, paths.Length, out uint len))
            sb.Append($", mount points=[{string.Join(",", new string(paths, 0, (int)len).Split('\0', StringSplitOptions.RemoveEmptyEntries))}]");
        else
            sb.Append($", GetVolumePathNames error {Marshal.GetLastWin32Error()}");
        using (var dev = Native.CreateFileW(volumePath.TrimEnd('\\'), 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
            sb.Append(dev.IsInvalid ? $", device open error {Marshal.GetLastWin32Error()}" : ", device exists");
        return sb.ToString();
    }

    static class Native
    {
        public const ulong Hidden = 0x4000000000000000;
        public const ulong NoDriveLetter = 0x8000000000000000;
        static readonly string DllDir = Path.GetDirectoryName(CliPath)!;

        /// <summary>Runs the static constructor (installs the DiskMove.dll resolver) before the first call.</summary>
        public static void Load() { }

        static Native()
        {
            NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, (lib, _, _) =>
                lib == "DiskMove.dll" ? NativeLibrary.Load(Path.Combine(DllDir, lib)) : IntPtr.Zero);
        }

        [DllImport("DiskMove.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
        public static extern int dmw_get_partition_attributes(string disk, byte[] guid, out ulong attributes);

        [DllImport("DiskMove.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
        public static extern int dmw_set_partition_attributes(string disk, byte[] guid, ulong attributes, out ulong old);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool DeleteVolumeMountPointW(string mountPoint);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetVolumeMountPointW(string mountPoint, string volumeName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, StringBuilder volumeName, int size);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr FindFirstVolumeW(StringBuilder name, int size);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool FindNextVolumeW(IntPtr h, StringBuilder name, int size);

        [DllImport("kernel32.dll")]
        public static extern bool FindVolumeClose(IntPtr h);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool GetVolumePathNamesForVolumeNameW(string volume, [Out] char[] names, int size, out uint len);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr sa, uint disposition,
                                                        uint flags, IntPtr template);
    }
}
