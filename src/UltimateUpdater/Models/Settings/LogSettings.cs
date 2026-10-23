namespace UltimateUpdater.Models.Settings;

/// <summary>
/// How long a day's log file is kept. The housekeeping runs at start-up, so a shorter value takes effect
/// on the next launch rather than immediately. The settings window offers 3, 7, 30 and 365 days; a value
/// outside that (an older default, or a hand-edited file) is honoured as written.
/// </summary>
public sealed class LogSettings
{
    public int RetentionDays { get; set; } = 30;
}
