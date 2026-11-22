namespace UltimateUpdater.Tests;

/// <summary>The acknowledge checkbox of a confirmation: the primary button only works once the sentence is ticked.</summary>
public class MessageDialogAcknowledgeTests
{
    private static MessageDialogViewModel Dialog(string? acknowledge) =>
        new(DialogKind.Warning, "Title", "Headline", "Body", "Go", "Cancel", destructive: true, acknowledgeText: acknowledge);

    [Fact]
    public void Without_an_acknowledge_text_the_primary_button_always_works()
    {
        var viewModel = Dialog(null);

        Assert.False(viewModel.HasAcknowledge);
        Assert.True(viewModel.CanConfirm);
    }

    [Fact]
    public void With_an_acknowledge_text_the_primary_button_waits_for_the_tick()
    {
        var viewModel = Dialog("I understand.");
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.True(viewModel.HasAcknowledge);
        Assert.False(viewModel.CanConfirm);

        viewModel.Acknowledged = true;

        Assert.True(viewModel.CanConfirm);
        Assert.Contains(nameof(MessageDialogViewModel.CanConfirm), changed);

        viewModel.Acknowledged = false;

        Assert.False(viewModel.CanConfirm);
    }
}
