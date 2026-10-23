using System.Windows.Threading;

namespace UltimateUpdater.Services;

/// <summary>
/// In-process interval timer; deliberately not a Windows service or scheduled task,
/// so the app never installs anything the user did not start. A due scan only ever notifies.
/// </summary>
public sealed class ScanScheduler : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly SettingsService _settings;
    private readonly LoggingService _logging;
    private readonly Func<Task<bool>> _runScanAsync;
    private readonly Func<bool> _isMetered;
    private readonly Func<bool> _isOnline;
    private bool _offlineSkipLogged;

    private DateTimeOffset _lastRun = DateTimeOffset.MinValue;
    private bool _isRunning;
    private bool _meteredSkipLogged;
    private bool _busySkipLogged;

    /// <param name="isMetered">Test seam (default <see cref="MeteredConnection.IsMetered"/>): the real
    /// probe reads whatever connection genuinely happens to be active on the host it runs on.</param>
    public ScanScheduler(SettingsService settings, LoggingService logging, Func<Task> runScanAsync, Func<bool>? isMetered = null)
        : this(settings, logging, async () =>
        {
            await runScanAsync();
            return true;
        }, isMetered)
    {
    }

    /// <param name="runScanAsync">Returns whether the scan actually ran. One that stepped aside (an
    /// install or another scan still running) leaves the interval due, so the next tick tries again
    /// instead of a whole interval later.</param>
    /// <param name="isMetered">See the other constructor.</param>
    /// <param name="isOnline">The internet probe the app passes (<see cref="NetworkAvailability.IsOnline"/>);
    /// left out (tests), every tick counts as online.</param>
    public ScanScheduler(
        SettingsService settings, LoggingService logging, Func<Task<bool>> runScanAsync, Func<bool>? isMetered = null,
        Func<bool>? isOnline = null)
    {
        _settings = settings;
        _logging = logging;
        _runScanAsync = runScanAsync;
        _isMetered = isMetered ?? MeteredConnection.IsMetered;
        _isOnline = isOnline ?? (() => true);

        // check once a minute; the interval itself is compared against the last run
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _timer.Tick += async (_, _) => await TickAsync();
    }

    public void Start()
    {
        _lastRun = DateTimeOffset.Now;
        _timer.Start();
    }

    public void NotifyScanCompleted() => _lastRun = DateTimeOffset.Now;

    /// <summary>Test seam: pretends no scan has ever run, so the next tick finds one due.</summary>
    internal void MakeDueForTest() => _lastRun = DateTimeOffset.MinValue;

    /// <summary>Internal, not private, so a test can call it directly instead of waiting on the real
    /// one-minute <see cref="DispatcherTimer"/> tick.</summary>
    internal async Task TickAsync()
    {
        var intervalMinutes = _settings.Current.Scan.IntervalMinutes;
        if (intervalMinutes <= 0 || _isRunning)
            return;

        // A negative gap means the clock was moved back past the last run (a wrong RTC corrected by time
        // sync): read as "due", not as hours or a year of silence until the wall clock catches up again.
        var sinceLastRun = DateTimeOffset.Now - _lastRun;
        if (sinceLastRun >= TimeSpan.Zero && sinceLastRun < TimeSpan.FromMinutes(intervalMinutes))
            return;

        if (_settings.Current.Scan.OnlyOnAc && !PowerStatus.IsOnAcPower())
            return;

        // A scan the user starts by hand always runs regardless; this only gates the automatic tick.
        if (_settings.Current.Scan.RespectMetered && _isMetered())
        {
            // The scan stays due, so every tick lands here until the connection changes: say it once.
            // _lastRun is deliberately not moved, the scan must run as soon as the connection is unmetered.
            if (!_meteredSkipLogged)
            {
                _logging.Info("Interval scan skipped: metered connection.");
                _meteredSkipLogged = true;
            }

            return;
        }

        _meteredSkipLogged = false;

        // Right after resume from sleep the tick usually fires before the network is back: every network
        // source would fail, yet the run counted as done and the next try was a whole interval later. Like
        // the metered skip, the scan stays due. The probe is COM, so it runs off the dispatcher; the tick
        // counts as running while it waits, so a probe slower than the next tick cannot start a second scan.
        _isRunning = true;
        bool online;
        try
        {
            online = await Task.Run(_isOnline);
        }
        catch (Exception ex)
        {
            _logging.Warn($"Interval scan: the network probe failed ({ex.Message}); treated as online.");
            online = true;
        }

        if (!online)
        {
            if (!_offlineSkipLogged)
            {
                _logging.Info("Interval scan skipped: no internet connection; it stays due.");
                _offlineSkipLogged = true;
            }

            _isRunning = false;
            return;
        }

        _offlineSkipLogged = false;

        // A scan that throws still counts as run, so a persistent failure is retried once per interval,
        // not on every tick.
        var ran = true;

        // A scan that keeps stepping aside is retried every tick for as long as the install runs; like the
        // metered skip, that is said once rather than once a minute.
        var wasSkipping = _busySkipLogged;
        try
        {
            if (!wasSkipping)
                _logging.Info("Interval scan starting.");
            ran = await _runScanAsync();
        }
        catch (Exception ex)
        {
            // a scheduled scan failing must never take the app down
            _logging.Error("Interval scan failed", ex);
        }
        finally
        {
            if (ran)
            {
                _lastRun = DateTimeOffset.Now;
                _busySkipLogged = false;
            }
            else if (!wasSkipping)
            {
                _logging.Info("Interval scan skipped: an install or another scan is running; it stays due.");
                _busySkipLogged = true;
            }

            _isRunning = false;
        }
    }

    public void Dispose() => _timer.Stop();
}

internal static class PowerStatus
{
    public static bool IsOnAcPower()
    {
        try
        {
            return System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus
                != System.Windows.Forms.PowerLineStatus.Offline;
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException)
        {
            // unknown power state: treat as mains so a scan is never silently skipped forever
            return true;
        }
    }
}
