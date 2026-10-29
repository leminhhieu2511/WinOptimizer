namespace WinOptimizer.Services.DiskSpace;

public static class DiskMovePlanner
{
    public const long MiB = 1L << 20, GiB = 1L << 30;

    public static long MinAmount => GiB;

    /// <summary>(size - minimum shrinkable size) - max(1 GiB, 5% of used), rounded down to 1 MiB; never negative.</summary>
    public static long MaxAmount(PartitionSnapshot source, long sourceMinSize)
    {
        long used = source.UsedBytes ?? source.Size; // unknown usage: reserve conservatively
        long reserve = Math.Max(GiB, used / 20);
        long max = source.Size - sourceMinSize - reserve;
        if (max <= 0) return 0;
        return max / MiB * MiB;
    }

    /// <summary>Partitions strictly between target and source by offset, in disk order.</summary>
    internal static List<PartitionSnapshot> Between(DiskSnapshot disk, PartitionSnapshot target, PartitionSnapshot source) =>
        disk.Partitions.Where(p => p.Offset > target.Offset && p.Offset < source.Offset)
            .OrderBy(p => p.Offset).ToList();

    internal static bool IsRecovery(PartitionSnapshot p) => p.IsRecovery || p.TypeGuid == PartitionTypes.Recovery;

    /// <summary>
    /// Precondition: <paramref name="target"/> is the Windows partition and the source lies after it on the same disk.
    /// <paramref name="sourceMinSize"/> is the measured minimum size of the source; the amount may not exceed <see cref="MaxAmount"/>.
    /// </summary>
    public static MovePlan Plan(DiskSnapshot disk, PartitionSnapshot target, PartitionSnapshot source, long amount,
        long sourceMinSize)
    {
        if (!target.IsWindowsPartition)
            throw new ArgumentException("Target must be the Windows partition.", nameof(target));
        if (source.Offset <= target.Offset)
            throw new ArgumentException("Source must be located after the target.", nameof(source));
        if (sourceMinSize <= 0 || sourceMinSize > source.Size)
            throw new ArgumentOutOfRangeException(nameof(sourceMinSize), sourceMinSize,
                "Minimum size must be positive and not larger than the source partition.");
        if (amount < MinAmount || amount % MiB != 0 || amount > MaxAmount(source, sourceMinSize))
            throw new ArgumentOutOfRangeException(nameof(amount), amount,
                "Amount must be a whole number of MiB, at least 1 GiB and within the maximum allowed.");

        var between = Between(disk, target, source);
        var recovery = between.Where(IsRecovery).ToList();
        if (between.Count != recovery.Count || recovery.Count > 1)
            throw new ArgumentException("Only one Recovery partition may lie between target and source.", nameof(disk));

        var shift = new List<PartitionSnapshot> { source };
        shift.AddRange(recovery);
        return new MovePlan(amount, shift, recovery.Count == 1);
    }
}
