using System.Windows;
using Microsoft.Win32;
using WinOptimizer.Services;

namespace WinOptimizer.SideTaskbar;

/// <summary>Creates, moves and removes the vertical taskbars to match the saved settings.</summary>
public static class SideTaskbarManager
{
    private static readonly Dictionary<string, SideTaskbarWindow> Bars = new();
    private static bool _watchingDisplays;

    public static bool AnyActive => Bars.Count > 0;

    /// <summary>Raised after bars are added or removed (the Taskbar page refreshes its controls).</summary>
    public static event Action? Changed;

    public static SideEdge? GetEdge(string device) =>
        SettingsStore.Current.SideTaskbars.FirstOrDefault(c => c.Device == device)?.Edge;

    /// <summary>Turns the vertical taskbar on a monitor on (Left/Right) or off (null) and saves it.</summary>
    public static void Set(string device, SideEdge? edge)
    {
        var configs = SettingsStore.Current.SideTaskbars;
        configs.RemoveAll(c => c.Device == device);
        if (edge is { } e) configs.Add(new SideTaskbarConfig { Device = device, Edge = e });
        SettingsStore.Save();
        Apply();
    }

    /// <summary>Makes the open bars match the settings and the monitors that are connected right now.</summary>
    public static void Apply()
    {
        if (!_watchingDisplays)
        {
            SystemEvents.DisplaySettingsChanged += (_, _) => Application.Current.Dispatcher.BeginInvoke(Apply);
            _watchingDisplays = true;
        }

        var connected = TaskbarService.GetDisplays().Select(d => d.DeviceName).ToHashSet();
        var wanted = SettingsStore.Current.SideTaskbars
            .Where(c => connected.Contains(c.Device))
            .ToDictionary(c => c.Device, c => c.Edge);

        foreach (var (device, bar) in Bars.ToList())
        {
            if (wanted.TryGetValue(device, out var edge) && edge == bar.Edge) continue;
            bar.Close();
            Bars.Remove(device);
        }

        foreach (var (device, edge) in wanted)
        {
            if (Bars.ContainsKey(device)) continue;
            var bar = new SideTaskbarWindow(device, edge);
            Bars[device] = bar;
            bar.Show();
        }

        foreach (var bar in Bars.Values) bar.Reposition();
        Changed?.Invoke();
    }

    /// <summary>Closes all bars without changing the saved settings (used when the app exits).</summary>
    public static void CloseAll()
    {
        foreach (var bar in Bars.Values) bar.Close();
        Bars.Clear();
    }
}
