using System.IO;
using UltimateUpdater.Models;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Services.Install;

/// <summary>What blocked one update from installing cleanly, and what to do about it. <paramref name="SkipOnly"/>:
/// the program is never offered for closing; the row is skipped for this run and tried again on the next one.
/// <paramref name="ProcessNames"/> then holds one entry per running process, so the count can be shown.
/// <paramref name="OnlyInsideFolder"/>: set for a Store app; only processes whose image lies in that folder are
/// the program, so closing never reaches a same-named process from anywhere else.</summary>
public sealed record BlockedInstall(
    UpdateItem Item, string DisplayProgramName, IReadOnlyList<string> ProcessNames, bool SkipOnly = false,
    string? OnlyInsideFolder = null)
{
    /// <summary>The running processes as short text, e.g. "bash ×3, ssh".</summary>
    public string DescribeProcesses() =>
        string.Join(", ", ProcessNames
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Count() > 1 ? $"{group.Key} ×{group.Count()}" : group.Key));
}

/// <summary>
/// Pre-install check: the program an update would overwrite may still be running, exactly what
/// produces a Visual Studio 8006 ("processes running") failure. Two ways of finding it: a small
/// fixed table for known repeat offenders (matched by package id prefix), and, the general case, the
/// update's own resolved install directory (<see cref="InstallLocationResolver"/>), whose TOP-LEVEL *.exe
/// files (never recursive, same conservatism as everywhere else that class resolves an executable) are
/// checked against what is actually running. Process enumeration is injected so tests never depend on
/// whatever happens to be running on the machine that runs them.
/// </summary>
public sealed class InstallBlockers
{
    /// <summary>
    /// Never suggested, never closed, full stop, regardless of what matched. A SEPARATE, smaller list from
    /// <see cref="Security.CommandDenyList"/> (that one denies COMMANDS this app might launch; this one
    /// denies PROCESSES this app might ask the user to close), checked unconditionally on every path below,
    /// including the generic one, which cannot structurally produce one of these names, but the guard
    /// exists anyway as defense in depth and is tested as such.
    /// </summary>
    private static readonly HashSet<string> NeverClose = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "csrss", "winlogon", "wininit", "smss", "services", "lsass", "dwm", "svchost",
    };

    /// <summary>
    /// The same rule for whoever does the closing. Detection filters this list, but the caller that
    /// actually closes and kills re-resolves the names into live processes, so the guarantee would
    /// otherwise rest on a producer several layers away staying correct for ever. Names carry no ".exe",
    /// matching <c>Process.ProcessName</c>.
    /// </summary>
    public static bool IsNeverClosed(string processName) => NeverClose.Contains(processName);

    /// <summary>
    /// A known repeat offender: a package id prefix, its display name for the dialog, and the process name
    /// PREFIXES that block it (ServiceHub spawns many differently-named children, Microsoft.VisualStudio's
    /// own uninstall/repair tooling calls them all "ServiceHub.*", so a prefix match is required, not an
    /// exact one). Discord and Firefox are the two "1-2 more sensible ones" the same pattern implies: both
    /// are extremely common installs that refuse to update themselves while running.
    /// </summary>
    private sealed record KnownBlocker(string PackageIdPrefix, string DisplayProgramName, IReadOnlyList<string> ProcessNamePrefixes,
        bool ExactNames = false, bool OwnFolderOnly = false);

    /// <summary>Git's silent setup finds its own shells still running, asks to close them and, with message
    /// boxes suppressed, asks again for ever at full CPU while every other winget install waits behind it.
    /// Its names are short and common ("sh", "git"), so they match exactly, never as a prefix. Git's own ssh,
    /// scp and gpg stop the setup too (seen: an ssh.exe of a running git fetch); Windows' OpenSSH shares
    /// those names, so only a process whose image lives in Git's own install folder counts. Nothing is closed,
    /// since a terminal on Git Bash is open all day: the row is skipped until the next run.</summary>
    private static readonly IReadOnlyList<KnownBlocker> Table =
    [
        new("Microsoft.VisualStudio.", "Visual Studio", ["devenv", "VSIXInstaller", "ServiceHub"]),
        new("Corsair.", "Corsair iCUE", ["iCUE"]),
        new("Discord.", "Discord", ["Discord"]),
        new("Mozilla.Firefox", "Firefox", ["firefox"]),
        new("Git.Git", "Git", ["bash", "sh", "mintty", "git", "git-bash", "ssh", "scp", "sftp", "ssh-agent", "gpg", "gpg-agent"],
            ExactNames: true, OwnFolderOnly: true),
    ];

    private readonly Func<IReadOnlyList<string>> _runningProcessNames;
    private readonly InstallLocationResolver? _locationResolver;
    private readonly Func<string, IReadOnlyList<string>> _topLevelExecutableNames;
    private readonly Func<string, IReadOnlyList<string?>> _imagePathsOf;
    private readonly Func<string?> _gitInstallFolder;
    private readonly PackagedApps? _packagedApps;
    private readonly Func<IReadOnlyList<(string Name, string Path)>> _processImages;

    /// <param name="imagePathsOf">Process name (no ".exe") to the full image path of every running instance;
    /// null for an instance whose image cannot be read.</param>
    /// <param name="gitInstallFolder">Git's install folder, or null when it cannot be found.</param>
    /// <param name="packagedApps">The installed Store packages; without it no Store app is looked for.</param>
    /// <param name="processImages">Name (no ".exe") and full image path of every running process.</param>
    public InstallBlockers(
        InstallLocationResolver? locationResolver = null,
        Func<IReadOnlyList<string>>? runningProcessNames = null,
        Func<string, IReadOnlyList<string>>? topLevelExecutableNames = null,
        Func<string, IReadOnlyList<string?>>? imagePathsOf = null,
        Func<string?>? gitInstallFolder = null,
        PackagedApps? packagedApps = null,
        Func<IReadOnlyList<(string Name, string Path)>>? processImages = null)
    {
        _locationResolver = locationResolver;
        _runningProcessNames = runningProcessNames ?? DefaultRunningProcessNames;
        _topLevelExecutableNames = topLevelExecutableNames ?? TopLevelExecutableNames;
        _imagePathsOf = imagePathsOf ?? DefaultImagePathsOf;
        _gitInstallFolder = gitInstallFolder ?? FindGitInstallFolder;
        _packagedApps = packagedApps;
        _processImages = processImages ?? DefaultProcessImages;
    }

    /// <summary>Every running process with its image path, read the same way the Git own-folder check reads one
    /// (a kernel query with limited rights, so protected and elevated processes still answer or are skipped).
    /// This app's own process is left out: it is never the program in the way.</summary>
    private static IReadOnlyList<(string Name, string Path)> DefaultProcessImages()
    {
        var processes = Array.Empty<System.Diagnostics.Process>();
        try
        {
            processes = System.Diagnostics.Process.GetProcesses();
            var images = new List<(string, string)>(processes.Length);
            foreach (var process in processes)
            {
                try
                {
                    if (process.Id != Environment.ProcessId && ProcessImage.TryGetPath(process.Id) is { Length: > 0 } path)
                        images.Add((process.ProcessName, path));
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // gone while being read
                }
            }

            return images;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return [];
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    /// <summary>
    /// A running Store app holds its package files: winget registers the update and Windows applies it only
    /// after the app has closed. Every running process whose image lies inside the package's own folder
    /// counts (the app's executables live in a subfolder, which the top-level check never sees). The package
    /// is matched by the row's exact name, and only a winget row can be one.
    /// </summary>
    private BlockedInstall? DetectPackagedApp(UpdateItem item, HashSet<string> runningSet)
    {
        if (_packagedApps is null || item.SourceId != "winget")
            return null;

        // Every registration with this name, not only the newest: while an update waits for the app to
        // close, the running copy is the older folder.
        var packages = _packagedApps.FindAll(item.Name).ToList();
        if (packages.Count == 0)
            return null;

        var images = _processImages();
        foreach (var package in packages)
        {
            var names = new List<string>();
            foreach (var (name, path) in images)
            {
                try
                {
                    if (runningSet.Contains(name) && !NeverClose.Contains(name) && IsInside(path, package.Root))
                        names.Add(name);
                }
                catch (ArgumentException)
                {
                    // a malformed path cannot be inside anything
                }
            }

            if (names.Count > 0)
                return new BlockedInstall(item, item.Name, names, OnlyInsideFolder: package.Root);
        }

        return null;
    }

    private static IReadOnlyList<string?> DefaultImagePathsOf(string processName)
    {
        var processes = Array.Empty<System.Diagnostics.Process>();
        try
        {
            processes = System.Diagnostics.Process.GetProcessesByName(processName);
            var paths = new List<string?>(processes.Length);
            foreach (var process in processes)
            {
                try
                {
                    paths.Add(process.MainModule?.FileName is { Length: > 0 } path ? path : null);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                                               or NotSupportedException)
                {
                    // An elevated or protected process cannot be read: kept as an unknown image, which the
                    // generic rule treats as possibly in the way and the Git rule as not Git's.
                    paths.Add(null);
                }
            }

            return paths;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return [null];
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    /// <summary>Git for Windows' install folder: its own registry key first, then the two default places.</summary>
    internal static string? FindGitInstallFolder()
    {
        try
        {
            foreach (var hive in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
            {
                using var key = hive.OpenSubKey(@"SOFTWARE\GitForWindows");
                if (key?.GetValue("InstallPath") is string { Length: > 0 } path && Directory.Exists(path))
                    return path;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // fall through to the default places
        }

        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Git"),
        ];
        return candidates.FirstOrDefault(Directory.Exists);
    }

    /// <summary>True when <paramref name="path"/> lies inside <paramref name="folder"/>. The folder is matched
    /// with a trailing separator so a sibling like "Git2" is not inside "Git".</summary>
    internal static bool IsInside(string path, string folder)
    {
        var root = Path.GetFullPath(folder).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether a running program of this name can be one that sits in <paramref name="directory"/>. A
    /// same-named program from another folder (a Python 3.11 script while 3.12 updates) does not hold these
    /// files. Anything unclear counts as in the way: no instance found any more, an unreadable image or an
    /// unusable path, because a missed blocker fails the install while a needless one only asks.</summary>
    private bool MayHoldFilesIn(string processName, string directory)
    {
        var paths = _imagePathsOf(processName);
        if (paths.Count == 0)
            return true;

        foreach (var path in paths)
        {
            if (path is null)
                return true;

            try
            {
                if (IsInside(path, directory))
                    return true;
            }
            catch (ArgumentException)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>One entry per running instance of these names whose image lies in Git's install folder. A
    /// shell from anywhere else (Windows' own OpenSSH, another Git copy) does not hold Git's files. With no
    /// known install folder nothing counts, so an unfindable Git is installed rather than skipped for ever.</summary>
    private List<string> InOwnFolder(IReadOnlyList<string> names)
    {
        if (names.Count == 0 || _gitInstallFolder() is not { Length: > 0 } folder)
            return [];

        var inside = new List<string>();
        foreach (var name in names)
        {
            foreach (var path in _imagePathsOf(name))
            {
                if (path is null)
                    continue;

                try
                {
                    if (IsInside(path, folder))
                        inside.Add(name);
                }
                catch (ArgumentException)
                {
                    // a malformed path cannot be inside anything
                }
            }
        }

        return inside;
    }

    /// <summary>
    /// Every Process object <see cref="System.Diagnostics.Process.GetProcesses"/> hands back owns a native
    /// handle that only a GC finalizer would otherwise reclaim; on a batch of N queued installs, calling
    /// this once per row (the old <see cref="Detect(UpdateItem)"/> path) leaked N full snapshots' worth of
    /// undisposed handles. Every object is disposed here, right after its name is read, before the method
    /// returns.
    /// </summary>
    private static IReadOnlyList<string> DefaultRunningProcessNames()
    {
        var processes = Array.Empty<System.Diagnostics.Process>();
        try
        {
            processes = System.Diagnostics.Process.GetProcesses();
            return processes.Select(p => p.ProcessName).ToList();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Best effort, same fail-closed-to-"nothing found" contract as the rest of this app's process
            // probing, a failed enumeration must never look like "blocked", only like "nothing detected".
            return [];
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    /// <summary>
    /// One process-name snapshot for a whole install batch, rather than one per row: pass the result to
    /// <see cref="Detect(UpdateItem, IReadOnlyList{string})"/> for every row being checked. Public so a
    /// caller checking several rows (MainViewModel's own install queue) can take this ONCE per batch instead
    /// of paying for <see cref="System.Diagnostics.Process.GetProcesses"/>'s full system snapshot on every
    /// single row, which is what <see cref="Detect(UpdateItem)"/> still does for a caller not yet switched
    /// over to the batch-friendly overload.
    /// </summary>
    public static IReadOnlyList<string> CaptureRunningProcessNames() => DefaultRunningProcessNames();

    /// <summary>
    /// Same one-snapshot-per-batch idea as <see cref="CaptureRunningProcessNames"/>, but through this
    /// instance's own process-name source rather than always the real one: a caller that constructed this
    /// instance with a fake <c>runningProcessNames</c> function (every test that needs a specific process
    /// blocked or not, without depending on whatever genuinely happens to be running) gets that same fake
    /// snapshot here too, shared across the whole batch instead of re-read per row.
    /// </summary>
    public IReadOnlyList<string> CaptureBatchSnapshot() => _runningProcessNames();

    /// <summary>The blocker for this one item, or null when nothing running looks like it would conflict.
    /// Takes its own fresh process-name snapshot; a caller checking several rows in one batch should prefer
    /// <see cref="Detect(UpdateItem, IReadOnlyList{string})"/> with one shared
    /// <see cref="CaptureRunningProcessNames"/> snapshot instead of paying for a fresh one per row.</summary>
    public BlockedInstall? Detect(UpdateItem item) => Detect(item, _runningProcessNames());

    /// <summary>
    /// Same check as <see cref="Detect(UpdateItem)"/>, against an already-captured process-name snapshot
    /// (<see cref="CaptureRunningProcessNames"/>) instead of taking a fresh one per call - the batch-friendly
    /// path: one snapshot shared across every row in the batch instead of one full system enumeration each.
    /// </summary>
    public BlockedInstall? Detect(UpdateItem item, IReadOnlyList<string> runningProcessNames)
    {
        if (runningProcessNames.Count == 0)
            return null;

        var runningSet = new HashSet<string>(runningProcessNames, StringComparer.OrdinalIgnoreCase);

        foreach (var known in Table)
        {
            if (!item.Id.StartsWith(known.PackageIdPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            IReadOnlyList<string> matches = known.ExactNames
                ? known.ProcessNamePrefixes.Where(name => runningSet.Contains(name) && !NeverClose.Contains(name)).ToList()
                : MatchByPrefix(runningSet, known.ProcessNamePrefixes);
            if (known.OwnFolderOnly)
            {
                matches = InOwnFolder(matches);
                if (matches.Count > 0)
                    return new BlockedInstall(item, known.DisplayProgramName, matches, SkipOnly: true);
                continue;
            }

            if (matches.Count > 0)
                return new BlockedInstall(item, known.DisplayProgramName, matches);
        }

        if (DetectPackagedApp(item, runningSet) is { } packaged)
            return packaged;

        if (_locationResolver?.Resolve(item) is { } directory)
        {
            var exeNames = _topLevelExecutableNames(directory);
            var matches = exeNames
                .Where(name => runningSet.Contains(name) && !NeverClose.Contains(name) && MayHoldFilesIn(name, directory))
                .ToList();
            if (matches.Count > 0)
                return new BlockedInstall(item, item.Name, matches);
        }

        return null;
    }

    /// <summary>
    /// Folds rows that were each detected as blocked individually into ONE entry per distinct blocking
    /// program, so a caller asks the user once per program instead of once per row (two rows waiting on the
    /// same running program, e.g. two Visual Studio components both held up by devenv, are one real
    /// decision, not two). The key is the matched process names (case insensitive, order independent) plus
    /// the display name shown in the dialog headline, so two rows only fold together when they name the
    /// exact same running program. <typeparamref name="TRow"/> is left generic on purpose: this stays a
    /// pure function over whatever the caller is grouping, with no dependency on the view models.
    /// </summary>
    public static IReadOnlyList<(BlockedInstall Blocker, IReadOnlyList<TRow> Rows)> GroupBlockedRows<TRow>(
        IEnumerable<(TRow Row, BlockedInstall Blocker)> blockedRows)
    {
        var groups = new List<(string Key, BlockedInstall Blocker, List<TRow> Rows)>();

        foreach (var (row, blocker) in blockedRows)
        {
            var key = GroupKey(blocker);
            var index = groups.FindIndex(g => g.Key == key);
            if (index < 0)
                groups.Add((key, blocker, [row]));
            else
                groups[index].Rows.Add(row);
        }

        return groups.Select(g => (g.Blocker, (IReadOnlyList<TRow>)g.Rows)).ToList();
    }

    private static string GroupKey(BlockedInstall blocker) =>
        string.Join('|', blocker.ProcessNames.Select(n => n.ToLowerInvariant()).OrderBy(n => n, StringComparer.Ordinal))
        + "::" + blocker.DisplayProgramName + "::" + blocker.OnlyInsideFolder;

    private static IReadOnlyList<string> MatchByPrefix(HashSet<string> runningSet, IReadOnlyList<string> prefixes) =>
        runningSet
            .Where(name => !NeverClose.Contains(name)
                           && prefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .ToList();

    /// <summary>
    /// The process names (no ".exe", matching <c>Process.ProcessName</c>'s own convention) of the TOP-LEVEL
    /// executables in an install directory, deliberately narrower than
    /// <c>InstallLocationResolver.DefaultExecutablesIn</c> (which also checks a bin/current subfolder for
    /// resolving a launch target): a deep or wide directory scan on every queued install would cost far
    /// more than this check is worth, so it stays top-level and capped.
    /// </summary>
    internal const int MaxTopLevelExecutables = 256;

    internal static IReadOnlyList<string> TopLevelExecutableNames(string directory)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        try
        {
            return Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, "*.exe", options)
                    .Select(Path.GetFileNameWithoutExtension)
                    .Where(name => !string.IsNullOrEmpty(name))
                    .Select(name => name!)
                    .Take(MaxTopLevelExecutables)
                    .ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }
}
