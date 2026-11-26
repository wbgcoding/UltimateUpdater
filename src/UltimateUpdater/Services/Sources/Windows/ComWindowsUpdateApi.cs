using System.Runtime.InteropServices;
using static UltimateUpdater.Services.Sources.Windows.WindowsUpdateSource;

namespace UltimateUpdater.Services.Sources.Windows;

/// <summary>
/// The real <see cref="IWindowsUpdateApi"/>: the in-box WUApiLib COM objects, late-bound through IDispatch
/// like the rest of <see cref="WindowsUpdateSource"/>.
/// </summary>
internal sealed class ComWindowsUpdateApi(LoggingService logging) : IWindowsUpdateApi
{
    /// <summary>What a search hands out for each update: the update plus the session that found it, which
    /// the later download and install calls reuse.</summary>
    private sealed record ComUpdateHandle(object Session, object Update);

    private sealed class ComSearchResult(ComScope com, IReadOnlyList<WuUpdateRef> updates) : IWuSearchResult
    {
        public IReadOnlyList<WuUpdateRef> Updates { get; } = updates;

        public void Dispose() => com.Dispose();
    }

    public IWuSearchResult Search(string criteria, CancellationToken cancellationToken)
    {
        var com = new ComScope();
        try
        {
            var session = com.Track(CreateComObject("Microsoft.Update.Session"))
                ?? throw new InvalidOperationException("Windows Update session unavailable.");

            var searcher = com.Track(Invoke(session, "CreateUpdateSearcher"))
                ?? throw new InvalidOperationException("Windows Update searcher unavailable.");

            var searchResult = com.Track(Invoke(searcher, "Search", criteria))
                ?? throw new InvalidOperationException("Windows Update search failed.");

            var updates = com.Track(GetProperty(searchResult, "Updates"));
            var count = Convert.ToInt32(GetProperty(updates, "Count") ?? 0);

            var found = new List<WuUpdateRef>(count);
            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var update = com.Track(InvokeGetItem(updates, i));
                if (update is null)
                    continue;

                var identity = com.Track(GetProperty(update, "Identity"));
                var updateId = GetProperty(identity, "UpdateID") as string;
                var revision = Convert.ToInt32(GetProperty(identity, "RevisionNumber") ?? 0);

                if (updateId is not null)
                    found.Add(new WuUpdateRef(updateId, revision, new ComUpdateHandle(session, update)));
            }

            return new ComSearchResult(com, found);
        }
        catch
        {
            com.Dispose();
            throw;
        }
    }

    public void AcceptEula(WuUpdateRef update)
    {
        var handle = (ComUpdateHandle)update.Handle;
        try
        {
            if (GetProperty(handle.Update, "EulaAccepted") is bool accepted && !accepted)
                Invoke(handle.Update, "AcceptEula");
        }
        catch (COMException ex)
        {
            logging.Info($"windowsupdate: AcceptEula failed, install continues unaccepted: {ex.Message}");
        }
    }

    public void Download(WuUpdateRef update, CancellationToken cancellationToken)
    {
        var handle = (ComUpdateHandle)update.Handle;
        using var com = new ComScope();

        var collection = com.Track(CreateComObject("Microsoft.Update.UpdateColl"))
            ?? throw new InvalidOperationException("Could not create update collection.");
        Invoke(collection, "Add", handle.Update);

        var downloader = com.Track(Invoke(handle.Session, "CreateUpdateDownloader"))
            ?? throw new InvalidOperationException("Windows Update downloader unavailable.");
        SetProperty(downloader, "Updates", collection);

        // Checked like the install result: a failed download (network, WSUS refusal) would otherwise go on to
        // Install and surface as a misleading install-stage code instead of the download's own HRESULT.
        var downloadResult = com.Track(Invoke(downloader, "Download"))
            ?? throw new InvalidOperationException("Windows Update download returned no result.");
        VerifySucceeded(downloadResult, com, "download");
    }

    public WuInstallOutcome Install(WuUpdateRef update, CancellationToken cancellationToken)
    {
        var handle = (ComUpdateHandle)update.Handle;
        using var com = new ComScope();

        var collection = com.Track(CreateComObject("Microsoft.Update.UpdateColl"))
            ?? throw new InvalidOperationException("Could not create update collection.");
        Invoke(collection, "Add", handle.Update);

        var installer = com.Track(Invoke(handle.Session, "CreateUpdateInstaller"))
            ?? throw new InvalidOperationException("Windows Update installer unavailable.");
        SetProperty(installer, "Updates", collection);

        WaitWhileBusy(installer, cancellationToken);

        if (GetProperty(installer, "RebootRequiredBeforeInstallation") is true)
            throw new InvalidOperationException("A restart is pending before this update can install.");

        var installResult = com.Track(Invoke(installer, "Install"))
            ?? throw new InvalidOperationException("Windows Update install returned no result.");

        VerifySucceeded(installResult, com, "install");

        return new WuInstallOutcome(GetProperty(installResult, "RebootRequired") is true);
    }
}
