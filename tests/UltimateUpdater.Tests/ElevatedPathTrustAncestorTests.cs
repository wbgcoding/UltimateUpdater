using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

/// <summary>
/// Two ancestor ACL shapes the trust rules let through, measured on a real temp folder: what an unelevated
/// user can actually do there, and the verdict of the rules on the same entries with an administrator owner
/// (a user cannot own a folder as Administrators, so the verdict uses in-memory security objects).
/// </summary>
public class ElevatedPathTrustAncestorTests : IDisposable
{
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier CreatorOwner = new(WellKnownSidType.CreatorOwnerSid, null);
    private static readonly SecurityIdentifier CurrentUser = WindowsIdentity.GetCurrent().User!;

    private readonly string _root = Directory.CreateTempSubdirectory("wu-ancestor-acl-").FullName;

    public void Dispose()
    {
        try
        {
            // The ACL under test may have taken the user's own rights away; the owner can always give them back.
            var restore = new DirectorySecurity();
            restore.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            restore.AddAccessRule(new FileSystemAccessRule(
                CurrentUser, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            foreach (var directory in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories).Reverse())
                new DirectoryInfo(directory).SetAccessControl(restore);

            new DirectoryInfo(_root).SetAccessControl(restore);
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static DirectorySecurity Protected(params FileSystemAccessRule[] rules)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var rule in rules)
            security.AddAccessRule(rule);

        return security;
    }

    private static FileSystemAccessRule Allow(SecurityIdentifier sid, FileSystemRights rights) =>
        new(sid, rights, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow);

    private static FileSystemAccessRule AllowInheritable(SecurityIdentifier sid, FileSystemRights rights) =>
        new(sid, rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.InheritOnly, AccessControlType.Allow);

    private static DirectorySecurity InMemory(params FileSystemAccessRule[] rules)
    {
        var security = new DirectorySecurity();
        security.SetOwner(Admins);
        foreach (var rule in rules)
            security.AddAccessRule(rule);

        return security;
    }

    /// <summary>
    /// WriteData on a folder is "add file": no subfolder can be created, so nothing in it can become a
    /// junction, and an existing child cannot be replaced by it. The ancestor rule therefore needs no
    /// WriteData bit and the entry stays accepted.
    /// </summary>
    [Fact]
    public void An_ancestor_granting_users_only_add_file_lets_them_plant_files_but_not_folders_or_links()
    {
        var ancestor = Path.Combine(_root, "ancestor");
        Directory.CreateDirectory(ancestor);
        var existing = Directory.CreateDirectory(Path.Combine(ancestor, "tool-folder")).FullName;
        File.WriteAllBytes(Path.Combine(existing, "tool.exe"), [1]);

        // Protected, so the ancestor's ACL change below does not propagate into it and strip its entries.
        var childSecurity = Protected(new FileSystemAccessRule(
            CurrentUser, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(existing).SetAccessControl(childSecurity);

        new DirectoryInfo(ancestor).SetAccessControl(Protected(
            Allow(CurrentUser, FileSystemRights.WriteData | FileSystemRights.ReadAndExecute)));

        File.WriteAllBytes(Path.Combine(ancestor, "planted.txt"), [1]);
        Assert.True(File.Exists(Path.Combine(ancestor, "planted.txt")));
        Assert.ThrowsAny<UnauthorizedAccessException>(() => Directory.CreateDirectory(Path.Combine(ancestor, "new-folder")));
        // A folder with content cannot be removed, so it cannot be re-created as a junction either.
        Assert.ThrowsAny<IOException>(() => Directory.Delete(existing));
        Assert.True(File.Exists(Path.Combine(existing, "tool.exe")));

        Assert.True(ElevatedPathTrust.IsSafeAncestor(InMemory(
            Allow(Admins, FileSystemRights.FullControl),
            Allow(Users, FileSystemRights.WriteData | FileSystemRights.ReadAndExecute))));
    }

    /// <summary>
    /// Inheritable CREATOR OWNER Full Control plus "create folders" for users (the shape of a shared
    /// drop folder): a user can create a child folder and owns it fully, but a folder with content cannot
    /// be removed or turned into a link, and the new child is not on any path an administrator launches
    /// from. The inherit-only CREATOR OWNER entry is a template for new children, not a right on the
    /// ancestor, so the entry stays accepted; as an own-directory entry it is still refused through the
    /// users' create rights.
    /// </summary>
    [Fact]
    public void An_ancestor_with_inheritable_creator_owner_and_create_folders_lets_users_own_only_new_children()
    {
        var ancestor = Path.Combine(_root, "shared");
        Directory.CreateDirectory(ancestor);

        new DirectoryInfo(ancestor).SetAccessControl(Protected(
            Allow(CurrentUser, FileSystemRights.AppendData | FileSystemRights.ReadAndExecute),
            AllowInheritable(CreatorOwner, FileSystemRights.FullControl)));

        // No add-file right: nothing can be planted next to the children.
        Assert.ThrowsAny<UnauthorizedAccessException>(() => File.WriteAllBytes(Path.Combine(ancestor, "planted.txt"), [1]));

        var child = Directory.CreateDirectory(Path.Combine(ancestor, "child")).FullName;
        File.WriteAllBytes(Path.Combine(child, "inside.txt"), [1]);

        // A folder with content cannot be deleted, so it cannot be re-created as a junction either.
        Assert.ThrowsAny<IOException>(() => Directory.Delete(child));

        var shape = new[]
        {
            Allow(Admins, FileSystemRights.FullControl),
            Allow(Users, FileSystemRights.AppendData | FileSystemRights.ReadAndExecute),
            AllowInheritable(CreatorOwner, FileSystemRights.FullControl),
        };
        Assert.True(ElevatedPathTrust.IsSafeAncestor(InMemory(shape)));
        Assert.False(ElevatedPathTrust.IsSafeOwnDirectory(InMemory(shape)));
    }
}
