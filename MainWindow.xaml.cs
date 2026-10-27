using System.Windows;
using System.Windows.Controls;
using WinOptimizer.Pages;
using WinOptimizer.Services;
using WinOptimizer.SideTaskbar;

namespace WinOptimizer;

public partial class MainWindow : Window
{
    public sealed record NavItem(string Icon, string Title, Func<FrameworkElement> Create);

    private readonly Dictionary<NavItem, FrameworkElement> _pages = new();

    public MainWindow()
    {
        InitializeComponent();

        NavList.ItemsSource = new[]
        {
            new NavItem("", "Home", () => new HomePage()),
            new NavItem("", "System Monitor", () => new MonitorPage()),
            new NavItem("", "Power Mode", () => new PowerPage()),
            new NavItem("", "Junk Cleaner", () => new CleanerPage()),
            new NavItem("", "Startup Apps", () => new StartupPage()),
            new NavItem("", "Taskbar & Displays", () => new TaskbarPage()),
            new NavItem("", "Privacy & Tweaks", () => new TweaksPage()),
        };

        if (AdminHelper.IsAdmin)
        {
            AdminPanel.Visibility = Visibility.Collapsed;
            Title += " (Administrator)";
        }

        TrayOnCloseCheck.IsChecked = SettingsStore.Current.MinimizeToTrayOnClose;
        NavList.SelectedIndex = 0;
    }

    private void TrayOnCloseCheck_Click(object sender, RoutedEventArgs e)
    {
        SettingsStore.Current.MinimizeToTrayOnClose = TrayOnCloseCheck.IsChecked == true;
        SettingsStore.Save();
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedItem is not NavItem item) return;
        if (!_pages.TryGetValue(item, out var page))
        {
            page = item.Create();
            _pages[item] = page;
        }
        PageHost.Content = page;
    }

    /// <summary>Selects a page in the sidebar by its title (used by the Home page shortcuts).</summary>
    public void NavigateTo(string title) =>
        NavList.SelectedItem = NavList.Items.Cast<NavItem>().FirstOrDefault(i => i.Title == title) ?? NavList.SelectedItem;

    private void RestartAsAdmin_Click(object sender, RoutedEventArgs e)
    {
        if (AdminHelper.RestartAsAdmin())
            App.ExitApp();
    }

    /// <summary>
    /// Closing the window hides it to the notification area when "Minimize to tray on close" is on, and always
    /// while a vertical taskbar or taskbar hiding is on (so they keep working). Otherwise it exits the app.
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        bool taskbarFeaturesOn = SideTaskbarManager.AnyActive || TaskbarHider.AnyActive;
        if (!SettingsStore.Current.MinimizeToTrayOnClose && !taskbarFeaturesOn)
        {
            App.ExitApp();
            return;
        }

        e.Cancel = true;
        Hide();
        if (!SettingsStore.Current.SeenBackgroundNotice)
        {
            SettingsStore.Current.SeenBackgroundNotice = true;
            SettingsStore.Save();
            TrayIcon.ShowBalloon("WinOptimizer is still running",
                taskbarFeaturesOn
                    ? "Your taskbar settings keep working. Click the tray icon to open it, or right-click it and choose Exit to quit."
                    : "Click the tray icon to open it, or right-click it and choose Exit to quit.");
        }
    }
}
