using WinOptimizer.Services.DiskSpace;

namespace WinOptimizer.Tests.Fakes;

/// <summary>Builds the default dev-machine layout (EFI, MSR, C:, Recovery, D:) with per-property overrides.</summary>
public sealed class LayoutBuilder
{
    public const long MiB = 1L << 20, GiB = 1L << 30;
    public static readonly Guid EfiType = new("c12a7328-f81f-11d2-ba4b-00a0c93ec93b");
    public static readonly Guid MsrType = new("e3c9e316-0b5c-4db8-817d-f92df00215ae");

    public string PartitionStyle = "GPT";
    public bool IsDynamic;
    public string Health = "Healthy";
    public int SectorSize = 512;
    public long CSize = (long)(325.9 * GiB) / MiB * MiB;
    public long RecoverySize = (long)(0.85 * GiB) / MiB * MiB;
    public long DSize = 150 * GiB;
    public long DUsed = (long)(36.8 * GiB);
    public string DFileSystem = "NTFS";
    public bool RecoveryAfterD;
    public bool WindowsOnD;
    public long DOffsetExtra; // shifts D (and anything after) off alignment when set
    public PartitionSnapshot? ExtraBetween; // inserted right after C:
    public PartitionSnapshot? ExtraAfterD;

    public DiskSnapshot Disk = null!;
    public PartitionSnapshot C = null!, D = null!, Recovery = null!;

    private int _n;

    public static PartitionSnapshot Part(int n, Guid type, long offset, long size, char? letter = null, string? fs = null,
        bool windows = false, bool recovery = false, long? used = null) =>
        new(n, Guid.NewGuid(), type, offset, size, letter, null, fs, used, 0, windows, recovery);

    public LayoutBuilder Build()
    {
        long off = MiB;
        var list = new List<PartitionSnapshot>();
        PartitionSnapshot Add(Guid type, long size, char? letter = null, string? fs = null, bool windows = false,
            bool rec = false, long? used = null)
        {
            var p = Part(++_n, type, off, size, letter, fs, windows, rec, used);
            list.Add(p);
            off += size;
            return p;
        }
        Add(EfiType, 100 * MiB, fs: "FAT32");
        Add(MsrType, 16 * MiB);
        C = Add(PartitionTypes.BasicData, CSize, 'C', "NTFS", windows: true);
        if (ExtraBetween is not null)
        {
            var e = ExtraBetween with { Offset = off };
            list.Add(e);
            off += e.Size;
        }
        off += DOffsetExtra;
        if (!RecoveryAfterD) Recovery = Add(PartitionTypes.Recovery, RecoverySize, rec: true);
        D = Add(PartitionTypes.BasicData, DSize, 'D', DFileSystem, windows: WindowsOnD, used: DUsed);
        if (RecoveryAfterD) Recovery = Add(PartitionTypes.Recovery, RecoverySize, rec: true);
        if (ExtraAfterD is not null)
        {
            var e = ExtraAfterD with { Offset = off };
            list.Add(e);
            off += e.Size;
        }
        Disk = new DiskSnapshot(0, Guid.NewGuid(), "Test Disk", PartitionStyle, IsDynamic, true, true, Health,
            SectorSize, off + GiB, list);
        return this;
    }

    public static SystemFacts Facts(bool isAdmin = true, bool onBattery = false, bool rebootPending = false,
        string[]? pagefiles = null, string cStatus = "FullyDecrypted", string dStatus = "FullyDecrypted",
        int bcdOsCount = 1, int? chkdsk = 0, long? minSize = 40 * GiB, LockHolder[]? holders = null,
        bool journal = false) =>
        new(isAdmin, onBattery, rebootPending, pagefiles ?? new[] { @"C:\pagefile.sys" },
            new Dictionary<char, string> { ['C'] = cStatus, ['D'] = dStatus }, bcdOsCount, chkdsk, minSize,
            holders ?? Array.Empty<LockHolder>(), journal);
}
