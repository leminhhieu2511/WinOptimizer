using Xunit;
using WinOptimizer.Services.DiskSpace;

namespace WinOptimizer.Tests;

/// <summary>Read-only; runs against the real machine's disks and needs no admin.</summary>
public class DiskLayoutServiceTests
{
    [Fact]
    public void GetDisks_ReturnsBootDiskWithWindowsPartition()
    {
        var disks = new DiskLayoutService().GetDisks();

        var boot = Assert.Single(disks, d => d.IsBoot);
        Assert.Single(boot.Partitions, p => p.IsWindowsPartition);

        Assert.Equal(disks.Select(d => d.Number).OrderBy(n => n), disks.Select(d => d.Number));

        foreach (var disk in disks)
        {
            var offsets = disk.Partitions.Select(p => p.Offset).ToList();
            Assert.Equal(offsets.OrderBy(o => o), offsets);
            Assert.True(disk.Partitions.Sum(p => p.Size) <= disk.Size,
                $"Disk {disk.Number}: partitions exceed disk size");
        }
    }
}
