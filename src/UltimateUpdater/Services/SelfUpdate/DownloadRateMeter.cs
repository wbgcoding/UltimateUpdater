namespace UltimateUpdater.Services.SelfUpdate;

/// <summary>
/// Turns byte readings into a speed that moves calmly: the raw rate between two readings jumps with every
/// network hiccup, so each new one only pulls the shown value part of the way.
/// </summary>
internal sealed class DownloadRateMeter
{
    private const double Smoothing = 0.3;

    private bool _started;
    private long _lastBytes;
    private TimeSpan _lastTime;
    private double _rate;

    /// <summary>Takes the bytes read so far at <paramref name="now"/> and returns the smoothed bytes per second.</summary>
    public double Add(long bytes, TimeSpan now)
    {
        if (!_started)
        {
            _started = true;
            _lastBytes = bytes;
            _lastTime = now;
            return 0;
        }

        var seconds = (now - _lastTime).TotalSeconds;
        if (seconds <= 0)
            return _rate;

        var current = Math.Max(0, bytes - _lastBytes) / seconds;
        _rate = _rate == 0 ? current : (_rate * (1 - Smoothing)) + (current * Smoothing);
        _lastBytes = bytes;
        _lastTime = now;
        return _rate;
    }
}
