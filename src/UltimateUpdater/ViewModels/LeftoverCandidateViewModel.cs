using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.ViewModels;

/// <summary>
/// One folder the leftover scanner found. Deliberately NEVER pre-selected,
/// IsSelected starts and stays false until the user ticks it themselves, a leftover that cannot be
/// identified with certainty must never be pre-checked for deletion.
/// </summary>
public sealed partial class LeftoverCandidateViewModel(LeftoverCandidateDto candidate) : ObservableObject
{
    [ObservableProperty]
    private bool isSelected;

    public string Path => candidate.Path;

    public string DisplayName => candidate.DisplayName;

    public override string ToString() => DisplayName;

    public long SizeBytes => candidate.SizeBytes;

    public string SizeText => ByteSize.Format(candidate.SizeBytes);

    public DateTimeOffset NewestWriteTime => candidate.NewestWriteTime;

    public string LastTouchedText =>
        string.Format(LocalizationService.Instance["CleanupLeftoverLastUsed"], NewestWriteTime.ToLocalTime().ToString("d", CultureInfo.CurrentUICulture));

    public void RefreshLocalizedText() => OnPropertyChanged(nameof(LastTouchedText));
}
