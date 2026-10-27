using Microsoft.Win32;

namespace WinOptimizer.Services;

public static class RegistryHelper
{
    public static RegistryKey OpenBase(RegistryHive hive) => RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);

    public static object? Read(RegistryHive hive, string path, string name)
    {
        using var key = OpenBase(hive).OpenSubKey(path);
        return key?.GetValue(name);
    }

    public static int ReadInt(RegistryHive hive, string path, string name, int defaultValue) =>
        Read(hive, path, name) is int value ? value : defaultValue;

    public static void Write(RegistryHive hive, string path, string name, object value, RegistryValueKind kind)
    {
        using var key = OpenBase(hive).CreateSubKey(path, writable: true);
        key.SetValue(name, value, kind);
    }

    public static void WriteDword(RegistryHive hive, string path, string name, int value) =>
        Write(hive, path, name, value, RegistryValueKind.DWord);

    public static void DeleteValue(RegistryHive hive, string path, string name)
    {
        using var key = OpenBase(hive).OpenSubKey(path, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}
