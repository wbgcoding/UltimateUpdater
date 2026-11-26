using System.IO;

namespace UltimateUpdater.Services.App;

/// <summary>What a data or backup folder may be, and why a picked one is refused.</summary>
public static partial class AppPaths
{
    /// <summary>Why a folder the user picked in the change-data-folder dialog was refused.</summary>
    public enum DataFolderRejection
    {
        None,
        Link,
        NotWritable,
        SystemFolder,
        InsideProgramFolder,
        NonEmptyTarget,
        InsideCurrentDataFolder,
        TempOrCache,

        /// <summary>Only for the registry backup folder, which the elevated cleanup refuses outside the
        /// user profile.</summary>
        OutsideUserProfile,
    }

    /// <summary>
    /// The move never lands directly in the folder the user picked, always in a dedicated subfolder, so a
    /// folder that holds the user's own unrelated files (a foreign <c>settings.json</c>, a <c>logs</c>
    /// subtree) is never overwritten or swept up by <see cref="AppDataCleaner"/>, which only touches paths
    /// under this app's own data directory.
    /// </summary>
    public const string DedicatedSubfolderName = "UltimateUpdater";

    /// <summary>True when the last segment of <paramref name="path"/> is <see cref="DedicatedSubfolderName"/>,
    /// the only shape this app ever writes as a data folder, so it is the only one it accepts back.</summary>
    internal static bool IsDedicatedDataFolder(string path)
    {
        try
        {
            // A relative pointer would resolve against whatever the current directory is at the time.
            if (!Path.IsPathFullyQualified(path))
                return false;

            var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(Path.GetFileName(trimmed), DedicatedSubfolderName, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The actual copy/pointer destination for a folder the user picked, always
    /// <c>&lt;picked&gt;\UltimateUpdater</c>.</summary>
    public static string EffectiveDataFolderTarget(string picked) =>
        Path.Combine(Path.GetFullPath(picked), DedicatedSubfolderName);

    private static readonly Environment.SpecialFolder[] SystemFolders =
    [
        Environment.SpecialFolder.Windows,
        Environment.SpecialFolder.System,
        Environment.SpecialFolder.SystemX86,
        Environment.SpecialFolder.ProgramFiles,
        Environment.SpecialFolder.ProgramFilesX86,
        Environment.SpecialFolder.CommonProgramFiles,
        Environment.SpecialFolder.CommonProgramFilesX86,
    ];

    /// <summary>
    /// The rules for a new data folder: no reparse point on the way (the cleanup dialog's own walk,
    /// <see cref="Cleanup.CleanupSafety.AncestryLeadsThroughLink"/>, so a junction is judged the same way
    /// there and here), not a Windows system folder, not inside the program's own folder unless this build
    /// is portable, and the dedicated <see cref="EffectiveDataFolderTarget"/> subfolder must not already exist
    /// with content in it and must be writable.
    /// </summary>
    public static DataFolderRejection ValidateNewDataFolder(string path) =>
        ValidateNewDataFolder(path, IsInstalled, AppContext.BaseDirectory, DataDirectory, Path.GetTempPath());

    internal static DataFolderRejection ValidateNewDataFolder(
        string path, bool isInstalled, string baseDirectory, string currentDataDirectory, string tempPath)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return DataFolderRejection.NotWritable;
        }

        if (Cleanup.CleanupSafety.AncestryLeadsThroughLink(full))
            return DataFolderRejection.Link;

        if (IsUnderAny(full, SystemFolders))
            return DataFolderRejection.SystemFolder;

        if (isInstalled && IsUnder(full, baseDirectory))
            return DataFolderRejection.InsideProgramFolder;

        // A target this app's own cleanup could later empty (CleanupSafety's name-based heuristic) is refused,
        // like %TEMP% itself.
        if (IsUnder(full, tempPath) || Cleanup.CleanupSafety.LooksLikeTempOrCacheLocation(full))
            return DataFolderRejection.TempOrCache;

        // The destination is always a dedicated subfolder, never the picked folder itself.
        var effective = EffectiveDataFolderTarget(full);

        // Equal to, inside, or an ancestor of the current data folder would let CopyRecursive copy the folder
        // into itself (or write a pointer that points at itself).
        if (RelatesToCurrentDataFolder(effective, currentDataDirectory))
            return DataFolderRejection.InsideCurrentDataFolder;

        if (Directory.Exists(effective) && Directory.EnumerateFileSystemEntries(effective).Any())
            return DataFolderRejection.NonEmptyTarget;

        return TryPrepare(effective) ? DataFolderRejection.None : DataFolderRejection.NotWritable;
    }

    /// <summary>
    /// The rules for the registry backup folder the user types or picks. Not the data-folder rules: the
    /// default backup folder lies inside the current data folder, so those would reject it. What decides
    /// is the boundary the elevated backup itself enforces (inside the user profile, no link on the way),
    /// plus the system, program and temp/cache folders no backup should ever land in.
    /// </summary>
    public static DataFolderRejection ValidateRegistryBackupFolder(string path) =>
        ValidateRegistryBackupFolder(
            path, IsInstalled, AppContext.BaseDirectory, Path.GetTempPath(),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    internal static DataFolderRejection ValidateRegistryBackupFolder(
        string path, bool isInstalled, string baseDirectory, string tempPath, string userProfile)
    {
        if (!Cleanup.RegistryBackupService.IsWithinUserProfile(path, userProfile))
            return DataFolderRejection.OutsideUserProfile;

        var full = Path.GetFullPath(path);

        if (Cleanup.CleanupSafety.AncestryLeadsThroughLink(full))
            return DataFolderRejection.Link;

        if (IsUnderAny(full, SystemFolders))
            return DataFolderRejection.SystemFolder;

        if (isInstalled && IsUnder(full, baseDirectory))
            return DataFolderRejection.InsideProgramFolder;

        if (IsUnder(full, tempPath) || Cleanup.CleanupSafety.LooksLikeTempOrCacheLocation(full))
            return DataFolderRejection.TempOrCache;

        return DataFolderRejection.None;
    }

    /// <summary>True when <paramref name="effective"/> equals, sits inside, or is an ancestor of
    /// <paramref name="currentDataDirectory"/>.</summary>
    private static bool RelatesToCurrentDataFolder(string effective, string currentDataDirectory)
    {
        string trimmedCurrent;
        try
        {
            trimmedCurrent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(currentDataDirectory));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (trimmedCurrent.Length == 0)
            return false;

        var trimmedEffective = Path.TrimEndingDirectorySeparator(effective);

        return string.Equals(trimmedEffective, trimmedCurrent, StringComparison.OrdinalIgnoreCase)
            || trimmedEffective.StartsWith(trimmedCurrent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || trimmedCurrent.StartsWith(trimmedEffective + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnderAny(string full, IEnumerable<Environment.SpecialFolder> folders)
    {
        foreach (var special in folders)
        {
            string specialPath;
            try
            {
                specialPath = Environment.GetFolderPath(special);
            }
            catch (PlatformNotSupportedException)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(specialPath) && IsUnder(full, specialPath))
                return true;
        }

        return false;
    }

    private static bool IsUnder(string full, string ancestor)
    {
        string trimmedAncestor;
        try
        {
            trimmedAncestor = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ancestor));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (trimmedAncestor.Length == 0)
            return false;

        return string.Equals(full, trimmedAncestor, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(trimmedAncestor + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
