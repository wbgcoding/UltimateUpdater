using System.Globalization;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UltimateUpdater.Models.Settings;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.ViewModels;

// A quiet banner when the last cleanup is long ago, same hand-built Border-banner pattern and dismiss
// idea as the self-update offer and the reboot notice (MainViewModel.SelfUpdate.cs,
// MainViewModel.Install.cs), read there, never edited here.
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

    /// <summary>
    /// True once the banner's x has been clicked for the current showing: the row then offers "only this
    /// time" / "never again" instead of its usual content. A second x click while this is already true is
    /// a no-op (there is no visible x once asking anyway), so the fork is offered exactly once per
    /// showing, never re-asked.
    /// </summary>
    [ObservableProperty]
    private bool cleanupReminderAsking;

    /// <summary>The banner's normal content (text, cleanup button, x): the exact complement of
    /// <see cref="CleanupReminderAsking"/>, its own property because there is no inverse-boolean visibility
    /// converter in the app and adding one just for this single spot was not worth it.</summary>
    public bool CleanupReminderOfferVisible => !CleanupReminderAsking;

    partial void OnCleanupReminderAskingChanged(bool value) => OnPropertyChanged(nameof(CleanupReminderOfferVisible));

    /// <summary>
    /// Checked once, from <c>App.xaml.cs</c>'s start-up sequence, the same "on start, never interrupts,
    /// only offers" spirit as <see cref="CheckForSelfUpdateAsync"/>, except this is a local file read, so
    /// there is nothing to await. A fresh <see cref="CleanupHistoryService"/> is used rather than a stored
    /// field: nothing else in <see cref="MainViewModel"/> needs the cleanup history, and building one is
    /// as cheap as the JSON file it reads.
    /// </summary>
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

    /// <summary>
    /// The pure decision, kept apart from disk and clock access so a test can drive every branch (inside
    /// the threshold, past it, no history at all, the reminder turned off) without touching a real file or
    /// the system clock.
    /// </summary>
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

    /// <summary>
    /// No install date is recorded anywhere; the data directory's own creation time is the closest
    /// standing signal for "how long has this been set up" without adding a new file or settings key just
    /// for this. Unreadable (a locked-down profile, a race on a freshly created directory) never claims a
    /// cleanup is overdue.
    /// </summary>
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
