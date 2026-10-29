using System.IO;
using System.Management;
using System.Runtime.InteropServices;

namespace WinOptimizer.Services.DiskSpace;

/// <summary>
/// Reads the disk layout through WMI (root\Microsoft\Windows\Storage). Strictly read-only: only queries,
/// no WMI method is ever invoked.
/// </summary>
public sealed class DiskLayoutService : IDiskLayoutSource
{
    private const string StorageScope = @"root\Microsoft\Windows\Storage";
    private const ushort BusTypeSpaces = 16;

    private static readonly Guid LdmMetadata = new("5808c8aa-7e8f-42e0-85d2-e1e90434cfb3");
    private static readonly Guid LdmData = new("af9b60a0-1431-4f62-bc68-3311714a69ad");

    // GPT attribute bits that MSFT_Partition exposes only as booleans.
    private const ulong AttrRequired = 0x1UL;
    private const ulong AttrShadowCopy = 0x2000000000000000UL;
    private const ulong AttrReadOnly = 0x1000000000000000UL;
    private const ulong AttrHidden = 0x4000000000000000UL;
    private const ulong AttrNoDriveLetter = 0x8000000000000000UL;

    public IReadOnlyList<DiskSnapshot> GetDisks()
    {
        var scope = new ManagementScope(StorageScope);
        scope.Connect();

        var volumes = ReadVolumes(scope);
        var windowsRoot = Path.GetPathRoot(Environment.SystemDirectory);
        char? windowsLetter = string.IsNullOrEmpty(windowsRoot) ? null : char.ToUpperInvariant(windowsRoot[0]);

        var disks = new List<DiskSnapshot>();
        foreach (var disk in Query(scope, "SELECT * FROM MSFT_Disk"))
        {
            using (disk)
            {
                bool isBoot = Get<bool>(disk, "IsBoot");
                try
                {
                    disks.Add(ReadDisk(scope, disk, volumes, windowsLetter));
                }
                catch (Exception ex) when (!isBoot && ex is ManagementException or InvalidCastException
                                               or FormatException or COMException)
                {
                    // One bad disk must not fail the whole call; failures on the boot disk always propagate.
                }
            }
        }
        return disks.OrderBy(d => d.Number).ToList();
    }

    private static DiskSnapshot ReadDisk(ManagementScope scope, ManagementBaseObject disk,
        List<VolumeInfo> volumes, char? windowsLetter)
    {
        int number = Convert.ToInt32(disk["Number"]);
        var style = Get<uint>(disk, "PartitionStyle") switch { 1 => "MBR", 2 => "GPT", _ => "RAW" };
        var health = Get<ushort>(disk, "HealthStatus") switch
        {
            0 => "Healthy",
            1 => "Warning",
            2 => "Unhealthy",
            _ => "Unknown",
        };

        var partitions = new List<PartitionSnapshot>();
        bool ldm = false;
        foreach (var part in Query(scope, $"SELECT * FROM MSFT_Partition WHERE DiskNumber = {number}"))
        {
            using (part)
            {
                var type = ParseGuid(part["GptType"] as string);
                ldm |= type == LdmMetadata || type == LdmData;
                partitions.Add(ReadPartition(part, type, volumes, windowsLetter));
            }
        }

        return new DiskSnapshot(
            number,
            ParseGuid(disk["Guid"] as string),
            disk["FriendlyName"] as string ?? "",
            style,
            ldm || Get<ushort>(disk, "BusType") == BusTypeSpaces,
            Get<bool>(disk, "IsBoot"),
            Get<bool>(disk, "IsSystem"),
            health,
            (int)Get<uint>(disk, "LogicalSectorSize"),
            (long)Get<ulong>(disk, "Size"),
            partitions.OrderBy(p => p.Offset).ToList());
    }

    private static PartitionSnapshot ReadPartition(ManagementBaseObject part, Guid type,
        List<VolumeInfo> volumes, char? windowsLetter)
    {
        char letter = part["DriveLetter"] is { } dl ? Convert.ToChar(dl) : '\0';
        char? driveLetter = letter == '\0' ? null : char.ToUpperInvariant(letter);

        var accessPaths = (part["AccessPaths"] as string[]) ?? [];
        var volume = volumes.FirstOrDefault(v => accessPaths.Contains(v.Path, StringComparer.OrdinalIgnoreCase));

        bool isRecovery = type == PartitionTypes.Recovery;
        // MSFT_Partition has no raw attribute property: this value is derived from its boolean flags
        // (IsHidden, NoDefaultDriveLetter, IsShadowCopy, IsReadOnly) plus bit 0 (platform required) for Recovery.
        ulong attrs = 0;
        if (Get<bool>(part, "IsHidden")) attrs |= AttrHidden;
        if (Get<bool>(part, "NoDefaultDriveLetter")) attrs |= AttrNoDriveLetter;
        if (Get<bool>(part, "IsShadowCopy")) attrs |= AttrShadowCopy;
        if (Get<bool>(part, "IsReadOnly")) attrs |= AttrReadOnly;
        if (isRecovery) attrs |= AttrRequired;

        return new PartitionSnapshot(
            Convert.ToInt32(part["PartitionNumber"]),
            ParseGuid(part["Guid"] as string),
            type,
            (long)Get<ulong>(part, "Offset"),
            (long)Get<ulong>(part, "Size"),
            driveLetter,
            volume?.Path,
            volume?.FileSystem,
            volume is { Size: { } size, SizeRemaining: { } rem } ? size - rem : null,
            attrs,
            driveLetter is not null && driveLetter == windowsLetter,
            isRecovery);
    }

    private sealed record VolumeInfo(string Path, string? FileSystem, long? Size, long? SizeRemaining);

    private static List<VolumeInfo> ReadVolumes(ManagementScope scope)
    {
        var list = new List<VolumeInfo>();
        foreach (var vol in Query(scope, "SELECT * FROM MSFT_Volume"))
        {
            using (vol)
            {
                if (vol["Path"] is not string path) continue;
                string? fs = vol["FileSystem"] as string;
                long? size = vol["Size"] is { } s ? (long)Convert.ToUInt64(s) : null;
                long? rem = vol["SizeRemaining"] is { } r ? (long)Convert.ToUInt64(r) : null;
                list.Add(new VolumeInfo(path, string.IsNullOrEmpty(fs) ? null : fs, size, rem));
            }
        }
        return list;
    }

    private static IEnumerable<ManagementBaseObject> Query(ManagementScope scope, string wql)
    {
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql));
        using var results = searcher.Get();
        foreach (var o in results) yield return o;
    }

    private static T Get<T>(ManagementBaseObject o, string name) where T : struct
        => o[name] is { } v ? (T)Convert.ChangeType(v, typeof(T)) : default;

    private static Guid ParseGuid(string? s)
        => Guid.TryParse(s, out var g) ? g : Guid.Empty;
}
