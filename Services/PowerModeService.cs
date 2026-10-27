using System.Runtime.InteropServices;

namespace WinOptimizer.Services;

public enum PowerMode
{
    BatterySaver,
    Balanced,
    HighPerformance,
}

/// <summary>
/// Reads and sets the Windows 10/11 "Power mode" (Settings > System > Power &amp; battery)
/// via the power overlay scheme APIs in powrprof.dll. No admin rights required.
/// </summary>
public static class PowerModeService
{
    // Overlay GUIDs used by the Windows "Power mode" slider.
    private static readonly Guid BestPowerEfficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    private static readonly Guid BalancedOverlay = Guid.Empty;
    private static readonly Guid BestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    public static readonly Guid BalancedPlan = new("381b4222-f694-41f0-9685-ff5bb260df2e");

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveOverlayScheme(Guid overlaySchemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetEffectiveOverlayScheme(out Guid effectiveOverlayGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadFriendlyName(IntPtr rootPowerKey, ref Guid schemeGuid, IntPtr subGroupGuid,
        IntPtr powerSettingGuid, byte[]? buffer, ref uint bufferSize);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private delegate void EffectivePowerModeCallback(int mode, IntPtr context);

    private const uint EffectivePowerModeV2 = 2;

    [DllImport("powrprof.dll")]
    private static extern int PowerRegisterForEffectivePowerModeNotifications(uint version,
        EffectivePowerModeCallback callback, IntPtr context, out IntPtr registrationHandle);

    [DllImport("powrprof.dll")]
    private static extern int PowerUnregisterFromEffectivePowerModeNotifications(IntPtr registrationHandle);

    // Kept in a field so the garbage collector doesn't free the delegate Windows calls back into.
    private static EffectivePowerModeCallback? _watchCallback;
    private static IntPtr _watchHandle;

    /// <summary>
    /// Calls <paramref name="onChanged"/> (on a thread-pool thread) whenever the effective power mode changes,
    /// whoever changed it: this app, Windows Settings, Quick Settings or the battery flyout. Windows also calls it
    /// once right after registering. Needs Windows 10 1809 or later; does nothing on older versions.
    /// </summary>
    public static void StartWatching(Action onChanged)
    {
        if (_watchHandle != IntPtr.Zero) return;
        _watchCallback = (_, _) => onChanged();
        try
        {
            if (PowerRegisterForEffectivePowerModeNotifications(EffectivePowerModeV2, _watchCallback, IntPtr.Zero, out _watchHandle) != 0)
                _watchHandle = IntPtr.Zero;
        }
        catch (EntryPointNotFoundException)
        {
            _watchHandle = IntPtr.Zero;
        }
    }

    public static void StopWatching()
    {
        if (_watchHandle == IntPtr.Zero) return;
        PowerUnregisterFromEffectivePowerModeNotifications(_watchHandle);
        _watchHandle = IntPtr.Zero;
        _watchCallback = null;
    }

    public static PowerMode GetCurrent()
    {
        uint result = PowerGetEffectiveOverlayScheme(out Guid guid);
        if (result != 0)
            throw new InvalidOperationException($"PowerGetEffectiveOverlayScheme failed (error {result}).");

        if (guid == BestPowerEfficiency) return PowerMode.BatterySaver;
        if (guid == BestPerformance) return PowerMode.HighPerformance;
        return PowerMode.Balanced;
    }

    public static void Set(PowerMode mode)
    {
        Guid guid = mode switch
        {
            PowerMode.BatterySaver => BestPowerEfficiency,
            PowerMode.HighPerformance => BestPerformance,
            _ => BalancedOverlay,
        };

        uint result = PowerSetActiveOverlayScheme(guid);
        if (result != 0)
            throw new InvalidOperationException(
                $"PowerSetActiveOverlayScheme failed (error {result}). " +
                "Power mode can only be changed while the Balanced power plan is active.");
    }

    public static (Guid Id, string Name) GetActivePlan()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out IntPtr ptr) != 0)
            return (Guid.Empty, "Unknown");

        Guid id = Marshal.PtrToStructure<Guid>(ptr);
        LocalFree(ptr);

        uint size = 0;
        PowerReadFriendlyName(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, null, ref size);
        var buffer = new byte[size];
        string name = PowerReadFriendlyName(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, buffer, ref size) == 0
            ? System.Text.Encoding.Unicode.GetString(buffer).TrimEnd('\0')
            : id.ToString();
        return (id, name);
    }

    public static void ActivateBalancedPlan()
    {
        Guid plan = BalancedPlan;
        uint result = PowerSetActiveScheme(IntPtr.Zero, ref plan);
        if (result != 0)
            throw new InvalidOperationException($"Could not activate the Balanced plan (error {result}).");
    }

    public static string DisplayName(PowerMode mode) => mode switch
    {
        PowerMode.BatterySaver => "Battery Life Saver",
        PowerMode.HighPerformance => "Highest Performance",
        _ => "Balanced",
    };

    public static bool TryParse(string text, out PowerMode mode)
    {
        PowerMode? parsed = text.ToLowerInvariant() switch
        {
            "saver" or "battery" => PowerMode.BatterySaver,
            "balanced" or "balance" => PowerMode.Balanced,
            "performance" or "high" => PowerMode.HighPerformance,
            _ => null,
        };
        mode = parsed ?? PowerMode.Balanced;
        return parsed.HasValue;
    }
}
