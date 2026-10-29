using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WinOptimizer.Services.DiskSpace;

/// <param name="RunChkdsk">False skips the (slow) read-only chkdsk; SourceChkdskExitCode then stays null.</param>
/// <param name="JournalPath">Path of the move journal; null means no journal can exist.</param>
public sealed record SystemFactsOptions(bool RunChkdsk = true, string? JournalPath = null);

/// <summary>
/// Gathers the machine facts the prechecks consume. Never throws for a missing permission: each fact is
/// probed on its own and falls back to its documented "unknown" value.
/// </summary>
public static class SystemFactsProbe
{
    private const string Unknown = "Unknown";
    private static readonly TimeSpan ChkdskTimeout = TimeSpan.FromMinutes(10);

    public static SystemFacts Collect(DiskSnapshot disk, PartitionSnapshot source, SystemFactsOptions? options = null)
    {
        options ??= new SystemFactsOptions();
        bool admin = Safe(() => AdminHelper.IsAdmin, false);

        var letters = disk.Partitions.Where(p => p.DriveLetter is not null).Select(p => p.DriveLetter!.Value)
            .Append(source.DriveLetter ?? '\0').Where(c => c != '\0').Select(char.ToUpperInvariant).ToList();
        var windows = Path.GetPathRoot(Environment.SystemDirectory);
        if (!string.IsNullOrEmpty(windows)) letters.Add(char.ToUpperInvariant(windows[0]));

        return new SystemFacts(
            admin,
            Safe(() => { var b = SystemInfoService.GetBattery(); return b.HasBattery && !b.OnAcPower; }, false),
            Safe(IsRebootPending, false),
            Safe(GetPagefiles, (IReadOnlyList<string>)[]),
            GetBitLocker(letters.Distinct(), admin),
            admin ? Safe(CountBcdOsLoaders, 0) : 0, // not admin: unknown, and the NotAdmin blocker applies anyway
            options.RunChkdsk && admin && source.DriveLetter is char l ? Safe(() => RunChkdsk(l), (int?)null) : null,
            Safe(() => GetMinSize(disk, source), (long?)null),
            admin ? Safe(() => GetLockHolders(source), (IReadOnlyList<LockHolder>)[]) : [],
            options.JournalPath is { Length: > 0 } jp && Safe(() => File.Exists(jp), false));
    }

    private static T Safe<T>(Func<T> f, T fallback)
    {
        try { return f(); }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException
                                       or Win32Exception or IOException or InvalidOperationException
                                       or System.Security.SecurityException or InvalidCastException
                                       or DllNotFoundException or EntryPointNotFoundException or NotSupportedException)
        {
            return fallback;
        }
    }

    private static bool IsRebootPending()
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        foreach (var key in new[]
                 {
                     @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending",
                     @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired",
                 })
        {
            using var k = hklm.OpenSubKey(key);
            if (k is not null) return true;
        }
        using var sm = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
        return sm?.GetValue("PendingFileRenameOperations") is string[] { Length: > 0 } ops && ops.Any(s => s.Length > 0);
    }

    private static IReadOnlyList<string> GetPagefiles()
    {
        var list = new List<string>();
        using var searcher = new ManagementObjectSearcher(@"root\cimv2", "SELECT Name FROM Win32_PageFileUsage");
        using var results = searcher.Get();
        foreach (var o in results)
            using (o)
                if (o["Name"] is string n && n.Length > 0) list.Add(n);
        return list;
    }

    /// <summary>What reading BitLocker state produced.</summary>
    public abstract record BitLockerOutcome
    {
        /// <summary>The WMI namespace or class does not exist (no BitLocker on this Windows edition).</summary>
        public sealed record NotPresent : BitLockerOutcome;
        /// <summary>Volumes the namespace listed, with their ConversionStatus.</summary>
        public sealed record Listed(IReadOnlyList<(char Letter, uint ConversionStatus)> Volumes) : BitLockerOutcome;
        /// <summary>Access denied or any other failure.</summary>
        public sealed record Failed : BitLockerOutcome;
    }

    public static IReadOnlyDictionary<char, string> MapBitLocker(IEnumerable<char> letters, BitLockerOutcome outcome)
    {
        var map = letters.ToDictionary(c => c, _ => Unknown);
        switch (outcome)
        {
            case BitLockerOutcome.NotPresent:
                // No BitLocker support (e.g. Windows Home): nothing can be encrypted, so every drive counts as decrypted.
                foreach (var c in map.Keys.ToList()) map[c] = "FullyDecrypted";
                break;
            case BitLockerOutcome.Listed listed:
                foreach (var (letter, status) in listed.Volumes)
                {
                    char c = char.ToUpperInvariant(letter);
                    if (!map.ContainsKey(c)) continue;
                    map[c] = status switch
                    {
                        0 => "FullyDecrypted",
                        1 => "FullyEncrypted",
                        2 => "EncryptionInProgress",
                        3 => "DecryptionInProgress",
                        4 => "EncryptionPaused",
                        5 => "DecryptionPaused",
                        _ => Unknown,
                    };
                }
                break;
        }
        return map; // Failed, or letters the namespace did not list: Unknown (blocks, the safe choice)
    }

    private static IReadOnlyDictionary<char, string> GetBitLocker(IEnumerable<char> letters, bool admin)
    {
        var all = letters.ToList();
        if (!admin) return MapBitLocker(all, new BitLockerOutcome.Failed()); // WMI BitLocker access needs admin
        try
        {
            var scope = new ManagementScope(@"root\cimv2\Security\MicrosoftVolumeEncryption");
            scope.Connect();
            var volumes = new List<(char, uint)>();
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM Win32_EncryptableVolume"));
            using var results = searcher.Get();
            foreach (var o in results)
            {
                using (o)
                {
                    if (o["DriveLetter"] is not string { Length: > 0 } dl) continue;
                    var inv = ((ManagementObject)o).InvokeMethod("GetConversionStatus", null, null);
                    if (inv is null || Convert.ToUInt32(inv["ReturnValue"]) != 0) continue;
                    volumes.Add((dl[0], Convert.ToUInt32(inv["ConversionStatus"])));
                }
            }
            return MapBitLocker(all, new BitLockerOutcome.Listed(volumes));
        }
        catch (ManagementException ex) when (ex.ErrorCode is ManagementStatus.InvalidNamespace
                                                 or ManagementStatus.InvalidClass or ManagementStatus.NotFound)
        {
            return MapBitLocker(all, new BitLockerOutcome.NotPresent());
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException
                                       or InvalidCastException or FormatException)
        {
            return MapBitLocker(all, new BitLockerOutcome.Failed());
        }
    }

    /// <summary>Counts the blocks of <c>bcdedit /enum osloader</c>. Each block starts with a title over a line of dashes.</summary>
    private static int CountBcdOsLoaders()
    {
        var psi = new ProcessStartInfo("bcdedit.exe", "/enum osloader")
        {
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("bcdedit did not start");
        var errTask = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(30_000)) { try { p.Kill(true); } catch (InvalidOperationException) { } return 0; }
        _ = errTask.Result;
        if (p.ExitCode != 0) return 0;
        return output.Split('\n').Count(line => line.Trim() is { Length: >= 5 } t && t.All(ch => ch == '-'));
    }

    /// <summary><c>chkdsk X:</c> without switches is an online read-only scan. Timeout kills it and counts as non-zero.</summary>
    private static int RunChkdsk(char letter)
    {
        var psi = new ProcessStartInfo("chkdsk.exe", $"{char.ToUpperInvariant(letter)}:")
        {
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("chkdsk did not start");
        _ = p.StandardOutput.ReadToEndAsync(); // drain so the child never blocks on a full pipe
        _ = p.StandardError.ReadToEndAsync();
        if (p.WaitForExit(ChkdskTimeout)) { p.WaitForExit(); return p.ExitCode; }
        try { p.Kill(true); } catch (InvalidOperationException) { }
        return -1;
    }

    private static long? GetMinSize(DiskSnapshot disk, PartitionSnapshot source)
    {
        var scope = new ManagementScope(@"root\Microsoft\Windows\Storage");
        scope.Connect();
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(
            $"SELECT * FROM MSFT_Partition WHERE DiskNumber = {disk.Number} AND PartitionNumber = {source.Number}"));
        using var results = searcher.Get();
        foreach (var o in results)
        {
            using (o)
            {
                // GetSupportedSize only reports the range; it changes nothing.
                var r = ((ManagementObject)o).InvokeMethod("GetSupportedSize", null, null);
                if (r is not null && Convert.ToUInt32(r["ReturnValue"]) == 0 && r["SizeMin"] is { } min)
                    return (long)Convert.ToUInt64(min);
            }
        }
        return null;
    }

    /// <summary>
    /// The volume lock attempt is the ground truth: lock + immediately close; OK means nobody holds it. Only when it
    /// reports DM_E_LOCKED is Restart Manager asked (best effort) for names. Without admin the lock cannot be tried,
    /// so the list stays empty (the NotAdmin blocker already stops the flow).
    /// </summary>
    private static IReadOnlyList<LockHolder> GetLockHolders(PartitionSnapshot source)
    {
        if (source.VolumePath is not { Length: > 0 } volume) return [];
        int status = DiskMoveNative.VolumeLock(volume, out var handle);
        if (handle != IntPtr.Zero) DiskMoveNative.VolumeClose(handle);
        if (status != DiskMoveNative.DM_E_LOCKED) return []; // OK = free; other errors are not "in use"

        var holders = RestartManagerHelper.GetHolders(source.DriveLetter is char c ? $"{c}:\\" : volume);
        return holders.Count > 0 ? holders : [new LockHolder("Unknown program or service", 0, false, false)];
    }
}
