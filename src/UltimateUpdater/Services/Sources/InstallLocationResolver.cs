using System.IO;
using UltimateUpdater.Models;

namespace UltimateUpdater.Services.Sources;

/// <summary>
/// Finds the directory an update is installed in, for the row's "open folder" action. Three answers,
/// most reliable first: a location the source already knows and put on the item (Steam's game folder);
/// the folder of the executable the row would open (a launcher client); and, the general case, the
/// InstallLocation the installer wrote into the Windows uninstall registry, matched to the row by its
/// DisplayName or its uninstall key.
/// Conservative on purpose: opening the WRONG program's folder is worse than offering nothing, so a
/// registry entry is matched on its full DisplayName (or the exact uninstall key), never by similarity,
/// and a directory is returned only once it is confirmed to exist. Rows that resolve to nothing simply
/// get no menu entry. The registry seam and the filesystem probes are parameters so the matching rules
/// can be tested without depending on whatever happens to be installed.
/// </summary>
public sealed class InstallLocationResolver
{
    // The same four hives a launcher lookup reads: the 64-bit and 32-bit machine hives, and the two
    // per-user ones (many apps, and every per-user install, register only under HKCU).

    private readonly IRegistryView _registry;
    private readonly Func<string, bool> _directoryExists;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, IEnumerable<string>> _executablesIn;
    private readonly Func<string, IEnumerable<string>> _subDirectoriesOf;
    private readonly string _winGetPackagesRoot;
    private readonly IReadOnlyList<string> _pathDirectories;
    private readonly object _indexLock = new();

    /// <summary>
    /// The four lookup dictionaries as ONE immutable object, published through a single reference write,
    /// so the lock-free fast path either sees the whole index fully built or not at all.
    /// </summary>
    private sealed record Index(
        IReadOnlyDictionary<string, string> ByDisplayName,
        IReadOnlyDictionary<string, string> ByUninstallKey,
        IReadOnlyDictionary<string, string> ExeByDisplayName,
        IReadOnlyDictionary<string, string> ExeByUninstallKey);

    // Built from the uninstall hives on first use, then reused until the next scan drops it: a right-click
    // must not re-read the registry, but a long tray session must still see programs installed since.
    private volatile Index? _index;

    /// <summary>Bumped by <see cref="Invalidate"/>; a build that started before the bump still answers its
    /// own caller but is not kept, so it cannot put the old registry state back.</summary>
    private int _generation;

    /// <summary>Drops the index so the next lookup reads the uninstall hives again.</summary>
    public void Invalidate()
    {
        Interlocked.Increment(ref _generation);
        _index = null;
    }

    public InstallLocationResolver(
        IRegistryView registry,
        Func<string, bool>? directoryExists = null,
        Func<string, bool>? fileExists = null,
        Func<string, IEnumerable<string>>? executablesIn = null,
        Func<string, IEnumerable<string>>? subDirectoriesOf = null,
        string? winGetPackagesRoot = null,
        IReadOnlyList<string>? pathDirectories = null)
    {
        _registry = registry;
        _directoryExists = directoryExists ?? Directory.Exists;
        _fileExists = fileExists ?? File.Exists;
        _executablesIn = executablesIn ?? DefaultExecutablesIn;
        _subDirectoriesOf = subDirectoriesOf ?? DefaultSubDirectoriesOf;
        _winGetPackagesRoot = winGetPackagesRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
        _pathDirectories = pathDirectories ?? DefaultPathDirectories();
    }

    private static IEnumerable<string> DefaultSubDirectoriesOf(string directory)
    {
        try
        {
            return Directory.Exists(directory) ? Directory.GetDirectories(directory) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    private static IReadOnlyList<string> DefaultPathDirectories() =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    /// <summary>
    /// The.exe files in an install directory: its own level plus the two subfolder names installers
    /// conventionally use. Not recursive; a deep walk of a large install directory on every row would cost
    /// far more than the answer is worth, and the program's own exe is essentially never buried deeper.
    /// </summary>
    private static IEnumerable<string> DefaultExecutablesIn(string directory)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        foreach (var folder in new[] { directory, Path.Combine(directory, "bin"), Path.Combine(directory, "current") })
        {
            string[] found;
            try
            {
                found = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.exe", options) : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                continue;
            }

            foreach (var file in found)
                yield return file;
        }
    }

    /// <summary>The install directory for this row, or null when none can be resolved reliably.</summary>
    public string? Resolve(UpdateItem item)
    {
        // 1. A folder the source itself recorded (Steam writes the game's install path into Meta).
        if (item.Meta.TryGetValue("installLocation", out var known)
            && !string.IsNullOrWhiteSpace(known) && _directoryExists(known))
        {
            return known;
        }

        // 2. The uninstall registry; the one place a Windows installer must register itself, and so
        // the only general answer to "where did this end up". Matched by DisplayName or key.
        var index = EnsureIndex();
        if (FromRegistry(item, index) is { } fromRegistry)
            return fromRegistry;

        // 3. The folder of the executable the row would open (a launcher client's own directory). A
        // URI target (steam://…) is not a rooted path, so it is skipped here.
        if (!string.IsNullOrEmpty(item.OpenTarget) && Path.IsPathRooted(item.OpenTarget) && _fileExists(item.OpenTarget))
        {
            var directory = Path.GetDirectoryName(item.OpenTarget);
            if (!string.IsNullOrEmpty(directory) && _directoryExists(directory))
                return directory;
        }

        // 4. winget: a portable or MSIX install winget did not also register uninstall metadata for still
        // lands under a folder named "<PackageId>_<publisher hash>" here; winget's own convention, not a
        // guess. Matched on the id as a whole prefix, never a partial id, for the same reason as everywhere
        // else in this class.
        if (item.SourceId == "winget" && FromWinGetPackages(item.Id) is { } fromWinGet)
            return fromWinGet;

        // 5. Developer-tool CLI packages (npm, pip, cargo, …): their manager put a console script
        // somewhere on PATH, named after the package itself. Exact name match only; package ids that
        // rename their own binary (most do) simply resolve to nothing rather than a wrong folder.
        if (item.Category == UpdateCategory.DeveloperTools && FindOnPath(item.Id) is { } exeOnPath)
        {
            var directory = Path.GetDirectoryName(exeOnPath);
            if (!string.IsNullOrEmpty(directory) && _directoryExists(directory))
                return directory;
        }

        return null;
    }

    private string? FromWinGetPackages(string packageId)
    {
        var id = packageId.Trim();
        if (id.Length == 0)
            return null;

        var prefix = id + "_";
        return _subDirectoriesOf(_winGetPackagesRoot)
            .FirstOrDefault(dir => Path.GetFileName(dir).StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly string[] PathExeExtensions = [".exe", ".cmd", ".bat"];

    private string? FindOnPath(string packageId)
    {
        var name = packageId.Trim();
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return null;

        foreach (var dir in _pathDirectories)
        {
            foreach (var ext in PathExeExtensions)
            {
                var candidate = Path.Combine(dir, name + ext);
                if (_fileExists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// An executable that STARTS this item, or null when none can be resolved reliably; what the row's
    /// "Start" needs for an ordinary package (a winget/Chocolatey app has no launch URI of its own, so the
    /// entry never appeared at all). Same conservative rules as the folder lookup: an
    /// entry is matched on its full DisplayName or its exact uninstall key, and the file must exist.
    /// Uninstallers and installer stubs are deliberately excluded; starting one of those would be worse
    /// than offering nothing.
    /// </summary>
    /// <param name="installDirectory">
    /// The directory <see cref="Resolve"/> already found for this item, when the caller has it. Passing it
    /// in avoids repeating the whole registry and existence lookup for every row.
    /// </param>
    public string? ResolveLaunchExe(UpdateItem item, string? installDirectory = null)
    {
        // A target the source itself knows (a launcher client's exe) always wins.
        if (!string.IsNullOrEmpty(item.OpenTarget) && Path.IsPathRooted(item.OpenTarget)
            && item.OpenTarget.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && _fileExists(item.OpenTarget))
        {
            return item.OpenTarget;
        }

        var index = EnsureIndex();

        if (index.ExeByDisplayName.TryGetValue(item.Name.Trim(), out var byName) && _fileExists(byName))
            return byName;

        if (index.ExeByUninstallKey.TryGetValue(item.Id.Trim(), out var byKey) && _fileExists(byKey))
            return byKey;

        // Plenty of packages register no DisplayIcon at all (most winget and Chocolatey ones do not),
        // which would leave "Start" missing on many rows. Their install directory is
        // known, so the program's own exe is looked for inside it.
        var directory = installDirectory ?? Resolve(item);
        return directory is null ? null : PickLaunchExe(directory, item.Name);
    }

    /// <summary>
    /// The one executable in an install directory that plausibly IS this program. Conservative by the same
    /// rule as everything else here: a name match, or a single candidate; never a guess between several,
    /// because starting the wrong program is worse than offering nothing.
    /// </summary>
    internal string? PickLaunchExe(string directory, string itemName)
    {
        List<string> candidates;
        try
        {
            candidates = _executablesIn(directory).Where(exe => !IsNotStartable(exe)).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        if (candidates.Count == 0)
            return null;

        var wanted = Simplify(itemName);

        // An exact match on the simplified name: "Notepad++" -> notepad++.exe, "7-Zip" -> 7zip.exe.
        var exact = candidates.FirstOrDefault(exe =>
            string.Equals(Simplify(Path.GetFileNameWithoutExtension(exe)), wanted, StringComparison.Ordinal));
        if (exact is not null)
            return exact;

        // Then one that contains the other: "Mozilla Firefox" -> firefox.exe, "VLC" -> vlc.exe. Only when
        // exactly one candidate qualifies, so a folder full of tools never picks arbitrarily.
        if (wanted.Length >= 3)
        {
            var contained = candidates.Where(exe =>
            {
                var name = Simplify(Path.GetFileNameWithoutExtension(exe));
                return name.Length >= 3 && (wanted.Contains(name, StringComparison.Ordinal)
                                            || name.Contains(wanted, StringComparison.Ordinal));
            }).ToList();

            if (contained.Count == 1)
                return contained[0];
        }

        // A directory with exactly one startable executable leaves nothing to get wrong.
        return candidates.Count == 1 ? candidates[0] : null;
    }

    /// <summary>Lower-case, letters and digits only; so "Notepad++ (64-bit)" and "notepad++" compare equal.</summary>
    private static string Simplify(string value) =>
        string.Concat(value.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    /// <summary>Executables that would remove or re-install rather than start the program.</summary>
    private static bool IsNotStartable(string exePath)
    {
        var name = Path.GetFileNameWithoutExtension(exePath);
        return name.Contains("unins", StringComparison.OrdinalIgnoreCase)
            || name.Contains("uninstall", StringComparison.OrdinalIgnoreCase)
            || name.Contains("setup", StringComparison.OrdinalIgnoreCase)
            || name.Contains("installer", StringComparison.OrdinalIgnoreCase);
    }

    private string? FromRegistry(UpdateItem item, Index index)
    {
        if (index.ByDisplayName.TryGetValue(item.Name.Trim(), out var byName) && _directoryExists(byName))
            return byName;

        // winget ids are sometimes the uninstall key itself (a product code or an ARP entry name).
        if (index.ByUninstallKey.TryGetValue(item.Id.Trim(), out var byKey) && _directoryExists(byKey))
            return byKey;

        return null;
    }

    private Index EnsureIndex()
    {
        if (_index is { } ready)
            return ready;

        lock (_indexLock)
        {
            if (_index is { } readyAfterLock)
                return readyAfterLock;

            var generation = Volatile.Read(ref _generation);

            var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var byKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var exeByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var exeByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var root in WindowsRegistryRoots.Uninstall)
            {
                foreach (var subKey in _registry.SubKeyNames(root))
                {
                    var entry = $@"{root}\{subKey}";
                    var displayName = _registry.Value(entry, "DisplayName") is { } name && !string.IsNullOrWhiteSpace(name)
                        ? name.Trim()
                        : null;

                    // The startable exe, from the same pass over the same entries.
                    if (LaunchExeOf(entry) is { } exe)
                    {
                        exeByKey.TryAdd(subKey.Trim(), exe);
                        if (displayName is not null)
                            exeByName.TryAdd(displayName, exe);
                    }

                    if (LocationOf(entry) is not { } location)
                        continue;

                    byKey.TryAdd(subKey.Trim(), location);

                    if (displayName is not null)
                        byName.TryAdd(displayName, location);
                }
            }

            var index = new Index(byName, byKey, exeByName, exeByKey);
            if (Volatile.Read(ref _generation) == generation)
                _index = index;
            return index;
        }
    }

    /// <summary>The install directory an uninstall entry points at: its InstallLocation, or the folder
    /// of its DisplayIcon executable when it names one.</summary>
    private string? LocationOf(string entry)
    {
        if (_registry.Value(entry, "InstallLocation") is { } installLocation)
        {
            var cleaned = NormalizeDirectory(CleanPath(installLocation));
            if (cleaned.Length > 0)
                return cleaned;
        }

        if (_registry.Value(entry, "DisplayIcon") is { } icon)
        {
            var exe = CleanPath(StripIconIndex(icon));
            if (exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                var directory = Path.GetDirectoryName(exe);
                if (!string.IsNullOrEmpty(directory))
                    return directory;
            }
        }

        return null;
    }

    /// <summary>The program's own exe as the Programs list knows it: DisplayIcon, when it names a real,
    /// startable executable (never an uninstaller or a setup stub).</summary>
    private string? LaunchExeOf(string entry)
    {
        if (_registry.Value(entry, "DisplayIcon") is not { } icon)
            return null;

        var exe = CleanPath(StripIconIndex(icon));
        return exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !IsNotStartable(exe) ? exe : null;
    }

    private static string CleanPath(string value) => UninstallEntryPaths.Clean(value);

    /// <summary>Drops a trailing separator (InstallLocation usually carries one), but keeps it on a bare
    /// drive root so "C:\" does not collapse to "C:".</summary>
    private static string NormalizeDirectory(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        return trimmed.Length == 2 && trimmed[1] == ':' ? trimmed + '\\' : trimmed;
    }

    private static string StripIconIndex(string value) => UninstallEntryPaths.StripIconIndex(value);
}
