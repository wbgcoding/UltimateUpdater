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
    /// <c>%APPDATA%\UltimateUpdater\registry-backups</c>, ALWAYS, portable mode included. Deliberately not
    /// the app's own data-directory resolver (<c>AppPaths.DataDirectory</c>): in portable mode that resolves
    /// to a <c>data</c> folder beside the exe, which sits OUTSIDE the user profile, and
    /// <c>RegistryBackupService.IsWithinUserProfile</c> would then refuse every backup, silently stopping
    /// registry cleanup from ever backing anything up. Created on demand, never up front.
    /// </summary>
    public string BackupFolder { get; set; } = DefaultBackupFolder;

    /// <summary>Exposed so <see cref="UltimateUpdater.Services.SettingsService"/> can compare a loaded value
    /// against it (to migrate the old profile-root default) without duplicating the path.</summary>
    internal static string DefaultBackupFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UltimateUpdater", "registry-backups");

    /// <summary>Where this default lived before the app was renamed from Windows Updater. A settings file
    /// saved before the rename still carries this path, and it needs to migrate to
    /// <see cref="DefaultBackupFolder"/> the same way the profile-root default already does.</summary>
    internal static string LegacyDefaultBackupFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowsUpdater", "registry-backups");

    public int RetentionCount { get; set; } = 5;
}
