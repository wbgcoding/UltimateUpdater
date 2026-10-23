using System.IO;

namespace UltimateUpdater.Services.Cleanup;

/// <summary>The fixed catalogue, in the order the dialog shows it.</summary>
public static class CleanupCatalog
{
    /// <summary>
    /// Each target carries its own <c>DefaultEnabled</c>: the routine temp and cache folders are on, the
    /// riskier or opt-in ones (recycle bin, driver and Python version cleanup, most repair steps, some
    /// folders) are off. Nothing here is ever run automatically: a cleanup always requires the user
    /// to press the button and then confirm the red warning. <paramref name="logWarning"/> hears about any
    /// drive skipped while gathering the defrag targets below (optional: every other caller of this method
    /// still works with none).
    /// </summary>
    public static IReadOnlyList<CleanupTarget> Build(Action<string>? logWarning = null) =>
        WithDisjointPaths(BuildCatalog(logWarning));

    /// <summary>
    /// Gives every target sole ownership of its own bytes. Some of the catalogue's directories nest inside
    /// each other. %TEMP%\chocolatey belongs to the package-manager cache but sits inside "user-temp" .
    /// and without this the outer target both counted and deleted the inner one's content, so deselecting
    /// the inner target protected nothing and selecting both counted the same megabytes twice.
    /// The inner path wins: whoever named it exactly is the target that owns it.
    /// </summary>
    internal static IReadOnlyList<CleanupTarget> WithDisjointPaths(IReadOnlyList<CleanupTarget> targets)
    {
        var owned = targets
            .SelectMany(target => target.Directories.Select(directory => (target.Id, Directory: directory)))
            .ToList();

        return targets.Select(target =>
        {
            // The sweep discovers its folders at run time, so which of them overlap a fixed target cannot
            // be worked out here; it simply yields to all of them. Everything the catalogue names
            // explicitly belongs to the target that names it.
            var excluded = target.Kind == CleanupKind.AppCacheSweep
                ? owned.Select(other => other.Directory).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : owned
                    .Where(other => !string.Equals(other.Id, target.Id, StringComparison.Ordinal))
                    .Where(other => target.Directories.Any(mine => IsStrictlyInside(other.Directory, mine)))
                    .Select(other => other.Directory)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

            return excluded.Count == 0 ? target : target with { ExcludedDirectories = excluded };
        }).ToList();
    }

    /// <summary>True when <paramref name="path"/> sits below <paramref name="root"/>, not when it IS it.</summary>
    private static bool IsStrictlyInside(string path, string root) =>
        path.StartsWith(
            Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<CleanupTarget> BuildCatalog(Action<string>? logWarning)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var sfc = Path.Combine(system, "sfc.exe");
        var dism = Path.Combine(system, "Dism.exe");

        return
        [
            new CleanupTarget("user-temp", "CleanupUserTemp", "CleanupUserTempDesc",
                CleanupKind.Directories, DefaultEnabled: true,
                RiskLevel: CleanupRiskLevel.Safe, ConsequenceKey: "CleanupUserTempConsequence")
            {
                Directories = Distinct([Path.GetTempPath(), Path.Combine(local, "Temp")]),
            },

            new CleanupTarget("all-users-temp", "CleanupAllUsersTemp", "CleanupAllUsersTempDesc",
                CleanupKind.Directories, DefaultEnabled: true,
                RiskLevel: CleanupRiskLevel.Safe, ConsequenceKey: "CleanupAllUsersTempConsequence", NeedsElevation: true)
            {
                Directories = OtherUsersTempDirectories(),
            },

            new CleanupTarget("windows-temp", "CleanupWindowsTemp", "CleanupWindowsTempDesc",
                CleanupKind.Directories, DefaultEnabled: true,
                RiskLevel: CleanupRiskLevel.Safe, ConsequenceKey: "CleanupWindowsTempConsequence", NeedsElevation: true)
            {
                Directories = Distinct([Path.Combine(windows, "Temp")]),
            },

            new CleanupTarget("windows-update-cache", "CleanupUpdateCache", "CleanupUpdateCacheDesc",
                CleanupKind.Directories, DefaultEnabled: true,
                RiskLevel: CleanupRiskLevel.Safe, ConsequenceKey: "CleanupUpdateCacheConsequence", NeedsElevation: true)
            {
                Directories = Distinct([Path.Combine(windows, "SoftwareDistribution", "Download")]),
            },

            new CleanupTarget("delivery-optimization", "CleanupDeliveryOptimization", "CleanupDeliveryOptimizationDesc",
                CleanupKind.Directories, DefaultEnabled: true,
                RiskLevel: CleanupRiskLevel.Safe, ConsequenceKey: "CleanupDeliveryOptimizationConsequence", NeedsElevation: true)
            {
                Directories = Distinct([Path.Combine(windows, "SoftwareDistribution", "DeliveryOptimization")]),
            },

            new CleanupTarget("thumbnail-cache", "CleanupThumbnails", "CleanupThumbnailsDesc",
                CleanupKind.Directories, DefaultEnabled: true,
                RiskLevel: CleanupRiskLevel.Safe, ConsequenceKey: "CleanupThumbnailsConsequence")
            {
                Directories = Distinct([Path.Combine(local, "Microsoft", "Windows", "Explorer")]),
            },

            new CleanupTarget("crash-dumps", "CleanupCrashDumps", "CleanupCrashDumpsDesc",
                CleanupKind.Directories, DefaultEnabled: true,
                RiskLevel: CleanupRiskLevel.Safe, ConsequenceKey: "CleanupCrashDumpsConsequence")
            {
                Directories = Distinct([Path.Combine(local, "CrashDumps")]),
            },

            // Shader caches. Every one of these is rebuilt by the driver on demand; the only cost of
            // emptying them is a slightly longer first launch of whatever used them. Nothing here is
            // browser data: that is deliberately absent from this whole catalogue.
            new CleanupTarget("shader-cache", "CleanupShaderCache", "CleanupShaderCacheDesc",
                CleanupKind.Directories, DefaultEnabled: true,
                RiskLevel: CleanupRiskLevel.Safe, ConsequenceKey: "CleanupShaderCacheConsequence")
            {
                Directories = Existing(
                [
                    Path.Combine(local, "D3DSCache"),
                    Path.Combine(local, "NVIDIA", "DXCache"),
                    Path.Combine(local, "NVIDIA", "GLCache"),
                    Path.Combine(local, "NVIDIA Corporation", "NV_Cache"),
                    Path.Combine(local, "AMD", "DxCache"),
                    Path.Combine(local, "AMD", "DxcCache"),
                    Path.Combine(local, "Intel", "ShaderCache"),
                ]),
            },

            // What package managers keep so a reinstall need not download again. Safe to drop: every one of
            // them re-fetches on demand. The package STORES (.nuget\packages, node_modules) are not here .
            // those are the installed packages themselves, not a cache.
            new CleanupTarget("package-manager-cache", "CleanupPackageCache", "CleanupPackageCacheDesc",
                CleanupKind.Directories, DefaultEnabled: true,
                RiskLevel: CleanupRiskLevel.Safe, ConsequenceKey: "CleanupPackageCacheConsequence")
            {
                Directories = Existing(
                [
                    Path.Combine(local, "pip", "Cache"),
                    Path.Combine(local, "npm-cache", "_cacache"),
                    Path.Combine(local, "Yarn", "Cache"),
                    Path.Combine(local, "NuGet", "v3-cache"),
                    Path.Combine(Path.GetTempPath(), "chocolatey"),
                    Path.Combine(Path.GetTempPath(), "NuGetScratch"),
                ]),
            },

            // Build-tool caches. Gradle's build cache and the Android SDK's download cache are both
            // regenerated; neither holds a project or a setting.
            new CleanupTarget("build-tool-cache", "CleanupBuildCache", "CleanupBuildCacheDesc",
                CleanupKind.Directories, DefaultEnabled: true,
                RiskLevel: CleanupRiskLevel.Safe, ConsequenceKey: "CleanupBuildCacheConsequence")
            {
                Directories = Existing(
                [
                    Path.Combine(profile, ".gradle", "caches", "build-cache-1"),
                    Path.Combine(profile, ".android", "cache"),
                ]),
            },

            // Every OTHER program's cache/log folder this catalogue does not already name individually,
            // found by walking %LOCALAPPDATA%/%APPDATA%/%PROGRAMDATA% rather than by listing programs .
            // see AppCacheDiscovery. OFF by default: broader and less individually reviewed than the rest
            // of this table, even though the same safety gate applies.
            new CleanupTarget("app-cache-sweep", "CleanupAppCacheSweep", "CleanupAppCacheSweepDesc",
                CleanupKind.AppCacheSweep, DefaultEnabled: false,
                RiskLevel: CleanupRiskLevel.Considered, ConsequenceKey: "CleanupAppCacheSweepConsequence"),

            // The LeftoverInstalls catalogue entry is intentionally absent. LeftoverInstallDiscovery and
            // LeftoverInstallSafety stay in the codebase, built and tested, for when a stronger detection
            // signal exists; nothing reachable may delete a real,
            // installed program in the meantime.

            // Folders pip itself leaves behind under a "~"-prefixed name after an install got interrupted
            // mid-rename; unambiguous by construction, see PythonCleanupDiscovery, so this is safe and on
            // by default, unlike the app-cache sweep above.
            new CleanupTarget("pip-install-leftovers", "CleanupPipLeftovers", "CleanupPipLeftoversDesc",
                CleanupKind.PipInstallLeftovers, DefaultEnabled: true,
                RiskLevel: CleanupRiskLevel.Safe, ConsequenceKey: "CleanupPipLeftoversConsequence"),

            // Kernel crash dumps. Each is hundreds of megabytes and only useful while someone is actually
            // investigating a blue screen: which is why this one is OFF unless asked for.
            new CleanupTarget("memory-dumps", "CleanupMemoryDumps", "CleanupMemoryDumpsDesc",
                CleanupKind.Directories, DefaultEnabled: false,
                RiskLevel: CleanupRiskLevel.Considered, ConsequenceKey: "CleanupMemoryDumpsConsequence", NeedsElevation: true)
            {
                Directories = Existing([Path.Combine(windows, "Minidump")]),
            },

            // Windows' own logs (CBS, DISM, setup). Safe to delete (Windows recreates them) but they are
            // exactly what someone diagnosing a failed update would want, so this is OFF by default.
            new CleanupTarget("windows-logs", "CleanupWindowsLogs", "CleanupWindowsLogsDesc",
                CleanupKind.Directories, DefaultEnabled: false,
                RiskLevel: CleanupRiskLevel.Considered, ConsequenceKey: "CleanupWindowsLogsConsequence", NeedsElevation: true)
            {
                Directories = Existing([Path.Combine(windows, "Logs")]),
            },

            // Off by default: emptying the bin is the one target whose contents the user may still want.
            new CleanupTarget("recycle-bin", "CleanupRecycleBin", "CleanupRecycleBinDesc",
                CleanupKind.RecycleBin, DefaultEnabled: false,
                RiskLevel: CleanupRiskLevel.Considered, ConsequenceKey: "CleanupRecycleBinConsequence"),

            // Needs admin to write its unattended profile; without it the run is skipped entirely, so it
            // is flagged like every other elevation target rather than failing quietly.
            new CleanupTarget("disk-cleanup", "CleanupDiskCleanup", "CleanupDiskCleanupDesc",
                CleanupKind.DiskCleanup, DefaultEnabled: true,
                RiskLevel: CleanupRiskLevel.Safe, ConsequenceKey: "CleanupDiskCleanupConsequence", NeedsElevation: true),

            // DISM comes FIRST, sfc restores from the component store DISM repairs. They are two separately
            // selectable rows, and catalogue order is run order. Both start OFF: they free no space and take a
            // long time, so they are an opt-in, not part of every run.
            new CleanupTarget("system-repair-dism", "CleanupSystemRepairDism", "CleanupSystemRepairDismDesc",
                CleanupKind.SystemRepair, DefaultEnabled: false,
                RiskLevel: CleanupRiskLevel.Considered, ConsequenceKey: "CleanupSystemRepairDismConsequence", NeedsElevation: true)
            {
                RepairSteps =
                [
                    new RepairStep(dism, ["/Online", "/Cleanup-Image", "/CheckHealth"], "DISM /CheckHealth"),
                    new RepairStep(dism, ["/Online", "/Cleanup-Image", "/ScanHealth"], "DISM /ScanHealth"),
                    new RepairStep(dism, ["/Online", "/Cleanup-Image", "/RestoreHealth"], "DISM /RestoreHealth"),
                ],
            },

            new CleanupTarget("system-repair-sfc", "CleanupSystemRepairSfc", "CleanupSystemRepairSfcDesc",
                CleanupKind.SystemRepair, DefaultEnabled: false,
                RiskLevel: CleanupRiskLevel.Considered, ConsequenceKey: "CleanupSystemRepairSfcConsequence", NeedsElevation: true)
            {
                RepairSteps = [new RepairStep(sfc, ["/scannow"], "sfc /scannow")],
            },

            // Component-store cleanup, after the repair rows, same catalogue-order-is-run-order
            // rule. ResetBase supersedes the plain cleanup when both are selected
            // (CleanupViewModel.Run.RunCleanupAsync drops the plain row from that run).
            new CleanupTarget(
                "component-cleanup", "CleanupComponentCleanup", "CleanupComponentCleanupDesc",
                CleanupKind.SystemRepair, DefaultEnabled: true,
                RiskLevel: CleanupRiskLevel.Considered, ConsequenceKey: "CleanupComponentCleanupDesc", NeedsElevation: true)
            {
                RepairSteps =
                [
                    new RepairStep(
                        dism, ["/Online", "/Cleanup-Image", "/StartComponentCleanup"], "DISM /StartComponentCleanup"),
                ],
            },

            new CleanupTarget(
                "component-resetbase", "CleanupComponentResetBase", "CleanupComponentResetBaseDesc",
                CleanupKind.SystemRepair, DefaultEnabled: false,
                RiskLevel: CleanupRiskLevel.Risky, ConsequenceKey: "CleanupComponentResetBaseDesc", NeedsElevation: true)
            {
                RepairSteps =
                [
                    new RepairStep(
                        dism, ["/Online", "/Cleanup-Image", "/StartComponentCleanup", "/ResetBase"],
                        "DISM /StartComponentCleanup /ResetBase"),
                ],
            },

            .. RegistryCleanupCatalog.Build(),

            // Security-critical: the elevated worker removes a device and deletes a driver package.
            // Off by default and red risk like the registry rows above; every row is individually
            // selectable and none is pre-selected (see DriverCleanupDiscovery/DriverCleanupExecutor).
            new CleanupTarget("driver-cleanup", "CleanupDrivers", "CleanupDriversDesc",
                CleanupKind.DriverCleanup, DefaultEnabled: false,
                RiskLevel: CleanupRiskLevel.Risky, ConsequenceKey: "CleanupDriversConsequence", NeedsElevation: true),

            // Security-critical the same way the driver row above is: the elevated worker runs an
            // interpreter's own registered uninstaller, but only for a machine-wide one. A per-user
            // interpreter's uninstall entry lives under HKCU, which the same unprivileged user this app
            // already runs as could tamper with, so PythonVersionCleanupExecutor never lets the elevated
            // worker touch a per-user row at all; the ordinary unelevated app removes those itself. This
            // target's own NeedsElevation stays true because the model has one flag per target and a
            // machine-wide selection genuinely needs it; a purely per-user selection just makes the
            // elevated call above a no-op.
            new CleanupTarget("old-python-versions", "CleanupOldPythonVersions", "CleanupOldPythonVersionsDesc",
                CleanupKind.OldPythonVersions, DefaultEnabled: false,
                RiskLevel: CleanupRiskLevel.Risky, ConsequenceKey: "CleanupOldPythonVersionsConsequence", NeedsElevation: true),

            .. DriveDefragTargets(logWarning),
        ];
    }

    /// <summary>
    /// The real, gathered facts about one drive, kept deliberately apart from any WMI or
    /// <see cref="DriveInfo"/> call so <see cref="BuildDriveDefragTargets"/> can be driven from fabricated
    /// data in a test. <paramref name="Letter"/> carries no trailing separator (e.g. "C:").
    /// </summary>
    internal readonly record struct DriveFacts(
        string Letter, string VolumeLabel, DriveType DriveType, long TotalSize, DriveMediaType MediaType, bool IsUsb);

    /// <summary>
    /// One target PER drive whose media type could actually be determined:
    /// "never guess between the two" means a drive <see cref="DriveMediaTypeReader"/> could not classify at
    /// all is still never offered, not shown with a placeholder action word - but a drive found and answering
    /// with no specific media type (<see cref="DriveMediaType.Unspecified"/>) now gets its own row, and a
    /// USB-attached drive gets one even where <see cref="DriveInfo.DriveType"/> reports it as
    /// <see cref="DriveType.Removable"/> rather than <see cref="DriveType.Fixed"/>. Off by
    /// default, like every elevation target that reports no freed bytes; unlike them, a real risk of a
    /// MULTI-HOUR run is why this one starts off even though its own risk level is only "considered", same
    /// as system repair. The command stays <c>defrag &lt;letter&gt; /O</c> for every row regardless of
    /// medium or bus, which is what Windows' own Optimize Drives uses to pick the right operation itself.
    /// </summary>
    private static IReadOnlyList<CleanupTarget> DriveDefragTargets(Action<string>? logWarning) => BuildDriveDefragTargets(
        GatherDriveFacts(
            DriveInfo.GetDrives().Where(drive => drive.IsReady),
            drive =>
            {
                var letter = drive.Name.TrimEnd('\\');
                return new DriveFacts(
                    letter,
                    CleanupService.TryGetVolumeLabel(drive) ?? string.Empty,
                    drive.DriveType,
                    drive.TotalSize,
                    DriveMediaTypeReader.TryGetMediaType(letter),
                    DriveMediaTypeReader.TryGetIsUsb(letter));
            },
            logWarning));

    /// <summary>
    /// Runs <paramref name="extract"/> over every drive-like source, skipping (and reporting through
    /// <paramref name="logWarning"/>) any one that throws while its facts are read instead of faulting the
    /// whole catalogue - a drive can be pulled, or a share can turn restricted, at any point between the
    /// <see cref="DriveInfo.IsReady"/> check above and a property read such as <see cref="DriveInfo.TotalSize"/>.
    /// Generic over the source rather than <see cref="DriveInfo"/> directly, which is sealed and exposes no
    /// way to fabricate a throwing instance, so a test can prove the skip with a fake list instead.
    /// </summary>
    internal static IReadOnlyList<DriveFacts> GatherDriveFacts<TDrive>(
        IEnumerable<TDrive> drives, Func<TDrive, DriveFacts> extract, Action<string>? logWarning)
    {
        var facts = new List<DriveFacts>();

        foreach (var drive in drives)
        {
            try
            {
                facts.Add(extract(drive));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logWarning?.Invoke($"Cleanup: a drive could not be read for defrag targets, skipped: {ex.Message}");
            }
        }

        return facts;
    }

    /// <summary>All row logic lives here, pure over <see cref="DriveFacts"/>, so a test can drive every
    /// branch (SSD/HDD/Unspecified, USB/not, present/absent title and sub-line wording) without a real
    /// drive or a WMI call.</summary>
    internal static IReadOnlyList<CleanupTarget> BuildDriveDefragTargets(IEnumerable<DriveFacts> facts)
    {
        var targets = new List<CleanupTarget>();

        foreach (var f in facts)
        {
            var isEligibleDriveType = f.DriveType == DriveType.Fixed || (f.DriveType == DriveType.Removable && f.IsUsb);
            if (!isEligibleDriveType || f.MediaType == DriveMediaType.Unknown)
                continue;

            // "DataNeu D:\" with a volume label, "D:\" without one: the single title argument.
            var displayName = string.IsNullOrEmpty(f.VolumeLabel) ? $"{f.Letter}\\" : $"{f.VolumeLabel} {f.Letter}\\";

            var titleKey = f.MediaType switch
            {
                DriveMediaType.Hdd => "CleanupDefragRowHdd",
                DriveMediaType.Ssd => "CleanupDefragRowSsd",
                _ => "CleanupDefragRowUnspecified",
            };

            var mediaWordKey = f.MediaType switch
            {
                DriveMediaType.Hdd => "MediaTypeHdd",
                DriveMediaType.Ssd => "MediaTypeSsd",
                _ => "MediaTypeOtherDrive",
            };

            // Sub-line wording, exhaustively: USB with a known medium gets the
            // longer "bus, medium (dot) size" template with two words; USB alone or a known medium alone
            // gets the shorter "word (dot) size" template with one word.
            var (descriptionKey, wordKeys) = f.IsUsb
                ? f.MediaType is DriveMediaType.Hdd or DriveMediaType.Ssd
                    ? ("CleanupDriveSublineUsb", (IReadOnlyList<string>)["MediaTypeUsbDrive", mediaWordKey])
                    : ("CleanupDriveSubline", (IReadOnlyList<string>)["MediaTypeUsbDrive"])
                : ("CleanupDriveSubline", (IReadOnlyList<string>)[mediaWordKey]);

            targets.Add(new CleanupTarget(
                $"defrag-{f.Letter.TrimEnd(':')}", titleKey, descriptionKey, CleanupKind.Defragmentation,
                DefaultEnabled: false, RiskLevel: CleanupRiskLevel.Considered,
                ConsequenceKey: "CleanupDefragConsequence", NeedsElevation: true)
            {
                TitleArguments = [displayName],
                DriveLetter = f.Letter,
                MediaType = f.MediaType,
                DescriptionWordKeys = wordKeys,
                DescriptionBytes = f.TotalSize,
            });
        }

        return targets;
    }

    /// <summary>
    /// Like <see cref="Distinct"/>, but drops what is not there. These targets name places that only exist
    /// when the tool that owns them is installed, and a row offering to clean a folder no machine has is
    /// noise: the list should describe THIS machine.
    /// </summary>
    private static IReadOnlyList<string> Existing(IEnumerable<string> paths) =>
        Distinct(paths).Where(Directory.Exists).ToList();

    private static IReadOnlyList<string> Distinct(IEnumerable<string> paths) =>
        paths.Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Drops the user's own Temp from the candidates by folder identity, never by spelling: an 8.3 or
    /// differently cased path to the same folder must not slip through, because the elevated worker would
    /// then clean a folder the unelevated user can swap at will. Fail closed: an own Temp whose identity
    /// cannot be read yields nothing, and a candidate whose identity cannot be read is left out.
    /// </summary>
    internal static IReadOnlyList<string> ExcludeOwnTemp(IEnumerable<string> candidates, string ownTemp)
    {
        if (!CleanupSafety.TryReadDirectoryIdentity(ownTemp, out var own))
            return [];

        return candidates
            .Where(candidate => CleanupSafety.TryReadDirectoryIdentity(candidate, out var id) && id != own)
            .ToList();
    }

    /// <summary>
    /// Every OTHER user's Temp folder: this user's own is already the first target. Enumerating the users
    /// directory needs no elevation, but deleting inside another user's profile does; a folder that cannot
    /// be touched is simply skipped at run time.
    /// </summary>
    private static IReadOnlyList<string> OtherUsersTempDirectories()
    {
        try
        {
            var usersRoot = Path.Combine(
                Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "C:\\", "Users");
            if (!Directory.Exists(usersRoot))
                return [];

            return Distinct(ExcludeOwnTemp(Directory.EnumerateDirectories(usersRoot)
                .Select(profile => Path.Combine(profile, "AppData", "Local", "Temp"))
                .Where(Directory.Exists), Path.GetFullPath(Path.GetTempPath())));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // an unreadable users directory simply means this target has nothing to offer
            return [];
        }
    }
}
