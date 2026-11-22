using System.IO;
using Microsoft.Win32;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Services.Uninstall;

/// <summary>One leftover the user chose, by what it is and where it is. The remover looks it up again in a
/// fresh scan; the text alone never selects anything.</summary>
public sealed class LeftoverSelectionDto
{
    public LeftoverKind Kind { get; set; }

    public string Path { get; set; } = string.Empty;
}

/// <summary>What one removal run did.</summary>
internal sealed record LeftoverRemoval(int Removed, int Skipped, long FreedBytes);

/// <summary>
/// Removes the leftovers the user ticked, nothing else. A selection is only a pointer: the scan runs again
/// right now and only a chosen item that the fresh scan still finds, and that still passes
/// <see cref="LeftoverTargetSafety"/>, is touched. Folders and shortcuts go to the recycle bin (never gone for
/// good); a registry key is exported first and removed handle by handle without ever following a link, and
/// only when the export holds it. Whatever fails stays where it is and is counted as skipped. With a
/// <see cref="QuarantineSession"/> the items are moved into the quarantine (and each key exported into it) instead,
/// so they can be restored; an item the quarantine cannot take goes the old way.
/// </summary>
internal sealed class LeftoverRemover
{
    private readonly LeftoverScanner _scanner;
    private readonly LeftoverRoots _roots;
    private readonly LoggingService _logging;
    private readonly Func<string, bool> _recycle;
    private readonly Func<IReadOnlyList<string>, string, int, CancellationToken, Task<RegistryBackupService.RegistryBackupResult?>> _backup;
    private readonly Func<string, RegistryBackupService.KeyTreeDeleteStatus> _deleteKey;

    internal LeftoverRemover(
        LeftoverScanner scanner, LeftoverRoots roots, LoggingService logging,
        Func<string, bool>? recycle = null,
        Func<IReadOnlyList<string>, string, int, CancellationToken, Task<RegistryBackupService.RegistryBackupResult?>>? backup = null,
        Func<string, RegistryBackupService.KeyTreeDeleteStatus>? deleteKey = null)
    {
        _scanner = scanner;
        _roots = roots;
        _logging = logging;
        _recycle = recycle ?? (path => RecycleBinDeleter.DeleteToRecycleBin(path, logging));
        _backup = backup ?? ((keys, folder, retention, ct) =>
            RegistryBackupService.BackupRootsBeforeWriteAsync(keys, folder, retention, logging, ct));
        _deleteKey = deleteKey ?? (keyPath => DeleteKeyTree(keyPath, logging));
    }

    internal async Task<LeftoverRemoval> RemoveAsync(
        LeftoverSubject subject, LeftoverClaims claims, LeftoverLane lane, IReadOnlyList<LeftoverSelectionDto> selected,
        string backupFolder, int backupRetention, CancellationToken cancellationToken, QuarantineSession? quarantine = null)
    {
        if (selected.Count == 0)
            return new LeftoverRemoval(0, 0, 0);

        var fresh = _scanner.Scan(subject, claims, lane, cancellationToken)
            .ToDictionary(i => $"{i.Kind}|{i.Path}", StringComparer.OrdinalIgnoreCase);

        var chosen = new List<LeftoverItem>();
        var skipped = 0;
        foreach (var pick in selected.DistinctBy(s => $"{s.Kind}|{s.Path}", StringComparer.OrdinalIgnoreCase))
        {
            if (fresh.TryGetValue($"{pick.Kind}|{pick.Path}", out var item))
                chosen.Add(item);
            else
                skipped++;
        }

        var removed = 0;
        long freed = 0;

        foreach (var item in chosen.Where(i => i.Kind != LeftoverKind.RegistryKey))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var safe = item.Kind == LeftoverKind.Folder
                ? LeftoverTargetSafety.IsSafeFolder(item.Path, _roots, out _)
                : LeftoverTargetSafety.IsSafeShortcut(item.Path, _roots, out _);
            if (!safe)
            {
                _logging.Warn($"Leftovers: '{item.Path}' no longer passes the safety check; kept.");
                skipped++;
                continue;
            }

            if (quarantine is not null && quarantine.TryMove(item))
            {
                removed++;
                freed += item.SizeBytes;
                continue;
            }

            if (quarantine is not null)
                _logging.Warn($"Leftovers: '{item.Path}' could not go into the quarantine; it goes to the recycle bin instead.");

            if (_recycle(item.Path))
            {
                removed++;
                freed += item.SizeBytes;
            }
            else
            {
                skipped++;
            }
        }

        var keys = chosen.Where(i => i.Kind == LeftoverKind.RegistryKey).ToList();
        if (quarantine is not null && keys.Count > 0)
        {
            // Each safe key is exported into the quarantine first; only then it is removed. A key the quarantine
            // could not take stays in the list for the regular backup below.
            var remaining = new List<LeftoverItem>();
            foreach (var key in keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!LeftoverTargetSafety.IsSafeRegistryKeyPath(key.Path, out _)
                    || !await quarantine.TryBackupKeyAsync(key, cancellationToken))
                {
                    remaining.Add(key);
                    continue;
                }

                var status = _deleteKey(key.Path);
                if (status == RegistryBackupService.KeyTreeDeleteStatus.Deleted)
                    removed++;
                else
                {
                    _logging.Warn($"Leftovers: '{key.Path}' was not removed ({status}).");
                    skipped++;
                }
            }

            keys = remaining;
        }

        if (keys.Count > 0)
        {
            var (done, kept) = await RemoveKeysAsync(keys, backupFolder, backupRetention, cancellationToken);
            removed += done;
            skipped += kept;
        }

        return new LeftoverRemoval(removed, skipped, freed);
    }

    private async Task<(int Removed, int Kept)> RemoveKeysAsync(
        IReadOnlyList<LeftoverItem> keys, string backupFolder, int retention, CancellationToken cancellationToken)
    {
        var safeKeys = keys.Where(k => LeftoverTargetSafety.IsSafeRegistryKeyPath(k.Path, out _)).ToList();

        // The backup comes first and its result decides: no backup, nothing is deleted.
        var backup = safeKeys.Count == 0
            ? null
            : await _backup([.. safeKeys.Select(k => k.Path)], backupFolder, retention, cancellationToken);
        if (backup is null)
        {
            _logging.Warn($"Leftovers: the registry backup failed or found nothing; {keys.Count} key(s) kept.");
            return (0, keys.Count);
        }

        var removed = 0;
        foreach (var key in safeKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!backup.ExportedRoots.Any(root => string.Equals(root, key.Path, StringComparison.OrdinalIgnoreCase)))
            {
                _logging.Warn($"Leftovers: '{key.Path}' is not in the backup; kept.");
                continue;
            }

            var status = _deleteKey(key.Path);
            if (status == RegistryBackupService.KeyTreeDeleteStatus.Deleted)
                removed++;
            else
                _logging.Warn($"Leftovers: '{key.Path}' was not removed ({status}).");
        }

        return (removed, keys.Count - removed);
    }

    /// <summary>
    /// Removes one <c>Software\&lt;publisher&gt;\&lt;program&gt;</c> key with its subkeys. The publisher key is
    /// opened link by link (a link anywhere ends it), then the program key is deleted through handles only.
    /// </summary>
    internal static RegistryBackupService.KeyTreeDeleteStatus DeleteKeyTree(string keyPath, LoggingService logging)
    {
        if (!LeftoverTargetSafety.IsSafeRegistryKeyPath(keyPath, out _))
            return RegistryBackupService.KeyTreeDeleteStatus.OpenFailed;

        var segments = keyPath.Split('\\');
        var program = segments[^1];
        var publisher = segments[^2];

        try
        {
            RegistryKey? parent;
            if (string.Equals(segments[0], "HKCU", StringComparison.OrdinalIgnoreCase))
            {
                parent = RegistryBackupService.OpenHkcuLinkSafe($@"Software\{publisher}", writable: true, logging);
            }
            else
            {
                // The Software key (and the WOW6432Node key under it) is admin-only ancestry and may itself be a
                // link; only what lies below it is walked link by link.
                var rootPath = segments.Length == 5 ? @"Software\WOW6432Node" : "Software";
                using var root = Registry.LocalMachine.OpenSubKey(rootPath, writable: false);
                parent = root is null
                    ? null
                    : RegistryBackupService.OpenBelowLinkSafe(root.Handle, publisher, writable: true, logging, "HKLM");
            }

            if (parent is null)
                return RegistryBackupService.KeyTreeDeleteStatus.NotFound;

            using (parent)
                return RegistryBackupService.DeleteKeyTreeLinkSafe(parent.Handle, program, out _);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            logging.Warn($"Leftovers: could not open '{keyPath}' for removal: {ex.Message}");
            return RegistryBackupService.KeyTreeDeleteStatus.OpenFailed;
        }
    }
}
