using UltimateUpdater.Services;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

/// <summary>A run without admin rights skips mostly files it may not touch; the summary must not call
/// those "in use", so the two counts are worded apart.</summary>
[Collection("UiLanguage")]
public class CleanupSkippedTextTests
{
    private static UltimateUpdater.Services.Cleanup.CleanupTarget Target() =>
        new("test-target", "x", "y", UltimateUpdater.Services.Cleanup.CleanupKind.Directories, DefaultEnabled: true,
            RiskLevel: UltimateUpdater.Services.Cleanup.CleanupRiskLevel.Safe, ConsequenceKey: "x");

    private static string L(string key) => LocalizationService.Instance[key];

    [Fact]
    public void Files_in_use_and_refused_files_get_their_own_sentence()
    {
        var text = CleanupViewModel.FormatSkipped(3, 3072, 2, 2048);

        Assert.Equal(
            string.Format(L("CleanupSkippedFilesSize"), 3, ByteSize.Format(3072)) + " "
            + string.Format(L("CleanupProtectedFilesSize"), 2, ByteSize.Format(2048)),
            text);
    }

    [Fact]
    public void Only_refused_files_never_read_as_in_use()
    {
        var text = CleanupViewModel.FormatSkipped(0, 0, 1, 0);

        Assert.Equal(string.Format(L("CleanupProtectedFilesOne"), 1), text);
    }

    [Fact]
    public void A_row_names_both_shares_of_what_stayed()
    {
        var item = new CleanupItemViewModel(Target(), () => { }) { SkippedBytes = 3072, ProtectedBytes = 1024 };

        Assert.Equal(
            string.Format(L("CleanupInUse"), ByteSize.Format(2048)) + ", " + string.Format(L("CleanupProtected"), ByteSize.Format(1024)),
            item.SkippedText);
    }

    [Fact]
    public void A_history_area_keeps_the_refused_share_apart()
    {
        var area = new CleanupHistoryAreaViewModel(new UltimateUpdater.Services.Cleanup.CleanupHistoryArea
        {
            Id = "test-target", TitleKey = "CleanupInUse", SkippedBytes = 1024, ProtectedBytes = 1024,
        });

        Assert.Equal(string.Format(L("CleanupProtected"), ByteSize.Format(1024)), area.SkippedText);
    }
}
