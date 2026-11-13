using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Services;

/// <summary>
/// Whether a directory chain and a file can only be changed by administrators. A signature says nothing
/// about the directory the signed file sits in: a folder a user created under the volume root inherits
/// "Authenticated Users: Modify", so a signed host next to a planted DLL there is code execution as admin.
/// </summary>
internal static class ElevatedPathTrust
{
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier TrustedInstaller =
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");
    private static readonly SecurityIdentifier CreatorOwner = new(WellKnownSidType.CreatorOwnerSid, null);

    // Raw generic bits can sit in a stored ACE; they imply every right below, so they count everywhere.
    private const FileSystemRights Generic = (FileSystemRights)0x10000000 | (FileSystemRights)0x40000000;

    /// <summary>Rule (b), every ancestor: only what could swap an existing child for another one. Creating a
    /// new subfolder in an ancestor cannot replace a child that already exists. WriteData is left out on
    /// purpose: on a folder it only adds files, never a subfolder, so nothing can become a junction and a
    /// child with content cannot be removed (measured on real ACLs, see ElevatedPathTrustAncestorTests).</summary>
    private const FileSystemRights AncestorForbidden =
        FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions
        | FileSystemRights.TakeOwnership | FileSystemRights.Delete | Generic;

    /// <summary>Rule (a), the file's own directory: anything that can add, replace or remove a sibling
    /// (a DLL next to the host) or rewrite the directory's own permissions, plus everything an ancestor
    /// is held to (deleting the directory itself, with its pinned file, is as bad as swapping it).</summary>
    private const FileSystemRights OwnDirectoryForbidden =
        FileSystemRights.WriteData | FileSystemRights.AppendData | AncestorForbidden;

    /// <summary>Rule (c), the file itself.</summary>
    private const FileSystemRights FileForbidden =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete
        | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | Generic;

    internal static bool IsSafeFile(FileSystemSecurity security) => IsSafe(security, FileForbidden);

    internal static bool IsSafeOwnDirectory(FileSystemSecurity security) =>
        IsSafe(security, OwnDirectoryForbidden, inheritedFileRights: FileForbidden);

    internal static bool IsSafeAncestor(FileSystemSecurity security) => IsSafe(security, AncestorForbidden);

    private static bool IsTrusted(SecurityIdentifier sid) =>
        sid.Equals(Administrators) || sid.Equals(LocalSystem) || sid.Equals(TrustedInstaller);

    /// <summary>
    /// The owner counts too: an owner always holds WRITE_DAC, so a user-owned object can be re-permissioned by
    /// that user whatever its ACEs say. No owner at all is untrusted. Only allow entries that apply to the
    /// object itself are looked at (inherit-only ones are a template for children), with one exception:
    /// <paramref name="inheritedFileRights"/> is given for the tool's own directory, where an entry that
    /// inherits to files or subfolders (inherit-only or not) is the effective right on every sibling that
    /// already exists, so it is judged against those file rights too. Deny entries are not subtracted, so a
    /// mixed ACL errs towards refusing.
    /// </summary>
    private static bool IsSafe(FileSystemSecurity security, FileSystemRights forbidden, FileSystemRights inheritedFileRights = 0)
    {
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !IsTrusted(owner))
            return false;

        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow)
                continue;

            var inheritOnly = rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly);
            var reachesChildren = inheritedFileRights != 0 && rule.InheritanceFlags != InheritanceFlags.None;
            if (inheritOnly && !reachesChildren)
                continue;

            if (rule.IdentityReference is not SecurityIdentifier sid)
                return false;

            // CREATOR OWNER on the object itself stands for whoever owns it. An inheritable one on an ancestor
            // is only a template for children created later (the owner of an existing child is whoever made
            // it), and a user who may create folders there owns just those new ones, none on an admin's path
            // (measured, see ElevatedPathTrustAncestorTests).
            if (sid.Equals(CreatorOwner))
                sid = owner;

            if (IsTrusted(sid))
                continue;

            if (!inheritOnly && (rule.FileSystemRights & forbidden) != 0)
                return false;

            if (reachesChildren && (rule.FileSystemRights & inheritedFileRights) != 0)
                return false;
        }

        return true;
    }

    /// <summary>
    /// The pinned file and every directory above it, read from where the bytes REALLY are. The path must
    /// equal the handle's final path, so a junction or symlink anywhere in it (which could be re-pointed
    /// between this check and the launch) fails closed instead of being judged by its own ACL. The open
    /// handle itself keeps the file and its parents from being renamed or replaced meanwhile. Anything that
    /// cannot be read is refused.
    /// </summary>
    internal static bool IsTrustedLocation(string path, FileStream pin)
    {
        try
        {
            var finalPath = RegistryBackupService.TryGetFinalPath(pin.SafeFileHandle);
            if (finalPath is null)
                return false;

            var full = Path.GetFullPath(path);
            if (!string.Equals(finalPath, full, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!IsSafeFile(pin.GetAccessControl()))
                return false;

            return Path.GetDirectoryName(finalPath) is { } directory && IsTrustedDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException or SecurityException or InvalidOperationException
                                       or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private const string MachineEnvironmentKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

    /// <summary>The system-wide PATH from HKLM, unexpanded; null when it cannot be read. Never the process
    /// environment, which the unelevated user controls.</summary>
    internal static string? ReadMachinePath()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(MachineEnvironmentKey);
            return key?.GetValue("Path", null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>One system-wide environment variable from HKLM, unexpanded; null when absent or unreadable.
    /// Never the process environment, which the unelevated user controls.</summary>
    internal static string? ReadMachineVariable(string name)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(MachineEnvironmentKey);
            return key?.GetValue(name, null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// A PATH for an elevated child: the machine PATH, with %SystemRoot%/%windir% expanded from the known
    /// folder (not from the process environment) and every entry that is relative, still carries another
    /// variable, or fails <paramref name="isTrustedDirectory"/> dropped. Never empty: System32 is the floor.
    /// </summary>
    internal static string BuildTrustedPath(string? machinePath, Func<string, bool> isTrustedDirectory)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var kept = new List<string>();

        foreach (var raw in (machinePath ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var entry = raw.Trim().Trim('"');
            if (windows.Length > 0)
            {
                entry = entry.Replace("%SystemRoot%", windows, StringComparison.OrdinalIgnoreCase)
                    .Replace("%windir%", windows, StringComparison.OrdinalIgnoreCase);
            }

            if (entry.Length == 0 || entry.Contains('%') || !Path.IsPathFullyQualified(entry))
                continue;

            entry = Path.TrimEndingDirectorySeparator(entry);
            if (kept.Contains(entry, StringComparer.OrdinalIgnoreCase) || !isTrustedDirectory(entry))
                continue;

            kept.Add(entry);
        }

        return kept.Count > 0 ? string.Join(';', kept) : Environment.SystemDirectory;
    }

    private const uint ReadControl = 0x00020000;
    private const uint FileReadAttributes = 0x80;
    private const uint ShareAll = 7; // read, write, delete: the check must never be what blocks a program
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint ReparsePointAttribute = 0x400;
    private const uint OwnerAndDacl = 1 | 4;

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKernelObjectSecurity(
        SafeFileHandle handle, uint requestedInformation, byte[]? descriptor, uint length, out uint lengthNeeded);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetLongPathNameW(string shortPath, char[] longPath, uint length);

    /// <summary>The directory under rule (a) and each of its ancestors up to the volume root under rule (b).</summary>
    internal static bool IsTrustedDirectory(string directory)
    {
        var handles = new List<SafeFileHandle>();
        try
        {
            var path = ExpandShortNames(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)));
            var current = new DirectoryInfo(path);
            var own = true;

            for (; current is not null; current = current.Parent)
            {
                // One open per level, and everything is read from THAT handle: the attributes, the final path
                // and the owner and ACL. A path-based read after a path-based check could see another folder
                // than the one that was checked, and a junction or symlink (where the user may create
                // folders) is followed by an ACL read, so its owner could re-point it while the elevated child
                // runs with that PATH. Reparse points themselves are opened, not followed, and refused.
                var handle = OpenDirectoryPinned(current.FullName);
                if (handle is null)
                    return false;

                handles.Add(handle);

                var security = ReadOwnerAndAccess(handle);
                if (security is null || !(own ? IsSafeOwnDirectory(security) : IsSafeAncestor(security)))
                    return false;

                own = false;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException or SecurityException or InvalidOperationException
                                       or PlatformNotSupportedException or OverflowException)
        {
            return false;
        }
        finally
        {
            foreach (var handle in handles)
                handle.Dispose();
        }
    }

    /// <summary>A directory handle, or null when the directory is a reparse point, cannot be opened, or its
    /// final path is not the path that was asked for (a link further up the chain).</summary>
    private static SafeFileHandle? OpenDirectoryPinned(string path)
    {
        var handle = CleanupNative.CreateFileW(
            path, ReadControl | FileReadAttributes, ShareAll, IntPtr.Zero, OpenExisting,
            BackupSemantics | OpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        if (!CleanupNative.GetFileInformationByHandle(handle, out var info)
            || (info.FileAttributes & ReparsePointAttribute) != 0
            || RegistryBackupService.TryGetFinalPath(handle) is not { } finalPath
            || !string.Equals(
                Path.TrimEndingDirectorySeparator(finalPath), Path.TrimEndingDirectorySeparator(path),
                StringComparison.OrdinalIgnoreCase))
        {
            handle.Dispose();
            return null;
        }

        return handle;
    }

    private static DirectorySecurity? ReadOwnerAndAccess(SafeFileHandle handle)
    {
        GetKernelObjectSecurity(handle, OwnerAndDacl, null, 0, out var needed);
        if (needed == 0 || needed > 64 * 1024)
            return null;

        var descriptor = new byte[needed];
        if (!GetKernelObjectSecurity(handle, OwnerAndDacl, descriptor, needed, out _))
            return null;

        var security = new DirectorySecurity();
        security.SetSecurityDescriptorBinaryForm(descriptor);
        return security;
    }

    /// <summary>A PATH entry may spell a folder with its 8.3 name; the final path of its handle never does, so
    /// the name is expanded first (this does not resolve links, so a junction still shows as a difference).</summary>
    private static string ExpandShortNames(string path)
    {
        if (!path.Contains('~'))
            return path;

        var buffer = new char[1024];
        var length = GetLongPathNameW(path, buffer, (uint)buffer.Length);
        return length is > 0 and < 1024 ? new string(buffer, 0, (int)length) : path;
    }
}
