using System.Runtime.InteropServices;

namespace WinOptimizer.Services.DiskSpace;

/// <summary>P/Invoke surface of the native DiskMove.dll (cdecl, Unicode).</summary>
internal static partial class DiskMoveNative
{
    private const string Dll = "DiskMove.dll";

    /// <summary>dm_status values this wrapper cares about.</summary>
    internal const int DM_OK = 0, DM_E_LOCKED = 11;

    /// <summary>Opens the volume and takes FSCTL_LOCK_VOLUME. DM_E_LOCKED if the volume is in use. Needs admin.</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, EntryPoint = "dmw_volume_lock")]
    internal static extern int VolumeLock(string volumePath, out IntPtr volume);

    /// <summary>Unlocks and closes a handle returned by <see cref="VolumeLock"/>.</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "dmw_volume_close")]
    internal static extern void VolumeClose(IntPtr volume);
}
