using WinOptimizer.Services;
using WinOptimizer.Services.DiskSpace;
using Xunit;

namespace WinOptimizer.Tests;

public class PowerGuardTests
{
    private static BatteryInfo Battery(bool hasBattery, int percent, bool onAc) =>
        new(hasBattery, percent, onAc, Charging: false, Remaining: null);

    [Theory]
    [InlineData(true, 19, false, true)]    // on battery, below 20%: pause
    [InlineData(true, 0, false, true)]
    [InlineData(true, 20, false, false)]   // exactly 20%: keep going
    [InlineData(true, 80, false, false)]
    [InlineData(true, 5, true, false)]     // plugged in: never pause
    [InlineData(false, 0, true, false)]    // desktop without a battery
    [InlineData(false, 0, false, false)]   // no battery reported: nothing to protect
    public void ShouldPause_OnlyOnBatteryBelow20(bool hasBattery, int percent, bool onAc, bool expected)
    {
        using var guard = new PowerGuard(IntPtr.Zero, () => Battery(hasBattery, percent, onAc));
        Assert.Equal(expected, guard.ShouldPause());
    }

    [Fact]
    public void ShouldPause_ReadsTheBatteryEachTime()
    {
        var state = Battery(true, 50, false);
        using var guard = new PowerGuard(IntPtr.Zero, () => state);
        Assert.False(guard.ShouldPause());
        state = Battery(true, 10, false);
        Assert.True(guard.ShouldPause());
        Assert.True(guard.OnBattery);
        state = Battery(true, 10, true);   // charger plugged back in
        Assert.False(guard.ShouldPause());
        Assert.False(guard.OnBattery);
    }

    [Fact]
    public void ShouldPause_FailingReaderDoesNotPause()
    {
        using var guard = new PowerGuard(IntPtr.Zero, () => throw new InvalidOperationException("no power info"));
        Assert.False(guard.ShouldPause());
    }

    [Fact]
    public void Dispose_Twice_IsHarmless()
    {
        var guard = new PowerGuard(IntPtr.Zero, () => Battery(false, 0, true));
        guard.Dispose();
        guard.Dispose();
    }
}
