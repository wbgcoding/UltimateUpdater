using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UltimateUpdater.Services;
using UltimateUpdater.ViewModels;
using UltimateUpdater.Views;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace UltimateUpdater.Tests;

/// <summary>The accent decision (fixed Windows blue unless the user opts into the Windows accent), its
/// persistence, and the settings switch that drives it.</summary>
[Collection("wpf")]
public class SystemAccentTests
{
    private static readonly Color WindowsBlue = Color.FromRgb(0x00, 0x78, 0xD4);

    [Theory]
    [InlineData("system", false, false, true)]
    [InlineData("light", false, false, true)]
    [InlineData("dark", false, false, true)]
    [InlineData("system", true, false, false)]
    [InlineData("light", true, false, false)]
    [InlineData("dark", true, false, false)]
    [InlineData("system", false, true, false)]
    [InlineData("dark", false, true, false)]
    [InlineData("light", true, true, false)]
    public void Light_dark_and_system_use_the_fixed_blue_unless_windows_accent_or_high_contrast(
        string theme, bool useSystemAccent, bool highContrast, bool expectBlue)
    {
        var accent = ThemeService.ResolveAccent(theme, useSystemAccent, highContrast);

        if (expectBlue)
            Assert.Equal(WindowsBlue, accent);
        else
            Assert.Null(accent);
    }

    [Theory]
    [InlineData("hacker", false)]
    [InlineData("hacker", true)]
    [InlineData("neon", false)]
    [InlineData("neon", true)]
    public void The_named_themes_keep_their_own_accent_whatever_the_setting_says(string theme, bool useSystemAccent)
    {
        var accent = ThemeService.ResolveAccent(theme, useSystemAccent, highContrast: false);

        Assert.NotNull(accent);
        Assert.NotEqual(WindowsBlue, accent);
        Assert.Null(ThemeService.ResolveAccent(theme, useSystemAccent, highContrast: true));
    }

    [Fact]
    public void The_setting_defaults_to_off_and_an_older_file_without_it_loads_as_off()
    {
        var logging = new LoggingService();
        var path = Path.Combine(Directory.CreateTempSubdirectory("wu-accent-").FullName, "settings.json");
        File.WriteAllText(path, "{\"ui\":{\"theme\":\"dark\"}}");

        var settings = new SettingsService(logging, path);

        Assert.False(settings.Current.Ui.UseSystemAccent);
    }

    [Fact]
    public void The_switch_persists_and_reloads()
    {
        var logging = new LoggingService();
        var path = Path.Combine(Directory.CreateTempSubdirectory("wu-accent-").FullName, "settings.json");
        var settings = new SettingsService(logging, path);
        var viewModel = new SettingsViewModel(settings, logging, new ThemeService(), new AutostartService(logging));
        Assert.False(viewModel.UseSystemAccent);

        viewModel.UseSystemAccent = true;

        Assert.True(settings.Current.Ui.UseSystemAccent);
        settings.Flush();
        Assert.True(new SettingsService(logging, path).Current.Ui.UseSystemAccent);

        settings.Current.Ui.UseSystemAccent = false;
        viewModel.ReloadFromSettings();
        Assert.False(viewModel.UseSystemAccent);
    }

    [Theory]
    [InlineData("system", true)]
    [InlineData("light", true)]
    [InlineData("dark", true)]
    [InlineData("hacker", false)]
    [InlineData("neon", false)]
    public void The_switch_is_only_available_where_the_accent_can_change(string theme, bool expected)
    {
        var logging = new LoggingService();
        var path = Path.Combine(Directory.CreateTempSubdirectory("wu-accent-").FullName, "settings.json");
        var viewModel = new SettingsViewModel(
            new SettingsService(logging, path), logging, new ThemeService(), new AutostartService(logging));

        viewModel.SelectedTheme = theme;

        Assert.Equal(expected, viewModel.CanChooseAccent);
    }

    [Theory]
    [InlineData("dark")]
    [InlineData("light")]
    public void The_settings_window_shows_the_accent_switch_bound_to_the_view_model(string theme)
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var logging = new LoggingService();
            var settings = new SettingsService(
                logging, Path.Combine(Directory.CreateTempSubdirectory("wu-accent-ui-").FullName, "settings.json"));
            LocalizationService.Instance.Apply("de");
            var themeService = new ThemeService();

            var viewModel = new SettingsViewModel(settings, logging, themeService, new AutostartService(logging), ["winget"]);
            viewModel.SelectedTheme = theme;
            var window = new SettingsWindow(viewModel);
            var appTheme = theme == "dark" ? ApplicationTheme.Dark : ApplicationTheme.Light;
            WpfHarness.ApplyTheme(appTheme);
            ApplicationAccentColorManager.Apply(WindowsBlue, appTheme);
            ApplicationThemeManager.Apply(appTheme, WindowBackdropType.None, updateAccent: false);
            ThemeService.ApplyAccentContrast(WindowsBlue);

            var toggle = window.FindName("SystemAccentToggle") as Wpf.Ui.Controls.ToggleSwitch;
            Assert.NotNull(toggle);

            var scroll = (ScrollViewer)window.FindName("SettingsScroll")!;
            var root = (FrameworkElement)window.Content;
            root.Measure(new Size(window.Width, window.Height));
            root.Arrange(new Rect(0, 0, window.Width, window.Height));
            root.UpdateLayout();
            toggle!.BringIntoView();
            scroll.UpdateLayout();

            result = WpfHarness.RenderWindow(window, $"settings-system-accent-{theme}", (int)window.Width, (int)window.Height + 140);

            Assert.True(toggle.ActualHeight > 0, "the accent switch was never laid out");
            Assert.True(toggle.IsEnabled);
            Assert.Equal(false, toggle.IsChecked);
            viewModel.UseSystemAccent = true;
            Assert.Equal(true, toggle.IsChecked);
            Assert.Equal("Windows-Akzentfarbe verwenden", LocalizationService.Instance["ThemeSystemAccent"]);

            viewModel.SelectedTheme = "hacker";
            Assert.False(toggle.IsEnabled);
        });

        Assert.NotNull(result);
        Assert.True(File.Exists(result.Path));
    }
}
