using System.IO;
using System.Runtime.Versioning;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

namespace UltimateUpdater.Services.Uninstall;

/// <summary>Whether a machine Uninstall key can only be changed by administrators; a seam so tests need no real keys.</summary>
public interface IUninstallKeyGuard
{
    /// <summary>True when only SYSTEM, administrators and TrustedInstaller can write the key (values, subkeys,
    /// permissions, owner). A key that cannot be read counts as not admin-only.</summary>
    bool IsAdminOnly(string keyPath);
}

/// <summary>
/// The guard over the real registry. The elevated worker takes a command out of an HKLM Uninstall key, so the
/// key has to be one an unelevated process of the user could not have written: a key whose ACL lets Users,
/// Authenticated Users or Everyone set a value is a way to put a command into an administrator's hands.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RegistryUninstallKeyGuard : IUninstallKeyGuard
{
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier TrustedInstaller =
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");
    private static readonly SecurityIdentifier CreatorOwner = new(WellKnownSidType.CreatorOwnerSid, null);

    /// <summary>"Terminal Server User" (S-1-5-13). Stock Windows grants it set value, create subkey and delete on the
    /// Uninstall root, so every key a setup creates without an ACL of its own inherits it. Any remote desktop logon
    /// (RD Session Host, multi-session editions, a client with remote users enabled) carries this group.</summary>
    private static readonly SecurityIdentifier TerminalServerUser = new(WellKnownSidType.TerminalServerSid, null);

    // Raw generic bits can sit in a stored ACE; they imply every right below, so they count.
    private const RegistryRights Generic = (RegistryRights)0x10000000 | (RegistryRights)0x40000000;

    private const RegistryRights Forbidden =
        RegistryRights.SetValue | RegistryRights.CreateSubKey | RegistryRights.ChangePermissions
        | RegistryRights.TakeOwnership | Generic;

    private const RegistryRights GenericRead = unchecked((RegistryRights)0x80000000);
    private const RegistryRights GenericWrite = (RegistryRights)0x40000000;

    // What the stock Terminal Server User entry may carry: read, write (set value, create subkey) and delete.
    private const RegistryRights TerminalServerUserAllowed =
        RegistryRights.ReadKey | RegistryRights.WriteKey | RegistryRights.Delete;

    private const string HklmPrefix = @"HKLM\";

    private readonly Func<TerminalServerGateValues> _readGate;

    public RegistryUninstallKeyGuard() : this(ReadGateValues)
    {
    }

    internal RegistryUninstallKeyGuard(Func<TerminalServerGateValues> readGate) => _readGate = readGate;

    /// <summary>The raw values the Terminal Server User trust decision rests on; null = missing or unreadable.</summary>
    internal readonly record struct TerminalServerGateValues(object? TsUserEnabled, object? EditionId, object? ProductType);

    public bool IsAdminOnly(string keyPath)
    {
        if (!keyPath.StartsWith(HklmPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            // The 64 bit view with the literal path: the WOW6432Node root is a real subkey there.
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hive.OpenSubKey(
                keyPath[HklmPrefix.Length..], RegistryKeyPermissionCheck.ReadSubTree, RegistryRights.ReadPermissions);
            return key is not null
                   && IsAdminOnly(
                       key.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner), TrustsTerminalServerUser());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException
                                       or ArgumentException or InvalidOperationException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static bool IsTrusted(SecurityIdentifier sid) =>
        sid.Equals(Administrators) || sid.Equals(LocalSystem) || sid.Equals(TrustedInstaller);

    /// <summary>True when no logon token carries S-1-5-13 (the Terminal Server User SID) on this machine: TSUserEnabled is
    /// DWORD 0 (remote desktop itself stays possible), the edition is not the multi-session one (EditionID ServerRdsh)
    /// and the product type is WinNT. Any missing, unreadable or other value counts as untrusted.</summary>
    internal bool TrustsTerminalServerUser()
    {
        var values = _readGate();
        return values.TsUserEnabled is int and 0
               && values.EditionId is string edition
               && !string.Equals(edition, "ServerRdsh", StringComparison.OrdinalIgnoreCase)
               && values.ProductType is string type
               && string.Equals(type, "WinNT", StringComparison.OrdinalIgnoreCase);
    }

    private static TerminalServerGateValues ReadGateValues()
    {
        try
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            return new TerminalServerGateValues(
                ReadValue(hive, @"SYSTEM\CurrentControlSet\Control\Terminal Server", "TSUserEnabled"),
                ReadValue(hive, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "EditionID"),
                ReadValue(hive, @"SYSTEM\CurrentControlSet\Control\ProductOptions", "ProductType"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return default;
        }
    }

    private static object? ReadValue(RegistryKey hive, string path, string name)
    {
        try
        {
            using var key = hive.OpenSubKey(path);
            return key?.GetValue(name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return null;
        }
    }

    /// <summary>
    /// The owner must be trusted (an owner always holds WRITE_DAC), and no other identity may hold a forbidden
    /// right in an allow entry that applies to the key itself. CREATOR OWNER counts as the owner when it
    /// applies to the key and is ignored when inherit-only (a template for children). Deny entries are not
    /// subtracted, so a mixed ACL errs towards refusing. Where no remote desktop logon can exist
    /// (<paramref name="trustTerminalServerUser"/>) the Terminal Server User group is skipped as long as its entry
    /// carries nothing beyond read, write and delete (the stock ACL of the Uninstall root hands it those, and every
    /// ordinary setup's key inherits them); anywhere else it is checked like any other identity.
    /// </summary>
    internal static bool IsAdminOnly(RegistrySecurity security, bool trustTerminalServerUser = false)
    {
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !IsTrusted(owner))
            return false;

        foreach (RegistryAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow)
                continue;

            if (rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly))
                continue;

            if (rule.IdentityReference is not SecurityIdentifier sid)
                return false;

            if (sid.Equals(CreatorOwner))
                sid = owner;

            if (IsTrusted(sid))
                continue;

            if (trustTerminalServerUser && sid.Equals(TerminalServerUser) && !CarriesMoreThanStockRights(rule.RegistryRights))
                continue;

            if ((rule.RegistryRights & Forbidden) != 0)
                return false;
        }

        return true;
    }

    /// <summary>True when the rights hold anything beyond read, write and delete; GENERIC_READ and GENERIC_WRITE
    /// count as the read and write sets, every other generic bit, WRITE_DAC and WRITE_OWNER as more.</summary>
    private static bool CarriesMoreThanStockRights(RegistryRights rights) =>
        (rights & ~(TerminalServerUserAllowed | GenericRead | GenericWrite)) != 0;
}
