using System.Security.AccessControl;
using System.Security.Principal;
using UltimateUpdater.Services.Uninstall;

namespace UltimateUpdater.Tests;

public class UninstallKeyGuardTests
{
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier Creator = new(WellKnownSidType.CreatorOwnerSid, null);

    private static RegistrySecurity Acl(SecurityIdentifier owner, params RegistryAccessRule[] rules)
    {
        var security = new RegistrySecurity();
        security.SetOwner(owner);
        foreach (var rule in rules)
            security.AddAccessRule(rule);
        return security;
    }

    private static RegistryAccessRule Allow(
        SecurityIdentifier sid, RegistryRights rights, PropagationFlags propagation = PropagationFlags.None) =>
        new(sid, rights, InheritanceFlags.ContainerInherit, propagation, AccessControlType.Allow);

    [Fact]
    public void A_key_only_administrators_and_system_can_write_is_admin_only()
    {
        var acl = Acl(Admins, Allow(Admins, RegistryRights.FullControl), Allow(LocalSystem, RegistryRights.FullControl),
            Allow(Users, RegistryRights.ReadKey));

        Assert.True(RegistryUninstallKeyGuard.IsAdminOnly(acl));
    }

    [Theory]
    [InlineData(RegistryRights.SetValue)]
    [InlineData(RegistryRights.CreateSubKey)]
    [InlineData(RegistryRights.ChangePermissions)]
    [InlineData(RegistryRights.TakeOwnership)]
    [InlineData(RegistryRights.FullControl)]
    public void A_key_users_hold_a_write_right_on_is_not_admin_only(RegistryRights right)
    {
        var acl = Acl(Admins, Allow(Admins, RegistryRights.FullControl), Allow(Users, right));

        Assert.False(RegistryUninstallKeyGuard.IsAdminOnly(acl));
    }

    [Fact]
    public void The_stock_terminal_server_user_entry_of_the_uninstall_root_passes_on_a_desktop_edition_only()
    {
        var terminalServer = new SecurityIdentifier(WellKnownSidType.TerminalServerSid, null);
        var acl = Acl(Admins, Allow(Admins, RegistryRights.FullControl), Allow(LocalSystem, RegistryRights.FullControl),
            Allow(Users, RegistryRights.ReadKey),
            Allow(terminalServer, RegistryRights.SetValue | RegistryRights.CreateSubKey | RegistryRights.Delete | RegistryRights.ReadKey));

        Assert.True(RegistryUninstallKeyGuard.IsAdminOnly(acl, desktopEdition: true));
        Assert.False(RegistryUninstallKeyGuard.IsAdminOnly(acl, desktopEdition: false));
        Assert.False(RegistryUninstallKeyGuard.IsAdminOnly(acl));
    }

    [Fact]
    public void A_desktop_edition_still_refuses_a_key_users_can_write()
    {
        var terminalServer = new SecurityIdentifier(WellKnownSidType.TerminalServerSid, null);
        var acl = Acl(Admins, Allow(Admins, RegistryRights.FullControl), Allow(terminalServer, RegistryRights.SetValue),
            Allow(Users, RegistryRights.SetValue));

        Assert.False(RegistryUninstallKeyGuard.IsAdminOnly(acl, desktopEdition: true));
    }

    [Fact]
    public void A_key_owned_by_a_user_is_not_admin_only()
    {
        var acl = Acl(Users, Allow(Admins, RegistryRights.FullControl));

        Assert.False(RegistryUninstallKeyGuard.IsAdminOnly(acl));
    }

    [Fact]
    public void Creator_owner_counts_as_the_owner_and_is_ignored_when_inherit_only()
    {
        var inheritOnly = Acl(Admins, Allow(Admins, RegistryRights.FullControl),
            Allow(Creator, RegistryRights.FullControl, PropagationFlags.InheritOnly));
        var applying = Acl(Users, Allow(Admins, RegistryRights.FullControl), Allow(Creator, RegistryRights.FullControl));

        Assert.True(RegistryUninstallKeyGuard.IsAdminOnly(inheritOnly));
        Assert.False(RegistryUninstallKeyGuard.IsAdminOnly(applying));
    }

    [Fact]
    public void A_key_outside_the_machine_hive_is_not_admin_only()
    {
        Assert.False(new RegistryUninstallKeyGuard().IsAdminOnly(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\Foo"));
    }
}
