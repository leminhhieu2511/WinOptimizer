using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace WinOptimizer.Services.DiskSpace;

/// <summary>
/// Protects the elevated runner from files planted by a non-admin (review I1). Called before the runner (or the
/// Resume screen) trusts journal.bin / pending.json and before a run writes anything there.
/// </summary>
public interface IDataFolderGuard
{
    /// <summary>
    /// (1) Checks the existing <paramref name="files"/>; (2) creates the data folder and its parent if missing and
    /// gives both the owner BUILTIN\Administrators and their protected DACLs; (3) checks both folders and the files
    /// again. Throws <see cref="DiskMoveUnsafeFilesException"/> if any step fails. Never touches a partition.
    /// </summary>
    void PrepareTrusted(string dataDir, IReadOnlyList<string> files);
}

/// <summary>The disk move files or folders are not provably admin-only; they were not used and nothing changed.</summary>
public sealed class DiskMoveUnsafeFilesException : DiskMoveException
{
    public DiskMoveUnsafeFilesException(string dataDir, string detail, Exception? inner = null)
        : base(Text(dataDir, detail), inner ?? new InvalidOperationException(detail)) { }

    private static string Text(string dataDir, string detail) =>
        "The disk move files have unsafe permissions and were not used. " + detail + " Nothing on the disk was changed. " +
        $"The folder {dataDir} and its files must be owned by Administrators or SYSTEM and writable only by them; " +
        "if you did not create them, ask an administrator to check the folder before continuing.";
}

/// <summary>
/// ACLs of C:\ProgramData\WinOptimizer (the parent) and ...\DiskMove (the data folder), and the trust check.
/// </summary>
public static class DataFolderSecurity
{
    public static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    public static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);

    /// <summary>Access-mask bits that let a principal change an object or what it contains: write/append data
    /// (= add file / add subfolder), write (extended) attributes, delete child, DELETE, WRITE_DAC, WRITE_OWNER,
    /// GENERIC_WRITE, GENERIC_ALL and MAXIMUM_ALLOWED.</summary>
    public const int WriteTypeMask = 0x2 | 0x4 | 0x10 | 0x40 | 0x100 | 0x10000 | 0x40000 | 0x80000 |
                                     0x10000000 | 0x40000000 | 0x02000000;

    /// <summary>
    /// The parent folder C:\ProgramData\WinOptimizer (protected): Administrators and SYSTEM full control, BUILTIN\Users
    /// read / execute / list; all inherited by subfolders and files, so other app data there stays readable.
    /// </summary>
    public static DirectorySecurity CreateParentFolderSecurity()
    {
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags all = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        sec.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(LocalSystem, FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute | FileSystemRights.ListDirectory, all,
            PropagationFlags.None, AccessControlType.Allow));
        return sec;
    }

    /// <summary>
    /// The data folder ...\DiskMove (protected). Administrators and SYSTEM: full control, inherited by the files
    /// (journal, pending marker, backups), which therefore stay readable by administrators only. BUILTIN\Users: list
    /// the folder and read its attributes and permissions, on this folder only, so a non-admin process can see THAT
    /// a journal or pending marker exists without being able to read either.
    /// </summary>
    public static DirectorySecurity CreateDataFolderSecurity()
    {
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { Administrators, LocalSystem })
            sec.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(Users,
            FileSystemRights.ListDirectory | FileSystemRights.ReadAttributes | FileSystemRights.ReadExtendedAttributes |
            FileSystemRights.ReadPermissions,
            InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        return sec;
    }

    private static bool IsTrustedSid(SecurityIdentifier? sid) => sid is not null && (sid == Administrators || sid == LocalSystem);

    /// <summary>
    /// The trust predicate (pure). Null when the owner is Administrators or SYSTEM and no allow ACE (inherit-only ones
    /// included, since they reach new children) gives any other principal a <see cref="WriteTypeMask"/> right;
    /// otherwise the reason. A missing (NULL) DACL or an ACE type it does not understand is unsafe.
    /// </summary>
    public static string? FindUnsafe(RawSecurityDescriptor sd)
    {
        ArgumentNullException.ThrowIfNull(sd);
        if (!IsTrustedSid(sd.Owner)) return $"it is owned by {Name(sd.Owner)}";
        if ((sd.ControlFlags & ControlFlags.DiscretionaryAclPresent) == 0 || sd.DiscretionaryAcl is null)
            return "it has no access list, so everyone may change it";
        foreach (GenericAce ace in sd.DiscretionaryAcl)
        {
            if (ace is not KnownAce known) return "its access list holds an entry of an unknown type";
            bool allow = ace.AceType is AceType.AccessAllowed or AceType.AccessAllowedCallback or AceType.AccessAllowedObject
                or AceType.AccessAllowedCallbackObject or AceType.AccessAllowedCompound;
            bool deny = ace.AceType is AceType.AccessDenied or AceType.AccessDeniedCallback or AceType.AccessDeniedObject
                or AceType.AccessDeniedCallbackObject;
            if (deny) continue;
            if (!allow) return $"its access list holds an entry of type {ace.AceType}";
            if (IsTrustedSid(known.SecurityIdentifier)) continue;
            if ((known.AccessMask & WriteTypeMask) != 0)
                return $"{Name(known.SecurityIdentifier)} may change it (rights 0x{known.AccessMask:X8})";
        }
        return null;
    }

    private static string Name(SecurityIdentifier? sid)
    {
        if (sid is null) return "nobody";
        try { return sid.Translate(typeof(NTAccount)).Value; }
        catch (Exception ex) when (ex is IdentityNotMappedException or SystemException) { return sid.Value; }
    }

    /// <summary>Reads the owner and DACL of a file or folder and applies <see cref="FindUnsafe"/>. Null = safe.</summary>
    public static string? CheckPath(string path)
    {
        byte[] bin = Directory.Exists(path)
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access).GetSecurityDescriptorBinaryForm()
            : new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access).GetSecurityDescriptorBinaryForm();
        return FindUnsafe(new RawSecurityDescriptor(bin, 0));
    }

    /// <summary>Creates <paramref name="path"/> if missing, sets its owner to Administrators (with
    /// SeTakeOwnershipPrivilege / SeRestorePrivilege enabled while doing so), then its protected DACL.</summary>
    private static void SecureFolder(string path, DirectorySecurity dacl)
    {
        if (!Directory.Exists(path)) new DirectoryInfo(path).Create(dacl);
        using (TokenPrivileges.Enable("SeTakeOwnershipPrivilege", "SeRestorePrivilege"))
        {
            var owner = new DirectorySecurity();
            owner.SetOwner(Administrators);
            new DirectoryInfo(path).SetAccessControl(owner);   // only the owner section is written
            new DirectoryInfo(path).SetAccessControl(dacl);    // only the DACL section is written
        }
    }

    /// <summary>The real guard (see <see cref="IDataFolderGuard.PrepareTrusted"/>).</summary>
    public static void PrepareTrusted(string dataDir, IReadOnlyList<string> files)
    {
        string data = Path.GetFullPath(dataDir);
        string parent = Path.GetDirectoryName(data) ?? throw new DiskMoveUnsafeFilesException(data, "The folder has no parent.");

        void CheckFiles(string when)
        {
            foreach (var f in files)
            {
                string? why;
                try
                {
                    if (!File.Exists(f)) continue;
                    why = CheckPath(f);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception or ArgumentException)
                {
                    throw new DiskMoveUnsafeFilesException(data, $"The permissions of {f} could not be read ({ex.Message}).", ex);
                }
                if (why is not null) throw new DiskMoveUnsafeFilesException(data, $"{f} is not safe{when}: {why}.");
            }
        }

        // (1) Before anything changes: re-applying the folder ACL would re-propagate inherited entries and could hide
        // that a file was writable by someone else.
        CheckFiles("");
        // (2) Owner + protected DACL on both folders. Failing here stops the caller before it trusts or writes anything.
        try
        {
            SecureFolder(parent, CreateParentFolderSecurity());
            SecureFolder(data, CreateDataFolderSecurity());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or Win32Exception or IdentityNotMappedException or ArgumentException)
        {
            throw new DiskMoveUnsafeFilesException(data, $"Its folders could not be secured ({ex.Message}).", ex);
        }
        // (3) Both folders and the files, as they are now.
        foreach (var folder in new[] { parent, data })
        {
            string? why;
            try { why = CheckPath(folder); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception or ArgumentException)
            {
                throw new DiskMoveUnsafeFilesException(data, $"The permissions of {folder} could not be read ({ex.Message}).", ex);
            }
            if (why is not null) throw new DiskMoveUnsafeFilesException(data, $"The folder {folder} is not safe: {why}.");
        }
        CheckFiles(" after securing its folder");
    }

    /// <summary><see cref="PrepareTrusted"/> as an <see cref="IDataFolderGuard"/>.</summary>
    public static IDataFolderGuard Guard { get; } = new WindowsGuard();

    private sealed class WindowsGuard : IDataFolderGuard
    {
        public void PrepareTrusted(string dataDir, IReadOnlyList<string> files) => DataFolderSecurity.PrepareTrusted(dataDir, files);
    }
}

/// <summary>Enables token privileges of the process for a scope and restores their previous state on Dispose.</summary>
internal sealed class TokenPrivileges : IDisposable
{
    private readonly List<(Luid Luid, uint Attributes)> _previous = [];

    public static TokenPrivileges Enable(params string[] names)
    {
        var t = new TokenPrivileges();
        foreach (var name in names)
        {
            if (!LookupPrivilegeValue(null, name, out var luid)) continue;
            var state = new TokenPrivilege { Count = 1, Luid = luid, Attributes = SePrivilegeEnabled };
            if (!Adjust(state, out var old)) continue;   // not held (not elevated): the call that needs it fails instead
            t._previous.Add((luid, old.Count == 1 ? old.Attributes : SePrivilegeEnabled));
        }
        return t;
    }

    public void Dispose()
    {
        foreach (var (luid, attrs) in _previous)
            Adjust(new TokenPrivilege { Count = 1, Luid = luid, Attributes = attrs & SePrivilegeEnabled }, out _);
        _previous.Clear();
    }

    private static bool Adjust(TokenPrivilege state, out TokenPrivilege old)
    {
        old = default;
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            int size = Marshal.SizeOf<TokenPrivilege>();
            bool ok = AdjustTokenPrivileges(token, false, ref state, size, out old, out _);
            return ok && Marshal.GetLastWin32Error() == 0;   // ERROR_NOT_ALL_ASSIGNED (1300) = privilege not held
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private const uint SePrivilegeEnabled = 0x2;
    private const uint TokenAdjustPrivileges = 0x20, TokenQuery = 0x8;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Luid { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TokenPrivilege { public uint Count; public Luid Luid; public uint Attributes; }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "LookupPrivilegeValueW")]
    private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivilege state, int bufferLength,
        out TokenPrivilege previous, out int returnLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
