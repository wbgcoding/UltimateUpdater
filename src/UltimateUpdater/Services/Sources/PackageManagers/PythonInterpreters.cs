using System.IO;
using System.Text.RegularExpressions;

namespace UltimateUpdater.Services.Sources.PackageManagers;

/// <summary>One Python interpreter this machine actually has installed, found through the registry
/// rather than trusted from PATH.</summary>
public readonly record struct PythonInterpreter(
    string PythonExe, Version Version, bool PerUser, bool Is64Bit)
{
    /// <summary>A short label for a row's detail/tooltip when more than one interpreter exists
    /// ("Python 3.14").</summary>
    public string DisplayLabel => $"Python {Version.Major}.{Version.Minor}";
}

/// <summary>
/// Finds every Python interpreter registered on this machine, per PEP 514: HKCU and HKLM (both the
/// native and WOW6432Node views) under <c>SOFTWARE\Python\PythonCore\&lt;tag&gt;</c>, each tag's own
/// <c>InstallPath</c> subkey carrying <c>ExecutablePath</c> (or, on an older installer, only the install
/// directory itself, with <c>python.exe</c> assumed to sit directly under it).
/// PATH is never consulted: with two or more versions installed side by side it only ever resolves to
/// one of them, which is exactly the bug this class exists to fix (a real machine with 3.13 and 3.14
/// per-user, where only 3.13 was ever scanned). The Windows Store's python.exe alias under
/// <c>WindowsApps</c> is skipped: launching it opens the Store listing rather than an interpreter when no
/// real one is installed, so it must never be reported as something this app can scan.
/// Behind <see cref="IRegistryView"/>, the same seam <see cref="GogGalaxySource"/>/<see cref="SteamSource"/>
/// already use, so a test can drive this from a fabricated registry rather than the real machine.
/// </summary>
public static class PythonInterpreters
{
    private static readonly (string KeyPath, bool PerUser)[] Roots =
    [
        (@"HKCU\SOFTWARE\Python\PythonCore", true),
        (@"HKLM\SOFTWARE\Python\PythonCore", false),
        (@"HKLM\SOFTWARE\WOW6432Node\Python\PythonCore", false),
    ];

    /// <summary>Every distinct, real interpreter this machine has registered, newest first.</summary>
    public static IReadOnlyList<PythonInterpreter> Discover(IRegistryView registry) =>
        Discover(registry, File.Exists);

    /// <summary>Overload for a test that also fakes which paths "exist" without touching the real disk.</summary>
    internal static IReadOnlyList<PythonInterpreter> Discover(IRegistryView registry, Func<string, bool> fileExists)
    {
        var found = new List<PythonInterpreter>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (rootPath, perUser) in Roots)
        {
            foreach (var tag in registry.SubKeyNames(rootPath))
            {
                var installPathKey = $@"{rootPath}\{tag}\InstallPath";
                var executablePath = registry.Value(installPathKey, "ExecutablePath");
                var installDirectory = registry.Value(installPathKey, string.Empty);

                var pythonExe = ResolvePythonExe(executablePath, installDirectory);
                if (pythonExe is null || !fileExists(pythonExe) || IsStoreAlias(pythonExe))
                    continue;

                string full;
                try
                {
                    full = Path.GetFullPath(pythonExe);
                }
                catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
                {
                    continue;
                }

                if (!seenPaths.Add(full))
                    continue;

                var version = ParseVersion(tag);
                // HKCU is not redirected, so a per-user 32-bit install registers as "3.11-32" in the same
                // root as the 64-bit ones (PEP 514): the tag says so, not only the WOW6432Node root.
                var is64Bit = !rootPath.Contains("WOW6432Node", StringComparison.OrdinalIgnoreCase)
                              && !tag.EndsWith("-32", StringComparison.OrdinalIgnoreCase);
                found.Add(new PythonInterpreter(full, version, perUser, is64Bit));
            }
        }

        return found.OrderByDescending(i => i.Version).ToList();
    }

    /// <summary>
    /// <c>ExecutablePath</c> is the documented PEP 514 value; a handful of older third-party installers
    /// only ever wrote <c>InstallPath</c>'s own default value, in which case <c>python.exe</c> is assumed
    /// to sit directly inside it, the layout every real CPython installer uses.
    /// </summary>
    private static string? ResolvePythonExe(string? executablePath, string? installDirectory)
    {
        if (!string.IsNullOrWhiteSpace(executablePath))
            return executablePath;

        return string.IsNullOrWhiteSpace(installDirectory) ? null : Path.Combine(installDirectory, "python.exe");
    }

    /// <summary>The Store alias lives under a fixed, well-known folder name regardless of which Store
    /// account installed it.</summary>
    private static bool IsStoreAlias(string pythonExe) =>
        pythonExe.Contains(
            Path.Combine("Microsoft", "WindowsApps"), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A PEP 514 tag is normally just "3.13"; a 32-bit interpreter registered on a 64-bit machine can
    /// carry a "-32" suffix and an ARM64 build a "-arm64" one, neither of which is a version component.
    /// Unparseable text (a future tag shape this has never seen) sorts last rather than throwing.
    /// </summary>
    internal static Version ParseVersion(string tag)
    {
        var numeric = new string(tag.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()).Trim('.');
        return Version.TryParse(numeric, out var version) ? version
            : int.TryParse(numeric, out var major) ? new Version(major, 0)
            : new Version(0, 0);
    }

    /// <summary>
    /// Subdirectories of <paramref name="sitePackages"/> whose name starts with <c>~</c>: exactly what pip
    /// leaves behind when an install is interrupted mid-rename (the old package's folder is renamed to a
    /// "~"-prefixed placeholder before the new one takes its place, and a crash between the two steps
    /// strands it there). pip then warns "Ignoring invalid distribution" about it on every later run.
    /// Only the name is matched, nothing about what is inside: a folder named this way is never a real,
    /// importable package under any Python packaging convention, so there is nothing else to check.
    /// </summary>
    public static IReadOnlyList<string> FindBrokenInstallLeftovers(string sitePackages)
    {
        if (string.IsNullOrWhiteSpace(sitePackages) || !Directory.Exists(sitePackages))
            return [];

        try
        {
            return Directory.GetDirectories(sitePackages, "~*")
                .Where(d => Path.GetFileName(d).StartsWith('~'))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary><c>Lib\site-packages</c> under an interpreter's own install root, wherever that root
    /// turns out to be (per-user installs under <c>AppData\Local\Programs\Python</c>, a machine-wide one
    /// under Program Files): derived from <see cref="PythonInterpreter.PythonExe"/> itself rather than
    /// guessed, since that is the one path this app actually verified exists.</summary>
    public static string? SitePackagesFor(PythonInterpreter interpreter) => SitePackagesForExe(interpreter.PythonExe);

    /// <summary>The same derivation from a bare <c>python.exe</c> path, for callers that hold only the path.</summary>
    public static string? SitePackagesForExe(string? pythonExe)
    {
        var root = Path.GetDirectoryName(pythonExe);
        return string.IsNullOrEmpty(root) ? null : Path.Combine(root, "Lib", "site-packages");
    }

    /// <summary>
    /// The command that removes an older interpreter through its OWN registered uninstaller, read from
    /// the Uninstall registry entry whose DisplayName names this exact Python release: never a hand-built
    /// guess, and never a delete of the install folder itself. <c>QuietUninstallString</c> is preferred
    /// (already the fully silent form the entry's own vendor chose); when only the interactive
    /// <c>UninstallString</c> exists, the python.org installer's own documented switches
    /// (<c>/uninstall /quiet</c>) are appended, matching how every python.org release since 3.5 supports
    /// being uninstalled unattended. Null when no matching entry exists at all (a portable/embeddable
    /// Python that was never really "installed" through Windows in the first place).
    /// </summary>
    public static string? FindUninstallCommand(IRegistryView registry, PythonInterpreter interpreter)
    {
        var roots = interpreter.PerUser
            ? new[] { @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall" }
            :
            [
                @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
            ];

        // A whole version token: "3.1" must not match "3.10", and "13.1" is not "3.1" either.
        var versionToken = new Regex(
            $@"(?<![\d.]){Regex.Escape($"{interpreter.Version.Major}.{interpreter.Version.Minor}")}(?!\d)",
            RegexOptions.CultureInvariant);
        var interpreterDirectory = NormalizeDirectory(Path.GetDirectoryName(interpreter.PythonExe));

        // An entry that names an install location is only ours when it is this interpreter's directory;
        // an entry without one is accepted as a fallback, since not every python.org bundle writes it.
        string? fallback = null;
        var fallbackAmbiguous = false;

        foreach (var root in roots)
        {
            foreach (var key in registry.SubKeyNames(root))
            {
                var keyPath = $@"{root}\{key}";
                var displayName = registry.Value(keyPath, "DisplayName");
                if (displayName is null
                    || !displayName.Contains("Python", StringComparison.OrdinalIgnoreCase)
                    || !versionToken.IsMatch(displayName)
                    // A conda distribution only mentions its bundled Python in the name; running its
                    // uninstaller would remove the whole distribution, not one interpreter.
                    || displayName.Contains("Anaconda", StringComparison.OrdinalIgnoreCase)
                    || displayName.Contains("Miniconda", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Bundles like python.org's list their parts (core, tcl/tk, docs...) as hidden component
                // entries named "Python 3.x ..." too, with no install location: they are not the uninstaller
                // and would make the fallback ambiguous, so only the visible entry is considered.
                if (registry.DwordValue(keyPath, "SystemComponent") == 1)
                    continue;

                var command = UninstallCommandOf(registry, keyPath);
                if (command is null)
                    continue;

                var location = NormalizeDirectory(registry.Value(keyPath, "InstallLocation"));
                if (location is null)
                {
                    // Without an install location the name is the only evidence, so it has to be the
                    // interpreter's own entry ("Python 3.11.5 (64-bit)"), not a product that contains Python.
                    // A 64-bit and a 32-bit install of the same major.minor both match by name, so the
                    // architecture the name states must fit, and two different candidates mean the name cannot
                    // tell them apart: no uninstaller at all is better than one for the wrong interpreter (it
                    // could be the 64-bit one this cleanup is meant to keep).
                    if (displayName.StartsWith("Python ", StringComparison.OrdinalIgnoreCase)
                        && !NamesOtherArchitecture(displayName, interpreter))
                    {
                        if (fallback is null)
                            fallback = command;
                        else if (!string.Equals(fallback, command, StringComparison.OrdinalIgnoreCase))
                            fallbackAmbiguous = true;
                    }
                }
                else if (interpreterDirectory is not null
                    && string.Equals(location, interpreterDirectory, StringComparison.OrdinalIgnoreCase))
                    return command;
            }
        }

        return fallbackAmbiguous ? null : fallback;
    }

    /// <summary>True when an uninstall entry's name states an architecture the interpreter does not have
    /// ("Python 3.11.5 (32-bit)" for a 64-bit interpreter). A name that states none says nothing.</summary>
    private static bool NamesOtherArchitecture(string displayName, PythonInterpreter interpreter) =>
        interpreter.Is64Bit
            ? displayName.Contains("32-bit", StringComparison.OrdinalIgnoreCase)
            : displayName.Contains("64-bit", StringComparison.OrdinalIgnoreCase)
              || displayName.Contains("ARM64", StringComparison.OrdinalIgnoreCase);

    private static string? UninstallCommandOf(IRegistryView registry, string keyPath)
    {
        var quiet = registry.Value(keyPath, "QuietUninstallString");
        if (!string.IsNullOrWhiteSpace(quiet))
            return quiet;

        var plain = registry.Value(keyPath, "UninstallString");
        return string.IsNullOrWhiteSpace(plain) ? null : $"{plain} /uninstall /quiet";
    }

    private static string? NormalizeDirectory(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : path.Trim().Trim('"').TrimEnd('\\', '/');

    /// <summary>
    /// Every installed interpreter whose major.minor is lower than the newest installed one of the SAME
    /// architecture (a 32-bit build is never compared against a 64-bit one; they serve different
    /// software and neither makes the other obsolete). The single newest interpreter of an architecture
    /// is never itself "older", so an architecture with only one interpreter contributes nothing here.
    /// </summary>
    public static IReadOnlyList<PythonInterpreter> FindOlderVersions(IReadOnlyList<PythonInterpreter> interpreters)
    {
        var older = new List<PythonInterpreter>();

        foreach (var group in interpreters.GroupBy(i => i.Is64Bit))
        {
            var newest = group.Max(i => i.Version);
            older.AddRange(group.Where(i => i.Version < newest));
        }

        return older;
    }
}
