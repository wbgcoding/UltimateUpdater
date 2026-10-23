using UltimateUpdater.Models;

namespace UltimateUpdater.Services.Sources;

/// <summary>One registry value, named by its full key path.</summary>
public readonly record struct RegistryValue(string KeyPath, string ValueName);

/// <summary>
/// Everything known about where one launcher can be. Each entry is a checkable fact about the real
/// product; the name its uninstall entry carries, the executable it installs, the key its vendor
/// writes; never a guess: a definition that matches nothing simply produces no row.
/// </summary>
public sealed class LauncherDefinition
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>Games unless a definition says otherwise; the dev-tool clients below are the exception.</summary>
    public UpdateCategory Category { get; init; } = UpdateCategory.Games;

    /// <summary>Executable file names, most likely first; also the keys of the App Paths lookup.</summary>
    public required IReadOnlyList<string> Executables { get; init; }

    /// <summary>Vendor keys holding the full path of the executable itself.</summary>
    public IReadOnlyList<RegistryValue> ExecutablePathValues { get; init; } = [];

    /// <summary>Vendor keys holding the install DIRECTORY; the executable names are appended.</summary>
    public IReadOnlyList<RegistryValue> InstallDirectoryValues { get; init; } = [];

    /// <summary>DisplayName of the product's uninstall entry, compared in full and case-insensitively.</summary>
    public IReadOnlyList<string> UninstallDisplayNames { get; init; } = [];

    /// <summary>Last resort: the vendor's default install locations.</summary>
    public IReadOnlyList<string> FallbackPaths { get; init; } = [];
}
