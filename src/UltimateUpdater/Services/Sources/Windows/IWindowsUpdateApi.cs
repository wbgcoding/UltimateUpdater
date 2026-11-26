namespace UltimateUpdater.Services.Sources.Windows;

/// <summary>One update found by a search: its GUID and revision, plus an opaque handle only the API
/// implementation that produced it can use.</summary>
internal readonly record struct WuUpdateRef(string UpdateId, int Revision, object Handle);

/// <summary>The outcome of one finished install that the caller still needs.</summary>
internal readonly record struct WuInstallOutcome(bool RebootRequired);

/// <summary>One search result. Owns every COM object behind its updates, so the updates stay usable for as
/// long as it lives and are released together when it is disposed.</summary>
internal interface IWuSearchResult : IDisposable
{
    IReadOnlyList<WuUpdateRef> Updates { get; }
}

/// <summary>
/// The Windows Update agent as <see cref="WindowsUpdateSource"/> uses it for an install: search, accept the
/// licence, download, install. A seam so the install logic (which update is picked, how often a search runs,
/// what "already installed" means, how a Stop ends a transfer) is unit-testable without the real service,
/// which a test must never touch. The real implementation is <see cref="ComWindowsUpdateApi"/>.
/// </summary>
internal interface IWindowsUpdateApi
{
    /// <summary>One search with the given criteria. A broken service throws, an empty answer returns an empty list.</summary>
    IWuSearchResult Search(string criteria, CancellationToken cancellationToken);

    /// <summary>Best effort: a licence that cannot be accepted is logged, the install goes on.</summary>
    void AcceptEula(WuUpdateRef update);

    /// <summary>Downloads the update. Honours the token: a Stop aborts the transfer and throws
    /// <see cref="OperationCanceledException"/>. A failed or aborted download throws with the stage's
    /// English result text.</summary>
    void Download(WuUpdateRef update, CancellationToken cancellationToken);

    /// <summary>Installs the (already downloaded) update; same cancellation and failure contract as
    /// <see cref="Download"/>.</summary>
    WuInstallOutcome Install(WuUpdateRef update, CancellationToken cancellationToken);
}
