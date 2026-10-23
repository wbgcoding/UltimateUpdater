using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using UltimateUpdater.Services;
using UltimateUpdater.ViewModels;
using UltimateUpdater.Views;

namespace UltimateUpdater.Tests;

/// <summary>
/// WPF has no built-in "Escape closes this window" gesture, and none of this app's dialog windows wired
/// one by hand before this, found while auditing keyboard/focus behaviour (a full-text search turned up
/// no <c>Key.Escape</c> handling anywhere and no <c>IsCancel</c>/<c>IsDefault</c> attribute in any XAML
/// file). <see cref="DialogKeyboard.CloseOnEscape"/> fixes it; these tests raise a real bubbling
/// <c>KeyDown</c> (never shown windows have no PresentationSource, but the route for an unrooted Window
/// raising its own event is just itself, so that is not needed) rather than only reading the source.
/// </summary>
[Collection("wpf")]
public class DialogKeyboardTests
{
    /// <summary>Never shown, never sized, exists only so <see cref="KeyEventArgs"/>'s constructor has a
    /// non-null <c>inputSource</c> to point at. RaiseEvent injects straight onto the target window, so
    /// this dummy is never actually routed through; it just has to be A valid PresentationSource.</summary>
    private static readonly PresentationSource DummyInputSource =
        new HwndSource(new HwndSourceParameters("DialogKeyboardTests") { WindowStyle = 0, Width = 0, Height = 0 });

    private static void PressEscape(System.Windows.Window window)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, DummyInputSource, 0, Key.Escape)
        {
            RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent,
        };
        window.RaiseEvent(args);
    }

    [Fact]
    public void Escape_closes_the_message_dialog_window()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = new MessageDialogViewModel(
                DialogKind.Info, "Titel", "Kopf", "Text", "OK");
            var window = new MessageDialogWindow(viewModel);

            var closed = false;
            window.Closed += (_, _) => closed = true;

            PressEscape(window);

            Assert.True(closed, "Escape must close the dialog, matching the title bar's own close behaviour.");
            Assert.Equal(UltimateUpdater.Services.ThreeWayChoice.Dismissed, window.Choice);
        });
    }

    [Fact]
    public void Escape_closes_the_error_detail_window()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = new ErrorDetailViewModel(
                "Firefox", "winget", "128.0.3 → 129.0.1", "winget install failed (exit 1).", new LoggingService());
            var window = new ErrorDetailWindow(viewModel);

            var closed = false;
            window.Closed += (_, _) => closed = true;

            PressEscape(window);

            Assert.True(closed);
        });
    }

    [Fact]
    public void Escape_closes_the_changelog_window()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = new ChangelogViewModel("Firefox", "129.0.1", "Bug fixes.", null, new LoggingService());
            var window = new ChangelogWindow(viewModel);

            var closed = false;
            window.Closed += (_, _) => closed = true;

            PressEscape(window);

            Assert.True(closed);
        });
    }

    /// <summary>The cleanup window has no Close button of its own, so Escape (and the title bar) is how it
    /// closes from the keyboard.</summary>
    [Fact]
    public void Escape_closes_the_cleanup_window()
    {
        WpfHarness.OnUiThread(() =>
        {
            var logging = new LoggingService();
            var settings = new SettingsService(logging, Path.Combine(Path.GetTempPath(), $"wu-test-settings-{Guid.NewGuid():N}.json"));
            var viewModel = new CleanupViewModel(new UltimateUpdater.Services.Cleanup.CleanupService(logging), logging, settings);
            WpfHarness.WaitForCleanupCatalog(viewModel);
            var window = new CleanupWindow(viewModel);

            var closed = false;
            window.Closed += (_, _) => closed = true;

            PressEscape(window);

            Assert.True(closed);
        });
    }
}
