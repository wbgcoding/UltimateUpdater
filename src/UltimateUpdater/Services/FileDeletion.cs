using System.IO;

namespace UltimateUpdater.Services;

/// <summary>Shared by every "delete this file, one locked/missing file must not abort the caller" site
/// (<c>IconCacheService</c>, <c>SelfUpdateService</c>, <c>RegistryBackupService</c> and
/// <c>DriverSources</c> all had byte-identical copies of this before). <paramref name="onFailed"/> lets a
/// caller log with its own context; the other three stay silent, matching their prior behavior.</summary>
internal static class FileDeletion
{
    internal static void TryDelete(string path, Action<Exception>? onFailed = null)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            onFailed?.Invoke(ex);
        }
    }

    /// <summary>The path of a fresh, randomly named folder under <paramref name="root"/> for ONE downloaded
    /// installer that will elevate. Nothing is created yet: the caller makes it right before the download.
    /// A signed installer that self-elevates may load DLLs (version.dll and the like) from its own folder,
    /// and the shared temp root is writable by any process of this user, so the file never sits there
    /// directly.</summary>
    internal static string NewStagingFolderPath(string root, string prefix) =>
        Path.Combine(root, prefix + Guid.NewGuid().ToString("N"));

    /// <summary>Deletes a staged file, then its staging folder, but only when the folder is empty: a
    /// non-recursive delete, so anything else planted in it is never taken along. A locked file simply
    /// leaves the folder in place. The folder is only touched when its name starts with
    /// <paramref name="folderPrefix"/>, so a file that was never staged never costs its folder.</summary>
    internal static void TryDeleteStaged(string filePath, string folderPrefix, Action<Exception>? onFailed = null)
    {
        TryDelete(filePath, onFailed);
        var folder = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(folder)
            || !Path.GetFileName(folder).StartsWith(folderPrefix, StringComparison.Ordinal))
            return;

        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            onFailed?.Invoke(ex);
        }
    }
}
