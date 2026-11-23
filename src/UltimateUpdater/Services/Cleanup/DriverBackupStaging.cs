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
    /// Test-only seam: applies the production owner rules although <see cref="ProgramDataRootOverride"/> is set.
    /// Those rules (no extra trust and no extra ACL entry for this process' own token owner, the creator's owner
    /// forced to Administrators) are what the real, elevated worker always runs under.
    /// </summary>
    internal static bool ProductionOwnerRulesForTest { get; set; }

    /// <summary>True for the real ProgramData (or when a test asks for it). Only a redirected root, which exists for
    /// the unelevated test process, may trust its own token owner: on a real machine an elevated token's default
    /// owner can be a plain user account (the "object creator" policy), which must never count as trusted.</summary>
    private static bool ProductionRules => ProgramDataRootOverride is null || ProductionOwnerRulesForTest;

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

        /// <summary>
        /// The end of the run. When it exported at least one package, the older backup folders beyond the
        /// newest few are trimmed (only then: a run that backed up nothing must never cost an older backup,
        /// which is the only restore source for drivers removed earlier). When it exported nothing, its own
        /// still-empty folder is removed again so it counts as no backup at all.
        /// </summary>
        public void Finish(bool exportedAnything, LoggingService logging)
        {
            if (exportedAnything)
            {
                var backupsRoot = Path.GetDirectoryName(BackupRoot);
                if (backupsRoot is not null)
                    PruneOldStagings(backupsRoot, FolderName, KeepNewest, logging, requireProtectedAcl: true);

                return;
            }

            // The pin has no delete share, so it is released first; only an EMPTY folder goes (a non-recursive
            // delete refuses anything else), and a link swapped in meanwhile would lose only itself.
            pin.Dispose();
            try
            {
                Directory.Delete(BackupRoot, recursive: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logging.Warn($"Driver cleanup: could not remove the empty backup folder '{FolderName}': {ex.Message}");
            }
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
    /// The leftover quarantine uses the same creation under its own <paramref name="subfolder"/> with
    /// <paramref name="usersCanRead"/> off (SYSTEM and Administrators only, two entries): what a removed
    /// program left is for an administrator to put back. <paramref name="label"/> only names the caller in the log.
    /// </summary>
    internal static StagingFolder? PrepareStagingRoot(
        LoggingService logging, string subfolder = "driver-backups", bool usersCanRead = true,
        string? folderName = null, string label = "Driver cleanup")
    {
        // SpecialFolder.CommonApplicationData resolves through the Win32 known-folder API
        // (SHGetKnownFolderPath), never through the %ProgramData% environment variable, which a same-user
        // process could otherwise have overridden in its own environment block before this elevated process
        // ever started.
        var programData = ProgramDataRootOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var appRoot = Path.Combine(programData, "UltimateUpdater");
        var backupsRoot = Path.Combine(appRoot, subfolder);
        folderName ??= StagingNameOverride ?? DateTime.Now.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);
        var target = Path.Combine(backupsRoot, folderName);

        if (CleanupSafety.AncestryLeadsThroughLink(target))
        {
            logging.Error($"{label}: refusing to stage backups, part of the ProgramData path is a reparse point.");
            return null;
        }

        try
        {
            // ProgramData lets users create subfolders and hands them CREATOR OWNER rights, so a user can
            // pre-create these two or swap one for a junction. Each is born protected, and one that already
            // exists must be a real directory that only an administrator could have created. CreateDirectory
            // accepts an existing folder silently, hence the owner check runs after it as well.
            if (!EnsureAdminOnlyDirectory(appRoot, usersCanRead: true) || !EnsureAdminOnlyDirectory(backupsRoot, usersCanRead))
            {
                logging.Error($"{label}: refusing to stage backups, a staging parent folder is a link or not owned by an administrator.");
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
                                       or InvalidOperationException)
        {
            logging.Error($"{label}: could not prepare the backup staging folder: {ex.Message}");
            return null;
        }

        // Re-checked after creation, same reasoning RegistryBackupService's own staging check documents: a
        // junction could be planted in the window between the check above and CreateDirectory resolving it.
        if (CleanupSafety.AncestryLeadsThroughLink(target))
        {
            logging.Error($"{label}: refusing to stage backups, a reparse point appeared while preparing the folder.");
            return null;
        }

        // The name is a timestamp this process is about to use for the first time, so anything already there
        // was put there by someone else. Never adopted, never "repaired".
        if (PathExists(target))
        {
            logging.Error($"{label}: refusing to stage backups, the staging folder already exists.");
            return null;
        }

        SafeFileHandle? pin = null;
        try
        {
            (usersCanRead ? ExpectedSecurity() : ParentSecurity(usersCanRead: false)).CreateDirectory(target);

            // CreateDirectory accepts an existing directory silently, so the checks below are what proves
            // this one is ours, not the exists-check above (which a racing process can still beat).
            pin = CleanupSafety.TryLockForUse(target);
            if (pin is null || !IsOurOwnNewFolder(pin, target, usersCanRead))
            {
                pin?.Dispose();
                logging.Error($"{label}: refusing to stage backups, the staging folder is not the one just created.");
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
                                       or InvalidOperationException)
        {
            pin?.Dispose();
            logging.Error($"{label}: could not create the backup folder with its ACL: {ex.Message}");
            return null;
        }

        return new StagingFolder(target, folderName, pin);
    }

    /// <summary>How many driver backup folders stay, the one of the running job included. Each holds a full
    /// package export, so they cannot pile up for ever; five is the default of the registry backups. Only
    /// folders that hold something count: an empty one is no backup.</summary>
    internal const int KeepNewest = 5;

    private static readonly System.Text.RegularExpressions.Regex StagingNamePattern = new(
        @"^\d{4}-\d{2}-\d{2}_\d{6}$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Deletes the oldest timestamp-named folders directly inside <paramref name="backupsRoot"/> so that no
    /// more than <paramref name="keepNewest"/> remain. <paramref name="keepName"/> (the running job's own
    /// folder) is never touched and counts as one of the kept ones. Only a plain folder whose name is exactly
    /// the timestamp shape is a candidate: a link, a file or anything with another name is left alone, and
    /// a path that leads through a link is not entered. Folders are ranked by their creation time (a clock
    /// that was wrong once cannot pin a slot through its name), empty ones neither count nor go, and nothing
    /// is pruned at all unless the running job's own folder holds something. With
    /// <paramref name="requireProtectedAcl"/> (the elevated side) a stale folder is deleted only when it is
    /// still exactly the folder this code creates: real directory, our own token owner, the protected
    /// three-entry ACL that keeps ordinary users from adding anything to it. Failures are logged, never thrown.
    /// </summary>
    internal static void PruneOldStagings(
        string backupsRoot, string keepName, int keepNewest, LoggingService logging, bool requireProtectedAcl = false)
    {
        if (keepNewest <= 0 || CleanupSafety.AncestryLeadsThroughLink(backupsRoot)
            || !HasContent(Path.Combine(backupsRoot, keepName)))
        {
            return;
        }

        try
        {
            // SafeEnumerate.Directories leaves out every reparse point, so a planted junction is never a candidate.
            var older = SafeEnumerate.Directories(backupsRoot)
                .Where(d => StagingNamePattern.IsMatch(Path.GetFileName(d))
                    && !string.Equals(Path.GetFileName(d), keepName, StringComparison.OrdinalIgnoreCase))
                .Where(HasContent)
                .OrderByDescending(Directory.GetCreationTimeUtc)
                .ThenByDescending(d => Path.GetFileName(d), StringComparer.Ordinal)
                .ToList();

            foreach (var stale in older.Skip(Math.Max(0, keepNewest - 1)))
            {
                try
                {
                    if (CleanupSafety.AncestryLeadsThroughLink(stale))
                        continue;

                    if (requireProtectedAcl && !IsStillOurProtectedFolder(stale))
                    {
                        logging.Warn($"Driver cleanup: left the old backup folder '{Path.GetFileName(stale)}' in place, it is not the protected folder this tool created.");
                        continue;
                    }

                    Directory.Delete(stale, recursive: true);
                    logging.Info($"Driver cleanup: removed the old backup folder '{Path.GetFileName(stale)}'.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logging.Warn($"Driver cleanup: could not remove the old backup folder '{stale}': {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logging.Warn($"Driver cleanup: could not prune old backups in '{backupsRoot}': {ex.Message}");
        }
    }

    /// <summary>The data-folder side of the retention, run after this run's copy arrived. Never from an
    /// elevated process: there the data path is no longer the user's own, and a recursive delete under it
    /// would run with administrator rights. A copy that holds nothing prunes nothing (see
    /// <see cref="PruneOldStagings"/>).</summary>
    internal static void PruneDataCopies(string dataBackupsRoot, string keepName, LoggingService logging)
    {
        if (ElevationService.IsElevated)
            return;

        PruneOldStagings(dataBackupsRoot, keepName, KeepNewest, logging);
    }

    private static bool HasContent(string directory)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(directory).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The folder is checked through an open handle, then released for the delete (the pin has no
    /// delete share): what is inside such a folder could only be put there by an administrator.</summary>
    private static bool IsStillOurProtectedFolder(string folder)
    {
        using var pin = CleanupSafety.TryLockForUse(folder);
        return pin is not null && IsOurOwnNewFolder(pin, folder);
    }

    private static SecurityIdentifier? TokenOwner() => TokenOwnerOverride ?? WindowsIdentity.GetCurrent().Owner;

    /// <summary>Administrators and SYSTEM, plus <paramref name="tokenOwner"/> when one is given (a test process'
    /// own account; see <see cref="ExtraTrustedOwner"/>).</summary>
    internal static bool IsTrustedFolderOwner(SecurityIdentifier owner, SecurityIdentifier? tokenOwner) =>
        IsAdminOrSystem(owner) || (tokenOwner is not null && owner.Equals(tokenOwner));

    private static bool IsAdminOrSystem(SecurityIdentifier sid) => sid.Equals(AdministratorsSid) || sid.Equals(SystemSid);

    /// <summary>The one extra account a folder owner may be: this process' own token owner, but only under a
    /// redirected test root. Never on the real machine, where an elevated token's default owner may be an
    /// ordinary user account that could have pre-created the folder.</summary>
    private static SecurityIdentifier? ExtraTrustedOwner() => ProductionRules ? null : TokenOwner();

    /// <summary>The owner a folder this code creates must have: Administrators (SYSTEM when the process runs as
    /// SYSTEM) on a real machine, this process' own token owner under a redirected test root.</summary>
    internal static SecurityIdentifier? ExpectedOwner()
    {
        var tokenOwner = TokenOwner();
        if (!ProductionRules)
            return tokenOwner;

        return tokenOwner is not null && tokenOwner.Equals(SystemSid) ? SystemSid : AdministratorsSid;
    }

    /// <summary>Makes the new folder owned by <see cref="ExpectedOwner"/> when the token's default owner would be
    /// someone else (the "object creator" policy gives an elevated token its user account as owner).</summary>
    private static DirectorySecurity WithExpectedOwner(DirectorySecurity security)
    {
        if (ExpectedOwner() is { } expected && !Equals(expected, TokenOwner()))
            security.SetOwner(expected);

        return security;
    }

    /// <summary>Creates the directory with the admin-only protected ACL when it is missing, then demands that
    /// what is there is a real directory (no reparse point) with a trusted owner.</summary>
    private static bool EnsureAdminOnlyDirectory(string path, bool usersCanRead)
    {
        if (!PathExists(path))
            ParentSecurity(usersCanRead).CreateDirectory(path);

        try
        {
            var info = new DirectoryInfo(path);
            if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null)
                return false;

            var security = info.GetAccessControl(AccessControlSections.Owner);
            return security.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner
                && IsTrustedFolderOwner(owner, ExtraTrustedOwner());
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
    internal static DirectorySecurity ExpectedSecurity(bool usersCanRead = true)
    {
        var security = WithExpectedOwner(new DirectorySecurity());
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            SystemSid, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            AdministratorsSid, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
        if (usersCanRead)
        {
            security.AddAccessRule(new FileSystemAccessRule(
                UsersSid, FileSystemRights.ReadAndExecute, Inherit, PropagationFlags.None, AccessControlType.Allow));
        }

        return security;
    }

    /// <summary>
    /// The ACL of the two parent levels: the staging folder's own ACL, plus FullControl for this process' own
    /// token owner when that is not already Administrators or SYSTEM, only under a redirected test root. It
    /// keeps the creating account able to create the child inside a parent it just made when this runs without
    /// elevation (the tests). The real elevated worker never gets the extra entry, whatever its token owner is.
    /// </summary>
    internal static DirectorySecurity ParentSecurity(bool usersCanRead = true)
    {
        var security = ExpectedSecurity(usersCanRead);
        if (!ProductionRules && TokenOwner() is { } owner && !IsAdminOrSystem(owner))
        {
            security.AddAccessRule(new FileSystemAccessRule(
                owner, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
        }

        return security;
    }

    /// <summary>
    /// Read through the pin (which cannot be swapped any more): the handle resolves to exactly this path
    /// with no reparse point, the owner is <see cref="ExpectedOwner"/> (a folder someone else created
    /// first is owned by them), and the ACL is exactly <see cref="ExpectedSecurity"/>.
    /// </summary>
    internal static bool IsOurOwnNewFolder(SafeFileHandle pin, string path, bool usersCanRead = true)
    {
        try
        {
            if (!ResolvesTo(pin, path))
                return false;

            var info = new DirectoryInfo(path);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return false;

            var security = info.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);

            var expectedOwner = ExpectedOwner();
            if (expectedOwner is null || security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !owner.Equals(expectedOwner))
                return false;

            // The quarantine child (no Users read) also carries an unelevated creator, as its parents do, or that
            // account could not use the folder it just made (the tests). The real elevated worker adds nothing.
            var tokenOwner = TokenOwner();
            var ownerExtra = !ProductionRules && !usersCanRead && tokenOwner is not null && !IsAdminOrSystem(tokenOwner);
            return AclMatches(security, usersCanRead, ownerExtra ? tokenOwner : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
                                       or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Protected (no inheritance) and exactly SYSTEM and Administrators full control, plus Users read
    /// and execute when <paramref name="usersCanRead"/>, plus <paramref name="extraFullControl"/> when given.</summary>
    private static bool AclMatches(FileSystemSecurity security, bool usersCanRead, SecurityIdentifier? extraFullControl)
    {
        if (!security.AreAccessRulesProtected)
            return false;

        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        return rules.Count == (usersCanRead ? 3 : 2) + (extraFullControl is null ? 0 : 1)
            && HasRule(rules, SystemSid, FileSystemRights.FullControl)
            && HasRule(rules, AdministratorsSid, FileSystemRights.FullControl)
            && (!usersCanRead || HasRule(rules, UsersSid, FileSystemRights.ReadAndExecute))
            && (extraFullControl is null || HasRule(rules, extraFullControl, FileSystemRights.FullControl));
    }

    /// <summary>
    /// Holds the three levels of the machine quarantine open and proven, for as long as it lives: the ProgramData
    /// app folder (a real directory owned by an administrator or SYSTEM), the quarantine root and, when asked for, one
    /// quarantine folder (each a real directory carrying exactly the protected administrator-only ACL this code
    /// creates it with, owned by an administrator). The handles have no delete share, so nothing in between can be
    /// swapped for a link while the caller works. Null, with the reason logged, when any level fails: a folder a
    /// plain user created first (ProgramData lets them make subfolders and own them) is never trusted, because
    /// what lies in it could be planted or re-pointed by that user while an elevated process acts on it.
    /// </summary>
    internal static IDisposable? PinMachineQuarantine(string root, string? folderName, LoggingService logging, string label)
    {
        var pins = new List<SafeFileHandle>(3);
        var parentExtra = !ProductionRules && TokenOwner() is { } t && !IsAdminOrSystem(t) ? t : null;

        bool Add(string path, bool usersCanRead, bool requireAcl, bool isNewFolder)
        {
            var pin = CleanupSafety.TryLockForUse(path);
            if (pin is null)
                return false;

            pins.Add(pin);
            try
            {
                if (isNewFolder)
                    return IsOurOwnNewFolder(pin, path, usersCanRead);

                if (!ResolvesTo(pin, path) || new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint))
                    return false;

                var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
                return security.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner
                    && IsTrustedFolderOwner(owner, ExtraTrustedOwner())
                    && (!requireAcl || AclMatches(security, usersCanRead, parentExtra));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
                                           or ArgumentException or NotSupportedException or InvalidOperationException)
            {
                return false;
            }
        }

        var appRoot = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)));
        var ok = appRoot is not null
            && Add(appRoot, usersCanRead: true, requireAcl: false, isNewFolder: false)
            && Add(root, usersCanRead: false, requireAcl: true, isNewFolder: false)
            && (folderName is null || Add(Path.Combine(root, folderName), usersCanRead: false, requireAcl: true, isNewFolder: true));
        if (ok)
            return new PinSet(pins);

        foreach (var pin in pins)
            pin.Dispose();

        logging.Warn($"{label}: the machine quarantine is not the protected administrator-only folder this tool creates; not used.");
        return null;
    }

    private sealed class PinSet(List<SafeFileHandle> pins) : IDisposable
    {
        public void Dispose()
        {
            foreach (var pin in pins)
                pin.Dispose();
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
