using System.Windows.Threading;
using Microsoft.Win32;

namespace WinOptimizer.Services;

/// <summary>
/// Switches the Windows power mode automatically when the charger is plugged in or unplugged
/// (one mode for "Plugged in", one for "On battery").
/// <para>
/// Windows reports power-status changes many times a minute (every battery percent counts), so a change only
/// applies a profile when the power source itself changed, and only after it has stayed that way for a short
/// moment, so a flaky charger connection doesn't flip modes back and forth. A mode the user picks by hand stays
/// until the next plug/unplug.
/// </para>
/// </summary>
public static class AutoProfileService
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);

    private static DispatcherTimer? _timer;
    private static bool? _lastOnAc;

    /// <summary>
    /// Raised on the UI thread when the charger was plugged in or unplugged (after any profile was applied) and
    /// whenever the power mode changes, from this app or from Windows, so open pages can show it right away.
    /// </summary>
    public static event Action? PowerStateChanged;

    public static bool IsOnAc => SystemInfoService.GetBattery().OnAcPower;

    /// <summary>Starts listening. Call once from the UI thread at startup.</summary>
    public static void Initialize()
    {
        if (_timer is not null) return;

        _timer = new DispatcherTimer { Interval = Debounce };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            ApplyIfSourceChanged();
        };
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        var dispatcher = _timer.Dispatcher;
        PowerModeService.StartWatching(() => dispatcher.BeginInvoke(() => PowerStateChanged?.Invoke()));

        // Bring the mode in line with the current power source once at startup.
        if (SettingsStore.Current.AutoProfilesEnabled) ApplyNow(notify: false);
        else _lastOnAc = IsOnAc;
    }

    public static void Shutdown()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        PowerModeService.StopWatching();
        _timer?.Stop();
        _timer = null;
    }

    /// <summary>Applies the profile for the current power source right away (e.g. after settings change).</summary>
    public static void ApplyNow(bool notify = true)
    {
        _lastOnAc = IsOnAc;
        if (SettingsStore.Current.AutoProfilesEnabled) Apply(_lastOnAc.Value, notify);
    }

    // Raised on SystemEvents' own thread.
    private static void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode is not (PowerModes.StatusChange or PowerModes.Resume)) return;
        _timer?.Dispatcher.BeginInvoke(() =>
        {
            if (_timer is null) return;
            _timer.Stop();
            _timer.Start(); // restart the debounce window
        });
    }

    private static void ApplyIfSourceChanged()
    {
        bool onAc = IsOnAc;
        if (onAc == _lastOnAc) return; // only the battery percentage changed
        _lastOnAc = onAc;
        if (SettingsStore.Current.AutoProfilesEnabled) Apply(onAc, notify: true);
        PowerStateChanged?.Invoke();
    }

    private static void Apply(bool onAc, bool notify)
    {
        var settings = SettingsStore.Current;
        var mode = onAc ? settings.AcPowerMode : settings.BatteryPowerMode;
        try
        {
            // Windows only lets the power mode change on the Balanced plan.
            if (settings.AutoProfilesSwitchPlan && PowerModeService.GetActivePlan().Id != PowerModeService.BalancedPlan)
                PowerModeService.ActivateBalancedPlan();

            if (PowerModeService.GetCurrent() != mode)
                PowerModeService.Set(mode);
        }
        catch (InvalidOperationException)
        {
            // Plan locked (and we may not switch it): leave the mode as it is. The Power page shows why.
            return;
        }

        if (notify && settings.AutoProfilesNotify)
            TrayIcon.ShowBalloon(onAc ? "Plugged in" : "On battery", $"Power mode: {PowerModeService.DisplayName(mode)}");
    }
}
