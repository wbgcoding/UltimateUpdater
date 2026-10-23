using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;
using Xunit;

namespace UltimateUpdater.Tests;

/// <summary>
/// Driver cleanup's export staging folder must never come from anything the unelevated user controls: no
/// <c>AppPaths</c>, no environment variable, an explicit restrictive ACL, and a refusal the moment any
/// segment of the path is a reparse point. <see cref="DriverBackupStaging.ProgramDataRootOverride"/>
/// redirects the root to a throwaway temp directory so these can be proven without touching the real
/// machine's <c>%ProgramData%</c>.
/// </summary>
public sealed class DriverBackupStagingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wu-driverstaging-" + Guid.NewGuid());

    public DriverBackupStagingTests()
    {
        Directory.CreateDirectory(_root);
        DriverBackupStaging.ProgramDataRootOverride = _root;
    }

    public void Dispose()
    {
        DriverBackupStaging.ProgramDataRootOverride = null;
        DriverBackupStaging.StagingNameOverride = null;
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void PrepareStagingRoot_creates_a_timestamped_folder_under_UltimateUpdater_driver_backups()
    {
        using var result = DriverBackupStaging.PrepareStagingRoot(new LoggingService());

        Assert.NotNull(result);
        var (backupRoot, folderName) = (result!.BackupRoot, result.FolderName);

        Assert.True(Directory.Exists(backupRoot));
        Assert.Equal(Path.Combine(_root, "UltimateUpdater", "driver-backups", folderName), backupRoot);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}_\d{6}$", folderName);
        Assert.Equal(backupRoot, DriverBackupStaging.ResolveExistingPath(folderName));
    }

    [Fact]
    public void PrepareStagingRoot_grants_only_read_and_execute_to_Users_and_full_control_to_admins()
    {
        using var result = DriverBackupStaging.PrepareStagingRoot(new LoggingService());
        Assert.NotNull(result);
        var backupRoot = result!.BackupRoot;

        var security = new DirectoryInfo(backupRoot).GetAccessControl();

        // Inheritance off: this folder must not silently pick up whatever ProgramData's own ACL grants.
        Assert.True(security.AreAccessRulesProtected);

        var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToList();

        var usersSid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var usersRule = Assert.Single(rules, r => r.IdentityReference == usersSid);
        Assert.Equal(AccessControlType.Allow, usersRule.AccessControlType);
        Assert.True(usersRule.FileSystemRights.HasFlag(FileSystemRights.ReadData));
        // Users must never get a write/delete/change-permissions bit on this folder: that is the whole
        // point of a dedicated admin-owned staging root rather than reusing something the user already
        // has write access to.
        Assert.False(usersRule.FileSystemRights.HasFlag(FileSystemRights.WriteData));
        Assert.False(usersRule.FileSystemRights.HasFlag(FileSystemRights.Delete));
        Assert.False(usersRule.FileSystemRights.HasFlag(FileSystemRights.DeleteSubdirectoriesAndFiles));
        Assert.False(usersRule.FileSystemRights.HasFlag(FileSystemRights.ChangePermissions));
        Assert.False(usersRule.FileSystemRights.HasFlag(FileSystemRights.TakeOwnership));

        var adminsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var adminsRule = Assert.Single(rules, r => r.IdentityReference == adminsSid);
        Assert.True(adminsRule.FileSystemRights.HasFlag(FileSystemRights.WriteData));
        Assert.True(adminsRule.FileSystemRights.HasFlag(FileSystemRights.DeleteSubdirectoriesAndFiles));
        Assert.True(adminsRule.FileSystemRights.HasFlag(FileSystemRights.ChangePermissions));

        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var systemRule = Assert.Single(rules, r => r.IdentityReference == systemSid);
        Assert.True(systemRule.FileSystemRights.HasFlag(FileSystemRights.WriteData));
        Assert.True(systemRule.FileSystemRights.HasFlag(FileSystemRights.DeleteSubdirectoriesAndFiles));
    }

    /// <summary>The child is created with its ACL and must not exist yet: anything already under the name
    /// was planted by someone else, and is refused instead of adopted.</summary>
    [Fact]
    public void PrepareStagingRoot_refuses_a_child_that_was_pre_created_as_a_junction()
    {
        const string name = "2026-10-03_120000";
        DriverBackupStaging.StagingNameOverride = name;
        var elsewhere = Path.Combine(Path.GetTempPath(), "wu-driverstaging-elsewhere-" + Guid.NewGuid());
        Directory.CreateDirectory(elsewhere);
        var backups = Path.Combine(_root, "UltimateUpdater", "driver-backups");
        Directory.CreateDirectory(backups);
        var child = Path.Combine(backups, name);
        CreateJunction(child, elsewhere);

        try
        {
            using var result = DriverBackupStaging.PrepareStagingRoot(new LoggingService());

            Assert.Null(result);
            Assert.Empty(Directory.GetFileSystemEntries(elsewhere));
        }
        finally
        {
            try { Directory.Delete(child); } catch { }
            try { Directory.Delete(elsewhere, recursive: true); } catch { }
        }
    }

    [Fact]
    public void PrepareStagingRoot_refuses_a_child_that_was_pre_created_as_a_plain_folder()
    {
        const string name = "2026-10-03_120001";
        DriverBackupStaging.StagingNameOverride = name;
        var child = Path.Combine(_root, "UltimateUpdater", "driver-backups", name);
        Directory.CreateDirectory(child);
        File.WriteAllText(Path.Combine(child, "planted.txt"), "x");

        using var result = DriverBackupStaging.PrepareStagingRoot(new LoggingService());

        Assert.Null(result);
        Assert.True(File.Exists(Path.Combine(child, "planted.txt")), "nothing pre-existing is deleted or adopted");
    }

    /// <summary>A folder at the right path with the right shape but created by someone else (other owner, or
    /// not the exact ACL) is not ours: this is the check that still holds when the create call itself
    /// raced a planted directory.</summary>
    [Fact]
    public void A_folder_created_by_someone_else_with_a_loose_acl_does_not_verify_as_ours()
    {
        var child = Path.Combine(_root, "loose-" + Guid.NewGuid());
        Directory.CreateDirectory(child);

        using var pin = CleanupSafety.TryLockForUse(child);
        Assert.NotNull(pin);

        Assert.False(DriverBackupStaging.IsOurOwnNewFolder(pin!, child));
    }

    [Fact]
    public void A_freshly_staged_folder_verifies_as_ours_and_stays_pinned_until_disposed()
    {
        using var staging = DriverBackupStaging.PrepareStagingRoot(new LoggingService());
        Assert.NotNull(staging);
        Assert.True(staging!.IsStillSafe());

        // The handle has no delete share: the folder and its parent cannot be moved or removed meanwhile.
        var moved = staging.BackupRoot + "-moved";
        Assert.ThrowsAny<IOException>(() => Directory.Move(staging.BackupRoot, moved));
        Assert.ThrowsAny<IOException>(() =>
            Directory.Move(Path.GetDirectoryName(staging.BackupRoot)!, Path.GetDirectoryName(staging.BackupRoot)! + "-moved"));
        Assert.True(Directory.Exists(staging.BackupRoot));

        var backupRoot = staging.BackupRoot;
        staging.Dispose();

        Assert.False(staging.IsStillSafe(), "an unpinned folder is no longer vouched for");
        Assert.True(Directory.Exists(backupRoot));
    }


    private static readonly SecurityIdentifier UsersSid = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier AdminsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier SystemAccount = new(WellKnownSidType.LocalSystemSid, null);

    /// <summary>ProgramData lets users create subfolders there, so the two levels above the staging folder are
    /// born with the protected admin-only ACL instead of inheriting it.</summary>
    [Theory]
    [InlineData("UltimateUpdater")]
    [InlineData("UltimateUpdater/driver-backups")]
    public void The_parent_levels_are_created_with_a_protected_admin_only_acl(string relative)
    {
        using var result = DriverBackupStaging.PrepareStagingRoot(new LoggingService());
        Assert.NotNull(result);

        var security = new DirectoryInfo(Path.Combine(_root, relative)).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);

        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();

        // exactly the staging ACL; the process' own account is the one possible extra (see ParentSecurity)
        var own = System.Security.Principal.WindowsIdentity.GetCurrent().Owner;
        Assert.All(rules, r => Assert.True(
            r.IdentityReference.Equals(UsersSid) || r.IdentityReference.Equals(AdminsSid)
            || r.IdentityReference.Equals(SystemAccount) || r.IdentityReference.Equals(own)));

        var users = Assert.Single(rules, r => r.IdentityReference.Equals(UsersSid));
        Assert.False(users.FileSystemRights.HasFlag(FileSystemRights.WriteData));
        Assert.False(users.FileSystemRights.HasFlag(FileSystemRights.AppendData));
        Assert.False(users.FileSystemRights.HasFlag(FileSystemRights.Delete));
        Assert.Contains(rules, r => r.IdentityReference.Equals(AdminsSid) && r.FileSystemRights.HasFlag(FileSystemRights.FullControl));
        Assert.Contains(rules, r => r.IdentityReference.Equals(SystemAccount) && r.FileSystemRights.HasFlag(FileSystemRights.FullControl));
    }

    [Fact]
    public void An_owner_is_trusted_only_when_it_is_an_administrator_system_or_this_process()
    {
        var self = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);

        Assert.True(DriverBackupStaging.IsTrustedFolderOwner(AdminsSid, null));
        Assert.True(DriverBackupStaging.IsTrustedFolderOwner(SystemAccount, null));
        Assert.True(DriverBackupStaging.IsTrustedFolderOwner(self, self));
        Assert.False(DriverBackupStaging.IsTrustedFolderOwner(UsersSid, null));
        Assert.False(DriverBackupStaging.IsTrustedFolderOwner(UsersSid, self));
    }

    /// <summary>A same-user process that pre-created the app folder owns it, and the admin worker must not
    /// build the staging tree inside it. Skipped where the creating account is itself an administrator owner.</summary>
    [Fact]
    public void PrepareStagingRoot_refuses_an_existing_app_folder_owned_by_someone_else()
    {
        var appRoot = Path.Combine(_root, "UltimateUpdater");
        Directory.CreateDirectory(appRoot);

        var owner = new DirectoryInfo(appRoot).GetAccessControl().GetOwner(typeof(SecurityIdentifier));
        if (owner is null || owner.Equals(AdminsSid) || owner.Equals(SystemAccount))
            return;

        DriverBackupStaging.TokenOwnerOverride = UsersSid;
        try
        {
            using var result = DriverBackupStaging.PrepareStagingRoot(new LoggingService());

            Assert.Null(result);
            Assert.False(Directory.Exists(Path.Combine(appRoot, "driver-backups")), "nothing is created inside it");
        }
        finally
        {
            DriverBackupStaging.TokenOwnerOverride = null;
        }
    }

    [Fact]
    public void PrepareStagingRoot_refuses_an_app_folder_that_is_a_junction()
    {
        var elsewhere = Path.Combine(Path.GetTempPath(), "wu-driverstaging-app-" + Guid.NewGuid());
        Directory.CreateDirectory(elsewhere);
        var appRoot = Path.Combine(_root, "UltimateUpdater");
        CreateJunction(appRoot, elsewhere);

        try
        {
            using var result = DriverBackupStaging.PrepareStagingRoot(new LoggingService());

            Assert.Null(result);
            Assert.Empty(Directory.GetFileSystemEntries(elsewhere));
        }
        finally
        {
            try { Directory.Delete(appRoot); } catch { }
            try { Directory.Delete(elsewhere, recursive: true); } catch { }
        }
    }

    private static void CreateJunction(string link, string target)
    {
        using var mklink = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        mklink.WaitForExit(5000);
        Assert.Equal(0, mklink.ExitCode);
    }

    /// <summary>
    /// A same-user attacker who cannot write under the real staging folder's normal parent could still
    /// plant a junction one level up (here: the ProgramData root itself, stood in for by the override) and
    /// have it silently followed into a location this process does not intend to write admin-owned files
    /// under. The staging code must refuse outright rather than create anything through it.
    /// </summary>
    [Fact]
    public void PrepareStagingRoot_refuses_when_the_root_is_reached_through_a_junction()
    {
        var realTarget = Path.Combine(Path.GetTempPath(), "wu-driverstaging-target-" + Guid.NewGuid());
        Directory.CreateDirectory(realTarget);

        // Replace the plain override directory with a junction pointing elsewhere: PrepareStagingRoot must
        // detect this at the FIRST existing ancestor of the target path, not just at a leaf it creates.
        Directory.Delete(_root);
        CreateJunction(_root, realTarget);

        try
        {
            var result = DriverBackupStaging.PrepareStagingRoot(new LoggingService());

            Assert.Null(result);
            Assert.False(Directory.Exists(Path.Combine(realTarget, "UltimateUpdater")));
        }
        finally
        {
            try { Directory.Delete(_root); } catch { }
            try { Directory.Delete(realTarget, recursive: true); } catch { }
        }
    }
}
