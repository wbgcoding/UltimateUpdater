using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.ViewModels;

/// <summary>
/// One "old driver package" row (Group B). Deliberately never pre-selected, same rule as
/// <see cref="LeftoverCandidateViewModel"/>.
/// </summary>
public sealed partial class DriverPackageCandidateViewModel(DriverPackageCandidateDto candidate) : ObservableObject
{
    [ObservableProperty]
    private bool isSelected;

    public string PackageName => candidate.PackageName;

    public override string ToString() => PackageName;

    public string Provider => candidate.Provider;

    public string ClassName => candidate.ClassName;

    public string? Version => candidate.Version;

    public DateTimeOffset? Date => candidate.Date;

    public long SizeBytes => candidate.SizeBytes;

    public string? OriginalName => candidate.OriginalName;

    public string? StorePath => candidate.StorePath;

    public string SizeText => ByteSize.Format(candidate.SizeBytes);

    /// <summary>"Provider, class" on one line; a version with no provider at all (malformed INF) still
    /// shows the class rather than an empty line.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Provider) ? ClassName : $"{Provider}, {ClassName}";

    /// <summary>"Version X, date" beneath the name; either half is left out (never shown as "unknown")
    /// when this package's own INF could not supply it, and the whole line hides itself
    /// (<see cref="HasVersionDateText"/>) when neither half is known.</summary>
    public string VersionDateText
    {
        get
        {
            var hasVersion = !string.IsNullOrWhiteSpace(Version);
            var hasDate = Date is not null;
            if (hasVersion && hasDate)
                return string.Format(LocalizationService.Instance["CleanupDriverVersionDate"], Version, Date!.Value.ToLocalTime().ToString("d", CultureInfo.CurrentUICulture));
            if (hasVersion)
                return Version!;
            return hasDate ? Date!.Value.ToLocalTime().ToString("d", CultureInfo.CurrentUICulture) : string.Empty;
        }
    }

    public bool HasVersionDateText => !string.IsNullOrEmpty(VersionDateText);

    /// <summary>Themed multi-line tooltip: every fact this package's own INF and driver-store folder can
    /// supply, one line each, missing facts simply left out rather than shown as "unknown". Ends with an
    /// "installed now" line only when this package has been superseded by a confidently newer one.</summary>
    public string DetailTooltipText
    {
        get
        {
            var loc = LocalizationService.Instance;
            var lines = new List<string>();

            void Add(string label, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    lines.Add($"{label}: {value}");
            }

            Add(loc["CleanupDriverProviderLabel"], Provider);
            Add(loc["CleanupDriverClassLabel"], ClassName);
            Add(loc["CleanupDriverInfFileLabel"], PackageName);
            Add(loc["CleanupDriverOriginalNameLabel"], OriginalName);
            Add(loc["CleanupDriverStorePathLabel"], StorePath);
            Add(loc["ColumnVersion"], Version);
            Add(loc["ColumnDate"], Date?.ToLocalTime().ToString("d", CultureInfo.CurrentUICulture));
            Add(loc["ColumnSize"], SizeBytes > 0 ? ByteSize.Format(SizeBytes) : null);

            if (candidate.NewerVersion is { } newerVersion && candidate.NewerDate is { } newerDate)
                lines.Add(string.Format(loc["CleanupDriverInstalledNow"], newerVersion, newerDate.ToLocalTime().ToString("d", CultureInfo.CurrentUICulture)));

            return string.Join("\n", lines);
        }
    }

    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(VersionDateText));
        OnPropertyChanged(nameof(HasVersionDateText));
        OnPropertyChanged(nameof(DetailTooltipText));
    }
}
