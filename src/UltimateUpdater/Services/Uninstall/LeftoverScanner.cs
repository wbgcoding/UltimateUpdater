using System.IO;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.Services.Platform;
using UltimateUpdater.Services.Sources.Windows;

namespace UltimateUpdater.Services.Uninstall;

/// <summary>
/// Finds what a removed program left behind: its install folder, folders in the user profile and the machine
/// folders named like it, its Software registry key and Start menu or desktop shortcuts whose target is gone.
/// Reading only: it offers, <see cref="LeftoverRemover"/> removes. The match is by EXACT normalised name
/// (never a substring), nothing a still-installed program claims is ever offered, and only a certain match
/// (the install folder, a publisher plus program folder or key, a shortcut into the install folder) comes
/// preselected.
/// </summary>
internal sealed class LeftoverScanner
{
    private const int MaxShortcutFiles = 3000;
    private const int MaxShortcutDepth = 4;
    private const int MeasureDepth = 8;
    private const int MeasureFiles = 20000;

    private static readonly (string Root, LeftoverLane Lane)[] RegistryRoots =
    [
        (@"HKCU\Software", LeftoverLane.User),
        (@"HKLM\Software", LeftoverLane.Machine),
        (@"HKLM\Software\WOW6432Node", LeftoverLane.Machine),
    ];

    private readonly LeftoverRoots _roots;
    private readonly IRegistryView _registry;
    private readonly Func<string, bool> _exists;
    private readonly Func<IReadOnlyList<string>, IReadOnlyDictionary<string, string?>> _readTargets;

    /// <param name="exists">True when the path exists (or cannot be told); the default treats an unplugged
    /// drive as existing.</param>
    /// <param name="readTargets">The target of each .lnk (null when it has none); the default reads them
    /// through the shell on one apartment thread.</param>
    internal LeftoverScanner(
        LeftoverRoots roots, IRegistryView registry, Func<string, bool>? exists = null,
        Func<IReadOnlyList<string>, IReadOnlyDictionary<string, string?>>? readTargets = null)
    {
        _roots = roots;
        _registry = registry;
        _exists = exists ?? RegistryCleanupScanner.ExistsInAnyView;
        _readTargets = readTargets ?? ReadShortcutTargets;
    }

    /// <summary>The scanner over the real machine.</summary>
    internal static LeftoverScanner ForThisMachine() => new(LeftoverRoots.Current(), WindowsRegistryView.Instance);

    /// <summary>
    /// Everything found for <paramref name="subject"/>. <paramref name="claims"/> must hold every program
    /// that is still installed; <paramref name="onlyLane"/> limits the answer to one lane (the elevated worker
    /// asks for the machine lane only).
    /// </summary>
    internal IReadOnlyList<LeftoverItem> Scan(
        LeftoverSubject subject, LeftoverClaims claims, LeftoverLane? onlyLane, CancellationToken cancellationToken)
    {
        // A Store app keeps its data in the package folders, which are not a leftover to guess at.
        if (subject.Key.StartsWith("appx:", StringComparison.OrdinalIgnoreCase))
            return [];

        var name = LeftoverNames.Normalize(subject.Name);
        var nameUsable = LeftoverNames.IsUsable(name);

        // The same program is still registered (another view, another version): nothing of it is left behind.
        if (nameUsable && claims.HasName(name))
            return [];

        var publisherForms = LeftoverNames.PublisherForms(subject.Publisher);
        var items = new Dictionary<string, LeftoverItem>(StringComparer.OrdinalIgnoreCase);

        void Add(LeftoverItem item)
        {
            if (onlyLane is { } lane && item.Lane != lane)
                return;

            var key = $"{item.Kind}|{item.Path}";
            if (items.TryGetValue(key, out var known))
                item = item with { Preselected = known.Preselected || item.Preselected };

            items[key] = item;
        }

        var installFolder = LeftoverRoots.Full(subject.InstallLocation);
        if (installFolder is not null)
            AddInstallFolder(subject, installFolder, name, claims, Add, cancellationToken);

        if (nameUsable)
        {
            AddNamedFolders(name, publisherForms, claims, Add, cancellationToken);
            if (publisherForms.Count > 0)
                AddRegistryKeys(name, publisherForms, Add, cancellationToken);
        }

        AddShortcuts(name, nameUsable, installFolder, claims, Add, cancellationToken);

        return [.. items.Values
            .OrderByDescending(i => i.Preselected)
            .ThenBy(i => i.Lane)
            .ThenBy(i => i.Kind)
            .ThenBy(i => i.Path, StringComparer.OrdinalIgnoreCase)];
    }

    private void AddInstallFolder(
        LeftoverSubject subject, string installFolder, string name, LeftoverClaims claims,
        Action<LeftoverItem> add, CancellationToken cancellationToken)
    {
        if (!AcceptFolder(installFolder, claims))
            return;

        // Certain only where an install folder normally is: directly or one level (publisher\program)
        // below a program root, or named like the program. Any other place the registry names (a whole games
        // drive folder) is shown but never ticked.
        var certain = _roots.InstallRoots.Any(root => LeftoverRoots.DepthBelow(installFolder, root) is 1 or 2)
            || (LeftoverNames.IsUsable(name)
                && string.Equals(LeftoverNames.Normalize(Path.GetFileName(installFolder)), name, StringComparison.Ordinal));

        add(FolderItem(installFolder, certain, cancellationToken));
    }

    private void AddNamedFolders(
        string name, IReadOnlyList<string> publisherForms, LeftoverClaims claims, Action<LeftoverItem> add,
        CancellationToken cancellationToken)
    {
        foreach (var root in _roots.InstallRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(root))
                continue;

            foreach (var child in SafeEnumerate.Directories(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var childName = LeftoverNames.Normalize(Path.GetFileName(child));

                if (publisherForms.Contains(childName, StringComparer.Ordinal))
                {
                    // <publisher>\<program>: only the program folder, never the publisher folder that other
                    // programs of the publisher share. A program named exactly like its publisher cannot be
                    // told from the publisher folder, so a folder of that name is left out.
                    if (childName == name)
                        continue;

                    foreach (var sub in SafeEnumerate.Directories(child))
                    {
                        if (string.Equals(LeftoverNames.Normalize(Path.GetFileName(sub)), name, StringComparison.Ordinal)
                            && AcceptFolder(sub, claims))
                        {
                            add(FolderItem(sub, preselected: true, cancellationToken));
                        }
                    }
                }
                else if (childName == name && AcceptFolder(child, claims))
                {
                    // Named like the program but not tied to its publisher: listed, never ticked.
                    add(FolderItem(child, preselected: false, cancellationToken));
                }
            }
        }
    }

    private bool AcceptFolder(string folder, LeftoverClaims claims) =>
        LeftoverTargetSafety.IsSafeFolder(folder, _roots, out _) && !claims.ClaimsFolder(folder, _roots);

    private LeftoverItem FolderItem(string folder, bool preselected, CancellationToken cancellationToken) =>
        new(LeftoverKind.Folder, _roots.LaneOfFolder(folder), folder, Path.GetFileName(folder), preselected,
            DirectorySizeWalker.Measure(folder, MeasureDepth, MeasureFiles, cancellationToken));

    private void AddRegistryKeys(
        string name, IReadOnlyList<string> publisherForms, Action<LeftoverItem> add, CancellationToken cancellationToken)
    {
        foreach (var (root, lane) in RegistryRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var publisherKey in _registry.SubKeyNames(root))
            {
                if (!publisherForms.Contains(LeftoverNames.Normalize(publisherKey), StringComparer.Ordinal))
                    continue;

                var publisherPath = $@"{root}\{publisherKey}";
                foreach (var programKey in _registry.SubKeyNames(publisherPath))
                {
                    if (!string.Equals(LeftoverNames.Normalize(programKey), name, StringComparison.Ordinal))
                        continue;

                    var keyPath = $@"{publisherPath}\{programKey}";
                    if (LeftoverTargetSafety.IsSafeRegistryKeyPath(keyPath, out _))
                        add(new LeftoverItem(LeftoverKind.RegistryKey, lane, keyPath, $"{publisherKey}\\{programKey}", true, 0));
                }
            }
        }
    }

    private void AddShortcuts(
        string name, bool nameUsable, string? installFolder, LeftoverClaims claims, Action<LeftoverItem> add,
        CancellationToken cancellationToken)
    {
        // A shortcut into the install folder is judged only when that folder is a believable one, never a
        // root or a user folder.
        var installUsable = installFolder is not null
            && !_roots.ProtectedFolders.Any(p => LeftoverRoots.SamePath(p, installFolder))
            && !_roots.InstallRoots.Any(p => LeftoverRoots.SamePath(p, installFolder))
            && Path.GetPathRoot(installFolder) is { } driveRoot
            && !string.Equals(driveRoot, installFolder, StringComparison.OrdinalIgnoreCase);

        if (!nameUsable && !installUsable)
            return;

        var found = new List<(string File, string Root, LeftoverLane Lane)>();
        foreach (var (folders, lane) in new[]
                 {
                     (_roots.UserShortcutFolders, LeftoverLane.User),
                     (_roots.MachineShortcutFolders, LeftoverLane.Machine),
                 })
        {
            foreach (var root in folders)
            {
                foreach (var file in ShortcutFiles(root, cancellationToken))
                    found.Add((file, root, lane));
            }
        }

        if (found.Count == 0)
            return;

        var targets = _readTargets([.. found.Select(f => f.File)]);
        foreach (var (file, root, lane) in found)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!targets.TryGetValue(file, out var rawTarget) || !IsLocalTarget(rawTarget, out var target))
                continue;

            var byName = nameUsable && NamedLikeProgram(file, root, name);
            var intoInstall = installUsable && LeftoverRoots.IsBelow(target, installFolder!);
            if (!byName && !intoInstall)
                continue;

            // Gone is proven, not assumed: an unplugged drive or a file that cannot be asked about counts as
            // present, and a target a still-installed program owns is never a leftover.
            if (_exists(target) || claims.ClaimsFile(target, _roots))
                continue;

            if (!LeftoverTargetSafety.IsSafeShortcut(file, _roots, out _))
                continue;

            add(new LeftoverItem(LeftoverKind.Shortcut, lane, file, Path.GetFileNameWithoutExtension(file), intoInstall, 0));
        }
    }

    private static bool NamedLikeProgram(string file, string root, string name)
    {
        if (string.Equals(LeftoverNames.Normalize(Path.GetFileNameWithoutExtension(file)), name, StringComparison.Ordinal))
            return true;

        // A shortcut in a Start menu folder named like the program.
        var folder = Path.GetDirectoryName(file);
        while (folder is not null && LeftoverRoots.IsBelow(folder, root))
        {
            if (string.Equals(LeftoverNames.Normalize(Path.GetFileName(folder)), name, StringComparison.Ordinal))
                return true;

            folder = Path.GetDirectoryName(folder);
        }

        return false;
    }

    private static bool IsLocalTarget(string? raw, out string target)
    {
        target = string.Empty;

        // No path at all (a Store app, a shell item), a relative one or a network path: unknown, never "gone".
        var text = raw?.Trim().Trim('"');
        if (text is not { Length: >= 3 } || !char.IsAsciiLetter(text[0]) || text[1] != ':' || text[2] != '\\')
            return false;

        if (LeftoverRoots.Full(text) is not { } full)
            return false;

        target = full;
        return true;
    }

    private static IEnumerable<string> ShortcutFiles(string root, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root))
            yield break;

        var count = 0;
        var pending = new Stack<(string Folder, int Depth)>();
        pending.Push((root, 0));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (folder, depth) = pending.Pop();

            foreach (var file in SafeEnumerate.Files(folder))
            {
                if (!file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (++count > MaxShortcutFiles)
                    yield break;

                yield return file;
            }

            if (depth >= MaxShortcutDepth)
                continue;

            foreach (var sub in SafeEnumerate.Directories(folder))
                pending.Push((sub, depth + 1));
        }
    }

    /// <summary>The target of each shortcut, read on one apartment thread (the shell link object needs one).</summary>
    internal static IReadOnlyDictionary<string, string?> ReadShortcutTargets(IReadOnlyList<string> files)
    {
        var targets = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var thread = new Thread(() =>
        {
            foreach (var file in files)
                targets[file] = StartMenuShortcuts.Read(file)?.Target;
        })
        {
            IsBackground = true,
            Name = "LeftoverShortcutRead",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return targets;
    }
}
