using System.Globalization;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UltimateUpdater.Models.Settings;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.ViewModels.Main;

// A quiet banner when the last cleanup is long ago; same Border-banner pattern and dismiss idea as the self-update offer and the reboot notice.
public partial class MainViewModel
{
    [ObservableProperty]
    private bool cleanupReminderVisible;

    private DateTimeOffset? _cleanupReminderReference;

    /// <summary>Built on every read from the stored date, so a language switch only has to re-raise it.</summary>
    public string CleanupReminderText => _cleanupReminderReference is not { } reference
        ? string.Empty
        : string.Format(
            Loc["CleanupReminderBanner"],
            reference.LocalDateTime.ToString(
                CultureInfo.CurrentUICulture.DateTimeFormat.ShortDatePattern, CultureInfo.CurrentUICulture));

    internal void ShowCleanupReminder(DateTimeOffset reference)
    {
        _cleanupReminderReference = reference;
        OnPropertyChanged(nameof(CleanupReminderText));
        CleanupReminderVisible = true;
    }

    /// <summary>True once the banner's x has been clicked for the current showing: the row then offers "only this time" or "never again". A second x click is a no-op, so the question is asked once per showing.</summary>
    [ObservableProperty]
    private bool cleanupReminderAsking;

    /// <summary>The banner's normal content, the complement of <see cref="CleanupReminderAsking"/>; its own property so the banner binds to it without an inverse-boolean converter.</summary>
    public bool CleanupReminderOfferVisible => !CleanupReminderAsking;

    partial void OnCleanupReminderAskingChanged(bool value) => OnPropertyChanged(nameof(CleanupReminderOfferVisible));

    /// <summary>Checked once from the start-up sequence. Only offers, never interrupts; a local file read, so nothing to await. A fresh <see cref="CleanupHistoryService"/> is used since nothing else here needs the history.</summary>
    public void EvaluateCleanupReminder()
    {
        try
        {
            var settings = _settings.Current;
            var history = new CleanupHistoryService(_logging).Load();
            var installedSince = ResolveInstalledSince();

            if (!ShouldShowCleanupReminder(history, DateTimeOffset.Now, installedSince, settings))
                return;

            ShowCleanupReminder(history.Count > 0 ? history.Max(e => e.Timestamp) : installedSince);
        }
        catch (Exception ex)
        {
            // A reminder is a nicety; it must never stop the window from opening.
            _logging.Warn($"Cleanup reminder check failed: {ex.Message}");
        }
    }

    /// <summary>The pure decision, apart from disk and clock access so a test can drive every branch.</summary>
    public static bool ShouldShowCleanupReminder(
        IReadOnlyList<CleanupHistoryEntry> history,
        DateTimeOffset now,
        DateTimeOffset installedSince,
        AppSettings settings)
    {
        if (settings.CleanupReminderOff)
            return false;

        var threshold = TimeSpan.FromDays(Math.Max(1, settings.CleanupReminderDays));
        var reference = history.Count > 0 ? history.Max(e => e.Timestamp) : installedSince;

        return now - reference >= threshold;
    }

    /// <summary>No install date is recorded anywhere; the data directory's creation time is the closest standing signal. Unreadable never claims a cleanup is overdue.</summary>
    private static DateTimeOffset ResolveInstalledSince()
    {
        try
        {
            return new DateTimeOffset(Directory.GetCreationTimeUtc(AppPaths.DataDirectory), TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return DateTimeOffset.Now;
        }
    }

    [RelayCommand]
    private void DismissCleanupReminder()
    {
        if (CleanupReminderAsking)
            return;

        CleanupReminderAsking = true;
    }

    [RelayCommand]
    private void DismissCleanupReminderOnce()
    {
        CleanupReminderAsking = false;
        CleanupReminderVisible = false;
    }

    [RelayCommand]
    private void DismissCleanupReminderForever()
    {
        _settings.Current.CleanupReminderOff = true;
        Persist();
        CleanupReminderAsking = false;
        CleanupReminderVisible = false;
    }
}
