namespace WinOptimizer.Services.DiskSpace;

public static class DiskMovePrechecks
{
    private const long Alignment = 4096;
    private static readonly string[] SyncApps = { "OneDrive", "Dropbox", "GoogleDrive" };

    public static IReadOnlyList<CheckResult> Run(DiskSnapshot disk, PartitionSnapshot target, PartitionSnapshot source,
        SystemFacts facts)
    {
        var r = new List<CheckResult>();
        void Add(CheckSeverity s, CheckCode c, string m) => r.Add(new CheckResult(s, c, m));

        if (!facts.IsAdmin)
            Add(CheckSeverity.Blocker, CheckCode.NotAdmin,
                "Administrator rights are required to change partitions. Restart WinOptimizer as administrator.");

        bool sameDisk = disk.Partitions.Any(p => p.Guid == target.Guid) && disk.Partitions.Any(p => p.Guid == source.Guid);
        if (!sameDisk)
            Add(CheckSeverity.Blocker, CheckCode.DifferentDisk,
                "The source and the Windows partition are not on the same physical disk. Choose a source partition on the same disk as C:.");

        if (disk.PartitionStyle != "GPT" || disk.IsDynamic)
            Add(CheckSeverity.Blocker, CheckCode.NotBasicGpt,
                "Only basic GPT disks are supported (not MBR, dynamic disks, Storage Spaces or RAID). Use a basic GPT disk for this operation.");

        if (!string.Equals(source.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase))
            Add(CheckSeverity.Blocker, CheckCode.SourceNotNtfs,
                "The source partition is not NTFS, so it cannot be resized safely. Pick an NTFS partition as the source.");

        if (!IsFullyDecrypted(facts, source.DriveLetter))
            Add(CheckSeverity.Blocker, CheckCode.SourceBitLocker,
                "The source drive is BitLocker-protected or its status is unknown. Fully decrypt it in BitLocker settings and try again.");

        if (source.IsWindowsPartition)
            Add(CheckSeverity.Blocker, CheckCode.WindowsOnSource,
                "Windows is installed on the source partition. Choose a data partition that does not hold Windows.");

        if (facts.BcdOsCount > 1)
            Add(CheckSeverity.Blocker, CheckCode.MultipleOs,
                "More than one operating system is listed in the boot menu. Remove the extra entries (or the extra systems) before moving space.");

        if (sameDisk && source.Offset > target.Offset)
        {
            var between = DiskMovePlanner.Between(disk, target, source);
            int recoveryCount = between.Count(DiskMovePlanner.IsRecovery);
            if (recoveryCount != between.Count || recoveryCount > 1)
                Add(CheckSeverity.Blocker, CheckCode.ForeignPartitionBetween,
                    "There is a partition other than Recovery between C: and the source (for example an OEM or data partition), or more than one Recovery partition. Remove or relocate it first, because only a Recovery partition can be moved along.");

            var shifted = new List<PartitionSnapshot> { source };
            shifted.AddRange(between);
            if (shifted.Any(p => p.Offset % Alignment != 0 || p.Size % Alignment != 0) ||
                disk.LogicalSectorSize is not (512 or 4096))
                Add(CheckSeverity.Blocker, CheckCode.UnalignedPartition,
                    "The partition layout is too old or unusual to move safely (partitions are not 4 KiB aligned or the sector size is unsupported). Use a different tool or re-create the layout.");
        }

        if (facts.SourceChkdskExitCode is int chk && chk != 0)
            Add(CheckSeverity.Blocker, CheckCode.SourceFsErrors,
                "A read-only chkdsk found errors on the source volume. Run chkdsk /f on it, then analyze again.");

        if (!string.Equals(disk.HealthStatus, "Healthy", StringComparison.OrdinalIgnoreCase))
            Add(CheckSeverity.Blocker, CheckCode.DiskUnhealthy,
                $"The disk reports health status '{disk.HealthStatus}'. Back up your data and repair or replace the disk first.");

        if (facts.SourceMinSize is long min && DiskMovePlanner.MaxAmount(source, min) < DiskMovePlanner.MinAmount)
            Add(CheckSeverity.Blocker, CheckCode.TooLittleSpace,
                "The source partition cannot give up at least 1 GiB. Free up space on the source (or defragment it) and try again.");

        if (facts.SourceLockHolders.Count > 0)
        {
            var names = string.Join(", ", facts.SourceLockHolders.Select(h => h.Name).Distinct());
            Add(CheckSeverity.FixFirst, CheckCode.SourceInUse,
                $"The source volume is in use by: {names}. Close these programs (or stop the services) and analyze again.");
        }

        if (source.DriveLetter is char sl && facts.PagefilePaths.Any(p =>
                p.Length >= 2 && char.ToUpperInvariant(p[0]) == char.ToUpperInvariant(sl) && p[1] == ':'))
            Add(CheckSeverity.FixFirst, CheckCode.PagefileOnSource,
                "A paging file is located on the source drive. Move it to C: in System settings and restart Windows.");

        if (facts.OnBattery)
            Add(CheckSeverity.FixFirst, CheckCode.OnBattery,
                "The computer is running on battery. Plug in the charger before continuing.");

        if (facts.RebootPending)
            Add(CheckSeverity.FixFirst, CheckCode.RebootPending,
                "Windows is waiting for a restart to finish updates. Restart the computer and try again.");

        if (facts.JournalExists)
            Add(CheckSeverity.FixFirst, CheckCode.JournalExists,
                "An unfinished move was found. Go to the Resume screen to finish it before starting a new one.");

        if (!r.Any(x => x.Severity == CheckSeverity.Blocker))
        {
            Add(CheckSeverity.Warning, CheckCode.Backup,
                "A power loss or hardware fault during the move can cause data loss. Back up your important files before starting.");
            Add(CheckSeverity.Warning, CheckCode.Duration,
                "The move copies whole partitions and can take a long time. Keep the computer powered and do not use it meanwhile.");
        }

        if (!IsFullyDecrypted(facts, target.DriveLetter))
            Add(CheckSeverity.Warning, CheckCode.TargetBitLocker,
                "The Windows drive uses BitLocker or its status is unknown. Make sure you have the recovery key at hand (see your Microsoft account or saved key file).");

        if (facts.SourceLockHolders.Any(h => SyncApps.Any(a => h.Name.Contains(a, StringComparison.OrdinalIgnoreCase))))
            Add(CheckSeverity.Warning, CheckCode.SyncAppsOnSource,
                "A sync app (OneDrive, Dropbox or Google Drive) uses the source drive and will lose it for a few minutes. Pause syncing and expect it to resume afterwards.");

        return r;
    }

    private static bool IsFullyDecrypted(SystemFacts facts, char? letter) =>
        letter is char c &&
        facts.BitLockerStatus.TryGetValue(char.ToUpperInvariant(c), out var s) && s == "FullyDecrypted";
}
