using System.Management;
using System.Runtime.InteropServices;

namespace WinOptimizer.Services.DiskSpace;

/// <summary>
/// Time estimate of a move. Every shifted partition is copied whole and read back after each block, so the time is
/// <c>BytesToCopy × 2 / speed</c>, with 400 MB/s for an SSD and 80 MB/s for an HDD (MSFT_PhysicalDisk.MediaType
/// 4 = SSD, 3 = HDD; anything else is treated as an HDD, the slow and therefore safe guess).
/// </summary>
public static class DiskMoveEstimate
{
    public const int MediaTypeHdd = 3, MediaTypeSsd = 4;
    public const long SsdBytesPerSecond = 400L * 1000 * 1000, HddBytesPerSecond = 80L * 1000 * 1000;

    public static long BytesPerSecond(int? mediaType) => mediaType == MediaTypeSsd ? SsdBytesPerSecond : HddBytesPerSecond;

    public static TimeSpan Duration(long bytesToCopy, int? mediaType) =>
        TimeSpan.FromSeconds(bytesToCopy * 2.0 / BytesPerSecond(mediaType));

    /// <summary>"~N min", rounded up, at least 1.</summary>
    public static string FormatMinutes(TimeSpan duration) => $"~{Math.Max(1, (int)Math.Ceiling(duration.TotalMinutes))} min";

    public static string MediaName(int? mediaType) => mediaType switch
    {
        MediaTypeSsd => "SSD",
        MediaTypeHdd => "HDD",
        _ => "unknown drive type (estimated as HDD)",
    };

    /// <summary>MSFT_PhysicalDisk.MediaType of disk <paramref name="diskNumber"/> (DeviceId = disk number); null if unknown.</summary>
    public static int? ReadMediaType(int diskNumber)
    {
        try
        {
            var scope = new ManagementScope(@"root\Microsoft\Windows\Storage");
            scope.Connect();
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(
                $"SELECT DeviceId, MediaType FROM MSFT_PhysicalDisk WHERE DeviceId = '{diskNumber}'"));
            using var results = searcher.Get();
            foreach (var o in results)
                using (o)
                    if (o["MediaType"] is { } m) return Convert.ToInt32(m);
            return null;
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException
                                       or InvalidCastException or FormatException)
        {
            return null;
        }
    }
}
