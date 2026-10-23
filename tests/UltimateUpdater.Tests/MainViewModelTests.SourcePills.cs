using System.Collections.Specialized;
using System.Reflection;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

public partial class MainViewModelTests
{
    [Fact]
    public void Switching_a_source_off_in_settings_dims_its_pill_once_the_window_closes()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create([new OneShotSource("winget", Item("git", "Git", "winget"))]);
            Pump(viewModel.ScanCommand.ExecuteAsync(null));
            var pill = viewModel.ProviderStatuses.Single(p => p.SourceId == "winget");
            Assert.True(pill.IsEnabled);

            var settings = viewModel.CreateSettingsViewModel();
            settings.SourceToggles.Single(t => t.Id == "winget").IsEnabled = false;

            typeof(MainViewModel).GetMethod("OnSettingsClosed", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(viewModel, null);

            Assert.False(pill.IsEnabled);
        });
    }

    /// <summary>A pill view reset rebuilds every pill button, closing its tooltip or context menu. The
    /// flush tick runs several times a second during a scan, so it resets the view only when a pill's sort
    /// or filter key actually moved.</summary>
    /// <summary>The pill view must survive a garbage collection plus WPF's own binding cleanup: a view taken
    /// from a throwaway CollectionViewSource was detached from its collection by that cleanup, and every later
    /// Refresh threw inside PrepareLocalArray, so the pills silently stopped updating.</summary>
    [Fact]
    public void The_pill_view_keeps_working_after_a_collection_and_binding_cleanup()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create([new OneShotSource("winget")]);
            Pump(viewModel.ScanCommand.ExecuteAsync(null));

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            RunBindingCleanup();

            viewModel.ToggleSourceCommand.Execute("winget");

            Assert.Contains(viewModel.PillView.Cast<ProviderStatusViewModel>(), p => p.SourceId == "winget");
        });

        // WPF purges view records whose CollectionViewSource is gone from an idle-time cleanup pass; run that
        // pass now instead of waiting for the dispatcher to get round to it.
        static void RunBindingCleanup()
        {
            var engineType = typeof(System.Windows.Data.CollectionViewSource).Assembly
                .GetType("MS.Internal.Data.DataBindEngine", throwOnError: true)!;
            var engine = engineType.GetProperty("CurrentDataBindEngine", BindingFlags.NonPublic | BindingFlags.Static)!
                .GetValue(null);
            engineType.GetMethod("Cleanup", BindingFlags.NonPublic | BindingFlags.Instance, Type.EmptyTypes)!
                .Invoke(engine, null);
        }
    }

    [Fact]
    public void The_pill_view_is_reset_only_when_a_pill_sort_or_filter_key_changes()
    {
        WpfHarness.OnUiThread(() =>
        {
            var viewModel = Create([new OneShotSource("winget")]);
            Pump(viewModel.ScanCommand.ExecuteAsync(null));

            var resets = 0;
            ((INotifyCollectionChanged)viewModel.PillView).CollectionChanged += (_, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Reset)
                    resets++;
            };

            viewModel.Flush();
            viewModel.Flush();
            viewModel.Flush();

            Assert.Equal(0, resets);
            Assert.DoesNotContain(viewModel.PillView.Cast<ProviderStatusViewModel>(), p => p.SourceId == "winget");

            // Switched off, the quiet pill becomes noteworthy: its filter key moved, so the view re-applies.
            viewModel.ToggleSourceCommand.Execute("winget");

            Assert.True(resets >= 1);
            Assert.Contains(viewModel.PillView.Cast<ProviderStatusViewModel>(), p => p.SourceId == "winget");
        });
    }
}
