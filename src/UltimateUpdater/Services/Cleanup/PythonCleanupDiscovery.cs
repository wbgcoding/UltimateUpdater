using System.IO;
using UltimateUpdater.Services.Sources;
using UltimateUpdater.Services.Sources.PackageManagers;

namespace UltimateUpdater.Services.Cleanup;

/// <summary>One broken pip-install leftover folder. Safe to delete outright: nothing
/// under a "~"-prefixed name is a real, importable package under any Python packaging convention (see
/// <see cref="PythonInterpreters.FindBrokenInstallLeftovers"/>).</summary>
public sealed record PipLeftoverCandidate(string Path, long SizeBytes);

/// <summary>
/// One installed Python interpreter older than the newest of its own architecture, offered for removal
/// through its own registered uninstaller. Never built for: the newest interpreter of
/// an architecture, the only interpreter on the machine (both already ruled out by
/// <see cref="PythonInterpreters.FindOlderVersions"/> itself), one PATH resolves <c>python</c>/<c>pip</c>
/// to, or one with no registered uninstaller at all (a portable/embeddable Python). <c>VenvUseCount</c> is
/// filled in by <see cref="PythonCleanupDiscovery.CountVenvUses(IReadOnlyList{PythonVersionCandidate}, CancellationToken)"/>
/// on the scan path only; 0 also means "not looked at". A venv, a conda
/// environment and the Windows Store alias are never even discovered in the first place, see
/// <see cref="PythonInterpreters.Discover(IRegistryView)"/>'s own doc comment.
/// </summary>
public sealed record PythonVersionCandidate(
    string PythonExe, string DisplayLabel, string? SitePackages, long SizeBytes, int PackageCount,
    string UninstallCommand, bool PerUser, int VenvUseCount = 0);

/// <summary>
/// Turns the pure judgments already in <see cref="PythonInterpreters"/> into the two rows the cleanup
/// dialog offers. Kept apart from that class (owned by the pip source) so this cleanup-specific decision,
/// which folders/rows the dialog actually shows, never has to become its concern; interpreter discovery
/// itself is the one seam every caller, this one included, reuses as-is.
/// </summary>
public static class PythonCleanupDiscovery
{
    /// <summary>Bounds a size walk the same way <see cref="LeftoverInstallDiscovery"/> does: a size shown
    /// next to a checkbox is a hint, not an audited total, and a real site-packages tree can hold tens of
    /// thousands of small files (numpy alone ships hundreds).</summary>
    private const int MaxFilesScannedPerCandidate = 20000;

    /// <summary>Every "~"-prefixed folder under every registered interpreter's own site-packages, across
    /// the whole machine. <paramref name="measure"/> false leaves every size at 0 for a caller that only
    /// needs the paths (the cleanup itself counts what it actually frees), sparing a walk of each folder.
    /// The per-user site-packages under the roaming profile (<paramref name="roamingAppData"/>, the signed-in
    /// user's AppData\Roaming unless a test names another) is searched as well: pip installs there when the
    /// interpreter folder is not writable.</summary>
    public static IReadOnlyList<PipLeftoverCandidate> DiscoverPipLeftovers(
        IRegistryView registry, bool measure = true, Action<string>? logSkip = null, string? roamingAppData = null,
        DateTime? utcNow = null)
    {
        var found = new List<PipLeftoverCandidate>();

        foreach (var interpreter in PythonInterpreters.Discover(registry))
        {
            var sitePackages = PythonInterpreters.SitePackagesFor(interpreter);
            if (sitePackages is null)
                continue;

            foreach (var directory in PythonInterpreters.FindBrokenInstallLeftovers(sitePackages, logSkip: logSkip))
            {
                var size = measure
                    ? DirectorySizeWalker.Measure(directory, DirectorySizeWalker.DefaultMaxDepth, MaxFilesScannedPerCandidate)
                    : 0;
                found.Add(new PipLeftoverCandidate(directory, size));
            }
        }

        var roaming = roamingAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        foreach (var sitePackages in UserSitePackages(roaming))
        {
            foreach (var directory in PythonInterpreters.FindBrokenInstallLeftovers(sitePackages, utcNow, logSkip))
            {
                // A "~" folder that is a link could point anywhere; only a real folder is ever offered.
                if (IsLink(directory))
                    continue;

                var size = measure
                    ? DirectorySizeWalker.Measure(directory, DirectorySizeWalker.DefaultMaxDepth, MaxFilesScannedPerCandidate)
                    : 0;
                found.Add(new PipLeftoverCandidate(directory, size));
            }
        }

        return found;
    }

    /// <summary>The <c>site-packages</c> folders of <c>Roaming\Python\Python3*</c> that exist. Neither the
    /// Python folder, a version folder nor site-packages itself may be a link: the walk below them would
    /// otherwise leave the profile.</summary>
    internal static IReadOnlyList<string> UserSitePackages(string? roamingAppData)
    {
        if (string.IsNullOrWhiteSpace(roamingAppData) || !CleanupSafety.IsLocalPathWithoutLinks(roamingAppData))
            return [];

        try
        {
            var pythonRoot = Path.Combine(roamingAppData, "Python");
            if (!Directory.Exists(pythonRoot) || CleanupSafety.AncestryLeadsThroughLink(pythonRoot))
                return [];

            var result = new List<string>();
            foreach (var versionFolder in Directory.GetDirectories(pythonRoot, "Python3*"))
            {
                if (IsLink(versionFolder))
                    continue;

                var sitePackages = Path.Combine(versionFolder, "site-packages");
                if (Directory.Exists(sitePackages) && !IsLink(sitePackages))
                    result.Add(sitePackages);
            }

            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return [];
        }
    }

    private static bool IsLink(string directory)
    {
        try
        {
            var info = new DirectoryInfo(directory);
            return info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// Older interpreters this machine could remove, one row each, newest-architecture-newest-version rule
    /// already applied by <see cref="PythonInterpreters.FindOlderVersions"/>. <paramref name="resolveOnPath"/>
    /// is <see cref="ProcessRunner.Resolve"/> by default; a test supplies a fake so the PATH exclusion can
    /// be proven without touching the real machine's PATH.
    /// </summary>
    public static IReadOnlyList<PythonVersionCandidate> DiscoverOldVersions(
        IRegistryView registry, Func<string, string?>? resolveOnPath = null)
    {
        var resolve = resolveOnPath ?? ProcessRunner.Resolve;
        var interpreters = PythonInterpreters.Discover(registry);
        var pathExe = ResolvePathInterpreter(interpreters, resolve);

        var candidates = new List<PythonVersionCandidate>();

        foreach (var interpreter in PythonInterpreters.FindOlderVersions(interpreters))
        {
            if (pathExe is not null && PathsEqual(interpreter.PythonExe, pathExe))
                continue;

            var command = PythonInterpreters.FindUninstallCommand(registry, interpreter);
            if (command is null)
                continue;

            var sitePackages = PythonInterpreters.SitePackagesFor(interpreter);
            var size = sitePackages is null ? 0 : DirectorySizeWalker.Measure(sitePackages, DirectorySizeWalker.DefaultMaxDepth, MaxFilesScannedPerCandidate);
            var packageCount = sitePackages is null ? 0 : CountPackages(sitePackages);

            candidates.Add(new PythonVersionCandidate(
                interpreter.PythonExe, interpreter.DisplayLabel, sitePackages, size, packageCount, command,
                interpreter.PerUser));
        }

        return candidates;
    }

    /// <summary>The whole walk for virtual environments stops after this long: the count is a warning
    /// shown next to a checkbox, never worth a scan that hangs on a huge profile.</summary>
    private static readonly TimeSpan VenvScanTimeCap = TimeSpan.FromSeconds(5);

    private const int VenvProfileDepth = 6;

    /// <summary>Adds, per candidate, how many virtual environments (<c>pyvenv.cfg</c> whose <c>home</c> lies
    /// inside that interpreter's folder) would break if it were uninstalled. Called from the scan only: the
    /// elevated executor re-discovers without it, because the count never gates a deletion, it only warns.</summary>
    public static IReadOnlyList<PythonVersionCandidate> CountVenvUses(
        IReadOnlyList<PythonVersionCandidate> candidates, string? callerProfilePath, CancellationToken cancellationToken) =>
        CountVenvUses(candidates, DefaultVenvRoots(callerProfilePath), VenvScanTimeCap, cancellationToken,
            TempFoldersToSkip(callerProfilePath));

    internal readonly record struct VenvSearchRoot(string Path, int MaxDepth);

    /// <summary>The profile the search starts from: the signed-in user's folder as the unelevated app reported
    /// it when that is a real absolute folder, else this process's own. The elevated worker may run as another
    /// account, whose profile would hold none of the signed-in user's environments. The path is only read.</summary>
    internal static string? ResolveProfile(string? callerProfilePath)
    {
        // Local and link-free only: the elevated worker walks this folder, and a share would make the admin
        // account sign in to it.
        if (CleanupSafety.IsLocalPathWithoutLinks(callerProfilePath))
        {
            try
            {
                var full = Path.GetFullPath(callerProfilePath);
                if (Directory.Exists(full))
                    return full;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // fall back to the process's own profile
            }
        }

        var own = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(own) ? null : own;
    }

    internal static IReadOnlyList<VenvSearchRoot> DefaultVenvRoots(string? callerProfilePath = null)
    {
        var profile = ResolveProfile(callerProfilePath);
        if (profile is null)
            return [];

        return
        [
            new VenvSearchRoot(profile, VenvProfileDepth),
            new VenvSearchRoot(Path.Combine(profile, "AppData", "Local", "pipx", "venvs"), 2),
            new VenvSearchRoot(Path.Combine(profile, "AppData", "Roaming", "pypoetry", "virtualenvs"), 2),
            new VenvSearchRoot(Path.Combine(profile, "AppData", "Local", "pypoetry", "Cache", "virtualenvs"), 2),
        ];
    }

    internal static IReadOnlyList<PythonVersionCandidate> CountVenvUses(
        IReadOnlyList<PythonVersionCandidate> candidates, IReadOnlyList<VenvSearchRoot> roots,
        TimeSpan timeCap, CancellationToken cancellationToken, HashSet<string>? skippedTemp = null)
    {
        if (candidates.Count == 0)
            return candidates;

        var homes = FindVenvHomes(roots, timeCap, cancellationToken, skippedTemp);
        if (homes.Count == 0)
            return candidates;

        return candidates
            .Select(c => c with { VenvUseCount = homes.Count(h => IsInsideFolder(h, SafeDirectoryName(c.PythonExe))) })
            .ToList();
    }

    /// <summary>The <c>home</c> directory of every virtual environment found under <paramref name="roots"/>.
    /// Never descends a junction or symlink, a Temp folder, <c>node_modules</c> or <c>.git</c>; a found
    /// environment is not searched further. Unreadable folders and files are skipped, never fatal.</summary>
    internal static IReadOnlyList<string> FindVenvHomes(
        IReadOnlyList<VenvSearchRoot> roots, TimeSpan timeCap, CancellationToken cancellationToken,
        HashSet<string>? skippedTemp = null)
    {
        var homes = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        skippedTemp ??= TempFoldersToSkip(null);
        var clock = System.Diagnostics.Stopwatch.StartNew();

        foreach (var root in roots)
        {
            // The listing below skips links, but a search root could be one itself (the profile the
            // unelevated app reported is user-writable, and the elevated worker walks it).
            if (CleanupSafety.AncestryLeadsThroughLink(root.Path))
                continue;

            var pending = new Stack<(string Path, int Depth)>();
            pending.Push((root.Path, 0));

            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (clock.Elapsed > timeCap)
                    return homes;

                var (directory, depth) = pending.Pop();
                if (!seen.Add(directory))
                    continue;

                var home = TryReadVenvHome(Path.Combine(directory, "pyvenv.cfg"), out var isVenv);
                if (isVenv)
                {
                    if (home is not null)
                        homes.Add(home);
                    continue;
                }

                if (depth >= root.MaxDepth)
                    continue;

                foreach (var sub in SafeEnumerate.Directories(directory))
                {
                    var name = Path.GetFileName(sub);
                    if (string.Equals(name, "node_modules", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase)
                        || skippedTemp.Contains(TrimSeparators(sub)))
                    {
                        continue;
                    }

                    pending.Push((sub, depth + 1));
                }
            }
        }

        return homes;
    }

    internal static HashSet<string> TempFoldersToSkip(string? callerProfilePath)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local))
            set.Add(TrimSeparators(Path.Combine(local, "Temp")));

        if (ResolveProfile(callerProfilePath) is { } profile)
            set.Add(TrimSeparators(Path.Combine(profile, "AppData", "Local", "Temp")));

        set.Add(TrimSeparators(Path.GetTempPath()));
        return set;
    }

    private static string TrimSeparators(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>The <c>home = ...</c> value of a <c>pyvenv.cfg</c>; <paramref name="isVenv"/> tells a
    /// readable config apart from no file at all, so a venv without a usable home still stops the descent.</summary>
    private static string? TryReadVenvHome(string cfgPath, out bool isVenv)
    {
        isVenv = false;
        try
        {
            if (!File.Exists(cfgPath))
                return null;

            isVenv = true;
            // A linked config is never followed (it could point at a share): no evidence either way.
            if (File.GetAttributes(cfgPath).HasFlag(FileAttributes.ReparsePoint))
                return null;

            // A real config is a handful of lines; the cap keeps a planted huge file harmless.
            foreach (var line in File.ReadLines(cfgPath).Take(64))
            {
                var eq = line.IndexOf('=');
                if (eq <= 0 || !line[..eq].Trim().Equals("home", StringComparison.OrdinalIgnoreCase))
                    continue;

                var value = line[(eq + 1)..].Trim().Trim('"');
                return value.Length == 0 ? null : value;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable: no evidence either way.
        }

        return null;
    }

    private static bool IsInsideFolder(string path, string? folder)
    {
        if (folder is null)
            return false;

        try
        {
            var full = TrimSeparators(Path.GetFullPath(path));
            var root = TrimSeparators(Path.GetFullPath(folder));
            return full.Equals(root, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// The interpreter PATH itself resolves <c>python</c> or <c>pip</c> to, checked both ways: pip's own
    /// launcher lives in the interpreter's Scripts folder, never next to python.exe itself, so matching
    /// python.exe alone would miss a machine where only "pip" is ever actually typed.
    /// </summary>
    private static string? ResolvePathInterpreter(
        IReadOnlyList<PythonInterpreter> interpreters, Func<string, string?> resolve)
    {
        var pythonOnPath = resolve("python");
        var byPython = pythonOnPath is null
            ? null
            : interpreters.Select(i => i.PythonExe).FirstOrDefault(exe => PathsEqual(exe, pythonOnPath));
        if (byPython is not null)
            return byPython;

        var pipOnPath = resolve("pip");
        var pipDirectory = pipOnPath is null ? null : SafeDirectoryName(pipOnPath);
        if (pipDirectory is null)
            return null;

        return interpreters.Select(i => i.PythonExe)
            .FirstOrDefault(exe =>
                PathsEqual(pipDirectory, Path.Combine(SafeDirectoryName(exe) ?? string.Empty, "Scripts")));
    }

    private static string? SafeDirectoryName(string path)
    {
        try
        {
            return Path.GetDirectoryName(path);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>One row per installed package is approximated by counting its metadata folder: every real
    /// installer (pip, wheel, setuptools) leaves exactly one *.dist-info or, for an older egg-based
    /// install, *.egg-info folder per package, never the package's own code folder, which can be split
    /// across several without any fixed naming at all.</summary>
    private static int CountPackages(string sitePackages)
    {
        try
        {
            return Directory.EnumerateDirectories(sitePackages)
                .Count(d => Path.GetFileName(d).EndsWith(".dist-info", StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(d).EndsWith(".egg-info", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
