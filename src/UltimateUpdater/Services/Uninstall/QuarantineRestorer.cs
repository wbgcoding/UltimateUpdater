using System.IO;
using System.Text;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.Services.Processes;

namespace UltimateUpdater.Services.Uninstall;

/// <summary>What a restore did: items put back, items left alone because the place was taken (or the key exists
/// again), and items that could not be put back. An item restored earlier counts nowhere.</summary>
public sealed record QuarantineRestoreResult(int Restored, int AlreadyPresent, int Failed)
{
    public static QuarantineRestoreResult None { get; } = new(0, 0, 0);

    public QuarantineRestoreResult Plus(QuarantineRestoreResult other) =>
        new(Restored + other.Restored, AlreadyPresent + other.AlreadyPresent, Failed + other.Failed);
}

/// <summary>
/// Puts the items of one quarantine folder back. Nothing is ever overwritten: a folder, shortcut or registry key
/// that exists again is left alone and counted. A target must still lie where leftovers are found (below the
/// lane's program, data or shortcut roots) with no link anywhere in its parent chain; the stored item must lie
/// inside the quarantine folder itself. A registry export is imported only after every key header in it has been
/// read and found to be the item's own key or below it (no deletion header, no other key).
/// </summary>
internal sealed class QuarantineRestorer(
    LoggingService logging, LeftoverRoots roots, Func<string, CancellationToken, Task<bool>>? importRegistryFile = null)
{
    private const long MaxRegFileBytes = 64L * 1024 * 1024;

    private readonly Func<string, CancellationToken, Task<bool>> _import = importRegistryFile ?? ImportWithRegExeAsync;

    /// <summary>Restores every item of <paramref name="folder"/> (a full quarantine folder path already resolved by
    /// <see cref="QuarantineStore.ResolveFolder"/>) that belongs to <paramref name="lane"/>.</summary>
    internal async Task<QuarantineRestoreResult> RestoreAsync(string folder, LeftoverLane lane, CancellationToken cancellationToken)
    {
        if (CleanupSafety.AncestryLeadsThroughLink(folder) || QuarantineStore.ReadManifest(folder) is not { } manifest)
        {
            logging.Warn("Leftover quarantine: the folder to restore has no readable manifest.");
            return new QuarantineRestoreResult(0, 0, 1);
        }

        if (manifest.Lane != lane)
        {
            logging.Warn("Leftover quarantine: the manifest belongs to the other lane; nothing restored.");
            return new QuarantineRestoreResult(0, 0, 1);
        }

        int restored = 0, present = 0, failed = 0;
        foreach (var entry in manifest.Items.Where(e => !e.Restored))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var outcome = entry.Kind == LeftoverKind.RegistryKey
                ? await RestoreKeyAsync(folder, entry, lane, cancellationToken)
                : RestoreFile(folder, entry, lane);
            switch (outcome)
            {
                case Outcome.Restored:
                    restored++;
                    entry.Restored = true;
                    break;
                case Outcome.Present:
                    present++;
                    break;
                default:
                    failed++;
                    break;
            }
        }

        if (restored > 0)
            Save(folder, manifest);

        return new QuarantineRestoreResult(restored, present, failed);
    }

    private enum Outcome
    {
        Restored,
        Present,
        Failed,
    }

    private Outcome RestoreFile(string folder, QuarantineEntry entry, LeftoverLane lane)
    {
        var original = LeftoverRoots.Full(entry.OriginalPath);
        var stored = StoredPath(folder, entry, "items");
        if (original is null || stored is null || !IsRestoreTarget(original, entry.Kind, lane))
        {
            logging.Warn($"Leftover quarantine: '{entry.OriginalPath}' is not a place leftovers are restored to; skipped.");
            return Outcome.Failed;
        }

        try
        {
            var parent = Path.GetDirectoryName(original);
            if (parent is null || !Directory.Exists(parent) || !CleanupSafety.IsLocalPathWithoutLinks(parent)
                || CleanupSafety.AncestryLeadsThroughLink(parent))
            {
                logging.Warn($"Leftover quarantine: the folder '{parent}' is gone or leads through a link; '{original}' not restored.");
                return Outcome.Failed;
            }

            if (Exists(original))
                return Outcome.Present;

            if (CleanupSafety.AncestryLeadsThroughLink(stored) || !Exists(stored))
            {
                logging.Warn($"Leftover quarantine: the stored item for '{original}' is missing or leads through a link.");
                return Outcome.Failed;
            }

            if (entry.Kind == LeftoverKind.Shortcut)
                File.Move(stored, original);
            else
                Directory.Move(stored, original);

            return Outcome.Restored;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            logging.Warn($"Leftover quarantine: could not restore '{original}': {ex.Message}");
            return Outcome.Failed;
        }
    }

    private async Task<Outcome> RestoreKeyAsync(string folder, QuarantineEntry entry, LeftoverLane lane, CancellationToken cancellationToken)
    {
        var hive = lane == LeftoverLane.User ? @"HKCU\" : @"HKLM\";
        if (!entry.OriginalPath.StartsWith(hive, StringComparison.OrdinalIgnoreCase)
            || !LeftoverTargetSafety.IsSafeRegistryKeyPath(entry.OriginalPath, out _))
        {
            logging.Warn($"Leftover quarantine: '{entry.OriginalPath}' is not a key leftovers are restored to; skipped.");
            return Outcome.Failed;
        }

        var file = StoredPath(folder, entry, "registry");
        if (file is null || !file.EndsWith(".reg", StringComparison.OrdinalIgnoreCase) || !File.Exists(file)
            || CleanupSafety.AncestryLeadsThroughLink(file))
        {
            logging.Warn($"Leftover quarantine: the export for '{entry.OriginalPath}' is missing.");
            return Outcome.Failed;
        }

        if (RegistryCleanupScanner.KeyExists(entry.OriginalPath))
            return Outcome.Present;

        if (!RegFileHoldsOnly(file, entry.OriginalPath))
        {
            logging.Warn($"Leftover quarantine: the export for '{entry.OriginalPath}' holds more than that key; not imported.");
            return Outcome.Failed;
        }

        try
        {
            return await _import(file, cancellationToken) ? Outcome.Restored : Outcome.Failed;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException
                                       or TimeoutException or UntrustedToolException)
        {
            logging.Warn($"Leftover quarantine: could not import the export for '{entry.OriginalPath}': {ex.Message}");
            return Outcome.Failed;
        }
    }

    /// <summary>
    /// True when <paramref name="file"/> is a registry export whose every key header is <paramref name="keyPath"/>
    /// (as <c>reg export</c> spells it, with the long hive name) or a key below it, and which deletes nothing.
    /// </summary>
    internal static bool RegFileHoldsOnly(string file, string keyPath)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length > MaxRegFileBytes)
                return false;

            var expected = RegistryBackupService.ToFullHiveName(keyPath);
            var sawHeader = false;
            var first = true;
            foreach (var raw in File.ReadLines(file, Encoding.Unicode))
            {
                var line = raw.TrimEnd();
                if (first)
                {
                    first = false;
                    if (!line.TrimStart('\uFEFF').Equals("Windows Registry Editor Version 5.00", StringComparison.Ordinal))
                        return false;

                    continue;
                }

                if (!line.StartsWith('['))
                    continue;

                if (!line.EndsWith(']') || line.Length < 3 || line[1] == '-')
                    return false;

                var name = line[1..^1];
                if (!name.Equals(expected, StringComparison.OrdinalIgnoreCase)
                    && !name.StartsWith(expected + @"\", StringComparison.OrdinalIgnoreCase))
                    return false;

                sawHeader = true;
            }

            return sawHeader;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private bool IsRestoreTarget(string original, LeftoverKind kind, LeftoverLane lane)
    {
        if (kind == LeftoverKind.Shortcut)
        {
            var folders = lane == LeftoverLane.User ? roots.UserShortcutFolders : roots.MachineShortcutFolders;
            return original.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
                && folders.Any(f => LeftoverRoots.IsBelow(original, f));
        }

        var places = lane == LeftoverLane.User ? roots.UserFolderRoots : roots.MachineFolderRoots;
        return places.Any(p => LeftoverRoots.IsBelow(original, p));
    }

    /// <summary>The stored item as a full path, only when it lies below <c>&lt;folder&gt;\&lt;area&gt;</c>.</summary>
    private static string? StoredPath(string folder, QuarantineEntry entry, string area)
    {
        if (string.IsNullOrWhiteSpace(entry.StoredAs) || Path.IsPathRooted(entry.StoredAs))
            return null;

        try
        {
            var full = Path.GetFullPath(Path.Combine(folder, entry.StoredAs));
            return LeftoverRoots.IsBelow(full, Path.Combine(folder, area)) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private void Save(string folder, QuarantineManifest manifest)
    {
        try
        {
            File.WriteAllText(
                Path.Combine(folder, QuarantineStore.ManifestName),
                System.Text.Json.JsonSerializer.Serialize(manifest, QuarantineStore.Json), new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logging.Warn($"Leftover quarantine: could not update the manifest: {ex.Message}");
        }
    }

    private static bool Exists(string path)
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
            return true;
        }
    }

    /// <summary>The system's own reg.exe, by its absolute path (never searched on PATH), importing a file this
    /// class has just verified.</summary>
    private static async Task<bool> ImportWithRegExeAsync(string file, CancellationToken cancellationToken)
    {
        var regExe = Path.Combine(Environment.SystemDirectory, "reg.exe");
        var result = await ProcessRunner.RunAsync(regExe, ["import", file], TimeSpan.FromSeconds(60), cancellationToken);
        return result.ExitCode == 0;
    }
}
