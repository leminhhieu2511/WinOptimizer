using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using WinOptimizer.Helpers;

namespace WinOptimizer.Services;

public enum StartupSource
{
    RegistryUser,
    RegistryMachine,
    RegistryMachine32,
    FolderUser,
    FolderCommon,
}

public sealed class StartupItem : ObservableObject
{
    private bool _isEnabled;

    public required string Name { get; init; }
    public required string Command { get; init; }
    public required StartupSource Source { get; init; }

    /// <summary>Value name used under StartupApproved (registry value name or shortcut file name).</summary>
    public required string ApprovalName { get; init; }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public bool IsMachineWide => Source is StartupSource.RegistryMachine or StartupSource.RegistryMachine32 or StartupSource.FolderCommon;
    public bool CanChange => !IsMachineWide || AdminHelper.IsAdmin;

    public string SourceText => Source switch
    {
        StartupSource.RegistryUser => "Registry · current user",
        StartupSource.RegistryMachine => "Registry · all users",
        StartupSource.RegistryMachine32 => "Registry · all users (32-bit)",
        StartupSource.FolderUser => "Startup folder · current user",
        _ => "Startup folder · all users",
    };
}

/// <summary>
/// Lists and enables/disables startup apps the same way Task Manager does: entries stay in place,
/// and their state is stored under Explorer\StartupApproved.
/// </summary>
public static class StartupAppsService
{
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    public static List<StartupItem> Load()
    {
        var items = new List<StartupItem>();
        AddRegistryItems(items, RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64), StartupSource.RegistryUser);
        AddRegistryItems(items, RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64), StartupSource.RegistryMachine);
        AddRegistryItems(items, RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32), StartupSource.RegistryMachine32);
        AddFolderItems(items, Environment.GetFolderPath(Environment.SpecialFolder.Startup), StartupSource.FolderUser);
        AddFolderItems(items, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), StartupSource.FolderCommon);

        foreach (var item in items)
            item.IsEnabled = ReadEnabled(item);
        return items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static void AddRegistryItems(List<StartupItem> items, RegistryKey baseKey, StartupSource source)
    {
        using (baseKey)
        using (var run = baseKey.OpenSubKey(RunPath))
        {
            if (run is null) return;
            foreach (string name in run.GetValueNames())
            {
                if (string.IsNullOrEmpty(name) || run.GetValue(name) is not string command) continue;
                items.Add(new StartupItem { Name = name, Command = command, Source = source, ApprovalName = name });
            }
        }
    }

    private static void AddFolderItems(List<StartupItem> items, string folder, StartupSource source)
    {
        if (!Directory.Exists(folder)) return;
        foreach (string file in Directory.GetFiles(folder))
        {
            string fileName = Path.GetFileName(file);
            if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            items.Add(new StartupItem
            {
                Name = Path.GetFileNameWithoutExtension(file),
                Command = file,
                Source = source,
                ApprovalName = fileName,
            });
        }
    }

    private static (RegistryHive Hive, string Path) ApprovalKey(StartupSource source) => source switch
    {
        StartupSource.RegistryUser => (RegistryHive.CurrentUser, ApprovedPath + @"\Run"),
        StartupSource.RegistryMachine => (RegistryHive.LocalMachine, ApprovedPath + @"\Run"),
        StartupSource.RegistryMachine32 => (RegistryHive.LocalMachine, ApprovedPath + @"\Run32"),
        StartupSource.FolderUser => (RegistryHive.CurrentUser, ApprovedPath + @"\StartupFolder"),
        _ => (RegistryHive.LocalMachine, ApprovedPath + @"\StartupFolder"),
    };

    private static bool ReadEnabled(StartupItem item)
    {
        var (hive, path) = ApprovalKey(item.Source);
        // First byte: even (0x02/0x06) = enabled, odd (0x03/0x07) = disabled. Missing = enabled.
        return RegistryHelper.Read(hive, path, item.ApprovalName) is not byte[] { Length: > 0 } data || (data[0] & 1) == 0;
    }

    public static void SetEnabled(StartupItem item, bool enabled)
    {
        var data = new byte[12];
        data[0] = enabled ? (byte)0x02 : (byte)0x03;
        if (!enabled)
            BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4); // when it was disabled

        var (hive, path) = ApprovalKey(item.Source);
        RegistryHelper.Write(hive, path, item.ApprovalName, data, RegistryValueKind.Binary);
        item.IsEnabled = enabled;
    }

    /// <summary>Opens File Explorer with the startup program (or shortcut) selected.</summary>
    public static bool OpenLocation(StartupItem item)
    {
        string? path = item.Source is StartupSource.FolderUser or StartupSource.FolderCommon
            ? item.Command
            : ExtractExecutable(item.Command);
        if (path is null || !File.Exists(path)) return false;

        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        return true;
    }

    private static string? ExtractExecutable(string command)
    {
        command = Environment.ExpandEnvironmentVariables(command.Trim());
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }

        int exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? command[..(exe + 4)] : command.Split(' ')[0];
    }
}
