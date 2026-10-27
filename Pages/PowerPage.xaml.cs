using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WinOptimizer.Helpers;
using WinOptimizer.Services;

namespace WinOptimizer.Pages;

public partial class PowerPage : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _updating;

    public PowerPage()
    {
        InitializeComponent();
        LoadAutoSettings();

        // Stay in sync if the mode is changed from Windows Settings (or by automatic switching).
        _timer.Tick += (_, _) => RefreshState();
        PageActivity.Track(this,
            onActive: () =>
            {
                RefreshState();
                _timer.Start();
                AutoProfileService.PowerStateChanged += RefreshState;
            },
            onInactive: () =>
            {
                _timer.Stop();
                AutoProfileService.PowerStateChanged -= RefreshState;
            });
    }

    private void LoadAutoSettings()
    {
        var s = SettingsStore.Current;
        List<ComboBoxItem> Modes() =>
            Enum.GetValues<PowerMode>().Select(m => new ComboBoxItem { Content = PowerModeService.DisplayName(m), Tag = m }).ToList();
        _updating = true;
        AcModeCombo.ItemsSource = Modes();
        BatteryModeCombo.ItemsSource = Modes();
        AcModeCombo.SelectedIndex = (int)s.AcPowerMode;
        BatteryModeCombo.SelectedIndex = (int)s.BatteryPowerMode;
        AutoEnabledCheck.IsChecked = s.AutoProfilesEnabled;
        AutoSwitchPlanCheck.IsChecked = s.AutoProfilesSwitchPlan;
        AutoNotifyCheck.IsChecked = s.AutoProfilesNotify;
        AutoOptions.IsEnabled = s.AutoProfilesEnabled;
        _updating = false;
    }

    private void AutoSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        var s = SettingsStore.Current;
        bool wasEnabled = s.AutoProfilesEnabled;
        s.AutoProfilesEnabled = AutoEnabledCheck.IsChecked == true;
        if (AcModeCombo.SelectedItem is ComboBoxItem { Tag: PowerMode ac }) s.AcPowerMode = ac;
        if (BatteryModeCombo.SelectedItem is ComboBoxItem { Tag: PowerMode battery }) s.BatteryPowerMode = battery;
        s.AutoProfilesSwitchPlan = AutoSwitchPlanCheck.IsChecked == true;
        s.AutoProfilesNotify = AutoNotifyCheck.IsChecked == true;
        SettingsStore.Save();
        AutoOptions.IsEnabled = s.AutoProfilesEnabled;

        // Turning it on, or changing the mode for the current source, takes effect right away
        // (no notification: the user is looking at this page).
        if (s.AutoProfilesEnabled && (!wasEnabled || sender is ComboBox))
            AutoProfileService.ApplyNow(notify: false);
        RefreshState();
    }

    private void RefreshState()
    {
        var (planId, planName) = PowerModeService.GetActivePlan();
        bool balancedPlan = planId == PowerModeService.BalancedPlan;
        PlanWarning.Visibility = balancedPlan ? Visibility.Collapsed : Visibility.Visible;
        PlanWarningText.Text = $"The active power plan is \"{planName}\". Windows only allows changing power mode on the Balanced plan.";

        try
        {
            var current = PowerModeService.GetCurrent();
            _updating = true;
            SaverRadio.IsChecked = current == PowerMode.BatterySaver;
            BalancedRadio.IsChecked = current == PowerMode.Balanced;
            PerformanceRadio.IsChecked = current == PowerMode.HighPerformance;
            StatusText.Text = $"Current mode: {PowerModeService.DisplayName(current)}   ·   Power plan: {planName}";
            var battery = SystemInfoService.GetBattery();
            AutoStatusText.Text = !battery.HasBattery
                ? "No battery found: this PC is always plugged in, so only the \"Plugged in\" mode is used."
                : battery.OnAcPower ? "Right now: plugged in" : "Right now: on battery";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            _updating = false;
        }
    }

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (_updating || sender is not RadioButton { Tag: string tag }) return;
        try
        {
            PowerModeService.Set(Enum.Parse<PowerMode>(tag));
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Could not change power mode",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        RefreshState();
    }

    private void UseBalancedPlan_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            PowerModeService.ActivateBalancedPlan();
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Power plan", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        RefreshState();
    }
}
