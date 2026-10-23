using System.IO;
using System.Reflection;
using UltimateUpdater.Services;
using UltimateUpdater.Services.SelfUpdate;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

/// <summary>
/// Language switches must reach the update status line and the contacted-servers list, and every list
/// item type must announce a readable name instead of its class name.
/// </summary>
[Collection("UiLanguage")]
public class SettingsLocalizationAndNamesTests
{
    private static SettingsViewModel Create(Func<Task<SelfUpdateCheckOutcome>>? check = null)
    {
        var logging = new LoggingService();
        var path = Path.Combine(Directory.CreateTempSubdirectory("wu-settings-loc-").FullName, "settings.json");
        return new SettingsViewModel(
            new SettingsService(logging, path), logging, new ThemeService(), new AutostartService(logging),
            checkForUpdates: check);
    }

    [Fact]
    public async Task The_up_to_date_status_follows_a_live_language_switch()
    {
        LocalizationService.Instance.Apply("de");
        var viewModel = Create(() => Task.FromResult(SelfUpdateCheckOutcome.None));
        viewModel.SelectedLanguage = "de";

        await viewModel.CheckForUpdatesCommand.ExecuteAsync(null);
        Assert.Equal(LocalizationService.Instance["SettingsUpToDate"], viewModel.UpdateStatusText);
        var german = viewModel.UpdateStatusText;

        viewModel.SelectedLanguage = "en";

        Assert.NotEqual(german, viewModel.UpdateStatusText);
        Assert.Equal(LocalizationService.Instance["SettingsUpToDate"], viewModel.UpdateStatusText);
        Assert.Contains(viewModel.UpdateStatusText, viewModel.VersionAndStatus);
        LocalizationService.Instance.Apply("de");
    }

    [Fact]
    public void Network_target_purposes_are_localized_and_show_no_code_paths()
    {
        LocalizationService.Instance.Apply("de");
        var viewModel = Create();
        viewModel.SelectedLanguage = "de";

        foreach (var target in viewModel.NetworkTargets)
        {
            Assert.DoesNotContain(".cs", target.Purpose);
            Assert.DoesNotContain("/", target.Purpose.Replace("http://", string.Empty));
            Assert.DoesNotContain(".cs", target.ToString());
        }

        var german = viewModel.NetworkTargets.Select(t => t.Purpose).ToList();
        viewModel.SelectedLanguage = "en";
        var english = viewModel.NetworkTargets.Select(t => t.Purpose).ToList();
        LocalizationService.Instance.Apply("de");

        Assert.NotEqual(german, english);
        Assert.All(german, purpose => Assert.False(string.IsNullOrWhiteSpace(purpose)));
    }

    [Theory]
    [InlineData(typeof(HistoryRowViewModel))]
    [InlineData(typeof(HistorySourceFilter))]
    [InlineData(typeof(SettingsViewModel.SourceToggle))]
    [InlineData(typeof(SettingsViewModel.NetworkTarget))]
    [InlineData(typeof(SettingsViewModel.ExcludedPathRow))]
    [InlineData(typeof(CleanupGroupViewModel))]
    [InlineData(typeof(CleanupItemViewModel))]
    [InlineData(typeof(CleanupEntryViewModel))]
    [InlineData(typeof(CleanupTopAreaViewModel))]
    [InlineData(typeof(CleanupHistoryRowViewModel))]
    [InlineData(typeof(CleanupHistoryAreaViewModel))]
    [InlineData(typeof(CleanupHistoryTopCategoryViewModel))]
    [InlineData(typeof(LeftoverCandidateViewModel))]
    [InlineData(typeof(DriverDeviceCandidateViewModel))]
    [InlineData(typeof(DriverPackageCandidateViewModel))]
    [InlineData(typeof(PythonVersionCandidateViewModel))]
    public void List_item_types_override_ToString_so_screen_readers_get_a_name(Type type)
    {
        var method = type.GetMethod(nameof(ToString), BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
        Assert.NotNull(method);
        Assert.Equal(type, method!.DeclaringType);
    }


    [Theory]
    [InlineData(SelfUpdateCheckOutcome.None, "SettingsUpToDate")]
    [InlineData(SelfUpdateCheckOutcome.Found, "SelfUpdateTitle")]
    [InlineData(SelfUpdateCheckOutcome.Failed, "SettingsCheckFailed")]
    public async Task The_check_status_line_names_each_outcome(SelfUpdateCheckOutcome outcome, string expectedKey)
    {
        LocalizationService.Instance.Apply("en");
        var viewModel = Create(() => Task.FromResult(outcome));

        await viewModel.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.Equal(LocalizationService.Instance[expectedKey], viewModel.UpdateStatusText);
        LocalizationService.Instance.Apply("de");
    }
}
