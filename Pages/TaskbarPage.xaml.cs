using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using WinOptimizer.Services;
using WinOptimizer.SideTaskbar;

namespace WinOptimizer.Pages;

public partial class TaskbarPage : UserControl
{
    private bool _loading;

    public TaskbarPage()
    {
        InitializeComponent();
        if (!TaskbarService.RegistrySettingsSupported) ShowSettingsOnlyMode();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) LoadState();
        };
        // e.g. a bar was turned off from its own menu
        SideTaskbarManager.Changed += () =>
        {
            if (IsVisible) LoadSideTaskbars();
        };
    }

    /// <summary>
    /// One row per display with two dropdowns: Windows' own taskbar (Shown / Auto-hide) and
    /// WinOptimizer's vertical taskbar (Off / Left side / Right side).
    /// </summary>
    private void LoadSideTaskbars()
    {
        SideTaskbarRows.Children.Clear();
        foreach (var d in TaskbarService.GetDisplays())
        {
            string title = d.IsPrimary ? $"Display {d.Number} (main)" : $"Display {d.Number}";

            bool hasTaskbar = TaskbarHider.HasTaskbar(d);
            var hideCombo = new ComboBox
            {
                Width = 150,
                Tag = d.DeviceName,
                ItemsSource = new[] { "Shown", "Auto-hide" },
                SelectedIndex = TaskbarHider.IsHidden(d.DeviceName) ? 1 : 0,
                IsEnabled = hasTaskbar || TaskbarHider.IsHidden(d.DeviceName),
                ToolTip = hasTaskbar
                    ? "Hide Windows' taskbar on this display; it appears when you move the mouse to its strip."
                    : "Windows shows no taskbar here. Turn on \"Show my taskbar on all displays\" in Windows Settings.",
            };
            AutomationProperties.SetName(hideCombo, $"Windows taskbar on {title}");
            hideCombo.SelectionChanged += HideTaskbar_Changed;

            var sideCombo = new ComboBox
            {
                Width = 150,
                Tag = d.DeviceName,
                ItemsSource = new[] { "Off", "Left side", "Right side" },
                SelectedIndex = SideTaskbarManager.GetEdge(d.DeviceName) switch { SideEdge.Left => 1, SideEdge.Right => 2, _ => 0 },
            };
            AutomationProperties.SetName(sideCombo, $"Vertical taskbar on {title}");
            sideCombo.SelectionChanged += SideEdge_Changed;

            var choices = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            choices.Children.Add(Labeled("Windows taskbar", hideCombo));
            choices.Children.Add(Labeled("Vertical taskbar", sideCombo, leftMargin: 12));
            DockPanel.SetDock(choices, Dock.Right);

            var text = new StackPanel { Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
            text.Children.Add(new TextBlock
            {
                Text = $"{d.Width} × {d.Height}  ·  {d.MonitorName}" + (hasTaskbar ? "" : "  ·  no Windows taskbar here"),
                Style = (Style)FindResource("SecondaryText"),
            });

            var row = new DockPanel();
            row.Children.Add(choices);
            row.Children.Add(text);
            SideTaskbarRows.Children.Add(new Border { Style = (Style)FindResource("Card"), Child = row });
        }
    }

    private StackPanel Labeled(string label, Control control, double leftMargin = 0)
    {
        var panel = new StackPanel { Margin = new Thickness(leftMargin, 0, 0, 0) };
        panel.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("SecondaryText"), Margin = new Thickness(2, 0, 0, 4) });
        panel.Children.Add(control);
        return panel;
    }

    private void HideTaskbar_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { Tag: string device } combo) return;
        bool hide = combo.SelectedIndex == 1;
        if (hide == TaskbarHider.IsHidden(device)) return; // just showing the saved choice
        try
        {
            TaskbarHider.Set(device, hide);
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Windows taskbar", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Puts every taskbar back the way Windows had it and turns off WinOptimizer's taskbar features.</summary>
    private void ResetTaskbars_Click(object sender, RoutedEventArgs e)
    {
        TaskbarHider.Reset();
        foreach (var d in TaskbarService.GetDisplays()) SideTaskbarManager.Set(d.DeviceName, null);
        LoadSideTaskbars();
    }

    private void OpenCreditLink_Click(object sender, RoutedEventArgs e) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/nikoleinonen/per-monitor-taskbar") { UseShellExecute = true });

    private void SideEdge_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { Tag: string device } combo) return;
        SideEdge? edge = combo.SelectedIndex switch { 1 => SideEdge.Left, 2 => SideEdge.Right, _ => null };
        if (edge == SideTaskbarManager.GetEdge(device)) return; // just showing the saved choice
        try
        {
            SideTaskbarManager.Set(device, edge);
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Vertical taskbar", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// On builds where Windows ignores the taskbar registry values, hide those options (their stored values are
    /// stale too) and point to Windows Settings instead. Auto-hide and the displays list keep working.
    /// </summary>
    private void ShowSettingsOnlyMode()
    {
        SettingsOnlyNotice.Visibility = Visibility.Visible;
        SettingsOnlyTitle.Text = $"Change taskbar options in Windows Settings (build {TaskbarService.WindowsBuild})";
        SubtitleText.Text = "See your displays and control auto-hide. Other taskbar options are in Windows Settings on this version of Windows.";

        MultiDisplayHeader.Visibility = Visibility.Collapsed;
        foreach (Control control in new Control[]
                 {
                     AllDisplaysToggle, ButtonsModeCombo, OtherCombineCombo,
                     AlignmentCombo, MainCombineCombo, SearchCombo, TaskViewToggle,
                 })
        {
            CardOf(control).Visibility = Visibility.Collapsed;
        }

        // Restarting Explorer doesn't help here, so hide that row as well.
        ((FrameworkElement)RestartExplorerButton.Parent).Visibility = Visibility.Collapsed;
    }

    /// <summary>Each option sits in DockPanel inside a card Border.</summary>
    private static FrameworkElement CardOf(Control control) =>
        (FrameworkElement)((FrameworkElement)control.Parent).Parent;

    private void OpenTaskbarSettings_Click(object sender, RoutedEventArgs e) => TaskbarService.OpenTaskbarSettings();

    private void LoadState()
    {
        _loading = true;
        try
        {
            DisplayList.ItemsSource = TaskbarService.GetDisplays().Select(d => new
            {
                Title = d.IsPrimary ? $"Display {d.Number} (main)" : $"Display {d.Number}",
                Resolution = $"{d.Width} × {d.Height}",
                Name = d.MonitorName,
            }).ToList();
            LoadSideTaskbars();

            AllDisplaysToggle.IsChecked = TaskbarService.ShowOnAllDisplays;
            ButtonsModeCombo.SelectedIndex = Math.Clamp(TaskbarService.MultiMonitorButtonsMode, 0, 2);
            OtherCombineCombo.SelectedIndex = Math.Clamp(TaskbarService.OtherTaskbarsCombine, 0, 2);
            AlignmentCombo.SelectedIndex = Math.Clamp(TaskbarService.Alignment, 0, 1);
            MainCombineCombo.SelectedIndex = Math.Clamp(TaskbarService.MainTaskbarCombine, 0, 2);
            SearchCombo.SelectedIndex = Math.Clamp(TaskbarService.SearchMode, 0, 3);
            TaskViewToggle.IsChecked = TaskbarService.ShowTaskView;
            AutoHideToggle.IsChecked = TaskbarService.AutoHide;
            UpdateMultiMonitorEnabled();
        }
        finally
        {
            _loading = false;
        }
    }

    private void UpdateMultiMonitorEnabled() =>
        ButtonsModeCombo.IsEnabled = OtherCombineCombo.IsEnabled = AllDisplaysToggle.IsChecked == true;

    /// <summary>Applies a change unless the page is just loading values; shows errors instead of crashing.</summary>
    /// <param name="needsRestart">
    /// Explorer doesn't redraw the taskbar when these registry settings change, even with WM_SETTINGCHANGE
    /// sent straight to the taskbar windows, so ask the user to restart it.
    /// </param>
    private void Apply(Action change, bool needsRestart = true)
    {
        if (_loading) return;
        try
        {
            change();
            if (needsRestart) RestartBanner.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Taskbar", MessageBoxButton.OK, MessageBoxImage.Warning);
            LoadState();
        }
    }

    private void AllDisplays_Changed(object sender, RoutedEventArgs e) => Apply(() =>
    {
        TaskbarService.ShowOnAllDisplays = AllDisplaysToggle.IsChecked == true;
        UpdateMultiMonitorEnabled();
    });

    private void ButtonsMode_Changed(object sender, SelectionChangedEventArgs e) =>
        Apply(() => TaskbarService.MultiMonitorButtonsMode = ButtonsModeCombo.SelectedIndex);

    private void OtherCombine_Changed(object sender, SelectionChangedEventArgs e) =>
        Apply(() => TaskbarService.OtherTaskbarsCombine = OtherCombineCombo.SelectedIndex);

    private void Alignment_Changed(object sender, SelectionChangedEventArgs e) =>
        Apply(() => TaskbarService.Alignment = AlignmentCombo.SelectedIndex);

    private void MainCombine_Changed(object sender, SelectionChangedEventArgs e) =>
        Apply(() => TaskbarService.MainTaskbarCombine = MainCombineCombo.SelectedIndex);

    private void Search_Changed(object sender, SelectionChangedEventArgs e) =>
        Apply(() => TaskbarService.SearchMode = SearchCombo.SelectedIndex);

    private void TaskView_Changed(object sender, RoutedEventArgs e) =>
        Apply(() => TaskbarService.ShowTaskView = TaskViewToggle.IsChecked == true);

    private void AutoHide_Changed(object sender, RoutedEventArgs e) =>
        Apply(() => TaskbarService.AutoHide = AutoHideToggle.IsChecked == true, needsRestart: false); // applied live via SHAppBarMessage

    private async void RestartExplorer_Click(object sender, RoutedEventArgs e)
    {
        RestartExplorerButton.IsEnabled = BannerRestartButton.IsEnabled = false;
        BannerTitle.Text = "Restarting Explorer…";
        try
        {
            await ExplorerHelper.RestartExplorerAsync();
            RestartBanner.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Restart Explorer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            RestartExplorerButton.IsEnabled = BannerRestartButton.IsEnabled = true;
            BannerTitle.Text = "Restart Explorer to apply your changes";
            LoadState();
        }
    }

    private void DismissBanner_Click(object sender, RoutedEventArgs e) =>
        RestartBanner.Visibility = Visibility.Collapsed;
}
