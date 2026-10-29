using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.Json;

namespace WinOptimizer.Services.DiskSpace;

/// <summary>
/// Runs steps 0-7 of a disk space move (spec section 4) and resumes an interrupted one from its journal.
///
/// <para>Journal: created in step 2 (after the lock and verify, before the hide) with both move plans and the
/// <see cref="RestoreState"/>. From then on every step k &gt;= 3 is recorded (SetJournalStep) BEFORE it starts;
/// "Done" is recorded after step 7, then the journal is deleted. Recording step 3 is the point of no return:
/// before it a cancel or a failure undoes everything; after it the only way is forward (resume).</para>
///
/// <para>Resume never guesses: before any write it checks that the actual starts of the source and the Recovery
/// partition are each exactly one of the values the recorded step allows (old / new), their sizes, the target size,
/// the move progress and that the source is still hidden while its data is half moved; otherwise it throws
/// <see cref="DiskMoveStateMismatchException"/>. A hidden partition has no volume device, so a resume never locks
/// it; dmw_run_move's own disk/partition identity check guards the raw writes.</para>
/// </summary>
public sealed class DiskMoveRunner
{
    public const ulong GptHidden = 0x4000000000000000UL, GptNoDriveLetter = 0x8000000000000000UL;
    public const ulong HideBits = GptHidden | GptNoDriveLetter;
    private const long MiB = 1L << 20;
    private const string StampFormat = "yyyyMMdd-HHmmss";
    private static readonly TimeSpan BackupRetention = TimeSpan.FromDays(30);

    /// <summary>C:\ProgramData\WinOptimizer\DiskMove\journal.bin</summary>
    public static string DefaultJournalPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WinOptimizer", "DiskMove", "journal.bin");

    private readonly IDiskMoveBackend _b;
    private readonly string _journal;
    private readonly bool _manageWinRe;

    public DiskMoveRunner(IDiskMoveBackend backend, string journalPath, bool manageWinRe = true)
    {
        _b = backend ?? throw new ArgumentNullException(nameof(backend));
        _journal = journalPath ?? throw new ArgumentNullException(nameof(journalPath));
        _manageWinRe = manageWinRe;
    }

    /// <summary>How often a paused move polls shouldPause (spec: 1 s).</summary>
    public TimeSpan PausePollInterval { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>Clock for the backup file names and their 30-day retention.</summary>
    public Func<DateTime> Now { get; init; } = () => DateTime.Now;

    /// <summary>File.Exists only: the journal's ACL makes it unreadable to a non-admin process.</summary>
    public static bool HasUnfinishedJournal(string journalPath) => File.Exists(journalPath);

    private string DataDir => Path.GetDirectoryName(Path.GetFullPath(_journal))!;

    /// <summary>Mutable state of one run/resume.</summary>
    private sealed class Ctx
    {
        public required int Disk;
        public required RestoreState State;
        public required IProgress<RunProgress> Progress;
        public required Func<bool> ShouldPause;
        public bool ManageWinRe;
        public IDisposable? SourceLock;
        public IDisposable? RecoveryLock;
        // Undo bookkeeping (only before step 3 of a fresh run).
        public bool Shrunk, JournalCreated, SourceHidden;

        public void ReleaseLocks()
        {
            SourceLock?.Dispose();
            SourceLock = null;
            RecoveryLock?.Dispose();
            RecoveryLock = null;
        }
    }

    // ================================================================== RunAsync

    public Task RunAsync(DiskSnapshot disk, PartitionSnapshot target, PartitionSnapshot source, MovePlan plan,
        Func<IReadOnlyList<CheckResult>> recheck, IProgress<RunProgress> progress, Func<bool> shouldPause,
        CancellationToken cancelBeforeMove)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(recheck);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(shouldPause);
        return Task.Run(() => Run(disk, target, source, plan, recheck, progress, shouldPause, cancelBeforeMove));
    }

    private void Run(DiskSnapshot disk, PartitionSnapshot target, PartitionSnapshot source, MovePlan plan,
        Func<IReadOnlyList<CheckResult>> recheck, IProgress<RunProgress> progress, Func<bool> shouldPause,
        CancellationToken cancel)
    {
        if (HasUnfinishedJournal(_journal))
            throw new DiskMoveException($"An unfinished move journal exists ({_journal}); resume it instead of starting a new move.");
        cancel.ThrowIfCancellationRequested();

        // ---------------------------------------------------------------- step 0: checks + layout backup
        if (plan.PartitionsToShift.Count == 0 || plan.PartitionsToShift[0].Guid != source.Guid)
            throw new ArgumentException("The plan must shift the source first.", nameof(plan));
        if (plan.PartitionsToShift.Count != (plan.HasRecovery ? 2 : 1))
            throw new ArgumentException("The plan must shift the source and at most one Recovery partition.", nameof(plan));
        var recovery = plan.HasRecovery ? plan.PartitionsToShift[1] : null;
        long amount = plan.Amount;
        long align = Math.Max(4096, disk.LogicalSectorSize);
        if (amount < MiB || amount % align != 0 || amount >= source.Size)
            throw new ArgumentException($"Invalid amount {amount}.", nameof(plan));
        if (disk.LogicalSectorSize is not (512 or 4096))
            throw new ArgumentException($"Unsupported sector size {disk.LogicalSectorSize}.", nameof(disk));
        if (string.IsNullOrEmpty(source.VolumePath))
            throw new DiskMoveException("The source partition has no volume, so it cannot be locked.");

        progress.Report(new RunProgress(MoveStep.Checks, 0, 0, "Checking the disk layout"));
        int diskNo = _b.FindDiskNumber(disk.Guid);
        void Same(PartitionSnapshot p, string what)
        {
            long start = _b.PartitionStart(diskNo, p.Guid), size = _b.PartitionSize(diskNo, p.Guid);
            if (start != p.Offset || size != p.Size)
                throw new DiskMoveStateMismatchException(
                    $"The {what} partition is now at {start} (+{size} bytes), not at {p.Offset} (+{p.Size}) as analyzed. Analyze again.");
        }
        Same(target, "target");
        Same(source, "source");
        if (recovery is not null)
        {
            Same(recovery, "Recovery");
            ulong ra = _b.GetPartitionAttributes(diskNo, recovery.Guid);
            if (string.IsNullOrEmpty(recovery.VolumePath) && (ra & GptHidden) == 0)
                throw new DiskMoveException("The Recovery partition is visible to Windows but has no volume path, so it cannot be locked.");
        }
        bool manageWinRe = _manageWinRe && recovery is not null;
        bool winRe = false;
        if (manageWinRe)
            winRe = _b.WinReEnabled() ?? throw new DiskMoveException("Could not determine whether Windows RE is enabled (reagentc /info).");
        SaveBackups(disk, diskNo);

        var ctx = new Ctx
        {
            Disk = diskNo,
            Progress = progress,
            ShouldPause = shouldPause,
            ManageWinRe = manageWinRe,
            State = new RestoreState(disk.Guid, source.Guid, source.DriveLetter ?? '\0', 0, recovery?.Guid, winRe,
                target.Guid, amount)
            {
                SourceVolumePath = source.VolumePath,
                SourceStart = source.Offset,
                SourceOriginalSize = source.Size,
                TargetOriginalSize = target.Size,
                RecoveryStart = recovery?.Offset ?? 0,
                RecoverySize = recovery?.Size ?? 0,
                RecoveryVolumePath = recovery?.VolumePath,
                SectorSize = disk.LogicalSectorSize,
                ManageWinRe = manageWinRe,
            },
        };

        try
        {
            // ------------------------------------------------------------ steps 1-2: undone on cancel / failure
            try
            {
                cancel.ThrowIfCancellationRequested();
                ShrinkSource(ctx);
                cancel.ThrowIfCancellationRequested();

                var results = recheck();
                if (results.Any(r => r.Severity is CheckSeverity.Blocker or CheckSeverity.FixFirst))
                {
                    UndoBeforeMove(ctx);
                    throw new DiskMoveAbortedException(results);
                }
                cancel.ThrowIfCancellationRequested();

                LockSourceFresh(ctx, recovery, cancel);
                cancel.ThrowIfCancellationRequested();   // the last chance to cancel
            }
            catch (DiskMoveAbortedException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                UndoBeforeMove(ctx);
                throw;
            }
            catch (Exception ex)
            {
                try
                {
                    UndoBeforeMove(ctx);
                }
                catch (Exception undo)
                {
                    throw new AggregateException(
                        "The move failed before any data was moved, and undoing it failed too. " +
                        (HasUnfinishedJournal(_journal) ? $"Resume will finish the move from {_journal}." : ""), ex, undo);
                }
                ExceptionDispatchInfo.Capture(ex).Throw();
                throw;
            }

            // ------------------------------------------------------------ steps 3-7: forward only
            ContinueFrom(ctx, MoveStep.MoveSource, recordFirst: true);
        }
        finally
        {
            ctx.ReleaseLocks();
        }
    }

    private void ShrinkSource(Ctx ctx)
    {
        var s = ctx.State;
        ctx.Progress.Report(new RunProgress(MoveStep.ShrinkSource, 0, 0, "Shrinking the source partition"));
        long size = _b.PartitionSize(ctx.Disk, s.SourceGuid);
        if (size == s.SourceOriginalSize)
        {
            ctx.Shrunk = true;
            _b.ResizePartition(ctx.Disk, s.SourceGuid, s.SourceShrunkSize);
        }
        else if (size != s.SourceShrunkSize)
        {
            throw new DiskMoveStateMismatchException($"The source is {size} bytes, neither {s.SourceOriginalSize} nor {s.SourceShrunkSize}.");
        }
        ctx.Shrunk = true;
        long after = _b.PartitionSize(ctx.Disk, s.SourceGuid);
        if (after != s.SourceShrunkSize)
            throw new DiskMoveException($"The source is {after} bytes after shrinking, expected {s.SourceShrunkSize}.");
    }

    /// <summary>Step 2 of a fresh run: lock, verify, create the journal, hide + remove the letter, dismount.</summary>
    private void LockSourceFresh(Ctx ctx, PartitionSnapshot? recovery, CancellationToken cancel)
    {
        var s = ctx.State;
        ctx.Progress.Report(new RunProgress(MoveStep.LockSource, 0, 0, "Locking the source volume"));
        ctx.SourceLock = _b.LockVolume(s.SourceVolumePath!);
        if (!_b.VerifyVolume(ctx.SourceLock, ctx.Disk, s.SourceStart, s.SourceShrunkSize))
            throw new DiskMoveException("The locked volume is not the source partition on this disk.");
        cancel.ThrowIfCancellationRequested();

        // Exact attribute bits, read through the DLL, recorded before anything changes them.
        ulong srcAttrs = _b.GetPartitionAttributes(ctx.Disk, s.SourceGuid);
        ulong recAttrs = recovery is null ? 0 : _b.GetPartitionAttributes(ctx.Disk, recovery.Guid);
        if ((srcAttrs & GptHidden) != 0)
            throw new DiskMoveException("The source partition is already hidden.");
        ctx.State = s = s with { SourceGptAttributes = srcAttrs, RecoveryGptAttributes = recAttrs };

        var moves = new List<NativeMovePlan>
        {
            new(s.DiskGuid, s.SourceGuid, s.SectorSize, s.SourceStart, s.SourceShrunkSize, s.Amount, [(0L, s.SourceShrunkSize)]),
        };
        if (s.RecoveryGuid is Guid rg)
            moves.Add(new(s.DiskGuid, rg, s.SectorSize, s.RecoveryStart, s.RecoverySize, s.Amount, [(0L, s.RecoverySize)]));
        _b.CreateJournal(_journal, moves, s);
        ctx.JournalCreated = true;
        cancel.ThrowIfCancellationRequested();

        HideSource(ctx);
    }

    /// <summary>original | HIDDEN | NO_DRIVE_LETTER in one layout write; re-verify the lock handle; remove the
    /// letter (Windows usually already did); dismount. Needs ctx.SourceLock.</summary>
    private void HideSource(Ctx ctx)
    {
        var s = ctx.State;
        ctx.SourceHidden = true;   // before the call: an undo restores the attributes even if it half failed
        _b.SetPartitionAttributes(ctx.Disk, s.SourceGuid, s.SourceGptAttributes | HideBits);
        if (!_b.VerifyVolume(ctx.SourceLock!, ctx.Disk, s.SourceStart, s.SourceShrunkSize))
            throw new DiskMoveException("The source volume lock no longer matches the partition after hiding it.");
        if (s.SourceLetter != '\0') _b.RemoveLetter(s.SourceVolumePath!, s.SourceLetter);
        _b.Dismount(ctx.SourceLock!);
    }

    /// <summary>Undo of steps 1-2 (cancel, failed recheck, failure before step 3). Stops at the first failure,
    /// keeping the journal while the source might still be hidden, so a resume can always finish.</summary>
    private void UndoBeforeMove(Ctx ctx)
    {
        var s = ctx.State;
        ctx.Progress.Report(new RunProgress(MoveStep.LockSource, 0, 0, "Undoing the changes"));
        if (ctx.SourceHidden)
            _b.SetPartitionAttributes(ctx.Disk, s.SourceGuid, s.SourceGptAttributes);   // still locked: nothing mounts before
        ctx.ReleaseLocks();
        if (ctx.SourceHidden && s.SourceLetter != '\0') _b.AssignLetter(s.SourceVolumePath!, s.SourceLetter);
        ctx.SourceHidden = false;
        if (ctx.JournalCreated) _b.DeleteJournal(_journal);
        ctx.JournalCreated = false;
        if (ctx.Shrunk) _b.ResizePartition(ctx.Disk, s.SourceGuid, s.SourceOriginalSize);
        ctx.Shrunk = false;
    }

    // ================================================================== ResumeAsync

    public Task ResumeAsync(IProgress<RunProgress> progress, Func<bool> shouldPause)
    {
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(shouldPause);
        return Task.Run(() => Resume(progress, shouldPause));
    }

    private void Resume(IProgress<RunProgress> progress, Func<bool> shouldPause)
    {
        if (!HasUnfinishedJournal(_journal)) return;
        var info = _b.ReadJournal(_journal, out var state);
        if (info is null) return;
        if (state is null) throw new DiskMoveException($"The journal {_journal} has no restore information.");

        int disk = _b.FindDiskNumber(state.DiskGuid);
        var step = info.Step < MoveStep.LockSource ? MoveStep.LockSource : info.Step;
        var ctx = new Ctx { Disk = disk, State = state, Progress = progress, ShouldPause = shouldPause, ManageWinRe = state.ManageWinRe };
        progress.Report(new RunProgress(step, 0, 0, $"Resuming at step {(int)step}"));

        if (step == MoveStep.Done)
        {
            Finish(ctx);
            return;
        }
        CheckResumeLayout(ctx, info, step);   // read-only; throws before any write

        try
        {
            if (step == MoveStep.LockSource)
            {
                LockSourceResume(ctx);
                ContinueFrom(ctx, MoveStep.MoveSource, recordFirst: true);
            }
            else
            {
                ContinueFrom(ctx, step, recordFirst: false);
            }
        }
        finally
        {
            ctx.ReleaseLocks();
        }
    }

    /// <summary>The disk must be in exactly one of the states the recorded step allows. Read-only.</summary>
    private void CheckResumeLayout(Ctx ctx, JournalInfo info, MoveStep step)
    {
        var s = ctx.State;
        string Where(long v, long oldV, long newV) => v == oldV ? "old" : v == newV ? "new" : "other";
        void Fail(string what) => throw new DiskMoveStateMismatchException(
            $"The disk does not match the journal (step {(int)step}): {what}. Nothing was changed. Journal: {_journal}; " +
            $"layout backups: {DataDir}.");

        // Source: start old before the commit (steps 2-3), old or new during it (4), new after it (5+).
        long srcStart = _b.PartitionStart(ctx.Disk, s.SourceGuid);
        long srcSize = _b.PartitionSize(ctx.Disk, s.SourceGuid);
        string srcAt = Where(srcStart, s.SourceStart, s.SourceNewStart);
        bool srcOk = step switch
        {
            <= MoveStep.MoveSource => srcAt == "old",
            MoveStep.CommitSource => srcAt is "old" or "new",
            _ => srcAt == "new",
        };
        if (!srcOk) Fail($"the source starts at {srcStart} ({srcAt}); expected {s.SourceStart} (old) / {s.SourceNewStart} (new)");
        if (srcSize != s.SourceShrunkSize) Fail($"the source is {srcSize} bytes, expected {s.SourceShrunkSize}");
        if (srcAt == "new" && !info.MoveComplete(0)) Fail("the source is at its new start but its move is not complete");
        if (step >= MoveStep.MoveSource && srcAt == "old")
        {
            ulong a = _b.GetPartitionAttributes(ctx.Disk, s.SourceGuid);
            if ((a & GptHidden) == 0) Fail("the source is visible to Windows while its data is being moved");
        }

        // Recovery: old until step 5, old or new during it. After step 5 it is done (and may have been re-created).
        if (s.RecoveryGuid is Guid rg && step <= MoveStep.MoveRecovery)
        {
            long recStart = _b.PartitionStart(ctx.Disk, rg);
            long recSize = _b.PartitionSize(ctx.Disk, rg);
            string recAt = Where(recStart, s.RecoveryStart, s.RecoveryNewStart);
            bool recOk = step < MoveStep.MoveRecovery ? recAt == "old" : recAt is "old" or "new";
            if (!recOk) Fail($"the Recovery partition starts at {recStart} ({recAt}); expected {s.RecoveryStart} (old) / {s.RecoveryNewStart} (new)");
            if (recSize != s.RecoverySize) Fail($"the Recovery partition is {recSize} bytes, expected {s.RecoverySize}");
            if (recAt == "new" && !info.MoveComplete(1)) Fail("the Recovery partition is at its new start but its move is not complete");
            if (step == MoveStep.MoveRecovery && recAt == "old" && info.MoveIndex == 1 && info.ChunksDone > 0 &&
                (_b.GetPartitionAttributes(ctx.Disk, rg) & GptHidden) == 0)
                Fail("the Recovery partition is visible to Windows while its data is being moved");
        }

        long tSize = _b.PartitionSize(ctx.Disk, s.TargetGuid);
        long tNew = s.TargetOriginalSize + s.Amount;
        bool tOk = step switch
        {
            < MoveStep.ExtendTarget => tSize == s.TargetOriginalSize,
            MoveStep.ExtendTarget => tSize == s.TargetOriginalSize || tSize == tNew,
            _ => tSize == tNew,
        };
        if (!tOk) Fail($"the target is {tSize} bytes; expected {s.TargetOriginalSize} or {tNew}");
    }

    /// <summary>Step 2 on resume: lock + hide only if the source is still visible (a hidden one has no volume).</summary>
    private void LockSourceResume(Ctx ctx)
    {
        var s = ctx.State;
        ctx.Progress.Report(new RunProgress(MoveStep.LockSource, 0, 0, "Locking the source volume"));
        ulong a = _b.GetPartitionAttributes(ctx.Disk, s.SourceGuid);
        if ((a & GptHidden) == 0)
        {
            ctx.SourceLock = _b.LockVolume(s.SourceVolumePath!);
            if (!_b.VerifyVolume(ctx.SourceLock, ctx.Disk, s.SourceStart, s.SourceShrunkSize))
                throw new DiskMoveStateMismatchException("The locked volume is not the source partition on this disk.");
            HideSource(ctx);
        }
        else if (s.SourceLetter != '\0')
        {
            _b.RemoveLetter(s.SourceVolumePath!, s.SourceLetter);   // tolerant: Windows dropped it with the hide
        }
    }

    // ================================================================== steps 3-7 (run and resume)

    private void ContinueFrom(Ctx ctx, MoveStep first, bool recordFirst)
    {
        for (var step = first; step <= MoveStep.Verify; step++)
        {
            if (step != first || recordFirst) _b.SetJournalStep(_journal, step);
            switch (step)
            {
                case MoveStep.MoveSource: MoveSource(ctx); break;
                case MoveStep.CommitSource: CommitSource(ctx); break;
                case MoveStep.MoveRecovery: MoveRecovery(ctx); break;
                case MoveStep.ExtendTarget: ExtendTarget(ctx); break;
                case MoveStep.Verify: Verify(ctx); break;
            }
        }
        _b.SetJournalStep(_journal, MoveStep.Done);
        Finish(ctx);
    }

    private void MoveSource(Ctx ctx)
    {
        ctx.Progress.Report(new RunProgress(MoveStep.MoveSource, 0, ctx.State.SourceShrunkSize, "Moving the source partition"));
        RunMove(ctx, 0, MoveStep.MoveSource);
    }

    /// <summary>Copy with pause: the progress callback returns shouldPause(); a stopped move waits (polling) and runs again.</summary>
    private void RunMove(Ctx ctx, int index, MoveStep step)
    {
        while (true)
        {
            bool complete = _b.RunMove(ctx.Disk, _journal, index, (done, total) =>
            {
                ctx.Progress.Report(new RunProgress(step, done, total, "Moving data"));
                return ctx.ShouldPause();
            });
            if (complete) return;
            ctx.Progress.Report(new RunProgress(step, 0, 0, "Paused"));
            while (ctx.ShouldPause()) Thread.Sleep(PausePollInterval);
        }
    }

    /// <summary>Step 4: NTFS HiddenSectors, GPT start, exact attributes back, letter back. Idempotent.</summary>
    private void CommitSource(Ctx ctx)
    {
        var s = ctx.State;
        ctx.Progress.Report(new RunProgress(MoveStep.CommitSource, 0, 0, "Updating the partition table"));
        long start = _b.PartitionStart(ctx.Disk, s.SourceGuid);
        ulong attrs = _b.GetPartitionAttributes(ctx.Disk, s.SourceGuid);
        bool restored = attrs == s.SourceGptAttributes;
        if (start == s.SourceStart && restored)
            throw new DiskMoveStateMismatchException("The source is visible at its old start after its data was moved.");
        if (start != s.SourceStart && start != s.SourceNewStart)
            throw new DiskMoveStateMismatchException($"The source starts at {start}, neither old nor new.");
        if (!restored)
        {
            // Raw writes are only allowed while the volume is hidden/dismounted: patch before the attributes come back.
            _b.PatchNtfsHiddenSectors(ctx.Disk, s.SourceNewStart, s.SourceShrunkSize, s.SectorSize);
            _b.SetPartitionStart(ctx.Disk, s.SourceGuid, s.SourceNewStart);
            _b.SetPartitionAttributes(ctx.Disk, s.SourceGuid, s.SourceGptAttributes);   // still locked in a fresh run
        }
        ctx.SourceLock?.Dispose();
        ctx.SourceLock = null;
        if (s.SourceLetter != '\0') _b.AssignLetter(s.SourceVolumePath!, s.SourceLetter);
    }

    /// <summary>Step 5: WinRE off, lock + hide + dismount the Recovery, move it, set its start, exact attributes
    /// back, WinRE back (KB5028997 fallback if enabling fails). Idempotent.</summary>
    private void MoveRecovery(Ctx ctx)
    {
        var s = ctx.State;
        if (s.RecoveryGuid is not Guid rg) return;
        ctx.Progress.Report(new RunProgress(MoveStep.MoveRecovery, 0, s.RecoverySize, "Moving the Recovery partition"));
        long start = _b.PartitionStart(ctx.Disk, rg);
        ulong attrs = _b.GetPartitionAttributes(ctx.Disk, rg);
        bool moved = start == s.RecoveryNewStart;
        if (!moved && start != s.RecoveryStart)
            throw new DiskMoveStateMismatchException($"The Recovery partition starts at {start}, neither old nor new.");

        if (!(moved && attrs == s.RecoveryGptAttributes))
        {
            if (ctx.ManageWinRe && !moved) _b.SetWinRe(false);
            if (!moved)
            {
                if ((attrs & GptHidden) == 0)
                {
                    if (string.IsNullOrEmpty(s.RecoveryVolumePath))
                        throw new DiskMoveException("The Recovery partition is visible but has no volume path to lock.");
                    ctx.RecoveryLock = _b.LockVolume(s.RecoveryVolumePath);
                    if (!_b.VerifyVolume(ctx.RecoveryLock, ctx.Disk, s.RecoveryStart, s.RecoverySize))
                        throw new DiskMoveStateMismatchException("The locked volume is not the Recovery partition on this disk.");
                    _b.SetPartitionAttributes(ctx.Disk, rg, s.RecoveryGptAttributes | HideBits);
                    if (!_b.VerifyVolume(ctx.RecoveryLock, ctx.Disk, s.RecoveryStart, s.RecoverySize))
                        throw new DiskMoveException("The Recovery volume lock no longer matches the partition after hiding it.");
                    _b.Dismount(ctx.RecoveryLock);
                }
                RunMove(ctx, 1, MoveStep.MoveRecovery);
            }
            // Still hidden here: raw writes are allowed. The DLL leaves a non-NTFS boot sector alone.
            _b.PatchNtfsHiddenSectors(ctx.Disk, s.RecoveryNewStart, s.RecoverySize, s.SectorSize);
            _b.SetPartitionStart(ctx.Disk, rg, s.RecoveryNewStart);
            _b.SetPartitionAttributes(ctx.Disk, rg, s.RecoveryGptAttributes);
            ctx.RecoveryLock?.Dispose();
            ctx.RecoveryLock = null;
        }

        if (!ctx.ManageWinRe) return;
        try
        {
            _b.SetWinRe(s.WinReWasEnabled);
        }
        catch (DiskMoveException) when (s.WinReWasEnabled)
        {
            ctx.Progress.Report(new RunProgress(MoveStep.MoveRecovery, 0, 0, "Re-creating the Recovery partition (KB5028997)"));
            _b.RecreateRecoveryPartition(ctx.Disk, rg, s.RecoveryNewStart, s.RecoverySize);
            _b.SetWinRe(true);
        }
    }

    /// <summary>Step 6: extend the target by X. Idempotent.</summary>
    private void ExtendTarget(Ctx ctx)
    {
        var s = ctx.State;
        ctx.Progress.Report(new RunProgress(MoveStep.ExtendTarget, 0, 0, "Extending the target partition"));
        long size = _b.PartitionSize(ctx.Disk, s.TargetGuid), want = s.TargetOriginalSize + s.Amount;
        if (size == s.TargetOriginalSize) _b.ResizePartition(ctx.Disk, s.TargetGuid, want);
        else if (size != want) throw new DiskMoveStateMismatchException($"The target is {size} bytes, neither {s.TargetOriginalSize} nor {want}.");
    }

    /// <summary>Step 7: read-only chkdsk of the source, WinRE as before, sizes and start as planned.</summary>
    private void Verify(Ctx ctx)
    {
        var s = ctx.State;
        ctx.Progress.Report(new RunProgress(MoveStep.Verify, 0, 0, "Verifying the result"));
        var problems = new List<string>();
        long tSize = _b.PartitionSize(ctx.Disk, s.TargetGuid);
        if (tSize != s.TargetOriginalSize + s.Amount) problems.Add($"target is {tSize} bytes");
        long srcSize = _b.PartitionSize(ctx.Disk, s.SourceGuid), srcStart = _b.PartitionStart(ctx.Disk, s.SourceGuid);
        if (srcSize != s.SourceShrunkSize || srcStart != s.SourceNewStart) problems.Add($"source is at {srcStart} (+{srcSize})");
        if (ctx.ManageWinRe && _b.WinReEnabled() != s.WinReWasEnabled)
            problems.Add($"Windows RE is not {(s.WinReWasEnabled ? "enabled" : "disabled")} as before");
        if (s.SourceLetter != '\0')
        {
            int rc = _b.Chkdsk(s.SourceLetter);
            if (rc != 0) problems.Add($"chkdsk {s.SourceLetter}: returned {rc}");
        }
        if (problems.Count > 0)
            throw new DiskMoveVerificationException("Verification failed: " + string.Join("; ", problems) +
                                                    $". The journal is kept: {_journal}.");
    }

    private void Finish(Ctx ctx)
    {
        _b.DeleteJournal(_journal);
        CleanupBackups();
        ctx.Progress.Report(new RunProgress(MoveStep.Done, 0, 0, "Done"));
    }

    // ================================================================== layout backups

    private void SaveBackups(DiskSnapshot disk, int diskNo)
    {
        Directory.CreateDirectory(DataDir);
        string stamp = Now().ToString(StampFormat, CultureInfo.InvariantCulture);
        File.WriteAllBytes(Path.Combine(DataDir, $"layout-{stamp}.json"),
            JsonSerializer.SerializeToUtf8Bytes(disk, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllBytes(Path.Combine(DataDir, $"gpt-{stamp}.bin"), _b.ReadRawGpt(diskNo, disk.LogicalSectorSize));
    }

    /// <summary>Deletes layout/GPT backups whose name stamp is older than 30 days (best effort).</summary>
    private void CleanupBackups()
    {
        try
        {
            if (!Directory.Exists(DataDir)) return;
            var limit = Now() - BackupRetention;
            foreach (var f in Directory.EnumerateFiles(DataDir, "layout-*.json").Concat(Directory.EnumerateFiles(DataDir, "gpt-*.bin")))
            {
                string name = Path.GetFileNameWithoutExtension(f);
                string stamp = name[(name.IndexOf('-') + 1)..];
                if (DateTime.TryParseExact(stamp, StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) && t < limit)
                {
                    try { File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
