using System.Net.Http;
using UltimateUpdater.Services.SelfUpdate;
using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

/// <summary>
/// The reinstall flow for winget's different-install-technology refusal: look up, confirm once, remove,
/// install fresh, and a retry that repeats only the install.
/// </summary>
public partial class MainViewModelTests
{
    private const string DifferentTechnologyDetail =
        "Installing Example.Widget failed (exit 0x8A15008E). The installed package uses a different install technology.";

    /// <summary>A stand-in for winget that records what the flow asked of it, in order.</summary>
    private sealed class ReinstallSource : IFreshInstallSource
    {
        public List<string> Calls { get; } = [];

        public bool Fetchable { get; set; } = true;

        public bool FailUninstall { get; set; }

        public bool FailFresh { get; set; }

        /// <summary>What the scope lookup answers (null = not known).</summary>
        public string? InstalledScope { get; set; }

        /// <summary>When set, the scope lookup waits for it: the seconds the real `winget list` takes.</summary>
        public TaskCompletionSource? ScopeGate { get; set; }

        /// <summary>The scope each fresh install was asked for (null entry = none).</summary>
        public List<string?> FreshScopes { get; } = [];

        public string Id => "winget";

        public UpdateCategory Category => UpdateCategory.Software;

        public SourceTier Tier => SourceTier.A;

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        /// <summary>What a scan reports; null = the package is not listed at all.</summary>
        public UpdateItem? Listed { get; set; }

        public IAsyncEnumerable<UpdateItem> ScanAsync(CancellationToken cancellationToken) =>
            Listed is { } item ? new[] { item }.ToAsyncEnumerable() : AsyncEnumerable.Empty<UpdateItem>();

        public bool CanUninstall(UpdateItem item) => true;

        public Task InstallAsync(UpdateItem item, IProgress<InstallProgress> progress, CancellationToken cancellationToken)
        {
            Calls.Add("install");
            return Task.CompletedTask;
        }

        public Task UninstallAsync(UpdateItem item, IProgress<InstallProgress> progress, CancellationToken cancellationToken)
        {
            Calls.Add("uninstall");
            return FailUninstall ? Task.FromException(new InvalidOperationException("remove failed (exit 1)")) : Task.CompletedTask;
        }

        public Task<bool> CanFetchAsync(UpdateItem item, CancellationToken cancellationToken)
        {
            Calls.Add("fetch");
            return Task.FromResult(Fetchable);
        }

        public async Task<string?> GetInstalledScopeAsync(UpdateItem item, CancellationToken cancellationToken)
        {
            Calls.Add("scope");
            if (ScopeGate is { } gate)
                await gate.Task;
            return InstalledScope;
        }

        public Task FreshInstallAsync(UpdateItem item, IProgress<InstallProgress> progress, CancellationToken cancellationToken)
        {
            Calls.Add("fresh");
            FreshScopes.Add(item.InstallScope);
            return FailFresh ? Task.FromException(new InvalidOperationException("install failed (exit 1)")) : Task.CompletedTask;
        }
    }

    private static (MainViewModel ViewModel, ReinstallSource Source, UpdateRowViewModel Row, Func<int> Confirms) CreateReinstall()
    {
        var logging = new LoggingService();
        var settings = new SettingsService(logging, Path.Combine(
            Directory.CreateTempSubdirectory("wu-vm-test-").FullName, "settings.json"));
        settings.Current.Ui.Language = "en";
        var source = new ReinstallSource();
        IReadOnlyList<IUpdateSource> sources = [source];
        var history = new HistoryService(logging);

        // A mock session never elevates, so the "winget" id runs in-process instead of starting the worker.
        var viewModel = new MainViewModel(
            settings, logging, new ThemeService(), new ScanOrchestrator(sources, logging),
            new IconCacheService(logging), new InstallQueue(sources, settings, history, logging, isMockSession: true), history,
            new AutostartService(logging), new NotificationService(settings, logging),
            new Services.SelfUpdate.SelfUpdateService(new HttpClient(), logging, new PublicUpdateChannel(logging)), sources);

        var confirms = 0;
        viewModel.ReinstallConfirmOverride = () =>
        {
            confirms++;
            return true;
        };

        var row = new UpdateRowViewModel(Item("Example.Widget", "Example Widget", "winget"), actions: viewModel);
        viewModel.Updates.Add(row);
        row.ApplyInstallStatus(new InstallItemStatus("winget", "Example.Widget", 0, null, false, "failed") { Detail = DifferentTechnologyDetail });
        return (viewModel, source, row, () => confirms);
    }

    private static void WithAttendedUi(Action body)
    {
        App.ForceAttendedForTest = true;
        try
        {
            WpfHarness.OnUiThread(body);
        }
        finally
        {
            App.ForceAttendedForTest = false;
        }
    }

    [Fact]
    public void The_reinstall_is_offered_for_the_different_technology_refusal_and_for_nothing_else()
    {
        WithAttendedUi(() =>
        {
            var (_, _, row, _) = CreateReinstall();
            Assert.True(row.ShowReinstallButton);
            Assert.True(row.ReinstallCommand.CanExecute(null));

            foreach (var other in new[]
            {
                "install failed (exit 0x8A15002F)",
                "install failed (exit 0x8A150011)",
                "install failed (exit 1618)",
            })
            {
                row.ApplyInstallStatus(new InstallItemStatus("winget", "Example.Widget", 0, null, false, "failed") { Detail = other });
                Assert.False(row.ShowReinstallButton, other);
            }
        });
    }

    [Fact]
    public void A_reinstall_looks_up_confirms_once_removes_then_installs_fresh_without_the_upgrade_path()
    {
        WithAttendedUi(() =>
        {
            var (viewModel, source, row, confirms) = CreateReinstall();

            RunPumped(viewModel.ReinstallRowAsync(row));

            Assert.Equal(["fetch", "scope", "uninstall", "fresh"], source.Calls);
            Assert.Equal(1, confirms());
            Assert.Equal(RowInstallState.Succeeded, row.InstallState);
            Assert.False(row.PendingFreshInstall);
            Assert.False(row.WasUninstalled);
        });
    }

    [Fact]
    public void A_lookup_that_fails_stops_the_reinstall_before_anything_is_confirmed_or_removed()
    {
        WithAttendedUi(() =>
        {
            var (viewModel, source, row, confirms) = CreateReinstall();
            source.Fetchable = false;

            RunPumped(viewModel.ReinstallRowAsync(row));

            Assert.Equal(["fetch"], source.Calls);
            Assert.Equal(0, confirms());
            Assert.Equal(RowInstallState.Failed, row.InstallState);
            Assert.False(row.PendingFreshInstall);
        });
    }

    [Fact]
    public void A_declined_confirmation_removes_nothing()
    {
        WithAttendedUi(() =>
        {
            var (viewModel, source, row, _) = CreateReinstall();
            viewModel.ReinstallConfirmOverride = () => false;

            RunPumped(viewModel.ReinstallRowAsync(row));

            Assert.Equal(["fetch"], source.Calls);
            Assert.Equal(RowInstallState.Failed, row.InstallState);
        });
    }

    [Fact]
    public void A_failed_removal_never_reaches_the_fresh_install()
    {
        WithAttendedUi(() =>
        {
            var (viewModel, source, row, _) = CreateReinstall();
            source.FailUninstall = true;

            RunPumped(viewModel.ReinstallRowAsync(row));

            Assert.Equal(["fetch", "scope", "uninstall"], source.Calls);
            Assert.False(row.PendingFreshInstall);
            Assert.Equal(RowInstallState.Failed, row.InstallState);
        });
    }

    [Fact]
    public void The_retry_of_a_failed_removal_reruns_the_whole_reinstall_not_a_bare_removal()
    {
        WithAttendedUi(() =>
        {
            var (viewModel, source, row, confirms) = CreateReinstall();
            source.FailUninstall = true;

            RunPumped(viewModel.ReinstallRowAsync(row));

            Assert.Equal(["fetch", "scope", "uninstall"], source.Calls);
            Assert.Equal(RowInstallState.Failed, row.InstallState);
            Assert.True(row.ReinstallRemovalFailed);

            source.FailUninstall = false;
            source.Calls.Clear();
            row.RetryCommand.Execute(null);
            Pump(WaitUntilAsync(() => row.InstallState == RowInstallState.Succeeded, TimeSpan.FromSeconds(5)));

            // Lookup, a second confirmation, the removal, then the fresh install: never the removal alone.
            Assert.Equal(["fetch", "scope", "uninstall", "fresh"], source.Calls);
            Assert.Equal(2, confirms());
            Assert.False(row.ReinstallRemovalFailed);
            Assert.False(row.PendingFreshInstall);
            Assert.False(row.WasUninstalled);
        });
    }

    [Fact]
    public void A_later_plain_run_on_the_row_drops_the_reinstall_retry()
    {
        WithAttendedUi(() =>
        {
            var (viewModel, source, row, _) = CreateReinstall();
            source.FailUninstall = true;
            RunPumped(viewModel.ReinstallRowAsync(row));
            Assert.True(row.ReinstallRemovalFailed);

            row.BeginInstall();

            Assert.False(row.ReinstallRemovalFailed);
        });
    }

    [Fact]
    public void A_failed_fresh_install_keeps_a_retry_that_repeats_only_the_install()
    {
        WithAttendedUi(() =>
        {
            var (viewModel, source, row, confirms) = CreateReinstall();
            source.FailFresh = true;

            RunPumped(viewModel.ReinstallRowAsync(row));

            Assert.Equal(["fetch", "scope", "uninstall", "fresh"], source.Calls);
            Assert.Equal(RowInstallState.Failed, row.InstallState);
            Assert.True(row.PendingFreshInstall);
            Assert.Equal(LocalizationService.Instance["ReinstallInstallFailed"], row.FailureLine);
            // The button no longer offers a second removal: the old version is already gone.
            Assert.False(row.ShowReinstallButton);

            source.FailFresh = false;
            source.Calls.Clear();
            row.RetryCommand.Execute(null);
            Pump(WaitUntilAsync(() => row.InstallState == RowInstallState.Succeeded, TimeSpan.FromSeconds(5)));

            Assert.Equal(["fresh"], source.Calls);
            Assert.False(row.PendingFreshInstall);
            Assert.Equal(1, confirms());
        });
    }

    [Theory]
    [InlineData("machine")]
    [InlineData("user")]
    [InlineData(null)]
    public void The_fresh_install_lands_in_the_scope_the_program_had_and_names_none_when_unknown(string? scope)
    {
        WithAttendedUi(() =>
        {
            var (viewModel, source, row, _) = CreateReinstall();
            source.InstalledScope = scope;

            RunPumped(viewModel.ReinstallRowAsync(row));

            Assert.Equal([scope], source.FreshScopes);
            Assert.Equal(RowInstallState.Succeeded, row.InstallState);
        });
    }

    [Fact]
    public void The_retry_of_a_failed_fresh_install_keeps_the_scope_read_before_the_removal()
    {
        WithAttendedUi(() =>
        {
            var (viewModel, source, row, _) = CreateReinstall();
            source.InstalledScope = "user";
            source.FailFresh = true;

            RunPumped(viewModel.ReinstallRowAsync(row));
            source.FailFresh = false;
            source.InstalledScope = null; // the program is gone: a second lookup would find nothing
            row.RetryCommand.Execute(null);
            Pump(WaitUntilAsync(() => row.InstallState == RowInstallState.Succeeded, TimeSpan.FromSeconds(5)));

            Assert.Equal(["user", "user"], source.FreshScopes);
        });
    }

    /// <summary>After the old version is gone and the new install failed, the row is the only way back to
    /// the program: a scan that no longer lists the package, or still lists it from before the removal,
    /// must neither drop the row nor replace it with a plain update row.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_scan_keeps_the_row_of_a_reinstall_whose_install_is_still_pending(bool stillListed)
    {
        WithAttendedUi(() =>
        {
            var (viewModel, source, manualRow, _) = CreateReinstall();
            viewModel.Updates.Remove(manualRow);
            source.Listed = Item("Example.Widget", "Example Widget", "winget");
            Pump(viewModel.ScanCommand.ExecuteAsync(null));
            var row = Assert.Single(viewModel.Updates);
            row.ApplyInstallStatus(new InstallItemStatus("winget", "Example.Widget", 0, null, false, "failed") { Detail = DifferentTechnologyDetail });

            source.FailFresh = true;
            RunPumped(viewModel.ReinstallRowAsync(row));
            Assert.True(row.PendingFreshInstall);

            if (!stillListed)
                source.Listed = null;
            Pump(viewModel.ScanCommand.ExecuteAsync(null));

            Assert.Same(row, Assert.Single(viewModel.Updates));
            Assert.True(row.PendingFreshInstall);
            Assert.Equal(RowInstallState.Failed, row.InstallState);
        });
    }

    [Fact]
    public void A_row_that_is_not_a_different_technology_failure_cannot_be_reinstalled_by_calling_the_flow_directly()
    {
        WithAttendedUi(() =>
        {
            var (viewModel, source, row, confirms) = CreateReinstall();
            row.ApplyInstallStatus(new InstallItemStatus("winget", "Example.Widget", 0, null, false, "failed") { Detail = "install failed (exit 1618)" });

            RunPumped(viewModel.ReinstallRowAsync(row));
            RunPumped(viewModel.RetryFreshInstallAsync(row));

            Assert.Empty(source.Calls);
            Assert.Equal(0, confirms());
        });
    }

    /// <summary>The reinstall holds its row from the lookup on: while it reads the scope (the row is still
    /// Failed then), a second reinstall or an uninstall of the same package is refused, so the package is
    /// never removed twice.</summary>
    [Fact]
    public void A_reinstall_under_way_refuses_a_second_reinstall_and_an_uninstall_of_the_same_row()
    {
        var warnings = new List<string>();
        Dialogs.WarnObserverForTests = warnings.Add;
        try
        {
            WithAttendedUi(() =>
            {
                var (viewModel, source, row, confirms) = CreateReinstall();
                var gate = source.ScopeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                viewModel.UninstallConfirmOverride = () => true;

                var first = viewModel.ReinstallRowAsync(row);
                Assert.Equal(["fetch", "scope"], source.Calls);

                RunPumped(viewModel.ReinstallRowAsync(row));
                RunPumped(viewModel.UninstallRowAsync(row));

                var refusal = LocalizationService.Instance["UninstallBlockedByActivity"];
                Assert.Equal([refusal, refusal], warnings);
                Assert.Equal(["fetch", "scope"], source.Calls);
                Assert.Equal(1, confirms());

                gate.SetResult();
                RunPumped(first);

                Assert.Equal(["fetch", "scope", "uninstall", "fresh"], source.Calls);
                Assert.Equal(RowInstallState.Succeeded, row.InstallState);
            });
        }
        finally
        {
            Dialogs.WarnObserverForTests = null;
        }
    }

    /// <summary>A Retry (or any install) clicked while the reinstall reads the scope must not queue the row:
    /// the upgrade would run beside the removal of the same package.</summary>
    [Fact]
    public void A_retry_during_the_scope_lookup_of_a_reinstall_does_not_queue_an_install_beside_it()
    {
        WithAttendedUi(() =>
        {
            var (viewModel, source, row, _) = CreateReinstall();
            var gate = source.ScopeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var first = viewModel.ReinstallRowAsync(row);
            Assert.Equal(["fetch", "scope"], source.Calls);

            row.RetryCommand.Execute(null);
            Pump(Task.Delay(600));

            Assert.DoesNotContain("install", source.Calls);
            Assert.Equal(RowInstallState.Failed, row.InstallState);

            gate.SetResult();
            RunPumped(first);

            Assert.Equal(["fetch", "scope", "uninstall", "fresh"], source.Calls);
            Assert.Equal(RowInstallState.Succeeded, row.InstallState);
        });
    }

    [Fact]
    public void A_declined_or_refused_reinstall_releases_its_hold_on_the_row()
    {
        WithAttendedUi(() =>
        {
            var (viewModel, source, row, _) = CreateReinstall();
            source.Fetchable = false;
            RunPumped(viewModel.ReinstallRowAsync(row));

            source.Fetchable = true;
            source.Calls.Clear();
            RunPumped(viewModel.ReinstallRowAsync(row));

            Assert.Equal(["fetch", "scope", "uninstall", "fresh"], source.Calls);
        });
    }
}
