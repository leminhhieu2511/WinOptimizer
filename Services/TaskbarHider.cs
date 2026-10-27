using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace WinOptimizer.Services;

/// <summary>
/// Auto-hides Windows' own taskbar on chosen monitors only (Windows itself can only auto-hide on all of them).
///
/// Technique credit: Per-Monitor Taskbar by Niko Leinonen (MIT License),
/// https://github.com/nikoleinonen/per-monitor-taskbar. This is an independent C# implementation of the same idea:
/// the taskbar window is made invisible and click-through (WS_EX_LAYERED + WS_EX_TRANSPARENT, alpha 0) and shown
/// again while the mouse is over its strip. The strip stays reserved, so maximized windows don't cover it.
///
/// Safety: Explorer's taskbars never have those two styles themselves, so restoring means clearing them. A "dirty"
/// flag is saved while anything is hidden; the next start restores leftovers if the app was killed. The app also
/// restores on exit, crash and sign-out, and Reset puts everything back.
/// </summary>
public static class TaskbarHider
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20;
    private const uint LWA_ALPHA = 0x2;
    private const uint RDW_INVALIDATE = 0x1, RDW_ERASE = 0x4, RDW_ALLCHILDREN = 0x80, RDW_FRAME = 0x400;

    // 50 ms like the original: fast enough that the taskbar appears as soon as you reach it. Runs only while
    // at least one taskbar is hidden; each tick is one GetCursorPos plus a few rectangle checks.
    private static readonly DispatcherTimer Timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private static readonly Dictionary<IntPtr, bool> Hidden = new(); // taskbar window -> currently invisible
    private static DateTime _lastScan = DateTime.MinValue;
    private static List<(IntPtr Handle, IntPtr Monitor)> _taskbars = [];

    static TaskbarHider() => Timer.Tick += (_, _) => Tick();

    public static bool AnyActive => SettingsStore.Current.HiddenTaskbars.Count > 0;

    public static bool IsHidden(string device) => SettingsStore.Current.HiddenTaskbars.Contains(device);

    /// <summary>Whether Windows shows a taskbar on that display (secondary displays need "Show my taskbar on all displays").</summary>
    public static bool HasTaskbar(DisplayInfo display) => FindTaskbars().Any(t => t.Monitor == display.Handle);

    /// <summary>Call once at startup: fixes leftovers from a crash, then applies saved choices.</summary>
    public static void Initialize()
    {
        if (SettingsStore.Current.TaskbarHidingDirty) RestoreAll(); // the app was killed while hiding a taskbar
        Apply();
    }

    public static void Set(string device, bool hide)
    {
        var list = SettingsStore.Current.HiddenTaskbars;
        list.Remove(device);
        if (hide) list.Add(device);
        SettingsStore.Save();
        Apply();
    }

    /// <summary>Starts or stops managing taskbars to match the saved settings.</summary>
    public static void Apply()
    {
        if (AnyActive)
        {
            if (!SettingsStore.Current.TaskbarHidingDirty)
            {
                SettingsStore.Current.TaskbarHidingDirty = true;
                SettingsStore.Save();
            }
            _lastScan = DateTime.MinValue;
            Tick();
            Timer.Start();
        }
        else
        {
            RestoreAll();
        }
    }

    /// <summary>Shows every Windows taskbar again and stops managing them. Saved choices are kept.</summary>
    public static void RestoreAll()
    {
        Timer.Stop();
        foreach (var (handle, _) in FindTaskbars()) Show(handle);
        Hidden.Clear();
        if (SettingsStore.Current.TaskbarHidingDirty)
        {
            SettingsStore.Current.TaskbarHidingDirty = false;
            SettingsStore.Save();
        }
    }

    /// <summary>Reset: show all Windows taskbars and forget the per-display hide choices.</summary>
    public static void Reset()
    {
        SettingsStore.Current.HiddenTaskbars.Clear();
        SettingsStore.Save();
        RestoreAll();
    }

    private static void Tick()
    {
        // Explorer recreates its taskbar windows when it restarts, so look them up again every second.
        if (DateTime.UtcNow - _lastScan > TimeSpan.FromSeconds(1))
        {
            _lastScan = DateTime.UtcNow;
            _taskbars = FindTaskbars();
            foreach (var gone in Hidden.Keys.Except(_taskbars.Select(t => t.Handle)).ToList()) Hidden.Remove(gone);
        }

        var hiddenDevices = SettingsStore.Current.HiddenTaskbars;
        var managed = TaskbarService.GetDisplaysCached()
            .Where(d => hiddenDevices.Contains(d.DeviceName))
            .Select(d => d.Handle)
            .ToHashSet();
        GetCursorPos(out POINT cursor);

        foreach (var (handle, monitor) in _taskbars)
        {
            if (!managed.Contains(monitor))
            {
                if (Hidden.TryGetValue(handle, out bool wasHidden) && wasHidden) Show(handle);
                Hidden.Remove(handle);
                continue;
            }

            GetWindowRect(handle, out RECT r);
            bool mouseOnStrip = cursor.X >= r.Left && cursor.X < r.Right && cursor.Y >= r.Top && cursor.Y < r.Bottom;
            bool hidden = Hidden.TryGetValue(handle, out bool h) && h;
            if (mouseOnStrip && hidden) Show(handle);
            else if (!mouseOnStrip && !hidden) Hide(handle);
        }
    }

    private static void Hide(IntPtr handle)
    {
        long ex = GetWindowLongPtr(handle, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(handle, GWL_EXSTYLE, (IntPtr)(ex | WS_EX_LAYERED | WS_EX_TRANSPARENT));
        SetLayeredWindowAttributes(handle, 0, 0, LWA_ALPHA);
        Hidden[handle] = true;
    }

    private static void Show(IntPtr handle)
    {
        long ex = GetWindowLongPtr(handle, GWL_EXSTYLE).ToInt64();
        if ((ex & (WS_EX_LAYERED | WS_EX_TRANSPARENT)) != 0)
        {
            SetWindowLongPtr(handle, GWL_EXSTYLE, (IntPtr)(ex & ~(WS_EX_LAYERED | WS_EX_TRANSPARENT)));
            RedrawWindow(handle, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ERASE | RDW_ALLCHILDREN | RDW_FRAME);
        }
        Hidden[handle] = false;
    }

    private static List<(IntPtr Handle, IntPtr Monitor)> FindTaskbars()
    {
        var result = new List<(IntPtr, IntPtr)>();
        foreach (string className in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
        {
            IntPtr h = IntPtr.Zero;
            while ((h = FindWindowEx(IntPtr.Zero, h, className, null)) != IntPtr.Zero)
                result.Add((h, MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST)));
        }
        return result;
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? title);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtr(IntPtr h, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr h, IntPtr rect, IntPtr region, uint flags);
}
