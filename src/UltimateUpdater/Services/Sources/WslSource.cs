using UltimateUpdater.Models;

namespace UltimateUpdater.Services.Sources;

/// <summary>
/// WSL, detection only. `wsl --update` exists, but nothing short of running it reports whether an
/// update is pending: there is no check flag. An invented "update available" row would be worse than
/// none, so the scan always reports nothing and the source only records that WSL is present.
/// No uninstall: `wsl --unregister` deletes the distribution's entire filesystem, far too destructive
/// for a context-menu entry that looks like every other "remove" item.
/// </summary>
public sealed class WslSource(LoggingService logging) : CliUpdateSource(logging)
{
    public override string Id => "wsl";

    public override UpdateCategory Category => UpdateCategory.SystemAndDrivers;

    protected override string Executable => "wsl";

    /// <summary>
    /// `wsl --version` running at all is the only presence signal available; a bare exe on PATH is not
    /// enough: a Windows without the feature enabled still ships the stub. Deliberately given its OWN
    /// short timeout instead of the two-minute scan timeout: this probe runs at startup, where a wsl.exe
    /// that sits there thinking held the whole window back and the app came up as a tray icon only
    /// An unanswered probe is treated as "not present" rather than delaying startup.
    /// </summary>
    private static readonly TimeSpan PresenceProbeTimeout = TimeSpan.FromSeconds(3);

    public override async Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        if (!ProcessRunner.Exists(Executable))
            return false;

        var result = await ProcessRunner.RunAsync(
            Executable, ["--version"], PresenceProbeTimeout, cancellationToken);
        return !result.TimedOut && result.ExitCode == 0;
    }

    protected override Task<IReadOnlyList<UpdateItem>> ScanCoreAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<UpdateItem>>([]);

    protected override IReadOnlyList<string> InstallArguments(UpdateItem item) => ["--update"];
}
