using UltimateUpdater.Services;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

/// <summary>
/// The offline "what does this mean" lookup on the error-detail window. Built the same way
/// <c>MainViewModel.ShowErrorDetail</c> does (explanation null exactly when
/// <c>InstallErrorExplainer.Describe</c> found nothing), so the button's own visibility rule is
/// exercised for real. These calls resolve through the real Win32 FormatMessage table, offline, no
/// network, the same thing the feature itself does; a code with a genuine, stable system message
/// (ERROR_FILE_NOT_FOUND = 2) is used rather than asserting exact wording, which would tie the test to
/// one specific Windows build/locale.
/// </summary>
public class ErrorDetailViewModelTests
{
    private static ErrorDetailViewModel Build(string detail, string? explanation = null) =>
        new("7-Zip", "winget", null, detail, new LoggingService(), explanation);

    [Fact]
    public void The_button_is_hidden_when_the_explainer_already_found_something()
    {
        var viewModel = Build("Installer failed with exit code: 1602", explanation: "already explained");

        Assert.False(viewModel.ShowWhatDoesThisMeanButton);
    }

    [Fact]
    public void The_button_is_hidden_when_no_code_shaped_text_exists_at_all()
    {
        var viewModel = Build("Something went wrong. No further detail.");

        Assert.False(viewModel.ShowWhatDoesThisMeanButton);
    }

    [Fact]
    public void A_known_win32_code_resolves_to_its_real_system_text()
    {
        var viewModel = Build("Something failed with error code 2 and no further detail.");
        Assert.True(viewModel.ShowWhatDoesThisMeanButton);

        viewModel.WhatDoesThisMeanCommand.Execute(null);

        Assert.True(viewModel.HasWindowsMeaning);
        Assert.False(viewModel.ShowWhatDoesThisMeanButton); // hides once resolved
        Assert.NotEqual(LocalizationService.Instance["ErrorNoMeaning"], viewModel.WindowsMeaningText);
    }

    [Fact]
    public void A_hresult_shaped_code_resolves_through_win32exception()
    {
        // A 4-digit hex token: Explain() itself only recognizes an exact 8-digit "0x........" form, so
        // this one reaches the button's own broader lookup as an HRESULT-shaped value instead.
        var viewModel = Build("HRESULT 0x2 failed.");
        Assert.True(viewModel.ShowWhatDoesThisMeanButton);

        viewModel.WhatDoesThisMeanCommand.Execute(null);

        Assert.True(viewModel.HasWindowsMeaning);
        Assert.NotEqual(LocalizationService.Instance["ErrorNoMeaning"], viewModel.WindowsMeaningText);
    }

    [Fact]
    public void An_invented_code_windows_does_not_recognize_shows_the_no_meaning_line()
    {
        var viewModel = Build("Something failed with error code 999999999 and no further detail.");
        Assert.True(viewModel.ShowWhatDoesThisMeanButton);

        viewModel.WhatDoesThisMeanCommand.Execute(null);

        Assert.Equal(LocalizationService.Instance["ErrorNoMeaning"], viewModel.WindowsMeaningText);
    }

    [Fact]
    public void A_failed_clipboard_write_shows_the_inline_failure_line_and_clears_it_again()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Build("Installer failed with exit code: 1");
            viewModel.CopyFeedbackDuration = TimeSpan.FromMilliseconds(60);
            viewModel.ClipboardWriter = _ => throw new System.Runtime.InteropServices.ExternalException("held open");

            viewModel.CopyCommand.Execute(null);

            Assert.True(viewModel.CopyFailed);
            Assert.False(viewModel.Copied);

            WaitForFeedbackToClear(viewModel);
            Assert.False(viewModel.CopyFailed);
        });
    }

    [Fact]
    public void The_copied_acknowledgement_goes_away_after_the_feedback_time()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Build("Installer failed with exit code: 1");
            viewModel.CopyFeedbackDuration = TimeSpan.FromMilliseconds(60);
            viewModel.ClipboardWriter = _ => { };

            viewModel.CopyCommand.Execute(null);
            Assert.True(viewModel.Copied);
            Assert.False(viewModel.CopyFailed);

            WaitForFeedbackToClear(viewModel);
            Assert.False(viewModel.Copied);
        });
    }

    private static void WaitForFeedbackToClear(ErrorDetailViewModel viewModel)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while ((viewModel.Copied || viewModel.CopyFailed) && DateTime.UtcNow < deadline)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            Thread.Sleep(20);
        }
    }
}
