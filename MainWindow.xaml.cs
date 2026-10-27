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

        NavList.SelectedIndex = 0;
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
    /// While a vertical taskbar or taskbar hiding is on, closing the window only hides it so they keep working.
    /// Otherwise closing the window exits the app.
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!SideTaskbarManager.AnyActive && !TaskbarHider.AnyActive)
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
            MessageBox.Show(
                "WinOptimizer is still running so your taskbar settings keep working.\n\n" +
                "To open it again, start WinOptimizer again or click the ⚡ button on a vertical taskbar. " +
                "To close it completely, right-click that ⚡ button and choose Exit.",
                "WinOptimizer", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
