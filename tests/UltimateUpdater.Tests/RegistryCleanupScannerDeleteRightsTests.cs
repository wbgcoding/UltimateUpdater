using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

/// <summary>
/// Several stock HKLM\SOFTWARE\WOW6432Node\Classes\CLSID keys are owned by TrustedInstaller and
/// always fail to delete with "Attempted to perform an unauthorized operation", on every run, once the
/// user confirms cleanup. <see cref="RegistryCleanupScanner.CanDeleteForTest"/> is the gate that now keeps
/// such a finding from being offered at all. Proven against a real HKCU scratch key with an explicit deny
/// ACE, never against a real elevated worker or a real UAC prompt: the ACL is always restored and
/// the key always removed in <c>finally</c>.
/// </summary>
public class RegistryCleanupScannerDeleteRightsTests
{
    private static SecurityIdentifier CurrentUserSid => WindowsIdentity.GetCurrent().User!;

    private static string NewScratchPath() => $@"Software\UltimateUpdaterTests\DeleteRights-{Guid.NewGuid():N}";

    [Fact]
    public void A_key_with_a_deny_delete_ace_is_reported_as_not_deletable()
    {
        var relativePath = NewScratchPath();
        using (var key = Registry.CurrentUser.CreateSubKey(relativePath))
            key!.SetValue("probe", "value");

        try
        {
            DenyOwnDeleteAccess(relativePath);

            var finding = new RegistryFinding(
                RegistryCleanupCategory.ActiveXCom, $@"HKCU\{relativePath}", ValueName: null, "Scratch", "C:\\missing.dll");

            Assert.False(RegistryCleanupScanner.CanDeleteForTest(finding));
        }
        finally
        {
            RestoreOwnAccess(relativePath);
            Registry.CurrentUser.DeleteSubKeyTree(relativePath, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void An_ordinary_key_with_no_deny_ace_is_reported_as_deletable()
    {
        var relativePath = NewScratchPath();
        using (var key = Registry.CurrentUser.CreateSubKey(relativePath))
            key!.SetValue("probe", "value");

        try
        {
            var finding = new RegistryFinding(
                RegistryCleanupCategory.ActiveXCom, $@"HKCU\{relativePath}", ValueName: null, "Scratch", "C:\\missing.dll");

            Assert.True(RegistryCleanupScanner.CanDeleteForTest(finding));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(relativePath, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void A_deny_delete_ace_on_a_child_key_blocks_the_whole_subtree_even_though_the_root_itself_is_open()
    {
        var relativePath = NewScratchPath();
        var childPath = $@"{relativePath}\InprocServer32";
        using (var key = Registry.CurrentUser.CreateSubKey(relativePath))
        using (var child = key!.CreateSubKey("InprocServer32"))
            child!.SetValue(string.Empty, "C:\\missing.dll");

        try
        {
            DenyOwnDeleteAccess(childPath);

            var finding = new RegistryFinding(
                RegistryCleanupCategory.ActiveXCom, $@"HKCU\{relativePath}", ValueName: null, "Scratch", "C:\\missing.dll");

            Assert.False(RegistryCleanupScanner.CanDeleteForTest(finding));
        }
        finally
        {
            RestoreOwnAccess(childPath);
            Registry.CurrentUser.DeleteSubKeyTree(relativePath, throwOnMissingSubKey: false);
        }
    }

    private static void DenyOwnDeleteAccess(string relativePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(relativePath, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.FullControl);
        Assert.NotNull(key);
        var security = key!.GetAccessControl();
        security.AddAccessRule(new RegistryAccessRule(CurrentUserSid, RegistryRights.Delete, AccessControlType.Deny));
        key.SetAccessControl(security);
    }

    private static void RestoreOwnAccess(string relativePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            relativePath, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ReadPermissions | RegistryRights.ChangePermissions);
        if (key is null)
            return;

        var security = key.GetAccessControl();
        foreach (RegistryAccessRule rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier)))
        {
            if (rule.IdentityReference.Equals(CurrentUserSid) && rule.AccessControlType == AccessControlType.Deny)
                security.RemoveAccessRule(rule);
        }

        key.SetAccessControl(security);
    }
}
