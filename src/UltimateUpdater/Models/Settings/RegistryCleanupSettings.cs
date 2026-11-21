using System.IO;

namespace UltimateUpdater.Models.Settings;

/// <summary>
/// Where the mandatory pre-write backup of every registry key the cleaner touches is saved, and how many
/// of those backups are kept. A registry write happens only after this backup succeeds, see
/// <c>RegistryCleanupScanner</c>.
/// </summary>
public sealed class RegistryCleanupSettings
{
    /// <summary>
    /// <c>%APPDATA%\UltimateUpdater\registry-backups</c>, ALWAYS, portable mode included. Not <c>AppPaths.DataDirectory</c>:
    /// portable mode resolves that beside the exe, outside the user profile, and
    /// <c>RegistryBackupService.IsWithinUserProfile</c> would then refuse every backup. Created on demand.
    /// </summary>
    public string BackupFolder { get; set; } = DefaultBackupFolder;

    /// <summary>Exposed so <see cref="UltimateUpdater.Services.SettingsService"/> can compare a loaded value
    /// against it (to migrate the old profile-root default) without duplicating the path.</summary>
    internal static string DefaultBackupFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UltimateUpdater", "registry-backups");

    /// <summary>The default location under the app's former name. A settings file that still carries it migrates to
    /// <see cref="DefaultBackupFolder"/>, like the profile-root default.</summary>
    internal static string LegacyDefaultBackupFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowsUpdater", "registry-backups");

    public int RetentionCount { get; set; } = 5;
}
