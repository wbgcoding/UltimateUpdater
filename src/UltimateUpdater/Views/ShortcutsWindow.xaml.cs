using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Wpf.Ui.Controls;
using UltimateUpdater.Services;

namespace UltimateUpdater.Views;

/// <summary>
/// The F1 overview: every entry in <see cref="KeyboardShortcuts.All"/>, its label resolved through the
/// same localization table every other window reads and its gesture text exactly as it fires. Needs no
/// injected services, since nothing here is more than static reference data, so this window is its own
/// DataContext rather than a separate view model class existing only to hold two read-only properties.
/// </summary>
public partial class ShortcutsWindow : FluentWindow
{
    /// <summary>One row's already-localized display text; resolved once at construction rather than
    /// staying live-bound through a later language switch, since a non-modal window closes on the very
    /// next click elsewhere in the app (<see cref="DismissOnDeactivate"/>) and changing language happens
    /// from Settings, which is exactly such a click.</summary>
    public sealed record Row(string Label, string Gesture);

    public IReadOnlyList<Row> Rows { get; }

    public LocalizationService Loc => LocalizationService.Instance;

    public ShortcutsWindow()
    {
        InitializeComponent();
        WindowBackdropType = ThemeService.ActiveBackdrop;
        Rows = KeyboardShortcuts.All
            .Select(entry => new Row(LocalizationService.Instance[entry.LabelKey], entry.Gesture))
            .ToList();
        DataContext = this;

        DialogKeyboard.CloseOnEscape(this);
        DialogSizing.FitHeightToContent(this);
        DismissOnDeactivate.Attach(this);
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();
}
