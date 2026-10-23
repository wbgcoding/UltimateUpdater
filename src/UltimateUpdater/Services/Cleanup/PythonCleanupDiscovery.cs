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
/// to, or one with no registered uninstaller at all (a portable/embeddable Python). A venv, a conda
/// environment and the Windows Store alias are never even discovered in the first place, see
/// <see cref="PythonInterpreters.Discover(IRegistryView)"/>'s own doc comment.
/// </summary>
public sealed record PythonVersionCandidate(
    string PythonExe, string DisplayLabel, string? SitePackages, long SizeBytes, int PackageCount,
    string UninstallCommand, bool PerUser);

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
    /// needs the paths (the cleanup itself counts what it actually frees), sparing a walk of each folder.</summary>
    public static IReadOnlyList<PipLeftoverCandidate> DiscoverPipLeftovers(IRegistryView registry, bool measure = true)
    {
        var found = new List<PipLeftoverCandidate>();

        foreach (var interpreter in PythonInterpreters.Discover(registry))
        {
            var sitePackages = PythonInterpreters.SitePackagesFor(interpreter);
            if (sitePackages is null)
                continue;

            foreach (var directory in PythonInterpreters.FindBrokenInstallLeftovers(sitePackages))
            {
                var size = measure
                    ? DirectorySizeWalker.Measure(directory, DirectorySizeWalker.DefaultMaxDepth, MaxFilesScannedPerCandidate)
                    : 0;
                found.Add(new PipLeftoverCandidate(directory, size));
            }
        }

        return found;
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
