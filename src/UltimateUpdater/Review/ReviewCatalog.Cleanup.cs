#if DEBUG
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.ViewModels.Cleanup;
using UltimateUpdater.Views.Cleanup;

namespace UltimateUpdater.Review;

internal static partial class ReviewCatalog
{
    private const string CleanupArea = "cleanup";

    private static void AddCleanup(List<ReviewSurface> list)
    {
        Add(list, "cleanup.choosing", CleanupArea, "Cleanup window before anything is measured", s =>
            Task.FromResult(CleanupScene(s, TimeSpan.Zero, null)));

        Add(list, "cleanup.measured", CleanupArea, "Cleanup window after the measurement, sizes shown", s =>
            Task.FromResult(CleanupScene(s, TimeSpan.Zero, async viewModel => await viewModel.PrescanAsync())));

        Add(list, "cleanup.cleaning", CleanupArea, "Cleanup run in progress", s =>
            Task.FromResult(CleanupScene(s, TimeSpan.FromSeconds(4), async viewModel =>
            {
                await viewModel.PrescanAsync();
                _ = viewModel.RunCleanupAsync();
                await WaitUntilAsync(() => viewModel.IsCleaning, 15);
                await Task.Delay(1500);
            })));

        Add(list, "cleanup.finished", CleanupArea, "Cleanup run finished, freed space shown", s =>
            Task.FromResult(CleanupScene(s, TimeSpan.FromMilliseconds(30), async viewModel =>
            {
                await viewModel.PrescanAsync();
                await viewModel.RunCleanupAsync();
                await WaitUntilAsync(() => viewModel.IsFinished, 30);
                await Task.Delay(1500);
            })));

        Add(list, "cleanup.stopped", CleanupArea, "Cleanup run stopped half way", s =>
            Task.FromResult(CleanupScene(s, TimeSpan.FromSeconds(4), async viewModel =>
            {
                await viewModel.PrescanAsync();
                var run = viewModel.RunCleanupAsync();
                await WaitUntilAsync(() => viewModel.IsCleaning, 15);
                await Task.Delay(800);
                viewModel.StopCommand.Execute(null);
                await run;
                await Task.Delay(1500);
            })));

        Add(list, "cleanup.history", CleanupArea, "Cleanup history window with several runs", s =>
        {
            var now = DateTimeOffset.Now;
            var viewModel = s.NewCleanupViewModel(seedHistory: history =>
            {
                history.Append(new CleanupHistoryEntry
                {
                    Timestamp = now.AddHours(-2), FreedBytes = 4_800_000_000, SkippedFiles = 3, SkippedBytes = 120_000,
                    Areas =
                    [
                        new CleanupHistoryArea { Id = "user-temp", TitleKey = "CleanupTargetUserTemp", FreedBytes = 1_200_000_000 },
                        new CleanupHistoryArea { Id = "recycle", TitleKey = "CleanupTargetRecycleBin", FreedBytes = 3_600_000_000 },
                    ],
                });
                history.Append(new CleanupHistoryEntry { Timestamp = now.AddDays(-9), FreedBytes = 0, Stopped = true });
            });
            return Task.FromResult(new ReviewScene { Window = new CleanupHistoryWindow(viewModel, expandNewest: true) });
        });
    }

    private static ReviewScene CleanupScene(ReviewSession session, TimeSpan cleanDelay, Func<CleanupViewModel, Task>? prepare)
    {
        var viewModel = session.NewCleanupViewModel(cleanDelay);
        var window = new CleanupWindow(viewModel);
        var scene = new ReviewScene { Window = window };
        if (prepare is not null)
            scene.AfterShown = () => prepare(viewModel);

        scene.AddCleanup(() =>
        {
            if (viewModel.IsCleaning)
                viewModel.StopCommand.Execute(null);

            ReviewPump.Wait(WaitUntilAsync(() => !viewModel.IsBusy, 30));
        });
        return scene;
    }
}
#endif
