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
