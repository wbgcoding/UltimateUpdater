using System.IO;
using System.Security.AccessControl;
using Microsoft.Win32;

namespace UltimateUpdater.Services.Cleanup;

/// <summary>
/// The actual registry I/O behind <see cref="RegistryCleanupCategory"/>: enumerate, judge (via
/// <see cref="RegistryCleanupSafety"/>, which is what carries the unit tests), delete. This class is the
/// impure edge (same role as <c>CleanupService.TryWriteSagesetProfile</c>) so it is exercised through a
/// live run rather than a unit test; nothing in here decides "broken" on its own, it only reads what
/// <see cref="RegistryCleanupSafety"/> already judged pure.
/// </summary>
public static class RegistryCleanupScanner
{
    /// <summary>
    /// HKCU as well as HKLM, everywhere a per-user copy of the key exists. Reading only the machine hive
    /// would miss a program installed for the current user alone, while
    /// <see cref="Sources.LauncherResolver"/> reads its App Paths from HKCU, so the two would disagree about
    /// what is there (the "Anwendungspfade" and "ActiveX/COM" categories came back empty in practice).
    /// </summary>
    private static readonly string[] AppPathsRoots =
    [
        @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths",
        @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths",
        @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths",
        @"HKCU\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths",
    ];

    private const string SharedDllsKey = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\SharedDLLs";

    /// <summary>Every place a packed MSI product code is registered when the product is genuinely
    /// installed: per-machine (native and the WOW6432Node view for a 32-bit product) and per-user.</summary>
    private static readonly string[] MsiProductRoots =
    [
        @"HKLM\SOFTWARE\Classes\Installer\Products",
        @"HKLM\SOFTWARE\WOW6432Node\Classes\Installer\Products",
        @"HKCU\SOFTWARE\Microsoft\Installer\Products",
    ];

    private static readonly string[] ClsidRoots =
    [
        @"HKLM\SOFTWARE\Classes\CLSID",
        @"HKLM\SOFTWARE\WOW6432Node\Classes\CLSID",
        @"HKCU\SOFTWARE\Classes\CLSID",
        @"HKCU\SOFTWARE\Classes\WOW6432Node\CLSID",
    ];

    private static readonly string[] AutostartRoots =
    [
        @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
        @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run",
        @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce",
        @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce",
        @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
        @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce",
    ];


    /// <summary>Every root key a category's scan/delete could touch; exactly what gets backed up first.</summary>
    public static IReadOnlyList<string> RootKeys(RegistryCleanupCategory category) => category switch
    {
        RegistryCleanupCategory.AppPaths => AppPathsRoots,
        RegistryCleanupCategory.SharedDlls => [SharedDllsKey],
        RegistryCleanupCategory.ActiveXCom => ClsidRoots,
        RegistryCleanupCategory.Autostart => AutostartRoots,
        RegistryCleanupCategory.UninstallEntries => WindowsRegistryRoots.Uninstall,
        _ => [],
    };

    /// <summary>
    /// Whether a key belongs to the category being cleaned right now. A finding carries its own category,
    /// but that says nothing about the target currently doing the deleting: the elevated worker gives every
    /// selected target the same flat finding list, so the key path itself is measured against the roots of
    /// the target at hand. A root is matched whole; <see cref="RootKeys"/> holds both keys that carry the
    /// findings as SUBKEYS and keys that are themselves the finding's key path (SharedDLLs, Run).
    /// The root-equals-keyPath branch is legitimate ONLY for the two categories whose real findings live as
    /// VALUES directly inside the root key (SharedDlls, Autostart/Run); for every other category a real
    /// finding is a SUBKEY, and a bare-root KeyPath would mean "delete this whole root key"
    /// (<c>ValueName</c> null). So <paramref name="valueName"/> is part of the judgment itself: the root
    /// validates as "under roots" only together with a genuine, non-empty value name, and the
    /// "delete the whole key" shape is refused here at the source, whatever produced it.
    /// </summary>
    public static bool IsUnderRoots(RegistryCleanupCategory category, string keyPath, string? valueName)
    {
        if (string.IsNullOrWhiteSpace(keyPath))
            return false;

        var rootItselfCanBeAFinding = category is RegistryCleanupCategory.SharedDlls or RegistryCleanupCategory.Autostart
            && !string.IsNullOrEmpty(valueName);

        return RootKeys(category).Any(root =>
            keyPath.StartsWith(root + '\\', StringComparison.OrdinalIgnoreCase)
            || (rootItselfCanBeAFinding && keyPath.Equals(root, StringComparison.OrdinalIgnoreCase)));
    }

    public static IReadOnlyList<RegistryFinding> Scan(
        RegistryCleanupCategory category, CancellationToken cancellationToken, LoggingService? logging = null)
    {
        var findings = category switch
        {
            RegistryCleanupCategory.AppPaths => ScanAppPaths(cancellationToken),
            RegistryCleanupCategory.SharedDlls => ScanSharedDlls(cancellationToken),
            RegistryCleanupCategory.ActiveXCom => ScanActiveXCom(cancellationToken),
            RegistryCleanupCategory.Autostart => ScanAutostart(cancellationToken),
            RegistryCleanupCategory.UninstallEntries => ScanUninstallEntries(cancellationToken),
            _ => (List<RegistryFinding>)[],
        };

        // A TrustedInstaller-owned key (several stock HKLM\SOFTWARE\WOW6432Node\Classes\CLSID
        // entries, seen on every run) always fails to delete with "Attempted to perform an unauthorized
        // operation" once the user confirms. Never worth offering, and never worth taking ownership just
        // to make it deletable: dropped here, at scan time, so the row never shows up in the first place.
        // One summary line, not one per key: a normal machine has dozens of these and they buried the log.
        var result = new List<RegistryFinding>(findings.Count);
        var skipped = new List<string>();
        foreach (var finding in findings)
        {
            if (CanDelete(finding))
                result.Add(finding);
            else
                skipped.Add(finding.KeyPath);
        }

        if (skipped.Count > 0)
            logging?.Info(NotOfferedSummary(category, skipped));

        return result;
    }

    /// <summary>The log line for findings left out because they cannot be deleted: the count, then the first
    /// few key paths so a specific key can still be found in the log.</summary>
    internal static string NotOfferedSummary(RegistryCleanupCategory category, IReadOnlyList<string> keyPaths)
    {
        const int Shown = 3;
        var sample = string.Join(", ", keyPaths.Take(Shown));
        var more = keyPaths.Count > Shown ? $" and {keyPaths.Count - Shown} more" : string.Empty;
        return $"Registry cleanup ({category}): {keyPaths.Count} entr{(keyPaths.Count == 1 ? "y" : "ies")} cannot be deleted with the rights available and {(keyPaths.Count == 1 ? "is" : "are")} not offered: {sample}{more}.";
    }

    /// <summary>
    /// True when this finding could actually be removed: a value needs write access on its containing key,
    /// a whole key needs delete access on itself AND every subkey a real subtree delete would have to
    /// remove (a TrustedInstaller ACE commonly sits on one nested key, not the CLSID root itself). Never
    /// changes anything, purely a probe: opening with a right does not use it.
    /// </summary>
    private static bool CanDelete(RegistryFinding finding) =>
        finding.ValueName is not null
            ? OpenForRights(finding.KeyPath, RegistryRights.SetValue) is { } valueKey && Dispose(valueKey)
            : CanDeleteKeyTree(finding.KeyPath);

    /// <summary>Test-only seam for the gate above: never a real UAC prompt, never a real elevated worker,
    /// just the same permission probe run against a scratch key the test itself owns.</summary>
    internal static bool CanDeleteForTest(RegistryFinding finding) => CanDelete(finding);

    private static bool CanDeleteKeyTree(string keyPath)
    {
        // Delete alone is not enough to walk the subtree: DeleteSubKeyTree itself needs to enumerate every
        // child before it can delete them (.NET's GetSubKeyNames calls RegQueryInfoKey first, which itself
        // needs KEY_QUERY_VALUE), so this probe requests all three rights together.
        using var key = OpenForRights(keyPath, RegistryRights.Delete | RegistryRights.ReadKey);
        if (key is null)
            return false;

        string[] children;
        try
        {
            children = key.GetSubKeyNames();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }

        foreach (var child in children)
        {
            if (!CanDeleteKeyTree($@"{keyPath}\{child}"))
                return false;
        }

        return true;
    }

    private static bool Dispose(RegistryKey key)
    {
        key.Dispose();
        return true;
    }

    private static RegistryKey? OpenForRights(string keyPath, RegistryRights rights)
    {
        var separator = keyPath.IndexOf('\\');
        if (separator <= 0)
            return null;

        var root = keyPath[..separator] switch
        {
            "HKLM" => Registry.LocalMachine,
            "HKCU" => Registry.CurrentUser,
            _ => null,
        };

        try
        {
            return root?.OpenSubKey(keyPath[(separator + 1)..], RegistryKeyPermissionCheck.Default, rights);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>Test seam, run after the last check and right before a delete call, so a test can remove the
    /// entry in exactly that gap. Per async flow, so parallel tests never see each other's hook.</summary>
    internal static readonly AsyncLocal<Action?> BeforeDeleteForTest = new();

    /// <summary>
    /// Deletes exactly the findings given. Every finding is re-judged with the SAME category-specific logic
    /// the scan used, immediately before its delete call: the no-throw delete overloads only cover a value
    /// that went away ENTIRELY, so a value a program repaired in the meantime (present again, with
    /// different content) would otherwise be deleted anyway. Real time passes while the user reviews the
    /// confirmation dialog, so a finding is a snapshot, never a standing permission to delete.
    /// </summary>
    public static int Delete(
        IReadOnlyList<RegistryFinding> findings, LoggingService logging, CancellationToken cancellationToken = default)
    {
        var deleted = 0;
        foreach (var finding in findings)
        {
            // Cancellation ends the run between findings, never inside one, and reports what is already gone
            // instead of throwing, so the caller's count stays truthful.
            if (cancellationToken.IsCancellationRequested)
            {
                logging.Info($"Registry cleanup: cancelled after {deleted} deletion(s).");
                break;
            }

            if (!IsStillBroken(finding))
            {
                logging.Info($"Registry cleanup: '{finding.DisplayName}' ({finding.KeyPath}) no longer looks broken; skipping.");
                continue;
            }

            try
            {
                if (finding.ValueName is null)
                {
                    var (parent, name) = SplitParentAndName(finding.KeyPath);
                    using var parentKey = OpenWritable(parent, logging);
                    if (parentKey is null)
                        continue; // could not open it: nothing was actually deleted, do not count it

                    // Handle-based tree delete: no name is resolved twice, so a link planted after the check
                    // can never be followed into another hive (HKCU and HKLM alike).
                    var status = RegistryBackupService.DeleteKeyTreeLinkSafe(
                        parentKey.Handle, name, out var ntStatus, BeforeDeleteForTest.Value);
                    switch (status)
                    {
                        case RegistryBackupService.KeyTreeDeleteStatus.Deleted:
                            deleted++;
                            break;
                        case RegistryBackupService.KeyTreeDeleteStatus.NotFound:
                            logging.Info($"Registry cleanup: '{finding.DisplayName}' ({finding.KeyPath}) was already gone; not counted.");
                            break;
                        case RegistryBackupService.KeyTreeDeleteStatus.RefusedLink:
                            logging.Warn($"Registry cleanup: '{finding.DisplayName}' ({finding.KeyPath}) contains a registry link; not deleting it.");
                            break;
                        case RegistryBackupService.KeyTreeDeleteStatus.OpenFailed:
                            logging.Warn($"Registry cleanup: could not open '{finding.DisplayName}' ({finding.KeyPath}) for a safe delete; not deleting it.");
                            break;
                        default:
                            logging.Warn($"Registry cleanup: could not remove '{finding.DisplayName}' ({finding.KeyPath}): status 0x{ntStatus:X8}; not counted.");
                            break;
                    }

                    continue;
                }

                BeforeDeleteForTest.Value?.Invoke();
                using var key = OpenWritable(finding.KeyPath, logging);
                if (key is null)
                    continue;

                key.DeleteValue(finding.ValueName, throwOnMissingValue: true);
                deleted++;
            }
            catch (ArgumentException)
            {
                // The throwing overloads report an entry that vanished since the last check as an
                // ArgumentException: it is gone, but not by this run, so it is not counted as deleted.
                logging.Info($"Registry cleanup: '{finding.DisplayName}' ({finding.KeyPath}) was already gone; not counted.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                           or System.Security.SecurityException)
            {
                logging.Warn($"Registry cleanup: could not remove '{finding.DisplayName}' ({finding.KeyPath}): {ex.Message}");
            }
        }

        return deleted;
    }

    /// <summary>
    /// File.Exists as a 32-bit program would see it, too. This process is 64-bit, but WOW6432Node keys,
    /// SharedDLLs and many Run/CLSID entries were written by 32-bit installers: their "C:\Windows\System32\x.ocx"
    /// really lives in SysWOW64, and a %ProgramFiles% they stored expands to "Program Files" here instead of
    /// "Program Files (x86)". Judged by the 64-bit view alone, a working registration reads as broken and is
    /// offered for deletion; a file found in either view keeps the entry, so this can only reduce deletions.
    /// </summary>
    internal static bool ExistsInAnyView(string path) => ExistsInAnyView(path, IsRootReadyCached);

    /// <summary>
    /// The testable core of <see cref="ExistsInAnyView(string)"/>. A path on a drive or share that is not
    /// available right now (unplugged USB stick, disconnected network drive, unreachable UNC share) is UNKNOWN,
    /// never broken: <c>File.Exists</c> answers false for it exactly as for a deleted file, and judging by that
    /// would offer a working registration for deletion. Such a path, and any path the file system refuses to
    /// evaluate, therefore counts as existing.
    /// </summary>
    internal static bool ExistsInAnyView(string path, Func<string, bool> isRootReady)
    {
        try
        {
            if (File.Exists(path))
                return true;

            foreach (var (native, wow) in WowRedirectedFolders.Value)
            {
                if (path.StartsWith(native + @"\", StringComparison.OrdinalIgnoreCase)
                    && File.Exists(wow + path[native.Length..]))
                {
                    return true;
                }
            }

            return IsOnUnavailableLocation(path, isRootReady);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return true;
        }
    }

    /// <summary>
    /// True for a UNC path (a share can always be merely unreachable) and for a path whose root is not a
    /// ready, existing drive. A path without any root (relative) names no drive and is not judged here.
    /// </summary>
    internal static bool IsOnUnavailableLocation(string path, Func<string, bool> isRootReady)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return true;

        var root = Path.GetPathRoot(path);
        return !string.IsNullOrEmpty(root) && !isRootReady(root);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (bool Ready, long Ticks)> RootReadiness
        = new(StringComparer.OrdinalIgnoreCase);

    private const long RootReadinessLifetimeMs = 30_000;

    private static bool IsRootReadyCached(string root)
    {
        var now = Environment.TickCount64;
        if (RootReadiness.TryGetValue(root, out var cached) && now - cached.Ticks < RootReadinessLifetimeMs)
            return cached.Ready;

        bool ready;
        try
        {
            ready = new DriveInfo(root).IsReady && Directory.Exists(root);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            ready = false;
        }

        RootReadiness[root] = (ready, now);
        return ready;
    }

    private static readonly Lazy<(string Native, string Wow)[]> WowRedirectedFolders = new(() =>
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        (string Native, string Wow)[] pairs =
        [
            (windows.Length > 0 ? Path.Combine(windows, "System32") : string.Empty,
             windows.Length > 0 ? Path.Combine(windows, "SysWOW64") : string.Empty),
            (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
             Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
             Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86)),
        ];
        return pairs
            .Where(p => p.Native.Length > 0 && p.Wow.Length > 0
                        && !string.Equals(p.Native, p.Wow, StringComparison.OrdinalIgnoreCase))
            .Select(p => (Path.TrimEndingDirectorySeparator(p.Native), Path.TrimEndingDirectorySeparator(p.Wow)))
            .ToArray();
    });

    /// <summary>
    /// Re-runs the same judgment <c>ScanXxx</c> applied to produce this finding, against whatever the registry
    /// says RIGHT NOW. False for anything that could not be re-confirmed: an entry the finding names that
    /// has vanished entirely is not "still broken", it is just gone, and there is nothing left to delete.
    /// </summary>
    private static bool IsStillBroken(RegistryFinding finding) => finding.Category switch
    {
        RegistryCleanupCategory.AppPaths =>
            RegistryCleanupSafety.IsBrokenFileReference(ReadValue(finding.KeyPath, string.Empty), ExistsInAnyView),

        RegistryCleanupCategory.SharedDlls =>
            finding.ValueName is { Length: > 0 } dllPath && !ExistsInAnyView(dllPath)
                && ValueNames(SharedDllsKey).Contains(dllPath, StringComparer.OrdinalIgnoreCase),

        RegistryCleanupCategory.ActiveXCom =>
            RegistryCleanupSafety.TryGetAllBrokenPaths(
                [ReadValue($@"{finding.KeyPath}\InprocServer32", string.Empty), ReadValue($@"{finding.KeyPath}\LocalServer32", string.Empty)],
                ExistsInAnyView, out _),

        RegistryCleanupCategory.Autostart =>
            finding.ValueName is { } valueName
                && RegistryCleanupSafety.IsBrokenFileReference(ReadValue(finding.KeyPath, valueName), ExistsInAnyView),

        RegistryCleanupCategory.UninstallEntries =>
            ReadDwordValue(finding.KeyPath, "SystemComponent") != 1
                && TryJudgeUninstallEntryBroken(finding.KeyPath, out _),

        _ => false,
    };

    /// <summary>
    /// Every real directory any installed program's uninstall entry claims as its own; InstallLocation
    /// verbatim, plus the directory portion of a confidently-parsed DisplayIcon/UninstallString. Used by
    /// the leftover-folder scanner to rule out a folder that a REAL, registered install still owns; unlike
    /// the rest of this class, this reads every entry regardless of whether it looks broken.
    /// </summary>
    public static IReadOnlyList<string> AllUninstallInstallPaths()
    {
        var paths = new List<string>();
        foreach (var root in WindowsRegistryRoots.Uninstall)
        {
            foreach (var subKeyName in SubKeyNames(root))
            {
                var keyPath = $@"{root}\{subKeyName}";

                if (ReadValue(keyPath, "InstallLocation") is { Length: > 0 } location)
                    paths.Add(location);

                foreach (var raw in new[] { ReadValue(keyPath, "DisplayIcon"), ReadValue(keyPath, "UninstallString") })
                {
                    if (RegistryCleanupSafety.ExtractFilePath(raw, ExistsInAnyView) is { Length: > 0 } filePath)
                    {
                        try
                        {
                            if (Path.GetDirectoryName(filePath) is { Length: > 0 } directory)
                                paths.Add(directory);
                        }
                        catch (ArgumentException)
                        {
                            // A malformed registry value with invalid path characters is simply skipped; this
                            // static method has no logger and feeds only LeftoverInstallDiscovery's best-effort
                            // heuristic, never a delete. Deliberate.
                        }
                    }
                }
            }
        }

        return paths;
    }

    private static List<RegistryFinding> ScanAppPaths(CancellationToken cancellationToken)
    {
        var findings = new List<RegistryFinding>();
        foreach (var root in AppPathsRoots)
        {
            foreach (var subKeyName in SubKeyNames(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var keyPath = $@"{root}\{subKeyName}";
                var target = ReadValue(keyPath, string.Empty);
                if (RegistryCleanupSafety.IsBrokenFileReference(target, ExistsInAnyView))
                    findings.Add(new RegistryFinding(RegistryCleanupCategory.AppPaths, keyPath, null, subKeyName, target!));
            }
        }

        return findings;
    }

    private static List<RegistryFinding> ScanSharedDlls(CancellationToken cancellationToken)
    {
        var findings = new List<RegistryFinding>();
        foreach (var dllPath in ValueNames(SharedDllsKey))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The value NAME is itself the DLL path here: the value data is only a refcount, nothing to
            // extract or unquote.
            if (!string.IsNullOrWhiteSpace(dllPath) && !ExistsInAnyView(dllPath))
                findings.Add(new RegistryFinding(
                    RegistryCleanupCategory.SharedDlls, SharedDllsKey, dllPath, Path.GetFileName(dllPath), dllPath));
        }

        return findings;
    }

    private static List<RegistryFinding> ScanActiveXCom(CancellationToken cancellationToken)
    {
        var findings = new List<RegistryFinding>();
        foreach (var root in ClsidRoots)
        {
            foreach (var clsid in SubKeyNames(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var keyPath = $@"{root}\{clsid}";

                // A CLSID can carry BOTH an in-proc and a local server; only checking whichever comes
                // first missed the case where that one is a stale leftover but the other still works and
                // would have kept the class activatable: the whole CLSID got deleted anyway. Broken only
                // when every server registration this CLSID actually has is confidently dead.
                var servers = new[]
                {
                    ReadValue($@"{keyPath}\InprocServer32", string.Empty),
                    ReadValue($@"{keyPath}\LocalServer32", string.Empty),
                };

                if (RegistryCleanupSafety.TryGetAllBrokenPaths(servers, ExistsInAnyView, out var brokenServers))
                    findings.Add(new RegistryFinding(RegistryCleanupCategory.ActiveXCom, keyPath, null, clsid, brokenServers[0]));
            }
        }

        return findings;
    }

    private static List<RegistryFinding> ScanAutostart(CancellationToken cancellationToken)
    {
        var findings = new List<RegistryFinding>();
        foreach (var root in AutostartRoots)
        {
            foreach (var valueName in ValueNames(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var command = ReadValue(root, valueName);
                if (RegistryCleanupSafety.IsBrokenFileReference(command, ExistsInAnyView))
                    findings.Add(new RegistryFinding(RegistryCleanupCategory.Autostart, root, valueName, valueName, command!));
            }
        }

        return findings;
    }

    private static List<RegistryFinding> ScanUninstallEntries(CancellationToken cancellationToken)
    {
        var findings = new List<RegistryFinding>();
        foreach (var root in WindowsRegistryRoots.Uninstall)
        {
            foreach (var subKeyName in SubKeyNames(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var keyPath = $@"{root}\{subKeyName}";

                // Windows itself never shows a SystemComponent=1 entry in "Apps & features" (shared
                // runtimes, update packages), never a target here, removing one can break whatever
                // still depends on it even though ITS OWN path looks gone.
                if (ReadDwordValue(keyPath, "SystemComponent") == 1)
                    continue;

                var displayName = ReadValue(keyPath, "DisplayName");
                if (string.IsNullOrWhiteSpace(displayName))
                    continue;

                if (TryJudgeUninstallEntryBroken(keyPath, out var claimedPath))
                    findings.Add(new RegistryFinding(RegistryCleanupCategory.UninstallEntries, keyPath, null, displayName!, claimedPath));
            }
        }

        return findings;
    }

    /// <summary>
    /// One entry's broken/kept judgment, shared between the scan and the re-check
    /// <see cref="IsStillBroken"/> does right before deleting. An MSI-based uninstall command
    /// (<c>MsiExec /X{GUID}</c>) is judged by whether that product code is still registered ANYWHERE a real
    /// install would leave it, not by file existence: the command never names a file. Broken only when the
    /// product code is registered nowhere AND there is no InstallLocation either; either present means
    /// keep, the same "existence anywhere proves keep" rule as the file-path side. Every other entry falls
    /// back to the DisplayIcon/UninstallString file-path judgment, which aborts (never broken) on a field
    /// it cannot confidently parse.
    /// </summary>
    internal static bool TryJudgeUninstallEntryBroken(string keyPath, out string claimedPath)
    {
        var uninstallString = ReadValue(keyPath, "UninstallString");
        var productCode = RegistryCleanupSafety.ExtractMsiProductCode(uninstallString);

        if (productCode is not null)
        {
            var packed = RegistryCleanupSafety.PackProductCode(productCode);
            var registered = packed is not null && MsiProductRoots.Any(root => KeyExists($@"{root}\{packed}"));
            var installLocation = ReadValue(keyPath, "InstallLocation");

            if (registered || !string.IsNullOrWhiteSpace(installLocation))
            {
                claimedPath = string.Empty;
                return false;
            }

            claimedPath = uninstallString!;
            return true;
        }

        if (RegistryCleanupSafety.TryGetAllBrokenPaths(
                [ReadValue(keyPath, "DisplayIcon"), uninstallString], ExistsInAnyView, out var brokenPaths))
        {
            claimedPath = brokenPaths[0];
            return true;
        }

        claimedPath = string.Empty;
        return false;
    }

    private static IReadOnlyList<string> SubKeyNames(string keyPath)
    {
        try
        {
            using var key = OpenReadable(keyPath);
            return key?.GetSubKeyNames() ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }
    }

    private static IReadOnlyList<string> ValueNames(string keyPath)
    {
        try
        {
            using var key = OpenReadable(keyPath);
            return key?.GetValueNames() ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }
    }

    private static string? ReadValue(string keyPath, string valueName)
    {
        try
        {
            using var key = OpenReadable(keyPath);
            return key?.GetValue(valueName) as string;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static int? ReadDwordValue(string keyPath, string valueName)
    {
        try
        {
            using var key = OpenReadable(keyPath);
            return key?.GetValue(valueName) is int value ? value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether a root key exists at all, independent of what <c>reg.exe export</c> says about it; used by
    /// <see cref="RegistryBackupService"/> to tell "nothing to back up here" apart from "the export
    /// actually failed" without trusting reg.exe's own exit code for that distinction.
    /// </summary>
    public static bool KeyExists(string keyPath)
    {
        using var key = OpenReadable(keyPath);
        return key is not null;
    }

    private static RegistryKey? OpenReadable(string keyPath) => Open(keyPath, writable: false);

    private static RegistryKey? OpenWritable(string keyPath, LoggingService logging) => Open(keyPath, writable: true, logging);

    internal static RegistryKey? Open(string keyPath, bool writable, LoggingService? logging = null)
    {
        var separator = keyPath.IndexOf('\\');
        if (separator <= 0)
            return null;

        // A user-writable HKCU key can be turned into a link to HKLM, which plain OpenSubKey follows at
        // every component; the elevated delete would then act on the target.
        if (keyPath.AsSpan(0, separator).SequenceEqual("HKCU") && separator + 1 < keyPath.Length)
        {
            try
            {
                return RegistryBackupService.OpenHkcuLinkSafe(keyPath[(separator + 1)..], writable, logging);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // A key that vanished mid-walk (ERROR_KEY_DELETED while its link marker was read) is just
                // not there, same as in the branch below.
                return null;
            }
        }

        // Opened for writing, the parent of a finding is the one place a link planted below a root could
        // redirect an elevated delete. The root itself is admin-only ancestry and may be a real link (the
        // WOW6432Node roots are), so it is resolved the plain way; only what lies below it is walked link by link.
        if (writable && keyPath.AsSpan(0, separator).SequenceEqual("HKLM") && separator + 1 < keyPath.Length)
        {
            try
            {
                return OpenHklmForWrite(keyPath, logging);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return null;
            }
        }

        var root = keyPath[..separator] switch
        {
            "HKLM" => Registry.LocalMachine,
            "HKCU" => Registry.CurrentUser,
            _ => null,
        };

        try
        {
            return root?.OpenSubKey(keyPath[(separator + 1)..], writable);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static RegistryKey? OpenHklmForWrite(string keyPath, LoggingService? logging)
    {
        if (SplitAtHklmRoot(keyPath) is not var (root, remainder))
            return null;

        var rootSubKey = root[(root.IndexOf('\\') + 1)..];
        if (remainder.Length == 0)
            return Registry.LocalMachine.OpenSubKey(rootSubKey, writable: true);

        using var rootKey = Registry.LocalMachine.OpenSubKey(rootSubKey, writable: false);
        if (rootKey is null)
            return null;

        return RegistryBackupService.OpenBelowLinkSafe(rootKey.Handle, remainder, writable: true, logging: logging, displayRoot: "HKLM");
    }

    /// <summary>
    /// Splits an HKLM key path at the longest cleanup root it equals or lies below (matched on a segment
    /// boundary, so <c>...\Run</c> never claims <c>...\RunOnce</c>). Remainder is empty when the path is
    /// the root itself. Null when no root covers the path: nothing outside the roots is ever opened for
    /// writing.
    /// </summary>
    internal static (string Root, string Remainder)? SplitAtHklmRoot(string keyPath)
    {
        string? best = null;
        foreach (var category in Enum.GetValues<RegistryCleanupCategory>())
        {
            foreach (var root in RootKeys(category))
            {
                if (!root.StartsWith(@"HKLM\", StringComparison.OrdinalIgnoreCase)
                    || (best is not null && root.Length <= best.Length))
                {
                    continue;
                }

                if (keyPath.Equals(root, StringComparison.OrdinalIgnoreCase)
                    || keyPath.StartsWith(root + '\\', StringComparison.OrdinalIgnoreCase))
                {
                    best = root;
                }
            }
        }

        return best is null ? null : (best, keyPath.Length == best.Length ? string.Empty : keyPath[(best.Length + 1)..]);
    }

    private static (string Parent, string Name) SplitParentAndName(string keyPath)
    {
        var lastSeparator = keyPath.LastIndexOf('\\');
        return (keyPath[..lastSeparator], keyPath[(lastSeparator + 1)..]);
    }
}
