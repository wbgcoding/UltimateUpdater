using System.IO;
using Microsoft.Win32;

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
        BuildFor(logWarning, null);

    /// <summary>This process's own Temp folders: the redirected <c>%TEMP%</c> and the one under the local
    /// profile. The unelevated app sends them to the elevated worker (see <see cref="CleanupCommand.CallerTempPaths"/>).</summary>
    internal static IReadOnlyList<string> OwnTempPaths() =>
        Distinct([
            Path.GetTempPath(),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp"),
        ]);

    /// <summary>
    /// The catalogue as the elevated worker builds it. <paramref name="callerTempPaths"/> are the
    /// unelevated caller's own Temp folders as the caller reported them: with over-the-shoulder elevation the
    /// worker runs as ANOTHER account, so its own Temp is not the signed-in user's, and without them that user's
    /// Temp would be listed (and cleaned with admin rights) as an "other user's" Temp. The paths are only ever
    /// used to take folders OUT of the all-users Temp list, never to add or delete anything, so a hostile value
    /// can only shrink it. <paramref name="usersRoot"/> is a test seam for the profiles folder.
    /// </summary>
    internal static IReadOnlyList<CleanupTarget> BuildFor(
        Action<string>? logWarning, IReadOnlyList<string>? callerTempPaths, string? usersRoot = null) =>
        WithDisjointPaths(BuildCatalog(logWarning, callerTempPaths, usersRoot));

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
            .SelectMany(target => target.Directories.Concat(target.Files).Select(directory => (target.Id, Directory: directory)))
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

            return excluded.Count == 0
                ? target
                : target with
                {
                    ExcludedDirectories = target.ExcludedDirectories.Concat(excluded)
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                };
        }).ToList();
    }

    /// <summary>True when <paramref name="path"/> sits below <paramref name="root"/>, not when it IS it.</summary>
    private static bool IsStrictlyInside(string path, string root) =>
        path.StartsWith(
            Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>How old a file in a temp location must be before cleanup takes it: a day, so an installer
    /// that is between two steps, a download in progress or another signed-in user's live files stay.</summary>
    internal static readonly TimeSpan TempMinFileAge = TimeSpan.FromHours(24);

    /// <summary>The same for a cache folder, which programs rewrite constantly and rebuild on demand: an hour
    /// is enough to leave what is being written right now.</summary>
    internal static readonly TimeSpan CacheMinFileAge = TimeSpan.FromHours(1);

    private static IReadOnlyList<CleanupTarget> BuildCatalog(
        Action<string>? logWarning, IReadOnlyList<string>? callerTempPaths, string? usersRoot)
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
                MinFileAge = TempMinFileAge,
                Directories = Distinct([Path.GetTempPath(), Path.Combine(local, "Temp")]),
            },

            new CleanupTarget("all-users-temp", "CleanupAllUsersTemp", "CleanupAllUsersTempDesc",
                CleanupKind.Directories, DefaultEnabled: true,
                RiskLevel: CleanupRiskLevel.Safe, ConsequenceKey: "CleanupAllUsersTempConsequence", NeedsElevation: true)
            {
                MinFileAge = TempMinFileAge,
                Directories = OtherUsersTempDirectories(callerTempPaths, usersRoot, logWarning),
            },

            new CleanupTarget("windows-temp", "CleanupWindowsTemp", "CleanupWindowsTempDesc",
                CleanupKind.Directories, DefaultEnabled: true,
                RiskLevel: CleanupRiskLevel.Safe, ConsequenceKey: "CleanupWindowsTempConsequence", NeedsElevation: true)
            {
                MinFileAge = TempMinFileAge,
                Directories = Distinct([Path.Combine(windows, "Temp")]),
            },

            new CleanupTarget("windows-update-cache", "CleanupUpdateCache", "CleanupUpdateCacheDesc",
                CleanupKind.Directories, DefaultEnabled: true,
                RiskLevel: CleanupRiskLevel.Safe, ConsequenceKey: "CleanupUpdateCacheConsequence", NeedsElevation: true)
            {
                MinFileAge = TempMinFileAge,
                AdminOnlyDelete = true,
                Directories = Distinct([Path.Combine(windows, "SoftwareDistribution", "Download")]),
            },

            new CleanupTarget("thumbnail-cache", "CleanupThumbnails", "CleanupThumbnailsDesc",
                CleanupKind.Directories, DefaultEnabled: true,
                RiskLevel: CleanupRiskLevel.Safe, ConsequenceKey: "CleanupThumbnailsConsequence")
            {
                MinFileAge = CacheMinFileAge,
                Directories = Distinct([Path.Combine(local, "Microsoft", "Windows", "Explorer")]),
            },

            new CleanupTarget("crash-dumps", "CleanupCrashDumps", "CleanupCrashDumpsDesc",
                CleanupKind.Directories, DefaultEnabled: true,
                RiskLevel: CleanupRiskLevel.Safe, ConsequenceKey: "CleanupCrashDumpsConsequence")
            {
                MinFileAge = CacheMinFileAge,
                Directories = Distinct([Path.Combine(local, "CrashDumps")]),
            },

            // Shader caches. Every one of these is rebuilt by the driver on demand; the only cost of
            // emptying them is a slightly longer first launch of whatever used them. Nothing here is
            // browser data: that is deliberately absent from this whole catalogue.
            new CleanupTarget("shader-cache", "CleanupShaderCache", "CleanupShaderCacheDesc",
                CleanupKind.Directories, DefaultEnabled: true,
                RiskLevel: CleanupRiskLevel.Safe, ConsequenceKey: "CleanupShaderCacheConsequence")
            {
                MinFileAge = CacheMinFileAge,
                Directories = ShaderCacheDirectories(local, KnownFolderPaths.LocalAppDataLow()),
            },

            // What package managers keep so a reinstall need not download again. Safe to drop: every one of
            // them re-fetches on demand. The package STORES (.nuget\packages, node_modules) are not here .
            // those are the installed packages themselves, not a cache.
            new CleanupTarget("package-manager-cache", "CleanupPackageCache", "CleanupPackageCacheDesc",
                CleanupKind.Directories, DefaultEnabled: true,
                RiskLevel: CleanupRiskLevel.Safe, ConsequenceKey: "CleanupPackageCacheConsequence")
            {
                MinFileAge = CacheMinFileAge,
                Directories = PackageCacheDirectories(local, profile, Path.GetTempPath()),
                CleanCommands = PackageCacheCleanCommands(local, profile),
                ExcludedDirectories = PackageCacheKeptFolders(local),
            },

            // Build-tool caches. Gradle's build cache and the Android SDK's download cache are both
            // regenerated; neither holds a project or a setting.
            new CleanupTarget("build-tool-cache", "CleanupBuildCache", "CleanupBuildCacheDesc",
                CleanupKind.Directories, DefaultEnabled: true,
                RiskLevel: CleanupRiskLevel.Safe, ConsequenceKey: "CleanupBuildCacheConsequence")
            {
                MinFileAge = CacheMinFileAge,
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
                RiskLevel: CleanupRiskLevel.Considered, ConsequenceKey: "CleanupAppCacheSweepConsequence")
            {
                MinFileAge = CacheMinFileAge,
            },

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
                // The full dump is one file in the Windows folder, which only an administrator may delete: an
                // unelevated run skips the row as needs-admin instead of reading "done" with nothing freed.
                MinFileAge = CacheMinFileAge,
                AdminOnlyDelete = true,
                Directories = Existing([Path.Combine(windows, "Minidump"), Path.Combine(windows, "LiveKernelReports")]),
                Files = ExistingFiles([Path.Combine(windows, "MEMORY.DMP")]),
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

            // Explorer style, the single title argument: "DataNeu (D:)" with a volume label, "D:\" without one.
            var displayName = string.IsNullOrEmpty(f.VolumeLabel) ? $"{f.Letter}\\" : $"{f.VolumeLabel} ({f.Letter})";

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

    /// <summary>The shader caches that exist. <paramref name="localLow"/> null (the shell could not name the
    /// LocalLow folder) leaves its paths out rather than guessing them.</summary>
    internal static IReadOnlyList<string> ShaderCacheDirectories(string local, string? localLow)
    {
        var candidates = new List<string>
        {
            Path.Combine(local, "D3DSCache"),
            Path.Combine(local, "NVIDIA", "DXCache"),
            Path.Combine(local, "NVIDIA", "GLCache"),
            Path.Combine(local, "NVIDIA Corporation", "NV_Cache"),
            Path.Combine(local, "AMD", "DxCache"),
            Path.Combine(local, "AMD", "DxcCache"),
            Path.Combine(local, "AMD", "VkCache"),
            Path.Combine(local, "AMD", "GLCache"),
            Path.Combine(local, "Intel", "ShaderCache"),
        };

        if (localLow is not null)
        {
            candidates.Add(Path.Combine(localLow, "NVIDIA", "PerDriverVersion", "DXCache"));
            candidates.Add(Path.Combine(localLow, "NVIDIA", "PerDriverVersion", "GLCache"));
        }

        return Existing(candidates);
    }

    /// <summary>The package and build caches that exist. Not here on purpose: the package STORES (NuGet
    /// packages, node_modules, the pnpm content store, Yarn Berry zips, Cargo's unpacked sources), which are
    /// the installed packages themselves or break a build when half emptied.</summary>
    internal static IReadOnlyList<string> PackageCacheDirectories(string local, string profile, string temp) => Existing(
    [
        Path.Combine(local, "pip", "Cache"),
        Path.Combine(local, "npm-cache", "_cacache"),
        Path.Combine(local, "Yarn", "Cache"),
        Path.Combine(local, "NuGet", "v3-cache"),
        Path.Combine(local, "go-build"),
        Path.Combine(local, "pnpm-cache"),
        Path.Combine(local, "uv", "cache"),
        Path.Combine(profile, ".cargo", "registry", "cache"),
        Path.Combine(temp, "chocolatey"),
        Path.Combine(temp, "NuGetScratch"),
    ]);

    /// <summary>Folders inside the package caches that are not cache and stay: pnpm's dlx installs (linked
    /// into place and reused as a whole for a day, so a half-emptied one breaks the next run) and Go's fuzzing
    /// corpus, which <c>go clean -cache</c> keeps as well.</summary>
    internal static IReadOnlyList<string> PackageCacheKeptFolders(string local) => Distinct(
    [
        Path.Combine(local, "pnpm-cache", "dlx"),
        Path.Combine(local, "go-build", "fuzz"),
    ]);

    /// <summary>How long the cleanup waits for uv to clear its cache. Past it uv is not ended (a kill would leave
    /// half a cache): the cleanup stops waiting and counts the folder as not freed.</summary>
    internal static readonly TimeSpan UvCacheCleanTimeout = TimeSpan.FromMinutes(10);

    /// <summary>The uv cache is emptied by uv itself, never file by file: a half-emptied entry would still be
    /// linked into new environments. The folder is passed explicitly so uv cleans exactly the folder that was
    /// measured, whatever its own settings or environment say.</summary>
    internal static IReadOnlyDictionary<string, DirectoryCleanCommand> PackageCacheCleanCommands(string local, string profile)
    {
        var commands = new Dictionary<string, DirectoryCleanCommand>(StringComparer.OrdinalIgnoreCase);
        var uvCache = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(local, "uv", "cache")));
        if (Directory.Exists(uvCache))
        {
            commands[uvCache] = new DirectoryCleanCommand(
                "uv", Path.Combine(profile, ".local", "bin", "uv.exe"),
                ["cache", "clean", "--cache-dir", DirectoryCleanCommand.FolderToken, "--no-config", "--no-progress"],
                "uv cache clean", UvCacheCleanTimeout);
        }

        return commands;
    }

    /// <summary>Like <see cref="Existing"/>, for single files: drops what is not there.</summary>
    private static IReadOnlyList<string> ExistingFiles(IEnumerable<string> paths) =>
        Distinct(paths).Where(File.Exists).ToList();

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
    /// More than one own Temp can exist: with TEMP redirected, <c>Path.GetTempPath()</c> and
    /// <c>%LOCALAPPDATA%\Temp</c> are two different folders, and both belong to the user.
    /// </summary>
    internal static IReadOnlyList<string> ExcludeOwnTemp(IEnumerable<string> candidates, params string[] ownTemps) =>
        ExcludeOwnTemp(candidates, ownTemps, null);

    /// <summary><paramref name="logWarning"/> hears about an own Temp whose identity cannot be read: that
    /// empties the whole list on purpose (fail closed), and the line says why.</summary>
    internal static IReadOnlyList<string> ExcludeOwnTemp(
        IEnumerable<string> candidates, IReadOnlyList<string> ownTemps, Action<string>? logWarning)
    {
        var own = new List<(uint VolumeSerial, ulong FileIndex)>();
        foreach (var ownTemp in ownTemps)
        {
            if (!CleanupSafety.TryReadDirectoryIdentity(ownTemp, out var identity))
            {
                logWarning?.Invoke(
                    $"Cleanup: the all-users Temp list is empty because an own Temp folder could not be read: {ownTemp}");
                return [];
            }

            own.Add(identity);
        }

        return candidates
            .Where(candidate => CleanupSafety.TryReadDirectoryIdentity(candidate, out var id) && !own.Contains(id))
            .ToList();
    }

    /// <summary>
    /// Every OTHER user's Temp folder: this user's own is already the first target. The profile folders come
    /// from the machine's profile list (a profile can live outside the Users folder); a test passes a users
    /// folder to list instead. Deleting inside another user's profile needs elevation; a folder that cannot
    /// be touched is simply skipped at run time.
    /// </summary>
    private static IReadOnlyList<string> OtherUsersTempDirectories(
        IReadOnlyList<string>? callerTempPaths, string? usersRootOverride, Action<string>? logWarning)
    {
        try
        {
            IReadOnlyList<string> profiles;
            if (usersRootOverride is not null)
            {
                if (!Directory.Exists(usersRootOverride))
                    return [];

                profiles = Directory.EnumerateDirectories(usersRootOverride).ToList();
            }
            else
            {
                profiles = ProfileFolders(ProfileListForTest.Value?.Invoke() ?? ReadProfileList());
            }

            // This process's own Temp folders plus the caller's: they differ whenever another account elevated.
            List<string> ownTemps =
            [
                Path.GetFullPath(Path.GetTempPath()),
                Path.GetFullPath(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp")),
            ];
            // A caller path is opened here with admin rights, so it must be a local, link-free folder: a share
            // would make the admin account sign in to it. One that is not leaves nothing to offer (fail closed).
            foreach (var callerTemp in callerTempPaths ?? [])
            {
                if (string.IsNullOrWhiteSpace(callerTemp))
                    continue;

                if (!CleanupSafety.IsLocalPathWithoutLinks(callerTemp))
                {
                    logWarning?.Invoke(
                        $"Cleanup: the all-users Temp list is empty because the caller's Temp is not a local folder: {callerTemp}");
                    return [];
                }

                ownTemps.Add(Path.GetFullPath(callerTemp));
            }

            return Distinct(ExcludeOwnTemp(profiles
                .Where(profile => IsUsableProfileFolder(profile, logWarning))
                .Select(profile => Path.Combine(profile, "AppData", "Local", "Temp"))
                .Where(Directory.Exists),
                ownTemps, logWarning));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // an unreadable users directory (or a malformed caller path) simply means this target has nothing to offer
            return [];
        }
    }

    private static readonly Environment.SpecialFolder[] SystemFolders =
    [
        Environment.SpecialFolder.Windows,
        Environment.SpecialFolder.ProgramFiles,
        Environment.SpecialFolder.ProgramFilesX86,
        Environment.SpecialFolder.CommonApplicationData,
    ];

    /// <summary>
    /// A profile path from the profile list is registry data, read and then walked with admin rights: its
    /// Temp folder is only offered when the profile is a local folder reached through no link (a network
    /// drive would make the admin account sign in to it), not a drive root and not inside Windows, Program
    /// Files or ProgramData. Checked before anything on that path is opened.
    /// </summary>
    internal static bool IsUsableProfileFolder(string profile, Action<string>? logWarning = null)
    {
        string? reason = null;
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profile));
            if (!CleanupSafety.IsLocalPathWithoutLinks(full))
                reason = "not a local folder or reached through a link";
            else if (string.Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full) ?? string.Empty), full, StringComparison.OrdinalIgnoreCase))
                reason = "a drive root";
            else if (SystemFolders
                     .Select(folder => Environment.GetFolderPath(folder))
                     .Where(folder => !string.IsNullOrEmpty(folder))
                     .Any(folder => IsUnder(full, Path.TrimEndingDirectorySeparator(folder))))
                reason = "inside a system folder";
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            reason = "an unusable path";
        }

        if (reason is null)
            return true;

        logWarning?.Invoke($"Cleanup: the profile folder '{profile}' is left out of the all-users Temp list: {reason}.");
        return false;
    }

    private static bool IsUnder(string full, string ancestor) =>
        string.Equals(full, ancestor, StringComparison.OrdinalIgnoreCase)
        || full.StartsWith(ancestor + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>Test seam for the machine's profile list (the registry read is not mockable otherwise): pairs
    /// of account SID and the raw profile path. Per async flow.</summary>
    internal static readonly AsyncLocal<Func<IReadOnlyList<KeyValuePair<string, string?>>>?> ProfileListForTest = new();

    private const string ProfileListKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

    private static IReadOnlyList<KeyValuePair<string, string?>> ReadProfileList()
    {
        try
        {
            using var list = Registry.LocalMachine.OpenSubKey(ProfileListKey);
            if (list is null)
                return [];

            var entries = new List<KeyValuePair<string, string?>>();
            foreach (var sid in list.GetSubKeyNames())
            {
                using var key = list.OpenSubKey(sid);
                var path = key?.GetValue("ProfileImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                entries.Add(new KeyValuePair<string, string?>(sid, path));
            }

            return entries;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    /// <summary>
    /// The profile folders of the real user accounts in a profile list: only local and domain accounts
    /// (SID prefix S-1-5-21-), never the system, service or app-pool profiles. A path may use
    /// %SystemDrive% or %SystemRoot%, read from the Windows folder, not from the environment (a variable the
    /// caller could have changed before an elevated process started); any other variable, a relative or
    /// network path leaves the entry out.
    /// </summary>
    internal static IReadOnlyList<string> ProfileFolders(IEnumerable<KeyValuePair<string, string?>> profileList)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var systemDrive = Path.GetPathRoot(windows)?.TrimEnd('\\') ?? "C:";
        var folders = new List<string>();

        foreach (var (sid, raw) in profileList)
        {
            if (!sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(raw))
                continue;

            var path = raw.Trim()
                .Replace("%SystemDrive%", systemDrive, StringComparison.OrdinalIgnoreCase)
                .Replace("%SystemRoot%", windows, StringComparison.OrdinalIgnoreCase);
            if (path.Contains('%') || path.Length < 4 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\')
                continue;

            try
            {
                folders.Add(Path.GetFullPath(path));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
            }
        }

        return folders;
    }
}
