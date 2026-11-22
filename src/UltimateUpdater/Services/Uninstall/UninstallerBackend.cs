namespace UltimateUpdater.Services.Uninstall;

/// <summary>One installed entry with what the window shows about it: how it would be removed and how much care it deserves.</summary>
public sealed record UninstallListing(InstalledProgramEntry Entry, UninstallPlan Plan, UninstallRisk Risk);

/// <summary>What the uninstaller window needs from the machine, so its view model runs without one in a test.</summary>
public interface IUninstallerBackend
{
    /// <summary>Every installed entry with its plan and rating. Slow (registry, winget, Store packages): never called on the UI thread.</summary>
    Task<IReadOnlyList<UninstallListing>> ListAsync(CancellationToken cancellationToken);

    /// <summary>The runner for one batch; the plan is the one <see cref="ListAsync"/> showed.</summary>
    UninstallRunner CreateRunner(Func<InstalledProgramEntry, UninstallPlan> plan);

    /// <summary>What the removed programs left behind (folders, registry keys, shortcuts); only programs with something left appear. Slow: never called on the UI thread.</summary>
    Task<IReadOnlyList<UninstallLeftoverGroup>> FindLeftoversAsync(
        IReadOnlyList<InstalledProgramEntry> removed, CancellationToken cancellationToken);

    /// <summary>Removes the given leftovers (the ticked ones). Items that could not be removed count as skipped.</summary>
    Task<UninstallLeftoverResult> RemoveLeftoversAsync(
        IReadOnlyList<UninstallLeftoverGroup> groups, CancellationToken cancellationToken);
}

/// <summary>The real machine behind <see cref="IUninstallerBackend"/>.</summary>
public sealed class UninstallerBackend(LoggingService logging, ElevatedHost? elevated, SettingsService? settings = null) : IUninstallerBackend
{
    private readonly UninstallLeftoverService _leftovers = new(
        logging, elevated, settings is null ? null : () => settings.Current.RegistryCleanup);

    public Task<IReadOnlyList<UninstallLeftoverGroup>> FindLeftoversAsync(
        IReadOnlyList<InstalledProgramEntry> removed, CancellationToken cancellationToken) =>
        _leftovers.FindAsync(removed, cancellationToken);

    public Task<UninstallLeftoverResult> RemoveLeftoversAsync(
        IReadOnlyList<UninstallLeftoverGroup> groups, CancellationToken cancellationToken) =>
        _leftovers.RemoveAsync(groups, cancellationToken);

    public Task<IReadOnlyList<UninstallListing>> ListAsync(CancellationToken cancellationToken) => Task.Run(
        async () =>
        {
            var inventory = new InstalledProgramInventory(
                WindowsRegistryView.Instance, new AppxPackageReader(), new WingetListReader());
            var entries = await inventory.ReadAsync(cancellationToken);
            return Rate(entries, new FileSystemUninstallProbe(), Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        },
        cancellationToken);

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
            InstalledProgramEntry entry, UninstallPlan plan, CancellationToken cancellationToken) =>
            Task.FromResult(new UninstallExecutionResult(null, "The elevated helper is not available."));
    }
}
