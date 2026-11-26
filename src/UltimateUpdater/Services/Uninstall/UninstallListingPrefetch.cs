using System.Diagnostics;

namespace UltimateUpdater.Services.Uninstall;

/// <summary>
/// One full read of the uninstaller listing, started in the background once per app session (after the first scan,
/// while the app is idle), so the window opens on fresh rows. It writes the saved listing like any full read; a window
/// opened while it runs awaits the same task instead of starting a second read, and a window opened within
/// <see cref="ReuseWindow"/> after it finished takes its result. Never elevated, cancelled when the app exits.
/// </summary>
public sealed class UninstallListingPrefetch(IUninstallerBackend backend, LoggingService logging, Func<DateTime>? clock = null)
{
    /// <summary>How long a finished prefetch's rows still count as fresh enough for an opening window.</summary>
    public static readonly TimeSpan ReuseWindow = TimeSpan.FromMinutes(5);

    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.UtcNow);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private Task<IReadOnlyList<UninstallListing>>? _task;
    private DateTime? _finishedAt;

    /// <summary>The read in flight or finished, or null when none was started.</summary>
    public Task<IReadOnlyList<UninstallListing>>? Current
    {
        get
        {
            lock (_gate)
                return _task;
        }
    }

    /// <summary>Starts the read once; later calls change nothing. Returns false when it was already started.</summary>
    public bool Start()
    {
        lock (_gate)
        {
            if (_task is not null)
                return false;

            var token = _cts.Token;

            // Task.Run keeps the read off the caller's (UI) thread; the thread that starts it runs below normal priority for
            // the synchronous stretch before the read's first await, then goes back, because a pool thread left lowered would
            // slow whatever work it takes next.
            _task = Task.Run(() => RunAsync(token), token);
            return true;
        }
    }

    private Task<IReadOnlyList<UninstallListing>> RunAsync(CancellationToken token)
    {
        var thread = Thread.CurrentThread;
        var before = thread.Priority;
        thread.Priority = ThreadPriority.BelowNormal;
        try
        {
            return ReadAsync(token);
        }
        finally
        {
            thread.Priority = before;
        }
    }

    private async Task<IReadOnlyList<UninstallListing>> ReadAsync(CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            var listings = await backend.ListAsync(token);
            await backend.SaveCacheAsync([.. listings.Select(listing => listing.Entry)]);
            lock (_gate)
                _finishedAt = _clock();

            logging.Info($"uninstaller prefetch: {listings.Count} rows in {timer.ElapsedMilliseconds} ms");
            return listings;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logging.Warn($"uninstaller prefetch failed after {timer.ElapsedMilliseconds} ms: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// The prefetch's rows for an opening window: awaits the read while it runs, returns a finished one when it is younger
    /// than <see cref="ReuseWindow"/>. Null when there is none, it failed or it is too old: the caller reads for itself.
    /// </summary>
    public async Task<IReadOnlyList<UninstallListing>?> TryTakeAsync(CancellationToken cancellationToken)
    {
        Task<IReadOnlyList<UninstallListing>>? task;
        lock (_gate)
            task = _task;

        if (task is null)
            return null;

        try
        {
            var listings = await task.WaitAsync(cancellationToken);
            lock (_gate)
            {
                if (_finishedAt is not { } finished || _clock() - finished > ReuseWindow)
                    return null;
            }

            return listings;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return null;   // failed or cancelled by the exit: the window does its own read
        }
    }

    /// <summary>Stops a read in flight; the app is exiting.</summary>
    public void Cancel() => _cts.Cancel();
}
