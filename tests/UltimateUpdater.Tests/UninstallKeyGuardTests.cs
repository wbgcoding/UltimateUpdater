using System.Security.AccessControl;
using System.Security.Principal;
using UltimateUpdater.Services.Uninstall;
using GateValues = UltimateUpdater.Services.Uninstall.RegistryUninstallKeyGuard.TerminalServerGateValues;

namespace UltimateUpdater.Tests;

public class UninstallKeyGuardTests
{
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier Creator = new(WellKnownSidType.CreatorOwnerSid, null);
    private static readonly SecurityIdentifier TerminalServer = new(WellKnownSidType.TerminalServerSid, null);

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

    private static RegistrySecurity StockAcl(RegistryRights terminalServerRights) =>
        Acl(Admins, Allow(Admins, RegistryRights.FullControl), Allow(LocalSystem, RegistryRights.FullControl),
            Allow(Users, RegistryRights.ReadKey), Allow(TerminalServer, terminalServerRights));

    private static readonly GateValues GoodGate = new(0, "Professional", "WinNT");

    private static bool Trusts(GateValues values) => new RegistryUninstallKeyGuard(() => values).TrustsTerminalServerUser();

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
    public void The_stock_terminal_server_user_entry_of_the_uninstall_root_passes_only_when_trusted()
    {
        var acl = StockAcl(RegistryRights.SetValue | RegistryRights.CreateSubKey | RegistryRights.Delete | RegistryRights.ReadKey);

        Assert.True(RegistryUninstallKeyGuard.IsAdminOnly(acl, trustTerminalServerUser: true));
        Assert.False(RegistryUninstallKeyGuard.IsAdminOnly(acl, trustTerminalServerUser: false));
        Assert.False(RegistryUninstallKeyGuard.IsAdminOnly(acl));
    }

    [Fact]
    public void The_generic_read_write_and_delete_bits_of_the_stock_entry_count_as_read_write_and_delete()
    {
        var acl = StockAcl(
            unchecked((RegistryRights)0x80000000) | (RegistryRights)0x40000000 | RegistryRights.Delete);

        Assert.True(RegistryUninstallKeyGuard.IsAdminOnly(acl, trustTerminalServerUser: true));
        Assert.False(RegistryUninstallKeyGuard.IsAdminOnly(acl));
    }

    [Theory]
    [InlineData(RegistryRights.ChangePermissions)]
    [InlineData(RegistryRights.TakeOwnership)]
    [InlineData(RegistryRights.FullControl)]
    [InlineData((RegistryRights)0x10000000)]
    public void A_trusted_terminal_server_user_entry_with_more_than_read_write_and_delete_is_refused(RegistryRights extra)
    {
        var acl = StockAcl(RegistryRights.SetValue | RegistryRights.CreateSubKey | RegistryRights.Delete | RegistryRights.ReadKey | extra);

        Assert.False(RegistryUninstallKeyGuard.IsAdminOnly(acl, trustTerminalServerUser: true));
    }

    [Fact]
    public void A_terminal_server_user_entry_that_alone_changes_permissions_or_owner_is_refused()
    {
        Assert.False(RegistryUninstallKeyGuard.IsAdminOnly(StockAcl(RegistryRights.ChangePermissions), trustTerminalServerUser: true));
        Assert.False(RegistryUninstallKeyGuard.IsAdminOnly(StockAcl(RegistryRights.TakeOwnership), trustTerminalServerUser: true));
    }

    [Fact]
    public void A_trusted_machine_still_refuses_a_key_users_can_write()
    {
        var acl = Acl(Admins, Allow(Admins, RegistryRights.FullControl), Allow(TerminalServer, RegistryRights.SetValue),
            Allow(Users, RegistryRights.SetValue));

        Assert.False(RegistryUninstallKeyGuard.IsAdminOnly(acl, trustTerminalServerUser: true));
    }

    [Fact]
    public void The_terminal_server_user_group_is_trusted_when_every_gate_value_is_good()
    {
        Assert.True(Trusts(GoodGate));
    }

    [Fact]
    public void Remote_users_enabled_keeps_the_terminal_server_user_group_untrusted()
    {
        Assert.False(Trusts(GoodGate with { TsUserEnabled = 1 }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData(2)]
    public void A_missing_or_odd_remote_users_value_keeps_the_group_untrusted(object? value)
    {
        Assert.False(Trusts(GoodGate with { TsUserEnabled = value }));
    }

    [Theory]
    [InlineData("ServerRdsh")]
    [InlineData("serverrdsh")]
    [InlineData(null)]
    [InlineData(5)]
    public void The_multi_session_edition_or_an_unreadable_edition_keeps_the_group_untrusted(object? edition)
    {
        Assert.False(Trusts(GoodGate with { EditionId = edition }));
    }

    [Theory]
    [InlineData("ServerNT")]
    [InlineData("LanmanNT")]
    [InlineData(null)]
    public void A_server_product_type_or_an_unreadable_one_keeps_the_group_untrusted(object? productType)
    {
        Assert.False(Trusts(GoodGate with { ProductType = productType }));
    }

    [Fact]
    public void A_gate_that_could_not_be_read_at_all_keeps_the_group_untrusted()
    {
        Assert.False(Trusts(default));
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
