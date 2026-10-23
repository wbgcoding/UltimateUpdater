using System.IO;

namespace UltimateUpdater.Services.Sources;

/// <summary>
/// Finds an installed launcher. Hardcoded paths under Program Files were only ever the vendors'
/// defaults; Riot installs to the drive root, and EA, Ubisoft or Battle.net are routinely put on a
/// second drive, so the registry is asked first and those defaults are the last candidate.
/// </summary>
public static class LauncherResolver
{
    // Addressed by path rather than by RegistryView: the app is 64-bit, so the 32-bit hive is reached
    // through WOW6432Node, and most launchers still install as 32-bit. Per-user installs (Amazon
    // Games, itch, Roblox) register under HKCU and are invisible in the machine hive.

    private static readonly string[] AppPathsRoots =
    [
        @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths",
        @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths",
        @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths",
    ];

    /// <summary>
    /// The first candidate that is really on disk, or null. <paramref name="fileExists"/> is a
    /// parameter so the rules can be tested without a launcher installed.
    /// </summary>
    public static string? Resolve(LauncherDefinition definition, IRegistryView registry, Func<string, bool> fileExists)
    {
        foreach (var candidate in Candidates(definition, registry))
        {
            if (candidate.Length > 0 && fileExists(candidate))
                return candidate;
        }

        return null;
    }

    private static IEnumerable<string> Candidates(LauncherDefinition definition, IRegistryView registry)
    {
        foreach (var value in definition.ExecutablePathValues)
        {
            if (registry.Value(value.KeyPath, value.ValueName) is { } path)
                yield return CleanPath(path);
        }

        foreach (var value in definition.InstallDirectoryValues)
        {
            if (registry.Value(value.KeyPath, value.ValueName) is { } directory)
            {
                foreach (var candidate in Combine(CleanPath(directory), definition.Executables))
                    yield return candidate;
            }
        }

        // App Paths: the key is named after the executable and its default value is the full path
        foreach (var root in AppPathsRoots)
        {
            foreach (var executable in definition.Executables)
            {
                if (registry.Value($@"{root}\{executable}", string.Empty) is { } path)
                    yield return CleanPath(path);
            }
        }

        foreach (var candidate in FromUninstallEntries(definition, registry))
            yield return candidate;

        foreach (var path in definition.FallbackPaths)
            yield return path;
    }

    /// <summary>
    /// The uninstall registry is the one place every Windows installer has to register itself, which
    /// makes it the only general answer to "where did this end up". Entries are matched on their
    /// DisplayName, because the key name is a product code on some products and a readable name on
    /// others, and matched in full, so a stray "Battle.net Games" entry cannot stand in for the client.
    /// </summary>
    private static IEnumerable<string> FromUninstallEntries(LauncherDefinition definition, IRegistryView registry)
    {
        if (definition.UninstallDisplayNames.Count == 0)
            yield break;

        foreach (var root in WindowsRegistryRoots.Uninstall)
        {
            foreach (var subKey in registry.SubKeyNames(root))
            {
                var entry = $@"{root}\{subKey}";
                var displayName = registry.Value(entry, "DisplayName");

                if (displayName is null ||
                    !definition.UninstallDisplayNames.Contains(displayName.Trim(), StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                // DisplayIcon is the client executable itself on nearly every launcher, with an
                // optional ",<index>" icon index appended.
                if (registry.Value(entry, "DisplayIcon") is { } icon)
                {
                    var path = CleanPath(StripIconIndex(icon));
                    if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        yield return path;
                }

                if (registry.Value(entry, "InstallLocation") is { } location)
                {
                    foreach (var candidate in Combine(CleanPath(location), definition.Executables))
                        yield return candidate;
                }
            }
        }
    }

    private static IEnumerable<string> Combine(string directory, IReadOnlyList<string> executables)
    {
        if (directory.Length == 0)
            return [];

        return executables.Select(executable => Path.Combine(directory, executable));
    }

    private static string CleanPath(string value) => UninstallEntryPaths.Clean(value);

    private static string StripIconIndex(string value) => UninstallEntryPaths.StripIconIndex(value);
}
