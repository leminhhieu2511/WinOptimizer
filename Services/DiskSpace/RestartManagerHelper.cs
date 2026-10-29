using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace WinOptimizer.Services.DiskSpace;

/// <summary>Names the programs holding files on a volume (Restart Manager) and politely closes them.</summary>
public static class RestartManagerHelper
{
    private const int ErrorSuccess = 0, ErrorMoreData = 234;
    private const int RmService = 3, RmCritical = 1000;
    private const int MaxFiles = 5000;
    private static readonly TimeSpan MaxWalk = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Best-effort: walks <paramref name="rootPath"/> for *.exe and *.dll (reparse points skipped, access errors ignored,
    /// stops after 5,000 files or 10 s) and asks Restart Manager who uses them. Never throws.
    /// </summary>
    public static IReadOnlyList<LockHolder> GetHolders(string rootPath)
    {
        try { return GetHolders(EnumerateBinaries(rootPath)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException
                                       or InvalidOperationException or ArgumentException)
        {
            return [];
        }
    }

    internal static IReadOnlyList<LockHolder> GetHolders(IReadOnlyList<string> files)
    {
        if (files.Count == 0) return [];
        if (RmStartSession(out uint session, 0, new StringBuilder(33)) != ErrorSuccess) return [];
        try
        {
            if (RmRegisterResources(session, (uint)files.Count, files.ToArray(), 0, null, 0, null) != ErrorSuccess)
                return [];

            uint needed = 0, count = 0;
            int rc = RmGetList(session, out needed, ref count, null, out _);
            if (rc == ErrorSuccess) return []; // nobody holds any of the files
            for (int attempt = 0; rc == ErrorMoreData && attempt < 5; attempt++)
            {
                var info = new RM_PROCESS_INFO[needed];
                count = needed;
                rc = RmGetList(session, out needed, ref count, info, out _);
                if (rc == ErrorSuccess)
                    return info.Take((int)count).GroupBy(i => i.Process.dwProcessId).Select(g =>
                    {
                        var i = g.First();
                        bool isService = i.ApplicationType == RmService;
                        return new LockHolder(i.strAppName, (int)i.Process.dwProcessId, isService,
                            !isService && i.ApplicationType != RmCritical);
                    }).ToList();
            }
            return [];
        }
        finally { RmEndSession(session); }
    }

    /// <summary>
    /// Polite close (RmShutdown without RmForceShutdown) of the non-service holders. True only if RmShutdown succeeds.
    /// </summary>
    public static bool CloseApps(IEnumerable<LockHolder> holders)
    {
        var procs = new List<RM_UNIQUE_PROCESS>();
        foreach (var h in holders.Where(h => !h.IsService && h.CanClose && h.ProcessId > 0))
        {
            try
            {
                using var p = Process.GetProcessById(h.ProcessId);
                procs.Add(new RM_UNIQUE_PROCESS { dwProcessId = (uint)h.ProcessId, ProcessStartTime = ToFileTime(p.StartTime) });
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone: nothing to close.
            }
        }
        if (procs.Count == 0) return true;

        if (RmStartSession(out uint session, 0, new StringBuilder(33)) != ErrorSuccess) return false;
        try
        {
            if (RmRegisterResources(session, 0, null, (uint)procs.Count, procs.ToArray(), 0, null) != ErrorSuccess)
                return false;
            return RmShutdown(session, 0, IntPtr.Zero) == ErrorSuccess; // flags 0: RmForceShutdown NOT set
        }
        finally { RmEndSession(session); }
    }

    private static FILETIME ToFileTime(DateTime t)
    {
        long ft = t.ToFileTime();
        return new FILETIME { dwLowDateTime = (int)(ft & 0xFFFFFFFF), dwHighDateTime = (int)(ft >> 32) };
    }

    private static List<string> EnumerateBinaries(string root)
    {
        var result = new List<string>();
        var clock = Stopwatch.StartNew();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0 && result.Count < MaxFiles && clock.Elapsed < MaxWalk)
        {
            string dir = pending.Pop();
            try
            {
                foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
                {
                    if (result.Count >= MaxFiles || clock.Elapsed >= MaxWalk) break;
                    if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                    if (entry is DirectoryInfo) pending.Push(entry.FullName);
                    else if (entry.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                             entry.Extension.Equals(".dll", StringComparison.OrdinalIgnoreCase))
                        result.Add(entry.FullName);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable folder: skip.
            }
        }
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public uint dwProcessId;
        public FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string strServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint sessionHandle, int sessionFlags, StringBuilder sessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint sessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint sessionHandle, uint nFiles, string[]? rgsFilenames,
        uint nApplications, RM_UNIQUE_PROCESS[]? rgApplications, uint nServices, string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint sessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo,
        [In, Out] RM_PROCESS_INFO[]? rgAffectedApps, out uint lpdwRebootReasons);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmShutdown(uint sessionHandle, int lActionFlags, IntPtr fnStatus);
}
