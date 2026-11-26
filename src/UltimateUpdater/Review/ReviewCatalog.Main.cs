#if DEBUG
using System.Windows;
using System.Windows.Controls;
using UltimateUpdater.Services.Sources;
using UltimateUpdater.ViewModels.Main;
using UltimateUpdater.Views.Main;

namespace UltimateUpdater.Review;

internal static partial class ReviewCatalog
{
    private const string MainArea = "main";

    private static void AddMain(List<ReviewSurface> list)
    {
        Add(list, "main.start", MainArea, "Main window before the first scan", s =>
            MainScene(s, MockUpdateSource.CreateAll(), prepare: null));

        Add(list, "main.scanning", MainArea, "Scan running: rows streaming in, one source still working", s =>
            MainScene(s, [.. MockUpdateSource.CreateAll(), new BlockingReviewSource("slowsource")], async (vm, _, _) =>
            {
                _ = vm.ScanCommand.ExecuteAsync(null);
                await WaitUntilAsync(() => vm.IsScanning, 10);
                await Task.Delay(1800);
            }, cleanup: async (vm, _) =>
            {
                await vm.CancelScanCommand.ExecuteAsync(null);
                await WaitUntilAsync(() => !vm.IsScanning, 20);
            }));

        Add(list, "main.results", MainArea, "Scan finished with update rows and a failing source", s =>
            MainScene(s, MockUpdateSource.CreateAll(), async (vm, _, _) => await ScanAsync(vm)));

        Add(list, "main.selected", MainArea, "Every row ticked, install band visible", s =>
            MainScene(s, MockUpdateSource.CreateAll(), async (vm, _, _) =>
            {
                await ScanAsync(vm);
                vm.SelectAllCommand.Execute(null);
            }));

        Add(list, "main.uptodate", MainArea, "Scan finished, nothing to update", s =>
            MainScene(s, [new EmptyReviewSource("winget"), new EmptyReviewSource("npm")], async (vm, _, _) => await ScanAsync(vm)));

        Add(list, "main.installing", MainArea, "Install running: progress per row and in the band", s =>
            MainScene(s, MockUpdateSource.CreateAll(), async (vm, _, _) =>
            {
                await ScanAsync(vm);
                vm.SelectAllCommand.Execute(null);
                _ = vm.InstallSelectedCommand.ExecuteAsync(null);
                await WaitUntilAsync(() => vm.IsInstalling, 10);
                await Task.Delay(2500);
            }, cleanup: async (vm, _) =>
            {
                vm.CancelInstallCommand.Execute(null);
                await WaitUntilAsync(() => !vm.IsInstalling, 30);
            }));

        Add(list, "main.finished", MainArea, "Install finished: done rows, failed rows, restart notice", s =>
            MainScene(s, MockUpdateSource.CreateAll(), async (vm, _, _) =>
            {
                await ScanAsync(vm);
                vm.SelectAllCommand.Execute(null);
                _ = vm.InstallSelectedCommand.ExecuteAsync(null);
                await WaitUntilAsync(() => vm.IsInstalling, 10);
                await WaitUntilAsync(() => !vm.IsInstalling, 150);
                await Task.Delay(500);
            }, cleanup: async (vm, _) =>
            {
                vm.CancelInstallCommand.Execute(null);
                await WaitUntilAsync(() => !vm.IsInstalling, 30);
            }));

        Add(list, "main.rowmenu", MainArea, "Right-click menu of an update row", s =>
            MainScene(s, MockUpdateSource.CreateAll(), async (vm, window, scene) =>
            {
                await ScanAsync(vm);
                await ReviewPump.SettleAsync();
                var row = FirstRow(window) ?? throw new InvalidOperationException("The table has no row to open a menu on.");
                OpenMenu(scene, "menu", row.ContextMenu ?? throw new InvalidOperationException("The row has no context menu."), row);
            }));

        Add(list, "main.columns", MainArea, "Column chooser menu of the table header", s =>
            MainScene(s, MockUpdateSource.CreateAll(), async (vm, window, scene) =>
            {
                await ScanAsync(vm);
                var anchor = window.Table.MainColumnsButton;
                var menu = ColumnChooser.ShowFor(
                    anchor, MainViewModel.MainTableColumns, vm.IsMainColumnHidden, vm.SetMainColumnHidden, vm.ResetMainColumns)
                    ?? throw new InvalidOperationException("The column chooser did not open.");
                scene.Popups.Add(("menu", menu));
                scene.AddCleanup(() => menu.IsOpen = false);
            }));

        Add(list, "main.toolbar-tips", MainArea, "Tooltips of the toolbar buttons", s =>
            MainScene(s, MockUpdateSource.CreateAll(), async (vm, window, scene) =>
            {
                await ScanAsync(vm);
                OpenToolTip(scene, "tip-cleanup", window.Toolbar.ToolbarCleanupButton);
                OpenToolTip(scene, "tip-uninstaller", window.Toolbar.ToolbarUninstallerButton);
            }));
    }

    private static async Task ScanAsync(MainViewModel vm)
    {
        await vm.ScanCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => !vm.IsScanning, 60);
        await Task.Delay(800);
    }

    /// <summary>The list is grouped: the generator's own items are the groups, so rows are reached by index over the flat items.</summary>
    private static ListBoxItem? FirstRow(MainWindow window)
    {
        var list = window.Table.UpdatesListBox;
        for (var i = 0; i < list.Items.Count; i++)
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem { IsVisible: true } row)
                return row;
        }

        return null;
    }

    private static Task<ReviewScene> MainScene(
        ReviewSession session,
        IReadOnlyList<IUpdateSource> sources,
        Func<MainViewModel, MainWindow, ReviewScene, Task>? prepare,
        Func<MainViewModel, ReviewScene, Task>? cleanup = null)
    {
        session.ThemeService.Apply(session.Theme);
        var viewModel = session.NewMain(sources, out var settings);
        var window = new MainWindow(viewModel, session.ThemeService, settings, session.Logging);
        var scene = new ReviewScene { Window = window };
        scene.AfterShown = prepare is null ? null : () => prepare(viewModel, window, scene);
        if (cleanup is not null)
            scene.AddCleanup(() => ReviewPump.Wait(cleanup(viewModel, scene)));
        return Task.FromResult(scene);
    }
}
#endif
