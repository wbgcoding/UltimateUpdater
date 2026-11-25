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
    /// Uninstall root, so every key a setup creates without an ACL of its own inherits it. Only a logon on an RD Session
    /// Host carries this group, which a desktop edition cannot be.</summary>
    private static readonly SecurityIdentifier TerminalServerUser = new(WellKnownSidType.TerminalServerSid, null);

    // Raw generic bits can sit in a stored ACE; they imply every right below, so they count.
    private const RegistryRights Generic = (RegistryRights)0x10000000 | (RegistryRights)0x40000000;

    private const RegistryRights Forbidden =
        RegistryRights.SetValue | RegistryRights.CreateSubKey | RegistryRights.ChangePermissions
        | RegistryRights.TakeOwnership | Generic;

    private const string HklmPrefix = @"HKLM\";

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
                       key.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner), IsDesktopEdition());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException
                                       or ArgumentException or InvalidOperationException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static bool IsTrusted(SecurityIdentifier sid) =>
        sid.Equals(Administrators) || sid.Equals(LocalSystem) || sid.Equals(TrustedInstaller);

    /// <summary>True on a Windows desktop edition (product type WinNT). A value that cannot be read counts as a server.</summary>
    private static bool IsDesktopEdition()
    {
        try
        {
            using var options = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\ProductOptions");
            return string.Equals(options?.GetValue("ProductType") as string, "WinNT", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// The owner must be trusted (an owner always holds WRITE_DAC), and no other identity may hold a forbidden
    /// right in an allow entry that applies to the key itself. CREATOR OWNER counts as the owner when it
    /// applies to the key and is ignored when inherit-only (a template for children). Deny entries are not
    /// subtracted, so a mixed ACL errs towards refusing. On a desktop edition the Terminal Server User group counts as
    /// trusted (no logon there carries it, and the stock ACL of the Uninstall root hands it write rights that every
    /// ordinary setup's key inherits); on a server it does not, since an RD Session Host logon holds it.
    /// </summary>
    internal static bool IsAdminOnly(RegistrySecurity security, bool desktopEdition = false)
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

            if (IsTrusted(sid) || (desktopEdition && sid.Equals(TerminalServerUser)))
                continue;

            if ((rule.RegistryRights & Forbidden) != 0)
                return false;
        }

        return true;
    }
}
