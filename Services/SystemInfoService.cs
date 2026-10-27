using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WinOptimizer.Services;

public sealed record DiskUsage(string Name, string Label, long TotalBytes, long FreeBytes)
{
    public long UsedBytes => TotalBytes - FreeBytes;
    public double UsedPercent => TotalBytes == 0 ? 0 : UsedBytes * 100.0 / TotalBytes;
}

public sealed record ProcessUsage(string Name, int Id, double CpuPercent, long MemoryBytes);

public sealed record BatteryInfo(bool HasBattery, int Percent, bool OnAcPower, bool Charging, TimeSpan? Remaining);

public sealed record SystemSnapshot(
    double CpuPercent,
    ulong MemoryTotal,
    ulong MemoryAvailable,
    BatteryInfo Battery,
    IReadOnlyList<DiskUsage>? Disks,          // null when not requested this time
    IReadOnlyList<ProcessUsage>? TopProcesses, // null when not requested this time
    TimeSpan Uptime)
{
    public ulong MemoryUsed => MemoryTotal - MemoryAvailable;
    public double MemoryPercent => MemoryTotal == 0 ? 0 : MemoryUsed * 100.0 / MemoryTotal;
}

/// <summary>Samples live system usage. Call <see cref="Sample"/> from one thread at a time.</summary>
public sealed class SystemInfoService
{
    private ulong _prevIdle, _prevKernel, _prevUser;
    private Dictionary<int, TimeSpan> _prevProcessTimes = new();
    private DateTime _prevProcessSample = DateTime.UtcNow;

    /// <summary>
    /// CPU, memory and battery are cheap (well under 1 ms). Drives and especially the process list cost more,
    /// so callers ask for them less often.
    /// </summary>
    public SystemSnapshot Sample(bool includeDisks = true, bool includeProcesses = true, int topProcessCount = 12)
    {
        double cpu = SampleCpu();
        var (total, available) = GetMemory();
        return new SystemSnapshot(cpu, total, available, GetBattery(),
            includeDisks ? GetDisks() : null,
            includeProcesses ? SampleProcesses(topProcessCount) : null,
            TimeSpan.FromMilliseconds(Environment.TickCount64));
    }

    private double SampleCpu()
    {
        if (!GetSystemTimes(out var idleFt, out var kernelFt, out var userFt)) return 0;
        ulong idle = ToUInt64(idleFt), kernel = ToUInt64(kernelFt), user = ToUInt64(userFt);

        ulong idleDelta = idle - _prevIdle;
        ulong totalDelta = (kernel - _prevKernel) + (user - _prevUser); // kernel time includes idle time
        bool first = _prevKernel == 0;
        (_prevIdle, _prevKernel, _prevUser) = (idle, kernel, user);

        if (first || totalDelta == 0) return 0;
        return Math.Clamp((totalDelta - idleDelta) * 100.0 / totalDelta, 0, 100);
    }

    private IReadOnlyList<ProcessUsage> SampleProcesses(int count)
    {
        var now = DateTime.UtcNow;
        double elapsedMs = Math.Max(1, (now - _prevProcessSample).TotalMilliseconds) * Environment.ProcessorCount;
        var times = new Dictionary<int, TimeSpan>();
        var result = new List<ProcessUsage>();

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id == 0) continue; // System Idle Process
                double cpu = 0;
                try
                {
                    var time = process.TotalProcessorTime;
                    times[process.Id] = time;
                    if (_prevProcessTimes.TryGetValue(process.Id, out var prev))
                        cpu = Math.Clamp((time - prev).TotalMilliseconds * 100.0 / elapsedMs, 0, 100);
                }
                catch
                {
                    // Access denied for protected/system processes.
                }

                try
                {
                    result.Add(new ProcessUsage(process.ProcessName, process.Id, cpu, process.WorkingSet64));
                }
                catch
                {
                    // Process exited while sampling.
                }
            }
        }

        _prevProcessTimes = times;
        _prevProcessSample = now;
        return result
            .OrderByDescending(p => p.CpuPercent)
            .ThenByDescending(p => p.MemoryBytes)
            .Take(count)
            .ToList();
    }

    private static (ulong Total, ulong Available) GetMemory()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref status) ? (status.ullTotalPhys, status.ullAvailPhys) : (0, 0);
    }

    public static BatteryInfo GetBattery()
    {
        if (!GetSystemPowerStatus(out var s)) return new BatteryInfo(false, 0, true, false, null);
        bool hasBattery = (s.BatteryFlag & 128) == 0 && s.BatteryFlag != 255;
        TimeSpan? remaining = s.BatteryLifeTime == -1 ? null : TimeSpan.FromSeconds(s.BatteryLifeTime);
        return new BatteryInfo(hasBattery, s.BatteryLifePercent == 255 ? 0 : s.BatteryLifePercent,
            s.ACLineStatus == 1, (s.BatteryFlag & 8) != 0, remaining);
    }

    public static IReadOnlyList<DiskUsage> GetDisks()
    {
        var disks = new List<DiskUsage>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                string label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Local Disk" : drive.VolumeLabel;
                disks.Add(new DiskUsage(drive.Name.TrimEnd('\\'), label, drive.TotalSize, drive.AvailableFreeSpace));
            }
            catch (IOException)
            {
                // Drive went away.
            }
        }
        return disks;
    }

    public static string GetOsName()
    {
        const string path = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        string product = RegistryHelper.Read(RegistryHive.LocalMachine, path, "ProductName") as string ?? "Windows";
        string version = RegistryHelper.Read(RegistryHive.LocalMachine, path, "DisplayVersion") as string ?? "";
        string build = RegistryHelper.Read(RegistryHive.LocalMachine, path, "CurrentBuild") as string ?? "";

        // ProductName still says "Windows 10" on Windows 11.
        if (int.TryParse(build, out int buildNumber) && buildNumber >= 22000)
            product = product.Replace("Windows 10", "Windows 11");
        return $"{product} {version} (build {build})".Trim();
    }

    public static string GetCpuName() =>
        (RegistryHelper.Read(RegistryHive.LocalMachine, @"HARDWARE\DESCRIPTION\System\CentralProcessor\0",
            "ProcessorNameString") as string)?.Trim() ?? "Unknown processor";

    private static ulong ToUInt64(FILETIME ft) => ((ulong)ft.dwHighDateTime << 32) | ft.dwLowDateTime;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
}
