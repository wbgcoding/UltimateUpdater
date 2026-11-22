namespace UltimateUpdater.Services.Uninstall;

/// <summary>
/// The installed entries the update table's rows map to, read once and kept until the next scan completes
/// (<see cref="Invalidate"/>). Reading it is slow (registry, winget, Store packages), so it happens on the
/// first context-menu open of a row that could use it, never while the table is built.
/// </summary>
public sealed class RowUninstallInventory(IUninstallerBackend backend, LoggingService logging)
{
    private readonly object _gate = new();
    private IReadOnlyList<UninstallListing>? _cached;
    private Task<IReadOnlyList<UninstallListing>?>? _loading;
    private int _generation;
    private int _readCount;

    /// <summary>The backend the entries are read through and a removal of one of them runs on.</summary>
    public IUninstallerBackend Backend { get; } = backend;

    /// <summary>How often the machine was read (a test reads it to see the cache hold).</summary>
    public int ReadCount => Volatile.Read(ref _readCount);

    /// <summary>The entries once loaded for the current scan, else null.</summary>
    public IReadOnlyList<UninstallListing>? Cached
    {
        get
        {
            lock (_gate)
                return _cached;
        }
    }

    /// <summary>Loads the entries unless they are already loaded or loading. Null when the read failed (the next menu open tries again).</summary>
    public Task<IReadOnlyList<UninstallListing>?> EnsureLoadedAsync()
    {
        lock (_gate)
        {
            if (_cached is not null)
                return Task.FromResult<IReadOnlyList<UninstallListing>?>(_cached);

            return _loading ??= LoadAsync(_generation);
        }
    }

    private async Task<IReadOnlyList<UninstallListing>?> LoadAsync(int generation)
    {
        Interlocked.Increment(ref _readCount);

        IReadOnlyList<UninstallListing>? listings;
        try
        {
            listings = await Backend.ListAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logging.Warn($"Uninstall list for the table rows failed: {ex.Message}");
            listings = null;
        }

        lock (_gate)
        {
            // A scan completed while this read ran: its answer belongs to the earlier state and is dropped.
            // The load of the current generation clears itself (success or failure), so a failed read is retried.
            if (generation == _generation)
            {
                _cached = listings;
                _loading = null;
            }
        }

        return listings;
    }

    /// <summary>A scan completed: what was read no longer counts, the next menu open reads again.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _generation++;
            _cached = null;
            _loading = null;
        }
    }
}
