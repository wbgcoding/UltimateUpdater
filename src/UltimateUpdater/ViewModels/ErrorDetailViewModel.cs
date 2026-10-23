using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.ViewModels;

/// <summary>
/// Backs the error-details window: the raw, unlocalized output from the tool that failed, shown only
/// when the user opens it deliberately (SECURITY.md). It reports what already happened: nothing here
/// changes state.
/// </summary>
public partial class ErrorDetailViewModel : ObservableObject
{
    public LocalizationService Loc => LocalizationService.Instance;

    public string Name { get; }

    public string SourceDisplay { get; }

    /// <summary>"1.2.3 → 1.2.4", or just the target version, or null when the row carries none.</summary>
    public string? VersionChange { get; }

    public bool HasVersionChange => !string.IsNullOrEmpty(VersionChange);

    /// <summary>The exact text the failing tool produced. English, and may contain the user's own paths.</summary>
    public string Detail { get; }

    /// <summary>
    /// Only set by the history window, where <see cref="Detail"/> may not be the entry's own stored
    /// output: null for a live row in the main table (the header line stays as it always was), one of
    /// "ErrorDetailSourceLog"/"ErrorDetailSourceShort" otherwise: see HistoryWindow.xaml.cs's own
    /// three-tier fallback.
    /// </summary>
    public string? DetailSourceKey { get; }

    public bool HasDetailSource => !string.IsNullOrEmpty(DetailSourceKey);

    // LocalizationService.Instance[...], not the Loc[...] alias: a DYNAMIC key (no quotes) through Loc[]
    // is still matched by LocalizationCompletenessTests' broad literal-usage scan (its regex accepts an
    // optional quote), which then treats the variable NAME itself as an undefined resx key. The direct
    // form is the established way this codebase spells a dynamic lookup (InstallErrorExplainer.Describe).
    public string DetailSourceText => DetailSourceKey is null ? string.Empty : LocalizationService.Instance[DetailSourceKey];

    /// <summary>
    /// The plain-language "what happened" + "what now" line, already localized by the
    /// caller (<c>MainViewModel.ShowErrorDetail</c> via <see cref="Services.Sources.InstallErrorExplainer"/>).
    /// Null when no code could be recognized at all. <see cref="HasExplanation"/> is the binding gate, so
    /// an unrecognized failure shows no explanation line rather than an empty one.
    /// </summary>
    public string? Explanation { get; }

    public bool HasExplanation => !string.IsNullOrEmpty(Explanation);

    /// <summary>
    /// Whether this failure was classified as "interactively maybe fixable". The actual retry lives only
    /// in the row's own context menu (<see cref="UpdateRowViewModel.RetryInteractiveCommand"/>); this
    /// flag gates the pointer text below telling the reader where to find it.
    /// </summary>
    public bool CanRetryInteractive { get; }

    public string WindowTitle => $"{Name}: {Loc["ErrorDetailTitle"]}";

    /// <summary>Where the log files are, shown so the button below is not a leap of faith: same property
    /// name and wording as the settings window's own log-folder card.</summary>
    public string LogFolder => AppPaths.LogsDirectory;

    /// <summary>Turns the Copy button into a brief "Copied" acknowledgement.</summary>
    [ObservableProperty]
    private bool copied;

    /// <summary>The clipboard refused the report; an inline line says so and where the saved copy is.</summary>
    [ObservableProperty]
    private bool copyFailed;

    /// <summary>How long the "Copied" or failure line stays; settable so a test does not wait the real time.</summary>
    internal TimeSpan CopyFeedbackDuration { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Test seam for the clipboard write.</summary>
    internal Action<string> ClipboardWriter { get; set; } = System.Windows.Clipboard.SetText;

    private System.Windows.Threading.DispatcherTimer? _feedbackTimer;

    /// <summary>
    /// A code this app's own explainer could not place at all (<see cref="HasExplanation"/> is
    /// false), found by <see cref="InstallErrorExplainer.TryFindUnknownCode"/>'s broader,
    /// non-vendor-specific reading of the same raw text - null whenever an explanation already exists
    /// (nothing "unexplained" to look up) or when even that broader read finds no code-shaped text at all.
    /// </summary>
    private readonly (int Code, bool IsHresult)? _unknownCode;

    public bool ShowWhatDoesThisMeanButton => _unknownCode is not null && !HasWindowsMeaning;

    /// <summary>Set once the button is clicked: the resolved Windows text, or the "no meaning" line when
    /// Windows itself has none for this code either. Null until then, which is also what keeps the
    /// button itself visible (<see cref="ShowWhatDoesThisMeanButton"/>).</summary>
    [ObservableProperty]
    private string? windowsMeaningText;

    public bool HasWindowsMeaning => !string.IsNullOrEmpty(WindowsMeaningText);

    partial void OnWindowsMeaningTextChanged(string? value)
    {
        OnPropertyChanged(nameof(HasWindowsMeaning));
        OnPropertyChanged(nameof(ShowWhatDoesThisMeanButton));
    }

    private readonly LoggingService _logging;

    public ErrorDetailViewModel(
        string name, string sourceDisplay, string? versionChange, string detail, LoggingService logging,
        string? explanation = null, bool canRetryInteractive = false,
        string? detailSourceKey = null)
    {
        Name = name;
        SourceDisplay = sourceDisplay;
        VersionChange = versionChange;
        Detail = detail;
        Explanation = explanation;
        CanRetryInteractive = canRetryInteractive;
        _logging = logging;
        DetailSourceKey = detailSourceKey;
        _unknownCode = explanation is null ? InstallErrorExplainer.TryFindUnknownCode(detail) : null;
    }

    /// <summary>
    /// Resolves the code Windows itself already knows how to describe, entirely offline
    /// (Marshal/Win32Exception both read the local system message table, no network involved, matching
    /// this app's own "a source never fetches anything just to check itself" rule). The two APIs the
    /// item's own spec names split by shape: a small decimal reads as a Win32 error code
    /// (<see cref="System.Runtime.InteropServices.Marshal.GetPInvokeErrorMessage"/>), a hex value as an
    /// HRESULT-shaped one (<see cref="System.ComponentModel.Win32Exception"/>). Both APIs fall back to a
    /// generic "Unknown error (0x...)" line, embedding the code's own hex form, when Windows has nothing
    /// for it - the one locale-independent signal available to tell "resolved" from "not resolved" apart.
    /// </summary>
    [RelayCommand]
    private void WhatDoesThisMean()
    {
        if (_unknownCode is not { } found)
            return;

        string? text;
        try
        {
            text = found.IsHresult
                ? new System.ComponentModel.Win32Exception(found.Code).Message
                : System.Runtime.InteropServices.Marshal.GetPInvokeErrorMessage(found.Code);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            _logging.Warn($"Could not resolve the Windows error text: {ex.Message}");
            text = null;
        }

        WindowsMeaningText = IsMissingWindowsText(text, found.Code) ? Loc["ErrorNoMeaning"] : text;
    }

    private static bool IsMissingWindowsText(string? text, int code) =>
        string.IsNullOrWhiteSpace(text)
        || text.Contains($"0x{code:x}", StringComparison.OrdinalIgnoreCase)
        || text.Contains("unknown error", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// One click builds the finished report (<see cref="ErrorReportBuilder"/>) and copies
    /// THAT, not the raw <see cref="Detail"/> text any more; the raw text stays reachable through the
    /// output box's own selection for anyone who wants it verbatim. The same report is also saved to
    /// the last-ten reports folder, so a copy the user later loses (clipboard overwritten) is not gone.
    /// </summary>
    [RelayCommand]
    private void Copy()
    {
        var report = ErrorReportBuilder.Build(Name, SourceDisplay, VersionChange, Detail);

        try
        {
            ClipboardWriter(report);
            Copied = true;
            CopyFailed = false;
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // the clipboard can be held open by another process; the saved copy below is still there
            Copied = false;
            CopyFailed = true;
        }

        ErrorReportBuilder.Save(report, _logging);
        RestartFeedbackTimer();
    }

    private void RestartFeedbackTimer()
    {
        _feedbackTimer?.Stop();
        _feedbackTimer = new System.Windows.Threading.DispatcherTimer { Interval = CopyFeedbackDuration };
        _feedbackTimer.Tick += (_, _) =>
        {
            _feedbackTimer?.Stop();
            Copied = false;
            CopyFailed = false;
        };
        _feedbackTimer.Start();
    }
}
