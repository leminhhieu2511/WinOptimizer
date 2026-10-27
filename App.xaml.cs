using System.Threading;
using System.Windows;
using WinOptimizer.Services;
using WinOptimizer.SideTaskbar;

namespace WinOptimizer;

public partial class App : Application
{
    private const string MutexName = @"Local\WinOptimizer.SingleInstance";
    private const string ShowEventName = @"Local\WinOptimizer.ShowWindow";
    private const string ExitEventName = @"Local\WinOptimizer.Exit";

    /// <summary>"WinOptimizer.exe --exit" closes the running copy cleanly (bars give their screen space back).</summary>
    public const string ExitArg = "--exit";

    /// <summary>Passed by "Restart as administrator" so the new copy waits for the old one to exit.</summary>
    public const string AfterRestartArg = "--after-restart";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showEvent;
    private EventWaitHandle? _exitEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Command-line use: WinOptimizer.exe saver | balanced | performance
        if (e.Args.Length > 0 && PowerModeService.TryParse(e.Args[0], out var mode))
        {
            try
            {
                PowerModeService.Set(mode);
                Shutdown(0);
            }
            catch
            {
                Shutdown(1);
            }
            return;
        }

        if (e.Args.Contains(ExitArg))
        {
            try
            {
                using var exit = EventWaitHandle.OpenExisting(ExitEventName);
                exit.Set();
            }
            catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException)
            {
                // Not running (or running as administrator, which a normal copy can't signal).
            }
            Shutdown(0);
            return;
        }

        if (!AcquireSingleInstance(waitForOldCopy: e.Args.Contains(AfterRestartArg)))
        {
            Shutdown(0);
            return;
        }

        // The app keeps running in the tray when the window is closed (see MainWindow.OnClosing).
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Never leave Windows' taskbar invisible: restore it on a crash and when Windows signs out or shuts down.
        AppDomain.CurrentDomain.UnhandledException += (_, _) => TaskbarHider.RestoreAll();
        DispatcherUnhandledException += (_, _) => TaskbarHider.RestoreAll();
        SessionEnding += (_, _) => TaskbarHider.RestoreAll();

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        TrayIcon.Initialize();
        AutoProfileService.Initialize();
        SideTaskbarManager.Apply();
        TaskbarHider.Initialize();
    }

    /// <summary>
    /// Only one copy runs at a time (two copies would create two sets of vertical taskbars). Starting it again
    /// brings the existing window to the front instead.
    /// </summary>
    private bool AcquireSingleInstance(bool waitForOldCopy)
    {
        _instanceMutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            bool acquired = false;
            if (waitForOldCopy)
            {
                try
                {
                    acquired = _instanceMutex.WaitOne(TimeSpan.FromSeconds(10));
                }
                catch (AbandonedMutexException)
                {
                    acquired = true; // the old copy exited without releasing it
                }
            }

            if (!acquired)
            {
                try
                {
                    using var show = EventWaitHandle.OpenExisting(ShowEventName);
                    show.Set();
                }
                catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException)
                {
                    // The other copy runs as administrator, so a normal copy isn't allowed to signal it.
                    MessageBox.Show("WinOptimizer is already running as administrator. Use the existing window, or the ⚡ button on the vertical taskbar.",
                        "WinOptimizer", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                _instanceMutex.Dispose();
                _instanceMutex = null;
                return false;
            }
        }

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        var listener = new Thread(() =>
        {
            var events = new WaitHandle[] { _showEvent, _exitEvent };
            while (true)
            {
                if (WaitHandle.WaitAny(events) == 1)
                {
                    Dispatcher.BeginInvoke(ExitApp);
                    return;
                }
                Dispatcher.BeginInvoke(ShowMainWindow);
            }
        })
        {
            IsBackground = true,
            Name = "WinOptimizer show-window listener",
        };
        listener.Start();
        return true;
    }

    public static void ShowMainWindow()
    {
        if (Current.MainWindow is not { } window) return;
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    public static void ExitApp()
    {
        AutoProfileService.Shutdown();
        TrayIcon.Dispose();
        TaskbarHider.RestoreAll();
        SideTaskbarManager.CloseAll();
        Current.Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AutoProfileService.Shutdown();
        TrayIcon.Dispose();
        TaskbarHider.RestoreAll();
        SideTaskbarManager.CloseAll();
        if (_instanceMutex is not null)
        {
            try
            {
                _instanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not owned (shouldn't happen).
            }
            _instanceMutex.Dispose();
        }
        base.OnExit(e);
    }
}
