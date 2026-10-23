namespace UltimateUpdater.Services.Sources;

/// <summary>
/// Named timeouts shared by scan-time external calls (HTTP requests, CLI subprocess runs) that
/// independently picked the same value for the same reason. A site with a genuinely
/// different concern keeps its own value: <c>ProviderStatusViewModel.SlowThreshold</c> (a UI "mark this
/// provider as slow" threshold, not a call timeout), <c>WindowsUpdateSource</c>'s busy-wait retry interval,
/// and <c>CliUpdateSource.DefaultScanTimeout</c>/<c>DefaultInstallTimeout</c> (the CLI-package-manager
/// family's own already-centralized constants: a different source family, kept separate on purpose) are
/// all untouched by this.
/// </summary>
internal static class ExternalCallTimeouts
{
    /// <summary>Default timeout for one external process/HTTP call a source makes while scanning.</summary>
    internal static readonly TimeSpan Default = TimeSpan.FromSeconds(15);

    /// <summary>Longer timeout for a call known to sometimes take a while (a slower CLI tool, a larger response).</summary>
    internal static readonly TimeSpan Long = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a request may spend reaching a configured proxy and getting its response headers back
    /// before the direct route is tried instead. Well under <see cref="Default"/>, which is what most
    /// callers cancel at: a proxy that silently drops the connection must leave time for the direct retry.
    /// </summary>
    internal static readonly TimeSpan ProxyAttempt = TimeSpan.FromSeconds(8);

    /// <summary>
    /// How long a <see cref="CliUpdateSource"/> scan may go with NO stdout/stderr activity at all before
    /// it is judged hung rather than merely slow (a source is judged by silence, not by total runtime).
    /// Wired into <see cref="CliUpdateSource.RunAsync"/> through
    /// <see cref="CliUpdateSource.ScanIdleTimeout"/>, which a subclass overrides only for a real, measured
    /// reason; the overall <see cref="CliUpdateSource.ScanTimeout"/> stays the true worst-case backstop.
    /// </summary>
    internal static readonly TimeSpan ScanIdle = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Overall deadline for a single source's whole scan/lookup before it is abandoned; the true worst
    /// case a source can ever take, now that a source going quiet is caught much sooner by
    /// <see cref="ProcessRunner"/>'s own idle timeout. Raised from the original 2 minutes: a source
    /// that keeps genuinely producing output, however slowly (a big update list legitimately takes
    /// minutes), must not be judged by total runtime any more.
    /// </summary>
    internal static readonly TimeSpan ScanDeadline = TimeSpan.FromMinutes(10);
}
