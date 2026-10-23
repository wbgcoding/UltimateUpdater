using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using static UltimateUpdater.Services.Cleanup.CleanupNative;

namespace UltimateUpdater.Services.Cleanup;

/// <summary>
/// The last line of defence before anything is deleted. Every single path handed to the deleter is checked
/// here, even though the catalogue is hard-coded: a bug that composed the wrong path would otherwise delete
/// a user's files, and no amount of "it cannot happen" is worth that risk.
/// </summary>
public static class CleanupSafety
{
    /// <summary>
    /// Folders that must NEVER be emptied, whatever the caller believes. Checked as "is this path one of
    /// these, or a parent of one", deleting a parent takes the child with it.
    /// </summary>
    private static readonly Environment.SpecialFolder[] ProtectedFolders =
    [
        Environment.SpecialFolder.MyDocuments,
        Environment.SpecialFolder.MyPictures,
        Environment.SpecialFolder.MyMusic,
        Environment.SpecialFolder.MyVideos,
        Environment.SpecialFolder.Desktop,
        Environment.SpecialFolder.DesktopDirectory,
        Environment.SpecialFolder.UserProfile,
        Environment.SpecialFolder.Windows,
        Environment.SpecialFolder.System,
        Environment.SpecialFolder.ProgramFiles,
        Environment.SpecialFolder.ProgramFilesX86,
        Environment.SpecialFolder.ApplicationData,
        Environment.SpecialFolder.LocalApplicationData,
        Environment.SpecialFolder.CommonApplicationData,
    ];

    /// <summary>
    /// Folder names that identify real user data, in either language. Note "downloads" (plural) only:
    /// Windows Update's own cache is literally named "Download" (singular) and must stay cleanable, while
    /// the user's Downloads folder must never be touched.
    /// </summary>
    private static readonly string[] ForbiddenNames =
    [
        "downloads", "documents", "dokumente", "desktop", "schreibtisch",
        "pictures", "bilder", "music", "musik", "videos", "onedrive", "favorites", "favoriten",
        "saved games", "contacts", "links", "searches",
    ];

    /// <summary>
    /// Directories that belong to a RUNNING program and happen to live inside a temp folder, deleting
    /// them takes the program down with it. Paths are spelled out here rather than read from
    /// <c>AppPaths</c> ON PURPOSE: this type also runs inside the ELEVATED cleanup worker, and merely
    /// touching <c>AppPaths</c> would run its static initializer, which creates directories and writes a
    /// probe file in locations the unelevated user controls.
    /// </summary>
    private static readonly string[] OwnPaths = BuildOwnPaths();

    private static string[] BuildOwnPaths()
    {
        var paths = new List<string>();

        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            try
            {
                paths.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // an unusable path cannot be protected and cannot be deleted either
            }
        }

        // Single-file extraction root (ours and everyone else's), our own base directory whatever form
        // the build takes, and the Temp-resident data folder AppPaths falls back to.
        Add(Path.Combine(Path.GetTempPath(), ".net"));
        Add(AppContext.BaseDirectory);
        Add(Path.Combine(Path.GetTempPath(), "UltimateUpdater"));

        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>True when the path IS one of our protected directories, or sits inside one.</summary>
    public static bool IsOwnPath(string path)
    {
        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        return OwnPaths.Any(own =>
            string.Equals(full, own, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(own + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// True when deleting this directory recursively would take a protected path with it. Temp itself is an
    /// ancestor of the extraction root, so this is asked about the SUBDIRECTORIES to be deleted, never about
    /// the target directory, otherwise cleaning Temp would refuse itself.
    /// </summary>
    public static bool ContainsOwnPath(string directory)
    {
        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        return OwnPaths.Any(own => own.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// True only for a path that is safe to empty: an existing, real (non-link) directory that is a temp or
    /// cache location, is not a protected folder or a parent of one, and is not named like user data.
    /// </summary>
    public static bool IsSafeToEmpty(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        // A drive root would take everything with it.
        if (Path.GetPathRoot(full) is { } root && string.Equals(
                Path.TrimEndingDirectorySeparator(root), full, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var name = Path.GetFileName(full);
        if (ForbiddenNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            return false;

        // Never empty a directory that is ours (or another running app's extraction root).
        if (IsOwnPath(full))
            return false;

        // Must look like a temp/cache location. This is what stops a mistyped catalogue entry outright. Checked
        // before the protected-folder lookups below (every check here is an AND, so the order changes no
        // answer): almost every folder the app-cache sweep asks about fails this cheap name test, and should
        // not first pay for a dozen GetFolderPath calls.
        if (!LooksLikeTempOrCache(full))
            return false;

        foreach (var special in ProtectedFolders)
        {
            var protectedPath = SafeFolderPath(special);
            if (protectedPath is null)
                continue;

            // exactly a protected folder, or an ancestor of one
            if (string.Equals(full, protectedPath, StringComparison.OrdinalIgnoreCase)
                || protectedPath.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        try
        {
            if (!Directory.Exists(full))
                return false;

            // A junction/symlink could aim anywhere at all, never delete through one. Checked the whole
            // way up, not just this directory: a parent can be replaced by a junction too.
            return !AncestryLeadsThroughLink(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// True when the directory itself, or ANY ancestor on the way up to the drive root, is a reparse point.
    /// A junction anywhere on the path silently redirects the whole thing at resolution time, checking only
    /// the leaf misses a swapped parent entirely. Unreadable counts as "leads through a link": a path that
    /// cannot be inspected earns no benefit of the doubt.
    /// </summary>
    public static bool AncestryLeadsThroughLink(string path)
    {
        try
        {
            for (var dir = new DirectoryInfo(Path.GetFullPath(path)); dir is not null; dir = dir.Parent)
            {
                if (dir.Exists && dir.LinkTarget is not null)
                    return true;
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return true;
        }
    }

    /// <summary>
    /// Opens a directory handle that, while it stays open, blocks any rename or delete of that exact
    /// filesystem object, Windows only allows those with FILE_SHARE_DELETE from every open handle, and this
    /// one does not grant it. Closes the gap between "checked safe" and "used minutes later while deleting
    /// hundreds of files": a normal user could otherwise swap the target for a junction in that window. The
    /// check-then-open happens back to back with no attacker-controlled work between them, and the
    /// handle then guards everything the caller does with that directory until it is disposed. Returns null,
    /// never a bare path to fall back to, when the directory leads through a link or cannot be opened
    /// this way at all.
    /// </summary>
    public static SafeFileHandle? TryLockForUse(string path)
    {
        if (AncestryLeadsThroughLink(path))
            return null;

        // Runs between the ancestry check and the open: the window an attacker would use to swap the
        // checked folder for a junction.
        BeforeLockOpenForTest.Value?.Invoke(path);

        // File.OpenHandle cannot open a directory at all ("This method can't open handles to
        // directories", confirmed the hard way, it throws UnauthorizedAccessException for every real
        // directory, which would have made this refuse to lock anything and skip the whole cleanup).
        // CreateFileW needs FILE_FLAG_BACKUP_SEMANTICS to open a directory; everything else about the
        // call is the same protection File.OpenHandle would have given a file. OPEN_REPARSE_POINT makes a
        // link at the root open as the link itself instead of being followed.
        const uint genericRead = 0x80000000;
        const uint fileShareRead = 0x00000001;
        const uint openExisting = 3;
        const uint fileFlagBackupSemantics = 0x02000000;
        const uint fileFlagOpenReparsePoint = 0x00200000;

        var handle = CreateFileW(path, genericRead, fileShareRead, IntPtr.Zero, openExisting,
            fileFlagBackupSemantics | fileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        // The ancestry check ran before the open, so it proves nothing about what was opened. The handle
        // itself must be a plain directory whose final path is the candidate's own long path: a handle
        // that came through a link reports the link target there, which can never equal the candidate.
        if (!HandleIsCandidateDirectory(handle, path) || AncestryLeadsThroughLink(path))
        {
            handle.Dispose();
            return null;
        }

        return handle;
    }

    /// <summary>Test seam, invoked with the path right before the root is opened.</summary>
    internal static readonly AsyncLocal<Action<string>?> BeforeLockOpenForTest = new();

    internal static bool HandleIsCandidateDirectory(SafeFileHandle handle, string path)
    {
        const uint attributeDirectory = 0x10;
        const uint attributeReparsePoint = 0x400;

        try
        {
            if (!GetFileInformationByHandle(handle, out var info)
                || (info.FileAttributes & attributeDirectory) == 0
                || (info.FileAttributes & attributeReparsePoint) != 0)
            {
                return false;
            }

            var actual = StripExtendedPrefix(VerifiedDelete.FinalPathOf(handle));
            // Asked after the open so a name that changed in between is measured against the same state.
            var expected = StripExtendedPrefix(LongPathOf(Path.GetFullPath(path)));
            if (actual is null || expected is null)
                return false;

            return string.Equals(
                Path.TrimEndingDirectorySeparator(actual),
                Path.TrimEndingDirectorySeparator(expected),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException
                                       or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Drops a leading extended-length prefix; a UNC final path counts as unreadable, so a target
    /// behind a network share is refused rather than compared.</summary>
    private static string? StripExtendedPrefix(string? path)
    {
        if (path is null || path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return null;

        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }

    private static string? LongPathOf(string fullPath)
    {
        var buffer = new StringBuilder(512);
        while (true)
        {
            var length = GetLongPathNameW(fullPath, buffer, (uint)buffer.Capacity);
            if (length == 0)
                return null;

            if (length < buffer.Capacity)
                return buffer.ToString();

            buffer.Capacity = (int)length + 1;
        }
    }

    /// <summary>
    /// Volume serial and file index of the directory a path names right now, the identity every spelling
    /// of one folder shares (8.3 name, other casing). False when it cannot be opened or read.
    /// </summary>
    internal static bool TryReadDirectoryIdentity(string path, out (uint VolumeSerial, ulong FileIndex) id)
    {
        id = default;
        const uint fileReadAttributes = 0x80;
        const uint shareAll = 0x7;
        const uint openExisting = 3;
        const uint fileFlagBackupSemantics = 0x02000000;

        try
        {
            using var handle = CreateFileW(path, fileReadAttributes, shareAll, IntPtr.Zero, openExisting,
                fileFlagBackupSemantics, IntPtr.Zero);
            return !handle.IsInvalid && VerifiedDelete.TryGetIdentity(handle, out id);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetLongPathNameW(string lpszShortPath, StringBuilder lpszLongPath, uint cchBuffer);

    /// <summary>
    /// Public seam for validators outside the cleanup pipeline, currently
    /// <c>AppPaths.ValidateNewDataFolder</c>, that need this exact "looks like a temp or cache location by
    /// name" heuristic without going through the whole <see cref="IsSafeToEmpty"/> gate (link checks,
    /// protected folders, existence). <paramref name="fullPath"/> is expected already resolved.
    /// </summary>
    public static bool LooksLikeTempOrCacheLocation(string fullPath) => LooksLikeTempOrCache(fullPath);

    /// <summary>Folders that end in "cache" but hold something a program still needs: installer copies used to
    /// repair or uninstall, a Store app's own data, the browser history and cookie database. A path through
    /// any of them is never a temp or cache location, however its other segments are named.</summary>
    private static readonly string[] NotACacheSegments = ["Package Cache", "LocalCache", "WebCache"];

    private static bool LooksLikeTempOrCache(string full)
    {
        var segments = full.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => NotACacheSegments.Contains(segment, StringComparer.OrdinalIgnoreCase)))
            return false;

        return segments.Any(segment =>
            segment.Equals("Temp", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("tmp", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("CrashDumps", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("Minidump", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("Logs", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("Download", StringComparison.OrdinalIgnoreCase)          // SoftwareDistribution\Download
            || segment.Equals("DeliveryOptimization", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("Explorer", StringComparison.OrdinalIgnoreCase)          // thumbnail cache
            // Anything NAMED as a cache: "Cache", "D3DSCache", "NV_Cache", "npm-cache", ".gradle\caches",
            // matched by SUFFIX, not whole segment, so names like these are still caught.
            || segment.EndsWith("cache", StringComparison.OrdinalIgnoreCase)
            || segment.EndsWith("caches", StringComparison.OrdinalIgnoreCase));
    }

    private static string? SafeFolderPath(Environment.SpecialFolder folder)
    {
        try
        {
            var path = Environment.GetFolderPath(folder);
            return string.IsNullOrEmpty(path) ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception)
        {
            // Fail closed, same as the rest of this file: a folder this safety gate could not resolve
            // cleanly must count as unusable, never crash the caller and never be guessed at.
            return null;
        }
    }

    /// <summary>
    /// The gate for deleting a single recycled item. True ONLY for a path that, once normalized, lies
    /// strictly inside <c>&lt;drive&gt;\$Recycle.Bin\&lt;this user's SID&gt;\</c>. Fail closed in every other
    /// case: an empty or unusable path, a SID that is not one (a caller-composed "*" or ".." would otherwise
    /// widen the folder to every account on the machine), a path that normalizes out of the folder again, a
    /// path that IS the folder rather than something in it, and anything whose ancestry leads through a
    /// reparse point, a junction inside the bin would make a delete land wherever it aims.
    /// Another user's SID folder is refused by construction, elevated or not.
    /// </summary>
    public static bool IsInsideOwnRecycleBin(string path, string? ownSid)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(ownSid) || !LooksLikeSid(ownSid))
            return false;

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root))
            return false;

        string ownBin;
        try
        {
            ownBin = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(Path.Combine(root, "$Recycle.Bin", ownSid)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (!full.StartsWith(ownBin + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return false;

        return !AncestryLeadsThroughLink(full);
    }

    /// <summary>A textual SID and nothing else, no separators, no wildcards, no relative segments.</summary>
    private static bool LooksLikeSid(string value) =>
        value.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)
        && value.Length <= 190
        && value.Skip(4).All(c => char.IsAsciiDigit(c) || c == '-');

    /// <summary>
    /// Which files inside a safe directory may be deleted. The thumbnail cache lives in the same folder as
    /// Explorer's own settings, so only the cache files there are touched.
    /// </summary>
    public static bool IsDeletableFile(string directory, string filePath)
    {
        // A file belonging to a running program's extraction root, or to this app's own data, deleting
        // one of those is how a temp cleanup crashes the very app doing the cleaning.
        if (IsOwnPath(filePath))
            return false;

        if (!Path.GetFileName(directory).Equals("Explorer", StringComparison.OrdinalIgnoreCase))
            return true;

        var name = Path.GetFileName(filePath);
        return name.StartsWith("thumbcache", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("iconcache", StringComparison.OrdinalIgnoreCase);
    }
}
