using UltimateUpdater.Models;

namespace UltimateUpdater.Tests;

/// <summary>
/// A scripted <see cref="IWindowsUpdateApi"/>: every search answers from a per-criteria list, and every call
/// is recorded, so the install logic can be tested without the real Windows Update service (which a test
/// must never touch).
/// </summary>
internal sealed class FakeWindowsUpdateApi : IWindowsUpdateApi
{
    private readonly Dictionary<string, List<WuUpdateRef>> _answers = new(StringComparer.Ordinal);

    public List<string> SearchCriteria { get; } = [];

    /// <summary>The criteria of the searches that asked for the local datastore only.</summary>
    public List<string> OfflineSearches { get; } = [];

    public List<string> Calls { get; } = [];

    /// <summary>How many search results were disposed (their COM objects released).</summary>
    public int DisposedResults { get; private set; }

    public Func<WuUpdateRef, CancellationToken, WuInstallOutcome>? OnInstall { get; set; }

    public Action<WuUpdateRef, CancellationToken>? OnDownload { get; set; }

    private readonly Dictionary<string, Exception> _failures = new(StringComparer.Ordinal);

    /// <summary>Sets what the NEXT searches with this criteria return (replaces any earlier answer).</summary>
    public void Offer(string criteria, params (string Id, int Revision)[] updates) =>
        _answers[criteria] = updates.Select(u => new WuUpdateRef(u.Id, u.Revision, u)).ToList();

    /// <summary>Makes searches with this criteria throw; null lifts the failure again.</summary>
    public void FailSearch(string criteria, Exception? failure)
    {
        if (failure is null)
            _failures.Remove(criteria);
        else
            _failures[criteria] = failure;
    }

    public IWuSearchResult Search(string criteria, CancellationToken cancellationToken, bool offline = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SearchCriteria.Add(criteria);
        if (offline)
            OfflineSearches.Add(criteria);

        if (_failures.TryGetValue(criteria, out var failure))
            throw failure;

        return new Result(_answers.GetValueOrDefault(criteria) ?? [], this);
    }

    public void AcceptEula(WuUpdateRef update) => Calls.Add("eula:" + update.UpdateId);

    public void Download(WuUpdateRef update, CancellationToken cancellationToken)
    {
        Calls.Add("download:" + update.UpdateId);
        OnDownload?.Invoke(update, cancellationToken);
    }

    public WuInstallOutcome Install(WuUpdateRef update, CancellationToken cancellationToken)
    {
        Calls.Add("install:" + update.UpdateId);
        return OnInstall?.Invoke(update, cancellationToken) ?? new WuInstallOutcome(false);
    }

    private sealed class Result(List<WuUpdateRef> updates, FakeWindowsUpdateApi owner) : IWuSearchResult
    {
        public IReadOnlyList<WuUpdateRef> Updates { get; } = updates;

        public void Dispose() => owner.DisposedResults++;
    }
}

/// <summary>An <see cref="IProgress{T}"/> that records synchronously (<see cref="Progress{T}"/> posts).</summary>
internal sealed class RecordingProgress : IProgress<InstallProgress>
{
    public List<InstallProgress> Reports { get; } = [];

    public void Report(InstallProgress value) => Reports.Add(value);
}
