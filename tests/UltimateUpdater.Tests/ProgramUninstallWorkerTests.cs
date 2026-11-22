using System.IO;
using UltimateUpdater.Services.Uninstall;

namespace UltimateUpdater.Tests;

/// <summary>
/// The elevated "uninstall-program" job treats its payload as a request: the key must be a machine Uninstall key,
/// the entry is re-read from the machine hive and planned again, and a plan that differs from the one asked for is
/// refused. No command text ever comes from the wire.
/// </summary>
public class ProgramUninstallWorkerTests
{
    private const string Hklm64 = FakeUninstallRegistry.Hklm64;
    private const string Hklm32 = FakeUninstallRegistry.Hklm32;
    private const string Hkcu = FakeUninstallRegistry.Hkcu;
    private const string Msi = "{12345678-ABCD-4EF0-9A1B-0123456789AB}";
    private const string Setup = @"C:\Program Files\Foo\unins000.exe";

    private sealed class Probe : IUninstallFileProbe
    {
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase) { Setup };

        public HashSet<string> Untrusted { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool FileExists(string path) => Files.Contains(path);

        public byte[]? ReadHead(string path, int count) => null;

        public bool IsTrustedForElevation(string path) => !Untrusted.Contains(path);
    }

    private static readonly IUninstallKeyGuard Keys = new FakeKeys();

    private static UninstallProgramJob Job(string? key, string? method, string? wingetId = null) =>
        new() { Key = key, Method = method, WingetId = wingetId };

    private static FakeUninstallRegistry Registry() => new FakeUninstallRegistry()
        .Program(Hklm64, "Foo", "Foo", $@"""{Setup}""")
        .Program(Hklm64, "Msi", "Msi Program", $"MsiExec.exe /X{Msi}")
        .Program(Hklm32, "Old", "Old Program", $@"""{Setup}""")
        .Program(Hkcu, "Mine", "Mine", $@"""{Setup}""");

    [Theory]
    [InlineData(Hklm64 + @"\Foo", "Foo")]
    [InlineData(Hklm32 + @"\Old", "Old")]
    [InlineData(Hklm64 + @"\{GUID-1}", "{GUID-1}")]
    [InlineData(Hklm64 + @"\Foo Bar 1.2", "Foo Bar 1.2")]
    public void A_key_directly_under_a_machine_root_gives_its_leaf(string key, string leaf)
    {
        Assert.Equal(leaf, ProgramUninstallWorker.MachineLeaf(key));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(Hkcu + @"\Mine")]
    [InlineData(Hklm64)]
    [InlineData(Hklm64 + @"\")]
    [InlineData(Hklm64 + @"\Foo\Child")]
    [InlineData(Hklm64 + @"\..\Foo")]
    [InlineData(Hklm64 + @"\Foo/Bar")]
    [InlineData(@"HKLM\SOFTWARE\Evil\Uninstall\Foo")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\UninstallX\Foo")]
    [InlineData(@"HKEY_USERS\S-1-5-21\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Foo")]
    [InlineData("appx:Foo_1.0.0.0_x64__abc")]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    public void A_key_that_is_not_a_machine_uninstall_key_has_no_leaf(string? key)
    {
        Assert.Null(ProgramUninstallWorker.MachineLeaf(key));
    }

    [Fact]
    public void A_leaf_with_a_control_character_or_over_the_key_name_limit_is_no_leaf()
    {
        Assert.Null(ProgramUninstallWorker.MachineLeaf(Hklm64 + "\\Foo\u0000Bar"));
        Assert.Null(ProgramUninstallWorker.MachineLeaf(Hklm64 + "\\Foo\nBar"));
        Assert.Null(ProgramUninstallWorker.MachineLeaf(Hklm64 + "\\" + new string('a', 256)));
        Assert.Equal(new string('a', 255), ProgramUninstallWorker.MachineLeaf(Hklm64 + "\\" + new string('a', 255)));
    }

    [Fact]
    public void A_listed_machine_program_plans_as_an_elevated_silent_msi_removal()
    {
        var decision = ProgramUninstallWorker.Resolve(Job($@"{Hklm64}\Msi", "Msi"), Registry(), new Probe(), Keys);

        Assert.Null(decision.Refusal);
        Assert.NotNull(decision.Plan);
        Assert.Equal(UninstallMethod.Msi, decision.Plan!.Method);
        Assert.True(decision.Plan.Elevated);
        Assert.Equal(["/x", Msi, "/qn", "/norestart"], decision.Plan.Arguments);
    }

    [Fact]
    public void The_key_is_matched_case_insensitively_and_the_registrys_spelling_is_used()
    {
        var decision = ProgramUninstallWorker.Resolve(Job($@"{Hklm64}\MSI", "Msi"), Registry(), new Probe(), Keys);

        Assert.NotNull(decision.Plan);
    }

    [Fact]
    public void A_silent_command_entry_resolves_from_the_registry_and_not_from_the_job()
    {
        var registry = Registry().Set($@"{Hklm64}\Foo", "QuietUninstallString", $@"""{Setup}"" /S");

        var decision = ProgramUninstallWorker.Resolve(Job($@"{Hklm64}\Foo", "QuietString"), registry, new Probe(), Keys);

        Assert.NotNull(decision.Plan);
        Assert.Equal(UninstallMethod.QuietString, decision.Plan!.Method);
        Assert.Equal(Setup, decision.Plan.Executable);
        Assert.Equal(["/S"], decision.Plan.Arguments);
    }

    [Fact]
    public void A_per_user_key_is_refused_even_when_it_exists()
    {
        var decision = ProgramUninstallWorker.Resolve(Job($@"{Hkcu}\Mine", "Interactive"), Registry(), new Probe(), Keys);

        Assert.Null(decision.Plan);
        Assert.NotNull(decision.Refusal);
    }

    [Theory]
    [InlineData(Hklm64 + @"\Unknown")]
    [InlineData(Hklm64 + @"\Foo\..\Foo")]
    [InlineData(Hklm64 + @"\Foo\Child")]
    [InlineData(@"HKLM\SOFTWARE\Evil\Foo")]
    [InlineData("")]
    [InlineData(null)]
    public void An_unknown_or_malformed_key_is_refused(string? key)
    {
        var decision = ProgramUninstallWorker.Resolve(Job(key, "Interactive"), Registry(), new Probe(), Keys);

        Assert.Null(decision.Plan);
        Assert.NotNull(decision.Refusal);
    }

    [Fact]
    public void A_missing_job_is_refused()
    {
        var decision = ProgramUninstallWorker.Resolve(null, Registry(), new Probe(), Keys);

        Assert.Null(decision.Plan);
    }

    [Theory]
    [InlineData("Refused")]
    [InlineData("StoreApp")]
    [InlineData("refused")]
    [InlineData("msi")]
    [InlineData("42")]
    [InlineData("Msi,Interactive")]
    [InlineData("")]
    [InlineData(null)]
    public void A_method_the_worker_does_not_run_is_refused(string? method)
    {
        var decision = ProgramUninstallWorker.Resolve(Job($@"{Hklm64}\Msi", method), Registry(), new Probe(), Keys);

        Assert.Null(decision.Plan);
        Assert.NotNull(decision.Refusal);
    }

    [Fact]
    public void A_method_that_differs_from_what_the_entry_plans_is_refused()
    {
        // The entry is an MSI program; asking for its (nonexistent) quiet command or a winget run must not work.
        var asQuiet = ProgramUninstallWorker.Resolve(Job($@"{Hklm64}\Msi", "QuietString"), Registry(), new Probe(), Keys);
        var asInteractive = ProgramUninstallWorker.Resolve(Job($@"{Hklm64}\Msi", "Interactive"), Registry(), new Probe(), Keys);

        Assert.Null(asQuiet.Plan);
        Assert.Null(asInteractive.Plan);
    }

    private static FakeUninstallRegistry WingetRegistry() => Registry().Set($@"{Hklm64}\Foo", "DisplayVersion", "1.0");

    private static readonly WingetListRow[] FooRow = [new("Foo", "Foo.Editor", "1.0")];

    [Fact]
    public void A_winget_job_plans_a_machine_scoped_winget_removal_with_the_id_the_worker_derived()
    {
        var ok = ProgramUninstallWorker.Resolve(Job($@"{Hklm64}\Foo", "Winget", "Foo.Editor"), WingetRegistry(), new Probe(), Keys, FooRow);

        Assert.NotNull(ok.Plan);
        Assert.Equal("winget", ok.Plan!.Executable);
        Assert.Contains("Foo.Editor", ok.Plan.Arguments);
        Assert.True(ok.Plan.Elevated);
        Assert.Equal(["--scope", "machine"], ok.Plan.Arguments.Skip(ok.Plan.Arguments.Count - 2));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("Other.Package")]
    [InlineData("-evil")]
    [InlineData("a b")]
    public void A_winget_job_whose_id_is_not_the_one_the_worker_derives_is_refused(string? id)
    {
        var decision = ProgramUninstallWorker.Resolve(Job($@"{Hklm64}\Foo", "Winget", id), WingetRegistry(), new Probe(), Keys, FooRow);

        Assert.Null(decision.Plan);
        Assert.NotNull(decision.Refusal);
    }

    [Fact]
    public void A_winget_job_is_refused_when_the_worker_finds_no_id_of_its_own()
    {
        var withoutRows = ProgramUninstallWorker.Resolve(Job($@"{Hklm64}\Foo", "Winget", "Foo.Editor"), WingetRegistry(), new Probe(), Keys);
        var otherRows = ProgramUninstallWorker.Resolve(
            Job($@"{Hklm64}\Foo", "Winget", "Foo.Editor"), WingetRegistry(), new Probe(), Keys, [new("Foo", "Foo.Editor", "2.0")]);

        Assert.Null(withoutRows.Plan);
        Assert.Null(otherRows.Plan);
    }

    [Fact]
    public void A_winget_id_for_an_entry_that_is_not_installed_is_refused()
    {
        var decision = ProgramUninstallWorker.Resolve(Job($@"{Hklm64}\Gone", "Winget", "Foo.Editor"), WingetRegistry(), new Probe(), Keys, FooRow);

        Assert.Null(decision.Plan);
    }

    private sealed class FakeKeys(params string[] writableByUsers) : IUninstallKeyGuard
    {
        public bool IsAdminOnly(string keyPath) => !writableByUsers.Contains(keyPath, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_key_users_can_write_is_refused_before_its_content_is_used()
    {
        var registry = Registry().Set($@"{Hklm64}\Foo", "QuietUninstallString", $@"""{Setup}"" /S");

        var writable = ProgramUninstallWorker.Resolve(
            Job($@"{Hklm64}\Foo", "QuietString"), registry, new Probe(), new FakeKeys($@"{Hklm64}\Foo"));
        var adminOnly = ProgramUninstallWorker.Resolve(Job($@"{Hklm64}\Foo", "QuietString"), registry, new Probe(), new FakeKeys());

        Assert.Null(writable.Plan);
        Assert.Contains("users", writable.Refusal);
        Assert.NotNull(adminOnly.Plan);
    }

    [Fact]
    public void A_system_tool_named_by_a_machine_entry_is_refused()
    {
        var net = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "net.exe");
        var probe = new Probe();
        probe.Files.Add(net);
        var registry = Registry().Set($@"{Hklm64}\Foo", "QuietUninstallString", $@"""{net}"" localgroup administrators x /add");

        var decision = ProgramUninstallWorker.Resolve(Job($@"{Hklm64}\Foo", "QuietString"), registry, probe, Keys);

        Assert.Null(decision.Plan);
        Assert.NotNull(decision.Refusal);
    }

    [Fact]
    public void The_apps_own_entry_is_refused()
    {
        var registry = Registry().Program(Hklm64, UninstallRiskClassifier.OwnUninstallKeyName, "Ultimate Updater", $@"""{Setup}""");

        var decision = ProgramUninstallWorker.Resolve(
            Job($@"{Hklm64}\{UninstallRiskClassifier.OwnUninstallKeyName}", "Interactive"), registry, new Probe(), Keys);

        Assert.Null(decision.Plan);
    }

    [Fact]
    public void An_uninstaller_in_a_folder_users_can_write_is_refused_for_the_elevated_run()
    {
        var probe = new Probe();
        probe.Untrusted.Add(Setup);
        var registry = Registry().Set($@"{Hklm64}\Foo", "QuietUninstallString", $@"""{Setup}"" /S");

        var decision = ProgramUninstallWorker.Resolve(Job($@"{Hklm64}\Foo", "QuietString"), registry, probe, Keys);

        Assert.Null(decision.Plan);
        Assert.NotNull(decision.Refusal);
    }

    [Fact]
    public void An_entry_that_is_not_a_removable_program_is_refused()
    {
        var registry = Registry()
            .Set($@"{Hklm64}\Patch", "DisplayName", "Patch")
            .Set($@"{Hklm64}\Patch", "UninstallString", $@"""{Setup}""")
            .Set($@"{Hklm64}\Patch", "ParentKeyName", "OperatingSystem");

        var decision = ProgramUninstallWorker.Resolve(Job($@"{Hklm64}\Patch", "Interactive"), registry, new Probe(), Keys);

        Assert.Null(decision.Plan);
    }

    [Fact]
    public void An_uninstall_job_is_a_recognized_kind_and_always_exclusive()
    {
        Assert.True(ElevatedWorker.IsRecognizedKind("uninstall-program"));
        Assert.False(ElevatedWorker.IsRecognizedKind("uninstall"));
    }
}
