using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Security.AccessControl;
using System.Security.Principal;
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
/// <see cref="DiskMoveStateMismatchException"/>. A hidden partition has no letter and no volume GUID path, so the runner
/// cannot lock it by name; but Windows keeps its volume device at the OLD offset and may mount it on any layout change.
/// Two defences (journal v2): the core zeroes the old boot sectors before the first chunk (any mount then sees RAW),
/// and dmw_run_move, given no lock of ours, finds that old-offset device itself and holds it locked + dismounted for
/// the whole copy. dmw_run_move's own disk/partition identity check guards the raw writes.</para>
/// </summary>
public sealed class DiskMoveRunner
{
    public const ulong GptHidden = 0x4000000000000000UL, GptNoDriveLetter = 0x8000000000000000UL;
    public const ulong HideBits = GptHidden | GptNoDriveLetter;
    private const long MiB = 1L << 20;
    private const string StampFormat = "yyyyMMdd-HHmmss";
    /// <summary>Marker written before step 1 and removed once the journal exists (or after an undo).</summary>
    public const string PendingFileName = "pending.json";
    public const string JournalFileName = "journal.bin";
    public const string WinReWarning =
        "Windows Recovery could not be re-enabled automatically. Run `reagentc /enable` as administrator; see Microsoft KB5028997.";
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
        SecureDataFolder = string.Equals(DataDir, Path.GetDirectoryName(DefaultJournalPath), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Guard the data folder (<see cref="DataFolderSecurity"/>): owner and protected ACLs on it and its parent,
    /// and an admin-only check of the folders, journal and marker before they are trusted. Defaults to true for the
    /// default ProgramData folder, false elsewhere (tests).</summary>
    public bool SecureDataFolder { get; init; }

    /// <summary>Overrides the data folder guard (tests). Null: <see cref="DataFolderSecurity.Guard"/> when
    /// <see cref="SecureDataFolder"/>, else none.</summary>
    public IDataFolderGuard? FolderGuard { get; init; }

    private IDataFolderGuard? Guard => FolderGuard ?? (SecureDataFolder ? DataFolderSecurity.Guard : null);

    /// <summary>Review I1: before any trust of journal.bin / pending.json and before a run writes there. Throws
    /// <see cref="DiskMoveUnsafeFilesException"/>; nothing on the disk has changed at that point.</summary>
    private void PrepareDataFolder() => Guard?.PrepareTrusted(DataDir, [_journal, PendingPath]);

    /// <summary>How often a paused move polls shouldPause (spec: 1 s).</summary>
    public TimeSpan PausePollInterval { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>Clock for the backup file names and their 30-day retention.</summary>
    public Func<DateTime> Now { get; init; } = () => DateTime.Now;

    /// <summary>File.Exists only (a non-admin process cannot see an admin-only file this way; use
    /// <see cref="HasUnfinishedMove"/> for that).</summary>
    public static bool HasUnfinishedJournal(string journalPath) => File.Exists(journalPath);

    /// <summary>A journal ("journal.bin") or a pending marker ("pending.json") exists in the data folder: the app must
    /// open the Resume screen. Uses directory listing only: the files are readable by administrators only, so
    /// File.Exists is false for a non-admin process, but BUILTIN\Users may list the folder
    /// (<see cref="CreateDataFolderSecurity"/>). A missing folder means no move.</summary>
    public static bool HasUnfinishedMove(string dataDir)
    {
        try
        {
            return Directory.EnumerateFiles(dataDir).Select(Path.GetFileName).Any(n =>
                string.Equals(n, JournalFileName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(n, PendingFileName, StringComparison.OrdinalIgnoreCase));
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private string PendingPath => Path.Combine(DataDir, PendingFileName);

    private string DataDir => Path.GetDirectoryName(Path.GetFullPath(_journal))!;

    /// <summary>Which way a resume will go, from what is in the data folder (mirrors <see cref="Resume"/>).</summary>
    public enum ResumeDirection
    {
        /// <summary>Neither a journal nor a marker.</summary>
        None,
        /// <summary>The move continues forward (journal without an undo marker, or at step 3 or later).</summary>
        Finish,
        /// <summary>Nothing was moved yet: the attempt is undone and the source gets its space back.</summary>
        Undo,
        /// <summary>The journal could not be read and there is no marker: the resume stops without changes.</summary>
        Unknown,
    }

    /// <summary>Predicts what <see cref="ResumeAsync"/> will do. <paramref name="journal"/> null with
    /// <paramref name="journalExists"/> means the journal is unreadable.</summary>
    public static ResumeDirection PredictResume(bool journalExists, JournalInfo? journal, PendingMove? pending)
    {
        if (!journalExists) return pending is null ? ResumeDirection.None : ResumeDirection.Undo;
        if (journal is null) return pending is null ? ResumeDirection.Unknown : ResumeDirection.Undo;   // torn create
        if (pending is { Undoing: true } && journal.Step < MoveStep.MoveSource) return ResumeDirection.Undo;
        return ResumeDirection.Finish;
    }

    /// <summary>Mutable state of one run/resume.</summary>
    private sealed class Ctx
    {
        public required int Disk;
        public required RestoreState State;
        public required IProgress<RunProgress> Progress;
        public required Func<bool> ShouldPause;
        public bool ManageWinRe;
        public IDisposable? SourceLock;
        /// <summary>SourceLock is already dismounted (a second FSCTL_DISMOUNT_VOLUME on the handle fails).</summary>
        public bool SourceDismounted;
        public IDisposable? RecoveryLock;
        // Undo bookkeeping (only before step 3 of a fresh run).
        public bool Shrunk, JournalCreated, SourceHidden, UndoMarkerWritten;
        public readonly List<string> Warnings = [];
        public char? FinalLetter;

        public void Warn(MoveStep step, string message)
        {
            if (Warnings.Contains(message)) return;
            Warnings.Add(message);
            Progress.Report(new RunProgress(step, 0, 0, "Warning: " + message));
        }

        public DiskMoveResult Result(DiskMoveOutcome outcome) => new(outcome, FinalLetter, Warnings.ToList());

        public void ReleaseLocks()
        {
            SourceLock?.Dispose();
            SourceLock = null;
            RecoveryLock?.Dispose();
            RecoveryLock = null;
        }
    }

    // ================================================================== RunAsync

    public Task<DiskMoveResult> RunAsync(DiskSnapshot disk, PartitionSnapshot target, PartitionSnapshot source, MovePlan plan,
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

    private DiskMoveResult Run(DiskSnapshot disk, PartitionSnapshot target, PartitionSnapshot source, MovePlan plan,
        Func<IReadOnlyList<CheckResult>> recheck, IProgress<RunProgress> progress, Func<bool> shouldPause,
        CancellationToken cancel)
    {
        if (HasUnfinishedJournal(_journal) || File.Exists(PendingPath))
            throw new DiskMoveException($"An unfinished move exists in {DataDir}; resume it instead of starting a new move.");
        cancel.ThrowIfCancellationRequested();
        PrepareDataFolder();   // refuse up front, before anything is written (review I1 / T12 N1)

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
            FinalLetter = source.DriveLetter,
        };
        // Before step 1: without it a crash before the journal exists would strand X bytes after the source.
        WritePending(new PendingMove(disk.Guid, source.Guid, source.DriveLetter ?? '\0', source.Size, target.Guid, amount, Now()));

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
                        "The move failed before any data was moved, and undoing it failed too. Your data is safe. " +
                        UndoFailedHint(ctx), ex, undo);
                }
                ExceptionDispatchInfo.Capture(ex).Throw();
                throw;
            }

            // ------------------------------------------------------------ steps 3-7: forward only
            ContinueFrom(ctx, MoveStep.MoveSource, recordFirst: true);
            return ctx.Result(DiskMoveOutcome.Completed);
        }
        finally
        {
            ctx.ReleaseLocks();
        }
    }

    /// <summary>What the next resume does after a failed undo (review minor 2): it undoes once the Undoing marker is
    /// written (or with a marker and no journal), it continues forward from a journal without that marker.</summary>
    private string UndoFailedHint(Ctx ctx)
    {
        bool journal = HasUnfinishedJournal(_journal), pending = File.Exists(PendingPath);
        if (ctx.UndoMarkerWritten || (pending && !journal))
            return "The Resume screen (Continue) will finish undoing it and give the source its original size back.";
        if (journal) return $"The Resume screen (Continue) will continue the move from the journal {_journal}.";
        return "";
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

    /// <summary>Step 2 of a fresh run: lock, verify, dismount, save the boot sectors (source and Recovery), create the
    /// journal, hide + remove the letter, dismount.</summary>
    private void LockSourceFresh(Ctx ctx, PartitionSnapshot? recovery, CancellationToken cancel)
    {
        var s = ctx.State;
        ctx.Progress.Report(new RunProgress(MoveStep.LockSource, 0, 0, "Locking the source volume"));
        ctx.SourceLock = _b.LockVolume(s.SourceVolumePath!);
        if (!_b.VerifyVolume(ctx.SourceLock, ctx.Disk, s.SourceStart, s.SourceShrunkSize))
            throw new DiskMoveException("The locked volume is not the source partition on this disk.");
        // Journal v2: the original boot sectors, read after the lock + dismount and before any write.
        _b.Dismount(ctx.SourceLock);
        ctx.SourceDismounted = true;
        var srcBoot = _b.ReadBootSectors(ctx.Disk, s.SourceStart, s.SourceShrunkSize, s.SectorSize);
        var recBoot = recovery is null ? default : ReadRecoveryBootSectors(ctx, recovery);
        SaveBootBackup(s, srcBoot, recovery is null ? null : recBoot);
        cancel.ThrowIfCancellationRequested();

        // Exact attribute bits, read through the DLL, recorded before anything changes them.
        ulong srcAttrs = _b.GetPartitionAttributes(ctx.Disk, s.SourceGuid);
        ulong recAttrs = recovery is null ? 0 : _b.GetPartitionAttributes(ctx.Disk, recovery.Guid);
        if ((srcAttrs & GptHidden) != 0)
            throw new DiskMoveException("The source partition is already hidden.");
        ctx.State = s = s with { SourceGptAttributes = srcAttrs, RecoveryGptAttributes = recAttrs };

        var moves = new List<NativeMovePlan>
        {
            new(s.DiskGuid, s.SourceGuid, s.SectorSize, s.SourceStart, s.SourceShrunkSize, s.Amount, [(0L, s.SourceShrunkSize)],
                srcBoot.First, srcBoot.Backup),
        };
        if (s.RecoveryGuid is Guid rg)
            moves.Add(new(s.DiskGuid, rg, s.SectorSize, s.RecoveryStart, s.RecoverySize, s.Amount, [(0L, s.RecoverySize)],
                recBoot.First, recBoot.Backup));
        _b.CreateJournal(_journal, moves, s);
        ctx.JournalCreated = true;
        DeletePending();   // the journal supersedes it
        cancel.ThrowIfCancellationRequested();

        HideSource(ctx);
    }

    /// <summary>The Recovery's boot sectors for the journal (step 2; the journal holds both moves). A visible Recovery is
    /// locked, verified and dismounted for the read, then released (step 5 locks it again for its move; reagentc needs it
    /// in between); a hidden one has no volume path and is read as it is. Nothing is written either way.</summary>
    private (byte[] First, byte[] Backup) ReadRecoveryBootSectors(Ctx ctx, PartitionSnapshot recovery)
    {
        var s = ctx.State;
        if ((_b.GetPartitionAttributes(ctx.Disk, recovery.Guid) & GptHidden) != 0 || string.IsNullOrEmpty(s.RecoveryVolumePath))
            return _b.ReadBootSectors(ctx.Disk, s.RecoveryStart, s.RecoverySize, s.SectorSize);
        using var rlock = _b.LockVolume(s.RecoveryVolumePath);
        if (!_b.VerifyVolume(rlock, ctx.Disk, s.RecoveryStart, s.RecoverySize))
            throw new DiskMoveException("The locked volume is not the Recovery partition on this disk.");
        _b.Dismount(rlock);
        return _b.ReadBootSectors(ctx.Disk, s.RecoveryStart, s.RecoverySize, s.SectorSize);
    }

    /// <summary>original | HIDDEN | NO_DRIVE_LETTER in one layout write; re-verify the lock handle; remove the
    /// letter (Windows usually already did); dismount unless step 2 already did. Needs ctx.SourceLock.</summary>
    private void HideSource(Ctx ctx)
    {
        var s = ctx.State;
        ctx.SourceHidden = true;   // before the call: an undo restores the attributes even if it half failed
        _b.SetPartitionAttributes(ctx.Disk, s.SourceGuid, s.SourceGptAttributes | HideBits);
        if (!_b.VerifyVolume(ctx.SourceLock!, ctx.Disk, s.SourceStart, s.SourceShrunkSize))
            throw new DiskMoveException("The source volume lock no longer matches the partition after hiding it.");
        if (s.SourceLetter != '\0') _b.RemoveLetter(s.SourceVolumePath!, s.SourceLetter);
        if (!ctx.SourceDismounted) _b.Dismount(ctx.SourceLock!);   // a fresh run dismounted it before the boot read
        ctx.SourceDismounted = true;
    }

    /// <summary>Undo of steps 1-2 (cancel, failed recheck, failure before step 3). Order: (1) pending marker with
    /// Undoing = true, (2) attributes + letter back, (3) delete the journal, (4) resize the source back, (5) delete the
    /// marker last. Stops at the first failure; a resume then finishes the undo from the marker (+ journal).</summary>
    private void UndoBeforeMove(Ctx ctx)
    {
        var s = ctx.State;
        ctx.Progress.Report(new RunProgress(MoveStep.LockSource, 0, 0, "Undoing the changes"));
        // Read-only first: the source must still be on this disk (also keeps a crashed process from touching the marker).
        long size = _b.PartitionSize(ctx.Disk, s.SourceGuid);
        if (size > s.SourceOriginalSize)
            throw new DiskMoveStateMismatchException(
                $"The source is {size} bytes, larger than its original {s.SourceOriginalSize}; the undo changed nothing.");
        WritePending(PendingFor(s, undoing: true));
        ctx.UndoMarkerWritten = true;
        if (ctx.SourceHidden)
            _b.SetPartitionAttributes(ctx.Disk, s.SourceGuid, s.SourceGptAttributes);   // still locked: nothing mounts before
        ctx.ReleaseLocks();
        if (ctx.SourceHidden && s.SourceLetter != '\0') _b.AssignLetter(s.SourceVolumePath!, s.SourceLetter);
        ctx.SourceHidden = false;
        if (ctx.JournalCreated) _b.DeleteJournal(_journal);
        ctx.JournalCreated = false;
        if (size < s.SourceOriginalSize) _b.ResizePartition(ctx.Disk, s.SourceGuid, s.SourceOriginalSize);   // never shrink
        ctx.Shrunk = false;
        DeletePending();
    }

    // ================================================================== ResumeAsync

    public Task<DiskMoveResult> ResumeAsync(IProgress<RunProgress> progress, Func<bool> shouldPause)
    {
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(shouldPause);
        return Task.Run(() => Resume(progress, shouldPause));
    }

    private DiskMoveResult Resume(IProgress<RunProgress> progress, Func<bool> shouldPause)
    {
        if (!HasUnfinishedJournal(_journal) && !File.Exists(PendingPath))
            return new DiskMoveResult(DiskMoveOutcome.NothingToDo, null, []);
        PrepareDataFolder();   // review I1: nothing is read or trusted before this
        if (!HasUnfinishedJournal(_journal)) return UndoPending(progress);
        var pending = ReadPending();
        JournalInfo? info;
        RestoreState? state;
        try
        {
            info = _b.ReadJournal(_journal, out state);
        }
        catch (DiskMoveException ex) when (pending is not null)
        {
            // A marker next to an unreadable journal: the create was torn (the marker goes away right after a
            // successful create) or an undo was running; either way no data has moved yet.
            return UndoTornJournal(pending, progress, ex);
        }
        if (info is null) return new DiskMoveResult(DiskMoveOutcome.NothingToDo, null, []);
        if (state is null) throw new DiskMoveException($"The journal {_journal} has no restore information.");
        if (pending is { Undoing: true } && info.Step < MoveStep.MoveSource)
            return UndoWithJournal(state, progress);   // finish the interrupted undo; nothing was moved
        DeletePending();   // a crash right after CreateJournal can leave both: the journal wins

        int disk = _b.FindDiskNumber(state.DiskGuid);
        var step = info.Step < MoveStep.LockSource ? MoveStep.LockSource : info.Step;
        var ctx = new Ctx
        {
            Disk = disk, State = state, Progress = progress, ShouldPause = shouldPause, ManageWinRe = state.ManageWinRe,
            FinalLetter = state.SourceLetter == '\0' ? null : state.SourceLetter,
        };
        progress.Report(new RunProgress(step, 0, 0, $"Resuming at step {(int)step}"));

        if (step == MoveStep.Done)
        {
            Finish(ctx);
            return ctx.Result(DiskMoveOutcome.Completed);
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
            return ctx.Result(DiskMoveOutcome.Completed);
        }
        finally
        {
            ctx.ReleaseLocks();
        }
    }

    /// <summary>An undo (cancel / failure before step 3) was interrupted while the journal still existed: restore
    /// the attributes and letter, delete the journal, then give the source its size back (UndoPending).</summary>
    private DiskMoveResult UndoWithJournal(RestoreState s, IProgress<RunProgress> progress)
    {
        progress.Report(new RunProgress(MoveStep.LockSource, 0, 0, "Finishing the interrupted undo"));
        int disk = _b.FindDiskNumber(s.DiskGuid);
        long start = _b.PartitionStart(disk, s.SourceGuid), size = _b.PartitionSize(disk, s.SourceGuid);
        if (start != s.SourceStart || (size != s.SourceShrunkSize && size != s.SourceOriginalSize))
            throw new DiskMoveStateMismatchException(
                $"The source is at {start} (+{size} bytes), not where the journal left it; nothing was changed. Journal: {_journal}.");
        if (_b.GetPartitionAttributes(disk, s.SourceGuid) != s.SourceGptAttributes)
            _b.SetPartitionAttributes(disk, s.SourceGuid, s.SourceGptAttributes);   // nothing was moved: safe to show again
        if (s.SourceLetter != '\0' && s.SourceVolumePath is not null) _b.AssignLetter(s.SourceVolumePath, s.SourceLetter);
        _b.DeleteJournal(_journal);
        return UndoPending(progress);
    }

    /// <summary>Pending marker + unreadable journal (m1): undo only if the source is provably untouched apart from
    /// the shrink (visible, at its start, original or shrunk size); otherwise stop without changing anything.</summary>
    private DiskMoveResult UndoTornJournal(PendingMove pending, IProgress<RunProgress> progress, Exception journalError)
    {
        int disk = _b.FindDiskNumber(pending.DiskGuid);
        long size = _b.PartitionSize(disk, pending.SourceGuid);
        ulong attrs = _b.GetPartitionAttributes(disk, pending.SourceGuid);
        if ((attrs & GptHidden) != 0 || (size != pending.SourceOriginalSize && size != pending.SourceOriginalSize - pending.Amount))
            throw new DiskMoveStateMismatchException(
                $"The journal {_journal} cannot be read ({journalError.Message}) and the source is not in a state that is safe " +
                "to undo automatically; nothing was changed.");
        progress.Report(new RunProgress(MoveStep.LockSource, 0, 0, "The journal is incomplete; undoing the interrupted attempt"));
        _b.DeleteJournal(_journal);
        return UndoPending(progress);
    }

    /// <summary>No journal but a pending marker: the attempt stopped in step 1 or 2 before any data moved. Never
    /// continue it silently: give the source its original size back and remove the marker.</summary>
    private DiskMoveResult UndoPending(IProgress<RunProgress> progress)
    {
        var pending = ReadPending() ?? throw new DiskMoveException($"The pending move marker {PendingPath} is gone.");
        progress.Report(new RunProgress(MoveStep.ShrinkSource, 0, 0, "Undoing the interrupted attempt"));
        int disk = _b.FindDiskNumber(pending.DiskGuid);
        long size = _b.PartitionSize(disk, pending.SourceGuid);
        if (size > pending.SourceOriginalSize)
            throw new DiskMoveStateMismatchException(
                $"The source is {size} bytes, larger than its original {pending.SourceOriginalSize}; nothing was changed. Marker: {PendingPath}.");
        string name = pending.SourceLetter == '\0' ? "the source partition" : $"{pending.SourceLetter}:";
        string warning;
        if (size < pending.SourceOriginalSize)
        {
            _b.ResizePartition(disk, pending.SourceGuid, pending.SourceOriginalSize);
            warning = $"The previous attempt was interrupted before any data moved; {name} was restored to its original size.";
        }
        else
        {
            warning = $"The previous attempt was interrupted before any data moved; {name} was not changed.";
        }
        DeletePending();
        progress.Report(new RunProgress(MoveStep.ShrinkSource, 0, 0, "Warning: " + warning));
        return new DiskMoveResult(DiskMoveOutcome.Undone, pending.SourceLetter == '\0' ? null : pending.SourceLetter, [warning]);
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

    /// <summary>Step 2 on resume: lock + hide only if the source is still visible (a hidden one has no volume path;
    /// DiskMove.dll locks its old-offset device itself during the copy).</summary>
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
            try
            {
                switch (step)
                {
                    case MoveStep.MoveSource: MoveSource(ctx); break;
                    case MoveStep.CommitSource: CommitSource(ctx); break;
                    case MoveStep.MoveRecovery: MoveRecovery(ctx); break;
                    case MoveStep.ExtendTarget: ExtendTarget(ctx); break;
                    case MoveStep.Verify: Verify(ctx); break;
                }
            }
            catch (DiskMoveException ex) when (step is MoveStep.MoveRecovery or MoveStep.ExtendTarget &&
                                               ex is not DiskMoveStateMismatchException)
            {
                // Review I2: after step 4 the source's data is moved and committed. A failure here (Recovery volume
                // busy, reagentc /disable failed, extend failed) is usually transient: keep the journal (resumable) and
                // say clearly that the data is safe.
                throw new DiskMoveAfterCommitException(AfterCommitText(ctx, step, ex), ex);
            }
        }
        _b.SetJournalStep(_journal, MoveStep.Done);
        Finish(ctx);
    }

    private static string AfterCommitText(Ctx ctx, MoveStep step, Exception ex)
    {
        var s = ctx.State;
        string src = ctx.FinalLetter is char l ? $"{l}:" : s.SourceLetter != '\0' ? $"{s.SourceLetter}:" : "The data partition";
        string what = step == MoveStep.MoveRecovery ? "Moving the Recovery partition" : "Extending the Windows partition";
        string winRe = step == MoveStep.MoveRecovery && s.ManageWinRe && s.WinReWasEnabled
            ? " Windows Recovery may stay turned off until the move is finished." : "";
        return $"{src}'s data is already moved and safe. {what} did not finish: {ex.Message} This is usually temporary " +
               "(for example the Recovery volume was in use). Choose \"Try again later\" on the Resume screen and continue " +
               "later, for example after a restart; until then the Windows partition is not extended and no new move can start." + winRe;
    }

    /// <summary>Review M2: on a resume the partition is hidden and we hold no lock of ours; lock the volume device still
    /// at its old offset ourselves and keep it through the copy, the pauses, the NTFS patch and the GPT commit (it is
    /// released with the lock in step 4 / 5). Null if there is no such device.</summary>
    private IDisposable? HoldOldOffset(Ctx ctx, Guid part, long oldStart, long length)
    {
        if (_b.PartitionStart(ctx.Disk, part) != oldStart || (_b.GetPartitionAttributes(ctx.Disk, part) & GptHidden) == 0) return null;
        return _b.LockOldOffset(ctx.Disk, oldStart, length);
    }

    private void MoveSource(Ctx ctx)
    {
        ctx.SourceLock ??= HoldOldOffset(ctx, ctx.State.SourceGuid, ctx.State.SourceStart, ctx.State.SourceShrunkSize);
        ctx.Progress.Report(new RunProgress(MoveStep.MoveSource, 0, ctx.State.SourceShrunkSize, "Moving the source partition"));
        RunMove(ctx, 0, MoveStep.MoveSource);
    }

    /// <summary>Copy with pause: the progress callback returns shouldPause(); a stopped move waits (polling) and runs again.
    /// Our own lock on the partition (fresh run, or a resume that found it visible) is handed to DiskMove.dll; without
    /// one the DLL locks the volume device still at the old offset for the whole run.</summary>
    private void RunMove(Ctx ctx, int index, MoveStep step)
    {
        while (true)
        {
            var held = index == 0 ? ctx.SourceLock : ctx.RecoveryLock;
            bool complete = _b.RunMove(ctx.Disk, _journal, index, (done, total) =>
            {
                ctx.Progress.Report(new RunProgress(step, done, total, "Moving data"));
                return ctx.ShouldPause();
            }, held);
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
            if (start == s.SourceStart) ctx.SourceLock ??= HoldOldOffset(ctx, s.SourceGuid, s.SourceStart, s.SourceShrunkSize);
            // Raw writes are only allowed while the volume is hidden/dismounted: patch before the attributes come back.
            // Always after step 3, so after the core wrote the saved boot sectors to the destination (journal v2).
            _b.PatchNtfsHiddenSectors(ctx.Disk, s.SourceNewStart, s.SourceShrunkSize, s.SectorSize);
            _b.SetPartitionStart(ctx.Disk, s.SourceGuid, s.SourceNewStart);
            _b.SetPartitionAttributes(ctx.Disk, s.SourceGuid, s.SourceGptAttributes);   // still locked in a fresh run
        }
        ctx.SourceLock?.Dispose();
        ctx.SourceLock = null;
        if (s.SourceLetter == '\0') return;
        if (AssignLetterOrContinue(ctx) is char letter) NoteLetter(ctx, MoveStep.CommitSource, letter);
        else ctx.FinalLetter = null;   // step 7 warns about it; steps 5-7 do not need the letter
    }

    /// <summary>
    /// Review I1: a missing drive letter never stops the move. After a kill right after the unhide, the volume can exist
    /// without the mount manager ever registering it (error 87), and nothing re-triggers the arrival. In order:
    /// (1) the normal assignment (waits ~20 s); (2) an arrival nudge to the mount manager, which writes nothing to the
    /// disk; (3) only if the volume is still provably unregistered (so it has no letter or open handle to lose), ONE
    /// hidden -> original attribute toggle, with no lock held; (4) otherwise continue without the letter.
    /// </summary>
    private char? AssignLetterOrContinue(Ctx ctx)
    {
        var s = ctx.State;
        char? Try()
        {
            try
            {
                return _b.AssignLetter(s.SourceVolumePath!, s.SourceLetter);
            }
            catch (DiskMoveException)
            {
                return null;
            }
        }
        if (Try() is char first) return first;
        ctx.Progress.Report(new RunProgress(MoveStep.CommitSource, 0, 0, "The drive letter is not back yet; asking Windows to register the volume"));
        if (_b.NudgeVolumeArrival(ctx.Disk, s.SourceNewStart, s.SourceShrunkSize) && Try() is char nudged) return nudged;
        if (!_b.VolumeRegistered(s.SourceVolumePath!))
        {
            _b.SetPartitionAttributes(ctx.Disk, s.SourceGuid, s.SourceGptAttributes | HideBits);
            _b.SetPartitionAttributes(ctx.Disk, s.SourceGuid, s.SourceGptAttributes);
            if (Try() is char toggled) return toggled;
        }
        ctx.Progress.Report(new RunProgress(MoveStep.CommitSource, 0, 0,
            "The drive letter could not be restored now; continuing (the data is moved and safe)"));
        return null;
    }

    /// <summary>Records the source's final letter; a different one than before is a warning, never a stop.</summary>
    private static void NoteLetter(Ctx ctx, MoveStep step, char letter)
    {
        ctx.FinalLetter = letter;
        char original = ctx.State.SourceLetter;
        if (original != '\0' && char.ToUpperInvariant(letter) != char.ToUpperInvariant(original))
            ctx.Warn(step, $"{original}: was taken by another drive, so your data drive is now {letter}:.");
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
                else
                {
                    ctx.RecoveryLock ??= HoldOldOffset(ctx, rg, s.RecoveryStart, s.RecoverySize);   // review M2
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
            // The move is committed and winre.wim is safe on C: (reagentc /disable put it there). Never re-create the
            // partition automatically: tell the user and finish steps 6-7.
            ctx.Warn(MoveStep.MoveRecovery, WinReWarning);
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

    /// <summary>
    /// Step 7: read-only chkdsk of the source, WinRE as before, sizes and start as planned. Review I2: nothing found here
    /// can mean that data was not moved correctly (every chunk was read back and compared before it was recorded, and
    /// steps 4-6 already checked the starts and sizes they set), so each problem becomes a result warning, the journal
    /// is deleted and the move completes. The layout backups stay for 30 days.
    /// </summary>
    private void Verify(Ctx ctx)
    {
        var s = ctx.State;
        ctx.Progress.Report(new RunProgress(MoveStep.Verify, 0, 0, "Verifying the result"));
        long tSize = _b.PartitionSize(ctx.Disk, s.TargetGuid), tWant = s.TargetOriginalSize + s.Amount;
        if (tSize != tWant)
            ctx.Warn(MoveStep.Verify, $"The Windows partition is {tSize} bytes, not the planned {tWant}. Check it in Disk Management.");
        long srcSize = _b.PartitionSize(ctx.Disk, s.SourceGuid), srcStart = _b.PartitionStart(ctx.Disk, s.SourceGuid);
        string src = s.SourceLetter != '\0' ? $"{s.SourceLetter}:" : "The data partition";
        if (srcSize != s.SourceShrunkSize || srcStart != s.SourceNewStart)
            ctx.Warn(MoveStep.Verify, $"{src} is at {srcStart} (+{srcSize} bytes), not at the planned {s.SourceNewStart} " +
                                      $"(+{s.SourceShrunkSize}). Check it in Disk Management.");
        if (ctx.ManageWinRe && _b.WinReEnabled() != s.WinReWasEnabled)
            ctx.Warn(MoveStep.Verify, s.WinReWasEnabled
                ? WinReWarning
                : "Windows Recovery is turned on now but was off before the move. Run `reagentc /disable` as administrator if you want it off.");
        if (s.SourceLetter != '\0')
        {
            if (_b.GetLetter(s.SourceVolumePath!) is not char letter)
            {
                ctx.Warn(MoveStep.Verify, $"{s.SourceLetter}: has no drive letter after the move. Its data is intact; give it a " +
                                          "letter in Disk Management, then run a disk check on it.");
            }
            else
            {
                NoteLetter(ctx, MoveStep.Verify, letter);
                int rc = _b.Chkdsk(letter);
                if (rc != 0)
                    ctx.Warn(MoveStep.Verify, $"chkdsk reported problems on {letter}: (exit code {rc}). Run `chkdsk {letter}: /f` as administrator.");
            }
        }
    }

    private void Finish(Ctx ctx)
    {
        _b.DeleteJournal(_journal);
        CleanupBackups();
        ctx.Progress.Report(new RunProgress(MoveStep.Done, 0, 0, "Done"));
    }

    // ================================================================== layout backups

    /// <summary>Creates the data folder if missing. With a guard it was already secured (and checked) at the start of
    /// the run or resume, so nothing is re-applied here and an undo cannot fail half way on an ACL write (T12 N1); a
    /// folder that has vanished since is created and secured again through the guard (throws before any write).</summary>
    private void EnsureDataDir()
    {
        if (Guard is not { } guard)
        {
            Directory.CreateDirectory(DataDir);
            return;
        }
        if (!Directory.Exists(DataDir)) guard.PrepareTrusted(DataDir, [_journal, PendingPath]);
    }

    /// <summary>The data folder's ACL: see <see cref="DataFolderSecurity.CreateDataFolderSecurity"/>.</summary>
    public static DirectorySecurity CreateDataFolderSecurity() => DataFolderSecurity.CreateDataFolderSecurity();

    /// <summary>Sets <see cref="CreateDataFolderSecurity"/> (DACL only) on an existing folder.</summary>
    public static void ApplyDataFolderSecurity(string dataDir) =>
        new DirectoryInfo(dataDir).SetAccessControl(CreateDataFolderSecurity());

    /// <summary>Atomic: write a temp file, flush it to disk, then rename it over the marker.</summary>
    private void WritePending(PendingMove pending)
    {
        EnsureDataDir();
        string tmp = PendingPath + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(pending.ToJsonBytes());
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, PendingPath, overwrite: true);
    }

    private void DeletePending() => File.Delete(PendingPath);

    private PendingMove PendingFor(RestoreState s, bool undoing) => new(s.DiskGuid, s.SourceGuid, s.SourceLetter,
        s.SourceOriginalSize, s.TargetGuid, s.Amount, Now(), undoing);

    /// <summary>The marker, or null if there is none.</summary>
    private PendingMove? ReadPending()
    {
        if (!File.Exists(PendingPath)) return null;
        try
        {
            return PendingMove.FromJsonBytes(File.ReadAllBytes(PendingPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new DiskMoveException($"The pending move marker {PendingPath} cannot be read: {ex.Message}", ex);
        }
    }

    /// <summary>Review M7: the saved boot sectors are also kept next to the layout backups (30 days), so a lost journal
    /// still leaves a manual way to put them back.</summary>
    private void SaveBootBackup(RestoreState s, (byte[] First, byte[] Backup) source, (byte[] First, byte[] Backup)? recovery)
    {
        EnsureDataDir();
        object Part(Guid g, long start, long length, (byte[] First, byte[] Backup) b) => new
        {
            PartitionGuid = g, Start = start, Length = length, s.SectorSize,
            FirstSector = Convert.ToBase64String(b.First), BackupBootSector = Convert.ToBase64String(b.Backup),
        };
        var parts = new List<object> { Part(s.SourceGuid, s.SourceStart, s.SourceShrunkSize, source) };
        if (recovery is { } r && s.RecoveryGuid is Guid rg) parts.Add(Part(rg, s.RecoveryStart, s.RecoverySize, r));
        string stamp = Now().ToString(StampFormat, CultureInfo.InvariantCulture);
        File.WriteAllBytes(Path.Combine(DataDir, $"boot-{stamp}.json"), JsonSerializer.SerializeToUtf8Bytes(
            new { s.DiskGuid, Partitions = parts }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void SaveBackups(DiskSnapshot disk, int diskNo)
    {
        EnsureDataDir();
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
            foreach (var f in Directory.EnumerateFiles(DataDir, "layout-*.json").Concat(Directory.EnumerateFiles(DataDir, "gpt-*.bin"))
                         .Concat(Directory.EnumerateFiles(DataDir, "boot-*.json")))
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
