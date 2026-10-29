using System.Security.AccessControl;
using System.Security.Principal;
using WinOptimizer.Services.DiskSpace;
using Xunit;

namespace WinOptimizer.Tests;

/// <summary>
/// Review C1: the real data folder (C:\ProgramData\WinOptimizer\DiskMove) is protected and its files are readable by
/// administrators only, yet a non-admin start must still see that a journal or pending marker exists. Rebuilt on a
/// temp folder with the runner's own folder ACL; the test process is the owner, so it can set the DACLs (and undo
/// them) without admin rights. Meaningful when run non-elevated (the normal test run).
/// </summary>
public sealed class DataFolderAclTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dsm-acl-" + Guid.NewGuid().ToString("N"));
    private static readonly bool IsAdmin =
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public DataFolderAclTests() => Directory.CreateDirectory(_dir);

    /// <summary>Admin + SYSTEM full control only, nothing inherited: what a file in the real folder gets.</summary>
    private static FileSecurity AdminOnlyFileSecurity()
    {
        var sec = new FileSecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        return sec;
    }

    [Theory]
    [InlineData(DiskMoveRunner.JournalFileName)]
    [InlineData(DiskMoveRunner.PendingFileName)]
    public void HasUnfinishedMove_SeesAdminOnlyFile_InProtectedFolder(string name)
    {
        string file = Path.Combine(_dir, name);
        File.WriteAllText(file, "x");                                  // before the folder is locked down
        new FileInfo(file).SetAccessControl(AdminOnlyFileSecurity());   // as the owner we may still set the DACL
        DiskMoveRunner.ApplyDataFolderSecurity(_dir);                   // exactly the runner's folder ACL

        Assert.True(DiskMoveRunner.HasUnfinishedMove(_dir));
    }

    /// <summary>The reviewer's scenario: a folder with the OLD Administrators + SYSTEM only ACL hides the journal from a
    /// non-admin process (File.Exists false, listing denied). Re-applying the current folder ACL (what the runner and an
    /// admin start now do for an existing folder) makes it visible.</summary>
    [Fact]
    public void OldAdminOnlyFolder_HidesJournal_UntilFolderSecurityIsReapplied()
    {
        string file = Path.Combine(_dir, DiskMoveRunner.JournalFileName);
        File.WriteAllText(file, "x");
        new FileInfo(file).SetAccessControl(AdminOnlyFileSecurity());
        var old = new DirectorySecurity();
        old.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
            old.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(_dir).SetAccessControl(old);

        if (!IsAdmin)
        {
            Assert.False(File.Exists(file));   // why the old File.Exists check missed an unfinished move
            Assert.Throws<UnauthorizedAccessException>(() => DiskMoveRunner.HasUnfinishedMove(_dir));
        }

        DiskMoveRunner.ApplyDataFolderSecurity(_dir);
        Assert.True(DiskMoveRunner.HasUnfinishedMove(_dir));
    }

    [Fact]
    public void HasUnfinishedMove_EmptyProtectedFolder_IsFalse()
    {
        DiskMoveRunner.ApplyDataFolderSecurity(_dir);
        Assert.False(DiskMoveRunner.HasUnfinishedMove(_dir));
    }

    [Fact]
    public void HasUnfinishedMove_OtherFilesOnly_IsFalse()
    {
        File.WriteAllText(Path.Combine(_dir, "layout-20261029-120000.json"), "{}");
        File.WriteAllText(Path.Combine(_dir, "gpt-20261029-120000.bin"), "x");
        DiskMoveRunner.ApplyDataFolderSecurity(_dir);
        Assert.False(DiskMoveRunner.HasUnfinishedMove(_dir));
    }

    [Fact]
    public void HasUnfinishedMove_MissingFolder_IsFalse() =>
        Assert.False(DiskMoveRunner.HasUnfinishedMove(Path.Combine(_dir, "missing")));

    [Fact]
    public void FolderSecurity_UsersMayOnlyListTheFolder()
    {
        var rules = DiskMoveRunner.CreateDataFolderSecurity().GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().ToList();
        var users = Assert.Single(rules, r => r.IdentityReference.Value == "S-1-5-32-545");
        // .NET adds Synchronize to every Allow rule.
        Assert.Equal(FileSystemRights.ListDirectory | FileSystemRights.ReadAttributes | FileSystemRights.ReadExtendedAttributes |
                     FileSystemRights.ReadPermissions | FileSystemRights.Synchronize, users.FileSystemRights);
        Assert.Equal(InheritanceFlags.None, users.InheritanceFlags);   // not passed on to the journal / marker
        foreach (var sid in new[] { "S-1-5-32-544", "S-1-5-18" })
        {
            var r = Assert.Single(rules, x => x.IdentityReference.Value == sid);
            Assert.Equal(FileSystemRights.FullControl, r.FileSystemRights);
            Assert.Equal(InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, r.InheritanceFlags);
        }
        Assert.Equal(3, rules.Count);
    }

    public void Dispose()
    {
        try
        {
            // Owner: give ourselves full control again, then delete.
            var me = WindowsIdentity.GetCurrent().User!;
            var open = new DirectorySecurity();
            open.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(_dir).SetAccessControl(open);
            foreach (var f in Directory.EnumerateFiles(_dir))
            {
                var fs = new FileSecurity();
                fs.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.FullControl, AccessControlType.Allow));
                new FileInfo(f).SetAccessControl(fs);
            }
            Directory.Delete(_dir, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // leave it in %TEMP%
        }
    }
}
