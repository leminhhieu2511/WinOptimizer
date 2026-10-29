using Xunit;
using WinOptimizer.Services.DiskSpace;
using WinOptimizer.Tests.Fakes;
using static WinOptimizer.Tests.Fakes.LayoutBuilder;

namespace WinOptimizer.Tests;

public class DiskMovePrecheckTests
{
    private static IReadOnlyList<CheckResult> Run(LayoutBuilder b, SystemFacts? facts = null) =>
        DiskMovePrechecks.Run(b.Disk, b.C, b.D, facts ?? Facts());

    private static void Has(IReadOnlyList<CheckResult> r, CheckCode code, CheckSeverity sev)
    {
        var hit = Assert.Single(r, x => x.Code == code);
        Assert.Equal(sev, hit.Severity);
        Assert.False(string.IsNullOrWhiteSpace(hit.Message));
    }

    [Fact]
    public void DefaultLayout_HasNoBlockersOrFixFirst()
    {
        var r = Run(new LayoutBuilder().Build());
        Assert.DoesNotContain(r, x => x.Severity != CheckSeverity.Warning);
        Has(r, CheckCode.Backup, CheckSeverity.Warning);
        Has(r, CheckCode.Duration, CheckSeverity.Warning);
        Assert.Equal(2, r.Count);
    }

    [Fact]
    public void NotAdmin_IsBlocker() =>
        Has(Run(new LayoutBuilder().Build(), Facts(isAdmin: false)), CheckCode.NotAdmin, CheckSeverity.Blocker);

    [Fact]
    public void DifferentDisk_IsBlocker()
    {
        var b = new LayoutBuilder().Build();
        var other = new LayoutBuilder().Build();
        Has(DiskMovePrechecks.Run(b.Disk, b.C, other.D, Facts()), CheckCode.DifferentDisk, CheckSeverity.Blocker);
    }

    [Fact]
    public void Mbr_IsBlocker() =>
        Has(Run(new LayoutBuilder { PartitionStyle = "MBR" }.Build()), CheckCode.NotBasicGpt, CheckSeverity.Blocker);

    [Fact]
    public void Dynamic_IsBlocker() =>
        Has(Run(new LayoutBuilder { IsDynamic = true }.Build()), CheckCode.NotBasicGpt, CheckSeverity.Blocker);

    [Fact]
    public void SourceNotNtfs_IsBlocker() =>
        Has(Run(new LayoutBuilder { DFileSystem = "exFAT" }.Build()), CheckCode.SourceNotNtfs, CheckSeverity.Blocker);

    [Theory]
    [InlineData("FullyEncrypted")]
    [InlineData("Unknown")]
    public void SourceBitLocker_IsBlocker(string status) =>
        Has(Run(new LayoutBuilder().Build(), Facts(dStatus: status)), CheckCode.SourceBitLocker, CheckSeverity.Blocker);

    [Fact]
    public void WindowsOnSource_IsBlocker() =>
        Has(Run(new LayoutBuilder { WindowsOnD = true }.Build()), CheckCode.WindowsOnSource, CheckSeverity.Blocker);

    [Fact]
    public void MultipleOs_IsBlocker() =>
        Has(Run(new LayoutBuilder().Build(), Facts(bcdOsCount: 2)), CheckCode.MultipleOs, CheckSeverity.Blocker);

    [Fact]
    public void OemPartitionBetween_IsBlocker()
    {
        var oem = Part(9, PartitionTypes.BasicData, 0, 500 * MiB, fs: "NTFS");
        Has(Run(new LayoutBuilder { ExtraBetween = oem }.Build()), CheckCode.ForeignPartitionBetween, CheckSeverity.Blocker);
    }

    [Fact]
    public void RecoveryAfterSource_IsNotBetween()
    {
        var r = Run(new LayoutBuilder { RecoveryAfterD = true }.Build());
        Assert.DoesNotContain(r, x => x.Code == CheckCode.ForeignPartitionBetween);
    }

    [Fact]
    public void SourceFsErrors_IsBlocker() =>
        Has(Run(new LayoutBuilder().Build(), Facts(chkdsk: 1)), CheckCode.SourceFsErrors, CheckSeverity.Blocker);

    [Fact]
    public void ChkdskNotRun_GivesNoResult() =>
        Assert.DoesNotContain(Run(new LayoutBuilder().Build(), Facts(chkdsk: null)), x => x.Code == CheckCode.SourceFsErrors);

    [Fact]
    public void DiskUnhealthy_IsBlocker() =>
        Has(Run(new LayoutBuilder { Health = "Warning" }.Build()), CheckCode.DiskUnhealthy, CheckSeverity.Blocker);

    [Fact]
    public void TooLittleSpace_WhenMaxBelow1GiB()
    {
        var b = new LayoutBuilder().Build();
        Has(Run(b, Facts(minSize: b.D.Size - GiB)), CheckCode.TooLittleSpace, CheckSeverity.Blocker);
    }

    [Fact]
    public void MinSizeNotMeasured_GivesNoTooLittleSpace() =>
        Assert.DoesNotContain(Run(new LayoutBuilder().Build(), Facts(minSize: null)), x => x.Code == CheckCode.TooLittleSpace);

    [Fact]
    public void SourceInUse_IsFixFirst() =>
        Has(Run(new LayoutBuilder().Build(), Facts(holders: new[] { new LockHolder("notepad", 1, false, true) })),
            CheckCode.SourceInUse, CheckSeverity.FixFirst);

    [Fact]
    public void PagefileOnSource_IsFixFirst() =>
        Has(Run(new LayoutBuilder().Build(), Facts(pagefiles: new[] { @"D:\pagefile.sys" })),
            CheckCode.PagefileOnSource, CheckSeverity.FixFirst);

    [Fact]
    public void OnBattery_IsFixFirst() =>
        Has(Run(new LayoutBuilder().Build(), Facts(onBattery: true)), CheckCode.OnBattery, CheckSeverity.FixFirst);

    [Fact]
    public void RebootPending_IsFixFirst() =>
        Has(Run(new LayoutBuilder().Build(), Facts(rebootPending: true)), CheckCode.RebootPending, CheckSeverity.FixFirst);

    [Fact]
    public void JournalExists_IsFixFirst() =>
        Has(Run(new LayoutBuilder().Build(), Facts(journal: true)), CheckCode.JournalExists, CheckSeverity.FixFirst);

    [Fact]
    public void Backup_And_Duration_AbsentWhenBlocked()
    {
        var r = Run(new LayoutBuilder().Build(), Facts(isAdmin: false));
        Assert.DoesNotContain(r, x => x.Code is CheckCode.Backup or CheckCode.Duration);
    }

    [Fact]
    public void Backup_And_Duration_PresentWithFixFirstOnly()
    {
        var r = Run(new LayoutBuilder().Build(), Facts(onBattery: true));
        Has(r, CheckCode.Backup, CheckSeverity.Warning);
        Has(r, CheckCode.Duration, CheckSeverity.Warning);
    }

    [Theory]
    [InlineData("FullyEncrypted")]
    [InlineData("Unknown")]
    public void TargetBitLocker_IsWarning(string status) =>
        Has(Run(new LayoutBuilder().Build(), Facts(cStatus: status)), CheckCode.TargetBitLocker, CheckSeverity.Warning);

    [Theory]
    [InlineData("OneDrive.exe")]
    [InlineData("dropbox")]
    [InlineData("GoogleDriveFS")]
    public void SyncAppsOnSource_IsWarning(string name) =>
        Has(Run(new LayoutBuilder().Build(), Facts(holders: new[] { new LockHolder(name, 1, false, true) })),
            CheckCode.SyncAppsOnSource, CheckSeverity.Warning);

    [Fact]
    public void NoSyncApps_NoSyncWarning() =>
        Assert.DoesNotContain(
            Run(new LayoutBuilder().Build(), Facts(holders: new[] { new LockHolder("notepad", 1, false, true) })),
            x => x.Code == CheckCode.SyncAppsOnSource);

    [Fact]
    public void UnalignedSource_IsBlocker() =>
        Has(Run(new LayoutBuilder { DOffsetExtra = 512 }.Build()), CheckCode.UnalignedPartition, CheckSeverity.Blocker);

    [Fact]
    public void UnalignedSize_IsBlocker() =>
        Has(Run(new LayoutBuilder { RecoverySize = 100 * MiB + 512 }.Build()), CheckCode.UnalignedPartition, CheckSeverity.Blocker);

    [Fact]
    public void UnusualSectorSize_IsBlocker() =>
        Has(Run(new LayoutBuilder { SectorSize = 1024 }.Build()), CheckCode.UnalignedPartition, CheckSeverity.Blocker);

    [Fact]
    public void UnalignedPartitionAfterSource_IsIgnored()
    {
        var odd = Part(8, PartitionTypes.BasicData, 0, 100 * MiB + 512, fs: "NTFS");
        var r = Run(new LayoutBuilder { ExtraAfterD = odd }.Build());
        Assert.DoesNotContain(r, x => x.Code == CheckCode.UnalignedPartition);
    }
}
