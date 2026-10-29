using System.IO;

namespace WinOptimizer.Services.DiskSpace;

/// <summary>
/// App-wide state of the disk space move: whether one is running right now (window close, Exit, "Restart as
/// administrator" and the sidebar are blocked meanwhile) and where its journal lives.
/// </summary>
public static class DiskMoveSession
{
    public const string BusyTitle = "Disk space move in progress";
    public const string BusyMessage =
        "WinOptimizer is moving disk space. It cannot be closed until the move has finished, because stopping now " +
        "would leave your partitions half moved. Please wait; the Disk Space page shows the progress.";

    private static volatile bool _running;

    /// <summary>True from "Start moving" / "Continue" until the run or resume has ended.</summary>
    public static bool IsRunning => _running;

    /// <summary>Raised on the thread that called <see cref="SetRunning"/> (the UI thread).</summary>
    public static event Action? RunningChanged;

    public static void SetRunning(bool running)
    {
        if (_running == running) return;
        _running = running;
        RunningChanged?.Invoke();
    }

    /// <summary>The journal of the move; its folder also holds the pending marker and the layout backups.</summary>
    public static string JournalPath { get; private set; } = DiskMoveRunner.DefaultJournalPath;

    public static string DataDir => Path.GetDirectoryName(JournalPath)!;

    /// <summary>A journal or pending marker exists: the Disk Space page must show the Resume screen.
    /// File.Exists only, so it also works without admin rights.</summary>
    public static bool HasUnfinishedMove()
    {
        try { return DiskMoveRunner.HasUnfinishedMove(DataDir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Windows RE is disabled/re-enabled around the Recovery move, except on a test target.</summary>
    public static bool ManageWinRe => TestTargetDisk is null;

    /// <summary>Debug builds only (--disk-move-test-target N): the disk the page uses as its target instead of the
    /// Windows disk. Never set in Release builds.</summary>
    public static int? TestTargetDisk { get; private set; }

#if DEBUG
    public const string TestTargetArg = "--disk-move-test-target";

    /// <summary>Debug only: use disk <paramref name="disk"/> (a VHDX; never a boot or system disk) as the target
    /// disk, with its own journal folder under %TEMP% so the real ProgramData folder is never touched.</summary>
    public static void UseTestTarget(int disk)
    {
        TestTargetDisk = disk;
        JournalPath = Path.Combine(Path.GetTempPath(), "WinOptimizer-DiskMoveTest", DiskMoveRunner.JournalFileName);
    }
#endif
}
