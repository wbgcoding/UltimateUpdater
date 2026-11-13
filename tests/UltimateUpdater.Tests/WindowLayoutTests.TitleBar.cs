using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UltimateUpdater.Services;
using UltimateUpdater.Views;

namespace UltimateUpdater.Tests;

public partial class WindowLayoutTests
{
    /// <summary>
    /// `ui:TitleBar` draws its own `Title` text only when its `Header` is null; MainWindow always sets a
    /// `Header`, which silently dropped the app name, confirmed by walking the rendered visual tree and
    /// finding no TextBlock anywhere carrying it. Checked at the window's own minimum width too, where the
    /// icon, the title text and the window's own caption buttons share one row.
    /// </summary>
    [Theory]
    [InlineData(860)]
    [InlineData(1160)]
    public void Main_window_title_bar_shows_the_app_name(int width)
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("de");
            Populate(viewModel);
            var window = new MainWindow(viewModel, theme, settings);
            var content = (FrameworkElement)window.Content;
            result = WpfHarness.RenderWindow(window, $"main-title-app-name-{width}", width, 700);

            var titleText = FindByName<TextBlock>(content, "AppTitleText");
            Assert.NotNull(titleText);
            Assert.Equal(Visibility.Visible, titleText!.Visibility);
            // the name alone, the version lives in Settings > About
            Assert.Equal(LocalizationService.Instance["AppTitle"], titleText.Text);
            Assert.True(titleText.ActualWidth > 0, "the title text was never actually laid out");

            var titleBar = FindFirstDescendant<Wpf.Ui.Controls.TitleBar>(content);
            Assert.NotNull(titleBar);
            AssertNoChildClips(titleBar!, [titleText]);

            // the shared TitleBarAppIcon style makes the app icon 20 px, not WPF-UI's 16 px default
            var icon = FindFirstDescendant<Wpf.Ui.Controls.ImageIcon>(titleBar!);
            Assert.NotNull(icon);
            Assert.Equal(20, icon!.ActualWidth);
            Assert.Equal(20, icon.ActualHeight);

            // and nothing between the icon and the title bar cuts it off: WPF-UI's own template gives the
            // icon slot a fixed 16 px height, and a 20 px icon laid out into it got a layout clip
            Assert.Null(System.Windows.Controls.Primitives.LayoutInformation.GetLayoutClip(icon));
            for (var node = VisualTreeHelper.GetParent(icon) as FrameworkElement;
                 node is not null && !ReferenceEquals(node, titleBar);
                 node = VisualTreeHelper.GetParent(node) as FrameworkElement)
            {
                Assert.True(
                    !node.ClipToBounds && node.Clip is null
                        && System.Windows.Controls.Primitives.LayoutInformation.GetLayoutClip(node) is null,
                    $"{node.GetType().Name} ({node.ActualWidth}x{node.ActualHeight}) clips the 20 px title bar icon");
            }
        });

        AssertRendered(result);
    }
}
