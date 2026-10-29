using WinOptimizer.Services.DiskSpace;
using WinOptimizer.Tests.Fakes;
using Xunit;

namespace WinOptimizer.Tests;

public sealed class DiskMoveRunnerTests : IDisposable
{
    const long X = 4 * FakeDiskMoveBackend.GiB;

    readonly string _dir = Path.Combine(Path.GetTempPath(), "wo-dmr-" + Guid.NewGuid().ToString("N"));
    string Journal => Path.Combine(_dir, "journal.bin");
    string Pending => Path.Combine(_dir, DiskMoveRunner.PendingFileName);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    sealed class SyncProgress : IProgress<RunProgress>
    {
        public readonly List<RunProgress> Items = [];
        public void Report(RunProgress value) { lock (Items) Items.Add(value); }
    }

    /// <summary>Original layout of the fake, captured before a run.</summary>
    sealed record Before(long CStart, long CSize, long RStart, long RSize, ulong RAttrs, long DStart, long DSize,
        ulong DAttrs, char DLetter, bool? WinRe);

    static Before Capture(FakeDiskMoveBackend f) => new(f.C.Start, f.C.Size, f.R?.Start ?? 0, f.R?.Size ?? 0,
        f.R?.Attrs ?? 0, f.D.Start, f.D.Size, f.D.Attrs, f.D.Letter, f.WinRe);

    DiskMoveRunner Runner(FakeDiskMoveBackend f, bool manageWinRe = true) =>
        new(f, Journal, manageWinRe) { PausePollInterval = TimeSpan.FromMilliseconds(1) };

    static MovePlan Plan(FakeDiskMoveBackend f) => f.R is null
        ? new MovePlan(X, [f.Snap(f.D)], false)
        : new MovePlan(X, [f.Snap(f.D), f.Snap(f.R)], true);

    Task<DiskMoveResult> Run(FakeDiskMoveBackend f, DiskMoveRunner? r = null, Func<IReadOnlyList<CheckResult>>? recheck = null,
        Func<bool>? pause = null, CancellationToken ct = default, IProgress<RunProgress>? progress = null)
    {
        var snap = f.Snapshot();
        return (r ?? Runner(f)).RunAsync(snap, f.Snap(f.C), f.Snap(f.D), Plan(f), recheck ?? (() => []),
            progress ?? new SyncProgress(), pause ?? (() => false), ct);
    }

    /// <summary>The disk after a complete move: C grew by X, D and Recovery moved right by X with data, NTFS
    /// HiddenSectors, exact attributes and letter intact; WinRE as before; no journal and no lock left.</summary>
    void AssertMoved(FakeDiskMoveBackend f, Before b)
    {
        Assert.Equal(b.CStart, f.C.Start);
        Assert.Equal(b.CSize + X, f.C.Size);
        Assert.Equal(b.DStart + X, f.D.Start);
        Assert.Equal(b.DSize - X, f.D.Size);
        Assert.Equal(f.D.Start, f.D.DataStart);
        Assert.Equal(f.D.Start / FakeDiskMoveBackend.Sector, f.D.HiddenSectors);
        Assert.Equal(b.DAttrs, f.D.Attrs);
        Assert.Equal(b.DLetter, f.D.Letter);
        if (f.R is not null)
        {
            Assert.Equal(b.RStart + X, f.R.Start);
            Assert.Equal(b.RSize, f.R.Size);
            Assert.Equal(f.R.Start, f.R.DataStart);
            Assert.Equal(f.R.Start / FakeDiskMoveBackend.Sector, f.R.HiddenSectors);
            Assert.Equal(b.RAttrs, f.R.Attrs);
            Assert.Equal(f.C.Start + f.C.Size, f.R.Start);   // Recovery right after C
        }
        Assert.Equal(b.WinRe, f.WinRe);
        Assert.False(DiskMoveRunner.HasUnfinishedJournal(Journal));
        Assert.False(File.Exists(Pending), "pending marker left");
        Assert.False(DiskMoveRunner.HasUnfinishedMove(_dir));
        Assert.All(f.Parts, p => Assert.False(p.Locked, $"{p.Name} still locked"));
    }

    /// <summary>An interrupted attempt that never got a journal was undone: D back to its original size.</summary>
    void AssertUndone(FakeDiskMoveBackend f, Before b, DiskMoveResult r)
    {
        Assert.Equal(DiskMoveOutcome.Undone, r.Outcome);
        Assert.Equal(b.DStart, f.D.Start);
        Assert.Equal(b.DSize, f.D.Size);
        Assert.Equal(f.D.Start, f.D.DataStart);
        Assert.Equal(b.DAttrs, f.D.Attrs);
        Assert.Equal(b.DLetter, f.D.Letter);
        Assert.Equal(b.CSize, f.C.Size);
        Assert.Equal(b.RStart, f.R.Start);
        Assert.False(DiskMoveRunner.HasUnfinishedMove(_dir));
        Assert.Contains("D: was restored to its original size", Assert.Single(r.Warnings));
    }

    /// <summary>Nothing but (maybe) the shrink of D happened: D visible with its letter at its start.</summary>
    static void AssertNotMoved(FakeDiskMoveBackend f, Before b)
    {
        Assert.Equal(b.DStart, f.D.Start);
        Assert.True(f.D.Size == b.DSize || f.D.Size == b.DSize - X, $"D size {f.D.Size}");
        Assert.Equal(f.D.Start, f.D.DataStart);
        Assert.Equal(b.DAttrs, f.D.Attrs);
        Assert.Equal(b.DLetter, f.D.Letter);
        Assert.Equal(b.CSize, f.C.Size);
        if (f.R is not null) Assert.Equal(b.RStart, f.R.Start);
    }

    static void AssertInOrder(IReadOnlyList<string> calls, params string[] expected)
    {
        int at = -1;
        foreach (var e in expected)
        {
            int i = -1;
            for (int k = at + 1; k < calls.Count; k++)
                if (calls[k].StartsWith(e, StringComparison.Ordinal)) { i = k; break; }
            Assert.True(i >= 0, $"'{e}' not found after index {at} in:\n{string.Join("\n", calls)}");
            at = i;
        }
    }

    // ------------------------------------------------------------------ tests

    [Fact]
    public async Task Run_CallsStepsInOrder()
    {
        var f = new FakeDiskMoveBackend();
        var b = Capture(f);
        var progress = new SyncProgress();
        await Run(f, progress: progress);

        AssertMoved(f, b);
        AssertInOrder(f.Calls,
            "ReadRawGpt",                                  // 0
            "Resize(D,",                                   // 1
            "Lock(D)", "Verify(D)", "CreateJournal",       // 2: journal BEFORE the hide
            "SetAttributes(D,hidden", "Verify(D)", "RemoveLetter(D,D)", "Dismount(D)",
            "Step(MoveSource)", "RunMove(0)", "Chunk(0,3)", // 3
            "Step(CommitSource)", "Patch(D)", "SetStart(D,", "SetAttributes(D,visible", "Unlock(D)", "AssignLetter(D,D)", // 4
            "Step(MoveRecovery)", "SetWinRe(False)", "Lock(R)", "Verify(R)", "SetAttributes(R,hidden", "Verify(R)",
            "Dismount(R)", "RunMove(1)", "Chunk(1,3)", "Patch(R)", "SetStart(R,", "SetAttributes(R,visible", "Unlock(R)", "SetWinRe(True)", // 5
            "Step(ExtendTarget)", "Resize(C,",            // 6
            "Step(Verify)", "Chkdsk(D)", "Step(Done)", "DeleteJournal"); // 7
        Assert.True(f.Calls.IndexOf("CreateJournal") < f.Calls.FindIndex(c => c.StartsWith("SetAttributes(D,hidden")));
        Assert.Equal(new[] { 4, 4 }, new[] { f.ChunkCopies[0], f.ChunkCopies[1] });
        var steps = progress.Items.Select(p => p.Step).Distinct().ToList();
        Assert.Equal(Enum.GetValues<MoveStep>().Where(s => s != MoveStep.Done), steps.Where(s => s != MoveStep.Done));
        Assert.Contains(progress.Items, p => p.Step == MoveStep.MoveSource && p.BytesTotal > 0 && p.BytesDone == p.BytesTotal);
    }

    /// <summary>Crash right after step k has been completed (the journal says step k+1).</summary>
    [Theory]
    [InlineData(MoveStep.LockSource)]
    [InlineData(MoveStep.MoveSource)]
    [InlineData(MoveStep.CommitSource)]
    [InlineData(MoveStep.MoveRecovery)]
    [InlineData(MoveStep.ExtendTarget)]
    [InlineData(MoveStep.Verify)]
    public async Task Run_CrashAfterEachStep_ResumeCompletes(MoveStep k)
    {
        var f = new FakeDiskMoveBackend();
        var b = Capture(f);
        string crashAfter = $"Step({k + 1})";
        f.OnCall = c => { if (c == crashAfter) f.CrashAtCall = f.CallCount; };
        await Assert.ThrowsAnyAsync<Exception>(() => Run(f));
        Assert.True(f.Dead);
        Assert.True(DiskMoveRunner.HasUnfinishedJournal(Journal));

        f.Revive();
        int callsBefore = f.Calls.Count;
        await Runner(f).ResumeAsync(new SyncProgress(), () => false);

        AssertMoved(f, b);
        var resumed = f.Calls.Skip(callsBefore).ToList();
        // Nothing done twice except the idempotent steps: one shrink, one journal, every chunk copied exactly once.
        Assert.Single(f.Calls, c => c.StartsWith("Resize(D,"));
        Assert.Single(f.Calls, c => c.StartsWith("Resize(C,"));
        Assert.Single(f.Calls, c => c == "CreateJournal");
        Assert.Equal(4, f.ChunkCopies[0]);
        Assert.Equal(4, f.ChunkCopies[1]);
        Assert.DoesNotContain(resumed, c => c.StartsWith($"Step({k + 1})"));   // the recorded step is not re-recorded
    }

    /// <summary>Crash after every single state-changing call of a clean run, then resume (if there is a journal).</summary>
    /// <summary>Crash after every single state-changing call of a clean run, then resume (if there is a journal);
    /// the resume itself is also crashed after each of its calls and resumed again.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]   // Windows does not give the letter back by itself after the unhide
    public async Task Run_CrashAtEveryCall_ResumeCompletesOrNothingMoved(bool windowsRestoresLetter)
    {
        var clean = new FakeDiskMoveBackend { WindowsRestoresLetter = windowsRestoresLetter };
        await Run(clean);
        int total = clean.CallCount;
        Assert.True(total > 30, $"only {total} calls");
        if (File.Exists(Journal)) File.Delete(Journal);

        async Task<(FakeDiskMoveBackend f, Before b)> CrashedAt(int n)
        {
            var f = new FakeDiskMoveBackend { CrashAtCall = n, WindowsRestoresLetter = windowsRestoresLetter };
            var b = Capture(f);
            await Assert.ThrowsAnyAsync<Exception>(() => Run(f));
            Assert.True(f.Dead, $"call {n}: no crash");
            f.Revive();
            return (f, b);
        }

        int resumed = 0, untouched = 0, finished = 0, undone = 0;
        for (int n = 1; n <= total; n++)
        {
            var (f, b) = await CrashedAt(n);
            if (!DiskMoveRunner.HasUnfinishedJournal(Journal) && File.Exists(Pending))
            {
                undone++;   // crashed in step 1 or in step 2 before the journal existed
                AssertUndone(f, b, await Runner(f).ResumeAsync(new SyncProgress(), () => false));
            }
            else if (DiskMoveRunner.HasUnfinishedJournal(Journal))
            {
                resumed++;
                await Runner(f).ResumeAsync(new SyncProgress(), () => false);
                AssertMoved(f, b);
                Assert.Equal(4, f.ChunkCopies[0]);
                Assert.Equal(4, f.ChunkCopies[1]);

                // Second crash: during the resume, after each of its calls, then resume again.
                for (int m = 1; windowsRestoresLetter; m++)
                {
                    var (g, gb) = await CrashedAt(n);
                    g.CrashAtCall = g.CallCount + m;
                    try
                    {
                        await Runner(g).ResumeAsync(new SyncProgress(), () => false);
                    }
                    catch (SimulatedCrashException) { }
                    catch (AggregateException) when (g.Dead) { }
                    if (!g.Dead)
                    {
                        AssertMoved(g, gb);
                        break;
                    }
                    g.Revive();
                    await Runner(g).ResumeAsync(new SyncProgress(), () => false);
                    AssertMoved(g, gb);
                    Assert.Equal(4, g.ChunkCopies[0]);
                    Assert.Equal(4, g.ChunkCopies[1]);
                }
            }
            else if (f.Calls.Contains("DeleteJournal"))
            {
                finished++;
                AssertMoved(f, b);
            }
            else
            {
                untouched++;
                AssertNotMoved(f, b);
            }
        }
        Assert.True(resumed > 20 && undone == 2 && untouched == 0 && finished == 1,
            $"resumed {resumed}, undone {undone}, untouched {untouched}, finished {finished}");
    }

    [Fact]
    public async Task Resume_FindsDiskByGuidWhenNumberChanged()
    {
        var f = new FakeDiskMoveBackend { DiskNumber = 0 };
        var b = Capture(f);
        f.OnCall = c => { if (c == "Chunk(0,1)") f.CrashAtCall = f.CallCount; };
        await Assert.ThrowsAnyAsync<Exception>(() => Run(f));
        f.Revive();
        f.DiskNumber = 2;   // e.g. a USB disk arrived before the reboot
        await Runner(f).ResumeAsync(new SyncProgress(), () => false);
        AssertMoved(f, b);   // every disk call checks the number: a call on disk 0 would have thrown
    }

    [Fact]
    public async Task Cancel_DuringLock_RestoresSource()
    {
        var f = new FakeDiskMoveBackend();
        var b = Capture(f);
        using var cts = new CancellationTokenSource();
        f.OnCall = c => { if (c == "Dismount(D)") cts.Cancel(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(f, ct: cts.Token));

        Assert.Equal(b.DAttrs, f.D.Attrs);
        Assert.Equal(b.DLetter, f.D.Letter);
        Assert.Equal(b.DSize, f.D.Size);
        Assert.Equal(b.DStart, f.D.Start);
        Assert.False(f.D.Locked);
        Assert.False(DiskMoveRunner.HasUnfinishedJournal(Journal));
        Assert.DoesNotContain(f.Calls, c => c.StartsWith("RunMove") || c.StartsWith("Step("));
        AssertInOrder(f.Calls, "SetAttributes(D,visible", "Unlock(D)", "AssignLetter(D,D)", "DeleteJournal", $"Resize(D,{b.DSize})");
        Assert.False(DiskMoveRunner.HasUnfinishedMove(_dir));
    }

    [Fact]
    public async Task Cancel_BeforeStart_ChangesNothing()
    {
        var f = new FakeDiskMoveBackend();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(f, ct: cts.Token));
        Assert.Equal(0, f.CallCount);
    }

    [Fact]
    public async Task Cancel_AfterMoveStarted_IsIgnored()
    {
        var f = new FakeDiskMoveBackend();
        var b = Capture(f);
        using var cts = new CancellationTokenSource();
        f.OnCall = c => { if (c == "Chunk(0,0)") cts.Cancel(); };
        await Run(f, ct: cts.Token);
        AssertMoved(f, b);
    }

    [Fact]
    public async Task FailureInStep2_UndoesLikeCancel()
    {
        var f = new FakeDiskMoveBackend();
        var b = Capture(f);
        f.OnCall = c => { if (c == "RemoveLetter(D,D)") throw new DiskMoveException("boom"); };
        var ex = await Assert.ThrowsAsync<DiskMoveException>(() => Run(f));
        Assert.Equal("boom", ex.Message);
        f.OnCall = null;
        Assert.Equal(b.DAttrs, f.D.Attrs);
        Assert.Equal(b.DSize, f.D.Size);
        Assert.Equal(b.DLetter, f.D.Letter);
        Assert.False(DiskMoveRunner.HasUnfinishedJournal(Journal));
    }

    [Fact]
    public async Task WinRe_EnableFails_WarnsAndCompletes()
    {
        var f = new FakeDiskMoveBackend { EnableFailures = 1 };
        var b = Capture(f);
        var progress = new SyncProgress();
        var r = await Run(f, progress: progress);
        AssertInOrder(f.Calls, "SetWinRe(False)", "RunMove(1)", "SetWinRe(True) failed", "Step(ExtendTarget)", "Resize(C,",
            "Step(Verify)", "Step(Done)", "DeleteJournal");
        Assert.False(f.WinRe);   // left for the user: winre.wim is on C:
        Assert.Equal(DiskMoveOutcome.Completed, r.Outcome);
        Assert.Equal([DiskMoveRunner.WinReWarning], r.Warnings);   // reported once, also by step 7
        Assert.Contains(progress.Items, p => p.Message.Contains("reagentc /enable"));
        AssertMoved(f, b with { WinRe = false });
    }

    [Fact]
    public async Task WinRe_EnableFailsAfterCrash_ResumeWarnsAndCompletes()
    {
        var f = new FakeDiskMoveBackend { EnableFailures = 5 };
        var b = Capture(f);
        f.OnCall = c => { if (c == "Step(ExtendTarget)") f.CrashAtCall = f.CallCount; };
        await Assert.ThrowsAnyAsync<Exception>(() => Run(f));
        f.Revive();
        var r = await Runner(f).ResumeAsync(new SyncProgress(), () => false);
        Assert.Equal([DiskMoveRunner.WinReWarning], r.Warnings);   // found again by step 7 alone
        AssertMoved(f, b with { WinRe = false });
    }

    [Theory]
    [InlineData(true)]    // Windows auto-assigns another letter after the unhide: it is kept
    [InlineData(false)]   // no letter at all: the first free one from D is assigned
    public async Task Letter_TakenByAnotherVolume_UsesAnotherLetterAndWarns(bool windowsRestoresLetter)
    {
        var f = new FakeDiskMoveBackend { WindowsRestoresLetter = windowsRestoresLetter };
        var b = Capture(f);
        f.OnCall = c => { if (c == "Dismount(D)") f.ForeignLetters.Add('D'); };   // a USB drive takes D: during the move
        f.ForeignLetters.Add('E');
        var r = await Run(f);
        Assert.Equal('F', f.D.Letter);
        Assert.Equal('F', r.FinalSourceLetter);
        Assert.Equal(["D: was taken by another drive, so your data drive is now F:."], r.Warnings);
        Assert.Contains("Chkdsk(D)", f.Calls);   // step 7 checked the volume under its new letter
        AssertMoved(f, b with { DLetter = 'F' });
    }

    [Fact]
    public async Task Letter_Kept_ReportedInResult()
    {
        var f = new FakeDiskMoveBackend();
        var r = await Run(f);
        Assert.Equal('D', r.FinalSourceLetter);
        Assert.Empty(r.Warnings);
        Assert.Equal(DiskMoveOutcome.Completed, r.Outcome);
    }

    [Theory]
    [InlineData("Resize(D,")]   // crash right after step 1
    [InlineData("Lock(D)")]     // crash in step 2, before CreateJournal
    public async Task CrashBeforeJournal_ResumeUndoesShrink(string crashAfter)
    {
        var f = new FakeDiskMoveBackend();
        var b = Capture(f);
        f.OnCall = c => { if (c.StartsWith(crashAfter)) f.CrashAtCall = f.CallCount; };
        await Assert.ThrowsAnyAsync<Exception>(() => Run(f));
        f.Revive();
        f.OnCall = null;
        Assert.False(DiskMoveRunner.HasUnfinishedJournal(Journal));
        Assert.True(File.Exists(Pending));
        Assert.True(DiskMoveRunner.HasUnfinishedMove(_dir));
        Assert.Equal(b.DSize - X, f.D.Size);

        var progress = new SyncProgress();
        var r = await Runner(f).ResumeAsync(progress, () => false);
        AssertUndone(f, b, r);
        Assert.Contains(progress.Items, p => p.Message.Contains("restored to its original size"));
        Assert.DoesNotContain(f.Calls, c => c.StartsWith("CreateJournal") || c.StartsWith("SetAttributes"));
    }

    [Fact]
    public async Task CrashAtCreateJournal_ResumeContinuesAndDropsPending()
    {
        var f = new FakeDiskMoveBackend();
        var b = Capture(f);
        f.OnCall = c => { if (c == "CreateJournal") f.CrashAtCall = f.CallCount; };
        await Assert.ThrowsAnyAsync<Exception>(() => Run(f));
        f.Revive();
        Assert.True(DiskMoveRunner.HasUnfinishedJournal(Journal));
        var r = await Runner(f).ResumeAsync(new SyncProgress(), () => false);
        Assert.Equal(DiskMoveOutcome.Completed, r.Outcome);
        AssertMoved(f, b);
    }

    [Fact]
    public async Task Pending_GoneOnceJournalExists()
    {
        var f = new FakeDiskMoveBackend();
        bool pendingBeforeShrink = false, pendingAfterJournal = true;
        f.OnCall = c =>
        {
            if (c.StartsWith("Resize(D,")) pendingBeforeShrink = File.Exists(Pending);
            if (c == "SetAttributes(D,hidden,0xC000000000000000)") pendingAfterJournal = File.Exists(Pending);
        };
        await Run(f);
        Assert.True(pendingBeforeShrink, "the marker must exist before step 1");
        Assert.False(pendingAfterJournal, "the journal supersedes the marker");
    }

    /// <summary>A cancel at the dismount starts the undo; it crashes at a point inside the undo; the resume must
    /// finish the undo (never move forward), whatever the point.</summary>
    [Theory]
    [InlineData("SetAttributes(D,visible")]   // marker re-written (Undoing), nothing undone yet... then attrs back
    [InlineData("AssignLetter(D")]
    [InlineData("DeleteJournal")]
    [InlineData("during-resize")]
    [InlineData("Resize-back")]               // resized, marker not yet deleted
    public async Task CrashInsideUndo_ResumeFinishesUndo(string point)
    {
        var f = new FakeDiskMoveBackend();
        var b = Capture(f);
        using var cts = new CancellationTokenSource();
        bool pendingAtFirstUndoCall = false;
        f.OnCall = c =>
        {
            if (c == "Dismount(D)") cts.Cancel();
            if (c.StartsWith("SetAttributes(D,visible")) pendingAtFirstUndoCall = File.Exists(Pending);
            bool hit = point switch
            {
                "Resize-back" => c == $"Resize(D,{b.DSize})",
                "during-resize" => false,
                _ => c.StartsWith(point),
            };
            if (hit) f.CrashAtCall = f.CallCount;
        };
        if (point == "during-resize") f.CrashBeforeResizeTo = b.DSize;
        await Assert.ThrowsAnyAsync<Exception>(() => Run(f, ct: cts.Token));
        Assert.True(f.Dead, "no crash");
        Assert.True(pendingAtFirstUndoCall, "the marker must be back before the undo changes anything");
        f.Revive();
        Assert.True(File.Exists(Pending), "the marker must survive a crash inside the undo");
        Assert.True(DiskMoveRunner.HasUnfinishedMove(_dir));

        int before = f.Calls.Count;
        var r = await Runner(f).ResumeAsync(new SyncProgress(), () => false);
        var resumed = f.Calls.Skip(before).ToList();
        Assert.DoesNotContain(resumed, c => c.StartsWith("RunMove") || c.StartsWith("Step(") || c.Contains("hidden"));
        Assert.Equal(DiskMoveOutcome.Undone, r.Outcome);
        Assert.Equal(b.DSize, f.D.Size);
        Assert.Equal(b.DStart, f.D.Start);
        Assert.Equal(b.DAttrs, f.D.Attrs);
        Assert.Equal(b.DLetter, f.D.Letter);
        Assert.False(DiskMoveRunner.HasUnfinishedJournal(Journal));
        Assert.False(DiskMoveRunner.HasUnfinishedMove(_dir));
    }

    /// <summary>Every state-changing call of a cancelled run's undo, crashed and resumed.</summary>
    [Fact]
    public async Task CrashAtEveryUndoCall_ResumeFinishesUndo()
    {
        async Task<(FakeDiskMoveBackend, Before, CancellationTokenSource)> Start(int? crashAt)
        {
            var f = new FakeDiskMoveBackend();
            var b = Capture(f);
            var cts = new CancellationTokenSource();
            f.OnCall = c => { if (c == "Dismount(D)") cts.Cancel(); };
            f.CrashAtCall = crashAt;
            await Assert.ThrowsAnyAsync<Exception>(() => Run(f, ct: cts.Token));
            return (f, b, cts);
        }
        var (clean, _, c0) = await Start(null);
        c0.Dispose();
        int first = clean.Calls.FindIndex(c => c.StartsWith("SetAttributes(D,visible"));
        int undoStart = clean.Calls.Take(first).Count(c => !c.StartsWith("Verify") && !c.StartsWith("Unlock") && c != "ReadRawGpt") + 1;
        int total = clean.CallCount;
        Assert.True(total - undoStart >= 3, $"undo calls {undoStart}..{total}");
        for (int n = undoStart; n <= total; n++)
        {
            var (f, b, cts) = await Start(n);
            cts.Dispose();
            Assert.True(f.Dead, $"call {n}: no crash");
            f.Revive();
            var r = await Runner(f).ResumeAsync(new SyncProgress(), () => false);
            Assert.Equal(DiskMoveOutcome.Undone, r.Outcome);
            Assert.Equal(b.DSize, f.D.Size);
            Assert.Equal(b.DAttrs, f.D.Attrs);
            Assert.Equal(b.DLetter, f.D.Letter);
            Assert.False(DiskMoveRunner.HasUnfinishedMove(_dir), $"call {n}");
        }
    }

    [Fact]
    public async Task CrashInsideRecheckAbort_ResumeFinishesUndo()
    {
        var f = new FakeDiskMoveBackend();
        var b = Capture(f);
        f.CrashBeforeResizeTo = b.DSize;
        await Assert.ThrowsAnyAsync<Exception>(() => Run(f, recheck: () => [new CheckResult(CheckSeverity.Blocker, CheckCode.SourceInUse, "x")]));
        f.Revive();
        Assert.True(File.Exists(Pending));
        AssertUndone(f, b, await Runner(f).ResumeAsync(new SyncProgress(), () => false));
    }

    [Fact]
    public async Task TornJournalWithPending_ResumeUndoes()
    {
        var f = new FakeDiskMoveBackend();
        var b = Capture(f);
        f.OnCall = c => { if (c == "CreateJournal") f.CrashAtCall = f.CallCount; };
        await Assert.ThrowsAnyAsync<Exception>(() => Run(f));
        f.Revive();
        File.WriteAllText(Journal, "{ torn");   // the create was cut short
        AssertUndone(f, b, await Runner(f).ResumeAsync(new SyncProgress(), () => false));
        Assert.False(File.Exists(Journal));
    }

    [Fact]
    public async Task TornJournalWithPending_HiddenSource_StopsWithoutChanges()
    {
        var f = new FakeDiskMoveBackend();
        f.OnCall = c => { if (c.StartsWith("SetAttributes(D,hidden")) f.CrashAtCall = f.CallCount; };
        await Assert.ThrowsAnyAsync<Exception>(() => Run(f));
        f.Revive();
        File.WriteAllText(Journal, "{ torn");
        File.WriteAllBytes(Pending, new PendingMove(f.DiskGuid, f.D.Guid, 'D', f.D.Size + X, f.C.Guid, X, DateTime.Now).ToJsonBytes());
        int count = f.CallCount;
        await Assert.ThrowsAsync<DiskMoveStateMismatchException>(() => Runner(f).ResumeAsync(new SyncProgress(), () => false));
        Assert.Equal(count, f.CallCount);
        Assert.True(File.Exists(Journal));
        Assert.True(File.Exists(Pending));
    }

    [Fact]
    public async Task Run_RefusesWhenPendingExists()
    {
        var f = new FakeDiskMoveBackend();
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Pending, "{}");
        await Assert.ThrowsAsync<DiskMoveException>(() => Run(f));
        Assert.Equal(0, f.CallCount);
    }

    [Fact]
    public async Task WinRe_NotManaged_NeverCallsReagentc()
    {
        var f = new FakeDiskMoveBackend { WinRe = null };   // unknown: would abort a managed run
        var b = Capture(f);
        await Run(f, Runner(f, manageWinRe: false));
        AssertMoved(f, b);
        Assert.DoesNotContain(f.Calls, c => c.StartsWith("SetWinRe"));
    }

    [Fact]
    public async Task WinRe_DisabledBefore_StaysDisabled()
    {
        var f = new FakeDiskMoveBackend { WinRe = false };
        var b = Capture(f);
        await Run(f);
        AssertMoved(f, b);
        Assert.DoesNotContain(f.Calls, c => c == "SetWinRe(True)");
    }

    [Fact]
    public async Task Pause_WaitsAndContinues()
    {
        var f = new FakeDiskMoveBackend();
        var b = Capture(f);
        int calls = 0;
        await Run(f, pause: () => Interlocked.Increment(ref calls) <= 3);
        AssertMoved(f, b);
        Assert.True(f.Calls.Count(c => c == "RunMove(0)") >= 2, string.Join("\n", f.Calls));
        Assert.Equal(4, f.ChunkCopies[0]);
        Assert.True(calls > 3);
    }

    [Fact]
    public async Task Run_RecheckBeforeLock_AbortsAndUndoesShrink()
    {
        var f = new FakeDiskMoveBackend();
        var b = Capture(f);
        var fix = new CheckResult(CheckSeverity.FixFirst, CheckCode.SourceInUse, "in use");
        bool shrunkWhenRechecked = false;
        var ex = await Assert.ThrowsAsync<DiskMoveAbortedException>(() => Run(f, recheck: () =>
        {
            shrunkWhenRechecked = f.D.Size == b.DSize - X;
            return [new CheckResult(CheckSeverity.Warning, CheckCode.Backup, "backup"), fix];
        }));
        Assert.True(shrunkWhenRechecked, "recheck must run after step 1");
        Assert.False(DiskMoveRunner.HasUnfinishedMove(_dir));
        Assert.Contains(fix, ex.Results);
        Assert.Equal(b.DSize, f.D.Size);
        Assert.False(DiskMoveRunner.HasUnfinishedJournal(Journal));
        Assert.DoesNotContain(f.Calls, c => c.StartsWith("Lock("));
    }

    [Fact]
    public async Task Run_RecheckWarningsOnly_Continues()
    {
        var f = new FakeDiskMoveBackend();
        var b = Capture(f);
        await Run(f, recheck: () => [new CheckResult(CheckSeverity.Warning, CheckCode.Backup, "backup")]);
        AssertMoved(f, b);
    }

    [Fact]
    public async Task Resume_StopsWhenLayoutUnexpected()
    {
        var f = new FakeDiskMoveBackend();
        f.OnCall = c => { if (c == "Chunk(0,1)") f.CrashAtCall = f.CallCount; };
        await Assert.ThrowsAnyAsync<Exception>(() => Run(f));
        f.Revive();
        f.D.Start += FakeDiskMoveBackend.MiB;   // neither the old nor the new start
        f.D.DataStart = f.D.Start;
        int count = f.CallCount;
        await Assert.ThrowsAsync<DiskMoveStateMismatchException>(() => Runner(f).ResumeAsync(new SyncProgress(), () => false));
        Assert.Equal(count, f.CallCount);   // no state-changing call
        Assert.True(DiskMoveRunner.HasUnfinishedJournal(Journal));
    }

    [Fact]
    public async Task Resume_StopsWhenRecoveryUnexpected()
    {
        var f = new FakeDiskMoveBackend();
        f.OnCall = c => { if (c == "Step(MoveRecovery)") f.CrashAtCall = f.CallCount; };
        await Assert.ThrowsAnyAsync<Exception>(() => Run(f));
        f.Revive();
        f.R.Start -= FakeDiskMoveBackend.MiB;
        int count = f.CallCount;
        await Assert.ThrowsAsync<DiskMoveStateMismatchException>(() => Runner(f).ResumeAsync(new SyncProgress(), () => false));
        Assert.Equal(count, f.CallCount);
    }

    [Fact]
    public async Task Resume_StopsWhenSourceVisibleDuringCopy()
    {
        var f = new FakeDiskMoveBackend();
        f.OnCall = c => { if (c == "Chunk(0,1)") f.CrashAtCall = f.CallCount; };
        await Assert.ThrowsAnyAsync<Exception>(() => Run(f));
        f.Revive();
        f.D.Attrs &= ~FakeDiskMoveBackend.Hidden;   // someone un-hid the half-moved volume
        int count = f.CallCount;
        await Assert.ThrowsAsync<DiskMoveStateMismatchException>(() => Runner(f).ResumeAsync(new SyncProgress(), () => false));
        Assert.Equal(count, f.CallCount);
    }

    [Fact]
    public async Task Resume_HiddenSourceIsNotLocked()
    {
        var f = new FakeDiskMoveBackend();
        var b = Capture(f);
        f.OnCall = c => { if (c == "Chunk(0,1)") f.CrashAtCall = f.CallCount; };
        await Assert.ThrowsAnyAsync<Exception>(() => Run(f));
        f.Revive();
        int before = f.Calls.Count;
        await Runner(f).ResumeAsync(new SyncProgress(), () => false);
        AssertMoved(f, b);
        Assert.DoesNotContain(f.Calls.Skip(before), c => c == "Lock(D)");
    }

    [Fact]
    public async Task Resume_WithoutJournal_DoesNothing()
    {
        var f = new FakeDiskMoveBackend();
        var r = await Runner(f).ResumeAsync(new SyncProgress(), () => false);
        Assert.Equal(DiskMoveOutcome.NothingToDo, r.Outcome);
        Assert.Equal(0, f.CallCount);
    }

    [Fact]
    public async Task Run_RefusesWhenJournalExists()
    {
        var f = new FakeDiskMoveBackend();
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Journal, "unfinished");
        await Assert.ThrowsAsync<DiskMoveException>(() => Run(f));
        Assert.Equal(0, f.CallCount);
        Assert.Equal("unfinished", File.ReadAllText(Journal));
    }

    [Fact]
    public async Task Run_RefusesWhenLayoutChangedSinceAnalysis()
    {
        var f = new FakeDiskMoveBackend();
        var snap = f.Snapshot();
        f.D.Size -= FakeDiskMoveBackend.GiB;
        await Assert.ThrowsAsync<DiskMoveStateMismatchException>(() => Runner(f).RunAsync(snap,
            snap.Partitions.Single(p => p.Guid == f.C.Guid), snap.Partitions.Single(p => p.Guid == f.D.Guid),
            new MovePlan(X, [snap.Partitions.Single(p => p.Guid == f.D.Guid), snap.Partitions.Single(p => p.Guid == f.R.Guid)], true),
            () => [], new SyncProgress(), () => false, default));
        Assert.Equal(0, f.CallCount);
    }

    [Fact]
    public async Task Run_WithoutRecovery_SkipsRecoveryMove()
    {
        var f = new FakeDiskMoveBackend(withRecovery: false);
        var b = Capture(f);
        await Run(f);
        AssertMoved(f, b);
        Assert.DoesNotContain(f.Calls, c => c.StartsWith("RunMove(1)") || c.StartsWith("SetWinRe"));
        Assert.Equal(f.C.Start + f.C.Size, f.D.Start);
    }

    [Fact]
    public async Task Run_SavesLayoutAndRawGpt()
    {
        var f = new FakeDiskMoveBackend();
        await Run(f);
        var layout = Assert.Single(Directory.GetFiles(_dir, "layout-*.json"));
        var gpt = Assert.Single(Directory.GetFiles(_dir, "gpt-*.bin"));
        Assert.Matches(@"layout-\d{8}-\d{6}\.json$", layout);
        Assert.Equal(Path.GetFileName(layout)[7..22], Path.GetFileName(gpt)[4..19]);
        Assert.Equal((34 + 33) * FakeDiskMoveBackend.Sector, new FileInfo(gpt).Length);
        Assert.Contains(f.D.Guid.ToString(), File.ReadAllText(layout));
    }

    [Fact]
    public async Task Verify_DeletesBackupsOlderThan30Days()
    {
        var f = new FakeDiskMoveBackend();
        Directory.CreateDirectory(_dir);
        var now = new DateTime(2026, 10, 29, 12, 0, 0);
        string old1 = Path.Combine(_dir, "layout-20260901-080000.json"), old2 = Path.Combine(_dir, "gpt-20260901-080000.bin");
        string recent = Path.Combine(_dir, "layout-20261015-080000.json");
        foreach (var p in new[] { old1, old2, recent }) File.WriteAllText(p, "x");
        await Run(f, new DiskMoveRunner(f, Journal) { Now = () => now });
        Assert.False(File.Exists(old1));
        Assert.False(File.Exists(old2));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(Path.Combine(_dir, "layout-20261029-120000.json")));
    }

    [Fact]
    public async Task Verify_ChkdskFails_KeepsJournal()
    {
        var f = new FakeDiskMoveBackend { ChkdskResult = 3 };
        await Assert.ThrowsAsync<DiskMoveVerificationException>(() => Run(f));
        Assert.True(DiskMoveRunner.HasUnfinishedJournal(Journal));
    }

    [Fact]
    public void RestoreState_RoundTripsThroughJson()
    {
        var s = new RestoreState(Guid.NewGuid(), Guid.NewGuid(), 'D', 0x8000000000000001UL, Guid.NewGuid(), true,
            Guid.NewGuid(), X)
        {
            SourceVolumePath = @"\\?\Volume{1}\", SourceStart = 1, SourceOriginalSize = 2, TargetOriginalSize = 3,
            RecoveryStart = 4, RecoverySize = 5, RecoveryGptAttributes = 0xC000000000000001UL, RecoveryVolumePath = "r",
            SectorSize = 4096, ManageWinRe = true,
        };
        Assert.Equal(s, RestoreState.FromJsonBytes(s.ToJsonBytes()));
        Assert.Throws<DiskMoveException>(() => RestoreState.FromJsonBytes((s with { Version = 2 }).ToJsonBytes()));
    }

    [Fact]
    public void DefaultJournalPath_IsUnderProgramData()
    {
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "WinOptimizer", "DiskMove", "journal.bin"), DiskMoveRunner.DefaultJournalPath);
    }
}

public sealed class WindowsDiskMoveBackendTests
{
    [Theory]
    [InlineData("Windows Recovery Environment (Windows RE) and system reset configuration\r\nInformation:\r\n\r\n    Windows RE status:         Enabled\r\n    Windows RE location:       (GLOBALROOT path)\r\n", true)]
    [InlineData("    Windows RE status:         Disabled\r\n    Windows RE location:\r\n", false)]
    [InlineData("REAGENTC.EXE: Operation failed: 2\r\n", null)]
    [InlineData("    Windows RE-Status:         Aktiviert\r\n", null)]   // localized output: unknown, never guessed
    public void ParseWinReStatus_ReadsReagentcInfo(string output, bool? expected) =>
        Assert.Equal(expected, WindowsDiskMoveBackend.ParseWinReStatus(output));

    [Theory]
    [InlineData("<?xml version='1.0' encoding='utf-8'?>\r\n<WindowsRE version=\"2.0\">\r\n  <WinreBCD id=\"{a4d113f5-47d9-11f1-89b2-e91252689b75}\"/>\r\n  <InstallState state=\"1\"/>\r\n  <OsInstallAvailable state=\"0\"/>\r\n</WindowsRE>", true)]
    [InlineData("<WindowsRE version=\"2.0\"><InstallState state=\"0\"/><IsAutoRepairOn state=\"1\"/></WindowsRE>", false)]
    [InlineData("<WindowsRE version=\"2.0\"><IsAutoRepairOn state=\"1\"/></WindowsRE>", null)]   // missing element
    [InlineData("<WindowsRE version=\"2.0\"><InstallState/></WindowsRE>", null)]                  // missing attribute
    [InlineData("<WindowsRE version=\"2.0\"><InstallState state=\"2\"/></WindowsRE>", null)]     // unknown value
    [InlineData("<WindowsRE><InstallState state=\"1\"></WindowsRE", null)]                         // malformed
    [InlineData("", null)]
    public void ParseReAgentXml_ReadsInstallState(string xml, bool? expected) =>
        Assert.Equal(expected, WindowsDiskMoveBackend.ParseReAgentXml(xml));
}
