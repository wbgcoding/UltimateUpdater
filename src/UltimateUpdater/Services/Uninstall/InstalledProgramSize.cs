using System.IO;

namespace UltimateUpdater.Services.Uninstall;

/// <summary>
/// The size of an entry: what it states itself, else the size of its install folder, measured on demand
/// (a walk over a big folder is slow, so the list never does it up front).
/// </summary>
public static class InstalledProgramSize
{
    /// <summary>A walk gives up past this many files and reports what it counted so far.</summary>
    private const int MaxFiles = 200_000;

    public static Task<long?> ResolveAsync(InstalledProgramEntry entry, CancellationToken cancellationToken) =>
        entry.SizeBytes is { } stated
            ? Task.FromResult<long?>(stated)
            : Task.Run(() => MeasureFolder(entry.InstallLocation, cancellationToken), cancellationToken);

    /// <summary>
    /// The summed length of the files under an install folder, or null when the value is no absolute existing
    /// folder. Links are never followed: a junction in an install folder must not pull another tree in.
    /// </summary>
    internal static long? MeasureFolder(string? location, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(location))
            return null;

        var root = location.Trim().Trim('"');
        if (!Path.IsPathFullyQualified(root))
            return null;

        try
        {
            var directory = new DirectoryInfo(root);
            if (!directory.Exists || directory.LinkTarget is not null)
                return null;

            long total = 0;
            var files = 0;
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };

            foreach (var file in directory.EnumerateFiles("*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                total += file.Length;
                if (++files >= MaxFiles)
                    break;
            }

            return total;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
