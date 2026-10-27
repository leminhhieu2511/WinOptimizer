using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WinOptimizer.Services;

/// <summary>A connected monitor. X/Y/Width/Height are its full bounds in screen pixels; Handle is the HMONITOR.</summary>
public sealed record DisplayInfo(int Number, string DeviceName, string MonitorName, int Width, int Height, bool IsPrimary,
    IntPtr Handle = default, int X = 0, int Y = 0);

/// <summary>Taskbar settings, including how the taskbar behaves across multiple monitors.</summary>
public static class TaskbarService
{
    private const string Advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string Search = @"Software\Microsoft\Windows\CurrentVersion\Search";

    /// <summary>
    /// Whether Windows still reads the classic taskbar registry values (alignment, combine buttons, search,
    /// Task View, multiple displays). On build 28000 (Canary) Settings stores them through an undocumented
    /// internal API instead: Settings neither reads nor writes these values and Explorer ignores them, even after
    /// a restart. Builds before 27000 (Windows 11 24H2/25H2 and older) still use the registry.
    /// Auto-hide is unaffected because it uses SHAppBarMessage.
    /// </summary>
    public static bool RegistrySettingsSupported { get; } = Environment.OSVersion.Version.Build < 27000;

    public static int WindowsBuild => Environment.OSVersion.Version.Build;

    public static void OpenTaskbarSettings() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:taskbar") { UseShellExecute = true });

    // --- Multiple displays ---

    /// <summary>"Show my taskbar on all displays".</summary>
    public static bool ShowOnAllDisplays
    {
        get => Get(Advanced, "MMTaskbarEnabled", 1) == 1;
        set => Set(Advanced, "MMTaskbarEnabled", value ? 1 : 0);
    }

    /// <summary>0 = all taskbars, 1 = main taskbar and taskbar where window is open, 2 = taskbar where window is open.</summary>
    public static int MultiMonitorButtonsMode
    {
        get => Get(Advanced, "MMTaskbarMode", 0);
        set => Set(Advanced, "MMTaskbarMode", value);
    }

    /// <summary>Combine buttons on other taskbars: 0 = always, 1 = when taskbar is full, 2 = never.</summary>
    public static int OtherTaskbarsCombine
    {
        get => Get(Advanced, "MMTaskbarGlomLevel", 0);
        set => Set(Advanced, "MMTaskbarGlomLevel", value);
    }

    // --- General ---

    /// <summary>Combine buttons on the main taskbar: 0 = always, 1 = when taskbar is full, 2 = never.</summary>
    public static int MainTaskbarCombine
    {
        get => Get(Advanced, "TaskbarGlomLevel", 0);
        set => Set(Advanced, "TaskbarGlomLevel", value);
    }

    /// <summary>0 = left, 1 = center (Windows 11).</summary>
    public static int Alignment
    {
        get => Get(Advanced, "TaskbarAl", 1);
        set => Set(Advanced, "TaskbarAl", value);
    }

    public static bool ShowTaskView
    {
        get => Get(Advanced, "ShowTaskViewButton", 1) == 1;
        set => Set(Advanced, "ShowTaskViewButton", value ? 1 : 0);
    }

    /// <summary>0 = hidden, 1 = icon only, 2 = search box, 3 = icon and label.</summary>
    public static int SearchMode
    {
        get => Get(Search, "SearchboxTaskbarMode", 2);
        set => Set(Search, "SearchboxTaskbarMode", value);
    }

    public static bool AutoHide
    {
        get
        {
            var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };
            return ((int)SHAppBarMessage(ABM_GETSTATE, ref data) & ABS_AUTOHIDE) != 0;
        }
        set
        {
            var data = new APPBARDATA
            {
                cbSize = Marshal.SizeOf<APPBARDATA>(),
                hWnd = FindWindow("Shell_TrayWnd", null),
                lParam = value ? ABS_AUTOHIDE : ABS_ALWAYSONTOP,
            };
            SHAppBarMessage(ABM_SETSTATE, ref data);
        }
    }

    private static int Get(string path, string name, int defaultValue) =>
        RegistryHelper.ReadInt(RegistryHive.CurrentUser, path, name, defaultValue);

    private static void Set(string path, string name, int value)
    {
        RegistryHelper.WriteDword(RegistryHive.CurrentUser, path, name, value);
        ExplorerHelper.BroadcastSettingChange();
    }

    // --- Displays ---

    private static List<DisplayInfo>? _displayCache;
    private static DateTime _displayCacheTime;

    /// <summary>Same as <see cref="GetDisplays"/> but reused for 2 seconds; for code that runs many times a second.</summary>
    public static List<DisplayInfo> GetDisplaysCached()
    {
        if (_displayCache is null || DateTime.UtcNow - _displayCacheTime > TimeSpan.FromSeconds(2))
        {
            _displayCache = GetDisplays();
            _displayCacheTime = DateTime.UtcNow;
        }
        return _displayCache;
    }

    public static List<DisplayInfo> GetDisplays()
    {
        var displays = new List<DisplayInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMonitor, _, _, _) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(hMonitor, ref info))
            {
                var device = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                string monitorName = EnumDisplayDevices(info.szDevice, 0, ref device, 0) && !string.IsNullOrWhiteSpace(device.DeviceString)
                    ? device.DeviceString
                    : "Display";

                displays.Add(new DisplayInfo(
                    displays.Count + 1,
                    info.szDevice,
                    monitorName,
                    info.rcMonitor.Right - info.rcMonitor.Left,
                    info.rcMonitor.Bottom - info.rcMonitor.Top,
                    (info.dwFlags & MONITORINFOF_PRIMARY) != 0,
                    hMonitor,
                    info.rcMonitor.Left,
                    info.rcMonitor.Top));
            }
            return true;
        }, IntPtr.Zero);
        return displays;
    }

    // --- Native ---

    private const int ABM_GETSTATE = 0x4;
    private const int ABM_SETSTATE = 0xA;
    private const int ABS_AUTOHIDE = 0x1;
    private const int ABS_ALWAYSONTOP = 0x2;
    private const int MONITORINFOF_PRIMARY = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("shell32.dll")]
    private static extern IntPtr SHAppBarMessage(int message, ref APPBARDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? device, uint devNum, ref DISPLAY_DEVICE displayDevice, uint flags);
}
