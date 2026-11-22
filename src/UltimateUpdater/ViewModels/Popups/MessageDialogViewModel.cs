using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace UltimateUpdater.ViewModels.Popups;

/// <summary>What the dialog is about; drives the icon and its colour.</summary>
public enum DialogKind
{
    Info,
    Warning,
    Error,
    Question,
    Success,
}

/// <summary>Backs the themed message and confirmation window the whole app uses instead of the system MessageBox. Optional details (a stack trace, a tool log) fold out into a read-only, copyable panel.</summary>
public partial class MessageDialogViewModel : ObservableObject
{
    public LocalizationService Loc => LocalizationService.Instance;

    public DialogKind Kind { get; }

    public string Title { get; }

    /// <summary>The one-line lead, bold.</summary>
    public string Headline { get; }

    /// <summary>The explanatory paragraph(s) under the headline. May be empty.</summary>
    public string Body { get; }

    public bool HasBody => !string.IsNullOrWhiteSpace(Body);

    /// <summary>Raw diagnostic text (a stack trace, a tool's output), shown behind a "Details" fold.</summary>
    public string? Details { get; }

    public bool HasDetails => !string.IsNullOrWhiteSpace(Details);

    public string PrimaryText { get; }

    public string? SecondaryText { get; }

    public bool HasSecondary => !string.IsNullOrEmpty(SecondaryText);

    /// <summary>A third button, for a blocker dialog that needs close-and-continue, skip and try-anyway; see <see cref="Dialogs.ConfirmThreeWay"/>.</summary>
    public string? TertiaryText { get; }

    public bool HasTertiary => !string.IsNullOrEmpty(TertiaryText);

    /// <summary>A destructive confirm paints its primary button red so it never reads as the safe default.</summary>
    public bool PrimaryIsDestructive { get; }

    /// <summary>Turns the Copy button on the details panel into a brief "Copied" acknowledgement.</summary>
    [ObservableProperty]
    private bool copied;

    /// <summary>A sentence the user must tick before the primary button works (a removal that can affect Windows). Null: no checkbox.</summary>
    public string? AcknowledgeText { get; }

    public bool HasAcknowledge => !string.IsNullOrEmpty(AcknowledgeText);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private bool acknowledged;

    /// <summary>Whether the primary button may be pressed: always, unless a sentence is still unticked.</summary>
    public bool CanConfirm => !HasAcknowledge || Acknowledged;

    public MessageDialogViewModel(
        DialogKind kind,
        string title,
        string headline,
        string body,
        string primaryText,
        string? secondaryText = null,
        string? details = null,
        bool destructive = false,
        string? tertiaryText = null,
        string? acknowledgeText = null)
    {
        AcknowledgeText = acknowledgeText;
        Kind = kind;
        Title = title;
        Headline = headline;
        Body = body;
        PrimaryText = primaryText;
        SecondaryText = secondaryText;
        Details = details;
        PrimaryIsDestructive = destructive;
        TertiaryText = tertiaryText;
    }

    [RelayCommand]
    private void Copy()
    {
        if (!HasDetails)
            return;

        try
        {
            System.Windows.Clipboard.SetText(Details!);
            Copied = true;
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // the clipboard can be held open by another process; a failed copy is not worth surfacing
        }
    }
}
