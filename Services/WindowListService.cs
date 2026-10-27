using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinOptimizer.Services;

/// <summary>A top-level window that would normally have a taskbar button.</summary>
public sealed record TaskWindow(IntPtr Handle, string Title, IntPtr Monitor, bool IsMinimized);

/// <summary>Lists app windows (like the taskbar / Alt+Tab do) and switches between them.</summary>
public static class WindowListService
{
    private static readonly HashSet<string> ShellClasses = ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"];

    public static List<TaskWindow> GetWindows()
    {
        var windows = new List<TaskWindow>();
        EnumWindows((h, _) =>
        {
            if (IsTaskbarWindow(h, out string title))
                windows.Add(new TaskWindow(h, title, MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST), IsIconic(h)));
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private static bool IsTaskbarWindow(IntPtr h, out string title)
    {
        title = "";
        if (!IsWindowVisible(h)) return false;

        long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
        bool appWindow = (ex & WS_EX_APPWINDOW) != 0;
        if (!appWindow)
        {
            if (GetWindow(h, GW_OWNER) != IntPtr.Zero) return false;          // dialogs/popups owned by another window
            if ((ex & (WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)) != 0) return false; // tool windows, our own side taskbars
        }

        // Cloaked = hidden by Windows (other virtual desktops, suspended UWP apps).
        if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;

        var className = new StringBuilder(64);
        GetClassName(h, className, className.Capacity);
        if (ShellClasses.Contains(className.ToString())) return false;

        int length = GetWindowTextLength(h);
        if (length == 0) return false;
        var text = new StringBuilder(length + 1);
        GetWindowText(h, text, text.Capacity);
        title = text.ToString();
        return true;
    }

    public static IntPtr GetForeground() => GetForegroundWindow();

    /// <summary>Clicking the button of the window that's already in front minimizes it, like the real taskbar.</summary>
    public static void ActivateOrMinimize(IntPtr h, IntPtr foregroundBeforeClick)
    {
        if (h == foregroundBeforeClick && !IsIconic(h))
        {
            ShowWindow(h, SW_MINIMIZE);
            return;
        }
        if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
        SetForegroundWindow(h);
    }

    public static void Close(IntPtr h) => PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

    /// <summary>Opens the Start menu (same as pressing the Windows key).</summary>
    public static void OpenStartMenu()
    {
        keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
        keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    /// <summary>The window's own icon, or its program's icon. Returns null if neither is available.</summary>
    public static ImageSource? GetIcon(IntPtr h)
    {
        IntPtr icon = IntPtr.Zero;
        foreach (int type in new[] { ICON_BIG, ICON_SMALL2, ICON_SMALL })
        {
            if (SendMessageTimeout(h, WM_GETICON, (IntPtr)type, IntPtr.Zero, SMTO_ABORTIFHUNG, 100, out icon) != IntPtr.Zero && icon != IntPtr.Zero)
                break;
            icon = IntPtr.Zero;
        }
        if (icon == IntPtr.Zero) icon = GetClassLongPtr(h, GCLP_HICON);
        if (icon == IntPtr.Zero) icon = GetClassLongPtr(h, GCLP_HICONSM);
        if (icon != IntPtr.Zero) return ToImage(icon); // owned by the window: don't destroy

        string? path = GetProcessPath(h);
        if (path is null) return null;
        var info = new SHFILEINFO();
        if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON) == IntPtr.Zero
            || info.hIcon == IntPtr.Zero)
            return null;
        try
        {
            return ToImage(info.hIcon);
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    private static ImageSource? ToImage(IntPtr icon)
    {
        try
        {
            var image = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException)
        {
            return null;
        }
    }

    private static string? GetProcessPath(IntPtr h)
    {
        GetWindowThreadProcessId(h, out uint pid);
        IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            int size = buffer.Capacity;
            return QueryFullProcessImageName(process, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    // --- Native ---

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000, WS_EX_NOACTIVATE = 0x08000000;
    private const uint GW_OWNER = 4;
    private const int DWMWA_CLOAKED = 14;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int SW_MINIMIZE = 6, SW_RESTORE = 9;
    private const int WM_CLOSE = 0x10, WM_GETICON = 0x7F;
    private const int ICON_SMALL = 0, ICON_BIG = 1, ICON_SMALL2 = 2;
    private const int GCLP_HICON = -14, GCLP_HICONSM = -34;
    private const int SMTO_ABORTIFHUNG = 2;
    private const byte VK_LWIN = 0x5B;
    private const uint KEYEVENTF_KEYUP = 2;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0x0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    private delegate bool EnumWindowsProc(IntPtr h, IntPtr data);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr data);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
    [DllImport("user32.dll")] private static extern IntPtr GetClassLongPtr(IntPtr h, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder name, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr h, int msg, IntPtr w, IntPtr l, int flags, int timeout, out IntPtr result);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attribute, out int value, int size);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);
}
