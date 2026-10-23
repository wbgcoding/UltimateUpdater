using System.Runtime.Versioning;
using System.Windows;

namespace UltimateUpdater.Services;

/// <summary>
/// Interval-scan results are notify-only: the app never installs on its own.
/// Uses the tray icon's balloon tip, which needs no packaged identity and no external dependency;
/// a toast is never the only channel, the window always shows the same result.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NotificationService
{
    private readonly SettingsService _settings;
    private readonly LoggingService _logging;

    public NotificationService(SettingsService settings, LoggingService logging)
    {
        _settings = settings;
        _logging = logging;
    }

    /// <summary>Raised with (title, message) when something should be shown; the tray icon renders it.</summary>
    public event Action<string, string>? NotificationRequested;

    public void NotifyUpdatesFound(int total, int critical)
    {
        var notify = _settings.Current.Notify;
        if (!notify.OnFound || total == 0)
            return;

        if (notify.CriticalOnly && critical == 0)
            return;

        var title = LocalizationService.Instance["AppTitle"];
        var key = (critical > 0, total == 1) switch
        {
            (true, true) => "NotifyFoundOneWithCritical",
            (true, false) => "NotifyFoundWithCritical",
            (false, true) => "NotifyFoundOne",
            (false, false) => "NotifyFound",
        };

        var message = string.Format(LocalizationService.Instance[key], total, critical);

        _logging.Info($"Notifying about {total} update(s), {critical} critical.");

        // Null-conditional like every other Application.Current use in this app: an interval scan finishing
        // during shutdown has no Application left, and this runs from an async-void timer tick where the
        // resulting NullReferenceException has nowhere to go but the process.
        Application.Current?.Dispatcher.Invoke(() => NotificationRequested?.Invoke(title, message));
    }

    /// <summary>
    /// A batch that finished while the window was hidden to tray never opens the restart dialog
    /// (nobody is watching for it); the banner still shows once the window is reopened, and this balloon
    /// is the one thing that reaches the user right away. Reuses the same sentence the banner itself
    /// shows, no separate copy for the same fact told twice.
    /// </summary>
    public void NotifyRebootNeeded(int count)
    {
        var title = LocalizationService.Instance["AppTitle"];
        var message = string.Format(
            LocalizationService.Instance[count == 1 ? "RebootRequiredInfoOne" : "RebootRequiredInfo"], count);

        Application.Current?.Dispatcher.Invoke(() => NotificationRequested?.Invoke(title, message));
    }
}
