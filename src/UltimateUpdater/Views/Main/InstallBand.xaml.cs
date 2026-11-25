using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using UltimateUpdater.Models;

namespace UltimateUpdater.Views.Main;

/// <summary>The live install band: the running rows, their progress and the reorderable queue. Binds to the window's MainViewModel through the inherited DataContext.</summary>
public partial class InstallBand
{
    public InstallBand()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel old)
            old.QueueRows.CollectionChanged -= OnQueueRowsChanged;
        if (e.NewValue is MainViewModel current)
            current.QueueRows.CollectionChanged += OnQueueRowsChanged;
    }

    /// <summary>The queue shrinks whenever an item starts and grows when one is added or removed, so every card's "position X of N" is stamped again; at Loaded priority, after the generator has made or dropped its containers.</summary>
    private void OnQueueRowsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            if (DataContext is MainViewModel viewModel)
                RefreshQueueRowAutomationNames(viewModel);
        });

    // WinForms has same-named Point, MouseEventArgs and DragEventArgs types (GlobalUsings.cs), so these are qualified at their use site.
    private System.Windows.Point? _queueDragStart;
    private UpdateRowViewModel? _queueDragRow;
    private System.Windows.Documents.AdornerLayer? _queueDragAdornerLayer;
    private DragAdorner? _queueDragAdorner;

    /// <summary>Remembers the row and pointer position a drag might start from; consumed only if the pointer moves far enough while held (see <see cref="OnQueueRowMouseMove"/>).</summary>
    private void OnQueueRowMouseDown(object sender, MouseButtonEventArgs e)
    {
        _queueDragStart = e.GetPosition(null);
        _queueDragRow = (sender as FrameworkElement)?.DataContext as UpdateRowViewModel;
    }

    private void OnQueueRowMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            // A press that ended without a drag must not arm a drag for a later press this card's MouseDown never saw (the remove button handles its own press, but its MouseMove still bubbles here).
            _queueDragRow = null;
            _queueDragStart = null;
            return;
        }

        if (_queueDragRow is null || _queueDragStart is not { } start
            || !ReferenceEquals((sender as FrameworkElement)?.DataContext, _queueDragRow))
            return;

        var current = e.GetPosition(null);
        if (Math.Abs(current.X - start.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var dragged = _queueDragRow;
        _queueDragRow = null;

        var draggedElement = (UIElement)sender;
        _queueDragAdornerLayer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(draggedElement);
        if (_queueDragAdornerLayer is not null)
        {
            _queueDragAdorner = new DragAdorner(draggedElement, draggedElement);
            _queueDragAdornerLayer.Add(_queueDragAdorner);
        }

        try
        {
            DragDrop.DoDragDrop(draggedElement, dragged, System.Windows.DragDropEffects.Move);
        }
        finally
        {
            if (_queueDragAdornerLayer is not null && _queueDragAdorner is not null)
                _queueDragAdornerLayer.Remove(_queueDragAdorner);
            _queueDragAdornerLayer = null;
            _queueDragAdorner = null;
        }
    }

    /// <summary>Moves the drag preview with the cursor. GiveFeedback carries no screen point, so the position comes from WinForms' Cursor, converted into the adorned element's coordinates.</summary>
    private void OnQueueRowGiveFeedback(object sender, System.Windows.GiveFeedbackEventArgs e)
    {
        if (_queueDragAdorner is not null && sender is UIElement element)
        {
            var screenPoint = System.Windows.Forms.Cursor.Position;
            var relative = element.PointFromScreen(new System.Windows.Point(screenPoint.X, screenPoint.Y));
            _queueDragAdorner.UpdatePosition(new System.Windows.Point(relative.X + 12, relative.Y + 8));
        }

        e.UseDefaultCursors = true;
        e.Handled = true;
    }

    private void OnQueueRowDragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(UpdateRowViewModel)) ? System.Windows.DragDropEffects.Move : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Reorders the queue panel's list and pushes the order down through <see cref="MainViewModel.MoveQueueRow"/>, so a drop changes the real install order.</summary>
    private void OnQueueRowDrop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetData(typeof(UpdateRowViewModel)) is not UpdateRowViewModel dragged
            || (sender as FrameworkElement)?.DataContext is not UpdateRowViewModel target
            || ReferenceEquals(dragged, target)
            || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        var oldIndex = viewModel.QueueRows.IndexOf(dragged);
        var newIndex = viewModel.QueueRows.IndexOf(target);
        if (oldIndex >= 0 && newIndex >= 0)
        {
            viewModel.MoveQueueRow(oldIndex, newIndex);
            RefreshQueueRowAutomationNames(viewModel);
        }

        e.Handled = true;
    }

    /// <summary>Entf removes the focused queue row (the command its hover button carries, found by name in the row's DataTemplate); Alt+Up and Alt+Down move it one place, since drag is otherwise the only way to reorder. A key chorded with Alt reports <see cref="Key.System"/> in e.Key, the key itself in <see cref="System.Windows.Input.KeyEventArgs.SystemKey"/>.</summary>
    private void OnQueueRowKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not FrameworkElement card)
            return;

        if (e.Key == Key.Delete)
        {
            if (card.FindName("RemoveQueueButton") is not Wpf.Ui.Controls.Button { Command: { } command } button)
                return;

            if (command.CanExecute(button.CommandParameter))
            {
                command.Execute(button.CommandParameter);
                e.Handled = true;
            }

            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) || (key != Key.Up && key != Key.Down))
            return;

        if (card.DataContext is not UpdateRowViewModel row || DataContext is not MainViewModel viewModel)
            return;

        var oldIndex = viewModel.QueueRows.IndexOf(row);
        if (oldIndex < 0)
            return;

        var newIndex = key == Key.Up ? oldIndex - 1 : oldIndex + 1;
        if (newIndex < 0 || newIndex >= viewModel.QueueRows.Count)
            return;

        viewModel.MoveQueueRow(oldIndex, newIndex);
        RefreshQueueRowAutomationNames(viewModel);
        e.Handled = true;
    }

    /// <summary>The row card's accessible name when it is created; a change of the queue refreshes the others (see <see cref="RefreshQueueRowAutomationNames"/>).</summary>
    private void OnQueueRowCardLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement card && DataContext is MainViewModel viewModel)
            SetQueueRowAutomationName(card, viewModel);
    }

    private static void SetQueueRowAutomationName(FrameworkElement card, MainViewModel viewModel)
    {
        if (card.DataContext is not UpdateRowViewModel row)
            return;

        var index = viewModel.QueueRows.IndexOf(row);
        if (index < 0)
            return;

        AutomationProperties.SetName(card,
            string.Format(viewModel.Loc["QueueRowPositionAutomation"], row.Name, index + 1, viewModel.QueueRows.Count));
    }

    /// <summary>Re-stamps every generated queue row's accessible name after the queue changed (a reorder, an item that started, a removal). WPF assigns <c>AlternationIndex</c> only when a container is generated and never recomputes it for rows a Move shifted, so a binding would leave stale positions.</summary>
    private void RefreshQueueRowAutomationNames(MainViewModel viewModel)
    {
        if (QueueItemsControl.ItemContainerGenerator.Status != GeneratorStatus.ContainersGenerated)
            return;

        for (var i = 0; i < QueueItemsControl.Items.Count; i++)
        {
            if (QueueItemsControl.ItemContainerGenerator.ContainerFromIndex(i) is not DependencyObject container)
                continue;
            if (FindQueueRowCard(container) is { } card)
                SetQueueRowAutomationName(card, viewModel);
        }
    }

    private static FrameworkElement? FindQueueRowCard(DependencyObject root)
    {
        if (root is FrameworkElement { Name: "QueueRowCard" } match)
            return match;

        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
            if (FindQueueRowCard(System.Windows.Media.VisualTreeHelper.GetChild(root, i)) is { } found)
                return found;

        return null;
    }
}
