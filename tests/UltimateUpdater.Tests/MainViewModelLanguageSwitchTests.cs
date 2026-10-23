using System.ComponentModel;
using UltimateUpdater.Services;
using Xunit;

namespace UltimateUpdater.Tests;

/// <summary>Settings is non-modal and switches the language live: the main window's banners that build
/// their text in code must follow, not keep the language they were shown in.</summary>
[Collection("wpf")]
public class MainViewModelLanguageSwitchTests
{
    [Fact]
    public void The_reboot_and_cleanup_banners_follow_a_language_switch()
    {
        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, _, _) = WindowLayoutTests.CreateMainViewModel("de");
            LocalizationService.Instance.Apply("de");
            viewModel.ShowRebootNotice(2);
            viewModel.ShowCleanupReminder(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
            var germanReboot = viewModel.RebootInfoText;
            var germanReminder = viewModel.CleanupReminderText;

            var raised = new List<string?>();
            ((INotifyPropertyChanged)viewModel).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            LocalizationService.Instance.Apply("en");
            viewModel.RefreshLocalizedText();

            Assert.Contains(nameof(viewModel.RebootInfoText), raised);
            Assert.Contains(nameof(viewModel.CleanupReminderText), raised);
            Assert.Equal(string.Format(LocalizationService.Instance["RebootRequiredInfo"], 2), viewModel.RebootInfoText);
            Assert.NotEqual(germanReboot, viewModel.RebootInfoText);
            Assert.NotEqual(germanReminder, viewModel.CleanupReminderText);

            LocalizationService.Instance.Apply("de");
        });
    }
}
