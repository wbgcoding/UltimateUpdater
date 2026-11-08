using UltimateUpdater.Services;
using UltimateUpdater.Services.SelfUpdate;

namespace UltimateUpdater.Tests;

public partial class MainViewModelTests
{
    /// <summary>The user said no to the elevation prompt: that is an answer, not a failure. No dialog, the
    /// banner stays, and the next click can install again.</summary>
    [Fact]
    public void A_declined_elevation_prompt_shows_no_failure_dialog_and_keeps_the_offer()
    {
        var warnings = new List<string>();
        Dialogs.WarnObserverForTests = warnings.Add;
        try
        {
            WpfHarness.OnUiThread(() =>
            {
                var viewModel = Create();
                var exited = false;
                viewModel.OfferSelfUpdateForTest(OfferedUpdate());
                viewModel.SelfUpdateDownloadForTest = (_, _) =>
                    Task.FromResult(new SelfUpdateDownloadResult(SelfUpdateDownloadOutcome.Downloaded, "verified.exe"));
                viewModel.SelfUpdateApplyForTest = _ => SelfUpdateApplyResult.Declined;
                viewModel.SelfUpdateExitForTest = () => exited = true;

                Pump(viewModel.InstallSelfUpdateCommand.ExecuteAsync(null));

                Assert.Empty(warnings);
                Assert.False(exited);
                Assert.True(viewModel.SelfUpdateAvailable);
                Assert.False(viewModel.SelfUpdateInProgress);
                Assert.True(viewModel.InstallSelfUpdateCommand.CanExecute(null));
            });
        }
        finally
        {
            Dialogs.WarnObserverForTests = null;
        }
    }

    /// <summary>After a decline the verified file is still held: the second click applies it without a
    /// second download and without a failure dialog.</summary>
    [Fact]
    public void A_second_click_with_a_held_verified_file_makes_no_download()
    {
        var warnings = new List<string>();
        Dialogs.WarnObserverForTests = warnings.Add;
        try
        {
            WpfHarness.OnUiThread(() =>
            {
                var viewModel = Create();
                var downloads = 0;
                var applied = new List<string>();
                var exited = new TaskCompletionSource();
                var results = new Queue<SelfUpdateApplyResult>([SelfUpdateApplyResult.Declined, SelfUpdateApplyResult.Started]);
                string? held = null;
                viewModel.OfferSelfUpdateForTest(OfferedUpdate());
                viewModel.SelfUpdateHeldPathForTest = _ => held;
                viewModel.SelfUpdateDownloadForTest = (_, _) =>
                {
                    downloads++;
                    return Task.FromResult(new SelfUpdateDownloadResult(SelfUpdateDownloadOutcome.Downloaded, "verified.exe"));
                };
                viewModel.SelfUpdateApplyForTest = path =>
                {
                    applied.Add(path);
                    var result = results.Dequeue();
                    if (result == SelfUpdateApplyResult.Declined)
                        held = path;
                    return result;
                };
                viewModel.SelfUpdateExitForTest = () => exited.TrySetResult();

                Pump(viewModel.InstallSelfUpdateCommand.ExecuteAsync(null));
                Pump(viewModel.InstallSelfUpdateCommand.ExecuteAsync(null));
                Pump(exited.Task);

                Assert.Equal(1, downloads);
                Assert.Equal(["verified.exe", "verified.exe"], applied);
                Assert.Empty(warnings);
            });
        }
        finally
        {
            Dialogs.WarnObserverForTests = null;
        }
    }
}
