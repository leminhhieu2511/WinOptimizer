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

        // Stay in sync if the mode is changed from Windows Settings.
        _timer.Tick += (_, _) => RefreshState();
        PageActivity.Track(this,
            onActive: () =>
            {
                RefreshState();
                _timer.Start();
            },
            onInactive: () => _timer.Stop());
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
