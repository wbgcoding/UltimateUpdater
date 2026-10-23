using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Wpf.Ui.Controls;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Views;

public partial class SettingsWindow : FluentWindow
{
    /// <summary>Window width from which the section list beside the page is shown.</summary>
    internal const double NavigationMinWidth = 760;

    // Set while the list is being moved by code (the scroll position drives it), so that does not
    // scroll the page again.
    private bool _syncingNavigation;

    // The section a click or key press scrolled to, until the scroll has arrived there; while it is
    // set, scrolling does not move the highlight away from what the user just picked (the last
    // sections cannot reach the top of the page).
    private FrameworkElement? _navigationTarget;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        WindowBackdropType = Services.ThemeService.ActiveBackdrop;
        DataContext = viewModel;
        DialogSizing.FitToWorkArea(this);
        DialogKeyboard.CloseOnEscape(this);

        // clicking away from a dialog that only shows things closes it
        DismissOnDeactivate.Attach(this);

        SettingsScroll.ScrollChanged += OnScrollChanged;
        // the user takes over the scrolling (wheel, scrollbar drag, keys): the position drives the highlight again
        SettingsScroll.PreviewMouseWheel += (_, _) => _navigationTarget = null;
        SettingsScroll.PreviewMouseDown += (_, _) => _navigationTarget = null;
        SettingsScroll.PreviewKeyDown += (_, _) => _navigationTarget = null;
        SettingsNav.Visibility = Width >= NavigationMinWidth ? Visibility.Visible : Visibility.Collapsed;

        // The backup folder box commits on LostFocus, which Escape or a click on another window never
        // raises; without this the typed folder would be dropped without a word.
        Closing += (_, _) => CleanupBackupFolderBox.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
    }

    /// <summary>Puts the box back to what the view model kept when it refused the typed folder (the binding
    /// does not re-read a value the source changed while it was being set).</summary>
    private void OnBackupFolderBoxLostFocus(object sender, RoutedEventArgs e) =>
        ((FrameworkElement)sender).GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateTarget();

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e) =>
        SettingsNav.Visibility = e.NewSize.Width >= NavigationMinWidth ? Visibility.Visible : Visibility.Collapsed;

    private void OnNavSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingNavigation || SettingsNav.SelectedItem is not ListBoxItem item)
            return;

        ScrollToSection(item);
    }

    // A click on the entry that is already highlighted raises no selection change, but still means "go there".
    private void OnNavClicked(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(SettingsNav, source) is ListBoxItem { IsSelected: true } item)
            ScrollToSection(item);
    }

    private void OnNavKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SettingsNav.SelectedItem is ListBoxItem item)
        {
            ScrollToSection(item);
            e.Handled = true;
        }
    }

    /// <summary>Scrolls the section named by the entry's tag to the top of the page; About also opens its card.</summary>
    internal void ScrollToSection(ListBoxItem entry)
    {
        if (FindName((string)entry.Tag) is not FrameworkElement target)
            return;

        if (target is System.Windows.Controls.Expander expander)
            expander.IsExpanded = true;

        SettingsScroll.UpdateLayout();
        var offset = Math.Min(SectionTop(target), SettingsScroll.ScrollableHeight);

        // Already there: no scroll event will follow, so nothing may wait for an arrival.
        _navigationTarget = Math.Abs(offset - SettingsScroll.VerticalOffset) < 1 ? null : target;
        SettingsScroll.ScrollToVerticalOffset(SectionTop(target));
    }

    private double SectionTop(FrameworkElement element)
    {
        // An expander sits inside its own card; the card is what should land at the top.
        FrameworkElement anchor = element is System.Windows.Controls.Expander && element.Parent is Border card ? card : element;
        var content = (UIElement)SettingsScroll.Content;
        return anchor.TransformToAncestor(content).Transform(new System.Windows.Point(0, 0)).Y;
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange == 0 && e.ViewportHeightChange == 0 && e.ExtentHeightChange == 0)
            return;

        if (_navigationTarget is { } target)
        {
            var arrived = Math.Abs(SettingsScroll.VerticalOffset - SectionTop(target)) < 1
                || SettingsScroll.VerticalOffset >= SettingsScroll.ScrollableHeight - 1;
            if (arrived)
                _navigationTarget = null;
            return;
        }

        HighlightTopmostSection();
    }

    /// <summary>The entry of the last section whose header has reached the top of the visible page.</summary>
    private void HighlightTopmostSection()
    {
        var entries = new List<ListBoxItem>();
        var tops = new List<double>();
        foreach (var entry in SettingsNav.Items.OfType<ListBoxItem>())
        {
            if (FindName((string)entry.Tag) is not FrameworkElement target)
                continue;
            entries.Add(entry);
            tops.Add(SectionTop(target));
        }

        var index = HighlightedSectionIndex(tops, SettingsScroll.VerticalOffset, SettingsScroll.ViewportHeight, SettingsScroll.ScrollableHeight);
        if (index < 0)
            return;
        var current = entries[index];
        if (ReferenceEquals(SettingsNav.SelectedItem, current))
            return;

        _syncingNavigation = true;
        try
        {
            SettingsNav.SelectedItem = current;
        }
        finally
        {
            _syncingNavigation = false;
        }
    }

    /// <summary>Index of the section to highlight: the last one whose header crossed a reading line near the
    /// top of the page. The last sections are too short to ever reach the top, so over the last screen of
    /// scrolling the line glides down to the bottom edge: at the very bottom the last visible section wins,
    /// and the sections in between are passed one by one instead of the highlight jumping to the end.</summary>
    internal static int HighlightedSectionIndex(IReadOnlyList<double> sectionTops, double offset, double viewportHeight, double scrollableHeight)
    {
        if (sectionTops.Count == 0)
            return -1;

        const double TopLine = 24;
        var lastScreen = Math.Min(scrollableHeight, viewportHeight);
        var progress = lastScreen > 0 ? Math.Clamp((offset - (scrollableHeight - lastScreen)) / lastScreen, 0, 1) : 0;
        var line = offset + TopLine + progress * Math.Max(0, viewportHeight - 1 - TopLine);
        var index = 0;
        for (var i = 0; i < sectionTops.Count; i++)
        {
            if (sectionTops[i] <= line)
                index = i;
        }
        return index;
    }
}
