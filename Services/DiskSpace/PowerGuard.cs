using System.Runtime.InteropServices;

namespace WinOptimizer.Services.DiskSpace;

/// <summary>
/// Held while a disk space move runs (spec section 6, "Trong lúc chạy"): keeps Windows from sleeping, hibernating or
/// turning the display off (SetThreadExecutionState) and asks Windows to hold a shutdown/sign-out with a reason
/// (ShutdownBlockReasonCreate). <see cref="Dispose"/> removes both.
/// <para>SetThreadExecutionState is per thread: create and dispose the guard on the same (UI) thread.</para>
/// </summary>
public sealed class PowerGuard : IDisposable
{
    public const string ShutdownReason = "WinOptimizer is moving disk space — please wait.";
    /// <summary>On battery below this percentage the move pauses between two blocks.</summary>
    public const int LowBatteryPercent = 20;

    private const uint EsContinuous = 0x80000000, EsSystemRequired = 0x00000001, EsDisplayRequired = 0x00000002,
        EsAwayModeRequired = 0x00000040;

    private readonly IntPtr _hwnd;
    private readonly Func<BatteryInfo> _readBattery;
    private bool _executionState, _shutdownBlock, _disposed;

    /// <param name="hwnd">Window that owns the shutdown block reason; IntPtr.Zero = no shutdown block (tests).</param>
    /// <param name="readBattery">Battery reader; defaults to <see cref="SystemInfoService.GetBattery"/>.</param>
    public PowerGuard(IntPtr hwnd, Func<BatteryInfo>? readBattery = null)
    {
        _hwnd = hwnd;
        _readBattery = readBattery ?? SystemInfoService.GetBattery;
        _executionState = SetThreadExecutionState(EsContinuous | EsSystemRequired | EsDisplayRequired | EsAwayModeRequired) != 0;
        if (hwnd != IntPtr.Zero) _shutdownBlock = ShutdownBlockReasonCreate(hwnd, ShutdownReason);
    }

    /// <summary>Running on battery (a battery exists and the charger is not connected).</summary>
    public bool OnBattery => Read() is { HasBattery: true, OnAcPower: false };

    /// <summary>True while on battery with less than 20% left: the copy then pauses safely between two blocks and
    /// continues when the charger is plugged in. Thread-safe (called from the move thread).</summary>
    public bool ShouldPause() => Read() is { HasBattery: true, OnAcPower: false } b && b.Percent < LowBatteryPercent;

    private BatteryInfo? Read()
    {
        try { return _readBattery(); }
        catch (Exception ex) when (ex is InvalidOperationException or ExternalException or UnauthorizedAccessException)
        {
            return null;   // unknown power state: never pause because of it
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_executionState) SetThreadExecutionState(EsContinuous);
        if (_shutdownBlock) ShutdownBlockReasonDestroy(_hwnd);
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShutdownBlockReasonCreate(IntPtr hWnd, string reason);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShutdownBlockReasonDestroy(IntPtr hWnd);
}
