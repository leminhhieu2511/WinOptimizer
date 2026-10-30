using System.Runtime.InteropServices;

namespace WinOptimizer.Services.DiskSpace;

/// <summary>P/Invoke surface of the native DiskMove.dll (cdecl, Unicode). See native/DiskMove/include/diskmove/dmw_api.h.</summary>
internal static partial class DiskMoveNative
{
    private const string Dll = "DiskMove.dll";

    /// <summary>dm_status values (dm_core.h).</summary>
    internal const int DM_OK = 0, DM_E_INVALID_ARG = 1, DM_E_JOURNAL_MISMATCH = 6, DM_E_STOPPED = 7,
        DM_E_NOT_FOUND = 9, DM_E_LOCKED = 11;

    /// <summary>dm_extent: bytes from the partition start.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct DmExtent
    {
        public ulong Offset, Length;
    }

    /// <summary>dm_move_plan (96 bytes on x64). GUIDs are Windows GUID structs (= .NET Guid layout).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct DmMovePlan
    {
        public Guid DiskGuid, PartitionGuid;
        public uint SectorSize;
        public ulong OldStart, Length, Shift;
        public uint ExtentCount;
        public IntPtr Extents;      // const dm_extent *
        public IntPtr BootFirst;    // const uint8_t * (SectorSize bytes): journal v2
        public IntPtr BootBackup;   // const uint8_t * (SectorSize bytes)
    }

    /// <summary>dm_journal_info (56 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct DmJournalInfo
    {
        public uint MoveCount, Step, MoveIndex;
        public ulong ChunksDone, ChunkCount0, ChunkCount1, Seq;
        public uint BootRestored;
    }

    /// <summary>dm_progress_cb: return non-zero to stop between two chunks.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ProgressCallback(IntPtr user, ulong bytesDone, ulong bytesTotal);

    /// <summary>Opens the volume and takes FSCTL_LOCK_VOLUME. DM_E_LOCKED if the volume is in use. Needs admin.</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, EntryPoint = "dmw_volume_lock")]
    internal static extern int VolumeLock(string volumePath, out IntPtr volume);

    /// <summary>Unlocks and closes a handle returned by <see cref="VolumeLock"/>.</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "dmw_volume_close")]
    internal static extern void VolumeClose(IntPtr volume);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "dmw_volume_dismount")]
    internal static extern int VolumeDismount(IntPtr volume);

    /// <summary>DM_OK only if the locked volume is exactly one extent [start, start + length) on diskNumber.</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "dmw_volume_verify")]
    internal static extern int VolumeVerify(IntPtr volume, uint diskNumber, ulong start, ulong length);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, EntryPoint = "dmw_disk_number")]
    internal static extern int DiskNumber(string diskPath, out uint number);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, EntryPoint = "dmw_journal_create")]
    internal static extern int JournalCreate(string path, [In] DmMovePlan[] moves, uint count, [In] byte[] appBlob, uint appLen);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, EntryPoint = "dmw_journal_read")]
    internal static extern int JournalRead(string path, out DmJournalInfo info, [Out] byte[]? appBlob, uint appCap, out uint appLen);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, EntryPoint = "dmw_journal_set_step")]
    internal static extern int JournalSetStep(string path, uint step);

    /// <summary>Runs/resumes a move; checks the disk GUID and the partition (old or new start) before writing. Without
    /// <paramref name="heldLock"/> (IntPtr.Zero) the DLL itself locks + dismounts the volume device still at the old offset.</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, EntryPoint = "dmw_run_move")]
    internal static extern int RunMove(string diskPath, string journalPath, uint moveIndex, ProgressCallback? progress, IntPtr user,
        IntPtr heldLock);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, EntryPoint = "dmw_lock_old_offset")]
    internal static extern int LockOldOffset(string diskPath, ulong start, ulong length, out IntPtr volume);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, EntryPoint = "dmw_volume_arrival_nudge")]
    internal static extern int VolumeArrivalNudge(string diskPath, ulong start, ulong length);

    /// <summary>Read-only: first and last logical sector of [start, start + length) (journal v2 boot sectors).</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, EntryPoint = "dmw_read_boot_sectors")]
    internal static extern int ReadBootSectors(string diskPath, ulong start, ulong length, uint sectorSize,
        [Out] byte[] first, [Out] byte[] backup);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, EntryPoint = "dmw_set_partition_start")]
    internal static extern int SetPartitionStart(string diskPath, in Guid partitionGuid, ulong newStart);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, EntryPoint = "dmw_set_partition_attributes")]
    internal static extern int SetPartitionAttributes(string diskPath, in Guid partitionGuid, ulong attributes, out ulong oldAttributes);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, EntryPoint = "dmw_get_partition_attributes")]
    internal static extern int GetPartitionAttributes(string diskPath, in Guid partitionGuid, out ulong attributes);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, EntryPoint = "dmw_patch_ntfs_hidden_sectors")]
    internal static extern int PatchNtfsHiddenSectors(string diskPath, ulong start, ulong length, uint sectorSize);
}
