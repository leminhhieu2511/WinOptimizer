using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using WinOptimizer.Services.DiskSpace;
using Xunit;

namespace WinOptimizer.Tests.IntegrationTests;

/// <summary>Skipped unless the test process is elevated (VHDX attach, raw disk I/O and chkdsk need admin).</summary>
public sealed class AdminFactAttribute : FactAttribute
{
    public AdminFactAttribute()
    {
        if (!Elevation.IsAdmin) Skip = "Integration test: needs an elevated (administrator) test process.";
    }
}

/// <inheritdoc cref="AdminFactAttribute"/>
public sealed class AdminTheoryAttribute : TheoryAttribute
{
    public AdminTheoryAttribute()
    {
        if (!Elevation.IsAdmin) Skip = "Integration test: needs an elevated (administrator) test process.";
    }
}

public static class Elevation
{
    public static bool IsAdmin { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
}

/// <summary>A partition of the attached VHDX as seen by the Storage WMI provider (MSFT_Partition).</summary>
public sealed record VhdxPartition(uint Number, long Offset, long Size, Guid Guid, Guid GptType, char Letter,
                                   string[] AccessPaths, bool IsHidden, bool NoDefaultDriveLetter)
{
    /// <summary>"\\?\Volume{...}\" or null.</summary>
    public string? VolumePath => AccessPaths.FirstOrDefault(a => a.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The VHDX test classes never run in parallel with each other: two diskpart/VDS sessions, letter assignments and
/// kill/resume timings on two VHDXs at once: the first combined run (both classes together) had 3 CLI kill-test
/// failures, 2 of them in tests that overlapped a runner test.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class VhdxCollection
{
    public const string Name = "VHDX integration (serial)";
}

/// <summary>
/// Creates, attaches and lays out a throw-away VHDX (diskpart /s) and hands out its disk number only after
/// checking (MSFT_Disk) that it is that VHDX and neither the boot nor the system disk. Detached and deleted on
/// Dispose. Layout (GPT, no ESP, no Windows volume): [MSR added by diskpart] CLIKE 1024 MB NTFS (letter),
/// RLIKE 300 MB recovery type + attributes 0x8000000000000001 (no letter), DLIKE 2048 MB NTFS (letter; its size and the
/// VHDX maximum can be raised for flows that need more than 1 GiB to move).
/// </summary>
public sealed class VhdxFixture : IDisposable
{
    public const long MiB = 1024 * 1024;
    public static readonly Guid RecoveryType = new("de94bba4-06d1-4d40-a16a-bfd50179d6ac");
    public static readonly Guid MsrType = new("e3c9e316-0b5c-4db8-817d-f92df00215ae");

    /// <summary>Scratch folder for the VHDX files, diskpart scripts, journals and logs (outside the repo):
    /// %WINOPT_VHDX_DIR%, else "%TEMP%\WinOptimizer.VhdxTests". Created if missing.</summary>
    public static string ScratchDir { get; } = Directory.CreateDirectory(
        Environment.GetEnvironmentVariable("WINOPT_VHDX_DIR") is { Length: > 0 } d
            ? d
            : Path.Combine(Path.GetTempPath(), "WinOptimizer.VhdxTests")).FullName;

    static readonly ManagementScope Scope = new(@"\\.\root\Microsoft\Windows\Storage");
    static readonly object CleanupLock = new();
    static bool _leftoversCleaned;

    readonly Action<string> _log;
    readonly int _disk;
    bool _disposed;

    public string VhdxPath { get; }
    public string Name { get; }
    public Guid DiskGuid { get; }
    public uint LogicalSectorSize { get; }
    public Guid CLikeGuid { get; }
    public Guid RLikeGuid { get; }
    public Guid DLikeGuid { get; }

    /// <summary>The VHDX disk number; re-checked (MSFT_Disk) on every access.</summary>
    public int DiskNumber
    {
        get
        {
            AssertSafe();
            return _disk;
        }
    }

    /// <summary>"\\.\PhysicalDriveN" of the VHDX; re-checked on every access.</summary>
    public string DiskPath => $@"\\.\PhysicalDrive{DiskNumber}";

    public VhdxPartition CLike => Partition(CLikeGuid);
    public VhdxPartition RLike => Partition(RLikeGuid);
    public VhdxPartition DLike => Partition(DLikeGuid);

    VhdxFixture(string name, string path, int disk, Action<string> log, Guid diskGuid, uint sector, Guid c, Guid r, Guid d)
    {
        Name = name;
        VhdxPath = path;
        _disk = disk;
        _log = log;
        DiskGuid = diskGuid;
        LogicalSectorSize = sector;
        CLikeGuid = c;
        RLikeGuid = r;
        DLikeGuid = d;
    }

    /// <summary>Creates "&lt;ScratchDir&gt;\&lt;name&gt;.vhdx" (4096 MB expandable by default), attaches it and lays it out.</summary>
    public static VhdxFixture Create(string name, Action<string>? log = null, int dLikeMb = 2048, int maximumMb = 4096)
    {
        if (dLikeMb + 1024 + 300 + 64 > maximumMb) throw new ArgumentOutOfRangeException(nameof(dLikeMb));
        log ??= _ => { };
        if (!Elevation.IsAdmin) throw new InvalidOperationException("VhdxFixture needs an elevated process.");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Directory.CreateDirectory(ScratchDir);
        CleanupLeftovers(log);
        string path = Path.Combine(ScratchDir, name + ".vhdx");
        DetachAndDelete(path, log);

        var before = Query("SELECT Number FROM MSFT_Disk").Select(o => Convert.ToInt32(o["Number"])).ToHashSet();
        int rc = Diskpart(Path.Combine(ScratchDir, name + ".attach.txt"),
            $"create vdisk file=\"{path}\" maximum={maximumMb} type=expandable\r\n" +
            $"select vdisk file=\"{path}\"\r\nattach vdisk\r\n", log);
        if (rc != 0) throw new InvalidOperationException($"diskpart create/attach failed ({rc})");

        int disk = -1;
        try
        {
            for (int i = 0; i < 30 && disk < 0; i++)
            {
                UpdateHostStorageCache();
                var hit = Query("SELECT * FROM MSFT_Disk").Where(o => SamePath(o["Location"], path)).ToList();
                if (hit.Count == 1) disk = Convert.ToInt32(hit[0]["Number"]);
                else Thread.Sleep(1000);
            }
            if (disk < 0) throw new InvalidOperationException("SAFETY: the attached VHDX disk was not found (MSFT_Disk.Location)");
            if (before.Contains(disk)) throw new InvalidOperationException($"SAFETY: disk {disk} existed before the attach");
            CheckDisk(disk, path);   // before the first write (diskpart layout)

            rc = Diskpart(Path.Combine(ScratchDir, name + ".layout.txt"),
                $"select disk {disk}\r\n" +
                "online disk noerr\r\n" +
                "attributes disk clear readonly noerr\r\n" +
                "convert gpt\r\n" +
                "create partition primary size=1024\r\nformat quick fs=ntfs label=CLIKE\r\nassign\r\n" +
                "create partition primary size=300\r\nformat quick fs=ntfs label=RLIKE\r\n" +
                $"set id={RecoveryType}\r\ngpt attributes=0x8000000000000001\r\n" +
                $"create partition primary size={dLikeMb}\r\nformat quick fs=ntfs label=DLIKE\r\nassign\r\n", log);
            if (rc != 0) throw new InvalidOperationException($"diskpart layout failed ({rc})");

            var d = Query($"SELECT * FROM MSFT_Disk WHERE Number={disk}").Single();
            var diskGuid = ToGuid(d["Guid"]);
            uint sector = Convert.ToUInt32(d["LogicalSectorSize"]);

            List<VhdxPartition> parts = [];
            for (int i = 0; i < 30; i++)   // drive letters arrive asynchronously
            {
                parts = ReadPartitions(disk);
                var data = parts.Where(p => p.GptType != MsrType && p.GptType != RecoveryType).ToList();
                if (data.Count == 2 && data.All(p => p.Letter != '\0')) break;
                Thread.Sleep(1000);
            }
            foreach (var p in parts) log($"partition {p}");
            var c = parts.Single(p => p.GptType != MsrType && p.GptType != RecoveryType && p.Size == 1024 * MiB);
            var r = parts.Single(p => p.GptType == RecoveryType);
            var dl = parts.Single(p => p.GptType != MsrType && p.GptType != RecoveryType && p.Size == dLikeMb * MiB);
            foreach (var x in new[] { c, dl })
                if (x.Letter == '\0' || x.Letter is 'C' or 'D')
                    throw new InvalidOperationException($"SAFETY: unexpected drive letter '{x.Letter}' on the VHDX");
            var f = new VhdxFixture(name, path, disk, log, diskGuid, sector, c.Guid, r.Guid, dl.Guid);
            f.AssertSafe();
            log($"VHDX {path} attached as disk {disk}, disk GUID {diskGuid}, sector {sector}");
            return f;
        }
        catch
        {
            DetachAndDelete(path, log);
            throw;
        }
    }

    /// <summary>Throws unless the disk number still is the attached VHDX and not a boot/system disk.</summary>
    public void AssertSafe()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CheckDisk(_disk, VhdxPath);
    }

    internal static void CheckDisk(int disk, string path)
    {
        var disks = Query($"SELECT * FROM MSFT_Disk WHERE Number={disk}");
        if (disks.Count != 1) throw new InvalidOperationException($"SAFETY: disk {disk} not found exactly once");
        var d = disks[0];
        // Fail closed: a missing value counts as boot/system.
        bool boot = d["IsBoot"] is not false, system = d["IsSystem"] is not false;
        string name = d["FriendlyName"]?.ToString() ?? "";
        if (boot || system) throw new InvalidOperationException($"SAFETY: disk {disk} IsBoot={boot} IsSystem={system}");
        if (!name.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"SAFETY: disk {disk} FriendlyName '{name}' is not a virtual disk");
        if (!SamePath(d["Location"], path))
            throw new InvalidOperationException($"SAFETY: disk {disk} Location '{d["Location"]}' is not {path}");
    }

    static bool SamePath(object? location, string path) =>
        string.Equals(location?.ToString(), path, StringComparison.OrdinalIgnoreCase);

    public VhdxPartition Partition(Guid guid)
    {
        AssertSafe();
        return ReadPartitions(_disk).Single(p => p.Guid == guid);
    }

    public IReadOnlyList<VhdxPartition> Partitions()
    {
        AssertSafe();
        return ReadPartitions(_disk);
    }

    internal static List<VhdxPartition> ReadPartitions(int disk)
    {
        UpdateHostStorageCache();
        return Query($"SELECT * FROM MSFT_Partition WHERE DiskNumber={disk}").Select(p => new VhdxPartition(
                Convert.ToUInt32(p["PartitionNumber"]),
                Convert.ToInt64(p["Offset"]),
                Convert.ToInt64(p["Size"]),
                ToGuid(p["Guid"]),
                ToGuid(p["GptType"]),
                ToLetter(p["DriveLetter"]),
                (p["AccessPaths"] as string[]) ?? [],
                p["IsHidden"] is true,
                p["NoDefaultDriveLetter"] is true))   // null on MSR/non-data partitions
            .OrderBy(p => p.Offset).ToList();
    }

    static Guid ToGuid(object? o) => o is string s && Guid.TryParse(s, out var g) ? g : Guid.Empty;

    static char ToLetter(object? o) => o switch
    {
        char c when c != '\0' => char.ToUpperInvariant(c),
        ushort u when u != 0 => char.ToUpperInvariant((char)u),
        string s when s.Length > 0 && s[0] != '\0' => char.ToUpperInvariant(s[0]),
        _ => '\0',
    };

    /// <summary>Shrinks a partition by <paramref name="bytes"/> with MSFT_Partition.Resize (end moves left).</summary>
    public VhdxPartition Shrink(Guid guid, long bytes)
    {
        AssertSafe();
        UpdateHostStorageCache();
        var p = Query($"SELECT * FROM MSFT_Partition WHERE DiskNumber={_disk}")
            .Single(o => ToGuid(o["Guid"]) == guid);
        long size = Convert.ToInt64(p["Size"]);
        var inp = p.GetMethodParameters("Resize");
        inp["Size"] = (ulong)(size - bytes);
        var outp = p.InvokeMethod("Resize", inp, null);
        uint rv = Convert.ToUInt32(outp["ReturnValue"]);
        _log($"MSFT_Partition.Resize({size - bytes}) ReturnValue={rv}");
        if (rv != 0) throw new InvalidOperationException($"MSFT_Partition.Resize failed ({rv})");
        var after = Partition(guid);
        if (after.Size != size - bytes) throw new InvalidOperationException($"shrink: size {after.Size}, expected {size - bytes}");
        return after;
    }

    /// <summary>Writes <paramref name="count"/> seeded random files (1 KiB..8 MiB, at most <paramref name="totalBytes"/>)
    /// under "X:\data" and returns relative path → SHA-256 (hex), checked by reading every file back.</summary>
    public Dictionary<string, string> PopulateFiles(char letter, int count, int seed, long totalBytes = 1024 * MiB)
    {
        AssertSafe();
        if (count < 2) throw new ArgumentOutOfRangeException(nameof(count));
        var sw = Stopwatch.StartNew();
        var rng = new Random(seed);
        var u = Enumerable.Range(0, count).Select(_ => rng.NextDouble()).ToArray();
        long maxSize = Math.Min(8 * MiB, totalBytes / 4);
        double k = 1.0;
        long[] sizes;
        while (true)   // skewed sizes: mostly small files, a few large ones, sum <= totalBytes
        {
            sizes = u.Select(x => (long)Math.Round(1024 * Math.Pow(maxSize / 1024.0, Math.Pow(x, k)))).ToArray();
            sizes[0] = 1024;
            sizes[1] = maxSize;
            if (sizes.Sum() <= totalBytes) break;
            k += 0.02;
        }
        string root = $@"{letter}:\data";
        var buf = new byte[maxSize];
        var hashes = new Dictionary<string, string>(count, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < count; i++)
        {
            string rel = $@"dir{i % 50:D2}\file{i:D4}.bin";
            string full = Path.Combine(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var span = buf.AsSpan(0, (int)sizes[i]);
            rng.NextBytes(span);
            using (var fs = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                fs.Write(span);
            hashes[rel] = Convert.ToHexString(SHA256.HashData(span));
        }
        AssertHashes(letter, hashes);
        _log($"populated {count} files, {sizes.Sum()} bytes on {letter}: in {sw.Elapsed.TotalSeconds:F1} s");
        return hashes;
    }

    /// <summary>Every file of <paramref name="hashes"/> exists under "X:\data" with the same SHA-256.</summary>
    public void AssertHashes(char letter, IReadOnlyDictionary<string, string> hashes)
    {
        var sw = Stopwatch.StartNew();
        var bad = new List<string>();
        foreach (var (rel, hash) in hashes)
        {
            string full = Path.Combine($@"{letter}:\data", rel);
            try
            {
                using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
                if (Convert.ToHexString(SHA256.HashData(fs)) != hash) bad.Add(rel + " (hash)");
            }
            catch (IOException ex)
            {
                bad.Add($"{rel} ({ex.GetType().Name}: {ex.Message})");
            }
        }
        _log($"hash check {letter}: {hashes.Count - bad.Count}/{hashes.Count} match in {sw.Elapsed.TotalSeconds:F1} s");
        Assert.True(bad.Count == 0, $"{bad.Count} of {hashes.Count} files differ, e.g. {string.Join("; ", bad.Take(5))}");
    }

    /// <summary>Read-only "chkdsk X:"; returns its exit code (0 = no problems).</summary>
    public int Chkdsk(char letter)
    {
        AssertSafe();
        var (rc, output) = Run("chkdsk.exe", [$"{letter}:"], _log);
        File.WriteAllText(Path.Combine(ScratchDir, $"{Name}.chkdsk-{letter}.txt"), output);
        return rc;
    }

    /// <summary>32-bit volume serial number (GetVolumeInformation) of "X:\".</summary>
    public static uint VolumeSerial(char letter)
    {
        if (!GetVolumeInformationW($@"{letter}:\", null, 0, out uint serial, out _, out _, null, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"GetVolumeInformation({letter}:)");
        return serial;
    }

    /// <summary>
    /// The trigger of the hash-mismatch investigation: a partition-layout change on THIS (second) disk while another
    /// disk's move is half done. Hides and unhides DLIKE <paramref name="cycles"/> times through DiskMove.dll
    /// (SET_DRIVE_LAYOUT_EX + UPDATE_PROPERTIES), 700 ms apart, on a background thread; join it when done.
    /// </summary>
    public Thread StartHideUnhide(Action<string> log, int cycles = 3)
    {
        int disk = DiskNumber;   // AssertSafe
        var native = new WindowsDiskMoveBackend();
        ulong original = native.GetPartitionAttributes(disk, DLikeGuid);
        var t = new Thread(() =>
        {
            try
            {
                for (int k = 0; k < cycles; k++)
                {
                    AssertSafe();
                    native.SetPartitionAttributes(disk, DLikeGuid, original | DiskMoveRunner.HideBits);
                    log($"ACTIVITY other disk {disk}: DLIKE hidden ({k + 1}/{cycles})");
                    Thread.Sleep(700);
                    AssertSafe();
                    native.SetPartitionAttributes(disk, DLikeGuid, original);
                    log($"ACTIVITY other disk {disk}: DLIKE visible again ({k + 1}/{cycles})");
                    Thread.Sleep(700);
                }
            }
            catch (Exception ex)
            {
                log($"ACTIVITY failed: {ex.Message}");
            }
        }) { IsBackground = true, Name = "other-disk layout changes" };
        t.Start();
        return t;
    }

    /// <summary>Read-only: <paramref name="count"/> bytes at <paramref name="offset"/> of the VHDX disk.</summary>
    public byte[] ReadRaw(long offset, int count)
    {
        using var h = CreateFileW(DiskPath, 0x80000000 /* GENERIC_READ */, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), $"open {DiskPath}");
        var buf = new byte[count];
        if (RandomAccess.Read(h, buf, offset) != count) throw new IOException($"short read at {offset}");
        return buf;
    }

    /// <summary>
    /// "\Device\HarddiskVolumeN" whose only extent is exactly [start, start + length) on the disk, or null. Zero-access
    /// opens only (a direct device open never mounts a file system), like DiskMove.dll's own lookup.
    /// </summary>
    public string? FindVolumeDevice(long start, long length)
    {
        int disk = DiskNumber;
        var buf = new byte[24 + 8 * 24];
        for (int n = 1; n <= 4096; n++)
        {
            using var h = CreateFileW($@"\\?\GLOBALROOT\Device\HarddiskVolume{n}", 0, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h.IsInvalid) continue;
            if (!DeviceIoControl(h, 0x00560000 /* IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS */, IntPtr.Zero, 0, buf, buf.Length,
                                 out _, IntPtr.Zero))
                continue;
            if (BitConverter.ToUInt32(buf, 0) == 1 && BitConverter.ToUInt32(buf, 8) == disk &&
                BitConverter.ToInt64(buf, 16) == start && BitConverter.ToInt64(buf, 24) == length)
                return $@"\Device\HarddiskVolume{n}";
        }
        return null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr sa, uint disposition, uint flags,
                                             IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, int inSize, [Out] byte[] outBuf, int outSize,
                                       out int returned, IntPtr overlapped);

    /// <summary>Takes the VHDX disk offline (diskpart): its volumes go away, so nothing writes to it.</summary>
    public void SetOffline()
    {
        int disk = DiskNumber;
        int rc = Diskpart(Path.Combine(ScratchDir, Name + ".offline.txt"), $"select disk {disk}\r\noffline disk\r\n", _log);
        if (rc != 0) throw new InvalidOperationException($"diskpart offline failed ({rc})");
        AssertSafe();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DetachAndDelete(VhdxPath, _log);
    }

    // ------------------------------------------------------------------ cleanup / diskpart / WMI

    /// <summary>Once per process: detaches and deletes every *.vhdx left in the scratch folder by an earlier run.</summary>
    static void CleanupLeftovers(Action<string> log)
    {
        lock (CleanupLock)
        {
            if (_leftoversCleaned) return;
            _leftoversCleaned = true;
            foreach (var f in Directory.GetFiles(ScratchDir, "*.vhdx"))
            {
                log($"leftover {f}: detaching and deleting");
                DetachAndDelete(f, log);
            }
        }
    }

    static void DetachAndDelete(string path, Action<string> log)
    {
        if (!File.Exists(path)) return;
        Diskpart(path + ".detach.txt", $"select vdisk file=\"{path}\"\r\ndetach vdisk noerr\r\n", log);
        for (int i = 0; i < 10; i++)
        {
            UpdateHostStorageCache();
            if (!Query("SELECT Location FROM MSFT_Disk").Any(o => SamePath(o["Location"], path))) break;
            Thread.Sleep(1000);
        }
        for (int i = 0; ; i++)
        {
            try
            {
                File.Delete(path);
                break;
            }
            catch (IOException) when (i < 10)
            {
                Thread.Sleep(1000);
            }
        }
        File.Delete(path + ".detach.txt");
        log($"VHDX {path} detached and deleted");
    }

    static int Diskpart(string scriptPath, string script, Action<string> log)
    {
        File.WriteAllText(scriptPath, script, Encoding.ASCII);
        return Run("diskpart.exe", ["/s", scriptPath], log).exitCode;
    }

    /// <summary>Runs a console tool (OEM output), logs and returns its exit code and output.</summary>
    public static (int exitCode, string output) Run(string exe, IEnumerable<string> args, Action<string> log, int timeoutMs = 600_000)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var oem = Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = oem, StandardErrorEncoding = oem,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var sw = Stopwatch.StartNew();
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(timeoutMs))
        {
            p.Kill(true);
            throw new TimeoutException($"{exe} timed out");
        }
        output += err.Result;
        log($"[{exe} {string.Join(' ', psi.ArgumentList)}] exit {p.ExitCode} in {sw.Elapsed.TotalSeconds:F1} s\n{output.Trim()}");
        return (p.ExitCode, output);
    }

    static void UpdateHostStorageCache()
    {
        try
        {
            using var cls = new ManagementClass(Scope, new ManagementPath("MSFT_StorageSetting"), null);
            cls.InvokeMethod("UpdateHostStorageCache", null);
        }
        catch (ManagementException)
        {
            // best effort: the queries still return (possibly older) data
        }
    }

    static List<ManagementObject> Query(string wql)
    {
        using var s = new ManagementObjectSearcher(Scope, new ObjectQuery(wql));
        return s.Get().Cast<ManagementObject>().ToList();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetVolumeInformationW(string root, StringBuilder? name, int nameSize, out uint serial,
                                             out uint maxComponent, out uint flags, StringBuilder? fsName, int fsNameSize);
}
