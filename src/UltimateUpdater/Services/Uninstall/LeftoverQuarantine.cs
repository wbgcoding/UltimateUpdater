using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Services.Uninstall;

/// <summary>One thing the quarantine holds: what it was, where it came from, where it lies inside the quarantine
/// folder (relative to it) and whether it has been put back.</summary>
internal sealed class QuarantineEntry
{
    public LeftoverKind Kind { get; set; }

    public string OriginalPath { get; set; } = string.Empty;

    public string StoredAs { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public bool Restored { get; set; }
}

/// <summary>The manifest.json of one quarantine folder: the program the leftovers belonged to, which lane
/// the folder serves and every item it holds.</summary>
internal sealed class QuarantineManifest
{
    public int Version { get; set; } = 1;

    public LeftoverLane Lane { get; set; }

    public string Program { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; }

    public List<QuarantineEntry> Items { get; set; } = [];
}

/// <summary>Where quarantine folders live, how they are named, read and pruned.</summary>
internal static class QuarantineStore
{
    /// <summary>How long a removed leftover can be taken back.</summary>
    internal const int RetentionDays = 14;

    internal const string ManifestName = "manifest.json";

    /// <summary>A manifest or key export larger than this is not read back (a damaged or planted file).</summary>
    internal const long MaxManifestBytes = 4 * 1024 * 1024;

    private static readonly Regex FolderNamePattern = new(
        @"^(?<stamp>\d{4}-\d{2}-\d{2}_\d{6})(_\d{1,3})?$", RegexOptions.CultureInvariant);

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>The user's quarantine root: below the local app data folder, which only this account writes.</summary>
    internal static string UserRoot(string? localAppData = null) => Path.Combine(
        localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UltimateUpdater", "Quarantine");

    /// <summary>The machine quarantine root, resolved through the known-folder API like the driver backups (never
    /// <see cref="AppPaths"/>, never an environment variable).</summary>
    internal static string MachineRoot() => Path.Combine(
        DriverBackupStaging.ProgramDataRootOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "UltimateUpdater", "Quarantine");

    internal static bool IsFolderName(string? name) => name is not null && FolderNamePattern.IsMatch(name);

    /// <summary>A name for a new folder in <paramref name="root"/> that does not exist yet.</summary>
    internal static string FreeName(string root, DateTime now)
    {
        var stamp = now.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);
        var name = stamp;
        for (var n = 2; n < 100 && (Directory.Exists(Path.Combine(root, name)) || File.Exists(Path.Combine(root, name))); n++)
            name = $"{stamp}_{n}";

        return name;
    }

    /// <summary>
    /// The quarantine folder <paramref name="pathOrName"/> names, or null. A bare name is taken inside
    /// <paramref name="root"/>; a full path is resolved first and must then be a direct child of the root with a
    /// quarantine folder name (so <c>..</c> segments and look-alike roots lead nowhere). A folder or a root that
    /// leads through a link is refused.
    /// </summary>
    internal static string? ResolveFolder(string root, string? pathOrName)
    {
        if (string.IsNullOrWhiteSpace(pathOrName) || pathOrName.IndexOfAny(['\0', '*', '?', '<', '>', '|', '"']) >= 0)
            return null;

        try
        {
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
                pathOrName.Contains('\\') || pathOrName.Contains('/') || pathOrName.Contains(':')
                    ? pathOrName
                    : Path.Combine(fullRoot, pathOrName)));

            if (!string.Equals(Path.GetDirectoryName(full), fullRoot, StringComparison.OrdinalIgnoreCase)
                || !IsFolderName(Path.GetFileName(full))
                || CleanupSafety.AncestryLeadsThroughLink(full)
                || !Directory.Exists(full))
            {
                return null;
            }

            return full;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return null;
        }
    }

    /// <summary>The manifest of a quarantine folder, or null when it is missing, too big or not readable.</summary>
    internal static QuarantineManifest? ReadManifest(string folder)
    {
        try
        {
            var file = new FileInfo(Path.Combine(folder, ManifestName));
            if (!file.Exists || file.Length > MaxManifestBytes || file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return null;

            return JsonSerializer.Deserialize<QuarantineManifest>(File.ReadAllText(file.FullName, Encoding.UTF8), Json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The moment a folder name records (local time), or null for a name of another shape.</summary>
    internal static DateTime? CreatedOf(string folderName)
    {
        var match = FolderNamePattern.Match(folderName);
        return match.Success && DateTime.TryParseExact(
            match.Groups["stamp"].Value, "yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var moment)
            ? moment
            : null;
    }

    /// <summary>True while a folder is still inside its restore window.</summary>
    internal static bool IsRestorable(string? folderName, DateTime now) =>
        folderName is not null && CreatedOf(folderName) is { } moment && now - moment <= TimeSpan.FromDays(RetentionDays);

    /// <summary>
    /// Deletes the quarantine folders of <paramref name="root"/> that are older than <see cref="RetentionDays"/>,
    /// by the moment in their name. Only a plain folder with a quarantine folder name is a candidate (a link, a
    /// file or any other name stays), and a root that leads through a link is not entered. Failures are logged,
    /// never thrown; a folder that cannot be deleted now is tried again at the next prune.
    /// </summary>
    internal static int Prune(string root, DateTime now, LoggingService logging)
    {
        var removed = 0;
        try
        {
            if (!Directory.Exists(root) || CleanupSafety.AncestryLeadsThroughLink(root))
                return 0;

            foreach (var folder in SafeEnumerate.Directories(root).ToList())
            {
                if (CreatedOf(Path.GetFileName(folder)) is not { } moment || now - moment <= TimeSpan.FromDays(RetentionDays))
                    continue;

                try
                {
                    Directory.Delete(folder, recursive: true);
                    removed++;
                    logging.Info($"Leftover quarantine: removed the expired folder '{Path.GetFileName(folder)}'.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logging.Warn($"Leftover quarantine: could not remove the expired folder '{Path.GetFileName(folder)}': {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logging.Warn($"Leftover quarantine: could not prune '{root}': {ex.Message}");
        }

        return removed;
    }
}

/// <summary>
/// One quarantine folder being filled for one removed program in one lane. A folder or shortcut is MOVED in
/// (never copied, never deleted): the move is a rename, so the quarantine must lie on the same drive, and an
/// item the move cannot take is left to the caller. A registry key is exported into the folder first through
/// <see cref="RegistryBackupService"/>; the key's removal is the caller's and only follows a true result. The
/// manifest is written before each move, so what lies in the folder is always listed. A user session is a plain
/// folder below the local app data; a machine session is created by <see cref="DriverBackupStaging"/> with an ACL
/// of SYSTEM and Administrators only, pinned until it is disposed.
/// </summary>
internal sealed class QuarantineSession : IDisposable
{
    private readonly LoggingService _logging;
    private readonly Func<bool> _stillSafe;
    private readonly IDisposable? _pin;
    private readonly Func<string, string, CancellationToken, Task<RegistryBackupService.RegistryBackupResult?>> _backup;
    private int _next;
    private bool _disposed;

    private QuarantineSession(
        string root, LeftoverLane lane, string program, LoggingService logging, Func<bool> stillSafe, IDisposable? pin,
        Func<string, string, CancellationToken, Task<RegistryBackupService.RegistryBackupResult?>>? backup)
    {
        Root = root;
        FolderName = Path.GetFileName(root);
        _logging = logging;
        _stillSafe = stillSafe;
        _pin = pin;
        _backup = backup ?? ((key, folder, ct) =>
            RegistryBackupService.BackupRootsBeforeWriteAsync([key], folder, 0, logging, ct, allowedRoot: root));
        Manifest = new QuarantineManifest { Lane = lane, Program = program, CreatedUtc = DateTime.UtcNow };
    }

    /// <summary>The full path of this quarantine folder.</summary>
    internal string Root { get; }

    /// <summary>The folder's name, the handle the history entry keeps.</summary>
    internal string FolderName { get; }

    internal QuarantineManifest Manifest { get; }

    /// <summary>True once something went in.</summary>
    internal bool HasItems => Manifest.Items.Count > 0;

    private string ManifestPath => Path.Combine(Root, QuarantineStore.ManifestName);

    /// <summary>A user quarantine folder below <paramref name="root"/>, or null when none can be made (a link in
    /// the way, no access); the caller then removes the old way.</summary>
    internal static QuarantineSession? OpenUser(
        string program, LoggingService logging, string? root = null, DateTime? now = null,
        Func<string, string, CancellationToken, Task<RegistryBackupService.RegistryBackupResult?>>? backup = null)
    {
        root ??= QuarantineStore.UserRoot();
        try
        {
            if (CleanupSafety.AncestryLeadsThroughLink(root))
            {
                logging.Warn("Leftover quarantine: the user quarantine path leads through a link; not used.");
                return null;
            }

            Directory.CreateDirectory(root);
            var folder = Path.Combine(root, QuarantineStore.FreeName(root, now ?? DateTime.Now));
            if (CleanupSafety.AncestryLeadsThroughLink(folder))
            {
                logging.Warn("Leftover quarantine: the user quarantine path leads through a link; not used.");
                return null;
            }

            Directory.CreateDirectory(folder);
            if (CleanupSafety.AncestryLeadsThroughLink(folder))
            {
                logging.Warn("Leftover quarantine: a link appeared in the user quarantine path; not used.");
                return null;
            }

            return new QuarantineSession(folder, LeftoverLane.User, program, logging, () => !CleanupSafety.AncestryLeadsThroughLink(folder), null, backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            logging.Warn($"Leftover quarantine: could not create the user quarantine folder: {ex.Message}");
            return null;
        }
    }

    /// <summary>A machine quarantine folder under the ProgramData root, created with the administrator-only ACL,
    /// or null when that cannot be done safely. Runs in the elevated worker.</summary>
    internal static QuarantineSession? OpenMachine(
        string program, LoggingService logging, DateTime? now = null,
        Func<string, string, CancellationToken, Task<RegistryBackupService.RegistryBackupResult?>>? backup = null)
    {
        string name;
        try
        {
            name = QuarantineStore.FreeName(QuarantineStore.MachineRoot(), now ?? DateTime.Now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logging.Warn($"Leftover quarantine: could not read the machine quarantine folder: {ex.Message}");
            return null;
        }

        var staging = DriverBackupStaging.PrepareStagingRoot(
            logging, "Quarantine", usersCanRead: false, folderName: name, label: "Leftover quarantine");
        return staging is null
            ? null
            : new QuarantineSession(staging.BackupRoot, LeftoverLane.Machine, program, logging, staging.IsStillSafe, staging, backup);
    }

    /// <summary>Moves a folder or shortcut into the quarantine. False (nothing moved, nothing listed) when the
    /// path or the quarantine leads through a link, the item is gone, or the move fails (another drive, a locked file).</summary>
    internal bool TryMove(LeftoverItem item)
    {
        if (item.Kind == LeftoverKind.RegistryKey || !_stillSafe())
            return false;

        var source = LeftoverRoots.Full(item.Path);
        if (source is null)
            return false;

        try
        {
            if (CleanupSafety.AncestryLeadsThroughLink(source) || IsLink(source))
            {
                _logging.Warn($"Leftover quarantine: '{source}' leads through a link; not moved.");
                return false;
            }

            var itemsFolder = Path.Combine(Root, "items");
            Directory.CreateDirectory(itemsFolder);

            var index = ++_next;
            var storedAs = Path.Combine("items", item.Kind == LeftoverKind.Shortcut ? $"{index}.lnk" : index.ToString(CultureInfo.InvariantCulture));
            var target = Path.Combine(Root, storedAs);
            if (CleanupSafety.AncestryLeadsThroughLink(target) || Exists(target))
            {
                _logging.Warn($"Leftover quarantine: '{target}' is not free; '{source}' not moved.");
                return false;
            }

            var entry = new QuarantineEntry { Kind = item.Kind, OriginalPath = source, StoredAs = storedAs, SizeBytes = item.SizeBytes };
            Manifest.Items.Add(entry);
            if (!Save())
            {
                Manifest.Items.Remove(entry);
                return false;
            }

            try
            {
                if (item.Kind == LeftoverKind.Shortcut)
                    File.Move(source, target);
                else
                    Directory.Move(source, target);

                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logging.Warn($"Leftover quarantine: could not move '{source}': {ex.Message}");
                Manifest.Items.Remove(entry);
                Save();
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _logging.Warn($"Leftover quarantine: could not prepare the move of '{source}': {ex.Message}");
            return false;
        }
    }

    /// <summary>Exports one registry key into the quarantine and lists the export. True only when the export holds
    /// that key and is listed: the caller may then remove the key.</summary>
    internal async Task<bool> TryBackupKeyAsync(LeftoverItem key, CancellationToken cancellationToken)
    {
        if (key.Kind != LeftoverKind.RegistryKey || !_stillSafe())
            return false;

        var index = ++_next;
        var folder = Path.Combine(Root, "registry", index.ToString(CultureInfo.InvariantCulture));
        var result = await _backup(key.Path, folder, cancellationToken);
        if (result is null || !result.ExportedRoots.Any(r => string.Equals(r, key.Path, StringComparison.OrdinalIgnoreCase)))
        {
            TryRemoveEmpty(folder);
            return false;
        }

        Manifest.Items.Add(new QuarantineEntry
        {
            Kind = LeftoverKind.RegistryKey,
            OriginalPath = key.Path,
            StoredAs = Path.GetRelativePath(Root, result.Path),
        });

        return Save();
    }

    /// <summary>Writes the manifest. False when it cannot be written; nothing is then moved or deleted on its account.</summary>
    private bool Save()
    {
        try
        {
            File.WriteAllText(ManifestPath, JsonSerializer.Serialize(Manifest, QuarantineStore.Json), new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logging.Warn($"Leftover quarantine: could not write the manifest: {ex.Message}");
            return false;
        }
    }

    private static bool IsLink(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
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

    private static void TryRemoveEmpty(string folder)
    {
        try
        {
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                Directory.Delete(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // an empty folder left behind is pruned with the rest
        }
    }

    /// <summary>Ends the session. A folder that took nothing is removed again, so it never shows up as a restore point.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _pin?.Dispose();
        if (HasItems)
            return;

        try
        {
            if (Directory.Exists(Root) && !CleanupSafety.AncestryLeadsThroughLink(Root))
                Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logging.Warn($"Leftover quarantine: could not remove the empty folder '{FolderName}': {ex.Message}");
        }
    }
}
