using System.Drawing;
using Forms = System.Windows.Forms;

namespace WinOptimizer.Services;

/// <summary>
/// The WinOptimizer icon in the notification area. It stays there while the app runs, so a window that was
/// closed to the tray can always be opened again (double-click) or the app exited (right-click → Exit).
/// </summary>
public static class TrayIcon
{
    private static Forms.NotifyIcon? _icon;

    public static void Initialize()
    {
        if (_icon is not null) return;

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open WinOptimizer", null, (_, _) => App.ShowMainWindow()).Font =
            new Font(menu.Font, System.Drawing.FontStyle.Bold);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => App.ExitApp());

        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "WinOptimizer",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) App.ShowMainWindow();
        };
    }

    /// <summary>Shows a short notification from the tray icon.</summary>
    public static void ShowBalloon(string title, string text)
    {
        _icon?.ShowBalloonTip(5000, title, text, Forms.ToolTipIcon.Info);
    }

    public static void Dispose()
    {
        if (_icon is null) return;
        _icon.Visible = false; // otherwise a dead icon lingers until the mouse passes over it
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        _icon = null;
    }

    private static Icon LoadIcon()
    {
        try
        {
            if (Environment.ProcessPath is { } exe && Icon.ExtractAssociatedIcon(exe) is { } icon)
                return icon;
        }
        catch (Exception ex) when (ex is ArgumentException or System.IO.IOException)
        {
            // Fall back to the generic icon.
        }
        return SystemIcons.Application;
    }
}
