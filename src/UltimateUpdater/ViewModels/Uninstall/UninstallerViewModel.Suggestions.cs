using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UltimateUpdater.Services.Uninstall;

namespace UltimateUpdater.ViewModels.Uninstall;

/// <summary>The suggestions card: a few entries worth a look, worked out in the background once the list has loaded.</summary>
public sealed partial class UninstallerViewModel
{
    /// <summary>How long a row shown by "Show" stays highlighted.</summary>
    private static readonly TimeSpan HighlightTime = TimeSpan.FromSeconds(4);

    private readonly List<string> _dismissedLocal = [];
    private IReadOnlyList<UninstallListing> _suggestionListings = [];
    private IReadOnlyDictionary<string, DateTime> _usage = new Dictionary<string, DateTime>();
    private IReadOnlyList<UninstallSuggestion> _suggestions = [];
    private CancellationTokenSource? _highlightCts;
    private int _suggestionVersion;

    /// <summary>Reads when each program was last used (blocking, runs on a pool thread). Null: no usage data, so no program is called unused; the app sets the real reader, a test never reads the machine.</summary>
    internal Func<IReadOnlyList<InstalledProgramEntry>, CancellationToken, IReadOnlyDictionary<string, DateTime>>? UsageReader { get; set; }

    /// <summary>Where the hidden suggestions are kept. Null: kept only for this window (tests, review).</summary>
    internal SettingsService? Settings { get; set; }

    /// <summary>The clock the unused rule measures against.</summary>
    internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    /// <summary>The lines of the card; empty hides it.</summary>
    public ObservableCollection<UninstallSuggestionViewModel> Suggestions { get; } = [];

    public bool HasSuggestions => Suggestions.Count > 0;

    public string SuggestionsTitle => $"{Loc["UninstallerSuggestTitle"]} ({Suggestions.Count})";

    /// <summary>Whether the card shows its lines; the header stays either way.</summary>
    [ObservableProperty]
    private bool suggestionsExpanded = true;

    /// <summary>Completes when the suggestions of the latest list load are on show; for tests.</summary>
    internal Task SuggestionsReady { get; private set; } = Task.CompletedTask;

    /// <summary>Raised when "Show" or "Select" wants the window to scroll to rows (and, with the flag, flash them).</summary>
    internal event Action<IReadOnlyList<UninstallRowViewModel>>? RowsRevealRequested;

    private List<string> DismissedKeys => Settings?.Current.UninstallSuggestionsDismissed ?? _dismissedLocal;

    /// <summary>Works the suggestions out again for the rows on show: the usage read runs off the UI thread, the rules are pure.</summary>
    private void RefreshSuggestions()
    {
        var version = ++_suggestionVersion;
        var listings = _all.Where(row => row.IsVerified).Select(row => row.Listing).ToList();
        var reader = UsageReader;
        if (reader is null)
        {
            ApplySuggestions(version, listings, new Dictionary<string, DateTime>());
            return;
        }

        var entries = listings.Select(listing => listing.Entry).ToList();
        var post = PostToUi ?? DispatcherPost();
        SuggestionsReady = Task.Run(() =>
        {
            IReadOnlyDictionary<string, DateTime> usage;
            try
            {
                usage = reader(entries, CancellationToken.None);
            }
            catch (Exception ex)
            {
                // No usage data is the safe answer: nothing is then called unused.
                _logging.Warn($"Uninstaller: reading the program usage failed: {ex.Message}");
                usage = new Dictionary<string, DateTime>();
            }

            post(() => ApplySuggestions(version, listings, usage));
        });
    }

    private void ApplySuggestions(int version, IReadOnlyList<UninstallListing> listings, IReadOnlyDictionary<string, DateTime> usage)
    {
        if (version != _suggestionVersion)
            return;

        _suggestionListings = listings;
        _usage = usage;
        RebuildSuggestions();
    }

    private void RebuildSuggestions()
    {
        _suggestions = UninstallSuggestions.Build(_suggestionListings, _usage, DismissedKeys, UtcNow());
        ShowSuggestions();
    }

    /// <summary>Puts the suggestions on the card, each bound to the rows that exist now.</summary>
    private void ShowSuggestions()
    {
        var rows = new Dictionary<string, UninstallRowViewModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _all)
            rows.TryAdd(row.Entry.Key, row);

        Suggestions.Clear();
        foreach (var suggestion in _suggestions)
        {
            var concerned = suggestion.Entries.Select(copy => rows.GetValueOrDefault(copy.Key)).OfType<UninstallRowViewModel>().ToList();
            if (concerned.Count == suggestion.Entries.Count)
                Suggestions.Add(new UninstallSuggestionViewModel(suggestion, concerned));
        }

        OnPropertyChanged(nameof(HasSuggestions));
        OnPropertyChanged(nameof(SuggestionsTitle));
    }

    /// <summary>"Select" ticks the row; "Show" (a duplicate, where the user must compare) only scrolls to the rows and highlights them.</summary>
    [RelayCommand]
    private void SelectSuggestion(UninstallSuggestionViewModel? line)
    {
        if (line is null)
            return;

        if (!line.IsShowAction)
        {
            foreach (var row in line.Rows)
                row.IsSelected = true;
        }

        Reveal(line.Rows, highlight: line.IsShowAction);
    }

    /// <summary>Hides the suggestion for good: its key goes to the settings and the card fills up with the next one.</summary>
    [RelayCommand]
    private void DismissSuggestion(UninstallSuggestionViewModel? line)
    {
        if (line is null)
            return;

        var keys = DismissedKeys;
        if (!keys.Contains(line.Suggestion.DismissKey, StringComparer.OrdinalIgnoreCase))
            keys.Add(line.Suggestion.DismissKey);

        Settings?.SaveDeferred();
        RebuildSuggestions();
    }

    /// <summary>Opens the sections the rows are in, asks the window to scroll to them and flashes them when asked.</summary>
    private void Reveal(IReadOnlyList<UninstallRowViewModel> rows, bool highlight)
    {
        foreach (var row in rows)
        {
            if (Sections.FirstOrDefault(section => section.Kind == SectionOf(row)) is { } section)
                section.IsExpanded = true;
        }

        _highlightCts?.Cancel();
        foreach (var row in _all.Where(row => row.IsHighlighted))
            row.IsHighlighted = false;

        if (highlight)
        {
            foreach (var row in rows)
                row.IsHighlighted = true;

            var cts = _highlightCts = new CancellationTokenSource();
            var post = PostToUi ?? DispatcherPost();
            _ = Task.Delay(HighlightTime, cts.Token).ContinueWith(
                _ => post(() =>
                {
                    foreach (var row in rows)
                        row.IsHighlighted = false;
                }),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnRanToCompletion,
                TaskScheduler.Default);
        }

        RowsRevealRequested?.Invoke(rows);
    }

    private void RefreshSuggestionText() => ShowSuggestions();
}
