using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinOptimizer.Services;

public enum SideEdge
{
    Left,
    Right,
}

/// <summary>A WinOptimizer vertical taskbar on one monitor (identified by its device name, e.g. \\.\DISPLAY2).</summary>
public sealed class SideTaskbarConfig
{
    public string Device { get; set; } = "";
    public SideEdge Edge { get; set; }
}

public sealed class AppSettings
{
    public List<SideTaskbarConfig> SideTaskbars { get; set; } = [];

    /// <summary>Displays (device names) whose Windows taskbar WinOptimizer auto-hides.</summary>
    public List<string> HiddenTaskbars { get; set; } = [];

    /// <summary>True while a Windows taskbar may be hidden; if set at startup, the app was killed and restores them.</summary>
    public bool TaskbarHidingDirty { get; set; }

    /// <summary>Whether we already explained that closing the window keeps the app running in the tray.</summary>
    public bool SeenBackgroundNotice { get; set; }

    /// <summary>Closing the main window hides it to the notification area instead of exiting.</summary>
    public bool MinimizeToTrayOnClose { get; set; } = true;

    /// <summary>Switch power mode automatically when the charger is plugged in or unplugged.</summary>
    public bool AutoProfilesEnabled { get; set; }

    public PowerMode AcPowerMode { get; set; } = PowerMode.HighPerformance;

    public PowerMode BatteryPowerMode { get; set; } = PowerMode.BatterySaver;

    /// <summary>Activate the Balanced plan first when another plan would block the power mode change.</summary>
    public bool AutoProfilesSwitchPlan { get; set; } = true;

    /// <summary>Show a tray notification each time a profile is applied.</summary>
    public bool AutoProfilesNotify { get; set; } = true;
}

/// <summary>Saves app settings to %LocalAppData%\WinOptimizer\settings.json.</summary>
public static class SettingsStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinOptimizer", "settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Current { get; } = Load();

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Unreadable settings: start fresh rather than fail to open.
        }
        return new AppSettings();
    }

    public static void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, Options));
    }
}
