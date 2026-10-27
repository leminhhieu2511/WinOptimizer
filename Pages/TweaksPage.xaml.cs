using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using WinOptimizer.Services;

namespace WinOptimizer.Pages;

public partial class TweaksPage : UserControl
{
    private readonly List<Tweak> _tweaks = TweaksService.CreateTweaks();

    public TweaksPage()
    {
        InitializeComponent();

        var view = new ListCollectionView(_tweaks);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Tweak.Category)));
        TweakList.ItemsSource = view;

        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) return;
            foreach (var tweak in _tweaks) tweak.Refresh();
            UpdateSummary();
        };
    }

    private void UpdateSummary() =>
        SummaryText.Text = $"{_tweaks.Count(t => t.IsApplied)} of {_tweaks.Count} tweaks on";

    private void Toggle_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: Tweak tweak } toggle) return;
        if (toggle.IsChecked == tweak.IsApplied) return; // switch is just showing the current state
        try
        {
            tweak.Apply(toggle.IsChecked == true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, tweak.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        tweak.Refresh();
        toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, tweak.IsApplied); // show the real state, keep the binding
        UpdateSummary();
    }

    private async void RestartExplorer_Click(object sender, RoutedEventArgs e)
    {
        RestartExplorerButton.IsEnabled = false;
        try
        {
            await ExplorerHelper.RestartExplorerAsync();
        }
        finally
        {
            RestartExplorerButton.IsEnabled = true;
        }
    }
}
