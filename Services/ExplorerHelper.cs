using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WinOptimizer.Services;

public static class ExplorerHelper
{
    private const int HWND_BROADCAST = 0xffff;
    private const int WM_SETTINGCHANGE = 0x001A;
    private const int SMTO_ABORTIFHUNG = 0x0002;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam, string lParam,
        int flags, int timeout, out IntPtr result);

    /// <summary>Tells the shell and other apps that a setting changed (e.g. "TraySettings").</summary>
    public static void BroadcastSettingChange(string area = "TraySettings") =>
        SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero, area, SMTO_ABORTIFHUNG, 2000, out _);

    /// <summary>Restarts Windows Explorer so taskbar / shell tweaks take effect.</summary>
    public static async Task RestartExplorerAsync()
    {
        foreach (var process in Process.GetProcessesByName("explorer"))
        {
            using (process)
            {
                try
                {
                    process.Kill();
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch
                {
                    // Already gone or not ours to kill.
                }
            }
        }

        // Windows normally restarts the shell by itself; start it only if it didn't come back.
        for (int i = 0; i < 10; i++)
        {
            await Task.Delay(500);
            if (Process.GetProcessesByName("explorer").Length > 0) return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
    }
}
