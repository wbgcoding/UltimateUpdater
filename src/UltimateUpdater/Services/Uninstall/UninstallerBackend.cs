using System.IO;
using UltimateUpdater.Models.Settings;
using UltimateUpdater.Services.Branding;
using UltimateUpdater.Services.Cleanup;
namespace UltimateUpdater.Services.Uninstall;

/// <summary>One installed entry with what the window shows about it: how it would be removed and how much care it deserves.</summary>
public sealed record UninstallListing(InstalledProgramEntry Entry, UninstallPlan Plan, UninstallRisk Risk);

/// <summary>The saved listing of the last full read, rated again with today's rules, and when it was saved.</summary>
public sealed record UninstallCachedListing(IReadOnlyList<UninstallListing> Listings, DateTimeOffset SavedAt);

/// <summary>What the uninstaller window needs from the machine, so its view model runs without one in a test.</summary>
public interface IUninstallerBackend
{
    /// <summary>Every installed entry with its plan and rating. Slow (registry, winget, Store packages): never called on the UI thread.</summary>
    Task<IReadOnlyList<UninstallListing>> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The listing for opening the window: <see cref="ListAsync"/>, or the result of a read the app already did in the
    /// background a moment ago. A reload button always asks <see cref="ListAsync"/>, for a fresh read.
    /// </summary>
    Task<IReadOnlyList<UninstallListing>> ListForOpenAsync(CancellationToken cancellationToken) => ListAsync(cancellationToken);

    /// <summary>The saved listing of the last full read, or null when there is none or it cannot be read. Never throws.</summary>
    Task<UninstallCachedListing?> LoadCacheAsync(CancellationToken cancellationToken) => Task.FromResult<UninstallCachedListing?>(null);

    /// <summary>The cheap first check of the saved rows: the ones that are certainly gone are dropped, nothing is added.</summary>
    Task<IReadOnlyList<UninstallListing>> RecheckAsync(IReadOnlyList<UninstallListing> cached, CancellationToken cancellationToken) =>
        Task.FromResult(cached);

    /// <summary>Saves the entries as the listing of the last full read. Never throws; a listing that cannot be saved is only a missed head start.</summary>
    Task SaveCacheAsync(IReadOnlyList<InstalledProgramEntry> entries) => Task.CompletedTask;

    /// <summary>The runner for one batch; the plan is the one <see cref="ListAsync"/> showed.</summary>
    UninstallRunner CreateRunner(Func<InstalledProgramEntry, UninstallPlan> plan);

    /// <summary>What the removed programs left behind (folders, registry keys, shortcuts); only programs with something left appear. Slow: never called on the UI thread.</summary>
    Task<IReadOnlyList<UninstallLeftoverGroup>> FindLeftoversAsync(
        IReadOnlyList<InstalledProgramEntry> removed, CancellationToken cancellationToken);

    /// <summary>Removes the given leftovers (the ticked ones). Items that could not be removed count as skipped.</summary>
    Task<UninstallLeftoverResult> RemoveLeftoversAsync(
        IReadOnlyList<UninstallLeftoverGroup> groups, CancellationToken cancellationToken);

    /// <summary>The setting that decides when a run gets a restore point.</summary>
    UninstallBackupMode BackupMode { get; }

    /// <summary>Creates the restore point of a run, or reports the one within the last 24 hours that covers it. Slow, may raise the UAC prompt.</summary>
    Task<UninstallRestorePointResult> CreateRestorePointAsync(CancellationToken cancellationToken);

    /// <summary>Writes the history line of an entry the run removed.</summary>
    void RecordRemoved(InstalledProgramEntry entry);

    /// <summary>The icon of an entry (its app's, its Store logo), or null when it has none and the row shows its kind's symbol. Reads files: never called on the UI thread.</summary>
    System.Windows.Media.Imaging.BitmapSource? ResolveIcon(InstalledProgramEntry entry) => null;
}

/// <summary>The real machine behind <see cref="IUninstallerBackend"/>.</summary>
public sealed class UninstallerBackend(
    LoggingService logging, ElevatedHost? elevated, SettingsService? settings = null, HistoryService? history = null,
    UninstallListingPrefetch? prefetch = null, IconCacheService? icons = null) : IUninstallerBackend
{
    public System.Windows.Media.Imaging.BitmapSource? ResolveIcon(InstalledProgramEntry entry) =>
        icons?.GetEntryIcon(
            entry.Key, entry.Name, entry.DisplayIcon, entry.InstallLocation, entry.Kind == UninstallEntryKind.StoreApp,
            UninstallEntryOrigin.SteamAppId(entry));

    private readonly UninstallLeftoverService _leftovers = new(
        logging, elevated, settings is { } current ? () => current.Current.RegistryCleanup : null);

    private readonly UninstallRestorePointService _restorePoint = new(logging, elevated);

    public UninstallBackupMode BackupMode => settings?.Current.Install.UninstallBackup ?? UninstallBackupMode.Automatic;

    public Task<UninstallRestorePointResult> CreateRestorePointAsync(CancellationToken cancellationToken) =>
        _restorePoint.EnsureAsync(cancellationToken);

    public void RecordRemoved(InstalledProgramEntry entry) => history?.Append(HistoryForRemoved(entry));

    /// <summary>The history line of a removed entry: the program's own version, no target version, no download size.</summary>
    internal static HistoryEntry HistoryForRemoved(InstalledProgramEntry entry) => new()
    {
        Timestamp = DateTimeOffset.Now,
        SourceId = "uninstaller",
        ItemId = entry.Key,
        Name = entry.Name,
        FromVersion = string.IsNullOrWhiteSpace(entry.Version) ? null : entry.Version,
        SizeBytes = entry.SizeBytes,
        Success = true,
        Action = "uninstall",
    };

    public Task<IReadOnlyList<UninstallLeftoverGroup>> FindLeftoversAsync(
        IReadOnlyList<InstalledProgramEntry> removed, CancellationToken cancellationToken) =>
        _leftovers.FindAsync(removed, cancellationToken);

    public async Task<UninstallLeftoverResult> RemoveLeftoversAsync(
        IReadOnlyList<UninstallLeftoverGroup> groups, CancellationToken cancellationToken)
    {
        var result = await _leftovers.RemoveAsync(groups, cancellationToken);
        foreach (var quarantined in result.QuarantinedOrEmpty)
            history?.Append(HistoryForLeftovers(quarantined));

        return result;
    }

    /// <summary>The history line of one program's removed leftovers: it keeps the quarantine folder names so the history can restore them.</summary>
    internal static HistoryEntry HistoryForLeftovers(QuarantinedLeftovers quarantined) => new()
    {
        Timestamp = DateTimeOffset.Now,
        SourceId = "uninstaller",
        ItemId = quarantined.Program,
        Name = quarantined.Program,
        Success = true,
        Action = "leftovers",
        QuarantineUser = quarantined.UserFolder,
        QuarantineMachine = quarantined.MachineFolder,
    };

    public Task<UninstallCachedListing?> LoadCacheAsync(CancellationToken cancellationToken) => Task.Run(
        () =>
        {
            var content = UninstallCache.Default().Load();
            return content is null
                ? null
                : new UninstallCachedListing(Rate(content.Entries, new FileSystemUninstallProbe(), WindowsDirectory), content.SavedAt);
        },
        cancellationToken);

    public Task<IReadOnlyList<UninstallListing>> RecheckAsync(IReadOnlyList<UninstallListing> cached, CancellationToken cancellationToken) =>
        Task.Run(
            () =>
            {
                var kept = UninstallCacheRecheck.Keep(
                    [.. cached.Select(listing => listing.Entry)], WindowsRegistryView.Instance, WindowsDirectory, File.Exists);
                var keptKeys = kept.Select(entry => entry.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                return (IReadOnlyList<UninstallListing>)[.. cached.Where(listing => keptKeys.Contains(listing.Entry.Key))];
            },
            cancellationToken);

    public Task SaveCacheAsync(IReadOnlyList<InstalledProgramEntry> entries) => Task.Run(
        () =>
        {
            if (!UninstallCache.Default().Save(entries))
                logging.Warn("Uninstaller: the listing could not be saved for the next start.");
        });

    private static string WindowsDirectory => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    /// <summary>The background read of this session when it is running or fresh, else a read of its own.</summary>
    public async Task<IReadOnlyList<UninstallListing>> ListForOpenAsync(CancellationToken cancellationToken) =>
        (prefetch is null ? null : await prefetch.TryTakeAsync(cancellationToken)) ?? await ListAsync(cancellationToken);

    public Task<IReadOnlyList<UninstallListing>> ListAsync(CancellationToken cancellationToken) => Task.Run(
        async () =>
        {
            // The hotfix list (WMI, names the update rows) is the slowest single read: it runs beside the rest.
            var fixes = Task.Run(() => new WindowsUpdateLister(logging).ReadQuickFixes(cancellationToken), cancellationToken);
            var inventory = new InstalledProgramInventory(
                WindowsRegistryView.Instance, new AppxPackageReader(), new WingetListReader());
            var entries = UninstallEntryOrigin.MarkEmptyFolders(await inventory.ReadAsync(cancellationToken), UninstallEntryOrigin.IsEmptyFolder);
            entries =
            [
                .. entries,
                .. DriverEntries(new SetupApiDriverInventoryProvider(logging.Warn), cancellationToken),
                .. UpdateEntries(WindowsRegistryView.Instance, await fixes, key => LocalizationService.Instance[key]),
            ];
            return Rate(entries, new FileSystemUninstallProbe(), WindowsDirectory);
        },
        cancellationToken);

    /// <summary>The installed Windows updates as entries, read from the registry (no elevation, no DISM). The elevated
    /// worker checks permanence before it removes one.</summary>
    internal static IReadOnlyList<InstalledProgramEntry> UpdateEntries(
        IRegistryView registry, IReadOnlyList<QuickFix> quickFixes, Func<string, string> text) =>
        WindowsUpdateDiscovery.ToEntries(WindowsUpdateRegistryReader.Read(registry), quickFixes, text);

    /// <summary>The third-party driver packages as entries. A driver inventory that cannot be read lists no driver at
    /// all (never a guess about which are in use).</summary>
    internal IReadOnlyList<InstalledProgramEntry> DriverEntries(IDriverInventoryProvider provider, CancellationToken cancellationToken)
    {
        try
        {
            var devices = provider.GatherDevices(cancellationToken);
            var packages = provider.GatherPackages(cancellationToken);
            return [.. DriverUninstallDiscovery.List(devices, packages).Select(DriverUninstallDiscovery.ToEntry)];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logging.Warn($"Driver list failed: {ex.Message}");
            return [];
        }
    }

    /// <summary>Plans and rates every entry. Pure apart from the probe the planner reads installers with.</summary>
    internal static IReadOnlyList<UninstallListing> Rate(
        IReadOnlyList<InstalledProgramEntry> entries, IUninstallFileProbe probe, string windowsDirectory)
    {
        var listings = new List<UninstallListing>(entries.Count);
        foreach (var entry in entries)
        {
            var plan = UninstallPlanner.Plan(entry, probe);
            var risk = UninstallRiskClassifier.Classify(new UninstallRiskInput
            {
                Kind = entry.Kind,
                DisplayName = entry.Name,
                Publisher = entry.Publisher,
                PackageFamilyName = entry.PackageFamilyName,
                Scope = entry.Scope,
                Method = plan.Method,
                UninstallKeyName = KeyName(entry.Key),
                InstallLocation = entry.InstallLocation,
                WindowsDirectory = windowsDirectory,
            });
            listings.Add(new UninstallListing(entry, plan, risk));
        }

        return listings;
    }

    private static string KeyName(string key)
    {
        var split = key.LastIndexOf('\\');
        return split >= 0 ? key[(split + 1)..] : key;
    }

    public UninstallRunner CreateRunner(Func<InstalledProgramEntry, UninstallPlan> plan)
    {
        var gone = new UninstallGoneCheck(WindowsRegistryView.Instance, new AppxPackageReader());
        IUninstallExecutor machine = elevated is null
            ? new UnavailableExecutor()
            : new ElevatedUninstallExecutor(elevated, logging);
        return new UninstallRunner(
            plan, machine, new InProcessUninstallExecutor(logging), gone.IsGoneAsync,
            key => LocalizationService.Instance[key]);
    }

    /// <summary>No elevated helper in this process (a test host): a machine removal fails with the usual "could not start" reason.</summary>
    private sealed class UnavailableExecutor : IUninstallExecutor
    {
        public Task<UninstallExecutionResult> RunAsync(
            InstalledProgramEntry entry, UninstallPlan plan, CancellationToken cancellationToken, CancellationToken stopWhenIdle) =>
            Task.FromResult(new UninstallExecutionResult(null, "The elevated helper is not available."));
    }
}
