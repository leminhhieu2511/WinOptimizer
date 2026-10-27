using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using WinOptimizer.Services;

namespace WinOptimizer.Pages;

public partial class StartupPage : UserControl
{
    private List<StartupItem> _items = [];

    public StartupPage()
    {
        InitializeComponent();
        Reload();
    }

    private void Reload()
    {
        _items = StartupAppsService.Load();
        AppList.ItemsSource = _items;
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        int enabled = _items.Count(i => i.IsEnabled);
        SummaryText.Text = $"{_items.Count} startup apps · {enabled} enabled";
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Reload();

    private void Toggle_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: StartupItem item } toggle) return;
        if (toggle.IsChecked == item.IsEnabled) return; // switch is just showing the current state
        try
        {
            StartupAppsService.SetEnabled(item, toggle.IsChecked == true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Could not change startup app",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        // Show the real state without breaking the one-way binding.
        toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, item.IsEnabled);
        UpdateSummary();
    }

    private void OpenLocation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: StartupItem item } && !StartupAppsService.OpenLocation(item))
            MessageBox.Show(Window.GetWindow(this), "The program file for this entry couldn't be found.",
                item.Name, MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
