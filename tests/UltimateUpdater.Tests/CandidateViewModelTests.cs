using System.ComponentModel;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

/// <summary>
/// The rows of the leftover and old-Python dialogs. The load-bearing rule is that nothing is ever ticked for
/// the user: removing an install folder or an interpreter must stay a decision the user makes.
/// </summary>
[Collection("UiLanguage")]
public class CandidateViewModelTests
{
    private static LeftoverCandidateViewModel Leftover(long size = 2048) => new(new LeftoverCandidateDto
    {
        Path = "C:\\Program Files\\OldTool",
        DisplayName = "OldTool",
        SizeBytes = size,
        NewestWriteTime = new DateTimeOffset(2024, 3, 9, 12, 0, 0, TimeSpan.Zero),
    });

    private static PythonVersionCandidateViewModel Python(int venvs = 0, int packages = 12) => new(new PythonVersionCandidateDto
    {
        PythonExe = "C:\\Python39\\python.exe",
        DisplayLabel = "Python 3.9.13",
        SitePackages = "C:\\Python39\\Lib\\site-packages",
        SizeBytes = 5L * 1024 * 1024,
        PackageCount = packages,
        VenvUseCount = venvs,
    });

    [Fact]
    public void A_leftover_candidate_is_never_pre_selected() =>
        Assert.False(Leftover().IsSelected);

    [Fact]
    public void A_python_candidate_is_never_pre_selected() =>
        Assert.False(Python().IsSelected);

    [Fact]
    public void A_leftover_exposes_its_data_and_is_named_by_its_display_name()
    {
        var vm = Leftover(size: 2048);

        Assert.Equal("C:\\Program Files\\OldTool", vm.Path);
        Assert.Equal("OldTool", vm.DisplayName);
        Assert.Equal("OldTool", vm.ToString());
        Assert.Equal(2048, vm.SizeBytes);
        Assert.Equal(ByteSize.Format(2048), vm.SizeText);
    }

    [Fact]
    public void The_last_touched_text_names_the_date_in_the_local_time_zone_of_the_ui_culture()
    {
        LocalizationService.Instance.Apply("en");
        var vm = Leftover();

        var expected = string.Format(LocalizationService.Instance["CleanupLeftoverLastUsed"],
            vm.NewestWriteTime.ToLocalTime().ToString("d", System.Globalization.CultureInfo.CurrentUICulture));

        Assert.Equal(expected, vm.LastTouchedText);
        Assert.StartsWith("Last touched ", vm.LastTouchedText);
        Assert.DoesNotContain("{0}", vm.LastTouchedText);
    }

    [Fact]
    public void Switching_the_language_refreshes_the_leftover_text()
    {
        LocalizationService.Instance.Apply("en");
        try
        {
            var vm = Leftover();
            var before = vm.LastTouchedText;
            var raised = new List<string?>();
            ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            LocalizationService.Instance.Apply("de");
            vm.RefreshLocalizedText();

            Assert.Contains(nameof(LeftoverCandidateViewModel.LastTouchedText), raised);
            Assert.NotEqual(before, vm.LastTouchedText);
        }
        finally
        {
            LocalizationService.Instance.Apply("en");
        }
    }

    [Fact]
    public void Ticking_a_candidate_raises_a_change_notification()
    {
        var vm = Leftover();
        var raised = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.IsSelected = true;

        Assert.Contains(nameof(LeftoverCandidateViewModel.IsSelected), raised);
        Assert.True(vm.IsSelected);
    }

    [Fact]
    public void A_python_candidate_exposes_its_data_and_is_named_by_its_label()
    {
        var vm = Python(packages: 12);

        Assert.Equal("C:\\Python39\\python.exe", vm.PythonExe);
        Assert.Equal("Python 3.9.13", vm.DisplayLabel);
        Assert.Equal("Python 3.9.13", vm.ToString());
        Assert.Equal("C:\\Python39\\Lib\\site-packages", vm.SitePackages);
        Assert.Equal(12, vm.PackageCount);
        Assert.Equal(ByteSize.Format(5L * 1024 * 1024), vm.SizeText);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(7, true)]
    public void The_venv_warning_shows_only_when_a_virtual_environment_still_uses_the_version(int venvs, bool shown) =>
        Assert.Equal(shown, Python(venvs).HasVenvUses);

    [Fact]
    public void The_python_texts_carry_their_numbers()
    {
        LocalizationService.Instance.Apply("en");
        var vm = Python(venvs: 3, packages: 12);

        Assert.Equal("C:\\Python39\\python.exe (12 packages)", vm.DetailText);
        Assert.Equal("Still used by virtual environments (3)", vm.VenvUseText);
        Assert.False(string.IsNullOrWhiteSpace(vm.VenvUseTip));
    }

    [Fact]
    public void Refreshing_a_python_candidate_raises_all_three_language_dependent_properties()
    {
        var vm = Python();
        var raised = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.RefreshLocalizedText();

        Assert.Equal(
            [nameof(PythonVersionCandidateViewModel.DetailText), nameof(PythonVersionCandidateViewModel.VenvUseText), nameof(PythonVersionCandidateViewModel.VenvUseTip)],
            raised);
    }

    [Fact]
    public void A_top_area_row_reads_as_title_and_size_for_screen_readers()
    {
        var row = new CleanupTopAreaViewModel("Browser caches", "1.2 GB", 80);

        Assert.Equal("Browser caches, 1.2 GB", row.ToString());
        Assert.Equal(80, row.BarWidth);
    }
}
