using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UltimateUpdater.Services;

namespace UltimateUpdater.ViewModels;

/// <summary>
/// Backs the changelog popup. Opened on deliberate request from a row's info icon; shows
/// whatever real changelog text and/or release-page link the source found, never both null, since the
/// row's own HasChangelog gates whether the icon appears at all.
/// </summary>
public partial class ChangelogViewModel : ObservableObject
{
    public LocalizationService Loc => LocalizationService.Instance;

    public string Name { get; }

    public string? VersionText { get; }

    public bool HasVersionText => !string.IsNullOrEmpty(VersionText);

    /// <summary>Markdown already stripped to plain text; see <see cref="ChangelogFormatter"/>.</summary>
    public string Text { get; }

    public bool HasText => !string.IsNullOrWhiteSpace(Text);

    public string? Url { get; }

    public bool HasUrl => !string.IsNullOrEmpty(Url);

    public string WindowTitle => $"{Name}: {Loc["ChangelogTitle"]}";

    private readonly LoggingService _logging;

    public ChangelogViewModel(string name, string? versionText, string? rawText, string? url, LoggingService logging)
    {
        Name = name;
        VersionText = versionText;
        Text = ChangelogFormatter.Strip(rawText);
        Url = url;
        _logging = logging;
    }

    /// <summary>
    /// Re-validates http(s) here too, the same defense-in-depth <see cref="UpdateRowViewModel.OpenInfoLink"/>
    /// already applies for its own URL; most ChangelogUrl producers are a fixed constant or built from an
    /// escaped id/version, but a couple (Steam's news API, Windows Update's MoreInfoUrls) read
    /// third-party content, and every value round-trips through EnrichmentCache's on-disk JSON before it
    /// ever reaches this button.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasUrl))]
    private void OpenRelease()
    {
        if (Url is { Length: > 0 } url
            && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            UriLauncher.LaunchDetached(url, _logging);
        }
    }
}
