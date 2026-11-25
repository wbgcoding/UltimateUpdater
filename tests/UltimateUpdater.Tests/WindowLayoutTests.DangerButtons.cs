using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UltimateUpdater.ViewModels.Popups;
using UltimateUpdater.Views.Popups;
using UltimateUpdater.Views.Uninstall;
using Wpf.Ui.Appearance;

namespace UltimateUpdater.Tests;

public partial class WindowLayoutTests
{
    /// <summary>
    /// The uninstaller's red button and every destructive confirmation used WPF-UI's raw Danger appearance
    /// (a fixed #F44336 fill with white text at 3.7:1 in dark, a near-black label in light). Both now use the
    /// shared DangerButton style; the label colour read back from the real windows must reach 4.5:1 against
    /// the fill in both themes.
    /// </summary>
    [Theory]
    [InlineData(ApplicationTheme.Dark)]
    [InlineData(ApplicationTheme.Light)]
    public void The_uninstall_and_the_destructive_confirmation_buttons_are_readable_on_their_fill(ApplicationTheme theme)
    {
        WpfHarness.OnUiThread(() =>
        {
            WpfHarness.ApplyTheme(theme);
            LocalizationService.Instance.Apply("en");

            var uninstaller = new UninstallerWindow(CreateUninstallerViewModel());
            var uninstallContent = (FrameworkElement)uninstaller.Content;
            WpfHarness.RenderWindow(uninstaller, $"danger-uninstaller-{theme}", (int)uninstaller.Width, (int)uninstaller.Height);
            var uninstallButton = FindByName<Wpf.Ui.Controls.Button>(uninstallContent, "UninstallButton")!;
            var uninstallLabel = FindByName<TextBlock>(uninstallContent, "UninstallButtonLabel")!;
            AssertReadable(theme, "uninstall button", uninstallLabel, uninstallButton);

            var dialog = new MessageDialogWindow(new MessageDialogViewModel(
                DialogKind.Warning, "Title", "Headline", "Body", "Delete", "Cancel", destructive: true));
            var dialogContent = (FrameworkElement)dialog.Content;
            WpfHarness.RenderWindow(dialog, $"danger-dialog-{theme}", (int)dialog.Width, 300);
            var primary = FindByName<Wpf.Ui.Controls.Button>(dialogContent, "PrimaryButton")!;
            var primaryLabel = Assert.IsType<TextBlock>(primary.Content);
            Assert.Equal("Delete", primaryLabel.Text);
            AssertReadable(theme, "destructive dialog primary", primaryLabel, primary);
        });
    }

    private static void AssertReadable(ApplicationTheme theme, string what, TextBlock label, Wpf.Ui.Controls.Button button)
    {
        var foreground = (label.Foreground as SolidColorBrush)?.Color;
        var rawFill = (button.Background as SolidColorBrush)?.Color;
        Assert.NotNull(foreground);
        Assert.NotNull(rawFill);

        var page = theme == ApplicationTheme.Dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xFA, 0xFA, 0xFA);
        var a = rawFill!.Value.A / 255.0;
        byte Mix(byte s, byte b) => (byte)((s * a) + (b * (1 - a)));
        var fill = Color.FromRgb(Mix(rawFill.Value.R, page.R), Mix(rawFill.Value.G, page.G), Mix(rawFill.Value.B, page.B));
        var contrast = AccentPalette.Contrast(foreground!.Value, fill);
        Assert.True(contrast >= 4.5, $"{theme}: {what} label {foreground} on fill {fill} is only {contrast:0.0}:1.");
    }
}
