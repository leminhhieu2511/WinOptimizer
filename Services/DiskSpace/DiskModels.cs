namespace WinOptimizer.Services.DiskSpace;

/// <summary>One partition of a disk. Plain data, no WMI types.</summary>
/// <param name="GptAttributes">Derived from MSFT_Partition flags (it exposes no raw value); see <see cref="DiskLayoutService"/>.</param>
public sealed record PartitionSnapshot(int Number, Guid Guid, Guid TypeGuid, long Offset, long Size,
    char? DriveLetter, string? VolumePath, string? FileSystem, long? UsedBytes, ulong GptAttributes,
    bool IsWindowsPartition, bool IsRecovery);

/// <summary>One physical/virtual disk with its partitions ordered by offset.</summary>
public sealed record DiskSnapshot(int Number, Guid Guid, string FriendlyName, string PartitionStyle, // "GPT" | "MBR" | "RAW"
    bool IsDynamic, bool IsBoot, bool IsSystem, string HealthStatus, int LogicalSectorSize, long Size,
    IReadOnlyList<PartitionSnapshot> Partitions);

/// <summary>Source of the current disk layout (faked in unit tests).</summary>
public interface IDiskLayoutSource
{
    IReadOnlyList<DiskSnapshot> GetDisks();
}

/// <summary>Well-known GPT partition type GUIDs.</summary>
public static class PartitionTypes
{
    public static readonly Guid Recovery = new("de94bba4-06d1-4d40-a16a-bfd50179d6ac");
    public static readonly Guid BasicData = new("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7");
}
