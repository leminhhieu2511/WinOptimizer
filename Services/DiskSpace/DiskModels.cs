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

/// <summary>A process or service holding a handle on the source volume.</summary>
public sealed record LockHolder(string Name, int ProcessId, bool IsService, bool CanClose);

/// <summary>Machine-wide facts gathered before the prechecks run.</summary>
/// <param name="BitLockerStatus">Per drive letter; "FullyDecrypted" is the only OK value. A missing letter counts as unknown.</param>
/// <param name="SourceChkdskExitCode">Null = not checked yet.</param>
/// <param name="SourceMinSize">Smallest size the source can shrink to; null = not measured yet.</param>
public sealed record SystemFacts(bool IsAdmin, bool OnBattery, bool RebootPending,
    IReadOnlyList<string> PagefilePaths, IReadOnlyDictionary<char, string> BitLockerStatus,
    int BcdOsCount, int? SourceChkdskExitCode, long? SourceMinSize,
    IReadOnlyList<LockHolder> SourceLockHolders, bool JournalExists);

public enum CheckSeverity { Blocker, FixFirst, Warning }

public enum CheckCode
{
    NotAdmin, DifferentDisk, NotBasicGpt, SourceNotNtfs, SourceBitLocker, WindowsOnSource,
    MultipleOs, ForeignPartitionBetween, SourceFsErrors, DiskUnhealthy, TooLittleSpace,
    SourceInUse, PagefileOnSource, OnBattery, RebootPending, JournalExists,
    Backup, TargetBitLocker, SyncAppsOnSource, Duration,
    UnalignedPartition
}

public sealed record CheckResult(CheckSeverity Severity, CheckCode Code, string Message);

/// <summary>What the move will do. The whole of every shifted partition is copied, not just its used space.</summary>
/// <param name="PartitionsToShift">Farthest from the target first: the source, then the Recovery partition between if any.</param>
public sealed record MovePlan(long Amount, IReadOnlyList<PartitionSnapshot> PartitionsToShift, bool HasRecovery)
{
    public long BytesToCopy => PartitionsToShift.Sum(p => p.Size);
}

/// <summary>Well-known GPT partition type GUIDs.</summary>
public static class PartitionTypes
{
    public static readonly Guid Recovery = new("de94bba4-06d1-4d40-a16a-bfd50179d6ac");
    public static readonly Guid BasicData = new("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7");
}
