using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using UltimateUpdater.Models;
using UltimateUpdater.Services;

namespace UltimateUpdater.Services.Sources;

/// <summary>
/// Tier A: Windows Update via the in-box WUApiLib COM API. Late-bound through
/// IDispatch so the app needs no COM reference or interop assembly; the API ships with Windows.
/// Searching works unelevated; DOWNLOADING/INSTALLING requires admin, so installs only ever run
/// inside the elevated worker. Only quality/driver/definition updates are offered; never feature
/// upgrades.
/// No uninstall: removing an installed Windows Update has no equivalent in this API and
/// would need a registry `UninstallString` fallback, which is out of scope for this app on principle;
/// an arbitrary registry command is not something to trust without knowing what it actually runs.
/// </summary>
public sealed class WindowsUpdateSource : IUpdateSource
{
    private const string SearchCriteria = "IsInstalled=0 and IsHidden=0";

    private readonly LoggingService _logging;

    public WindowsUpdateSource(LoggingService logging)
    {
        _logging = logging;
    }

    public string Id => "windowsupdate";

    public UpdateCategory Category => UpdateCategory.SystemAndDrivers;

    public SourceTier Tier => SourceTier.A;

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Type.GetTypeFromProgID("Microsoft.Update.Session") is not null);

    public async IAsyncEnumerable<UpdateItem> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // The COM search is a long blocking call; keep it off the caller's thread. It only checks the token
        // between results, so Stop waits on the task rather than on the search: an abandoned search finishes
        // in the background and its fault, if any, is observed instead of going unnoticed.
        var search = Task.Run(() => Search(cancellationToken), cancellationToken);
        _ = search.ContinueWith(t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        var items = await search.WaitAsync(cancellationToken);

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
        }
    }

    public Task InstallAsync(UpdateItem item, IProgress<Models.InstallProgress> progress, CancellationToken cancellationToken)
    {
        if (!ElevationService.IsElevated)
            throw new InvalidOperationException("Windows Update installs require elevation.");

        return Task.Run(() => Install(item, progress, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// No COM call of its own: Description/MoreInfoUrls were already captured into Meta during the scan
    /// (see Search/TryMapUpdate); a fresh WUA session plus a full re-search per row here, the only way to
    /// look an update up again by id, would have multiplied the scan's own cost by the row count.
    /// </summary>
    public Task<UpdateEnrichment?> EnrichAsync(UpdateItem item, CancellationToken cancellationToken)
    {
        item.Meta.TryGetValue("description", out var description);
        item.Meta.TryGetValue("moreInfoUrl", out var moreInfoUrl);

        if (description is null && moreInfoUrl is null)
            return Task.FromResult<UpdateEnrichment?>(null);

        var body = description is null ? null : ChangelogFormatter.Strip(description);
        return Task.FromResult<UpdateEnrichment?>(new UpdateEnrichment(null, null, ChangelogBody: body, ChangelogUrl: moreInfoUrl));
    }

    /// <summary>
    /// The search. A COM failure here PROPAGATES: catching it and returning an empty list would let the
    /// orchestrator read it as a clean scan, so a Windows Update service that is broken or switched off
    /// would look exactly like a machine with nothing to install. A source that cannot answer must
    /// say so (the pill goes red), never report "nothing found".
    /// </summary>
    private List<UpdateItem> Search(CancellationToken cancellationToken)
    {
        var results = new List<UpdateItem>();

        // Every RCW this method creates, released in reverse order; releasing only the session would leave
        // a scan's searcher/result/collection/update/identity objects to accumulate for the app's lifetime.
        using var com = new ComScope();

        var session = com.Track(CreateComObject("Microsoft.Update.Session"))
            ?? throw new InvalidOperationException("Windows Update session unavailable.");

        var searcher = com.Track(Invoke(session, "CreateUpdateSearcher"))
            ?? throw new InvalidOperationException("Windows Update searcher unavailable.");

        var searchResult = com.Track(Invoke(searcher, "Search", SearchCriteria))
            ?? throw new InvalidOperationException("Windows Update search returned nothing at all.");

        var updates = com.Track(GetProperty(searchResult, "Updates"));
        var count = Convert.ToInt32(GetProperty(updates, "Count") ?? 0);

        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var update = com.Track(InvokeGetItem(updates, i));
            if (update is null)
                continue;

            var item = TryMapUpdate(update, com);
            if (item is not null)
                results.Add(item);
        }

        return results;
    }

    internal UpdateItem? TryMapUpdate(object update, ComScope com)
    {
        try
        {
            var title = GetProperty(update, "Title") as string ?? string.Empty;
            if (title.Length == 0)
                return null;

            var (categoryIds, categoriesReadSuccessfully) = GetCategoryIds(update, com);

            // Feature upgrades are explicitly out of scope; they are not what this tool installs.
            if (IsFeatureUpgrade(categoryIds, categoriesReadSuccessfully))
                return null;

            var identity = com.Track(GetProperty(update, "Identity"));
            var updateId = GetProperty(identity, "UpdateID") as string ?? title;
            var revision = Convert.ToInt32(GetProperty(identity, "RevisionNumber") ?? 0);

            // Windows Update classifies its own updates; that verdict is kept as it comes. But WUA
            // frequently leaves MsrcSeverity blank on cumulative/dynamic security updates even though
            // they carry the "Security Updates" classification; a missing rating is not the same as a
            // low one, so a security update with no severity is still marked critical. The identifier
            // table the other sources use is never consulted here (SecurityRelevance).
            var severity = GetProperty(update, "MsrcSeverity") as string;
            var isCritical = SecurityRelevance.IsCriticalSeverity(severity)
                || (string.IsNullOrEmpty(severity) && categoryIds.Contains(SecurityUpdatesCategoryId));

            var meta = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(severity))
                meta["msrcSeverity"] = severity;
            if (GetProperty(update, "RebootRequired") is bool reboot && reboot)
                meta["rebootRequired"] = "true";

            // Captured here, off the same COM object already open for Title/Categories/severity above;
            // EnrichAsync then reads these straight out of Meta with no COM call of its own. Re-querying
            // WUA per row from EnrichAsync would mean a fresh session + a full re-search for every single
            // item (there is no "get update by id" lookup), the exact per-row-COM-call cost this project
            // already measured and rejected once for winget scanning (5s module vs 190s per-row COM).
            if (GetProperty(update, "Description") is string description && !string.IsNullOrWhiteSpace(description))
                meta["description"] = description;
            if (ReadFirstMoreInfoUrl(update, com) is { } moreInfoUrl)
                meta["moreInfoUrl"] = moreInfoUrl;

            if (IsDriverUpdate(update))
            {
                meta["isDriverUpdate"] = "true";
                if (ReadDriverManufacturer(update) is { } manufacturer && !string.IsNullOrWhiteSpace(manufacturer))
                    meta["driverManufacturer"] = manufacturer;
            }

            return new UpdateItem
            {
                Id = $"{updateId}:{revision}",
                SourceId = Id,
                Category = Category,
                Name = title,
                InstalledVersion = null,
                // WUApiLib never reports a version number itself. Most titles carry one anyway
                // ("... Version 32.0.15.6094"), so try that before falling back to the KB number
                // (the one identifier every title has) rather than showing nothing at all.
                AvailableVersion = PackageManagerParsers.FirstVersionToken(title) ?? ExtractKbNumber(title),
                IsCritical = isCritical,
                // Windows Update is one of the few sources that knows the size and date up front.
                DownloadSize = DownloadSizeFor(categoryIds, ReadDownloadSize(update)),
                PublishDate = ReadDeploymentTime(update),
                Meta = meta,
            };
        }
        catch (COMException ex)
        {
            // one unreadable update must not abort the whole search
            _logging.Warn($"windowsupdate: skipped an update whose properties could not be read (HRESULT 0x{ex.HResult:X8}).");
            return null;
        }
    }

    /// <summary>
    /// Windows' "Upgrades" category, identified by its stable id. Category NAMES are localized, so
    /// testing them only ever worked on an English or German system; anywhere else a feature upgrade
    /// would have shown up as an ordinary update and been installed, which this source must never do.
    /// </summary>
    private const string UpgradesCategoryId = "3689bdc8-b205-4af4-8d4a-a63924c5e9d5";

    /// <summary>Microsoft's stable classification id for "Security Updates" (not localized).</summary>
    private const string SecurityUpdatesCategoryId = "0fa1201d-4330-4fa8-8ae9-b877473b6441";

    /// <summary>Microsoft's stable classification id for "Definition Updates" (not localized): Defender
    /// signature updates, whose MaxDownloadSize is measured to overstate the real download by two to
    /// three orders of magnitude (a 1.5 GB report on a host where every such update actually transfers a
    /// few MB), so their size is treated as unknown rather than shown wrong.</summary>
    private const string DefinitionUpdatesCategoryId = "e0789628-ce08-4437-be74-2495b842f43b";

    /// <summary>WUA's IWindowsDriverUpdate.Type value; the only Type that carries a DriverManufacturer.</summary>
    private const int DriverUpdateType = 2;

    /// <summary>Last-resort version fallback: every WU title carries a KB number even when it has no
    /// version string of its own.</summary>
    private static readonly Regex KbNumberPattern = new(@"KB\d{5,8}", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static string? ExtractKbNumber(string title)
    {
        var match = KbNumberPattern.Match(title);
        return match.Success ? match.Value : null;
    }

    /// <summary>MoreInfoUrls is a COM string collection; own try/catch so a malformed/empty collection
    /// only loses the changelog link, not the whole row (same reasoning as GetCategoryIds below). Passed
    /// through <see cref="UrlSafety.SafeHttpUrl"/>; a WSUS server is a configurable update source, not
    /// something this app controls.</summary>
    private static string? ReadFirstMoreInfoUrl(object update, ComScope com)
    {
        try
        {
            var urls = com.Track(GetProperty(update, "MoreInfoUrls"));
            var count = Convert.ToInt32(GetProperty(urls, "Count") ?? 0);
            var url = count > 0 ? InvokeGetItem(urls, 0) as string : null;

            return UrlSafety.SafeHttpUrl(url);
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static (HashSet<string> Ids, bool ReadSuccessfully) GetCategoryIds(object update, ComScope com)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var categories = com.Track(GetProperty(update, "Categories"));
            var count = Convert.ToInt32(GetProperty(categories, "Count") ?? 0);
            for (var i = 0; i < count; i++)
            {
                var category = com.Track(InvokeGetItem(categories, i));
                if (GetProperty(category, "CategoryID") is string categoryId)
                    ids.Add(categoryId.Trim('{', '}'));
            }

            return (ids, true);
        }
        catch (COMException)
        {
            return (ids, false);
        }
    }

    /// <summary>
    /// the previous fallback matched the English string "Feature update" in the title when
    /// categories could not be read; a locale-dependent guess that never matches on a non-English
    /// Windows install, letting a feature upgrade slip through undetected on exactly the machines that
    /// most need it caught. The category GUID is stable across every language; when it genuinely cannot
    /// be read at all, that is "unknown", not "safe"; fail closed and exclude it either way.
    /// </summary>
    internal static bool IsFeatureUpgrade(HashSet<string> categoryIds, bool categoriesReadSuccessfully) =>
        categoryIds.Contains(UpgradesCategoryId) || !categoriesReadSuccessfully;

    /// <summary>Reverses the scan's own `$"{updateId}:{revision}"` (TryMapUpdate) back to the GUID alone.
    /// The revision segment is always a plain integer with no colon of its own, so the LAST colon is
    /// always the right split point even when the GUID (or the title it falls back to) contains one.</summary>
    private static string ExtractUpdateId(string itemId)
    {
        var separatorIndex = itemId.LastIndexOf(':');
        return separatorIndex < 0 ? itemId : itemId[..separatorIndex];
    }

    /// <summary>The revision the original scan saw, for the "moved to a different revision" log line only.</summary>
    private static int? ExtractRevision(string itemId)
    {
        var separatorIndex = itemId.LastIndexOf(':');
        return separatorIndex >= 0 && int.TryParse(itemId[(separatorIndex + 1)..], out var revision)
            ? revision
            : null;
    }

    /// <summary>orcSucceeded; the only result code that means this update is installed.</summary>
    private const int OperationResultSucceeded = 2;

    /// <summary>orcSucceededWithErrors; an AGGREGATE verdict over the batch, not a per-update one.</summary>
    private const int OperationResultSucceededWithErrors = 3;

    private void Install(UpdateItem item, IProgress<Models.InstallProgress> progress, CancellationToken cancellationToken)
    {
        // Every RCW created here, released in reverse order; see Search.
        using var com = new ComScope();

        var session = com.Track(CreateComObject("Microsoft.Update.Session"))
            ?? throw new InvalidOperationException("Windows Update session unavailable.");

        var searcher = com.Track(Invoke(session, "CreateUpdateSearcher"))
            ?? throw new InvalidOperationException("Windows Update searcher unavailable.");

        var searchResult = com.Track(Invoke(searcher, "Search", SearchCriteria))
            ?? throw new InvalidOperationException("Windows Update search failed.");

        var updates = com.Track(GetProperty(searchResult, "Updates"));
        var count = Convert.ToInt32(GetProperty(updates, "Count") ?? 0);

        var collection = com.Track(CreateComObject("Microsoft.Update.UpdateColl"))
            ?? throw new InvalidOperationException("Could not create update collection.");

        var candidates = new List<(string UpdateId, int Revision, object Update)>();

        for (var i = 0; i < count; i++)
        {
            var update = com.Track(InvokeGetItem(updates, i));
            if (update is null)
                continue;

            var identity = com.Track(GetProperty(update, "Identity"));
            var updateId = GetProperty(identity, "UpdateID") as string;
            var revision = Convert.ToInt32(GetProperty(identity, "RevisionNumber") ?? 0);

            if (updateId is not null)
                candidates.Add((updateId, revision, update));
        }

        // Windows Update re-evaluates continuously, so the revision it reports here can differ from the
        // one the scan saw a moment ago without the update actually being gone (WindowsUpdateMatching).
        // Matching stays on the GUID alone; only its absence means the update is gone from the offer.
        var wantedUpdateId = ExtractUpdateId(item.Id);
        var chosenRevision = WindowsUpdateMatching.PickInstallTarget(
            wantedUpdateId, candidates.Select(c => (c.UpdateId, c.Revision)).ToList());

        if (chosenRevision is null)
            throw new InvalidOperationException($"Update '{item.Id}' is no longer offered.");

        var scannedRevision = ExtractRevision(item.Id);
        if (chosenRevision != scannedRevision)
        {
            _logging.Info(
                $"windowsupdate: '{wantedUpdateId}' moved from revision {scannedRevision} " +
                $"to {chosenRevision} between scan and install, installing the current revision.");
        }

        var chosen = candidates.First(c =>
            string.Equals(c.UpdateId, wantedUpdateId, StringComparison.OrdinalIgnoreCase)
            && c.Revision == chosenRevision);

        AcceptEulaIfNeeded(chosen.Update);
        Invoke(collection, "Add", chosen.Update);

        cancellationToken.ThrowIfCancellationRequested();
        progress.Report(new Models.InstallProgress { PercentComplete = 10, StatusText = item.Name });

        var downloader = com.Track(Invoke(session, "CreateUpdateDownloader"))!;
        SetProperty(downloader, "Updates", collection);
        // Checked like the install result: a failed download (network, WSUS refusal) would otherwise go on to
        // Install and surface as a misleading install-stage code instead of the download's own HRESULT.
        var downloadResult = com.Track(Invoke(downloader, "Download"));
        VerifySucceeded(downloadResult, com, "download");

        cancellationToken.ThrowIfCancellationRequested();
        progress.Report(new Models.InstallProgress { PercentComplete = 60, StatusText = item.Name });

        var installer = com.Track(Invoke(session, "CreateUpdateInstaller"))!;
        SetProperty(installer, "Updates", collection);

        WaitWhileBusy(installer, cancellationToken);

        if (GetProperty(installer, "RebootRequiredBeforeInstallation") is true)
            throw new InvalidOperationException("A restart is pending before this update can install.");

        var installResult = com.Track(Invoke(installer, "Install"));

        VerifySucceeded(installResult, com, "install");

        var rebootRequired = GetProperty(installResult, "RebootRequired") is true;
        progress.Report(new Models.InstallProgress
        {
            PercentComplete = 100,
            StatusText = item.Name,
            RebootRequired = rebootRequired,
        });
    }

    /// <summary>
    /// IUpdateInstaller.IsBusy means the shared, system-wide Windows Update installer object is already
    /// running someone else's job; starting our own Install on top of that fails outright instead of
    /// queuing behind it (matches a real-world "result code 5" report). Five 15-second backoffs before giving
    /// up: long enough for a short-lived system scan, short enough not to hang a queued batch forever.
    /// </summary>
    internal static void WaitWhileBusy(object installer, CancellationToken cancellationToken)
    {
        const int maxAttempts = 5;
        for (var attempt = 0; attempt < maxAttempts && GetProperty(installer, "IsBusy") is true; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // WaitHandle.WaitOne, not Thread.Sleep: a Stop during this wait must not sit unnoticed for
            // up to the full 15 seconds; WaitOne returns the moment the token is signalled.
            cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(15));
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (GetProperty(installer, "IsBusy") is true)
            throw new InvalidOperationException("Windows Update is still busy with another operation.");
    }

    /// <summary>
    /// The batch verdict alone is not an answer for the ONE update in it: every code except
    /// orcSucceededWithErrors thrown as a bare number with no HRESULT reproduces exactly the
    /// unexplained "result code 5" report above. So <c>GetUpdateResult(0)</c>; the one update
    /// actually in the collection; is read for EVERY code short of orcSucceeded, and its own result
    /// code plus HRESULT are what the exception reports. The code's English name is spelled out in the
    /// exception text itself; that text stays English end to end (raw text and exceptions stay English);
    /// InstallErrorExplainer builds the translated summary line from this same text.
    /// </summary>
    private static void VerifySucceeded(object? operationResult, ComScope com, string stage)
    {
        var resultCode = Convert.ToInt32(GetProperty(operationResult, "ResultCode") ?? 0);
        if (resultCode == OperationResultSucceeded)
            return;

        object? single;
        try
        {
            // Exactly one update was added to the collection, so index 0 is it.
            single = com.Track(Invoke(operationResult!, "GetUpdateResult", 0));
        }
        catch (COMException)
        {
            single = null;
        }

        if (single is null)
        {
            throw new InvalidOperationException(
                $"Windows Update {stage} returned result code {resultCode} ({ResultCodeName(resultCode)}).");
        }

        var singleCode = Convert.ToInt32(GetProperty(single, "ResultCode") ?? 0);
        var hresult = Convert.ToInt32(GetProperty(single, "HResult") ?? 0);

        if (singleCode == OperationResultSucceeded && hresult == 0)
            return;

        throw new InvalidOperationException(
            $"Windows Update {stage} returned result code {singleCode} ({ResultCodeName(singleCode)}), HRESULT 0x{hresult:X8}.");
    }

    /// <summary>WUA's own six install states (orcNotStarted..orcAborted); English, see VerifySucceeded.</summary>
    private static string ResultCodeName(int code) => code switch
    {
        OperationResultSucceeded => "succeeded",
        OperationResultSucceededWithErrors => "succeeded with errors",
        0 => "not started",
        1 => "in progress",
        4 => "failed",
        5 => "aborted",
        _ => "unknown",
    };

    /// <summary>MaxDownloadSize is a COM decimal; a missing or unreadable value simply means "unknown".</summary>
    private static long? ReadDownloadSize(object update)
    {
        try
        {
            return GetProperty(update, "MaxDownloadSize") is { } size and not 0 ? Convert.ToInt64(size) : null;
        }
        catch (Exception ex) when (ex is COMException or OverflowException or InvalidCastException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Pure decision so it is testable without COM: a Definition Update's own reported size is
    /// discarded, every other update keeps whatever <see cref="ReadDownloadSize"/> found.</summary>
    internal static long? DownloadSizeFor(HashSet<string> categoryIds, long? rawSize) =>
        categoryIds.Contains(DefinitionUpdatesCategoryId) ? null : rawSize;

    /// <summary>Whether WUA classifies this update as a driver (IWindowsDriverUpdate); an unreadable
    /// Type means "not a driver update" rather than aborting the whole row.</summary>
    private static bool IsDriverUpdate(object update)
    {
        try
        {
            return GetProperty(update, "Type") is { } typeValue && Convert.ToInt32(typeValue) == DriverUpdateType;
        }
        catch (Exception ex) when (ex is COMException or OverflowException or InvalidCastException or FormatException)
        {
            return false;
        }
    }

    /// <summary>DriverManufacturer only exists on a driver update; call only after <see cref="IsDriverUpdate"/>
    /// confirms that, same pattern as the other optional COM reads above.</summary>
    private static string? ReadDriverManufacturer(object update)
    {
        try
        {
            return GetProperty(update, "DriverManufacturer") as string;
        }
        catch (COMException)
        {
            return null;
        }
    }

    /// <summary>When the update was last changed in the catalogue; the closest thing to a publish date.</summary>
    private static DateTimeOffset? ReadDeploymentTime(object update)
    {
        try
        {
            return GetProperty(update, "LastDeploymentChangeTime") is DateTime time
                ? new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc))
                : null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    private void AcceptEulaIfNeeded(object update)
    {
        try
        {
            if (GetProperty(update, "EulaAccepted") is bool accepted && !accepted)
                Invoke(update, "AcceptEula");
        }
        catch (COMException ex)
        {
            _logging.Info($"windowsupdate: AcceptEula failed, install continues unaccepted: {ex.Message}");
        }
    }

    private static object? CreateComObject(string progId)
    {
        var type = Type.GetTypeFromProgID(progId);
        return type is null ? null : Activator.CreateInstance(type);
    }

    // InvokeMember reports an exception thrown by the COM callee wrapped in a TargetInvocationException.
    // Every catch (COMException) below is written for the COM error itself, so the wrapper is removed here,
    // once, and any other wrapped exception keeps propagating unchanged.
    private static object? Invoke(object target, string method, params object?[] args) =>
        Unwrapped(() => target.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, target, args));

    private static object? GetProperty(object? target, string property) =>
        target is null ? null : Unwrapped(() => target.GetType().InvokeMember(property, BindingFlags.GetProperty, null, target, null));

    private static void SetProperty(object target, string property, object value) =>
        Unwrapped(() => target.GetType().InvokeMember(property, BindingFlags.SetProperty, null, target, [value]));

    /// <summary>WUApiLib collections expose an indexer named "Item".</summary>
    private static object? InvokeGetItem(object? collection, int index) =>
        collection is null ? null : Unwrapped(() => collection.GetType().InvokeMember("Item", BindingFlags.GetProperty, null, collection, [index]));

    private static object? Unwrapped(Func<object?> lateBoundCall)
    {
        try
        {
            return lateBoundCall();
        }
        catch (TargetInvocationException ex) when (ex.InnerException is COMException inner)
        {
            ExceptionDispatchInfo.Capture(inner).Throw();
            throw;
        }
    }

    private static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
            Marshal.FinalReleaseComObject(comObject);
    }

    /// <summary>
    /// Holds every COM object one call created and releases them in reverse order of creation. WUApiLib
    /// hands back a fresh RCW for each searcher, result, collection, update and identity, and only the
    /// session was ever released; so every scan and every install leaked the rest of them for as long as
    /// the app ran. Non-COM values may be tracked too; <see cref="Release"/> ignores them.
    /// </summary>
    internal sealed class ComScope : IDisposable
    {
        private readonly List<object> _tracked = [];

        // WUA can hand back the SAME RCW (one shared wrapper per COM identity) for more than one call;
        // GetCategoryIds sees this across categories shared between updates. Tracking by reference
        // identity, never Equals, keeps a duplicate reference from being released twice: the second
        // Marshal.FinalReleaseComObject on an already-released RCW throws InvalidComObjectException, which
        // would fail the whole scan rather than just one item.
        private readonly HashSet<object> _seen = new(ReferenceEqualityComparer.Instance);

        public object? Track(object? comObject)
        {
            if (comObject is not null && _seen.Add(comObject))
                _tracked.Add(comObject);

            return comObject;
        }

        /// <summary>Test seam only: a real COM object is not available in a unit test, so the property
        /// under test is "the same reference is only ever queued for release once", observed here rather
        /// than through Marshal.FinalReleaseComObject itself.</summary>
        internal int TrackedCount => _tracked.Count;

        public void Dispose()
        {
            for (var i = _tracked.Count - 1; i >= 0; i--)
                Release(_tracked[i]);

            _tracked.Clear();
        }
    }
}
