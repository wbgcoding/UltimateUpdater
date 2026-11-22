using System.IO;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Services.Uninstall;

/// <summary>
/// The gate every leftover passes before it is offered and again right before it is removed, per kind of
/// target. A different question from <see cref="LeftoverInstallSafety"/>, which only judges the top-level
/// program-folder roots of the old age heuristic. Every rule can only refuse: nothing here makes a path
/// deletable, the scanner has to propose it first.
/// </summary>
internal static class LeftoverTargetSafety
{
    /// <summary>
    /// A folder is removable only when it is a real directory on a local drive, with no link anywhere in its
    /// path, not a drive root, not inside Windows, not the program's own data or install folder (or one that
    /// holds it), not a protected user folder, not a root the scan reads, and not a folder that holds one.
    /// </summary>
    internal static bool IsSafeFolder(string? path, LeftoverRoots roots, out string reason)
    {
        reason = string.Empty;
        var full = LeftoverRoots.Full(path);
        if (full is null)
        {
            reason = "unusable path";
            return false;
        }

        if (!CleanupSafety.IsLocalPathWithoutLinks(full))
        {
            reason = "not a local path without links";
            return false;
        }

        if (Path.GetPathRoot(full) is { } driveRoot
            && string.Equals(Path.TrimEndingDirectorySeparator(driveRoot), full, StringComparison.OrdinalIgnoreCase))
        {
            reason = "drive root";
            return false;
        }

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrWhiteSpace(windows)
            && (LeftoverRoots.SamePath(full, windows) || LeftoverRoots.IsBelow(full, windows)))
        {
            reason = "inside Windows";
            return false;
        }

        if (CleanupSafety.IsOwnPath(full) || CleanupSafety.ContainsOwnPath(full))
        {
            reason = "the program's own folder";
            return false;
        }

        foreach (var protectedFolder in roots.ProtectedFolders.Concat(roots.InstallRoots))
        {
            var protectedFull = LeftoverRoots.Full(protectedFolder);
            if (protectedFull is null)
                continue;

            if (string.Equals(full, protectedFull, StringComparison.OrdinalIgnoreCase)
                || protectedFull.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                reason = "a protected folder or one that holds it";
                return false;
            }
        }

        if (LeftoverNames.IsGeneric(LeftoverNames.Normalize(Path.GetFileName(full))))
        {
            reason = "a generic folder name";
            return false;
        }

        try
        {
            if (!Directory.Exists(full))
            {
                reason = "does not exist";
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reason = "could not be inspected";
            return false;
        }

        return true;
    }

    /// <summary>The hives a leftover key may live in, and the Software roots below them.</summary>
    private static readonly string[] KeyRoots =
    [
        @"HKCU\Software",
        @"HKLM\Software",
        @"HKLM\Software\WOW6432Node",
    ];

    /// <summary>
    /// A registry key is removable only when it is exactly <c>&lt;hive&gt;\Software\&lt;publisher&gt;\&lt;program&gt;</c>
    /// (also below WOW6432Node): never a publisher key itself (other programs of that publisher live there),
    /// never a deeper key, never a Windows or generic name. Which program it belongs to is the scanner's
    /// question; this only bounds WHERE a key can be.
    /// </summary>
    internal static bool IsSafeRegistryKeyPath(string? keyPath, out string reason)
    {
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(keyPath) || keyPath.Contains("..", StringComparison.Ordinal))
        {
            reason = "unusable key path";
            return false;
        }

        // The longest root first, so WOW6432Node is read as a root, not as a publisher.
        foreach (var root in KeyRoots.OrderByDescending(r => r.Length))
        {
            if (!keyPath.StartsWith(root + @"\", StringComparison.OrdinalIgnoreCase))
                continue;

            var rest = keyPath[(root.Length + 1)..].Split('\\');
            if (rest.Length != 2 || rest.Any(string.IsNullOrWhiteSpace))
            {
                reason = "not exactly Software\\<publisher>\\<program>";
                return false;
            }

            if (root.EndsWith(@"Software", StringComparison.OrdinalIgnoreCase)
                && string.Equals(rest[0], "WOW6432Node", StringComparison.OrdinalIgnoreCase))
            {
                reason = "WOW6432Node is a root, not a publisher";
                return false;
            }

            if (LeftoverNames.IsGeneric(LeftoverNames.Normalize(rest[0])) || LeftoverNames.IsGeneric(LeftoverNames.Normalize(rest[1])))
            {
                reason = "a generic or Windows key name";
                return false;
            }

            return true;
        }

        reason = "outside the Software keys";
        return false;
    }

    /// <summary>
    /// A shortcut is removable only when it is a plain .lnk file (not a link itself) below one of the Start menu
    /// or desktop folders, with no link in its path. That its target is really gone is the scanner's finding,
    /// re-checked by the caller of this gate.
    /// </summary>
    internal static bool IsSafeShortcut(string? path, LeftoverRoots roots, out string reason)
    {
        reason = string.Empty;
        var full = LeftoverRoots.Full(path);
        if (full is null || !full.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            reason = "not a .lnk file";
            return false;
        }

        if (!roots.UserShortcutFolders.Concat(roots.MachineShortcutFolders).Any(folder => LeftoverRoots.IsBelow(full, folder)))
        {
            reason = "not in a Start menu or desktop folder";
            return false;
        }

        if (!CleanupSafety.IsLocalPathWithoutLinks(full))
        {
            reason = "not a local path without links";
            return false;
        }

        try
        {
            if (!File.Exists(full) || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            {
                reason = "missing or a link";
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reason = "could not be inspected";
            return false;
        }

        return true;
    }
}
