using CommunityToolkit.Mvvm.ComponentModel;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.ViewModels;

/// <summary>
/// One "older Python version" row. Deliberately never pre-selected, same rule as
/// <see cref="LeftoverCandidateViewModel"/>/<see cref="DriverPackageCandidateViewModel"/>: removing an
/// installed interpreter something might still target by its exact version is not a decision this dialog
/// makes on its own.
/// </summary>
public sealed partial class PythonVersionCandidateViewModel(PythonVersionCandidateDto candidate) : ObservableObject
{
    [ObservableProperty]
    private bool isSelected;

    public string PythonExe => candidate.PythonExe;

    public string DisplayLabel => candidate.DisplayLabel;

    public override string ToString() => DisplayLabel;

    public string? SitePackages => candidate.SitePackages;

    public long SizeBytes => candidate.SizeBytes;

    public string SizeText => ByteSize.Format(candidate.SizeBytes);

    public int PackageCount => candidate.PackageCount;

    public string DetailText => string.Format(
        LocalizationService.Instance["CleanupOldPythonVersionDetail"], PythonExe, PackageCount);

    public void RefreshLocalizedText() => OnPropertyChanged(nameof(DetailText));
}
