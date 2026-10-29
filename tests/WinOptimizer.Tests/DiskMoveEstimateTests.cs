using WinOptimizer.Services.DiskSpace;
using Xunit;

namespace WinOptimizer.Tests;

public class DiskMoveEstimateTests
{
    [Theory]
    [InlineData(4, 400_000_000L)]
    [InlineData(3, 80_000_000L)]
    [InlineData(0, 80_000_000L)]     // unspecified: assume HDD
    [InlineData(5, 80_000_000L)]     // SCM: not in the rule, assume HDD
    [InlineData(null, 80_000_000L)]
    public void BytesPerSecond_ByMediaType(int? mediaType, long expected) =>
        Assert.Equal(expected, DiskMoveEstimate.BytesPerSecond(mediaType));

    [Fact]
    public void Duration_IsCopyPlusReadback()
    {
        // 150 GB on an SSD: 150e9 * 2 / 400e6 = 750 s = 12.5 min -> "~13 min"
        var d = DiskMoveEstimate.Duration(150_000_000_000L, 4);
        Assert.Equal(750, d.TotalSeconds, 3);
        Assert.Equal("~13 min", DiskMoveEstimate.FormatMinutes(d));
        Assert.Equal(3750, DiskMoveEstimate.Duration(150_000_000_000L, 3).TotalSeconds, 3);
    }

    [Fact]
    public void FormatMinutes_IsAtLeastOne() =>
        Assert.Equal("~1 min", DiskMoveEstimate.FormatMinutes(TimeSpan.FromSeconds(3)));
}
