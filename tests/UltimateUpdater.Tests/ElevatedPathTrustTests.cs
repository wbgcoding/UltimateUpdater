using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

/// <summary>
/// The location rule for an elevated launch: a signed host in a folder a user can write is code execution
/// as admin, so the file, its own directory and every ancestor must only be changeable by administrators,
/// SYSTEM or TrustedInstaller.
/// </summary>
public class ElevatedPathTrustTests
{
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Installer =
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier Authenticated = new(WellKnownSidType.AuthenticatedUserSid, null);
    private static readonly SecurityIdentifier Everyone = new(WellKnownSidType.WorldSid, null);
    private static readonly SecurityIdentifier Interactive = new(WellKnownSidType.InteractiveSid, null);
    private static readonly SecurityIdentifier CreatorOwner = new(WellKnownSidType.CreatorOwnerSid, null);
    private static readonly SecurityIdentifier CurrentUser = WindowsIdentity.GetCurrent().User!;

    private static DirectorySecurity Dir(SecurityIdentifier owner, params (SecurityIdentifier Sid, FileSystemRights Rights)[] allows)
        => Dir(owner, inheritOnly: false, allows);

    private static DirectorySecurity Dir(
        SecurityIdentifier? owner, bool inheritOnly, params (SecurityIdentifier Sid, FileSystemRights Rights)[] allows)
    {
        var security = new DirectorySecurity();
        if (owner is not null)
            security.SetOwner(owner);

        foreach (var (sid, rights) in allows)
        {
            security.AddAccessRule(inheritOnly
                ? new FileSystemAccessRule(sid, rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.InheritOnly, AccessControlType.Allow)
                : new FileSystemAccessRule(sid, rights, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        }

        return security;
    }

    private static FileSecurity Fil(SecurityIdentifier? owner, params (SecurityIdentifier Sid, FileSystemRights Rights)[] allows)
    {
        var security = new FileSecurity();
        if (owner is not null)
            security.SetOwner(owner);

        foreach (var (sid, rights) in allows)
            security.AddAccessRule(new FileSystemAccessRule(sid, rights, AccessControlType.Allow));

        return security;
    }

    private static readonly (SecurityIdentifier, FileSystemRights)[] StockTrusted =
    [
        (Installer, FileSystemRights.FullControl),
        (SystemSid, FileSystemRights.FullControl),
        (Admins, FileSystemRights.FullControl),
        (Users, FileSystemRights.ReadAndExecute),
    ];

    public static TheoryData<string> UntrustedPrincipals => new() { "Users", "Authenticated", "Everyone", "Interactive", "CurrentUser" };

    private static SecurityIdentifier Principal(string name) => name switch
    {
        "Users" => Users,
        "Authenticated" => Authenticated,
        "Everyone" => Everyone,
        "Interactive" => Interactive,
        _ => CurrentUser,
    };

    [Fact]
    public void A_stock_admin_owned_directory_is_accepted_in_all_three_roles()
    {
        Assert.True(ElevatedPathTrust.IsSafeOwnDirectory(Dir(Installer, StockTrusted)));
        Assert.True(ElevatedPathTrust.IsSafeAncestor(Dir(Installer, StockTrusted)));
        Assert.True(ElevatedPathTrust.IsSafeFile(Fil(Installer, StockTrusted)));
    }

    [Theory]
    [MemberData(nameof(UntrustedPrincipals))]
    public void Rule_a_refuses_an_untrusted_principal_with_modify_on_the_files_own_directory(string who) =>
        Assert.False(ElevatedPathTrust.IsSafeOwnDirectory(
            Dir(Admins, (Admins, FileSystemRights.FullControl), (Principal(who), FileSystemRights.Modify))));

    [Theory]
    [InlineData(FileSystemRights.WriteData)]
    [InlineData(FileSystemRights.AppendData)]
    [InlineData(FileSystemRights.DeleteSubdirectoriesAndFiles)]
    [InlineData(FileSystemRights.ChangePermissions)]
    [InlineData(FileSystemRights.TakeOwnership)]
    [InlineData(FileSystemRights.Delete)]
    public void Rule_a_refuses_each_single_dangerous_right_on_the_own_directory(FileSystemRights right) =>
        Assert.False(ElevatedPathTrust.IsSafeOwnDirectory(
            Dir(Admins, (Admins, FileSystemRights.FullControl), (Users, FileSystemRights.ReadAndExecute | right))));

    [Theory]
    [InlineData("GW")]
    [InlineData("GA")]
    public void Rule_a_refuses_a_raw_generic_bit_the_rights_enum_has_no_name_for(string generic)
    {
        // Straight from SDDL, because the rule constructor refuses to build such a mask itself.
        var security = new DirectorySecurity();
        security.SetSecurityDescriptorSddlForm($"O:BAG:SYD:(A;;FA;;;BA)(A;;{generic};;;BU)");

        Assert.False(ElevatedPathTrust.IsSafeOwnDirectory(security));
    }

    [Fact]
    public void Rule_a_counts_an_inherit_only_entry_on_the_own_directory_because_it_is_the_right_on_every_sibling()
    {
        // "Users: Modify (OI)(IO)" is what every existing DLL next to the host effectively grants.
        Assert.False(ElevatedPathTrust.IsSafeOwnDirectory(
            Dir(Installer, inheritOnly: true, (Authenticated, FileSystemRights.Modify))));
    }

    [Theory]
    [InlineData(InheritanceFlags.ObjectInherit)]
    [InlineData(InheritanceFlags.ContainerInherit)]
    public void Rule_a_counts_an_inherit_only_entry_that_reaches_files_or_subfolders(InheritanceFlags inherit)
    {
        var security = Dir(Installer, (Installer, FileSystemRights.FullControl));
        security.AddAccessRule(new FileSystemAccessRule(
            Users, FileSystemRights.Write, inherit, PropagationFlags.InheritOnly, AccessControlType.Allow));

        Assert.False(ElevatedPathTrust.IsSafeOwnDirectory(security));
    }

    [Fact]
    public void Rule_a_accepts_an_inherit_only_entry_that_grants_only_reading()
    {
        Assert.True(ElevatedPathTrust.IsSafeOwnDirectory(
            Dir(Installer, inheritOnly: true, (Users, FileSystemRights.ReadAndExecute))));
    }

    [Fact]
    public void Rule_b_still_skips_an_inherit_only_entry_on_an_ancestor()
    {
        // C:\ carries exactly this for Authenticated Users, and C:\ itself must stay acceptable.
        Assert.True(ElevatedPathTrust.IsSafeAncestor(
            Dir(Installer, inheritOnly: true, (Authenticated, FileSystemRights.Modify))));
    }

    [Theory]
    [InlineData(FileSystemRights.WriteData)]
    [InlineData(FileSystemRights.AppendData)]
    public void Rule_b_accepts_an_ancestor_that_lets_users_create_children(FileSystemRights right) =>
        Assert.True(ElevatedPathTrust.IsSafeAncestor(
            Dir(Installer, (Installer, FileSystemRights.FullControl), (Authenticated, right))));

    [Theory]
    [InlineData(FileSystemRights.DeleteSubdirectoriesAndFiles)]
    [InlineData(FileSystemRights.ChangePermissions)]
    [InlineData(FileSystemRights.TakeOwnership)]
    [InlineData(FileSystemRights.Delete)]
    public void Rule_b_refuses_an_ancestor_where_an_untrusted_principal_can_replace_a_child(FileSystemRights right) =>
        Assert.False(ElevatedPathTrust.IsSafeAncestor(
            Dir(Installer, (Installer, FileSystemRights.FullControl), (Users, FileSystemRights.ReadAndExecute | right))));

    [Theory]
    [InlineData(FileSystemRights.WriteData)]
    [InlineData(FileSystemRights.AppendData)]
    [InlineData(FileSystemRights.Delete)]
    [InlineData(FileSystemRights.ChangePermissions)]
    [InlineData(FileSystemRights.TakeOwnership)]
    public void Rule_c_refuses_each_single_dangerous_right_on_the_file_itself(FileSystemRights right) =>
        Assert.False(ElevatedPathTrust.IsSafeFile(
            Fil(Installer, (Installer, FileSystemRights.FullControl), (Users, FileSystemRights.ReadAndExecute | right))));

    [Fact]
    public void A_creator_owner_entry_counts_as_the_owner_and_is_trusted_only_for_a_trusted_owner()
    {
        Assert.True(ElevatedPathTrust.IsSafeOwnDirectory(
            Dir(Admins, (Admins, FileSystemRights.FullControl), (CreatorOwner, FileSystemRights.FullControl))));

        // the same entry with an owner that is the current user: refused (through the owner rule AND through
        // the entry itself resolving to that untrusted owner)
        Assert.False(ElevatedPathTrust.IsSafeOwnDirectory(
            Dir(CurrentUser, (Admins, FileSystemRights.FullControl), (CreatorOwner, FileSystemRights.FullControl))));
    }

    [Fact]
    public void An_owner_outside_the_three_trusted_principals_is_refused_even_with_a_clean_acl()
    {
        // an owner always holds WRITE_DAC, so the entries alone prove nothing
        Assert.False(ElevatedPathTrust.IsSafeOwnDirectory(Dir(CurrentUser, (Admins, FileSystemRights.FullControl))));
        Assert.False(ElevatedPathTrust.IsSafeAncestor(Dir(CurrentUser, (Admins, FileSystemRights.FullControl))));
        Assert.False(ElevatedPathTrust.IsSafeFile(Fil(CurrentUser, (Admins, FileSystemRights.FullControl))));
    }

    [Fact]
    public void A_missing_owner_is_refused()
    {
        Assert.False(ElevatedPathTrust.IsSafeOwnDirectory(Dir(null, inheritOnly: false, (Admins, FileSystemRights.FullControl))));
        Assert.False(ElevatedPathTrust.IsSafeFile(Fil(null, (Admins, FileSystemRights.FullControl))));
    }

    [Fact]
    public void A_deny_entry_does_not_rescue_an_untrusted_allow_entry()
    {
        var security = Dir(Admins, (Admins, FileSystemRights.FullControl), (Users, FileSystemRights.Modify));
        security.AddAccessRule(new FileSystemAccessRule(
            Users, FileSystemRights.WriteData, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny));

        Assert.False(ElevatedPathTrust.IsSafeOwnDirectory(security));
    }

    [Fact]
    public void The_real_acls_of_system32_cmd_and_program_files_are_accepted()
    {
        var cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        using (var pin = new FileStream(cmd, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.True(ElevatedPathTrust.IsTrustedLocation(cmd, pin));

        Assert.True(ElevatedPathTrust.IsTrustedDirectory(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)));
    }

    [Fact]
    public void A_temp_folder_granting_users_modify_is_refused_and_so_is_its_signed_content()
    {
        var folder = Directory.CreateTempSubdirectory("wu-acl-").FullName;
        try
        {
            var security = new DirectoryInfo(folder).GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(
                Users, FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(folder).SetAccessControl(security);

            Assert.False(ElevatedPathTrust.IsTrustedDirectory(folder));

            var copy = Path.Combine(folder, "tool.exe");
            File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe"), copy);

            Assert.Throws<UntrustedToolException>(() => ProcessRunner.ResolveForPrivilege(copy, elevated: true));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>The finding itself: a signed host in a folder outside C:\Users that this user can write.
    /// The test output directory is such a folder.</summary>
    [Fact]
    public void A_signed_binary_in_a_user_writable_folder_outside_the_profile_is_refused()
    {
        var copy = Path.Combine(AppContext.BaseDirectory, $"wu-loc-{Guid.NewGuid():N}.exe");
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe"), copy);

        try
        {
            Assert.Throws<UntrustedToolException>(() => ProcessRunner.ResolveForPrivilege(copy, elevated: true));
        }
        finally
        {
            File.Delete(copy);
        }
    }

    /// <summary>A junction in the path could be re-pointed between the check and the launch, so a path that is
    /// not its own final path is refused even when the target directory is perfectly trustworthy.</summary>
    [Fact]
    public void A_junction_in_the_path_fails_closed_even_when_it_points_at_a_trusted_folder()
    {
        var parent = Directory.CreateTempSubdirectory("wu-junction-").FullName;
        var link = Path.Combine(parent, "sys");
        CreateJunction(link, Environment.GetFolderPath(Environment.SpecialFolder.System));

        try
        {
            var viaLink = Path.Combine(link, "cmd.exe");
            using var pin = new FileStream(viaLink, FileMode.Open, FileAccess.Read, FileShare.Read);

            Assert.False(ElevatedPathTrust.IsTrustedLocation(viaLink, pin));
            Assert.Throws<UntrustedToolException>(() => ProcessRunner.ResolveForPrivilege(viaLink, elevated: true));
        }
        finally
        {
            Directory.Delete(link);
            Directory.Delete(parent, recursive: true);
        }
    }

    private static readonly string NotepadPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");

    private static bool AnyLocation(string path, FileStream pin) => true;

    /// <summary>A chocolatey-shaped tree outside the profile (the test output folder): bin\choco.exe and
    /// the real choco.exe in the root.</summary>
    private static string MakeChocoTree(bool withTarget, bool targetSigned = true)
    {
        var root = Path.Combine(AppContext.BaseDirectory, $"wu-shim-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "bin"));
        File.Copy(NotepadPath, Path.Combine(root, "bin", "choco.exe"));

        if (withTarget && targetSigned)
            File.Copy(NotepadPath, Path.Combine(root, "choco.exe"));
        else if (withTarget)
            File.WriteAllBytes(Path.Combine(root, "choco.exe"), [0x4D, 0x5A, 0x90, 0x00]);

        return root;
    }

    [Fact]
    public void The_chocolatey_shim_target_is_the_choco_exe_in_the_root()
    {
        Assert.Equal(@"C:\choco\choco.exe", ProcessRunner.KnownShimTarget(@"C:\choco\bin\choco.exe"));
        Assert.Null(ProcessRunner.KnownShimTarget(@"C:\choco\choco.exe"));
        Assert.Null(ProcessRunner.KnownShimTarget(@"C:\tools\bin\other.exe"));
    }

    [Fact]
    public void A_shim_whose_target_is_trusted_and_signed_is_accepted_and_both_files_stay_pinned()
    {
        var root = MakeChocoTree(withTarget: true);
        try
        {
            using (var accepted = ProcessRunner.ResolveForPrivilege(Path.Combine(root, "bin", "choco.exe"), elevated: true, AnyLocation))
            {
                Assert.NotNull(accepted.ShimTargetPin);
                Assert.Null(TryOpenExclusive(Path.Combine(root, "choco.exe")));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_shim_is_refused_when_the_image_it_starts_sits_in_an_untrusted_directory()
    {
        var root = MakeChocoTree(withTarget: true);
        try
        {
            var shim = Path.Combine(root, "bin", "choco.exe");
            var target = Path.Combine(root, "choco.exe");

            // the shim's own directory passes, the real image's directory does not
            var refused = Assert.Throws<UntrustedToolException>(() =>
                ProcessRunner.ResolveForPrivilege(shim, elevated: true, (path, pin) => !string.Equals(path, target, StringComparison.OrdinalIgnoreCase)));

            Assert.Equal(target, refused.Path);
            using (var released = TryOpenExclusive(shim))
                Assert.NotNull(released);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void A_shim_is_refused_when_its_target_is_missing_or_unsigned(bool withTarget, bool signed)
    {
        var root = MakeChocoTree(withTarget, signed);
        try
        {
            Assert.Throws<UntrustedToolException>(() =>
                ProcessRunner.ResolveForPrivilege(Path.Combine(root, "bin", "choco.exe"), elevated: true, AnyLocation));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static FileStream? TryOpenExclusive(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
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
        })!;
        mklink.StandardOutput.ReadToEnd();
        mklink.WaitForExit(5000);
        Assert.Equal(0, mklink.ExitCode);
    }
}
