using System.Runtime.InteropServices;
using static UltimateUpdater.Services.Sources.Windows.WindowsUpdateSource;

namespace UltimateUpdater.Services.Sources.Windows;

/// <summary>
/// The real <see cref="IWindowsUpdateApi"/>: the in-box WUApiLib COM objects, late-bound through IDispatch
/// like the rest of <see cref="WindowsUpdateSource"/>. Download and install run as WUA's asynchronous
/// <c>Begin*</c>/<c>End*</c> pair, polled through <see cref="WuAsyncRunner"/>, so a Stop can abort them.
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

    public IWuSearchResult Search(string criteria, CancellationToken cancellationToken, bool offline = false)
    {
        var com = new ComScope();
        try
        {
            var session = com.Track(CreateComObject("Microsoft.Update.Session"))
                ?? throw new InvalidOperationException("Windows Update session unavailable.");

            var searcher = com.Track(Invoke(session, "CreateUpdateSearcher"))
                ?? throw new InvalidOperationException("Windows Update searcher unavailable.");

            // Online = false: the search reads the local datastore instead of asking the update service, so a
            // question about installed updates neither waits for the network nor hits the service.
            if (offline)
                SetProperty(searcher, "Online", false);

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
        var downloadResult = com.Track(RunOperation(downloader, "BeginDownload", "EndDownload", "Download", com, cancellationToken))
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

        var installResult = com.Track(RunOperation(installer, "BeginInstall", "EndInstall", "Install", com, cancellationToken))
            ?? throw new InvalidOperationException("Windows Update install returned no result.");

        VerifySucceeded(installResult, com, "install");

        return new WuInstallOutcome(GetProperty(installResult, "RebootRequired") is true);
    }

    /// <summary>
    /// Starts the operation asynchronously (no callback objects: the job's own <c>IsCompleted</c> is polled)
    /// and drives it through <see cref="WuAsyncRunner"/>. Should the late-bound begin call be refused, the
    /// synchronous call runs instead, exactly as before: a Stop then only lands between stages, which beats
    /// failing the install over the way it is polled.
    /// </summary>
    private object? RunOperation(object operation, string beginMethod, string endMethod, string syncMethod,
        ComScope com, CancellationToken cancellationToken)
    {
        var job = TryBegin(operation, beginMethod);
        if (job is null)
            return Invoke(operation, syncMethod);

        com.Track(job);
        return WuAsyncRunner.Run(new ComAsyncJob(operation, endMethod, job), cancellationToken, log: logging.Info);
    }

    private object? TryBegin(object operation, string beginMethod)
    {
        // Two spellings of "no callback": a typed null interface pointer first (what the IUnknown parameters
        // want), a plain null second.
        object?[][] attempts =
        [
            [new UnknownWrapper(null), new UnknownWrapper(null), null],
            [null, null, null],
        ];

        foreach (var args in attempts)
        {
            try
            {
                if (Invoke(operation, beginMethod, args) is { } job)
                    return job;
            }
            catch (Exception ex) when (ex is COMException or ArgumentException or MissingMethodException or NotSupportedException)
            {
                logging.Info($"windowsupdate: {beginMethod} refused a call form ({ex.GetType().Name} 0x{ex.HResult:X8}).");
            }
        }

        logging.Warn($"windowsupdate: {beginMethod} is unavailable, using the blocking call (a Stop lands between stages only).");
        return null;
    }

    private sealed class ComAsyncJob(object operation, string endMethod, object job) : IWuAsyncJob
    {
        public bool IsCompleted => GetProperty(job, "IsCompleted") is true;

        public void RequestAbort() => Invoke(job, "RequestAbort");

        public object? End() => Invoke(operation, endMethod, job);
    }
}
