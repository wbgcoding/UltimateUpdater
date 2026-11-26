using System.Globalization;
using UltimateUpdater.Services.Formatting;
using UltimateUpdater.Services.Uninstall;

namespace UltimateUpdater.ViewModels.Uninstall;

/// <summary>One line of the suggestions card: the program, why it is suggested, what it frees and which rows the buttons act on.</summary>
public sealed class UninstallSuggestionViewModel
{
    public UninstallSuggestionViewModel(UninstallSuggestion suggestion, IReadOnlyList<UninstallRowViewModel> rows)
    {
        Suggestion = suggestion;
        Rows = rows;
    }

    public UninstallSuggestion Suggestion { get; }

    /// <summary>The rows this line concerns, in the order of the suggestion (a duplicate has one per copy).</summary>
    public IReadOnlyList<UninstallRowViewModel> Rows { get; }

    /// <summary>The row whose icon the line shows.</summary>
    public UninstallRowViewModel Row => Rows[0];

    public string Name => Suggestion.Name;

    /// <summary>A duplicate has two rows to compare, so its button shows them instead of ticking one.</summary>
    public bool IsShowAction => Suggestion.Kind == UninstallSuggestionKind.DuplicateInstall;

    public string SizeText => Suggestion.ReclaimableBytes > 0 ? ByteSize.Format(Suggestion.ReclaimableBytes) : string.Empty;

    public bool HasSize => Suggestion.ReclaimableBytes > 0;

    /// <summary>The reason in one short line, in the current language.</summary>
    public string ReasonText
    {
        get
        {
            var loc = LocalizationService.Instance;
            switch (Suggestion.Kind)
            {
                case UninstallSuggestionKind.DuplicateInstall:
                    var copies = string.Join(", ", Suggestion.Entries.Select(copy =>
                        copy.Bytes is > 0 ? $"{copy.Source} {ByteSize.Format(copy.Bytes.Value)}" : copy.Source));
                    return Suggestion.Entries.Count == 2
                        ? string.Format(CultureInfo.CurrentCulture, loc["UninstallerSuggestDuplicate"], copies)
                        : string.Format(CultureInfo.CurrentCulture, loc["UninstallerSuggestDuplicateMany"], Suggestion.Entries.Count, copies);
                case UninstallSuggestionKind.EmptyEntry:
                    return loc["UninstallerSuggestEmpty"];
                default:
                    var since = (Suggestion.LastUsed ?? DateTime.UtcNow).ToLocalTime().ToString("d", CultureInfo.CurrentCulture);
                    return string.Format(CultureInfo.CurrentCulture, loc["UninstallerSuggestUnused"], since);
            }
        }
    }

    public string ActionText => LocalizationService.Instance[IsShowAction ? "UninstallerSuggestShow" : "UninstallerSuggestSelect"];

    public string ActionAutomationName =>
        string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance[IsShowAction ? "UninstallerSuggestShowFor" : "UninstallerSuggestSelectFor"], Name);

    public string DismissText => LocalizationService.Instance["UninstallerSuggestDismiss"];

    public string DismissAutomationName =>
        string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["UninstallerSuggestDismissFor"], Name);
}
