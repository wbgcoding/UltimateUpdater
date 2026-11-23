using System.Globalization;
using UltimateUpdater.Services.Processes;
using UltimateUpdater.Services.Uninstall;

namespace UltimateUpdater.Tests;

/// <summary>
/// Installed Windows updates in the uninstaller: DISM's package table and package info in English and German, which
/// packages are listed and which may be removed, the join with the hotfix list, the plan, the gone check and the
/// elevated worker's own checks. DISM is faked: nothing is listed or removed on the machine running the tests.
/// </summary>
public class UninstallWindowsUpdateTests
{
    private const string Cbs = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\Packages";
    private const string Kb = "Package_for_KB5043080~31bf3856ad364e35~amd64~~26100.1.1.0";
    private const string Kb2 = "Package_for_KB5050000~31bf3856ad364e35~amd64~~26100.1.1.0";
    private const string Rollup = "Package_for_RollupFix~31bf3856ad364e35~amd64~~26100.4061.1.9";
    private const string Stack = "Package_for_ServicingStack_4061~31bf3856ad364e35~amd64~~26100.4061.1.2";

    private const string EnglishTable = """
        Deployment Image Servicing and Management tool
        Version: 10.0.26100.1150

        Image Version: 10.0.26100.4061

        Packages listing:

        ----------------------------------------------- | ------------- | --------------- | ------------------
        Package Identity                                | State         | Release Type    | Install Time
        ----------------------------------------------- | ------------- | --------------- | ------------------
        Package_for_KB5043080~31bf3856ad364e35~amd64~~26100.1.1.0 | Installed | Update | 9/12/2024 3:14 PM
        Package_for_KB5050000~31bf3856ad364e35~amd64~~26100.1.1.0 | Installed | Security Update | 1/3/2025 8:01 AM
        Package_for_RollupFix~31bf3856ad364e35~amd64~~26100.4061.1.9 | Installed | Security Update | 5/14/2025 8:01 AM
        Package_for_ServicingStack_4061~31bf3856ad364e35~amd64~~26100.4061.1.2 | Installed | Update | 5/14/2025 8:01 AM
        Microsoft-Windows-Client-LanguagePack-Package~31bf3856ad364e35~amd64~de-DE~10.0.26100.1 | Installed | Language Pack | 1/1/2025 1:00 AM
        Package_for_KB5011111~31bf3856ad364e35~amd64~~26100.1.1.0 | Superseded | Update | 2/2/2025 1:00 AM
        Microsoft-Windows-FodMetadata-Package~31bf3856ad364e35~amd64~~10.0.26100.1 | Installed | Feature Pack | 1/1/2025 1:00 AM

        The operation completed successfully.
        """;

    private const string GermanTable = """
        Tool zur Imageverwaltung für die Bereitstellung
        Version: 10.0.26100.1150

        Paketliste:

        ----------------------------------------------- | ------------- | --------------- | ------------------
        Paketidentität                                  | Status        | Releasetyp      | Installationszeit
        ----------------------------------------------- | ------------- | --------------- | ------------------
        Package_for_KB5043080~31bf3856ad364e35~amd64~~26100.1.1.0 | Installiert | Update | 12.09.2024 15:14
        Package_for_KB5011111~31bf3856ad364e35~amd64~~26100.1.1.0 | Ersetzt | Update | 02.02.2025 01:00

        Der Vorgang wurde erfolgreich beendet.
        """;

    private static CultureScope English() => new("en-US");

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _before = CultureInfo.CurrentCulture;

        public CultureScope(string name) => CultureInfo.CurrentCulture = new CultureInfo(name);

        public void Dispose() => CultureInfo.CurrentCulture = _before;
    }

    private sealed class NoFiles : IUninstallFileProbe
    {
        public bool FileExists(string path) => false;

        public byte[]? ReadHead(string path, int count) => null;

        public bool IsTrustedForElevation(string path) => true;
    }

    private sealed class AllowAllKeys : IUninstallKeyGuard
    {
        public bool IsAdminOnly(string keyPath) => true;
    }

    // ---- the DISM table ----

    [Fact]
    public void The_english_table_gives_identity_state_release_type_and_time_of_every_package_row()
    {
        using var _ = English();

        var packages = WindowsUpdateDiscovery.ParsePackages(EnglishTable);

        Assert.Equal(7, packages.Count);
        var first = packages[0];
        Assert.Equal(Kb, first.Identity);
        Assert.Equal("Installed", first.State);
        Assert.Equal("Update", first.ReleaseType);
        Assert.Equal(new DateTime(2024, 9, 12, 15, 14, 0), first.InstallTime);
        Assert.Equal("Security Update", packages[1].ReleaseType);
    }

    [Fact]
    public void The_german_table_is_read_by_its_shape_not_its_headers()
    {
        using var _ = new CultureScope("de-DE");

        var packages = WindowsUpdateDiscovery.ParsePackages(GermanTable);

        Assert.Equal(2, packages.Count);
        Assert.Equal("Installiert", packages[0].State);
        Assert.Equal("Update", packages[0].ReleaseType);
        Assert.Equal(new DateTime(2024, 9, 12, 15, 14, 0), packages[0].InstallTime);
        Assert.True(WindowsUpdateDiscovery.IsInstalledState(packages[0].State));
        Assert.False(WindowsUpdateDiscovery.IsInstalledState(packages[1].State));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Error: 740")]
    [InlineData("a | b | c")]
    public void Output_that_is_not_a_table_gives_no_rows(string output)
    {
        Assert.Empty(WindowsUpdateDiscovery.ParsePackages(output));
        Assert.Empty(WindowsUpdateDiscovery.ParsePackages(null));
    }

    [Fact]
    public void A_row_with_an_unsafe_identity_is_not_read()
    {
        var rows = WindowsUpdateDiscovery.ParsePackages(
            "Package_for_KB5043080~x\" /y | Installed | Update | 9/12/2024 3:14 PM\r\n"
            + "Package_for_KB5043080~x;calc | Installed | Update | 9/12/2024 3:14 PM");

        Assert.Empty(rows);
    }

    // ---- permanence ----

    [Theory]
    [InlineData("Package Identity : Package_for_KB1~x\r\nPermanence : Removable\r\n", DismPermanence.Removable)]
    [InlineData("Dauerhaftigkeit : x\r\nPermanence : Permanent\r\n", DismPermanence.NotRemovable)]
    [InlineData("Permanence : Not removable\r\n", DismPermanence.NotRemovable)]
    [InlineData("Permanenz : Entfernbar\r\n", DismPermanence.Removable)]
    [InlineData("State : Installed\r\n", DismPermanence.Unknown)]
    [InlineData("", DismPermanence.Unknown)]
    public void The_permanence_line_decides_removable_or_not(string output, DismPermanence expected) =>
        Assert.Equal(expected, WindowsUpdateDiscovery.ParsePermanence(output));

    // ---- which packages are listed and which are removable ----

    private static IReadOnlyList<WindowsUpdateRow> ClassifyEnglish(
        Func<string, DismPermanence> permanence, FakeUninstallRegistry? registry = null)
    {
        using var _ = English();
        return WindowsUpdateDiscovery.Classify(WindowsUpdateDiscovery.ParsePackages(EnglishTable), registry, permanence);
    }

    [Fact]
    public void Only_installed_updates_are_listed_and_language_packs_features_and_superseded_ones_are_not()
    {
        var rows = ClassifyEnglish(_ => DismPermanence.Removable);

        Assert.Equal([Kb, Kb2, Rollup, Stack], rows.Select(r => r.Identity));
    }

    [Fact]
    public void A_kb_update_is_removable_unless_its_info_says_permanent()
    {
        var rows = ClassifyEnglish(identity => identity == Kb2 ? DismPermanence.NotRemovable : DismPermanence.Removable);

        Assert.True(rows.Single(r => r.Identity == Kb).Removable);
        Assert.False(rows.Single(r => r.Identity == Kb2).Removable);
    }

    [Fact]
    public void The_cumulative_update_and_the_servicing_stack_are_listed_but_never_removable()
    {
        var rows = ClassifyEnglish(_ => DismPermanence.Removable);

        Assert.False(rows.Single(r => r.Identity == Rollup).Removable);
        Assert.False(rows.Single(r => r.Identity == Stack).Removable);
    }

    [Fact]
    public void A_superseded_package_counts_when_the_registry_still_says_installed()
    {
        var registry = new FakeUninstallRegistry()
            .Set(Cbs + @"\Package_for_KB5011111~31bf3856ad364e35~amd64~~26100.1.1.0", "CurrentState", "112");

        var rows = ClassifyEnglish(_ => DismPermanence.Removable, registry);

        Assert.Contains(rows, r => r.Identity.StartsWith("Package_for_KB5011111", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unknown_state_word_that_the_registry_does_not_confirm_is_not_listed()
    {
        var packages = new[] { new DismPackage(Kb, "Zorgonized", "Update", null) };

        Assert.Empty(WindowsUpdateDiscovery.Classify(packages, new FakeUninstallRegistry(), _ => DismPermanence.Removable));
    }

    // ---- the join with the hotfix list ----

    [Fact]
    public void The_hotfix_list_gives_the_kb_description_and_date_and_the_others_get_their_own_names()
    {
        var rows = ClassifyEnglish(_ => DismPermanence.Removable);
        var fixes = new[] { new QuickFix("KB5043080", "Security Update", new DateOnly(2024, 9, 14)) };

        var entries = WindowsUpdateDiscovery.ToEntries(rows, fixes, key => key);

        var kb = entries.Single(e => e.Key == "update:" + Kb);
        Assert.Equal("KB5043080 (Security Update)", kb.Name);
        Assert.Equal(new DateOnly(2024, 9, 14), kb.InstallDate);
        Assert.Equal(UninstallEntryKind.WindowsUpdate, kb.Kind);
        Assert.True(kb.WindowsAllowsRemoval);

        Assert.Equal("KB5050000", entries.Single(e => e.Key == "update:" + Kb2).Name);
        Assert.Equal("UninstallerUpdateServicingStack", entries.Single(e => e.Key == "update:" + Stack).Name);
        Assert.Equal("UninstallerUpdateCumulative", entries.Single(e => e.Key == "update:" + Rollup).Name);
        Assert.False(entries.Single(e => e.Key == "update:" + Rollup).WindowsAllowsRemoval);
    }

    [Fact]
    public void The_cumulative_update_is_named_by_its_build()
    {
        var row = new WindowsUpdateRow { Identity = Rollup, ReleaseType = "Security Update", Removable = false };

        var entry = WindowsUpdateDiscovery.ToEntries([row], [], _ => "Cumulative (build {0})").Single();

        Assert.Equal("Cumulative (build 26100.4061)", entry.Name);
    }

    [Theory]
    [InlineData("12/9/2024 3:14:00 PM", 2024, 12, 9)]
    [InlineData("2024-09-12", 2024, 9, 12)]
    [InlineData("0x01DB04A0A0A0A0A0", 2024, 11, 18)]
    public void The_hotfix_date_reads_as_a_date_or_a_file_time(string text, int year, int month, int day)
    {
        using var _ = English();

        var date = WindowsUpdateDiscovery.ParseQuickFixDate(text);

        Assert.NotNull(date);
        Assert.Equal(year, date!.Value.Year);
        if (!text.StartsWith("0x", StringComparison.Ordinal))
        {
            Assert.Equal(month, date.Value.Month);
            Assert.Equal(day, date.Value.Day);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a date")]
    public void An_unreadable_hotfix_date_is_no_date(string? text) =>
        Assert.Null(WindowsUpdateDiscovery.ParseQuickFixDate(text));

    // ---- the plan ----

    private static InstalledProgramEntry UpdateEntry(string key, bool removable = true) => new()
    {
        Key = key, Kind = UninstallEntryKind.WindowsUpdate, Name = "KB5043080", Scope = UninstallScope.Machine,
        WindowsAllowsRemoval = removable,
    };

    [Fact]
    public void A_removable_update_plans_a_quiet_dism_removal_by_the_system_path_and_needs_elevation()
    {
        var plan = UninstallPlanner.Plan(UpdateEntry("update:" + Kb), new NoFiles());

        Assert.True(plan.IsRunnable);
        Assert.True(plan.Elevated);
        Assert.Equal(UninstallMethod.WindowsUpdate, plan.Method);
        Assert.Equal(
            Path.Combine(Environment.SystemDirectory, "dism.exe"), plan.Executable);
        Assert.Equal(
            ["/online", "/remove-package", "/packagename:" + Kb, "/quiet", "/norestart"], plan.Arguments);
    }

    [Fact]
    public void An_update_windows_does_not_allow_removing_is_refused_with_its_own_reason()
    {
        var plan = UninstallPlanner.Plan(UpdateEntry("update:" + Rollup, removable: false), new NoFiles());

        Assert.False(plan.IsRunnable);
        Assert.Equal(UninstallRefusal.UpdateNotRemovable, plan.Refusal);
    }

    [Theory]
    [InlineData("update:")]
    [InlineData("update:Package_for_KB5043080~x /y")]
    [InlineData("update:Microsoft-Windows-Client-LanguagePack-Package~31bf3856ad364e35~amd64~de-DE~10.0.26100.1")]
    [InlineData("update:..\\evil")]
    [InlineData("driver:oem1.inf")]
    [InlineData("Package_for_KB5043080~31bf3856ad364e35~amd64~~26100.1.1.0")]
    public void A_key_that_is_not_an_update_wrapper_identity_is_never_planned(string key)
    {
        var plan = UninstallPlanner.Plan(UpdateEntry(key), new NoFiles());

        Assert.False(plan.IsRunnable);
    }

    [Fact]
    public void The_program_worker_does_not_take_an_update_through_its_registry_path()
    {
        var job = new UninstallProgramJob { Key = "update:" + Kb, Method = nameof(UninstallMethod.WindowsUpdate) };

        var decision = ProgramUninstallWorker.Resolve(job, new FakeUninstallRegistry(), new NoFiles(), new AllowAllKeys());

        Assert.Null(decision.Plan);
        Assert.NotNull(decision.Refusal);
    }

    // ---- the gone check ----

    private static async Task<bool> GoneAsync(FakeUninstallRegistry registry, string key) =>
        await new UninstallGoneCheck(registry, new NoStoreApps(), attempts: 1).IsGoneAsync(UpdateEntry(key), CancellationToken.None);

    private sealed class NoStoreApps : IAppxPackageSource
    {
        public Task<IReadOnlyList<InstalledProgramEntry>?> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<InstalledProgramEntry>?>([]);
    }

    [Fact]
    public async Task An_update_whose_package_key_is_gone_from_a_readable_list_counts_as_removed()
    {
        var registry = new FakeUninstallRegistry().Set(Cbs + "\\" + Kb2, "CurrentState", "112");

        Assert.True(await GoneAsync(registry, "update:" + Kb));
    }

    [Fact]
    public async Task An_update_still_installed_is_not_gone()
    {
        var registry = new FakeUninstallRegistry().Set(Cbs + "\\" + Kb, "CurrentState", "112");

        Assert.False(await GoneAsync(registry, "update:" + Kb));
    }

    [Fact]
    public async Task An_update_in_another_state_counts_as_removed()
    {
        var registry = new FakeUninstallRegistry().Set(Cbs + "\\" + Kb, "CurrentState", "64");

        Assert.True(await GoneAsync(registry, "update:" + Kb));
    }

    [Fact]
    public async Task An_unreadable_package_list_never_counts_as_removed()
    {
        Assert.False(await GoneAsync(new FakeUninstallRegistry(), "update:" + Kb));
    }

    [Fact]
    public async Task A_listed_package_with_an_unreadable_state_never_counts_as_removed()
    {
        var registry = new FakeUninstallRegistry().Set(Cbs + "\\" + Kb, "Other", "x");

        Assert.False(await GoneAsync(registry, "update:" + Kb));
    }

    // ---- the elevated worker ----

    private sealed class DismFake(string table, Func<string, string> info, int removeExit = 0)
    {
        public List<string> Calls { get; } = [];

        public Task<ProcessResult> Run(IReadOnlyList<string> arguments)
        {
            var line = string.Join(' ', arguments);
            lock (Calls)
                Calls.Add(line);

            if (line.StartsWith("/online /get-packages", StringComparison.Ordinal))
                return Task.FromResult(new ProcessResult(table.Length == 0 ? 1 : 0, table, string.Empty));

            if (line.StartsWith("/online /get-packageinfo /packagename:", StringComparison.Ordinal))
                return Task.FromResult(new ProcessResult(0, info(line["/online /get-packageinfo /packagename:".Length..]), string.Empty));

            if (line.StartsWith("/online /remove-package", StringComparison.Ordinal))
                return Task.FromResult(new ProcessResult(removeExit, "The operation completed successfully.", string.Empty));

            return Task.FromResult(new ProcessResult(1, string.Empty, "unexpected"));
        }

        public bool Removed => Calls.Any(c => c.StartsWith("/online /remove-package", StringComparison.Ordinal));
    }

    private static string Removable(string _) => "Permanence : Removable";

    private static async Task<UpdateRemovalOutcome> RemoveAsync(string? key, DismFake dism)
    {
        using var _ = English();
        WindowsUpdateWorker.DismForTest.Value = dism.Run;
        try
        {
            return await WindowsUpdateWorker.RemoveAsync(
                key, new FakeUninstallRegistry(), new NoFiles(), new LoggingService(), CancellationToken.None);
        }
        finally
        {
            WindowsUpdateWorker.DismForTest.Value = null;
        }
    }

    [Fact]
    public async Task The_worker_lists_the_package_info_only_of_installed_kb_updates()
    {
        using var _ = English();
        var dism = new DismFake(EnglishTable, Removable);
        WindowsUpdateWorker.DismForTest.Value = dism.Run;
        try
        {
            var rows = await WindowsUpdateWorker.ListAsync(new FakeUninstallRegistry(), new LoggingService(), CancellationToken.None);

            Assert.Equal([Kb, Kb2, Rollup, Stack], rows!.Select(r => r.Identity));
            var infoCalls = dism.Calls.Where(c => c.Contains("get-packageinfo", StringComparison.Ordinal)).Order().ToList();
            Assert.Equal(
                ["/online /get-packageinfo /packagename:" + Kb, "/online /get-packageinfo /packagename:" + Kb2], infoCalls);
        }
        finally
        {
            WindowsUpdateWorker.DismForTest.Value = null;
        }
    }

    [Fact]
    public async Task An_update_whose_package_info_cannot_be_read_is_listed_but_not_removable()
    {
        using var _ = English();
        var table = new DismFake(EnglishTable, Removable);
        WindowsUpdateWorker.DismForTest.Value = arguments =>
            arguments.Contains("/get-packageinfo") ? Task.FromResult(new ProcessResult(1, string.Empty, "error")) : table.Run(arguments);
        try
        {
            var rows = await WindowsUpdateWorker.ListAsync(new FakeUninstallRegistry(), new LoggingService(), CancellationToken.None);

            Assert.All(rows!.Where(r => r.Identity is Kb or Kb2), row => Assert.False(row.Removable));
        }
        finally
        {
            WindowsUpdateWorker.DismForTest.Value = null;
        }
    }

    [Fact]
    public async Task The_worker_lists_nothing_when_dism_cannot_list_the_packages()
    {
        using var _ = English();
        WindowsUpdateWorker.DismForTest.Value = new DismFake(string.Empty, Removable).Run;
        try
        {
            var rows = await WindowsUpdateWorker.ListAsync(new FakeUninstallRegistry(), new LoggingService(), CancellationToken.None);

            Assert.Null(rows);
        }
        finally
        {
            WindowsUpdateWorker.DismForTest.Value = null;
        }
    }

    [Fact]
    public async Task A_removable_update_is_removed_with_the_exact_dism_arguments_and_the_exit_code_comes_back()
    {
        var dism = new DismFake(EnglishTable, Removable, removeExit: 3010);

        var outcome = await RemoveAsync("update:" + Kb, dism);

        Assert.Null(outcome.Refusal);
        Assert.Equal(3010, outcome.ExitCode);
        Assert.Contains("/online /remove-package /packagename:" + Kb + " /quiet /norestart", dism.Calls);
    }

    [Fact]
    public async Task A_permanent_update_is_refused_and_nothing_is_removed()
    {
        var dism = new DismFake(EnglishTable, _ => "Permanence : Permanent");

        var outcome = await RemoveAsync("update:" + Kb, dism);

        Assert.NotNull(outcome.Refusal);
        Assert.Null(outcome.ExitCode);
        Assert.False(dism.Removed);
    }

    [Theory]
    [InlineData("update:" + Rollup)]
    [InlineData("update:" + Stack)]
    public async Task The_cumulative_update_and_the_servicing_stack_are_refused(string key)
    {
        var dism = new DismFake(EnglishTable, Removable);

        var outcome = await RemoveAsync(key, dism);

        Assert.NotNull(outcome.Refusal);
        Assert.False(dism.Removed);
    }

    [Fact]
    public async Task A_package_the_worker_does_not_find_installed_is_refused_even_with_a_valid_looking_identity()
    {
        var dism = new DismFake(EnglishTable, Removable);

        var outcome = await RemoveAsync("update:Package_for_KB5099999~31bf3856ad364e35~amd64~~26100.1.1.0", dism);

        Assert.NotNull(outcome.Refusal);
        Assert.False(dism.Removed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("update:")]
    [InlineData("update:Package_for_KB5043080~x /y")]
    [InlineData("driver:oem1.inf")]
    [InlineData("update:Microsoft-Windows-Client-LanguagePack-Package~31bf3856ad364e35~amd64~de-DE~10.0.26100.1")]
    public async Task A_key_that_is_no_update_identity_runs_no_dism_at_all(string? key)
    {
        var dism = new DismFake(EnglishTable, Removable);

        var outcome = await RemoveAsync(key, dism);

        Assert.NotNull(outcome.Refusal);
        Assert.Empty(dism.Calls);
    }

    [Fact]
    public async Task A_removal_that_dism_reports_as_failed_comes_back_with_its_exit_code()
    {
        var dism = new DismFake(EnglishTable, Removable, removeExit: -2146498523);

        var outcome = await RemoveAsync("update:" + Kb, dism);

        Assert.Null(outcome.Refusal);
        Assert.Equal(-2146498523, outcome.ExitCode);
    }

    [Fact]
    public async Task The_worker_refuses_when_the_package_table_cannot_be_read()
    {
        var dism = new DismFake(string.Empty, Removable);

        var outcome = await RemoveAsync("update:" + Kb, dism);

        Assert.NotNull(outcome.Refusal);
        Assert.False(dism.Removed);
    }
}
