using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32.SafeHandles;

namespace WinOptimizer.Services.DiskSpace;

/// <summary>
/// The real <see cref="IDiskMoveBackend"/>: DiskMove.dll for exact GPT attributes, partition start, NTFS patch,
/// volume lock/verify/dismount, journal and moves; IOCTL_DISK_GET_DRIVE_LAYOUT_EX (read-only) for starts and sizes;
/// the Win32 mount point API for drive letters (tolerating Windows dropping / re-adding the letter around a hide);
/// WMI only for MSFT_Partition.Resize; WinRE state from ReAgent.xml (reagentc /info as fallback); reagentc and chkdsk
/// as processes. Needs administrator rights.
/// </summary>
public sealed class WindowsDiskMoveBackend : IDiskMoveBackend
{
    private const string StorageScope = @"root\Microsoft\Windows\Storage";

    private readonly IDiskLayoutSource _layout;

    public WindowsDiskMoveBackend(IDiskLayoutSource? layout = null) => _layout = layout ?? new DiskLayoutService();

    private static string DiskPath(int disk) => $@"\\.\PhysicalDrive{disk}";

    private static void Check(int status, string operation)
    {
        if (status == DiskMoveNative.DM_OK) return;
        if (status == DiskMoveNative.DM_E_JOURNAL_MISMATCH)
            throw new DiskMoveStateMismatchException($"{operation}: the disk does not match the journal (DM_E_JOURNAL_MISMATCH).");
        throw new DiskMoveNativeException(status, operation);
    }

    // ================================================================== disk and partition table

    public int FindDiskNumber(Guid diskGuid)
    {
        foreach (var d in _layout.GetDisks().Where(d => d.Guid == diskGuid))
        {
            try
            {
                if (ReadLayout(d.Number).DiskId == diskGuid) return d.Number;   // WMI can be stale: trust the disk itself
            }
            catch (DiskMoveException)
            {
                // try the next candidate
            }
        }
        throw new DiskMoveException($"No disk with GPT disk GUID {diskGuid} is attached.");
    }

    private sealed record GptEntry(int Number, long Start, long Length, Guid Type, Guid Id, ulong Attributes);
    private sealed record Layout(Guid DiskId, IReadOnlyList<GptEntry> Entries);

    private static GptEntry Entry(int disk, Guid part) =>
        ReadLayout(disk).Entries.SingleOrDefault(e => e.Id == part)
        ?? throw new DiskMoveException($"Partition {part} is not on disk {disk}.");

    public long PartitionStart(int disk, Guid part) => Entry(disk, part).Start;
    public long PartitionSize(int disk, Guid part) => Entry(disk, part).Length;

    /// <summary>IOCTL_DISK_GET_DRIVE_LAYOUT_EX (GENERIC_READ). Throws unless the disk is GPT.</summary>
    private static Layout ReadLayout(int disk)
    {
        using var h = OpenDisk(disk);
        int size = 48 + 144 * 128;
        while (true)
        {
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                if (!DeviceIoControl(h, IOCTL_DISK_GET_DRIVE_LAYOUT_EX, IntPtr.Zero, 0, buf, size, out _, IntPtr.Zero))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == ERROR_INSUFFICIENT_BUFFER && size < (1 << 24)) { size *= 2; continue; }
                    throw new DiskMoveException($"Reading the partition table of disk {disk} failed: {new Win32Exception(err).Message}");
                }
                if (Marshal.ReadInt32(buf, 0) != PARTITION_STYLE_GPT)
                    throw new DiskMoveException($"Disk {disk} is not a GPT disk.");
                int count = Marshal.ReadInt32(buf, 4);
                var diskId = ReadGuid(buf, 8);
                var list = new List<GptEntry>();
                for (int i = 0; i < count; i++)
                {
                    int o = 48 + i * 144;   // PARTITION_INFORMATION_EX
                    long start = Marshal.ReadInt64(buf, o + 8), length = Marshal.ReadInt64(buf, o + 16);
                    int number = Marshal.ReadInt32(buf, o + 24);
                    var type = ReadGuid(buf, o + 32);
                    var id = ReadGuid(buf, o + 48);
                    ulong attrs = (ulong)Marshal.ReadInt64(buf, o + 64);
                    if (length > 0 && id != Guid.Empty) list.Add(new GptEntry(number, start, length, type, id, attrs));
                }
                return new Layout(diskId, list);
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
    }

    private static Guid ReadGuid(IntPtr buf, int offset)
    {
        var b = new byte[16];
        Marshal.Copy(buf + offset, b, 0, 16);
        return new Guid(b);
    }

    private static SafeFileHandle OpenDisk(int disk)
    {
        var h = CreateFileW(DiskPath(disk), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            h.Dispose();
            throw new DiskMoveException($"Cannot open disk {disk}: {new Win32Exception(err).Message}");
        }
        return h;
    }

    public void ResizePartition(int disk, Guid part, long newSize)
    {
        if (PartitionSize(disk, part) == newSize) return;
        var scope = new ManagementScope(StorageScope);
        scope.Connect();
        UpdateHostStorageCache(scope);   // the move changed the table behind WMI's back
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery($"SELECT * FROM MSFT_Partition WHERE DiskNumber = {disk}"));
        using var results = searcher.Get();
        var p = results.Cast<ManagementObject>().SingleOrDefault(o => Guid.TryParse(o["Guid"] as string, out var g) && g == part)
                ?? throw new DiskMoveException($"MSFT_Partition {part} not found on disk {disk}.");
        using (p)
        {
            var inp = p.GetMethodParameters("Resize");
            inp["Size"] = (ulong)newSize;
            var outp = p.InvokeMethod("Resize", inp, null);
            uint rv = Convert.ToUInt32(outp["ReturnValue"]);
            if (rv != 0)
                throw new DiskMoveException($"Resizing the partition to {newSize} bytes failed (MSFT_Partition.Resize returned {rv}).");
        }
        long after = PartitionSize(disk, part);
        if (after != newSize) throw new DiskMoveException($"The partition is {after} bytes after resizing, expected {newSize}.");
    }

    private static void UpdateHostStorageCache(ManagementScope scope)
    {
        try
        {
            using var cls = new ManagementClass(scope, new ManagementPath("MSFT_StorageSetting"), null);
            cls.InvokeMethod("UpdateHostStorageCache", null);
        }
        catch (ManagementException)
        {
            // best effort
        }
    }

    public ulong GetPartitionAttributes(int disk, Guid part)
    {
        Check(DiskMoveNative.GetPartitionAttributes(DiskPath(disk), part, out ulong a), "Reading the partition attributes");
        return a;
    }

    public void SetPartitionAttributes(int disk, Guid part, ulong attributes) =>
        Check(DiskMoveNative.SetPartitionAttributes(DiskPath(disk), part, attributes, out _), "Setting the partition attributes");

    public void SetPartitionStart(int disk, Guid part, long newStart) =>
        Check(DiskMoveNative.SetPartitionStart(DiskPath(disk), part, (ulong)newStart), "Updating the partition start");

    public void PatchNtfsHiddenSectors(int disk, long start, long length, int sectorSize) =>
        Check(DiskMoveNative.PatchNtfsHiddenSectors(DiskPath(disk), (ulong)start, (ulong)length, (uint)sectorSize),
            "Updating the NTFS boot sectors");

    public byte[] ReadRawGpt(int disk, int sectorSize)
    {
        using var h = OpenDisk(disk);
        if (!DeviceIoControl(h, IOCTL_DISK_GET_LENGTH_INFO, IntPtr.Zero, 0, out long length, 8, out _, IntPtr.Zero))
            throw new DiskMoveException($"Reading the size of disk {disk} failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        var result = new byte[(34 + 33) * sectorSize];
        ReadAt(h, 0, result, 0, 34 * sectorSize);
        ReadAt(h, length - 33L * sectorSize, result, 34 * sectorSize, 33 * sectorSize);
        return result;
    }

    private static void ReadAt(SafeFileHandle h, long offset, byte[] buf, int index, int count)
    {
        if (!SetFilePointerEx(h, offset, out _, 0))
            throw new DiskMoveException($"Seek failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        var tmp = new byte[count];
        if (!ReadFile(h, tmp, count, out int read, IntPtr.Zero) || read != count)
            throw new DiskMoveException($"Reading the disk failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        Buffer.BlockCopy(tmp, 0, buf, index, count);
    }

    // ================================================================== volumes and letters

    private sealed class VolumeHandle(IntPtr handle) : IDisposable
    {
        public IntPtr Handle { get; private set; } = handle;

        public void Dispose()
        {
            if (Handle == IntPtr.Zero) return;
            DiskMoveNative.VolumeClose(Handle);
            Handle = IntPtr.Zero;
        }
    }

    private static IntPtr H(IDisposable v) =>
        v is VolumeHandle { Handle: var h } && h != IntPtr.Zero ? h : throw new ObjectDisposedException("volume lock");

    public IDisposable LockVolume(string volumePath)
    {
        int status = 0;
        for (int attempt = 0; attempt < 5; attempt++)   // a just-shrunk volume can be busy for a moment
        {
            status = DiskMoveNative.VolumeLock(volumePath, out IntPtr h);
            if (status == DiskMoveNative.DM_OK) return new VolumeHandle(h);
            if (h != IntPtr.Zero) DiskMoveNative.VolumeClose(h);
            if (status != DiskMoveNative.DM_E_LOCKED) break;
            Thread.Sleep(1000);
        }
        if (status == DiskMoveNative.DM_E_LOCKED)
            throw new DiskMoveNativeException(status, "Locking the volume (it is in use: close the programs using it)");
        throw new DiskMoveNativeException(status, "Locking the volume");
    }

    public bool VerifyVolume(IDisposable volume, int disk, long start, long length) =>
        DiskMoveNative.VolumeVerify(H(volume), (uint)disk, (ulong)start, (ulong)length) == DiskMoveNative.DM_OK;

    public void Dismount(IDisposable volume) => Check(DiskMoveNative.VolumeDismount(H(volume)), "Dismounting the volume");

    public IDisposable? LockOldOffset(int disk, long start, long length)
    {
        int st = DiskMoveNative.LockOldOffset(DiskPath(disk), (ulong)start, (ulong)length, out IntPtr h);
        if (st == DiskMoveNative.DM_E_LOCKED)
            throw new DiskMoveNativeException(st, "Locking the half-moved partition's old volume (it is in use)");
        Check(st, "Locking the half-moved partition's old volume");
        return h == IntPtr.Zero ? null : new VolumeHandle(h);
    }

    public bool NudgeVolumeArrival(int disk, long start, long length) =>
        DiskMoveNative.VolumeArrivalNudge(DiskPath(disk), (ulong)start, (ulong)length) == DiskMoveNative.DM_OK;

    public bool VolumeRegistered(string volumePath)
    {
        string vol = Norm(volumePath);
        if (GetVolumePathNamesForVolumeNameW(vol, new char[1024], 1024, out _)) return true;
        var name = new StringBuilder(260);
        IntPtr h = FindFirstVolumeW(name, name.Capacity);
        if (h == new IntPtr(-1)) return true;   // cannot prove it is unregistered
        try
        {
            do
            {
                if (Same(name.ToString(), vol)) return true;
            } while (FindNextVolumeW(h, name, name.Capacity));
            return false;
        }
        finally
        {
            FindVolumeClose(h);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstVolumeW(StringBuilder name, int size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool FindNextVolumeW(IntPtr h, StringBuilder name, int size);

    [DllImport("kernel32.dll")]
    private static extern bool FindVolumeClose(IntPtr h);

    private static string Root(char letter) => $"{char.ToUpperInvariant(letter)}:\\";
    private static string Norm(string volume) => volume.EndsWith('\\') ? volume : volume + "\\";

    /// <summary>What a mount point leads to now ("\\?\Volume{...}\"), or null.</summary>
    private static string? Resolve(string mountPoint)
    {
        var sb = new StringBuilder(64);
        return GetVolumeNameForVolumeMountPointW(mountPoint, sb, sb.Capacity) ? sb.ToString() : null;
    }

    private static bool Same(string? a, string b) => a is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Deletes the letter only if it leads to this volume; gone already (Windows drops it on hide) counts as done.</summary>
    public void RemoveLetter(string volumePath, char letter)
    {
        string vol = Norm(volumePath), root = Root(letter);
        if (!Same(Resolve(root), vol)) return;
        if (DeleteVolumeMountPointW(root)) return;
        int err = Marshal.GetLastWin32Error();
        if (Same(Resolve(root), vol))
            throw new DiskMoveException($"Cannot remove the drive letter {root}: {new Win32Exception(err).Message}");
    }

    /// <summary>
    /// The volume re-arrives asynchronously after a layout change, and Windows often gives the letter back itself
    /// (SetVolumeMountPoint then fails with error 145), so each attempt first checks where the letter leads.
    /// - The letter leads to this volume, or SetVolumeMountPoint succeeds: drop letters Windows auto-assigned; done.
    /// - The letter leads to ANOTHER volume (e.g. a USB drive took it during the move): keep the letter Windows
    ///   auto-assigned to this volume, else assign the first free letter D..Z. Never leave it without a letter.
    /// Waits up to about 20 s for the volume to arrive.
    /// </summary>
    public char AssignLetter(string volumePath, char letter)
    {
        string vol = Norm(volumePath), root = Root(letter);
        int err = 0;
        for (int attempt = 0; attempt < 40; attempt++)
        {
            string? now = Resolve(root);
            if (Same(now, vol) || (now is null && SetVolumeMountPointW(root, vol)))
            {
                DropOtherLetters(vol, letter);
                return char.ToUpperInvariant(letter);
            }
            err = Marshal.GetLastWin32Error();
            if (now is not null && Letters(vol) is { } current)   // taken by another volume, and ours has arrived
            {
                if (current.Count > 0) return current[0];
                foreach (char c in FreeLetters())
                    if (SetVolumeMountPointW(Root(c), vol)) return c;
            }
            Thread.Sleep(500);
        }
        throw new DiskMoveException($"Cannot give the moved partition a drive letter ({root}): {new Win32Exception(err).Message}");
    }

    public char? GetLetter(string volumePath) => Letters(Norm(volumePath)) is { Count: > 0 } l ? l[0] : null;

    /// <summary>Drive letters of the volume (sorted), or null if the volume is not there (yet).</summary>
    private static List<char>? Letters(string vol)
    {
        var buf = new char[4096];
        if (!GetVolumePathNamesForVolumeNameW(vol, buf, buf.Length, out _)) return null;
        return new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(m => m.Length == 3 && m[1] == ':' && m[2] == '\\').Select(m => char.ToUpperInvariant(m[0])).Order().ToList();
    }

    /// <summary>D..Z not used by any drive (GetLogicalDrives) and not resolving to a volume.</summary>
    private static IEnumerable<char> FreeLetters()
    {
        uint used = GetLogicalDrives();
        for (char c = 'D'; c <= 'Z'; c++)
            if ((used & (1u << (c - 'A'))) == 0 && Resolve(Root(c)) is null) yield return c;
    }

    private static void DropOtherLetters(string vol, char keep)
    {
        var buf = new char[4096];
        if (!GetVolumePathNamesForVolumeNameW(vol, buf, buf.Length, out _)) return;
        foreach (var m in new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries))
            if (m.Length == 3 && m[1] == ':' && m[2] == '\\' && char.ToUpperInvariant(m[0]) != char.ToUpperInvariant(keep))
                DeleteVolumeMountPointW(m);
    }

    // ================================================================== WinRE

    private static string SystemTool(string exe) => Path.Combine(Environment.SystemDirectory, exe);

    /// <summary>Primary: %WINDIR%\System32\Recovery\ReAgent.xml (locale-independent, maintained by reagentc);
    /// fallback: the English "Windows RE status" line of reagentc /info; null if neither gives a state.</summary>
    public bool? WinReEnabled()
    {
        string xml = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "Recovery", "ReAgent.xml");
        try
        {
            if (File.Exists(xml) && ParseReAgentXml(File.ReadAllText(xml)) is bool state) return state;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // fall back to reagentc
        }
        var (_, output) = RunTool(SystemTool("reagentc.exe"), "/info", TimeSpan.FromMinutes(2));
        return ParseWinReStatus(output);
    }

    /// <summary>ReAgent.xml: &lt;InstallState state="1"/&gt; = enabled, "0" = disabled; null if missing or malformed.</summary>
    public static bool? ParseReAgentXml(string xml)
    {
        try
        {
            var state = XDocument.Parse(xml).Descendants().FirstOrDefault(e => e.Name.LocalName == "InstallState")
                ?.Attribute("state")?.Value.Trim();
            return state switch { "1" => true, "0" => false, _ => null };
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>"Windows RE status:   Enabled|Disabled" (reagentc /info); null if absent (e.g. localized output).</summary>
    public static bool? ParseWinReStatus(string output)
    {
        var m = Regex.Match(output, @"Windows RE status\s*:\s*(Enabled|Disabled)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Equals("Enabled", StringComparison.OrdinalIgnoreCase) : null;
    }

    public void SetWinRe(bool enabled)
    {
        try
        {
            if (WinReEnabled() == enabled) return;
            var (rc, output) = RunTool(SystemTool("reagentc.exe"), enabled ? "/enable" : "/disable", TimeSpan.FromMinutes(5));
            if (WinReEnabled() != enabled)
                throw new DiskMoveException($"reagentc {(enabled ? "/enable" : "/disable")} failed (exit {rc}): {output.Trim()}");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            throw new DiskMoveException($"reagentc could not be run: {ex.Message}", ex);
        }
    }

    // ================================================================== journal and moves

    public void CreateJournal(string path, IReadOnlyList<NativeMovePlan> moves, RestoreState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var pins = new List<GCHandle>();
        try
        {
            IntPtr Pin(object o)
            {
                var pin = GCHandle.Alloc(o, GCHandleType.Pinned);
                pins.Add(pin);
                return pin.AddrOfPinnedObject();
            }
            var plans = moves.Select(m =>
            {
                if (m.BootFirst?.Length != m.SectorSize || m.BootBackup?.Length != m.SectorSize)
                    throw new ArgumentException("Each move needs its saved boot sectors (sector size bytes each).", nameof(moves));
                var ext = m.Extents.Select(x => new DiskMoveNative.DmExtent { Offset = (ulong)x.Offset, Length = (ulong)x.Length }).ToArray();
                return new DiskMoveNative.DmMovePlan
                {
                    DiskGuid = m.DiskGuid, PartitionGuid = m.PartitionGuid, SectorSize = (uint)m.SectorSize,
                    OldStart = (ulong)m.OldStart, Length = (ulong)m.Length, Shift = (ulong)m.Shift,
                    ExtentCount = (uint)ext.Length, Extents = Pin(ext),
                    BootFirst = Pin(m.BootFirst), BootBackup = Pin(m.BootBackup),
                };
            }).ToArray();
            var blob = state.ToJsonBytes();
            Check(DiskMoveNative.JournalCreate(path, plans, (uint)plans.Length, blob, (uint)blob.Length), "Creating the journal");
        }
        finally
        {
            foreach (var p in pins) p.Free();
        }
    }

    public JournalInfo? ReadJournal(string path, out RestoreState? state)
    {
        state = null;
        if (!File.Exists(path)) return null;
        int st = DiskMoveNative.JournalRead(path, out var info, null, 0, out uint len);
        if (st == DiskMoveNative.DM_E_INVALID_ARG && len > 0)
        {
            var blob = new byte[len];
            st = DiskMoveNative.JournalRead(path, out info, blob, len, out len);
            Check(st, "Reading the journal");
            state = RestoreState.FromJsonBytes(blob.AsSpan(0, (int)len));
        }
        else
        {
            Check(st, "Reading the journal");
        }
        return new JournalInfo((int)info.MoveCount, (MoveStep)info.Step, (int)info.MoveIndex, (long)info.ChunksDone,
            [(long)info.ChunkCount0, (long)info.ChunkCount1]);
    }

    public void SetJournalStep(string path, MoveStep step) =>
        Check(DiskMoveNative.JournalSetStep(path, (uint)step), "Recording the step in the journal");

    public void DeleteJournal(string path) => File.Delete(path);

    public (byte[] First, byte[] Backup) ReadBootSectors(int disk, long start, long length, int sectorSize)
    {
        byte[] first = new byte[sectorSize], backup = new byte[sectorSize];
        Check(DiskMoveNative.ReadBootSectors(DiskPath(disk), (ulong)start, (ulong)length, (uint)sectorSize, first, backup),
            "Reading the boot sectors");
        return (first, backup);
    }

    public bool RunMove(int disk, string journal, int moveIndex, Func<long, long, bool> progress, IDisposable? heldLock)
    {
        Exception? callbackError = null;
        DiskMoveNative.ProgressCallback cb = (_, done, total) =>
        {
            try
            {
                return progress((long)done, (long)total) ? 1 : 0;
            }
            catch (Exception ex)
            {
                callbackError ??= ex;
                return 1;   // stop between two chunks, then rethrow below
            }
        };
        int st = DiskMoveNative.RunMove(DiskPath(disk), journal, (uint)moveIndex, cb, IntPtr.Zero,
            heldLock is null ? IntPtr.Zero : H(heldLock));
        GC.KeepAlive(cb);
        if (callbackError is not null) ExceptionDispatchInfo.Capture(callbackError).Throw();
        if (st == DiskMoveNative.DM_E_STOPPED) return false;
        Check(st, $"Moving the data (move {moveIndex})");
        return true;
    }

    // ================================================================== chkdsk and tools

    public int Chkdsk(char letter)
    {
        var (rc, _) = RunTool(SystemTool("chkdsk.exe"), $"{char.ToUpperInvariant(letter)}:", TimeSpan.FromMinutes(30));
        return rc;
    }

    /// <summary>Runs a console tool hidden; returns its exit code (-1 on timeout) and output (OEM code page).</summary>
    private static (int ExitCode, string Output) RunTool(string exe, string args, TimeSpan timeout)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var oem = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        var psi = new ProcessStartInfo(exe, args)
        {
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = oem, StandardErrorEncoding = oem,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"{exe} did not start");
        var err = p.StandardError.ReadToEndAsync();
        var outp = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(timeout))
        {
            try { p.Kill(true); } catch (InvalidOperationException) { }
            return (-1, "");
        }
        p.WaitForExit();
        return (p.ExitCode, outp.Result + err.Result);
    }

    // ================================================================== Win32

    private const uint GENERIC_READ = 0x80000000, FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
    private const uint IOCTL_DISK_GET_DRIVE_LAYOUT_EX = 0x00070050, IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C;
    private const int ERROR_INSUFFICIENT_BUFFER = 122, PARTITION_STYLE_GPT = 1;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation,
        uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, int inSize, IntPtr outBuf,
        int outSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, int inSize, out long outValue,
        int outSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFilePointerEx(SafeFileHandle h, long distance, out long newPointer, uint method);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle h, [Out] byte[] buf, int count, out int read, IntPtr overlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, StringBuilder volumeName, int size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumePathNamesForVolumeNameW(string volumeName, [Out] char[] names, int size, out int returned);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool DeleteVolumeMountPointW(string mountPoint);

    [DllImport("kernel32.dll")]
    private static extern uint GetLogicalDrives();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetVolumeMountPointW(string mountPoint, string volumeName);
}
