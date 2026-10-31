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
[Collection(VhdxCollection.Name)]
public sealed class NativeMoveVhdxTests(ITestOutputHelper output)
{
    const long Shift = 512 * VhdxFixture.MiB;
    const int FileCount = 2000;
    const int FileSeed = 20261027;

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

    /// <summary>
    /// Kills "move" (TerminateProcess) at a point taken from its own output: after ~10/50/90 % of the copied
    /// bytes, or right after the "phase hide/patch/commit-gpt/restore" line. Checks the crash state in the
    /// journal, recovers (resume + commit, or undo + move when nothing was copied) and checks the result.
    /// </summary>
    [AdminTheory]
    [InlineData("progress:10")]
    [InlineData("progress:50")]
    [InlineData("progress:90")]
    [InlineData("phase:hide")]
    [InlineData("phase:patch")]
    [InlineData("phase:commit-gpt")]
    [InlineData("phase:restore")]
    [InlineData("phase:unhidden")]
    public void Move_KilledAtPoint_ResumesCleanly(string killAt)
    {
        string name = "kill-" + killAt.Replace(':', '-');
        try
        {
            using var vhdx = VhdxFixture.Create(name, Log);
            var m = Prepare(vhdx, name);
            KillAt(m, killAt);
            Recover(m, killAt);
            AssertMoved(vhdx, m);
        }
        finally
        {
            SaveLog(name);
        }
    }

    /// <summary>
    /// Regression of the hash-mismatch investigation (Ruling 19): "move" is killed at 50 % of the copy; while the
    /// half-moved DLIKE is hidden, a SECOND VHDX's partition layout changes (DLIKE there hidden + unhidden 3 times),
    /// which makes Windows re-enumerate and mount the old-offset volume device of our DLIKE. Before the fix NTFS wrote
    /// into the not-yet-copied source and the resume copied the damage (4 of 4 runs). Now the old view's boot
    /// sectors are zero (RAW) and the resume locks that device, so every hash must match and chkdsk be clean; the old
    /// first sector must still be zero after the commit, and the new one an NTFS boot sector.
    /// </summary>
    [AdminTheory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Move_KilledAtProgress50_LayoutChangeOnOtherDisk_ResumesCleanly(int run)
    {
        string name = $"bootfix-cli-{run}";
        VhdxFixture? other = null;
        Thread? activity = null;
        try
        {
            other = VhdxFixture.Create(name + "-other", Log);
            using var vhdx = VhdxFixture.Create(name, Log);
            var m = Prepare(vhdx, name);
            KillAt(m, "progress:50");
            activity = other.StartHideUnhide(Log);
            Recover(m, "progress:50", afterResume: () =>
            {
                activity.Join();
                Log("activity on the other disk done (it overlapped the resume)");
            });
            AssertMoved(vhdx, m);

            var oldFirst = vhdx.ReadRaw(m.OldStart, (int)m.Sector);
            Assert.True(oldFirst.All(x => x == 0), "the old first sector must have been invalidated (zeroed)");
            var newFirst = vhdx.ReadRaw(m.NewStart, (int)m.Sector);
            Assert.Equal("NTFS    ", Encoding.ASCII.GetString(newFirst, 3, 8));
            Assert.Equal((uint)(m.NewStart / m.Sector), BitConverter.ToUInt32(newFirst, 0x1C));   // patched after the restore
            Log("RESULT bootfix CLI regression: hashes, chkdsk, identity, boot sectors all as expected");
        }
        finally
        {
            activity?.Join();
            other?.Dispose();
            SaveLog(name);
        }
    }

    /// <summary>
    /// Review I1 (error 87): "move" is killed right after it un-hid the committed volume while still holding its lock
    /// ("phase unhidden"). The volume then often exists without the mount manager registering it. "commit --journal"
    /// must still finish: the letter comes back by itself or after the arrival nudge (the CLI output says which), and
    /// the move must never fail on the letter.
    /// </summary>
    [AdminTheory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Move_KilledRightAfterUnhide_CommitBringsTheLetterBack(int run)
    {
        string name = $"unhide-kill-{run}";
        try
        {
            using var vhdx = VhdxFixture.Create(name, Log);
            var m = Prepare(vhdx, name);
            KillAt(m, "phase:unhidden");
            Recover(m, "phase:unhidden");
            AssertMoved(vhdx, m);
        }
        finally
        {
            SaveLog(name);
        }
    }

    /// <summary>
    /// A crashed move must not be resumed on another disk: "resume --disk" of a second (offline) VHDX is
    /// refused with DM_E_JOURNAL_MISMATCH (exit code 6), its bytes and the journal stay unchanged, and the
    /// right disk then resumes normally.
    /// </summary>
    [AdminFact]
    public void Resume_OnAnotherDisk_IsRefusedWithoutWriting()
    {
        const string name = "wrong-disk";
        try
        {
            using var other = VhdxFixture.Create(name + "-other", Log);
            other.SetOffline();   // no file-system writes there while its bytes are compared
            using var vhdx = VhdxFixture.Create(name, Log);
            var m = Prepare(vhdx, name);
            KillAt(m, "progress:10");
            var (statusRc, journalBefore) = Cli(["status", "--journal", m.Journal]);
            Assert.Equal(0, statusRc);

            string otherDisk = other.DiskPath;
            string rawBefore = RawHash(otherDisk, m.OldStart, m.Length + Shift);
            var (rc, outp) = Cli(["resume", "--disk", otherDisk, "--journal", m.Journal, "--move", "0"]);
            Assert.True(rc == 6, $"resume on another disk: expected DM_E_JOURNAL_MISMATCH (6), got {rc}: {outp}");
            Assert.Contains("DM_E_JOURNAL_MISMATCH", outp);
            Assert.DoesNotContain("progress ", outp);
            Assert.Equal(rawBefore, RawHash(otherDisk, m.OldStart, m.Length + Shift));
            var (_, journalAfter) = Cli(["status", "--journal", m.Journal]);
            Assert.Equal(journalBefore, journalAfter);
            Log("RESULT resume on another disk refused; other disk bytes and journal unchanged");

            Recover(m, "progress:10");
            AssertMoved(vhdx, m);
        }
        finally
        {
            SaveLog(name);
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

    VhdxFixture? _vhdx;

    VhdxFixture Vhdx(MoveCase m) => _vhdx ?? throw new InvalidOperationException($"no fixture for {m.Disk}");

    /// <summary>Fills DLIKE with 2000 files, shrinks it by 512 MiB and collects what the move needs.</summary>
    MoveCase Prepare(VhdxFixture vhdx, string name)
    {
        _vhdx = vhdx;
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

    /// <summary>Runs "move" and kills it (TerminateProcess) as soon as its output reaches the kill point
    /// ("progress:P" = P % of the bytes copied, "phase:X" = the line "phase X"); fails if it ends by itself.</summary>
    void KillAt(MoveCase m, string killAt, int attempt = 0)
    {
        string[] kp = killAt.Split(':');
        var outBuf = new StringBuilder();
        var psi = new ProcessStartInfo(CliPath)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in m.MoveArgs([])) psi.ArgumentList.Add(a);
        var sw = Stopwatch.StartNew();
        using var p = new Process { StartInfo = psi };
        int killed = 0;
        string trigger = "";
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (outBuf) outBuf.AppendLine(e.Data);
            if (!IsKillPoint(kp, e.Data) || Interlocked.Exchange(ref killed, 1) != 0) return;
            try { p.Kill(true); } catch (InvalidOperationException) { /* already gone */ }
            trigger = e.Data;
        };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (outBuf) outBuf.AppendLine("ERR " + e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        Assert.True(p.WaitForExit(600_000), "move hung");
        p.WaitForExit();   // flush the async readers
        string moveOut;
        lock (outBuf) moveOut = outBuf.ToString();
        Log($"move {(killed != 0 ? $"KILLED at '{trigger}'" : $"EXITED by itself (rc {p.ExitCode})")} after " +
            $"{sw.ElapsedMilliseconds} ms\n{Tail(moveOut, 8)}");
        if (killed == 0 && IsLockBusy(p.ExitCode, moveOut) && attempt < 5)
        {
            Thread.Sleep(2000);   // nothing was changed: try again
            KillAt(m, killAt, attempt + 1);
            return;
        }
        Assert.True(killed != 0, $"kill point {killAt} never reached: {moveOut}");
    }

    static bool IsKillPoint(string[] kp, string line)
    {
        if (kp[0] == "phase") return line == "phase " + kp[1];
        if (!line.StartsWith("progress ")) return false;
        var dt = line["progress ".Length..].Split('/');
        return dt.Length == 2 && ulong.TryParse(dt[0], out ulong done) && ulong.TryParse(dt[1], out ulong total) &&
               total > 0 && done * 100 >= total * ulong.Parse(kp[1]);
    }

    static ulong StatusValue(string status, string key) =>
        ulong.Parse(status.Split('\n').Select(l => l.Trim()).First(l => l.StartsWith(key + " "))
                          .Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]);

    /// <summary>Checks the crash state (journal + sidecar only: the hidden volume is not opened), then finishes
    /// the move: resume + commit if the journal exists, otherwise undo the hiding and move again.</summary>
    void Recover(MoveCase m, string killAt, Action? afterResume = null)
    {
        bool sidecar = File.Exists(m.Sidecar);
        var (statusRc, statusOut) = Cli(["status", "--journal", m.Journal]);
        Log($"after kill at {killAt}: sidecar {sidecar}, journal status rc {statusRc}; " + VolumeProbe(m.VolumePath));
        ulong done = 0, count = 0;
        if (statusRc == 0)
        {
            done = StatusValue(statusOut, "chunks_done");
            count = StatusValue(statusOut, "chunk_count");
            Log($"journal: {done}/{count} chunks done");
        }
        if (killAt.StartsWith("progress:"))   // mid-copy: partial progress, still hidden
        {
            Assert.Equal(0, statusRc);
            Assert.True(done > 0 && done < count, $"expected partial progress, got {done}/{count}");
            Assert.True(sidecar, "the sidecar must exist mid-copy");
        }
        else if (killAt is "phase:patch" or "phase:commit-gpt" or "phase:restore" or "phase:unhidden")   // copied, not finished
        {
            Assert.Equal(0, statusRc);
            Assert.True(done == count && count > 0, $"expected a finished copy, got {done}/{count}");
            if (killAt is not ("phase:restore" or "phase:unhidden")) Assert.True(sidecar, "the sidecar must exist before the restore");
        }

        if (statusRc == 0)
        {
            // The journal exists, so copying may have started: finish it (resume without --volume: a hidden volume
            // has no name to lock; DiskMove.dll locks its old-offset device itself), then commit + restore.
            Log("RECOVERY resume + commit (journal valid)");
            if (done > 0 && done < count)
                Log("old-offset volume device before the resume: " + (Vhdx(m).FindVolumeDevice(m.OldStart, m.Length) ?? "none"));
            var (rc, outp) = Cli(m.ResumeArgs());
            Assert.True(rc == 0, $"resume failed ({rc}): {outp}");
            int copied = outp.Split('\n').Count(l => l.StartsWith("progress "));
            Log($"resume reported {copied} copied chunk(s)");
            if (done < count) Assert.True(copied > 0, "the resume must copy the remaining chunks");
            afterResume?.Invoke();
            (rc, outp) = Cli(m.CommitArgs());
            if (rc != 0)
                Log($"DIAG commit failed: volume device at the old offset {Vhdx(m).FindVolumeDevice(m.OldStart, m.Length) ?? "none"}, " +
                    $"at the new offset {Vhdx(m).FindVolumeDevice(m.NewStart, m.Length) ?? "none"}; " + VolumeProbe(m.VolumePath));
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

    /// <summary>SHA-256 of [offset, offset + length) of a disk, read-only.</summary>
    static string RawHash(string disk, long offset, long length)
    {
        using var h = Native.CreateFileW(disk, 0x80000000 /* GENERIC_READ */, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        Assert.False(h.IsInvalid, $"open {disk}: error {Marshal.GetLastWin32Error()}");
        using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        var buf = new byte[4 * VhdxFixture.MiB];
        for (long pos = 0; pos < length;)
        {
            int want = (int)Math.Min(buf.Length, length - pos);
            int got = RandomAccess.Read(h, buf.AsSpan(0, want), offset + pos);
            Assert.True(got == want, $"short read at {offset + pos}");
            sha.AppendData(buf, 0, got);
            pos += got;
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }

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
