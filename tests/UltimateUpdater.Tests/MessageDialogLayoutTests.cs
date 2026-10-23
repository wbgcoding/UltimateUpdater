using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using UltimateUpdater.Services;
using UltimateUpdater.ViewModels;
using UltimateUpdater.Views;

namespace UltimateUpdater.Tests;

/// <summary>
/// Measures, not screenshots, <see cref="MessageDialogWindow"/> against the width it will really get.
/// The dialog cannot be resized, so a button row that does not fit is simply clipped: the German labels of
/// the install-blocker flow (<see cref="Dialogs.ConfirmThreeWay"/>) are ~48 px wider than the old fixed
/// 460 px allowed, which is why the window now grows between MinWidth and MaxWidth. Both halves of that
/// are asserted here, it has to grow for the three-way case, and it must NOT grow for an ordinary one.
/// </summary>
[Collection("wpf")]
public class MessageDialogLayoutTests(Xunit.Abstractions.ITestOutputHelper output)
{
    // Fixture text only, standing in for a realistically long German headline/body: these tests exercise
    // MessageDialogWindow's general three-way layout, not the install-blocker feature specifically (that
    // feature's own real headline/body moved into InstallPreflightWindow's lines, this fix).
    private const string SampleHeadline = "Beispielprogramm läuft noch. Es muss geschlossen werden, um zu aktualisieren.";
    private const string SampleBody = "Du kannst es schließen und fortfahren, dieses Update vorerst überspringen oder es trotzdem versuchen.";

    private sealed record Layout(double WindowWidth, double RowWanted, double RowAvailable, string[] Buttons);

    [Fact]
    public void ThreeWay_button_row_is_not_clipped_by_the_German_labels()
    {
        var layout = Measure(loc => new MessageDialogViewModel(
            DialogKind.Warning,
            loc["AppTitle"],
            SampleHeadline,
            SampleBody,
            loc["InstallBlockerCloseAndContinue"],
            loc["InstallBlockerSkip"],
            tertiaryText: loc["InstallBlockerTryAnyway"]));

        // A collapsed button would make the row "fit" for the wrong reason and quietly retire this test.
        Assert.Equal(3, layout.Buttons.Count(button => button.Contains("visible=Visible")));

        Assert.True(layout.RowWanted <= layout.RowAvailable,
            $"the three German buttons want {layout.RowWanted:F0} px but the dialog offers " +
            $"{layout.RowAvailable:F0} px at its {layout.WindowWidth:F0} px width, so one is clipped:" +
            $"{Environment.NewLine}{string.Join(Environment.NewLine, layout.Buttons)}");
    }

    /// <summary>The cleanup confirmation lists every selected item, one line each,
    /// select enough and the body alone wants far more than the window's own MaxHeight="640". Every row
    /// in the Grid is Auto-sized with nothing to absorb the overflow, so WPF does not reflow anything: it
    /// just clips whatever falls outside the window's actual rendered height, and since the button row is
    /// the LAST row, Abbrechen/Bereinigen were what got clipped off. Measured at the window's real
    /// MaxHeight (not the content's unclamped desired height, which is what <see cref="Measure"/> above
    /// uses and would never have caught this), the way WPF itself arranges a SizeToContent window capped
    /// by MaxHeight.</summary>
    [Fact]
    public void A_very_long_body_does_not_push_the_button_row_off_the_bottom_of_the_window()
    {
        double rowBottom = 0, windowHeight = 0;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");
            var loc = LocalizationService.Instance;

            var longBody = string.Join(Environment.NewLine, Enumerable.Range(1, 80)
                .Select(i => $"  • Eintrag {i}, ein Beispieleintrag mit etwas mehr Text"));
            var vm = new MessageDialogViewModel(
                DialogKind.Warning, loc["AppTitle"], "Sehr viele Elemente ausgewählt", longBody,
                loc["CleanupStart"], secondaryText: loc["DialogCancel"]);
            var window = new MessageDialogWindow(vm);
            var content = (FrameworkElement)window.Content;

            // A one-shot Measure/Arrange leaves data-bound TEXT empty on a never-shown window, the
            // binding engine pushes the initial value at DataBind priority, which only runs once the
            // dispatcher is pumped (Visibility bindings apply fine either way; Text/Content bindings do
            // not). WpfHarness.RenderWindow's own multi-pass loop plus an explicit SystemIdle pump is
            // what makes every other render-based layout test in this suite see real bound text,
            // mirrored here rather than a bare single Measure/Arrange.
            content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var width = Math.Clamp(content.DesiredSize.Width, window.MinWidth, window.MaxWidth);

            for (var pass = 0; pass < 4; pass++)
            {
                // What SizeToContent="WidthAndHeight" actually renders at: capped by MaxHeight, same as
                // a real window, never the content's own unclamped desired height.
                content.Measure(new Size(width, window.MaxHeight));
                windowHeight = Math.Min(content.DesiredSize.Height, window.MaxHeight);
                content.Arrange(new Rect(0, 0, width, windowHeight));
                content.UpdateLayout();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.SystemIdle);
            }

            var row = window.ButtonRow;
            rowBottom = row.TransformToAncestor(content).Transform(new Point(0, 0)).Y + row.ActualHeight;
        });

        output.WriteLine($"window height {windowHeight:F0}px · button row bottom {rowBottom:F0}px");
        Assert.True(rowBottom <= windowHeight + 0.5,
            $"button row bottom ({rowBottom:F0}px) is below the window's own rendered height " +
            $"({windowHeight:F0}px): Abbrechen/Bereinigen would be clipped off the bottom of the window.");
    }

    [Fact]
    public void An_ordinary_dialog_still_opens_at_the_minimum_width()
    {
        // Wrapping text has no natural width. Were the MaxWidth on the message area ever dropped,
        // SizeToContent would lay the body out as one long line and stretch every dialog to MaxWidth.
        var layout = Measure(loc => new MessageDialogViewModel(
            DialogKind.Info,
            loc["AppTitle"],
            SampleBody,
            SampleBody,
            loc["DialogOk"]));

        Assert.Equal(460, layout.WindowWidth);
    }

    /// <summary>
    /// The window used to carry a fixed <c>MinHeight="180"</c>, so a plain one-line dialog rendered with
    /// exactly as much blank space at the bottom as it took to reach that floor. Dropping MinHeight lets
    /// SizeToContent shrink-wrap short dialogs instead, checked here against the same window's real
    /// rendered height (via <see cref="Render"/>, not DesiredSize) for a short dialog versus one with a
    /// long body, the way <see cref="A_very_long_body_does_not_push_the_button_row_off_the_bottom_of_the_window"/>
    /// grows it.
    /// </summary>
    [Fact]
    public void A_short_dialog_renders_shorter_than_one_with_a_long_body()
    {
        RenderResult? shortResult = null, longResult = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");
            var loc = LocalizationService.Instance;

            var shortVm = new MessageDialogViewModel(
                DialogKind.Info, loc["AppTitle"], loc["DialogOk"], string.Empty, loc["DialogOk"]);
            shortResult = Render(new MessageDialogWindow(shortVm), "message-dialog-short");

            var longBody = string.Join(Environment.NewLine, Enumerable.Range(1, 20)
                .Select(i => $"  • Eintrag {i}, ein Beispieleintrag mit etwas mehr Text"));
            var longVm = new MessageDialogViewModel(
                DialogKind.Info, loc["AppTitle"], loc["DialogOk"], longBody, loc["DialogOk"]);
            longResult = Render(new MessageDialogWindow(longVm), "message-dialog-long-body");
        });

        Assert.NotNull(shortResult);
        Assert.NotNull(longResult);
        output.WriteLine($"short {shortResult!.Height}px, long body {longResult!.Height}px");
        Assert.True(shortResult.Height < longResult.Height,
            $"short dialog rendered at {shortResult.Height}px, expected shorter than the long-body " +
            $"dialog's {longResult.Height}px now that MinHeight no longer floors it.");
    }

    /// <summary>
    /// Nothing in this file ever wrote a PNG before, every test above measures geometry only, which
    /// proves a control fits but not that its text renders without clipping. Same three-way button
    /// row as <see cref="ThreeWay_button_row_is_not_clipped_by_the_German_labels"/>, actually rendered,
    /// in both languages.
    /// </summary>
    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void MessageDialogWindow_renders(string language)
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply(language);
            var loc = LocalizationService.Instance;

            var vm = new MessageDialogViewModel(
                DialogKind.Warning,
                loc["AppTitle"],
                SampleHeadline,
                SampleBody,
                loc["InstallBlockerCloseAndContinue"],
                loc["InstallBlockerSkip"],
                tertiaryText: loc["InstallBlockerTryAnyway"]);
            var window = new MessageDialogWindow(vm);
            result = Render(window, $"message-dialog-threeway-{language}");
        });

        AssertRendered(result);
    }

    /// <summary>The light theme is never checked for this window otherwise.</summary>
    [Fact]
    public void MessageDialogWindow_renders_in_the_light_theme()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");
            var theme = new ThemeService();
            theme.AttachWindow(new Window());
            theme.Apply("light");

            var loc = LocalizationService.Instance;
            var vm = new MessageDialogViewModel(
                DialogKind.Warning,
                loc["AppTitle"],
                SampleHeadline,
                SampleBody,
                loc["InstallBlockerCloseAndContinue"],
                loc["InstallBlockerSkip"],
                tertiaryText: loc["InstallBlockerTryAnyway"]);
            var window = new MessageDialogWindow(vm);
            result = Render(window, "message-dialog-threeway-light-de");

            theme.Apply("dark");
        });

        AssertRendered(result);
    }

    /// <summary>Lays the dialog out the way SizeToContent actually would (see the very-long-body test
    /// above for why that clamp is needed), then hands the resulting size to the render harness, the
    /// harness itself always needs an explicit pixel size, which this window's own properties do not give
    /// directly.</summary>
    private static RenderResult Render(MessageDialogWindow window, string fileName)
    {
        var content = (FrameworkElement)window.Content;

        // A single Measure leaves data-bound button Content empty, the binding engine pushes the
        // initial value at DataBind priority, which only runs once the dispatcher is pumped (see the
        // comment on A_very_long_body_does_not_push_the_button_row_off_the_bottom_of_the_window above).
        // Without this the button row measures at its bare MinWidth, the window renders at 460px, and
        // the REAL (bound) text, resolved too late by WpfHarness.RenderWindow's own pump, clips.
        for (var pass = 0; pass < 4; pass++)
        {
            content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var primeWidth = Math.Max(content.DesiredSize.Width, window.MinWidth);
            content.Arrange(new Rect(0, 0, primeWidth, content.DesiredSize.Height));
            content.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.SystemIdle);
        }

        var width = (int)Math.Clamp(content.DesiredSize.Width, window.MinWidth, window.MaxWidth);

        content.Measure(new Size(width, window.MaxHeight));
        var height = (int)Math.Min(content.DesiredSize.Height, window.MaxHeight);

        return WpfHarness.RenderWindow(window, fileName, width, height);
    }

    private static void AssertRendered(RenderResult? result)
    {
        Assert.NotNull(result);
        Assert.True(File.Exists(result.Path), $"No image was written to {result.Path}.");
        Assert.True(
            result.DistinctColours >= 20,
            $"{Path.GetFileName(result.Path)} came out effectively blank, only {result.DistinctColours} distinct colours.");
    }

    private Layout Measure(Func<LocalizationService, MessageDialogViewModel> build)
    {
        Layout? layout = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");

            var window = new MessageDialogWindow(build(LocalizationService.Instance));
            var content = (FrameworkElement)window.Content;

            content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            // What SizeToContent="WidthAndHeight" will settle on, clamped the way the window declares it.
            var width = Math.Clamp(content.DesiredSize.Width, window.MinWidth, window.MaxWidth);
            content.Measure(new Size(width, double.PositiveInfinity));
            content.Arrange(new Rect(0, 0, width, content.DesiredSize.Height));

            var row = window.ButtonRow;
            layout = new Layout(
                width,
                row.DesiredSize.Width,
                width - row.Margin.Left - row.Margin.Right,
                [.. row.Children.OfType<Button>().Select(button =>
                    $"{button.Content} visible={button.Visibility} width={button.DesiredSize.Width:F0}")]);
        });

        Assert.NotNull(layout);
        output.WriteLine($"window {layout!.WindowWidth:F0} px · row wants {layout.RowWanted:F0} of {layout.RowAvailable:F0}");
        foreach (var button in layout.Buttons)
            output.WriteLine(button);

        return layout;
    }
}
