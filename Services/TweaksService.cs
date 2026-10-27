using System.Runtime.InteropServices;
using Microsoft.Win32;
using WinOptimizer.Helpers;

namespace WinOptimizer.Services;

/// <summary>One registry value a tweak changes. <see cref="Off"/> = null means "delete the value" (Windows default).</summary>
public sealed record RegistrySetting(RegistryHive Hive, string Path, string Name, object On, object? Off,
    RegistryValueKind Kind = RegistryValueKind.DWord);

public sealed class Tweak : ObservableObject
{
    private bool _isApplied;

    public required string Category { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public bool RequiresAdmin { get; init; }
    public bool NeedsExplorerRestart { get; init; }
    public required Func<bool> Read { get; init; }
    public required Action<bool> Apply { get; init; }

    public bool CanChange => !RequiresAdmin || AdminHelper.IsAdmin;

    public string Badges => string.Join("   ",
        new[] { RequiresAdmin ? "Requires admin" : null, NeedsExplorerRestart ? "Restart Explorer to apply" : null }
            .Where(b => b is not null));

    public bool IsApplied
    {
        get => _isApplied;
        set => SetProperty(ref _isApplied, value);
    }

    public void Refresh()
    {
        try
        {
            IsApplied = Read();
        }
        catch
        {
            IsApplied = false;
        }
    }
}

public static class TweaksService
{
    private const string Cdm = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    private const string Advanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string ClassicMenuKey = @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";

    private const RegistryHive HKCU = RegistryHive.CurrentUser;
    private const RegistryHive HKLM = RegistryHive.LocalMachine;

    public static List<Tweak> CreateTweaks()
    {
        var tweaks = new List<Tweak>
        {
            // --- Privacy ---
            Registry("Privacy", "Turn off advertising ID",
                "Stops apps from using your advertising ID to show personalized ads.",
                Value(HKCU, @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0, 1)),
            Registry("Privacy", "Turn off tailored experiences",
                "Microsoft won't use your diagnostic data for personalized tips, ads and recommendations.",
                Value(HKCU, @"Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0, 1)),
            RegistryTweak("Privacy", "Minimize diagnostic data (telemetry)",
                "Sends only the minimum diagnostic data. On Home/Pro this means \"Required diagnostic data\".",
                admin: true,
                settings: [Value(HKLM, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0, null)]),
            RegistryTweak("Privacy", "Turn off activity history",
                "Stops Windows from collecting and uploading the apps and files you use.",
                admin: true,
                settings:
                [
                    Value(HKLM, @"SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 0, null),
                    Value(HKLM, @"SOFTWARE\Policies\Microsoft\Windows\System", "UploadUserActivities", 0, null),
                ]),
            Registry("Privacy", "Turn off app launch tracking",
                "Windows won't track which apps you open to improve Start and search.",
                Value(HKCU, Advanced, "Start_TrackProgs", 0, 1)),
            Registry("Privacy", "Turn off feedback requests",
                "Stops Windows from asking for feedback.",
                Value(HKCU, @"Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 0, null)),

            // --- Ads & suggestions ---
            Registry("Ads & suggestions", "Turn off Start menu suggestions",
                "Removes promoted apps and suggestions from Start.",
                Value(HKCU, Cdm, "SystemPaneSuggestionsEnabled", 0, 1),
                Value(HKCU, Cdm, "SubscribedContent-338388Enabled", 0, 1)),
            Registry("Ads & suggestions", "Turn off tips and tricks notifications",
                "Stops \"Get tips and suggestions when using Windows\" notifications.",
                Value(HKCU, Cdm, "SubscribedContent-338389Enabled", 0, 1),
                Value(HKCU, Cdm, "SoftLandingEnabled", 0, 1)),
            Registry("Ads & suggestions", "Turn off lock screen fun facts and ads",
                "Removes tips, tricks and ads from the lock screen.",
                Value(HKCU, Cdm, "RotatingLockScreenOverlayEnabled", 0, 1),
                Value(HKCU, Cdm, "SubscribedContent-338387Enabled", 0, 1)),
            Registry("Ads & suggestions", "Turn off suggested content in Settings",
                "Removes suggestions shown inside the Settings app.",
                Value(HKCU, Cdm, "SubscribedContent-338393Enabled", 0, 1),
                Value(HKCU, Cdm, "SubscribedContent-353694Enabled", 0, 1),
                Value(HKCU, Cdm, "SubscribedContent-353696Enabled", 0, 1)),
            Registry("Ads & suggestions", "Turn off automatic install of suggested apps",
                "Stops Windows from silently installing promoted apps.",
                Value(HKCU, Cdm, "SilentInstalledAppsEnabled", 0, 1),
                Value(HKCU, Cdm, "PreInstalledAppsEnabled", 0, 1),
                Value(HKCU, Cdm, "OemPreInstalledAppsEnabled", 0, 1)),
            RegistryTweak("Ads & suggestions", "Turn off web results in Start search",
                "Start search shows only local results (no Bing).",
                restart: true,
                settings: [Value(HKCU, @"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1, null)]),

            // --- Performance & gaming ---
            Registry("Performance & gaming", "Game Mode",
                "Prioritizes games and pauses background activity while you play.",
                Value(HKCU, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1, 0)),
            Registry("Performance & gaming", "Turn off background game recording",
                "Disables Game DVR / Captures recording in the background, which costs performance.",
                Value(HKCU, @"System\GameConfigStore", "GameDVR_Enabled", 0, 1),
                Value(HKCU, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0, 1)),
            Registry("Performance & gaming", "Remove startup app delay",
                "Startup apps launch right away after sign-in instead of waiting.",
                Value(HKCU, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", "StartupDelayInMSec", 0, null)),
            Registry("Performance & gaming", "Faster menus",
                "Menus open after 100 ms instead of 400 ms. Takes effect after you sign out.",
                Value(HKCU, @"Control Panel\Desktop", "MenuShowDelay", "100", "400", RegistryValueKind.String)),
            Registry("Performance & gaming", "Turn off transparency effects",
                "Makes windows and the taskbar opaque. Saves a little GPU power.",
                Value(HKCU, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 0, 1)),
            new Tweak
            {
                Category = "Performance & gaming",
                Name = "Turn off animation effects",
                Description = "Windows and controls appear instantly instead of animating.",
                Read = () => !GetAnimationsEnabled(),
                Apply = on => SetAnimationsEnabled(!on),
            },

            // --- File Explorer ---
            Registry("File Explorer", "Show file name extensions",
                "Shows extensions like .exe and .pdf, which helps you spot fake files.",
                Value(HKCU, Advanced, "HideFileExt", 0, 1)),
            Registry("File Explorer", "Show hidden files and folders",
                "Shows files and folders marked as hidden.",
                Value(HKCU, Advanced, "Hidden", 1, 2)),
            Registry("File Explorer", "Open File Explorer to This PC",
                "File Explorer opens to This PC instead of Home.",
                Value(HKCU, Advanced, "LaunchTo", 1, null)),
            new Tweak
            {
                Category = "File Explorer",
                Name = "Classic right-click menu",
                Description = "Shows the full Windows 10 style context menu without needing \"Show more options\".",
                NeedsExplorerRestart = true,
                Read = () =>
                {
                    using var key = RegistryHelper.OpenBase(HKCU).OpenSubKey(ClassicMenuKey + @"\InprocServer32");
                    return key is not null;
                },
                Apply = on =>
                {
                    using var root = RegistryHelper.OpenBase(HKCU);
                    if (on)
                    {
                        using var key = root.CreateSubKey(ClassicMenuKey + @"\InprocServer32", writable: true);
                        key.SetValue("", "");
                    }
                    else
                    {
                        root.DeleteSubKeyTree(ClassicMenuKey, throwOnMissingSubKey: false);
                    }
                },
            },
        };

        foreach (var tweak in tweaks) tweak.Refresh();
        return tweaks;
    }

    private static RegistrySetting Value(RegistryHive hive, string path, string name, object on, object? off,
        RegistryValueKind kind = RegistryValueKind.DWord) => new(hive, path, name, on, off, kind);

    private static Tweak Registry(string category, string name, string description, params RegistrySetting[] settings) =>
        RegistryTweak(category, name, description, admin: false, restart: false, settings: settings);

    private static Tweak RegistryTweak(string category, string name, string description,
        bool admin = false, bool restart = false, params RegistrySetting[] settings) => new()
    {
        Category = category,
        Name = name,
        Description = description,
        RequiresAdmin = admin,
        NeedsExplorerRestart = restart,
        Read = () => settings.All(s => Equals(RegistryHelper.Read(s.Hive, s.Path, s.Name)?.ToString(), s.On.ToString())),
        Apply = on =>
        {
            foreach (var s in settings)
            {
                object? value = on ? s.On : s.Off;
                if (value is null)
                    RegistryHelper.DeleteValue(s.Hive, s.Path, s.Name);
                else
                    RegistryHelper.Write(s.Hive, s.Path, s.Name, value, s.Kind);
            }
            ExplorerHelper.BroadcastSettingChange("ImmersiveColorSet");
            ExplorerHelper.BroadcastSettingChange("TraySettings");
        },
    };

    // --- Animation effects (Settings > Accessibility > Visual effects) ---

    private const uint SPI_GETCLIENTAREAANIMATION = 0x1042;
    private const uint SPI_SETCLIENTAREAANIMATION = 0x1043;
    private const uint SPIF_UPDATEINIFILE = 0x1;
    private const uint SPIF_SENDCHANGE = 0x2;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref bool value, uint winIni);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, IntPtr value, uint winIni);

    private static bool GetAnimationsEnabled()
    {
        bool enabled = true;
        SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, ref enabled, 0);
        return enabled;
    }

    private static void SetAnimationsEnabled(bool enabled) =>
        SystemParametersInfo(SPI_SETCLIENTAREAANIMATION, 0, enabled ? 1 : 0, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
}
