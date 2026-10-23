using System.Net.Http;
using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

public class UninstallCommandTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(1641, true)]   // reboot initiated
    [InlineData(3010, true)]   // reboot required
    [InlineData(1, false)]
    [InlineData(5, false)]
    [InlineData(unchecked((int)0x80070005), false)]   // access denied
    [InlineData(unchecked((int)0x8A15002B), false)]   // "no applicable upgrade", winget did nothing
    public void Winget_treats_reboot_codes_as_success(int exitCode, bool expected)
    {
        Assert.Equal(expected, WingetSource.IsInstallSuccess(exitCode));
    }

    /// <summary>
    /// 0x8A15002B means winget changed NOTHING: every command this app sends names one exact package the
    /// scan had just seen an upgrade for, so it is a "retry unelevated, then say so" outcome,
    /// never a success.
    /// </summary>
    [Fact]
    public void No_applicable_upgrade_is_not_a_successful_update()
    {
        Assert.True(WingetSource.DidNothing(unchecked((int)0x8A15002B)));
        Assert.False(WingetSource.IsInstallSuccess(unchecked((int)0x8A15002B)));

        Assert.False(WingetSource.DidNothing(0));
        Assert.False(WingetSource.DidNothing(3010));
    }

    /// <summary>
    /// The scan passes --include-unknown; the install has to as well, or winget lists a package as
    /// upgradable and then refuses to upgrade it. That refusal is what PokeMMO produced, word for word:
    /// "Die Versionsnummer dieses Pakets kann nicht bestimmt werden. Um trotzdem ein Upgrade durchzuführen,
    /// fügen Sie … --include-unknown hinzu."
    /// </summary>
    [Fact]
    public void The_upgrade_command_includes_packages_with_an_unknown_version() =>
        Assert.Contains("--include-unknown", WingetSource.InstallArguments("PokeMMO.PokeMMO"));

    /// <summary>
    /// Every rung stays silent and non-interactive, nothing here may put a window in front of the user in
    /// the middle of an unattended batch.
    /// </summary>
    [Fact]
    public void Every_attempt_stays_silent_and_non_interactive()
    {
        foreach (var attempt in WingetSource.InstallLadder("Contoso.App"))
        {
            Assert.Contains("--silent", attempt.Arguments);
            Assert.Contains("--disable-interactivity", attempt.Arguments);
            Assert.Contains("Contoso.App", attempt.Arguments);
            Assert.DoesNotContain("--interactive", attempt.Arguments);
            Assert.DoesNotContain("--wait", attempt.Arguments);
        }
    }

    /// <summary>
    /// The ladder, in order: the plain upgrade, then the same pinned to the user scope (Bitcoin Core's
    /// "eine neuere Paketversion … trifft jedoch nicht auf Ihr System zu"), then install instead of upgrade
    /// for packages that have no upgrade path at all. --include-unknown is not a valid install option and is
    /// left off that last rung.
    /// </summary>
    [Fact]
    public void The_ladder_escalates_scope_and_then_falls_back_to_install()
    {
        var ladder = WingetSource.InstallLadder("BitcoinCoreProject.BitcoinCore");

        Assert.Equal(3, ladder.Count);

        Assert.Equal("upgrade", ladder[0].Arguments[0]);
        Assert.DoesNotContain("--scope", ladder[0].Arguments);

        Assert.Equal("upgrade", ladder[1].Arguments[0]);
        Assert.Contains("--scope", ladder[1].Arguments);
        Assert.Contains("user", ladder[1].Arguments);

        Assert.Equal("install", ladder[2].Arguments[0]);
        Assert.DoesNotContain("--include-unknown", ladder[2].Arguments);
    }

    /// <summary>
    /// The queue decides whether to retry a job WITHOUT administrator rights by looking for winget's
    /// scope/elevation codes in the failure text. Reporting only the last rung threw that away: Bitcoin
    /// Core's FIRST rung is the one that says 0x8A15002B, the last one says something else, and the retry
    /// that would have worked never ran.
    /// </summary>
    [Fact]
    public void A_ladder_failure_names_every_rung_so_the_unelevated_retry_still_fires()
    {
        string[] attempts =
        [
            "[upgrade] winget install failed for 'BitcoinCoreProject.BitcoinCore' (exit 0x8A15002B). ...",
            "[install] winget install failed for 'BitcoinCoreProject.BitcoinCore' (exit 0x8A150061). ...",
        ];

        var detail = WingetSource.LadderFailure(attempts, unchecked((int)0x8A150061));

        Assert.Contains("[upgrade]", detail);
        Assert.Contains("[install]", detail);
        Assert.True(
            WingetSource.NeedsUserScopeRetry(detail),
            "The first rung's user-scope refusal has to survive into the reported failure.");
    }

    /// <summary>And the "winget did nothing at all" case still says so in plain words.</summary>
    [Fact]
    public void A_ladder_that_changed_nothing_says_so()
    {
        var detail = WingetSource.LadderFailure(["[upgrade] ..."], unchecked((int)0x8A15002B));

        Assert.Contains("changed nothing", detail);
    }

    /// <summary>
    /// a ladder that ends on "no applicable upgrade" is not the user's failed install, so
    /// it throws its own terminal outcome and the row reads "stuck" instead of red. Any other exit code
    /// stays an ordinary failure.
    /// </summary>
    [Fact]
    public void Only_a_no_applicable_upgrade_ladder_throws_the_stuck_outcome()
    {
        Assert.IsType<NotApplicableException>(
            WingetSource.LadderException(["[upgrade] ..."], unchecked((int)0x8A15002B), "17.0.9"));

        Assert.IsType<InvalidOperationException>(
            WingetSource.LadderException(["[upgrade] ..."], unchecked((int)0x8A150061)));
    }

    /// <summary>
    /// A real observed Microsoft.OpenJDK.17 ladder: rung 1 ("upgrade") reports 0x8A15002B ("no applicable
    /// upgrade found"), rung 2 ("upgrade --scope user"), the LAST/terminal rung, reports a DIFFERENT
    /// but equally harmless code, 0x8A150014 ("no installed package found matching input criteria",
    /// "kein installiertes Paket gefunden"). Classification is keyed on the terminal rung's OWN code
    /// (<see cref="WingetSource.IsInapplicableOutcome"/>), broadened to recognise this second code too,
    /// not on "did any earlier rung say 0x8A15002B", which would also excuse a genuinely different later
    /// failure (see the next test).
    /// </summary>
    [Fact]
    public void An_equally_harmless_terminal_code_also_marks_the_ladder_stuck()
    {
        string[] attempts =
        [
            "[upgrade] winget install failed for 'Microsoft.OpenJDK.17' (exit 0x8A15002B). ...",
            "[upgrade --scope user] winget install failed for 'Microsoft.OpenJDK.17' (exit 0x8A150014). ...",
        ];

        var ex = WingetSource.LadderException(attempts, unchecked((int)0x8A150014));

        Assert.IsType<NotApplicableException>(ex);
    }

    /// <summary>
    /// An EARLIER rung's "not applicable" must never excuse a LATER rung's genuine, unrelated failure.
    /// Rung 1 says 0x8A15002B same as OpenJDK; rung 2, the terminal rung, crashes for real (access
    /// denied). The row must stay a red failure, not read "stuck" just because an earlier, different rung
    /// happened to say "not applicable" first.
    /// </summary>
    [Fact]
    public void An_earlier_not_applicable_rung_never_masks_a_later_genuine_failure()
    {
        string[] attempts =
        [
            "[upgrade] winget install failed for 'Some.Package' (exit 0x8A15002B). ...",
            "[upgrade --scope user] winget install failed for 'Some.Package' (exit 0x80070005). ...", // access denied
        ];

        var ex = WingetSource.LadderException(attempts, unchecked((int)0x80070005));

        Assert.IsType<InvalidOperationException>(ex);
    }

    /// <summary>
    /// The context-menu Uninstall entry exists at three places (MainWindow.xaml's shared row menu, the
    /// manual-row menu, Steam's child menu) but is only USABLE where <see cref="IUpdateSource.CanUninstall"/>
    /// says yes, this table is the guarded record of which sources that is, so a future source cannot
    /// silently join the "no removal" list by omission. Every id from <see cref="SourceRegistry.CreateAll"/>
    /// must appear here exactly once; the per-launcher Tier C sources from
    /// <see cref="ClientLauncherSource.CreateKnown"/> are covered generically below instead, since their
    /// count varies with <see cref="LauncherCatalog"/>.
    /// </summary>
    private static readonly Dictionary<string, bool> ExpectedCanUninstall = new()
    {
        // has removal
        ["winget"] = true,
        ["steam"] = true,
        ["chocolatey"] = true,
        ["scoop"] = true,
        ["pip"] = true,
        ["npm"] = true,
        ["pnpm"] = true,
        ["dotnet-tool"] = true,
        ["psgallery"] = true,
        ["uv-tool"] = true,
        ["gh-extension"] = true,
        ["pipx"] = true,
        ["vscode-extension"] = true,
        ["gem"] = true,
        ["composer"] = true,
        ["vcpkg"] = true,
        ["mise"] = true,
        ["android-sdk"] = true,
        ["gcloud"] = true,
        ["ollama"] = true,
        ["volta"] = true,
        ["pyenv-win"] = true,

        // deliberately not offered, a one-line reason lives on each source's class doc
        ["windowsupdate"] = false,
        ["nvidia-driver"] = false,
        ["rustup"] = false,
        ["wsl"] = false,
        ["deno"] = false,
        ["bun"] = false,
        ["uv"] = false,
        ["flutter"] = false,
        ["android-studio"] = false,
        ["github-release"] = false,
        // msstore/epic/gog are LauncherSourceBase subclasses, covered generically below, not listed here.
    };

    /// <summary>A representative item per source, id/name are placeholders; only the source-specific
    /// fields <see cref="CanUninstall"/> actually reads (InstalledVersion, Meta) carry real data.</summary>
    private static UpdateItem RepresentativeItem(IUpdateSource source) => new()
    {
        // Numeric so Steam's own id validation (a real app id) also accepts it, every other source's
        // CanUninstall is indifferent to the id's shape.
        Id = "440",
        SourceId = source.Id,
        Category = source.Category,
        Name = "Sample Package",
        InstalledVersion = "1.0.0",
        Meta = source is VoltaSource ? new Dictionary<string, string> { ["voltaName"] = "sample" } : [],
    };

    [Fact]
    public void The_uninstall_coverage_table_matches_every_registered_source()
    {
        var sources = SourceRegistry.CreateAll(new LoggingService());
        var launcherIds = new HashSet<string>();

        foreach (var source in sources)
        {
            if (source is LauncherSourceBase)
            {
                launcherIds.Add(source.Id);
                Assert.False(
                    source.CanUninstall(RepresentativeItem(source)),
                    $"'{source.Id}' is a Tier C launcher hand-off; it must not offer removal.");
                continue;
            }

            Assert.True(
                ExpectedCanUninstall.TryGetValue(source.Id, out var expected),
                $"'{source.Id}' is registered but missing from the uninstall coverage table, add a row.");

            var actual = source.CanUninstall(RepresentativeItem(source));
            Assert.True(
                expected == actual,
                $"'{source.Id}': expected CanUninstall == {expected}, was {actual}.");
        }

        var coveredNonLauncherIds = sources.Where(s => s is not LauncherSourceBase).Select(s => s.Id).ToHashSet();
        var stale = ExpectedCanUninstall.Keys.Where(id => !coveredNonLauncherIds.Contains(id)).ToList();
        Assert.True(stale.Count == 0, $"Coverage table names sources no longer registered: {string.Join(", ", stale)}");
    }

    [Fact]
    public void Winget_uninstall_is_exact_and_silent()
    {
        var arguments = WingetSource.UninstallArguments("Git.Git");

        Assert.Contains("uninstall", arguments);
        Assert.Contains("--id", arguments);
        Assert.Contains("Git.Git", arguments);
        Assert.Contains("--exact", arguments);
        Assert.Contains("--silent", arguments);
        Assert.DoesNotContain("--interactive", arguments);
    }

    [Fact]
    public void A_steam_game_can_be_started_by_its_numeric_id()
    {
        var source = new SteamSource(new LoggingService());

        Assert.Equal("steam://rungameid/440", source.LaunchTarget(SteamItem("440")));
    }

    [Fact]
    public void A_non_numeric_steam_id_gets_no_start()
    {
        var source = new SteamSource(new LoggingService());

        Assert.Null(source.LaunchTarget(SteamItem("440/../evil")));
    }

    /// <summary>
    /// steam://uninstall only opens Steam's confirmation dialog; whether the user then confirmed is
    /// unknowable from here, and the queue reads "returned without throwing" as success. So this reports a
    /// manual handoff instead of a completed removal, never both, and never a claim in either direction.
    /// </summary>
    [Fact]
    public async Task Steam_uninstall_reports_a_handoff_not_a_completed_removal()
    {
        // A no-op stand-in for the shell hand-off, without it, the real one (UriLauncher.LaunchDetached)
        // would open Steam's own uninstall confirmation for app id 440 on whatever machine runs
        // `dotnet test`/`build.bat`.
        var launched = new List<string>();
        var source = new SteamSource(new LoggingService(), launched.Add);
        var game = SteamItem("440");

        Assert.True(source.CanUninstall(game));
        await Assert.ThrowsAsync<ManualHandoffException>(() =>
            source.UninstallAsync(game, new Progress<InstallProgress>(), CancellationToken.None));

        Assert.Equal(["steam://uninstall/440"], launched);
    }

    [Fact]
    public async Task A_non_numeric_steam_id_refuses_removal_outright()
    {
        var source = new SteamSource(new LoggingService());
        var game = SteamItem("440/../evil");

        Assert.False(source.CanUninstall(game));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            source.UninstallAsync(game, new Progress<InstallProgress>(), CancellationToken.None));
    }

    /// <summary>
    /// Retry always installed, whatever had failed, so retrying a failed REMOVAL installed the package the
    /// user had just asked to have taken off the machine, and the row then read "Uninstalled" for it.
    /// </summary>
    [Fact]
    public void Retrying_a_failed_uninstall_removes_again_instead_of_installing()
    {
        var actions = new RecordingRowActions();
        var row = new UpdateRowViewModel(WingetItem(), actions: actions);

        // What UninstallRowAsync does to a row, then the failure.
        row.BeginInstall();
        row.WasUninstalled = true;
        row.InstallState = RowInstallState.Failed;

        row.RetryCommand.Execute(null);

        Assert.Equal(1, actions.UninstallCalls);
        Assert.Equal(0, actions.InstallCalls);
    }

    [Fact]
    public void Retrying_a_failed_install_installs()
    {
        var actions = new RecordingRowActions();
        var row = new UpdateRowViewModel(WingetItem(), actions: actions);

        row.BeginInstall();
        row.InstallState = RowInstallState.Failed;

        row.RetryCommand.Execute(null);

        Assert.Equal(1, actions.InstallCalls);
        Assert.Equal(0, actions.UninstallCalls);
    }

    /// <summary>
    /// Installing a row that had just failed to be removed must not keep the removal wording: the result
    /// of an install is "done", never "uninstalled".
    /// </summary>
    [Fact]
    public void Installing_after_a_failed_removal_clears_the_removal_result()
    {
        var row = new UpdateRowViewModel(WingetItem());

        row.BeginInstall();
        row.WasUninstalled = true;
        row.InstallState = RowInstallState.Failed;

        row.BeginInstall();

        Assert.False(row.WasUninstalled);
    }

    /// <summary>A row that is queued or running is not installable, its checkbox must not stay on screen
    /// or be counted in the select-all totals while it is already being installed.</summary>
    [Theory]
    [InlineData(RowInstallState.Idle, true)]
    [InlineData(RowInstallState.Failed, true)]
    [InlineData(RowInstallState.Queued, false)]
    [InlineData(RowInstallState.Running, false)]
    [InlineData(RowInstallState.Succeeded, false)]
    public void A_row_is_only_installable_while_it_is_idle_or_failed(RowInstallState state, bool expected)
    {
        var row = new UpdateRowViewModel(WingetItem()) { InstallState = state };

        Assert.Equal(expected, row.CanInstall);
    }

    /// <summary>
    /// Every button in a row's action cell is drawn in the SAME grid cell, so two visible at once means one
    /// is painted over the other. The NVIDIA row is exactly that case: Tier A and installable, but it also
    /// carries a link, its "Aktualisieren" button must not be covered by "Öffnen".
    /// </summary>
    [Fact]
    public void An_installable_row_with_a_link_shows_its_install_button_not_open()
    {
        var row = new UpdateRowViewModel(new UpdateItem
        {
            Id = "nvidia-graphics-driver",
            SourceId = "nvidia-driver",
            Category = UpdateCategory.SystemAndDrivers,
            Name = "NVIDIA Graphics Driver",
            InstalledVersion = "580.88",
            AvailableVersion = "610.74",
            Tier = SourceTier.A,
            OpenTarget = "https://www.nvidia.com/en-us/drivers/details/274187/",
        });

        Assert.True(row.ShowInstallButton);
        Assert.False(row.ShowOpenButton);

        // the link is still reachable, the row is not manual-only, so it keeps its context menu
        Assert.True(row.CanOpen);
        Assert.False(row.IsManualOnly);
    }

    /// <summary>And a row that genuinely cannot be installed still offers Open, in the same cell.</summary>
    [Fact]
    public void A_manual_only_row_shows_open()
    {
        var row = new UpdateRowViewModel(new UpdateItem
        {
            Id = "epic",
            SourceId = "epic",
            Category = UpdateCategory.Games,
            Name = "Epic Games Launcher",
            Tier = SourceTier.C,
            OpenTarget = "com.epicgames.launcher://apps",
        });

        Assert.True(row.ShowOpenButton);
        Assert.False(row.ShowInstallButton);
    }

    /// <summary>
    /// The whole cell, state by state: exactly one of Install / Open / Retry-and-state / "show again" may
    /// be visible, or the later one in the template covers the earlier.
    /// </summary>
    [Theory]
    [InlineData(RowInstallState.Idle, false)]
    [InlineData(RowInstallState.Queued, false)]
    [InlineData(RowInstallState.Running, false)]
    [InlineData(RowInstallState.Succeeded, false)]
    [InlineData(RowInstallState.Failed, false)]
    [InlineData(RowInstallState.Idle, true)]
    [InlineData(RowInstallState.Queued, true)]
    [InlineData(RowInstallState.Failed, true)]
    [InlineData(RowInstallState.NeedsManualWork, true)]
    [InlineData(RowInstallState.NeedsManualWork, false)]
    public void Only_one_action_owns_a_rows_action_cell(RowInstallState state, bool ignored)
    {
        foreach (var item in new[] { WingetItem(), NvidiaItem(), ManualItem() })
        {
            var row = new UpdateRowViewModel(item) { IsIgnored = ignored, InstallState = state };

            var visible = new[]
            {
                row.ShowInstallButton,
                row.ShowOpenButton,
                row.ShowStateLabel,
                row.ShowUnignoreButton,
            }.Count(shown => shown);

            Assert.True(
                visible <= 1,
                $"{item.SourceId} in state {state} (ignored: {ignored}) shows {visible} actions at once.");
        }
    }

    /// <summary>
    /// An ignored row stuck in NeedsManualWork used to have no way back at all, since the
    /// "show again" action only ever appeared for an ignored row that was Idle. It must show for every
    /// ignored row regardless of install state; the state word is what steps aside instead
    /// (<see cref="UpdateRowViewModel.ShowStateLabel"/> already suppresses itself for an ignored row).
    /// </summary>
    [Fact]
    public void An_ignored_row_needing_manual_work_can_still_be_shown_again()
    {
        var row = new UpdateRowViewModel(WingetItem())
        {
            InstallState = RowInstallState.NeedsManualWork,
            IsIgnored = true,
        };

        Assert.True(row.ShowUnignoreButton);
        Assert.False(row.ShowStateLabel);
    }

    private static UpdateItem NvidiaItem() => new()
    {
        Id = "nvidia-graphics-driver",
        SourceId = "nvidia-driver",
        Category = UpdateCategory.SystemAndDrivers,
        Name = "NVIDIA Graphics Driver",
        InstalledVersion = "580.88",
        AvailableVersion = "610.74",
        Tier = SourceTier.A,
        OpenTarget = "https://www.nvidia.com/en-us/drivers/details/274187/",
    };

    private static UpdateItem ManualItem() => new()
    {
        Id = "msstore",
        SourceId = "msstore",
        Category = UpdateCategory.Store,
        Name = "Microsoft Store & Xbox",
        Tier = SourceTier.C,
        OpenTarget = "ms-windows-store://downloadsandupdates",
    };

    /// <summary>The select-all state is computed from CanInstall, so the change has to be announced.</summary>
    [Fact]
    public void Starting_an_install_announces_that_the_row_is_no_longer_installable()
    {
        var row = new UpdateRowViewModel(WingetItem());
        var raised = new List<string?>();
        row.PropertyChanged += (_, args) => raised.Add(args.PropertyName);

        row.BeginInstall();

        Assert.Contains(nameof(UpdateRowViewModel.CanInstall), raised);
    }

    private sealed class RecordingRowActions : IRowActions
    {
        public int InstallCalls { get; private set; }

        public int UninstallCalls { get; private set; }

        public bool CanStart(UpdateItem item) => false;

        public bool CanUninstall(UpdateItem item) => true;

        public void Start(UpdateRowViewModel row)
        {
        }

        public void Uninstall(UpdateRowViewModel row) => UninstallCalls++;

        public void InstallSingle(UpdateRowViewModel row) => InstallCalls++;
    }

    private static UpdateItem WingetItem() => new()
    {
        Id = "Contoso.App",
        SourceId = "winget",
        Category = UpdateCategory.Software,
        Name = "Contoso App",
        InstalledVersion = "1.0",
        AvailableVersion = "1.1",
        Tier = SourceTier.A,
    };

    private static UpdateItem SteamItem(string id) => new()
    {
        Id = id,
        SourceId = "steam",
        Category = UpdateCategory.Games,
        Name = "A game",
        Tier = SourceTier.B,
    };

    /// <summary>
    /// Unlike every <c>CliUpdateSource</c>-derived source (whose base class validates the package id
    /// centrally in <c>InstallAsync</c>/<c>UninstallAsync</c> before either ever runs), VoltaSource
    /// implements <see cref="IUpdateSource"/> directly and builds its own command from
    /// <c>Meta["voltaName"]</c>, a value read straight out of `volta list`'s own output
    /// (<c>VoltaParsers.ParseDefaultTools</c>'s name group is a bare <c>\S+</c>, so it accepts a value
    /// starting with '-'). Both entry points must reject that value themselves; nothing upstream does it
    /// for them. Silent no-op progress reporter, only whether the call throws/what it argues matters here.
    /// </summary>
    private sealed class NoOpProgress : IProgress<InstallProgress>
    {
        public void Report(InstallProgress value) { }
    }

    [Theory]
    [InlineData("--registry=https://evil.example")]
    [InlineData("-rf")]
    public async Task Volta_install_refuses_a_tool_name_that_would_be_read_as_an_option(string hostileName)
    {
        var source = new VoltaSource(new LoggingService(), new HttpClient());
        var item = new UpdateItem
        {
            Id = $"volta-{hostileName}",
            SourceId = "volta",
            Category = UpdateCategory.DeveloperTools,
            Name = "Sample Package",
            AvailableVersion = "1.0.0",
            Meta = new Dictionary<string, string> { ["voltaName"] = hostileName },
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.InstallAsync(item, new NoOpProgress(), CancellationToken.None));
    }

    [Theory]
    [InlineData("--registry=https://evil.example")]
    [InlineData("-rf")]
    public async Task Volta_uninstall_refuses_a_tool_name_that_would_be_read_as_an_option(string hostileName)
    {
        var source = new VoltaSource(new LoggingService(), new HttpClient());
        var item = new UpdateItem
        {
            Id = $"volta-{hostileName}",
            SourceId = "volta",
            Category = UpdateCategory.DeveloperTools,
            Name = "Sample Package",
            InstalledVersion = "1.0.0",
            Meta = new Dictionary<string, string> { ["voltaName"] = hostileName },
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.UninstallAsync(item, new NoOpProgress(), CancellationToken.None));
    }
}
