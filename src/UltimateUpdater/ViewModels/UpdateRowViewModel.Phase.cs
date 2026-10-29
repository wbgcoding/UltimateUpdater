using UltimateUpdater.Models;
using UltimateUpdater.Services;

namespace UltimateUpdater.ViewModels;

/// <summary>
/// The honest download/install split of a running row. A figure is shown only while the tool really
/// reports a download; once the download is over, or the tool never reported anything measurable, the
/// row says "Installing…" with the time that run has taken instead of a percentage nobody measured.
/// </summary>
public partial class UpdateRowViewModel
{
    /// <summary>How long a finished download reading may stand without a new transfer starting before the
    /// row calls the rest "installing". Long enough for pip to open the next file's bar.</summary>
    internal static readonly TimeSpan DownloadDoneGrace = TimeSpan.FromSeconds(1.5);

    /// <summary>The row's clock; swapped in tests so the grace period and the elapsed text are exact.</summary>
    internal Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    private InstallPhase _phase;

    // An install line from the tool is final. A phase inferred from a finished download reading is not:
    // pip downloads several files in a row, so a new transfer after the pause takes the row back.
    private bool _phaseIsFinal;
    private bool _lastReadingComplete;
    private DateTimeOffset _lastReadingAt;
    private DateTimeOffset? _runStartedAt;
    private string _shownRunningText = string.Empty;

    // Several downloads in a row (pip: one bar per file) add up to one bar instead of running 0 to 100
    // once per file.
    private long _completedBytes;
    private long _fileTotal;
    private long _fileReceived;

    public InstallPhase Phase => _phase;

    /// <summary>True while the row has no honest figure to show: installing, or nothing measurable reported yet.</summary>
    public bool IsIndeterminateRun =>
        IsInstallRunning && !IsRunningInteractively && (_phase == InstallPhase.Installing || !_hasRealProgress);

    public bool ShowDeterminateProgress => ShowRunningProgress && !IsIndeterminateRun;

    public bool ShowIndeterminateProgress => ShowRunningProgress && IsIndeterminateRun;

    /// <summary>"Installing… 1:23": what the row is doing and for how long this run has taken.</summary>
    public string RunningStatusText =>
        string.Format(LocalizationService.Instance["RowInstallingPhase"], FormatElapsed(Clock() - (_runStartedAt ?? Clock())));

    /// <summary>The text beside a running row's bar: the real percentage, or the installing line when there is none.</summary>
    public string RunningProgressText => IsIndeterminateRun ? RunningStatusText : InstallPercentText;

    internal static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;

        return elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes}:{elapsed.Seconds:00}";
    }

    /// <summary>Driven by the install timer: ends a finished download's pause and moves the elapsed time.</summary>
    public void Tick()
    {
        if (InstallState != RowInstallState.Running)
            return;

        if (_phase == InstallPhase.Downloading && _lastReadingComplete && Clock() - _lastReadingAt >= DownloadDoneGrace)
            SetPhase(InstallPhase.Installing, final: false);

        var text = RunningStatusText;
        if (text == _shownRunningText)
            return;

        _shownRunningText = text;
        OnPropertyChanged(nameof(RunningStatusText));
        OnPropertyChanged(nameof(RunningProgressText));
    }

    private void ResetPhase()
    {
        _phase = InstallPhase.Unknown;
        _phaseIsFinal = false;
        _lastReadingComplete = false;
        _lastReadingAt = default;
        _completedBytes = 0;
        _fileTotal = 0;
        _fileReceived = 0;
        _shownRunningText = string.Empty;
        NotifyPhaseChanged();
    }

    private void SetPhase(InstallPhase phase, bool final)
    {
        _phaseIsFinal |= final;
        if (_phase == phase)
            return;

        _phase = phase;
        NotifyPhaseChanged();
    }

    private void OnRunningChanged(bool running)
    {
        if (running)
            _runStartedAt ??= Clock();
        else
            _runStartedAt = null;
    }

    private void NotifyPhaseChanged()
    {
        OnPropertyChanged(nameof(Phase));
        OnPropertyChanged(nameof(IsIndeterminateRun));
        OnPropertyChanged(nameof(ShowDeterminateProgress));
        OnPropertyChanged(nameof(ShowIndeterminateProgress));
        OnPropertyChanged(nameof(ShowSourceText));
        OnPropertyChanged(nameof(RunningStatusText));
        OnPropertyChanged(nameof(RunningProgressText));
    }

    /// <summary>
    /// Reads the phase out of one status and returns it with the figures the row should show. A download
    /// reading keeps the row in Downloading; an install line from the tool, or a finished reading that
    /// nothing followed (see <see cref="Tick"/>), moves it to Installing.
    /// </summary>
    private InstallItemStatus TrackPhase(InstallItemStatus status)
    {
        if (status.IsTerminal)
        {
            // The queue closes a download with the LAST file's size; the row's figure is the whole set.
            if (_completedBytes > 0 && status.BytesTotal is > 0)
            {
                var total = _completedBytes + status.BytesTotal.Value;
                var received = status.Success == true ? total : _completedBytes + (status.BytesReceived ?? 0);
                return status with { BytesReceived = received, BytesTotal = total };
            }

            return status;
        }

        if (status.Phase == InstallPhase.Installing)
        {
            SetPhase(InstallPhase.Installing, final: true);
            return status;
        }

        var hasReading = status.Percent > 0 || status.BytesTotal is > 0 || status.BytesReceived is > 0;
        if (!hasReading)
            return status;

        if (status.BytesTotal is > 0)
            status = AddUpDownloads(status);

        _lastReadingAt = Clock();
        _lastReadingComplete = status.Percent >= 100;

        // A repeat of the finished reading must not undo an inferred Installing; only a transfer that is
        // still short of its end does (the next file).
        if (!_phaseIsFinal && !(_phase == InstallPhase.Installing && _lastReadingComplete))
            SetPhase(InstallPhase.Downloading, final: false);

        return status;
    }

    private InstallItemStatus AddUpDownloads(InstallItemStatus status)
    {
        var received = status.BytesReceived ?? 0;
        var total = status.BytesTotal!.Value;

        // A different size, or a count that went backwards, is the next file starting.
        if (_fileTotal > 0 && (total != _fileTotal || received < _fileReceived))
            _completedBytes += _fileTotal;

        _fileTotal = total;
        _fileReceived = received;

        if (_completedBytes == 0)
            return status;

        var allReceived = _completedBytes + received;
        var allTotal = _completedBytes + total;
        return status with
        {
            BytesReceived = allReceived,
            BytesTotal = allTotal,
            Percent = Math.Clamp(allReceived * 100.0 / allTotal, 0, 100),
        };
    }
}
