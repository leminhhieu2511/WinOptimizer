using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WinOptimizer.Helpers;
using WinOptimizer.Services;

namespace WinOptimizer.Pages;

/// <summary>
/// Overview page. Kept deliberately light: only CPU, memory and battery refresh on a timer (well under 1 ms each);
/// drives and tool summaries are read once each time the page is shown, on a background thread, and the junk
/// scan never runs from here.
/// </summary>
public partial class HomePage : UserControl
{
    private readonly SystemInfoService _service = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _refreshing;

    public HomePage()
    {
        InitializeComponent();
        CpuDetailText.Text = $"{Environment.ProcessorCount} logical processors";

        _timer.Tick += async (_, _) => await RefreshLiveAsync();
        PageActivity.Track(this,
            onActive: async () =>
            {
                UpdateGreeting();
                _timer.Start();
                AutoProfileService.PowerStateChanged += OnPowerStateChanged;
                await RefreshLiveAsync();
                await RefreshSummariesAsync();
            },
            onInactive: () =>
            {
                _timer.Stop();
                AutoProfileService.PowerStateChanged -= OnPowerStateChanged;
            });
    }

    /// <summary>Plugged in/unplugged or the power mode changed (here or in Windows): update tiles and buttons now.</summary>
    private async void OnPowerStateChanged()
    {
        await RefreshLiveAsync();
        await RefreshPowerAsync();
    }

    private void UpdateGreeting()
    {
        int hour = DateTime.Now.Hour;
        string part = hour < 12 ? "morning" : hour < 18 ? "afternoon" : "evening";
        GreetingText.Text = $"Good {part}, {Environment.UserName}";
    }

    /// <summary>Cheap values that change all the time.</summary>
    private async Task RefreshLiveAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var s = await Task.Run(() => _service.Sample(includeDisks: false, includeProcesses: false));

            CpuText.Text = $"{s.CpuPercent:0}%";
            MemoryText.Text = $"{s.MemoryPercent:0}%";
            MemoryDetailText.Text = $"{Format.Bytes(s.MemoryUsed)} of {Format.Bytes(s.MemoryTotal)}";

            if (s.Battery.HasBattery)
            {
                BatteryText.Text = $"{s.Battery.Percent}%";
                BatteryDetailText.Text = s.Battery.Charging ? "Charging" : s.Battery.OnAcPower ? "Plugged in" : "On battery";
            }
            else
            {
                BatteryText.Text = "AC";
                BatteryDetailText.Text = "No battery";
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    private sealed record Summaries(
        string Os, TimeSpan Uptime, DiskUsage? SystemDrive, PowerMode? Mode, string Plan,
        int StartupTotal, int StartupEnabled, int TweaksTotal, int TweaksOn, int Displays);

    /// <summary>Things that rarely change. Read in one background pass each time the page is shown.</summary>
    private async Task RefreshSummariesAsync()
    {
        var s = await Task.Run(() =>
        {
            string systemRoot = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";
            var drive = SystemInfoService.GetDisks().FirstOrDefault(d => d.Name.Equals(systemRoot, StringComparison.OrdinalIgnoreCase));
            PowerMode? mode = null;
            try { mode = PowerModeService.GetCurrent(); } catch { }
            var startup = StartupAppsService.Load();
            var tweaks = TweaksService.CreateTweaks();
            return new Summaries(
                SystemInfoService.GetOsName(), TimeSpan.FromMilliseconds(Environment.TickCount64), drive,
                mode, PowerModeService.GetActivePlan().Name,
                startup.Count, startup.Count(i => i.IsEnabled),
                tweaks.Count, tweaks.Count(t => t.IsApplied),
                TaskbarService.GetDisplays().Count);
        });

        SubtitleText.Text = $"{Environment.MachineName}  ·  {s.Os}  ·  up {Format.Duration(s.Uptime)}";

        if (s.SystemDrive is { } d)
        {
            StorageLabel.Text = $"Storage ({d.Name})";
            StorageText.Text = Format.Bytes(d.FreeBytes);
            StorageDetailText.Text = $"free of {Format.Bytes(d.TotalBytes)}";
            StorageBar.Value = d.UsedPercent;
        }

        UpdatePowerButtons(s.Mode, s.Plan);

        JunkStatusText.Text = JunkCleanerService.LastScanBytes is { } junk
            ? $"Last scan found {Format.Bytes(junk)} you can free up"
            : "Find temporary and cache files you can safely remove";
        StartupStatusText.Text = $"{s.StartupEnabled} of {s.StartupTotal} apps start with Windows";
        TweaksStatusText.Text = $"{s.TweaksOn} of {s.TweaksTotal} tweaks on";
        TaskbarStatusText.Text = s.Displays == 1 ? "1 display connected" : $"{s.Displays} displays connected";
    }

    private void UpdatePowerButtons(PowerMode? current, string plan)
    {
        var accent = TryFindResource("AccentButtonStyle") as Style;
        foreach (var (button, mode) in new[]
                 {
                     (SaverButton, PowerMode.BatterySaver),
                     (BalancedButton, PowerMode.Balanced),
                     (PerformanceButton, PowerMode.HighPerformance),
                 })
        {
            // ClearValue (not Style = null) so the theme's normal button style applies again.
            if (mode == current && accent is not null) button.Style = accent;
            else button.ClearValue(StyleProperty);
        }

        PowerTitleText.Text = current is { } m ? PowerModeService.DisplayName(m) : "Unknown";
        PowerDetailText.Text = $"Power plan: {plan}";
    }

    private async void PowerMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        try
        {
            PowerModeService.Set(Enum.Parse<PowerMode>(tag));
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Could not change power mode",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        await RefreshPowerAsync();
    }

    private async Task RefreshPowerAsync()
    {
        var (mode, plan) = await Task.Run(() =>
        {
            PowerMode? m = null;
            try { m = PowerModeService.GetCurrent(); } catch { }
            return (m, PowerModeService.GetActivePlan().Name);
        });
        UpdatePowerButtons(mode, plan);
    }

    private void Navigate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string page } && Window.GetWindow(this) is MainWindow window)
            window.NavigateTo(page);
    }
}
