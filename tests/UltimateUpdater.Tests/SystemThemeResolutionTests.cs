using UltimateUpdater.Services;
using Wpf.Ui.Appearance;

namespace UltimateUpdater.Tests;

public class SystemThemeResolutionTests
{
    /// <summary>The dark Windows 11 built-in themes come back from WPF-UI as their own values; a user on
    /// one of them must get the dark app theme under "system".</summary>
    [Theory]
    [InlineData(SystemTheme.Dark, true)]
    [InlineData(SystemTheme.Glow, true)]
    [InlineData(SystemTheme.CapturedMotion, true)]
    [InlineData(SystemTheme.Light, false)]
    [InlineData(SystemTheme.Sunrise, false)]
    [InlineData(SystemTheme.Flow, false)]
    [InlineData(SystemTheme.Unknown, false)]
    public void Only_the_dark_system_themes_resolve_to_dark(SystemTheme theme, bool dark) =>
        Assert.Equal(dark, ThemeService.IsDarkSystemTheme(theme));
}
