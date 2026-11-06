using UltimateUpdater.Services;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

public partial class MainViewModelTests
{
    [Theory]
    [InlineData("en", "Clean up", "Clean up 47 %")]
    [InlineData("de", "Bereinigen", "Bereinigen 47 %")]
    public void The_toolbar_cleanup_button_carries_the_progress_while_a_cleanup_runs(
        string language, string resting, string running)
    {
        WpfHarness.OnUiThread(() =>
        {
            var rig = CreatePrescanRig(settingOn: false);
            LocalizationService.Instance.Apply(language);
            var viewModel = rig.ViewModel;
            viewModel.RefreshLocalizedText();
            var cleanup = viewModel.EnsureCleanupViewModel();
            var changed = new List<string?>();
            viewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

            Assert.Equal(resting, viewModel.ToolbarCleanupText);

            cleanup.CleanPercent = 47;
            Assert.Equal(resting, viewModel.ToolbarCleanupText);

            cleanup.Phase = CleanupPhase.Cleaning;
            Assert.Equal(running, viewModel.ToolbarCleanupText);
            Assert.Contains(nameof(MainViewModel.ToolbarCleanupText), changed);

            changed.Clear();
            cleanup.CleanPercent = 48.4;
            Assert.EndsWith("48 %", viewModel.ToolbarCleanupText);
            Assert.Contains(nameof(MainViewModel.ToolbarCleanupText), changed);

            cleanup.Phase = CleanupPhase.Finished;
            Assert.Equal(resting, viewModel.ToolbarCleanupText);
        });
    }

    [Fact]
    public void The_toolbar_cleanup_button_follows_a_language_switch_while_it_runs()
    {
        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("en");
            var rig = CreatePrescanRig(settingOn: false);
            var viewModel = rig.ViewModel;
            var cleanup = viewModel.EnsureCleanupViewModel();
            cleanup.CleanPercent = 30;
            cleanup.Phase = CleanupPhase.Cleaning;
            Assert.Equal("Clean up 30 %", viewModel.ToolbarCleanupText);

            LocalizationService.Instance.Apply("de");
            viewModel.RefreshLocalizedText();

            Assert.Equal("Bereinigen 30 %", viewModel.ToolbarCleanupText);
        });
    }
}
