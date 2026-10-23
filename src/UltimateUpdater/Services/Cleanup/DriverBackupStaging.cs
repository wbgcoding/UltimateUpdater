using System.Globalization;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace UltimateUpdater.Services.Cleanup;

/// <summary>
/// Where <see cref="DriverCleanupExecutor"/> exports a driver package before deleting it. Runs AS ADMIN, so
/// it must never resolve <see cref="AppPaths"/> (that would run its whole static initializer, following a
/// pointer file the unelevated user controls, elevated) and must never take its root from an environment
/// variable (a same-user process can set one in its own environment block before the elevated process
/// inherits it). The staging root lives under ProgramData, resolved through the known-folder API instead,
/// and is created fresh, with an explicit ACL, on every run rather than reused.
/// </summary>
internal static class DriverBackupStaging
{
    /// <summary>
    /// Test-only seam: redirects the ProgramData root itself, so a test can exercise link detection and ACL
    /// creation against a throwaway temp directory instead of the real machine's <c>%ProgramData%</c>. Null
    /// (the default) always resolves the real known-folder path; never set outside a test.
    /// </summary>
    internal static string? ProgramDataRootOverride { get; set; }

    /// <summary>
    /// Test-only seam: the name of the timestamped child folder, so a test knows which path will be created
    /// and can plant something there first. Null (the default) uses the current time; never set outside a test.
    /// </summary>
    internal static string? StagingNameOverride { get; set; }

    /// <summary>
    /// Test-only seam: stands in for this process' own token owner, so a test can make the real owner of a
    /// folder count as a foreign one. Null (the default) reads the real token; never set outside a test.
    /// </summary>
    internal static SecurityIdentifier? TokenOwnerOverride { get; set; }

    /// <summary>
    /// The folder the exports go into, held open for as long as this object lives: the handle has no delete
    /// share, so the folder and every directory above it can be neither renamed nor replaced by a link while
    /// the exports run (an admin process writing into whatever a same-user process re-pointed the path at is
    /// exactly what this prevents). Dispose it when the job is over.
    /// </summary>
    internal sealed class StagingFolder(string backupRoot, string folderName, SafeFileHandle pin) : IDisposable
    {
        public string BackupRoot { get; } = backupRoot;

        public string FolderName { get; } = folderName;

        /// <summary>
        /// Checked right before every export. False, which the caller treats as "skip this row, delete
        /// nothing", when the pin is gone, the path now leads through a reparse point anywhere, or the
        /// handle resolves to a different path.
        /// </summary>
        public bool IsStillSafe()
        {
            if (pin.IsClosed || pin.IsInvalid)
                return false;

            if (CleanupSafety.AncestryLeadsThroughLink(BackupRoot))
                return false;

            return ResolvesTo(pin, BackupRoot);
        }

        public void Dispose() => pin.Dispose();
    }

    /// <summary>True when the open handle really is the directory at <paramref name="path"/> (no reparse
    /// point on the way): Windows reports where the handle points, which is compared with the path asked for.</summary>
    private static bool ResolvesTo(SafeFileHandle handle, string path)
    {
        var actual = RegistryBackupService.TryGetFinalPath(handle);
        return actual is not null
            && string.Equals(
                actual.TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Creates `%ProgramData%\UltimateUpdater\driver-backups\&lt;yyyy-MM-dd_HHmmss&gt;` and returns it pinned.
    /// The two levels above it are created with the same protected admin-only ACL (never with whatever
    /// ProgramData would let them inherit, which lets a user pre-create them) and an existing one is accepted
    /// only as a real directory owned by Administrators, SYSTEM or this process' own token owner.
    /// The child is created WITH its ACL in one call (SYSTEM and Administrators full, Users read and execute,
    /// inheritance off), never created first and re-permissioned afterwards, and it must not exist yet: a
    /// pre-existing child (something a same-user process planted under the name) is refused, and the result
    /// is only accepted once the pinned handle proves it is a real directory at exactly this path, owned by
    /// this process' own token owner and carrying exactly that ACL. Returns null on any failure; the caller's
    /// contract is to treat that as "nowhere safe to export" and skip every row rather than delete without a
    /// backup.
    /// </summary>
    internal static StagingFolder? PrepareStagingRoot(LoggingService logging)
    {
        // SpecialFolder.CommonApplicationData resolves through the Win32 known-folder API
        // (SHGetKnownFolderPath), never through the %ProgramData% environment variable, which a same-user
        // process could otherwise have overridden in its own environment block before this elevated process
        // ever started.
        var programData = ProgramDataRootOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var appRoot = Path.Combine(programData, "UltimateUpdater");
        var backupsRoot = Path.Combine(appRoot, "driver-backups");
        var folderName = StagingNameOverride ?? DateTime.Now.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);
        var target = Path.Combine(backupsRoot, folderName);

        if (CleanupSafety.AncestryLeadsThroughLink(target))
        {
            logging.Error("Driver cleanup: refusing to stage backups, part of the ProgramData path is a reparse point.");
            return null;
        }

        try
        {
            // ProgramData lets users create subfolders and hands them CREATOR OWNER rights, so a user can
            // pre-create these two or swap one for a junction. Each is born protected, and one that already
            // exists must be a real directory that only an administrator could have created. CreateDirectory
            // accepts an existing folder silently, hence the owner check runs after it as well.
            if (!EnsureAdminOnlyDirectory(appRoot) || !EnsureAdminOnlyDirectory(backupsRoot))
            {
                logging.Error("Driver cleanup: refusing to stage backups, a staging parent folder is a link or not owned by an administrator.");
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            logging.Error($"Driver cleanup: could not prepare the backup staging folder: {ex.Message}");
            return null;
        }

        // Re-checked after creation, same reasoning RegistryBackupService's own staging check documents: a
        // junction could be planted in the window between the check above and CreateDirectory resolving it.
        if (CleanupSafety.AncestryLeadsThroughLink(target))
        {
            logging.Error("Driver cleanup: refusing to stage backups, a reparse point appeared while preparing the folder.");
            return null;
        }

        // The name is a timestamp this process is about to use for the first time, so anything already there
        // was put there by someone else. Never adopted, never "repaired".
        if (PathExists(target))
        {
            logging.Error("Driver cleanup: refusing to stage backups, the staging folder already exists.");
            return null;
        }

        SafeFileHandle? pin = null;
        try
        {
            ExpectedSecurity().CreateDirectory(target);

            // CreateDirectory accepts an existing directory silently, so the checks below are what proves
            // this one is ours, not the exists-check above (which a racing process can still beat).
            pin = CleanupSafety.TryLockForUse(target);
            if (pin is null || !IsOurOwnNewFolder(pin, target))
            {
                pin?.Dispose();
                logging.Error("Driver cleanup: refusing to stage backups, the staging folder is not the one just created.");
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            pin?.Dispose();
            logging.Error($"Driver cleanup: could not create the backup folder with its ACL: {ex.Message}");
            return null;
        }

        return new StagingFolder(target, folderName, pin);
    }

    private static SecurityIdentifier? TokenOwner() => TokenOwnerOverride ?? WindowsIdentity.GetCurrent().Owner;

    /// <summary>Administrators and SYSTEM, plus this process' own token owner (what a folder this very call
    /// creates is owned by; an elevated token's is Administrators).</summary>
    internal static bool IsTrustedFolderOwner(SecurityIdentifier owner, SecurityIdentifier? tokenOwner) =>
        owner.Equals(AdministratorsSid) || owner.Equals(SystemSid) || (tokenOwner is not null && owner.Equals(tokenOwner));

    /// <summary>Creates the directory with the admin-only protected ACL when it is missing, then demands that
    /// what is there is a real directory (no reparse point) with a trusted owner.</summary>
    private static bool EnsureAdminOnlyDirectory(string path)
    {
        if (!PathExists(path))
            ParentSecurity().CreateDirectory(path);

        try
        {
            var info = new DirectoryInfo(path);
            if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null)
                return false;

            var security = info.GetAccessControl(AccessControlSections.Owner);
            return security.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner
                && IsTrustedFolderOwner(owner, TokenOwner());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
                                       or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool PathExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;   // present but unreadable: not ours to use
        }
    }

    private static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier AdministratorsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier UsersSid = new(WellKnownSidType.BuiltinUsersSid, null);
    private const InheritanceFlags Inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    /// <summary>The ACL the folder is born with. Inheritance off: a folder this narrow must not pick up
    /// whatever ProgramData's own ACL happens to grant, now or after some future change to it.</summary>
    private static DirectorySecurity ExpectedSecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            SystemSid, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            AdministratorsSid, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            UsersSid, FileSystemRights.ReadAndExecute, Inherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    /// <summary>
    /// The ACL of the two parent levels: the staging folder's own ACL, plus FullControl for this process' own
    /// token owner when that is not already Administrators or SYSTEM. An elevated worker's token owner IS
    /// Administrators, so there nothing is added; it only keeps the creating account able to create the
    /// child inside a parent it just made when this runs without elevation (the tests).
    /// </summary>
    private static DirectorySecurity ParentSecurity()
    {
        var security = ExpectedSecurity();
        if (TokenOwner() is { } owner && !owner.Equals(AdministratorsSid) && !owner.Equals(SystemSid))
        {
            security.AddAccessRule(new FileSystemAccessRule(
                owner, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
        }

        return security;
    }

    /// <summary>
    /// Read through the pin (which cannot be swapped any more): the handle resolves to exactly this path
    /// with no reparse point, the owner is this process' own default owner (a folder someone else created
    /// first is owned by them), and the ACL is exactly <see cref="ExpectedSecurity"/>.
    /// </summary>
    internal static bool IsOurOwnNewFolder(SafeFileHandle pin, string path)
    {
        try
        {
            if (!ResolvesTo(pin, path))
                return false;

            var info = new DirectoryInfo(path);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return false;

            var security = info.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);

            var tokenOwner = TokenOwner();
            if (tokenOwner is null || security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !owner.Equals(tokenOwner))
                return false;

            if (!security.AreAccessRulesProtected)
                return false;

            var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
            return rules.Count == 3
                && HasRule(rules, SystemSid, FileSystemRights.FullControl)
                && HasRule(rules, AdministratorsSid, FileSystemRights.FullControl)
                && HasRule(rules, UsersSid, FileSystemRights.ReadAndExecute);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
                                       or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool HasRule(List<FileSystemAccessRule> rules, SecurityIdentifier sid, FileSystemRights rights) =>
        rules.Any(rule => sid.Equals(rule.IdentityReference)
            && rule.AccessControlType == AccessControlType.Allow
            // Windows stores the Synchronize bit with ReadAndExecute even though the enum value leaves it out.
            && (rule.FileSystemRights | FileSystemRights.Synchronize) == (rights | FileSystemRights.Synchronize)
            && rule.InheritanceFlags == Inherit
            && rule.PropagationFlags == PropagationFlags.None
            && !rule.IsInherited);

    /// <summary>
    /// The same path <see cref="PrepareStagingRoot"/> creates for a given folder name, computed unelevated
    /// (reading, not writing, so no admin rights are needed) so the app can copy that folder into the data
    /// folder after the job.
    /// </summary>
    internal static string ResolveExistingPath(string folderName) =>
        Path.Combine(
            ProgramDataRootOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "UltimateUpdater", "driver-backups", folderName);
}
