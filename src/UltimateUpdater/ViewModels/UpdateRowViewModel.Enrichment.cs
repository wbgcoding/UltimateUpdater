using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UltimateUpdater.Services;

namespace UltimateUpdater.ViewModels;

public partial class UpdateRowViewModel
{
    /// <summary>
    /// Filled in after the scan by the background enrichment pass for sources whose listing carries no date
    /// or size. Preferred over the snapshot values on the immutable <see cref="Item"/>.
    /// </summary>
    [ObservableProperty]
    private DateTimeOffset? enrichedPublishDate;

    [ObservableProperty]
    private long? enrichedDownloadSize;

    private DateTimeOffset? EffectivePublishDate => EnrichedPublishDate ?? Item.PublishDate;

    private long? EffectiveDownloadSize => EnrichedDownloadSize ?? Item.DownloadSize;

    /// <summary>Merges a background enrichment result onto the row. Only fills what is unknown, never overwrites a reported figure.</summary>
    public void ApplyEnrichment(
        DateTimeOffset? date, long? size, string? projectUrl = null, string? availableVersion = null,
        string? changelogBody = null, string? changelogUrl = null, string? publisher = null)
    {
        if (date is not null && Item.PublishDate is null)
            EnrichedPublishDate = date;
        if (size is > 0 && Item.DownloadSize is null or 0)
            EnrichedDownloadSize = size;
        if (!string.IsNullOrEmpty(projectUrl))
            EnrichedProjectUrl = projectUrl;
        // only fills a gap: a version the source itself reported is never overwritten
        if (!string.IsNullOrEmpty(availableVersion) && string.IsNullOrEmpty(Item.AvailableVersion))
            EnrichedAvailableVersion = availableVersion;
        // Item.Meta wins when a source filled it at scan time; this only fills the gap for sources whose changelog arrives with the enrichment pass.
        if (!string.IsNullOrEmpty(changelogBody) && string.IsNullOrEmpty(ChangelogText))
            EnrichedChangelogBody = changelogBody;
        if (!string.IsNullOrEmpty(changelogUrl) && string.IsNullOrEmpty(ChangelogUrl))
            EnrichedChangelogUrl = changelogUrl;
        if (!string.IsNullOrEmpty(publisher) && EnrichedPublisher is null)
            EnrichedPublisher = publisher;
    }

    /// <summary>The newest version, for sources whose own listing does not report one (see UpdateEnrichment).</summary>
    [ObservableProperty]
    private string? enrichedAvailableVersion;

    partial void OnEnrichedAvailableVersionChanged(string? value)
    {
        OnPropertyChanged(nameof(AvailableVersion));
        OnPropertyChanged(nameof(HasVersions));
        OnPropertyChanged(nameof(SortVersion));
        OnPropertyChanged(nameof(VersionChangeTooltip));
    }

    /// <summary>A "learn more" link (changelog/homepage) the enrichment pass found; http(s) only.</summary>
    [ObservableProperty]
    private string? enrichedProjectUrl;

    /// <summary>A pip row's publisher, found by the background enrichment pass; see <see cref="Publisher"/>.</summary>
    [ObservableProperty]
    private string? enrichedPublisher;

    partial void OnEnrichedPublisherChanged(string? value)
    {
        OnPropertyChanged(nameof(Publisher));
        OnPropertyChanged(nameof(PublisherDisplay));
        OnPropertyChanged(nameof(HasPublisher));
        OnPropertyChanged(nameof(NameTooltip));
    }

    partial void OnEnrichedProjectUrlChanged(string? value)
    {
        OnPropertyChanged(nameof(HasInfoLink));
        OpenInfoLinkCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Opens the project/changelog page. The URL was validated http(s) at parse time; re-checked here.</summary>
    [RelayCommand(CanExecute = nameof(HasInfoLink))]
    private void OpenInfoLink()
    {
        if (EnrichedProjectUrl is not { } url || _logging is null)
            return;

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            UriLauncher.LaunchDetached(url, _logging);
        }
    }

    /// <summary>
    /// The install directory, resolved in the background after the scan. Null until then or when none is
    /// found, in which case the row offers no "open folder" action.
    /// </summary>
    [ObservableProperty]
    private string? installLocationPath;

    public bool CanOpenFolder => !string.IsNullOrEmpty(InstallLocationPath);

    partial void OnInstallLocationPathChanged(string? value)
    {
        OnPropertyChanged(nameof(CanOpenFolder));
        OpenFolderCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Opens the install directory in Explorer. UriLauncher refuses anything that is not a real rooted path.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenFolder))]
    private void OpenFolder()
    {
        if (InstallLocationPath is { Length: > 0 } path && _logging is not null)
            UriLauncher.LaunchDetached(path, _logging);
    }

    partial void OnEnrichedPublishDateChanged(DateTimeOffset? value)
    {
        OnPropertyChanged(nameof(PublishDateText));
        OnPropertyChanged(nameof(HasPublishDate));
        OnPropertyChanged(nameof(SortDate));
    }

    partial void OnEnrichedDownloadSizeChanged(long? value)
    {
        OnPropertyChanged(nameof(DownloadSizeText));
        OnPropertyChanged(nameof(HasDownloadSize));
        OnPropertyChanged(nameof(SortSize));
        // DownloadSizeBytes announces no change of its own; selection sums need a signal when the size arrives from enrichment.
        OnPropertyChanged(nameof(DownloadSizeBytes));
    }

    public string? ChangelogUrl => Item.Meta.GetValueOrDefault("changelogUrl") ?? EnrichedChangelogUrl;

    /// <summary>
    /// True only for npm: its enrichment never fills the changelog fields, so the button must light up before
    /// any text is known and a click goes to look one up (<c>MainViewModel.TryFetchNpmChangelogAsync</c>).
    /// </summary>
    public bool CanFetchChangelogOnClick => SourceId == "npm";

    /// <summary>True while a click-triggered release fetch is in flight; the button shows a waiting ring instead.</summary>
    [ObservableProperty]
    private bool isChangelogLoading;

    partial void OnIsChangelogLoadingChanged(bool value) => OnPropertyChanged(nameof(ShowChangelogIcon));

    [RelayCommand(CanExecute = nameof(HasChangelog))]
    private async Task ShowChangelog()
    {
        if (_onShowChangelog is null)
            return;

        IsChangelogLoading = true;
        try
        {
            await _onShowChangelog(this);
        }
        finally
        {
            IsChangelogLoading = false;
        }
    }

    /// <summary>Filled by the background enrichment pass when the source's own scan left Item.Meta empty.</summary>
    [ObservableProperty]
    private string? enrichedChangelogBody;

    [ObservableProperty]
    private string? enrichedChangelogUrl;

    partial void OnEnrichedChangelogBodyChanged(string? value) => NotifyChangelogChanged();

    partial void OnEnrichedChangelogUrlChanged(string? value) => NotifyChangelogChanged();

    private void NotifyChangelogChanged()
    {
        OnPropertyChanged(nameof(ChangelogText));
        OnPropertyChanged(nameof(ChangelogUrl));
        OnPropertyChanged(nameof(HasChangelog));
        ShowChangelogCommand.NotifyCanExecuteChanged();
    }
}
