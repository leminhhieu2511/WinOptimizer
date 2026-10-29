using System.Security.AccessControl;
using System.Security.Principal;
using WinOptimizer.Services.DiskSpace;
using Xunit;

namespace WinOptimizer.Tests;

/// <summary>
/// Review I1: the trust predicate the runner and the Resume screen apply to C:\ProgramData\WinOptimizer, ...\DiskMove,
/// journal.bin and pending.json before trusting them. Built from constructed security descriptors (SDDL).
/// </summary>
public sealed class DataFolderTrustTests
{
    private static string? Check(string sddl) => DataFolderSecurity.FindUnsafe(new RawSecurityDescriptor(sddl));

    // The shapes the app itself creates.
    [Theory]
    [InlineData("O:BAG:SYD:P(A;OICI;FA;;;BA)(A;OICI;FA;;;SY)(A;;0x120089;;;BU)")]       // DiskMove folder
    [InlineData("O:BAG:SYD:P(A;OICI;FA;;;BA)(A;OICI;FA;;;SY)(A;OICI;0x1200A9;;;BU)")]   // WinOptimizer folder
    [InlineData("O:BAG:SYD:P(A;;FA;;;BA)(A;;FA;;;SY)")]                                  // journal.bin (DLL ACL)
    [InlineData("O:SYG:SYD:AI(A;ID;FA;;;BA)(A;ID;FA;;;SY)")]                            // pending.json (inherited), owner SYSTEM
    [InlineData("O:BAG:SYD:P(A;;FA;;;BA)(D;;FA;;;BU)")]                                  // a deny entry is fine
    public void AdminOnly_IsSafe(string sddl) => Assert.Null(Check(sddl));

    [Theory]
    [InlineData("O:BUG:SYD:P(A;;FA;;;BA)(A;;FA;;;SY)", "owned by")]                                   // owner Users
    [InlineData("O:S-1-5-21-1-2-3-1001G:SYD:P(A;;FA;;;BA)(A;;FA;;;SY)", "owned by")]                  // owner a normal user
    [InlineData("O:BAG:SYD:P(A;;FA;;;BA)(A;;FA;;;SY)(A;;FW;;;BU)", "may change it")]                  // Users write
    [InlineData("O:BAG:SYD:P(A;;FA;;;BA)(A;;GA;;;WD)", "may change it")]                              // Everyone generic all
    [InlineData("O:BAG:SYD:P(A;;FA;;;BA)(A;;GW;;;AU)", "may change it")]                              // Authenticated Users generic write
    [InlineData("O:BAG:SYD:P(A;;FA;;;BA)(A;;0x4;;;BU)", "may change it")]                             // append / add subfolder
    [InlineData("O:BAG:SYD:P(A;;FA;;;BA)(A;;0x2;;;BU)", "may change it")]                             // write / add file
    [InlineData("O:BAG:SYD:P(A;;FA;;;BA)(A;;0x10000;;;S-1-5-21-1-2-3-1001)", "may change it")]        // DELETE for a user
    [InlineData("O:BAG:SYD:P(A;;FA;;;BA)(A;;WD;;;S-1-5-21-1-2-3-1001)", "may change it")]             // WRITE_DAC
    [InlineData("O:BAG:SYD:P(A;;FA;;;BA)(A;;WO;;;BU)", "may change it")]                              // WRITE_OWNER
    [InlineData("O:BAG:SYD:P(A;;FA;;;BA)(A;;0x40;;;BU)", "may change it")]                            // delete child
    [InlineData("O:BAG:SYD:P(A;;FA;;;BA)(A;OICIIO;GA;;;CO)", "may change it")]                        // CREATOR OWNER, inherit-only
    [InlineData("O:BAG:SYD:AI(A;ID;FA;;;BA)(A;OICIID;0x6;;;BU)", "may change it")]                    // ProgramData-style inherited
    public void WritableByOthers_OrForeignOwner_IsUnsafe(string sddl, string reason)
    {
        string? why = Check(sddl);
        Assert.NotNull(why);
        Assert.Contains(reason, why);
    }

    [Fact]
    public void NullDacl_IsUnsafe()
    {
        var sd = new RawSecurityDescriptor(ControlFlags.SelfRelative, DataFolderSecurity.Administrators, DataFolderSecurity.LocalSystem, null, null);
        Assert.Contains("no access list", DataFolderSecurity.FindUnsafe(sd));
    }

    [Fact]
    public void AppFolderAcls_PassThePredicate()
    {
        foreach (var sec in new[] { DataFolderSecurity.CreateDataFolderSecurity(), DataFolderSecurity.CreateParentFolderSecurity() })
        {
            sec.SetOwner(DataFolderSecurity.Administrators);
            Assert.Null(DataFolderSecurity.FindUnsafe(new RawSecurityDescriptor(sec.GetSecurityDescriptorSddlForm(AccessControlSections.All))));
        }
    }

    [Fact]
    public void ParentFolderAcl_UsersReadOnly_Inherited()
    {
        var rules = DataFolderSecurity.CreateParentFolderSecurity().GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().ToList();
        var users = Assert.Single(rules, r => r.IdentityReference.Value == "S-1-5-32-545");
        Assert.Equal(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, users.FileSystemRights);
        Assert.Equal(InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, users.InheritanceFlags);
        Assert.Equal(3, rules.Count);
        Assert.True(DataFolderSecurity.CreateParentFolderSecurity().AreAccessRulesProtected);
    }

    /// <summary>A file the (non-elevated or elevated) test user made in %TEMP% is owned by, or writable by, that user.</summary>
    [Fact]
    public void CheckPath_UserCreatedTempFile_IsUnsafe()
    {
        string f = Path.Combine(Path.GetTempPath(), "dsm-trust-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllText(f, "x");
        try
        {
            Assert.NotNull(DataFolderSecurity.CheckPath(f));
        }
        finally
        {
            File.Delete(f);
        }
    }

    /// <summary>The real guard refuses a planted file before it changes anything (step 1 runs before the folders are
    /// secured, so a non-admin test process gets the refusal, not an access error).</summary>
    [Fact]
    public void PrepareTrusted_PlantedFile_RefusesWithClearMessage()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dsm-trust-" + Guid.NewGuid().ToString("N"), "DiskMove");
        Directory.CreateDirectory(dir);
        string journal = Path.Combine(dir, DiskMoveRunner.JournalFileName);
        File.WriteAllText(journal, "planted");
        try
        {
            var ex = Assert.Throws<DiskMoveUnsafeFilesException>(() => DataFolderSecurity.PrepareTrusted(dir, [journal]));
            Assert.StartsWith("The disk move files have unsafe permissions and were not used.", ex.Message);
            Assert.Contains("Nothing on the disk was changed", ex.Message);
            Assert.Equal("planted", File.ReadAllText(journal));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(dir)!, true);
        }
    }
}
