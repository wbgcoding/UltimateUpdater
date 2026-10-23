using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.ViewModels;

/// <summary>
/// One "device no longer connected" row (Group A). Deliberately never pre-selected, same rule as
/// <see cref="LeftoverCandidateViewModel"/>: removing a device the user might reconnect tomorrow is not a
/// decision this dialog makes on its own.
/// </summary>
public sealed partial class DriverDeviceCandidateViewModel(DriverDeviceCandidateDto candidate) : ObservableObject
{
    [ObservableProperty]
    private bool isSelected;

    public string InstanceId => candidate.InstanceId;

    public string DisplayName => candidate.DisplayName;

    public override string ToString() => DisplayName;

    public string ClassName => candidate.ClassName;

    public string PackageName => candidate.PackageName;

    public DateTimeOffset? LastConnected => candidate.LastConnected;

    /// <summary>The row's own date text picks its wording from which kind of date it ended up with
    /// (a real connect/disconnect event, or a fallback to when the driver was installed) rather than
    /// always claiming "last connected" for a date that is really an install date.</summary>
    public string LastConnectedText => (LastConnected, candidate.LastConnectedKind) switch
    {
        ({ } value, DriverLastSeenKind.Installed) =>
            string.Format(LocalizationService.Instance["CleanupDriverInstalledDate"], value.ToLocalTime().ToString("d", CultureInfo.CurrentUICulture)),
        ({ } value, _) =>
            string.Format(LocalizationService.Instance["CleanupDriverLastConnected"], value.ToLocalTime().ToString("d", CultureInfo.CurrentUICulture)),
        _ => LocalizationService.Instance["CleanupDriverLastConnectedUnknown"],
    };

    public string? Provider => candidate.Provider;

    public string? OriginalName => candidate.OriginalName;

    public string? StorePath => candidate.StorePath;

    public string? Version => candidate.Version;

    public DateTimeOffset? Date => candidate.Date;

    public long SizeBytes => candidate.SizeBytes;

    /// <summary>"Version X, date" beneath the name; either half is left out (never shown as "unknown")
    /// when this device's bound package could not supply it, and the whole line hides itself
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

    /// <summary>Themed multi-line tooltip: every fact the bound package can supply, one line each, missing
    /// facts simply left out rather than shown as "unknown". Ends with an "installed now" line only when
    /// this device's package has been superseded by a confidently newer one.</summary>
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
        OnPropertyChanged(nameof(LastConnectedText));
        OnPropertyChanged(nameof(VersionDateText));
        OnPropertyChanged(nameof(HasVersionDateText));
        OnPropertyChanged(nameof(DetailTooltipText));
    }
}
