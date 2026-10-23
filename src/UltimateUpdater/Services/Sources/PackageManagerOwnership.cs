using System.IO;

namespace UltimateUpdater.Services.Sources;

/// <summary>
/// Answers one question: is this executable already looked after by a package manager this app also scans?
/// It exists because of a duplicate found in practice: a tool that can update ITSELF is only worth a
/// row of its own when nothing else is going to update it. Git for Windows is the clear case: it registers
/// an uninstall entry, so `winget upgrade` offers it, and a second "git can update itself" row would be the
/// same update twice. The same is true package-manager-wide: a `deno.exe` living inside winget's own package
/// store belongs to winget, and winget's row is the one that should be shown.
/// The test is the PATH the executable resolves from, not a lookup: the managers keep their payloads in
/// their own directories, so this needs no process, no network and no cache. A tool installed by its own
/// script into the user's profile matches nothing here and keeps its self-update row, which is exactly the
/// case that row is for.
/// </summary>
public static class PackageManagerOwnership
{
    /// <summary>
    /// True when <paramref name="executablePath"/> sits inside a package manager's own store or shim
    /// directory. Unknown or unreadable paths answer false: refusing to show a row is worse than showing
    /// one the user can ignore.
    /// </summary>
    public static bool IsManagedElsewhere(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return false;

        string full;
        try
        {
            full = Path.GetFullPath(executablePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return false;
        }

        return ManagedRoots().Any(root => IsUnder(root, full));
    }

    /// <summary>Convenience for a source that only knows the command name: resolves it the way we run it.</summary>
    public static bool IsCommandManagedElsewhere(string fileName) =>
        IsManagedElsewhere(ProcessRunner.Resolve(fileName));

    /// <summary>
    /// Where the managers this app already scans keep what they install. Shim directories count as well as
    /// package stores: winget's Links folder holds a stub per portable package, and that stub is what PATH
    /// resolution actually finds.
    /// </summary>
    private static IEnumerable<string> ManagedRoots()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        var roots = new List<string?>
        {
            Path.Combine(localAppData, "Microsoft", "WinGet", "Packages"),
            Path.Combine(localAppData, "Microsoft", "WinGet", "Links"),
        };

        // Scoop is relocatable through its own environment variables, so those come first.
        var scoop = Environment.GetEnvironmentVariable("SCOOP");
        roots.Add(Path.Combine(string.IsNullOrWhiteSpace(scoop) ? Path.Combine(userProfile, "scoop") : scoop, "apps"));
        roots.Add(Path.Combine(string.IsNullOrWhiteSpace(scoop) ? Path.Combine(userProfile, "scoop") : scoop, "shims"));

        var scoopGlobal = Environment.GetEnvironmentVariable("SCOOP_GLOBAL");
        if (!string.IsNullOrWhiteSpace(scoopGlobal))
        {
            roots.Add(Path.Combine(scoopGlobal, "apps"));
            roots.Add(Path.Combine(scoopGlobal, "shims"));
        }
        else
        {
            roots.Add(Path.Combine(programData, "scoop", "apps"));
            roots.Add(Path.Combine(programData, "scoop", "shims"));
        }

        var chocolatey = Environment.GetEnvironmentVariable("ChocolateyInstall");
        var chocolateyRoot = string.IsNullOrWhiteSpace(chocolatey) ? Path.Combine(programData, "chocolatey") : chocolatey;
        roots.Add(Path.Combine(chocolateyRoot, "lib"));
        roots.Add(Path.Combine(chocolateyRoot, "bin"));

        return roots.Where(root => !string.IsNullOrWhiteSpace(root)).Select(root => root!);
    }

    /// <summary>
    /// Directory containment, with the separator appended so a root is never a mere name prefix of a
    /// sibling ("…\scoop\appsomething" is not inside "…\scoop\apps").
    /// </summary>
    private static bool IsUnder(string root, string candidate)
    {
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                     + Path.DirectorySeparatorChar;

        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
