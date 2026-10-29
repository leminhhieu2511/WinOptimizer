using Xunit;
using WinOptimizer.Services.DiskSpace;
using WinOptimizer.Tests.Fakes;
using static WinOptimizer.Tests.Fakes.LayoutBuilder;

namespace WinOptimizer.Tests;

public class DiskMovePlannerTests
{
    [Fact]
    public void MaxAmount_UsesFormula()
    {
        long used = (long)(36.8 * GiB);
        var src = Part(1, PartitionTypes.BasicData, MiB, 150 * GiB, 'D', "NTFS", used: used);
        long expected = (110 * GiB - Math.Max(GiB, used / 20)) / MiB * MiB;
        Assert.Equal(expected, DiskMovePlanner.MaxAmount(src, 40 * GiB));
        Assert.Equal(0, expected % MiB);
        Assert.Equal(GiB, DiskMovePlanner.MinAmount);
    }

    [Fact]
    public void MaxAmount_ReserveIsAtLeast1GiB()
    {
        var src = Part(1, PartitionTypes.BasicData, MiB, 100 * GiB, 'D', "NTFS", used: 5 * GiB);
        Assert.Equal(100 * GiB - 10 * GiB - GiB, DiskMovePlanner.MaxAmount(src, 10 * GiB));
    }

    [Fact]
    public void Planner_OnlyMovesPartitionsBetweenTargetAndSource()
    {
        var a = new LayoutBuilder().Build();
        var pa = DiskMovePlanner.Plan(a.Disk, a.C, a.D, 20 * GiB, 40 * GiB);
        Assert.Equal(new[] { a.D, a.Recovery }, pa.PartitionsToShift);
        Assert.True(pa.HasRecovery);
        Assert.Equal(a.D.Size + a.Recovery.Size, pa.BytesToCopy);

        var b = new LayoutBuilder { RecoveryAfterD = true }.Build();
        var pb = DiskMovePlanner.Plan(b.Disk, b.C, b.D, 20 * GiB, 40 * GiB);
        Assert.Equal(new[] { b.D }, pb.PartitionsToShift);
        Assert.False(pb.HasRecovery);

        var after = Part(9, PartitionTypes.BasicData, 0, 10 * GiB, 'E', "NTFS");
        var c = new LayoutBuilder { ExtraAfterD = after }.Build();
        var pc = DiskMovePlanner.Plan(c.Disk, c.C, c.D, 20 * GiB, 40 * GiB);
        Assert.Equal(new[] { c.D, c.Recovery }, pc.PartitionsToShift);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(GiB - MiB)]
    [InlineData(GiB + 1)]            // not a MiB multiple
    [InlineData(150 * GiB)]          // as large as the source
    public void Planner_RejectsAmountOutsideRange(long amount)
    {
        var b = new LayoutBuilder().Build();
        Assert.Throws<ArgumentOutOfRangeException>(() => DiskMovePlanner.Plan(b.Disk, b.C, b.D, amount, 40 * GiB));
    }

    [Fact]
    public void Planner_RejectsAmountAboveMax_WhenMinSizeKnown()
    {
        var b = new LayoutBuilder().Build();
        long max = DiskMovePlanner.MaxAmount(b.D, 40 * GiB);
        Assert.NotNull(DiskMovePlanner.Plan(b.Disk, b.C, b.D, max, 40 * GiB));
        Assert.Throws<ArgumentOutOfRangeException>(() => DiskMovePlanner.Plan(b.Disk, b.C, b.D, max + MiB, 40 * GiB));
    }

    [Fact]
    public void Planner_RejectsSourceNotAfterTarget()
    {
        var b = new LayoutBuilder().Build();
        Assert.Throws<ArgumentException>(() => DiskMovePlanner.Plan(b.Disk, b.D, b.C, 2 * GiB, 40 * GiB));
        Assert.Throws<ArgumentException>(() => DiskMovePlanner.Plan(b.Disk, b.C, b.C, 2 * GiB, 40 * GiB));
    }

    [Fact]
    public void Planner_RejectsNonWindowsTarget()
    {
        var b = new LayoutBuilder().Build();
        Assert.Throws<ArgumentException>(() => DiskMovePlanner.Plan(b.Disk, b.Recovery, b.D, 2 * GiB, 40 * GiB));
    }

    [Fact]
    public void Planner_RejectsForeignPartitionBetween()
    {
        var oem = Part(9, PartitionTypes.BasicData, 0, 500 * MiB, fs: "NTFS");
        var b = new LayoutBuilder { ExtraBetween = oem }.Build();
        Assert.Throws<ArgumentException>(() => DiskMovePlanner.Plan(b.Disk, b.C, b.D, 2 * GiB, 40 * GiB));
    }

    [Fact]
    public void Planner_RejectsTwoRecoveriesBetween()
    {
        var extra = Part(9, PartitionTypes.Recovery, 0, 500 * MiB, recovery: true);
        var b = new LayoutBuilder { ExtraBetween = extra }.Build();
        Assert.Throws<ArgumentException>(() => DiskMovePlanner.Plan(b.Disk, b.C, b.D, 2 * GiB, 40 * GiB));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(151 * GiB)]
    public void Planner_RejectsInvalidMinSize(long min)
    {
        var b = new LayoutBuilder().Build();
        Assert.Throws<ArgumentOutOfRangeException>(() => DiskMovePlanner.Plan(b.Disk, b.C, b.D, 2 * GiB, min));
    }
}
