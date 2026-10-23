using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

public partial class CleanupViewModelTests
{
    private static void SelectOnly(CleanupViewModel viewModel, params CleanupItemViewModel[] items)
    {
        foreach (var item in viewModel.Items)
            item.IsSelected = false;
        foreach (var item in items)
            item.IsSelected = true;
    }

    private static CleanupItemViewModel RecyclableFileTarget(CleanupViewModel viewModel) =>
        viewModel.Items.First(i => i.Target.Kind is CleanupKind.Directories or CleanupKind.AppCacheSweep
            && !i.Target.NeedsElevation);

    [Fact]
    public void Permanent_texts_are_used_when_the_recycle_setting_is_off()
    {
        var settings = CreateSettings();
        settings.Current.CleanupRecycleInsteadOfDelete = false;
        var viewModel = Create(settings);
        SelectOnly(viewModel, RecyclableFileTarget(viewModel));

        var (headline, warning) = viewModel.BuildConfirmationTexts();

        var loc = LocalizationService.Instance;
        Assert.Equal(string.Format(loc["CleanupConfirmHeadline"], viewModel.SelectedText), headline);
        Assert.Equal(loc["CleanupConfirmWarning"], warning);
    }

    [Theory]
    [InlineData("en", "Delete 2.0 GB permanently?")]
    [InlineData("de", "2,0 GB endgültig löschen?")]
    public void The_confirmation_headline_sizes_follow_the_chosen_language_not_the_windows_one(string language, string expected)
    {
        var previousCulture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            LocalizationService.Instance.Apply(language);
            var settings = CreateSettings();
            settings.Current.CleanupRecycleInsteadOfDelete = false;
            var viewModel = Create(settings);
            var target = RecyclableFileTarget(viewModel);
            target.Bytes = 2L * 1024 * 1024 * 1024;
            SelectOnly(viewModel, target);

            var (headline, _) = viewModel.BuildConfirmationTexts();

            Assert.Equal(expected, headline);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previousCulture;
            LocalizationService.Instance.Apply("en");
        }
    }

    [Fact]
    public void Recycle_texts_are_used_when_every_selected_file_target_goes_to_the_recycle_bin()
    {
        var settings = CreateSettings();
        settings.Current.CleanupRecycleInsteadOfDelete = true;
        var viewModel = Create(settings);
        SelectOnly(viewModel, RecyclableFileTarget(viewModel));

        var (headline, warning) = viewModel.BuildConfirmationTexts();

        var loc = LocalizationService.Instance;
        Assert.Equal(string.Format(loc["CleanupConfirmHeadlineRecycle"], viewModel.SelectedText), headline);
        Assert.Equal(loc["CleanupConfirmWarningRecycle"], warning);
    }

    [Fact]
    public void Recycle_texts_stay_when_only_non_file_targets_are_added()
    {
        var settings = CreateSettings();
        settings.Current.CleanupRecycleInsteadOfDelete = true;
        var viewModel = Create(settings);
        var registry = viewModel.Items.First(i => i.Target.Kind == CleanupKind.RegistryCleanup);
        SelectOnly(viewModel, RecyclableFileTarget(viewModel), registry);

        var (_, warning) = viewModel.BuildConfirmationTexts();

        Assert.Equal(LocalizationService.Instance["CleanupConfirmWarningRecycle"], warning);
    }

    [Fact]
    public void Permanent_texts_are_kept_when_a_selected_file_target_needs_elevation()
    {
        var settings = CreateSettings();
        settings.Current.CleanupRecycleInsteadOfDelete = true;
        var viewModel = Create(settings);
        var elevated = viewModel.Items.First(i => i.Target.Kind == CleanupKind.Directories && i.Target.NeedsElevation);
        SelectOnly(viewModel, RecyclableFileTarget(viewModel), elevated);

        var (_, warning) = viewModel.BuildConfirmationTexts();

        Assert.Equal(LocalizationService.Instance["CleanupConfirmWarning"], warning);
    }

    [Fact]
    public void Permanent_texts_are_kept_when_the_recycle_bin_row_is_selected()
    {
        var settings = CreateSettings();
        settings.Current.CleanupRecycleInsteadOfDelete = true;
        var viewModel = Create(settings);
        SelectOnly(viewModel, RecyclableFileTarget(viewModel), Find(viewModel, "recycle-bin"));

        var (_, warning) = viewModel.BuildConfirmationTexts();

        Assert.Equal(LocalizationService.Instance["CleanupConfirmWarning"], warning);
    }

    [Fact]
    public void Permanent_texts_are_kept_for_file_targets_the_recycle_setting_does_not_reach()
    {
        var settings = CreateSettings();
        settings.Current.CleanupRecycleInsteadOfDelete = true;
        var viewModel = Create(settings);
        var pipLeftovers = viewModel.Items.First(i => i.Target.Kind == CleanupKind.PipInstallLeftovers);
        SelectOnly(viewModel, RecyclableFileTarget(viewModel), pipLeftovers);

        var (_, warning) = viewModel.BuildConfirmationTexts();

        Assert.Equal(LocalizationService.Instance["CleanupConfirmWarning"], warning);
    }

    [Fact]
    public void The_confirmation_detail_names_a_selected_driver_cleanup()
    {
        LocalizationService.Instance.Apply("en");
        var viewModel = Create(CreateSettings());
        var drivers = viewModel.Items.First(i => i.Target.Id == "driver-cleanup");
        SelectOnly(viewModel, drivers);

        var detail = viewModel.BuildConfirmationDetail();

        Assert.Contains(drivers.Title, detail);
    }
}
