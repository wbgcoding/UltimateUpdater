using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.ViewModels.Popups;

/// <summary>Backs the error-details window: the raw, unlocalized output from the tool that failed, shown only when the user opens it deliberately. Nothing here changes state.</summary>
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

    /// <summary>Set only by the history window, where <see cref="Detail"/> may not be the entry's own stored output: null for a live row, otherwise "ErrorDetailSourceLog" or "ErrorDetailSourceShort" (the history window's three-tier fallback).</summary>
    public string? DetailSourceKey { get; }

    public bool HasDetailSource => !string.IsNullOrEmpty(DetailSourceKey);

    // LocalizationService.Instance[...], not the Loc[...] alias: LocalizationCompletenessTests' literal-usage scan would match a dynamic key through Loc[] and treat the variable name as an undefined resx key.
    public string DetailSourceText => DetailSourceKey is null ? string.Empty : LocalizationService.Instance[DetailSourceKey];

    /// <summary>The plain-language "what happened" and "what now" line, already localized by the caller. Null when no code was recognized; <see cref="HasExplanation"/> gates the binding.</summary>
    public string? Explanation { get; }

    public bool HasExplanation => !string.IsNullOrEmpty(Explanation);

    /// <summary>Whether this failure was classified as interactively fixable. The retry itself lives in the row's context menu (<see cref="UpdateRowViewModel.RetryInteractiveCommand"/>); this flag gates the text pointing there.</summary>
    public bool CanRetryInteractive { get; }

    /// <summary>The reinstall the row offers for winget's different-install-technology refusal; null for every other failure, which hides the button. The window only forwards the click to the row's own flow.</summary>
    private readonly Action? _reinstall;

    public bool ShowReinstallButton => _reinstall is not null;

    [RelayCommand]
    private void Reinstall() => _reinstall?.Invoke();

    public string WindowTitle => $"{Name}: {Loc["ErrorDetailTitle"]}";

    /// <summary>Where the log files are; same wording as the settings window's log-folder card.</summary>
    public string LogFolder => AppPaths.LogsDirectory;

    /// <summary>Test seam for opening the log folder in Explorer.</summary>
    internal Action<string> FolderOpener { get; set; }

    /// <summary>Opens the folder the failing tool's log lives in; the settings window's button does the same.</summary>
    [RelayCommand]
    private void OpenLogFolder() => FolderOpener(LogFolder);

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

    /// <summary>A code the app's own explainer could not place (<see cref="HasExplanation"/> is false), found by the broader reading of <see cref="InstallErrorExplainer.TryFindUnknownCode"/>. Null when an explanation exists or no code-shaped text is found.</summary>
    private readonly (int Code, bool IsHresult)? _unknownCode;

    public bool ShowWhatDoesThisMeanButton => _unknownCode is not null && !HasWindowsMeaning;

    /// <summary>Set once the button is clicked: the resolved Windows text, or the "no meaning" line. Null until then, which keeps the button visible (<see cref="ShowWhatDoesThisMeanButton"/>).</summary>
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
        string? detailSourceKey = null, Action? reinstall = null)
    {
        Name = name;
        SourceDisplay = sourceDisplay;
        VersionChange = versionChange;
        Detail = detail;
        Explanation = explanation;
        CanRetryInteractive = canRetryInteractive;
        _reinstall = reinstall;
        _logging = logging;
        FolderOpener = folder => UriLauncher.OpenFolderDetached(folder, "log folder", _logging);
        DetailSourceKey = detailSourceKey;
        _unknownCode = explanation is null ? InstallErrorExplainer.TryFindUnknownCode(detail) : null;
    }

    /// <summary>Resolves the code through Windows' own local message table, offline, so nothing is fetched just to look it up. A small decimal reads as a Win32 error (<see cref="System.Runtime.InteropServices.Marshal.GetPInvokeErrorMessage"/>), a hex value as an HRESULT (<see cref="System.ComponentModel.Win32Exception"/>). Both fall back to a generic "Unknown error (0x...)" line when Windows has nothing, the one locale-independent signal for "not resolved".</summary>
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

    /// <summary>Builds the finished report (<see cref="ErrorReportBuilder"/>) and copies that, not the raw <see cref="Detail"/>; the raw text stays selectable in the output box. The report is also saved to the last-ten reports folder, so a lost clipboard is not a lost report.</summary>
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
            // The clipboard can be held open by another process; the saved copy is still there.
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
