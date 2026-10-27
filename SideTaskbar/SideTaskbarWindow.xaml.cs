using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WinOptimizer.Services;

namespace WinOptimizer.SideTaskbar;

/// <summary>One button on the vertical taskbar.</summary>
public sealed record TaskButton(IntPtr Handle, string Title, ImageSource? Icon, bool IsActive, HorizontalAlignment IndicatorSide)
{
    public string Initial => Title.Length > 0 ? Title[..1].ToUpperInvariant() : "?";
    public Visibility InitialVisibility => Icon is null ? Visibility.Visible : Visibility.Collapsed;
    public double IndicatorHeight => IsActive ? 18 : 0;
}

/// <summary>
/// A vertical taskbar docked to the left or right edge of one monitor. It registers as an "app bar"
/// (SHAppBarMessage), so Windows shrinks that monitor's work area and maximized windows don't cover it.
/// It never takes focus (WS_EX_NOACTIVATE), so clicks go to the window you pick.
/// </summary>
public partial class SideTaskbarWindow : Window
{
    private const double BarWidth = 56; // in DIPs

    public string Device { get; }
    public SideEdge Edge { get; }

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private readonly Dictionary<IntPtr, ImageSource?> _icons = new();
    private readonly List<IntPtr> _order = new();
    private IntPtr _hwnd;
    private uint _callbackMessage;
    private bool _registered;
    private string _signature = "";

    public SideTaskbarWindow(string device, SideEdge edge)
    {
        Device = device;
        Edge = edge;
        InitializeComponent();
        EdgeBorder.BorderThickness = edge == SideEdge.Left ? new Thickness(0, 0, 1, 0) : new Thickness(1, 0, 0, 0);
        _timer.Tick += (_, _) => RefreshWindows();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;

        long ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, (IntPtr)(ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
        HwndSource.FromHwnd(_hwnd)!.AddHook(WndProc);

        _callbackMessage = RegisterWindowMessage("WinOptimizer.SideTaskbar");
        var data = NewData();
        data.uCallbackMessage = _callbackMessage;
        _registered = SHAppBarMessage(ABM_NEW, ref data) != IntPtr.Zero;
        Reposition();

        _timer.Start();
        RefreshWindows();
    }

    /// <summary>Docks the bar to its monitor edge. Called on start, display changes and when Windows asks.</summary>
    public void Reposition()
    {
        var display = TaskbarService.GetDisplays().FirstOrDefault(d => d.DeviceName == Device);
        if (display is null || _hwnd == IntPtr.Zero) return;

        // Two passes: the first move puts the window on the target monitor so its DPI is right for the second.
        for (int pass = 0; pass < 2; pass++)
        {
            int width = (int)Math.Round(BarWidth * GetDpiForWindow(_hwnd) / 96.0);
            var data = NewData();
            data.uEdge = Edge == SideEdge.Left ? ABE_LEFT : ABE_RIGHT;
            data.rc = new RECT { Left = display.X, Top = display.Y, Right = display.X + display.Width, Bottom = display.Y + display.Height };
            Narrow(ref data.rc, width);

            if (_registered)
            {
                SHAppBarMessage(ABM_QUERYPOS, ref data); // Windows may move our rect away from other bars
                Narrow(ref data.rc, width);
                SHAppBarMessage(ABM_SETPOS, ref data);
            }
            SetWindowPos(_hwnd, HWND_TOPMOST, data.rc.Left, data.rc.Top, data.rc.Right - data.rc.Left,
                data.rc.Bottom - data.rc.Top, SWP_NOACTIVATE);
        }
    }

    private void Narrow(ref RECT rc, int width)
    {
        if (Edge == SideEdge.Left) rc.Right = rc.Left + width;
        else rc.Left = rc.Right - width;
    }

    private void RefreshWindows()
    {
        var display = TaskbarService.GetDisplays().FirstOrDefault(d => d.DeviceName == Device);
        if (display is null) return;

        IntPtr foreground = WindowListService.GetForeground();
        var current = WindowListService.GetWindows().Where(w => w.Monitor == display.Handle).ToDictionary(w => w.Handle);

        // Keep buttons in a stable order (first seen first), like the real taskbar. Windows lists windows
        // front-to-back, so without this the buttons would jump around every time you switch apps.
        _order.RemoveAll(h => !current.ContainsKey(h));
        foreach (var handle in current.Keys)
            if (!_order.Contains(handle)) _order.Add(handle);
        var windows = _order.Select(h => current[h]).ToList();

        // Rebuild the buttons only when something visible changed.
        string signature = string.Join("|", windows.Select(w => $"{w.Handle}:{w.Title}:{w.Handle == foreground}"));
        if (signature == _signature) return;
        _signature = signature;

        foreach (var gone in _icons.Keys.Except(windows.Select(w => w.Handle)).ToList()) _icons.Remove(gone);
        var side = Edge == SideEdge.Left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        WindowList.ItemsSource = windows.Select(w =>
        {
            if (!_icons.TryGetValue(w.Handle, out var icon))
                _icons[w.Handle] = icon = WindowListService.GetIcon(w.Handle);
            return new TaskButton(w.Handle, w.Title, icon, w.Handle == foreground, side);
        }).ToList();
    }

    private void Window_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TaskButton button }) return;
        // The bar never takes focus, so the foreground window is still the one the user was using.
        WindowListService.ActivateOrMinimize(button.Handle, WindowListService.GetForeground());
        _signature = "";
        RefreshWindows();
    }

    private void CloseWindow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TaskButton button }) WindowListService.Close(button.Handle);
    }

    private void Start_Click(object sender, RoutedEventArgs e) => WindowListService.OpenStartMenu();

    private void App_Click(object sender, RoutedEventArgs e) => App.ShowMainWindow();

    private void OpenApp_Click(object sender, RoutedEventArgs e) => App.ShowMainWindow();

    private void TurnOff_Click(object sender, RoutedEventArgs e) => SideTaskbarManager.Set(Device, null);

    private void Exit_Click(object sender, RoutedEventArgs e) => App.ExitApp();

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_MOUSEACTIVATE)
        {
            handled = true;
            return (IntPtr)MA_NOACTIVATE;
        }
        if (_callbackMessage != 0 && msg == _callbackMessage)
        {
            switch (wParam.ToInt32())
            {
                case ABN_POSCHANGED:
                    Reposition();
                    break;
                case ABN_FULLSCREENAPP:
                    // Step back while a full-screen app (game, video) runs on this monitor.
                    SetWindowPos(_hwnd, lParam != IntPtr.Zero ? HWND_BOTTOM : HWND_TOPMOST, 0, 0, 0, 0,
                        SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                    break;
            }
            handled = true;
        }
        return IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        if (_registered)
        {
            var data = NewData();
            SHAppBarMessage(ABM_REMOVE, ref data); // gives the screen space back
            _registered = false;
        }
        base.OnClosed(e);
    }

    private APPBARDATA NewData() => new() { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = _hwnd };

    // --- Native ---

    private const int ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3;
    private const int ABN_POSCHANGED = 1, ABN_FULLSCREENAPP = 2;
    private const uint ABE_LEFT = 0, ABE_RIGHT = 2;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    private const int WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3;
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
    private static readonly IntPtr HWND_TOPMOST = new(-1), HWND_BOTTOM = new(1);

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

    [DllImport("shell32.dll")] private static extern IntPtr SHAppBarMessage(int message, ref APPBARDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtr(IntPtr h, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr h);
}
