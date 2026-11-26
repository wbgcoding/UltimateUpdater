using System.IO;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.Services.Uninstall;

namespace UltimateUpdater.Tests;

/// <summary>
/// Third-party driver packages in the uninstaller: which packages are listed, which count as in use, how the plan
/// and the elevated worker treat them. Everything runs on fabricated inventories and a fake pnputil: no real driver
/// is touched.
/// </summary>
[Collection("DriverStaging")]
public class UninstallDriverTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("wu-driveruninstall-").FullName;

    public UninstallDriverTests()
    {
        DriverBackupStaging.ProgramDataRootOverride = Path.Combine(_root, "programdata");
        Directory.CreateDirectory(DriverBackupStaging.ProgramDataRootOverride);
    }

    public void Dispose()
    {
        DriverBackupStaging.ProgramDataRootOverride = null;
        DriverBackupStaging.StagingNameOverride = null;
        DriverCleanupExecutor.PnputilForTest.Value = null;
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class FakeInventory(IReadOnlyList<DriverDeviceInfo> devices, IReadOnlyList<DriverPackageInfo> packages)
        : IDriverInventoryProvider
    {
        public IReadOnlyList<DriverDeviceInfo> GatherDevices(CancellationToken cancellationToken) => devices;

        public IReadOnlyList<DriverPackageInfo> GatherPackages(CancellationToken cancellationToken, Func<string, bool>? measure = null) =>
            packages;
    }

    private sealed class ThrowingInventory : IDriverInventoryProvider
    {
        public IReadOnlyList<DriverDeviceInfo> GatherDevices(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("device pass failed");

        public IReadOnlyList<DriverPackageInfo> GatherPackages(CancellationToken cancellationToken, Func<string, bool>? measure = null) => [];
    }

    private sealed class NoFiles : IUninstallFileProbe
    {
        public bool FileExists(string path) => false;

        public byte[]? ReadHead(string path, int count) => null;

        public bool IsTrustedForElevation(string path) => true;
    }

    private static DriverPackageInfo Package(
        string name, string provider = "Acme", string className = "Net", bool manufacturerSection = true) =>
        new(name, provider, className, "1.2.3.4", new DateTimeOffset(2024, 5, 6, 0, 0, 0, TimeSpan.Zero),
            "gadget.inf", 4096, null, manufacturerSection);

    private static DriverDeviceInfo Device(string name, string inf, bool present = true) =>
        new("PCI\\" + name, name, "Net", present, @"C:\Windows\INF\" + inf, "1.0", null, null, null);

    // ---- which packages are listed ----

    [Fact]
    public void Microsoft_and_inbox_packages_are_not_listed()
    {
        var packages = new[]
        {
            Package("oem1.inf", "Acme"),
            Package("oem2.inf", "Microsoft"),
            Package("oem3.inf", "Microsoft Corporation"),
            Package("oem4.inf", ""),
            Package("usb.inf", "Acme"),
            Package("netrasa.inf", "Acme"),
        };

        var listed = DriverUninstallDiscovery.List([], packages);

        Assert.Equal(["oem1.inf"], listed.Select(c => c.Package.PackageName));
    }

    [Fact]
    public void A_package_of_a_class_that_is_never_offered_or_with_no_device_section_is_not_listed()
    {
        var packages = new[]
        {
            Package("oem1.inf", className: "Net"),
            Package("oem2.inf", className: "Printer"),
            Package("oem3.inf", manufacturerSection: false),
        };

        var listed = DriverUninstallDiscovery.List([], packages);

        Assert.Equal(["oem1.inf"], listed.Select(c => c.Package.PackageName));
    }

    [Fact]
    public void A_package_a_present_device_uses_is_flagged_with_the_device_names()
    {
        var devices = new[]
        {
            Device("Wifi Card", "oem1.inf"),
            Device("Wifi Card Two", "OEM1.INF"),
            Device("Gone Card", "oem2.inf", present: false),
        };
        var packages = new[] { Package("oem1.inf"), Package("oem2.inf"), Package("oem3.inf") };

        var listed = DriverUninstallDiscovery.List(devices, packages).ToDictionary(c => c.Package.PackageName);

        Assert.True(listed["oem1.inf"].InUse);
        Assert.Equal(["Wifi Card", "Wifi Card Two"], listed["oem1.inf"].UsedBy);
        Assert.False(listed["oem2.inf"].InUse);
        Assert.False(listed["oem3.inf"].InUse);
    }

    [Fact]
    public void The_in_use_note_names_two_devices_and_counts_the_rest()
    {
        Assert.Equal("A", DriverUninstallDiscovery.UsedByText(["A"]));
        Assert.Equal("A, B", DriverUninstallDiscovery.UsedByText(["A", "B"]));
        Assert.Equal("A, B (+2)", DriverUninstallDiscovery.UsedByText(["A", "B", "C", "D"]));
    }

    [Fact]
    public void A_listed_package_becomes_a_driver_entry_with_its_facts()
    {
        var candidate = DriverUninstallDiscovery.List(
            [Device("Wifi Card", "oem7.inf")], [Package("oem7.inf", "Acme Corp", "Net")]).Single();

        var entry = DriverUninstallDiscovery.ToEntry(candidate);

        Assert.Equal("driver:oem7.inf", entry.Key);
        Assert.Equal(UninstallEntryKind.Driver, entry.Kind);
        Assert.Equal("Acme Corp Net (oem7.inf)", entry.Name);
        Assert.Equal("Acme Corp", entry.Publisher);
        Assert.Equal("1.2.3.4", entry.Version);
        Assert.Equal(new DateOnly(2024, 5, 6), entry.InstallDate);
        Assert.Equal(4096, entry.SizeBytes);
        Assert.Equal("Wifi Card", entry.InUseBy);
    }

    [Theory]
    [InlineData("driver:oem12.inf", "oem12.inf")]
    [InlineData("driver:OEM3.INF", "OEM3.INF")]
    [InlineData("driver:usb.inf", null)]
    [InlineData("driver:oem12.inf\\..\\x", null)]
    [InlineData("driver:C:\\Windows\\INF\\oem1.inf", null)]
    [InlineData("driver:oem1.inf /force", null)]
    [InlineData("driver:", null)]
    [InlineData("oem12.inf", null)]
    [InlineData(null, null)]
    public void Only_a_bare_oem_inf_name_is_a_driver_package_key(string? key, string? expected)
    {
        Assert.Equal(expected, DriverUninstallDiscovery.PackageNameOf(key));
    }

    // ---- the backend's list ----

    [Fact]
    public void The_backend_lists_drivers_from_the_inventory_and_lists_none_when_it_cannot_be_read()
    {
        var backend = new UninstallerBackend(new LoggingService(), elevated: null);

        var listed = backend.DriverEntries(
            new FakeInventory([Device("Wifi Card", "oem1.inf")], [Package("oem1.inf"), Package("oem2.inf")]), CancellationToken.None);
        var failed = backend.DriverEntries(new ThrowingInventory(), CancellationToken.None);

        Assert.Equal(["driver:oem1.inf", "driver:oem2.inf"], listed.Select(e => e.Key));
        Assert.Empty(failed);
    }

    // ---- the plan ----

    private static InstalledProgramEntry DriverEntry(string key = "driver:oem5.inf", string? inUseBy = null) =>
        new() { Key = key, Kind = UninstallEntryKind.Driver, Name = "Acme Net", Scope = UninstallScope.Machine, InUseBy = inUseBy };

    [Fact]
    public void An_unused_driver_plans_as_an_elevated_delete_driver_without_uninstall_or_force()
    {
        var plan = UninstallPlanner.Plan(DriverEntry(), new NoFiles());

        Assert.Equal(UninstallMethod.Driver, plan.Method);
        Assert.True(plan.Elevated);
        Assert.True(plan.IsRunnable);
        Assert.Equal(["/delete-driver", "oem5.inf"], plan.Arguments);
        Assert.Equal(
            Path.Combine(Environment.SystemDirectory, "pnputil.exe"), plan.Executable, ignoreCase: true);
        Assert.DoesNotContain("/uninstall", plan.Arguments);
        Assert.DoesNotContain("/force", plan.Arguments);
    }

    [Fact]
    public void A_driver_in_use_is_refused()
    {
        var plan = UninstallPlanner.Plan(DriverEntry(inUseBy: "Wifi Card"), new NoFiles());

        Assert.False(plan.IsRunnable);
        Assert.Equal(UninstallRefusal.DriverInUse, plan.Refusal);
    }

    [Theory]
    [InlineData("driver:usb.inf")]
    [InlineData("driver:oem1.inf /uninstall")]
    [InlineData("oem1.inf")]
    public void A_driver_with_a_malformed_key_is_refused(string key)
    {
        var plan = UninstallPlanner.Plan(DriverEntry(key), new NoFiles());

        Assert.False(plan.IsRunnable);
        Assert.Equal(UninstallRefusal.InvalidId, plan.Refusal);
    }

    // ---- the elevated worker ----

    [Fact]
    public void The_machine_program_resolver_refuses_the_driver_and_update_methods()
    {
        var job = new UninstallProgramJob { Key = "driver:oem1.inf", Method = nameof(UninstallMethod.Driver) };

        var decision = ProgramUninstallWorker.Resolve(job, new FakeUninstallRegistry(), new NoFiles(), new AllowAllKeys());

        Assert.Null(decision.Plan);
        Assert.NotNull(decision.Refusal);
    }

    private sealed class AllowAllKeys : IUninstallKeyGuard
    {
        public bool IsAdminOnly(string keyPath) => true;
    }

    private static Task<DriverRemovalOutcome> Remove(string? key, IDriverInventoryProvider inventory, Func<IReadOnlyList<string>, int> pnputil, List<string> calls)
    {
        DriverCleanupExecutor.PnputilForTest.Value = args =>
        {
            calls.Add(string.Join(' ', args));
            return Task.FromResult(pnputil(args));
        };
        return DriverUninstallWorker.RunAsync(key, inventory, new NoFiles(), new LoggingService(), CancellationToken.None);
    }

    [Fact]
    public async Task An_unused_package_is_exported_and_then_deleted()
    {
        var calls = new List<string>();

        var outcome = await Remove("driver:oem1.inf", new FakeInventory([], [Package("oem1.inf")]), _ => 0, calls);

        Assert.Null(outcome.Refusal);
        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal(2, calls.Count);
        Assert.StartsWith("/export-driver oem1.inf ", calls[0]);
        Assert.Equal("/delete-driver oem1.inf", calls[1]);
    }

    [Fact]
    public async Task A_package_a_present_device_uses_is_refused_and_pnputil_never_runs()
    {
        var calls = new List<string>();

        var outcome = await Remove(
            "driver:oem1.inf", new FakeInventory([Device("Wifi Card", "oem1.inf")], [Package("oem1.inf")]), _ => 0, calls);

        Assert.Null(outcome.ExitCode);
        Assert.NotNull(outcome.Refusal);
        Assert.Empty(calls);
    }

    [Theory]
    [InlineData("driver:oem9.inf")]
    [InlineData("driver:usb.inf")]
    [InlineData("driver:oem1.inf /force")]
    [InlineData(@"C:\Windows\INF\oem1.inf")]
    [InlineData(null)]
    public async Task A_package_the_fresh_inventory_does_not_list_or_a_bad_name_is_refused(string? key)
    {
        var calls = new List<string>();

        var outcome = await Remove(key, new FakeInventory([], [Package("oem1.inf")]), _ => 0, calls);

        Assert.Null(outcome.ExitCode);
        Assert.NotNull(outcome.Refusal);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task A_microsoft_package_is_refused_even_when_the_wire_asks_for_it()
    {
        var calls = new List<string>();

        var outcome = await Remove("driver:oem1.inf", new FakeInventory([], [Package("oem1.inf", "Microsoft")]), _ => 0, calls);

        Assert.NotNull(outcome.Refusal);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task A_package_whose_backup_fails_is_not_deleted()
    {
        var calls = new List<string>();

        var outcome = await Remove(
            "driver:oem1.inf", new FakeInventory([], [Package("oem1.inf")]), args => args[0] == "/export-driver" ? 5 : 0, calls);

        Assert.Null(outcome.ExitCode);
        Assert.NotNull(outcome.Refusal);
        Assert.DoesNotContain(calls, c => c.StartsWith("/delete-driver", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_pnputil_refusal_comes_back_as_its_exit_code()
    {
        var calls = new List<string>();

        var outcome = await Remove(
            "driver:oem1.inf", new FakeInventory([], [Package("oem1.inf")]), args => args[0] == "/delete-driver" ? 5 : 0, calls);

        Assert.Null(outcome.Refusal);
        Assert.Equal(5, outcome.ExitCode);
    }

    // ---- the gone check and the failure text ----

    private sealed class EmptyRegistry : IRegistryView
    {
        public IReadOnlyList<string> SubKeyNames(string keyPath) => [];

        public string? Value(string keyPath, string valueName) => null;
    }

    private sealed class NoApps : IAppxPackageSource
    {
        public Task<IReadOnlyList<InstalledProgramEntry>?> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<InstalledProgramEntry>?>([]);
    }

    [Fact]
    public async Task A_driver_is_gone_once_its_inf_file_is_no_longer_published()
    {
        var windows = Path.Combine(_root, "windows");
        Directory.CreateDirectory(Path.Combine(windows, "INF"));
        var inf = Path.Combine(windows, "INF", "oem5.inf");
        File.WriteAllText(inf, "x");
        var check = new UninstallGoneCheck(new EmptyRegistry(), new NoApps(), 1, TimeSpan.Zero, windows);

        Assert.False(await check.IsGoneAsync(DriverEntry(), CancellationToken.None));
        File.Delete(inf);
        Assert.True(await check.IsGoneAsync(DriverEntry(), CancellationToken.None));
        Assert.False(await check.IsGoneAsync(DriverEntry("driver:usb.inf"), CancellationToken.None));
    }

    [Fact]
    public void A_driver_pnputil_kept_is_explained_with_the_exit_code()
    {
        var runner = new UninstallRunner(
            _ => new UninstallPlan(), new NoopExecutor(), new NoopExecutor(), (_, _) => Task.FromResult(false),
            key => key == "UninstallDriverKept" ? "kept {0}" : key);
        var plan = new UninstallPlan { Method = UninstallMethod.Driver, Executable = "pnputil.exe", Elevated = true };

        Assert.Equal("kept 5", runner.FailureReason(plan, 5, "whatever pnputil said"));
    }

    private sealed class NoopExecutor : IUninstallExecutor
    {
        public Task<UninstallExecutionResult> RunAsync(InstalledProgramEntry entry, UninstallPlan plan, CancellationToken ct, CancellationToken stopWhenIdle) =>
            Task.FromResult(new UninstallExecutionResult(0));
    }
}
