using System.Windows;
using UltimateUpdater.Services;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

/// <summary>
/// When the themed dialog throws, <see cref="Dialogs"/> falls back to the system MessageBox. With OK as
/// its only button a confirmation could only ever come back confirmed (Close also returns OK), so a
/// destructive question must keep a Cancel button there.
/// </summary>
public class DialogFallbackTests
{
    [Fact]
    public void A_confirmation_falls_back_to_a_box_that_can_say_no()
    {
        var question = new MessageDialogViewModel(
            DialogKind.Warning, "Title", "Delete everything?", "", "Delete", "Cancel", destructive: true);

        Assert.Equal(MessageBoxButton.OKCancel, Dialogs.FallbackButtons(question));
    }

    [Fact]
    public void A_plain_notice_falls_back_to_a_single_OK()
    {
        var notice = new MessageDialogViewModel(DialogKind.Info, "Title", "Done", "", "OK");

        Assert.Equal(MessageBoxButton.OK, Dialogs.FallbackButtons(notice));
    }
}
