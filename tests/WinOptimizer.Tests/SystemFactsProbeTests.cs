using System.Diagnostics;
using Xunit;
using WinOptimizer.Services;
using WinOptimizer.Services.DiskSpace;

namespace WinOptimizer.Tests;

/// <summary>Read-only; runs against the real machine. Never locks, dismounts or writes a volume, and never runs chkdsk.</summary>
public class SystemFactsProbeTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void Collect_OnDevMachine_ReportsKnownFacts()
    {
        Assert.False(AdminHelper.IsAdmin, "Run this test non-elevated: the lock path must stay untested here.");

        var disk = new DiskLayoutService().GetDisks().Single(d => d.IsBoot);
        var source = disk.Partitions.First(p => p.IsWindowsPartition);

        var facts = SystemFactsProbe.Collect(disk, source, new SystemFactsOptions(RunChkdsk: false));

        output.WriteLine($"admin={facts.IsAdmin} battery={facts.OnBattery} reboot={facts.RebootPending} pagefiles={string.Join(",", facts.PagefilePaths)} bitlocker={string.Join(",", facts.BitLockerStatus)} bcd={facts.BcdOsCount} chkdsk={facts.SourceChkdskExitCode} minSize={facts.SourceMinSize} holders={facts.SourceLockHolders.Count} journal={facts.JournalExists}");
        Assert.False(facts.IsAdmin);
        Assert.Contains(facts.PagefilePaths, p => string.Equals(p, @"C:\pagefile.sys", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus ==
                     System.Windows.Forms.PowerLineStatus.Offline, facts.OnBattery);
        Assert.Null(facts.SourceChkdskExitCode);
        Assert.Empty(facts.SourceLockHolders);
        Assert.False(facts.JournalExists);
        Assert.Equal("Unknown", facts.BitLockerStatus[source.DriveLetter!.Value]);
    }

    [Theory]
    [InlineData(DiskMoveRunner.JournalFileName)]
    [InlineData(DiskMoveRunner.PendingFileName)]
    public void Collect_JournalPath_ReflectsUnfinishedMove(string file)
    {
        var disk = new DiskLayoutService().GetDisks().Single(d => d.IsBoot);
        var source = disk.Partitions.First(p => p.IsWindowsPartition);
        string dir = Path.Combine(Path.GetTempPath(), "dsm-journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var opt = new SystemFactsOptions(RunChkdsk: false, JournalPath: Path.Combine(dir, DiskMoveRunner.JournalFileName));
            Assert.False(SystemFactsProbe.Collect(disk, source, opt).JournalExists);
            File.WriteAllText(Path.Combine(dir, file), "x");   // a journal or only a pending marker
            Assert.True(SystemFactsProbe.Collect(disk, source, opt).JournalExists);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void GetHolders_DetectsOpenFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dsm-rm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "locked.dll");
        File.WriteAllText(file, "x");
        Process? child = null;
        try
        {
            string ready = Path.Combine(dir, "ready.flag");
            string script = $"$f=[IO.File]::Open('{file}','Open','ReadWrite','None'); 'x' | Set-Content -LiteralPath '{ready}'; Start-Sleep 60";
            child = Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -NonInteractive -Command \"{script}\"") { CreateNoWindow = true, UseShellExecute = false })!;
            for (int i = 0; i < 200 && !File.Exists(ready); i++) Thread.Sleep(100);
            Assert.True(File.Exists(ready), "child did not open the file");

            var holders = RestartManagerHelper.GetHolders(dir);

            Assert.Contains(holders, h => h.ProcessId == child.Id);
        }
        finally
        {
            try { child?.Kill(true); child?.WaitForExit(5000); } catch { }
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}

public class BitLockerMappingTests
{
    private static readonly char[] Letters = ['C', 'D', 'E'];

    [Fact]
    public void NamespaceMissing_MapsEveryLetterToFullyDecrypted()
    {
        var map = SystemFactsProbe.MapBitLocker(Letters, new SystemFactsProbe.BitLockerOutcome.NotPresent());
        Assert.All(Letters, c => Assert.Equal("FullyDecrypted", map[c]));
    }

    [Fact]
    public void Listed_MapsStatusesAndKeepsUnlistedLettersUnknown()
    {
        var map = SystemFactsProbe.MapBitLocker(Letters, new SystemFactsProbe.BitLockerOutcome.Listed(
            [('c', 1u), ('D', 0u)]));
        Assert.Equal("FullyEncrypted", map['C']);
        Assert.Equal("FullyDecrypted", map['D']);
        Assert.Equal("Unknown", map['E']);
    }

    [Fact]
    public void OtherError_KeepsEveryLetterUnknown()
    {
        var map = SystemFactsProbe.MapBitLocker(Letters, new SystemFactsProbe.BitLockerOutcome.Failed());
        Assert.All(Letters, c => Assert.Equal("Unknown", map[c]));
    }
}

public class BcdOsLoaderCountTests
{
    private const string Windows = """
        Windows Boot Loader
        -------------------
        identifier              {current}
        device                  partition=C:
        path                    \Windows\system32\winload.efi
        description             Windows 11
        recoverysequence        {6c3e1f1a-0000-0000-0000-000000000001}
        recoveryenabled         Yes
        osdevice                partition=C:
        systemroot              \Windows

        """;

    private const string WinRe = """
        Windows Boot Loader
        -------------------
        identifier              {6c3e1f1a-0000-0000-0000-000000000001}
        device                  ramdisk=[\Device\HarddiskVolume4]\Recovery\WindowsRE\Winre.wim,{6c3e1f1a-0000-0000-0000-000000000002}
        path                    \windows\system32\winload.efi
        description             Windows Recovery Environment
        osdevice                ramdisk=[\Device\HarddiskVolume4]\Recovery\WindowsRE\Winre.wim,{6c3e1f1a-0000-0000-0000-000000000002}
        systemroot              \windows
        nx                      OptIn
        winpe                   Yes

        """;

    [Fact]
    public void WindowsWithItsRecoveryEnvironment_IsOneOs() =>
        Assert.Equal(1, SystemFactsProbe.CountOsLoaders(Windows + WinRe));

    [Fact]
    public void TwoWindowsInstalls_AreTwo() =>
        Assert.Equal(2, SystemFactsProbe.CountOsLoaders(Windows + WinRe + Windows.Replace("{current}", "{other}")));

    [Fact]
    public void LocalizedTitles_AndCrLf_StillCount() =>
        Assert.Equal(1, SystemFactsProbe.CountOsLoaders(
            (Windows.Replace("Windows Boot Loader", "Windows-Start-Ladeprogramm") +
             WinRe.Replace("Windows Boot Loader", "Windows-Start-Ladeprogramm").Replace("Yes", "Ja")).Replace("\n", "\r\n")));

    [Fact]
    public void EmptyOutput_IsZero() => Assert.Equal(0, SystemFactsProbe.CountOsLoaders(""));
}
