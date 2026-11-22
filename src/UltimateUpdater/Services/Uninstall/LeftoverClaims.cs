using System.IO;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.Services.Platform;
using UltimateUpdater.Services.Sources.Windows;

namespace UltimateUpdater.Services.Uninstall;

/// <summary>
/// Everything a still-installed program says is its own: display names, publishers and folders. A leftover
/// scan offers nothing that any of this claims, so a shared publisher folder, a second install of the same
/// program or the folder of a neighbour never shows up as "left behind". More claims can only mean fewer
/// offers, so every source here is read generously: every Uninstall key counts, also system components and
/// entries with no uninstall command.
/// </summary>
internal sealed class LeftoverClaims
{
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);
    private readonly HashSet<string> _publishers = new(StringComparer.Ordinal);
    private readonly List<string> _paths = [];

    /// <summary>Caps for what a caller may add; the worker takes claims over a pipe.</summary>
    internal const int MaxClaims = 20000;

    internal int PathCount => _paths.Count;

    /// <summary>Every Uninstall key of all four roots except <paramref name="excludeKey"/> (the removed
    /// program itself, should its key still be there).</summary>
    internal static LeftoverClaims FromRegistry(IRegistryView registry, string? excludeKey = null)
    {
        var claims = new LeftoverClaims();
        foreach (var root in WindowsRegistryRoots.Uninstall)
        {
            foreach (var name in registry.SubKeyNames(root))
            {
                var key = $@"{root}\{name}";
                if (excludeKey is not null && string.Equals(key, excludeKey, StringComparison.OrdinalIgnoreCase))
                    continue;

                claims.AddName(registry.Value(key, "DisplayName"));
                claims.AddPublisher(registry.Value(key, "Publisher"));
                claims.AddPath(registry.Value(key, "InstallLocation"));

                foreach (var raw in new[] { registry.Value(key, "DisplayIcon"), registry.Value(key, "UninstallString") })
                {
                    if (RegistryCleanupSafety.ExtractFilePath(raw, RegistryCleanupScanner.ExistsInAnyView) is { Length: > 0 } file)
                        claims.AddPath(SafeDirectoryName(file));
                }
            }
        }

        return claims;
    }

    /// <summary>The inventory's entries (Store apps and winget programs included) on top.</summary>
    internal LeftoverClaims With(IEnumerable<InstalledProgramEntry> entries, string? excludeKey)
    {
        foreach (var entry in entries)
        {
            if (excludeKey is not null && string.Equals(entry.Key, excludeKey, StringComparison.OrdinalIgnoreCase))
                continue;

            AddName(entry.Name);
            AddPublisher(entry.Publisher);
            AddPath(entry.InstallLocation);
        }

        return this;
    }

    /// <summary>Claims carried as plain text (over the worker pipe). Normalised again here, bounded.</summary>
    internal LeftoverClaims WithText(IEnumerable<string>? names, IEnumerable<string>? publishers, IEnumerable<string>? paths)
    {
        foreach (var name in (names ?? []).Take(MaxClaims))
            AddName(name);

        foreach (var publisher in (publishers ?? []).Take(MaxClaims))
            AddPublisher(publisher);

        foreach (var path in (paths ?? []).Take(MaxClaims))
            AddPath(path);

        return this;
    }

    internal IReadOnlyList<string> Names => [.. _names];

    internal IReadOnlyList<string> Publishers => [.. _publishers];

    internal IReadOnlyList<string> Paths => _paths;

    internal bool HasName(string normalized) => _names.Contains(normalized);

    private void AddName(string? name)
    {
        if (LeftoverNames.Normalize(name) is { Length: > 0 } normalized)
            _names.Add(normalized);
    }

    private void AddPublisher(string? publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher))
            return;

        _publishers.Add(LeftoverNames.Normalize(publisher));
        foreach (var form in LeftoverNames.PublisherForms(publisher))
            _publishers.Add(form);
    }

    private void AddPath(string? path)
    {
        if (LeftoverRoots.Full(path) is { } full)
            _paths.Add(full);
    }

    private static string? SafeDirectoryName(string file)
    {
        try
        {
            return Path.GetDirectoryName(file);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when a still-installed program owns this folder: it is a claimed folder, holds one, or lies inside
    /// one; or its name is a still-installed program's name or publisher. Claims that are only a scanned root
    /// or a protected folder (many entries state "C:\Program Files" as their location) say nothing and are
    /// left out of the path test.
    /// </summary>
    internal bool ClaimsFolder(string fullPath, LeftoverRoots roots)
    {
        var leaf = LeftoverNames.Normalize(Path.GetFileName(fullPath));
        if (leaf.Length > 0 && (_names.Contains(leaf) || _publishers.Contains(leaf)))
            return true;

        foreach (var claimed in _paths)
        {
            if (IsRootLike(claimed, roots))
                continue;

            if (string.Equals(claimed, fullPath, StringComparison.OrdinalIgnoreCase)
                || LeftoverRoots.IsBelow(claimed, fullPath)
                || LeftoverRoots.IsBelow(fullPath, claimed))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when a still-installed program owns the folder this file lies in or the file itself.</summary>
    internal bool ClaimsFile(string fullPath, LeftoverRoots roots)
    {
        foreach (var claimed in _paths)
        {
            if (IsRootLike(claimed, roots))
                continue;

            if (string.Equals(claimed, fullPath, StringComparison.OrdinalIgnoreCase)
                || LeftoverRoots.IsBelow(fullPath, claimed))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsRootLike(string claimed, LeftoverRoots roots) =>
        string.Equals(claimed, Path.GetPathRoot(claimed), StringComparison.OrdinalIgnoreCase)
        || roots.InstallRoots.Concat(roots.ProtectedFolders).Any(root => LeftoverRoots.SamePath(claimed, root));
}
