using System.IO;
using System.Text;
using UltimateUpdater.Services.Uninstall;

namespace UltimateUpdater.Tests;

public class UninstallPlannerTests
{
    private const string Msi = "{12345678-ABCD-4EF0-9A1B-0123456789AB}";
    private const string Setup = @"C:\Program Files\Foo\unins000.exe";

    /// <summary>A file system made of the files a test lists. Everything listed is admin-owned unless marked.</summary>
    private sealed class FakeProbe : IUninstallFileProbe
    {
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, byte[]> Contents { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Untrusted { get; } = new(StringComparer.OrdinalIgnoreCase);

        public FakeProbe With(params string[] files)
        {
            foreach (var file in files)
                Files.Add(file);
            return this;
        }

        public bool FileExists(string path) => Files.Contains(path);

        public byte[]? ReadHead(string path, int count) =>
            Contents.TryGetValue(path, out var bytes) ? bytes.Take(count).ToArray() : null;

        public bool IsTrustedForElevation(string path) => !Untrusted.Contains(path);
    }

    private static InstalledProgramEntry Entry(
        string uninstall = "", string quiet = "", UninstallScope scope = UninstallScope.Machine,
        string? msi = null, string? winget = null) =>
        new()
        {
            Key = @"HKLM\Uninstall\Foo", Name = "Foo", Scope = scope, UninstallString = uninstall,
            QuietUninstallString = quiet, MsiProductCode = msi, WingetId = winget,
        };

    private static UninstallPlan Plan(InstalledProgramEntry entry, FakeProbe? probe = null) =>
        UninstallPlanner.Plan(entry, probe ?? new FakeProbe().With(Setup));

    [Fact]
    public void A_winget_id_wins_over_everything_else()
    {
        var plan = Plan(Entry(uninstall: $@"""{Setup}""", quiet: $@"""{Setup}"" /S", msi: Msi, winget: "Foo.Editor"));

        Assert.Equal(UninstallMethod.Winget, plan.Method);
        Assert.Equal("winget", plan.Executable);
        Assert.Equal(["uninstall", "--id", "Foo.Editor", "--exact", "--silent", "--disable-interactivity", "--accept-source-agreements"],
            plan.Arguments);
        Assert.True(plan.Elevated);
        Assert.False(plan.Interactive);
        Assert.True(plan.IsRunnable);
    }

    [Theory]
    [InlineData("-evil")]
    [InlineData("/evil")]
    [InlineData("a b")]
    [InlineData("a..b")]
    [InlineData("a\"b")]
    public void An_unsafe_winget_id_is_refused_and_not_replaced_by_another_method(string id)
    {
        var plan = Plan(Entry(uninstall: $@"""{Setup}""", winget: id));

        Assert.Equal(UninstallMethod.Refused, plan.Method);
        Assert.Equal(UninstallRefusal.InvalidId, plan.Refusal);
        Assert.False(plan.IsRunnable);
    }

    [Fact]
    public void The_msi_product_code_wins_over_the_quiet_and_normal_strings()
    {
        var plan = Plan(Entry(uninstall: $@"""{Setup}""", quiet: $@"""{Setup}"" /S", msi: Msi));

        Assert.Equal(UninstallMethod.Msi, plan.Method);
        Assert.Equal(["/x", Msi, "/qn", "/norestart"], plan.Arguments);
        Assert.Equal("msiexec.exe", Path.GetFileName(plan.Executable));
        Assert.Equal(Environment.SystemDirectory, Path.GetDirectoryName(plan.Executable));
        Assert.True(plan.Elevated);
    }

    [Fact]
    public void The_quiet_string_runs_with_its_own_arguments_when_the_program_sits_in_an_admin_folder()
    {
        var plan = Plan(Entry(uninstall: $@"""{Setup}""", quiet: $@"""{Setup}"" /SILENT /LOG=""C:\Program Files\Foo\x y.log"""));

        Assert.Equal(UninstallMethod.QuietString, plan.Method);
        Assert.Equal(Setup, plan.Executable);
        Assert.Equal(["/SILENT", @"/LOG=C:\Program Files\Foo\x y.log"], plan.Arguments);
        Assert.True(plan.Elevated);
        Assert.False(plan.Interactive);
    }

    [Fact]
    public void A_program_outside_an_admin_only_folder_is_refused_for_a_machine_entry()
    {
        var probe = new FakeProbe().With(Setup);
        probe.Untrusted.Add(Setup);

        foreach (var entry in new[] { Entry(quiet: $@"""{Setup}"" /S"), Entry(uninstall: $@"""{Setup}""") })
        {
            var plan = Plan(entry, probe);
            Assert.Equal(UninstallMethod.Refused, plan.Method);
            Assert.Equal(UninstallRefusal.UntrustedLocation, plan.Refusal);
        }
    }

    [Fact]
    public void A_user_entry_never_runs_elevated_whatever_its_location()
    {
        const string userSetup = @"C:\Users\Someone\AppData\Local\Foo\uninstall.exe";
        var probe = new FakeProbe().With(userSetup);
        probe.Untrusted.Add(userSetup);

        var quiet = Plan(Entry(quiet: $@"""{userSetup}"" /S", scope: UninstallScope.User), probe);
        var normal = Plan(Entry(uninstall: $@"""{userSetup}""", scope: UninstallScope.User), probe);
        var msi = Plan(Entry(msi: Msi, scope: UninstallScope.User), probe);
        var winget = Plan(Entry(winget: "Foo.Editor", scope: UninstallScope.User), probe);

        foreach (var plan in new[] { quiet, normal, msi, winget })
        {
            Assert.True(plan.IsRunnable);
            Assert.False(plan.Elevated);
        }
    }

    [Fact]
    public void An_inno_uninstaller_gets_its_silent_switches_only_with_the_dat_file_beside_it()
    {
        var with = Plan(Entry(uninstall: $@"""{Setup}"""), new FakeProbe().With(Setup, @"C:\Program Files\Foo\unins000.dat"));
        var without = Plan(Entry(uninstall: $@"""{Setup}"""), new FakeProbe().With(Setup));

        Assert.Equal(UninstallMethod.SilentSwitch, with.Method);
        Assert.Equal(["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART"], with.Arguments);
        Assert.False(with.Interactive);

        Assert.Equal(UninstallMethod.Interactive, without.Method);
        Assert.Empty(without.Arguments);
        Assert.True(without.Interactive);
    }

    [Fact]
    public void The_inno_name_must_be_unins_and_three_digits()
    {
        const string other = @"C:\Program Files\Foo\uninstall.exe";
        var probe = new FakeProbe().With(other, @"C:\Program Files\Foo\uninstall.dat");

        Assert.Equal(UninstallMethod.Interactive, Plan(Entry(uninstall: $@"""{other}"""), probe).Method);

        const string two = @"C:\Program Files\Foo\unins00.exe";
        probe.With(two, @"C:\Program Files\Foo\unins00.dat");
        Assert.Equal(UninstallMethod.Interactive, Plan(Entry(uninstall: $@"""{two}"""), probe).Method);
    }

    [Fact]
    public void An_nsis_uninstaller_is_recognised_by_its_marker_in_the_first_megabyte()
    {
        const string nsis = @"C:\Program Files\Bar\Uninstall.exe";
        var probe = new FakeProbe().With(nsis);
        var head = new byte[2048];
        Encoding.ASCII.GetBytes("Nullsoft Install System").CopyTo(head, 1000);
        probe.Contents[nsis] = head;

        var plan = Plan(Entry(uninstall: $@"""{nsis}"""), probe);

        Assert.Equal(UninstallMethod.SilentSwitch, plan.Method);
        Assert.Equal(["/S"], plan.Arguments);
    }

    [Fact]
    public void An_nsis_marker_past_the_first_megabyte_is_not_seen()
    {
        const string late = @"C:\Program Files\Bar\Uninstall.exe";
        var probe = new FakeProbe().With(late);
        var bytes = new byte[1024 * 1024 + 100];
        Encoding.ASCII.GetBytes("Nullsoft").CopyTo(bytes, 1024 * 1024 + 10);
        probe.Contents[late] = bytes;

        Assert.Equal(UninstallMethod.Interactive, Plan(Entry(uninstall: $@"""{late}"""), probe).Method);
    }

    [Fact]
    public void An_unrecognised_uninstaller_with_arguments_runs_as_written_and_interactive()
    {
        var probe = new FakeProbe().With(@"C:\Program Files\Baz\remove.exe");

        var plan = Plan(Entry(uninstall: @"""C:\Program Files\Baz\remove.exe"" --uninstall"), probe);

        Assert.Equal(UninstallMethod.Interactive, plan.Method);
        Assert.Equal(["--uninstall"], plan.Arguments);
        Assert.True(plan.Interactive);
    }

    [Fact]
    public void A_recognised_installer_with_its_own_arguments_gets_no_guessed_switch()
    {
        var probe = new FakeProbe().With(Setup, @"C:\Program Files\Foo\unins000.dat");

        var plan = Plan(Entry(uninstall: $@"""{Setup}"" /something"), probe);

        Assert.Equal(UninstallMethod.Interactive, plan.Method);
        Assert.Equal(["/something"], plan.Arguments);
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\cmd.exe /c del x")]
    [InlineData(@"""C:\Windows\System32\cmd.exe"" /c del x")]
    [InlineData(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe -File x.ps1")]
    [InlineData(@"C:\Program Files\PowerShell\7\pwsh.exe -c x")]
    [InlineData(@"C:\Windows\System32\rundll32.exe setupapi.dll,InstallHinfSection x")]
    [InlineData(@"C:\Windows\System32\wscript.exe x.vbs")]
    [InlineData(@"C:\Windows\System32\cscript.exe x.vbs")]
    [InlineData(@"C:\Windows\System32\mshta.exe x.hta")]
    [InlineData(@"C:\Windows\System32\regsvr32.exe /u foo")]
    [InlineData(@"C:\Windows\System32\wbem\wmic.exe product call uninstall")]
    [InlineData(@"C:\Windows\System32\msiexec.exe /x something-without-a-code")]
    [InlineData(@"C:\Python312\python.exe x.py")]
    [InlineData(@"C:\Program Files\nodejs\node.exe x.js")]
    [InlineData(@"C:\Windows\System32\bash.exe -c x")]
    [InlineData(@"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\InstallUtil.exe /u foo")]
    public void A_script_host_or_interpreter_as_the_uninstaller_is_refused(string command)
    {
        var probe = new FakeProbe();
        var path = UninstallCommandLine.Parse(command, _ => true, out _)?.Executable;
        Assert.Null(path);

        foreach (var entry in new[] { Entry(uninstall: command), Entry(quiet: command) })
        {
            var plan = Plan(entry, probe.With(Setup));
            Assert.Equal(UninstallMethod.Refused, plan.Method);
            Assert.Equal(UninstallRefusal.ScriptHost, plan.Refusal);
        }
    }

    [Theory]
    [InlineData(@"""C:\Program Files\Foo\setup.bat""", UninstallRefusal.NotAnExecutable)]
    [InlineData(@"""C:\Program Files\Foo\setup.msi""", UninstallRefusal.NotAnExecutable)]
    [InlineData(@"""C:\Program Files\Foo\setup.lnk""", UninstallRefusal.NotAnExecutable)]
    [InlineData(@"""C:\Program Files\Foo\setup""", UninstallRefusal.NotAnExecutable)]
    [InlineData(@"""setup.exe"" /S", UninstallRefusal.Unparseable)]
    [InlineData(@"""..\setup.exe"" /S", UninstallRefusal.Unparseable)]
    [InlineData(@"""C:\Program Files\..\Windows\setup.exe""", UninstallRefusal.Unparseable)]
    [InlineData(@"""%ProgramFiles%\Foo\unins000.exe""", UninstallRefusal.Unparseable)]
    [InlineData(@"""\\server\share\unins000.exe""", UninstallRefusal.Unparseable)]
    [InlineData(@"""\\?\C:\Program Files\Foo\unins000.exe""", UninstallRefusal.Unparseable)]
    [InlineData(@"""C:\Program Files\Foo\unins000.exe:stream.exe""", UninstallRefusal.Unparseable)]
    [InlineData(@"""C:\Program Files\Foo\unins000.exe"" /S ""unterminated", UninstallRefusal.Unparseable)]
    [InlineData(@"""C:\Program Files\Foo\unins000.exe""/S", UninstallRefusal.Unparseable)]
    [InlineData("\"C:\\Program Files\\Foo\\unins000.exe\" /S\r\ncalc", UninstallRefusal.Unparseable)]
    [InlineData(@"""C:\Program Files\Foo\unins000.exe", UninstallRefusal.Unparseable)]
    [InlineData("", UninstallRefusal.NoCommand)]
    [InlineData("   ", UninstallRefusal.NoCommand)]
    public void A_command_that_is_not_one_plain_program_path_is_refused(string command, UninstallRefusal expected)
    {
        var plan = Plan(Entry(uninstall: command, scope: UninstallScope.User), new FakeProbe().With(Setup));

        Assert.Equal(UninstallMethod.Refused, plan.Method);
        Assert.Equal(expected, plan.Refusal);
    }

    [Fact]
    public void A_program_that_does_not_exist_is_refused()
    {
        var plan = Plan(Entry(uninstall: @"""C:\Program Files\Gone\unins000.exe"""), new FakeProbe());

        Assert.Equal(UninstallRefusal.MissingFile, plan.Refusal);
    }

    [Fact]
    public void An_unquoted_path_with_spaces_is_split_by_the_file_that_exists()
    {
        var parsed = UninstallCommandLine.Parse(
            @"C:\Program Files\Foo Bar\unins000.exe /SILENT", path => path == @"C:\Program Files\Foo Bar\unins000.exe",
            out var refusal)!;

        Assert.Equal(UninstallRefusal.None, refusal);
        Assert.Equal(@"C:\Program Files\Foo Bar\unins000.exe", parsed.Executable);
        Assert.Equal(["/SILENT"], parsed.Arguments);
    }

    [Fact]
    public void An_unquoted_command_whose_extracted_path_is_not_its_prefix_is_refused()
    {
        // An icon index glued to the path would shift the arguments; fail closed instead of guessing.
        Assert.Null(UninstallCommandLine.Parse(@"C:\Foo\unins000.exe,0", _ => true, out var refusal));
        Assert.Equal(UninstallRefusal.Unparseable, refusal);
    }

    [Theory]
    [InlineData("", new string[0])]
    [InlineData("/S", new[] { "/S" })]
    [InlineData("  /S   /D=C:\\x  ", new[] { "/S", "/D=C:\\x" })]
    [InlineData("\"a b\" c", new[] { "a b", "c" })]
    [InlineData("/D=\"C:\\Program Files\\x\"", new[] { "/D=C:\\Program Files\\x" })]
    [InlineData("a\\\\\\\"b", new[] { "a\\\"b" })]
    [InlineData("\"a\\\\\" b", new[] { "a\\", "b" })]
    [InlineData("\"\" x", new[] { "", "x" })]
    [InlineData("C:\\dir\\ x", new[] { "C:\\dir\\", "x" })]
    public void Arguments_are_split_like_CommandLineToArgvW(string text, string[] expected)
    {
        Assert.Equal(expected, UninstallCommandLine.SplitArguments(text));
    }

    [Fact]
    public void An_unterminated_quote_in_the_arguments_gives_no_split()
    {
        Assert.Null(UninstallCommandLine.SplitArguments("/D=\"C:\\x"));
    }

    [Fact]
    public void A_store_app_is_removed_with_a_package_name_passed_in_the_environment()
    {
        const string fullName = "Contoso.Notes_2.1.0.0_x64__8wekyb3d8bbwe";
        var entry = new InstalledProgramEntry
        {
            Key = "appx:" + fullName, Kind = UninstallEntryKind.StoreApp, Name = "Contoso.Notes", PackageFullName = fullName,
            Scope = UninstallScope.User,
        };

        var plan = Plan(entry);

        Assert.Equal(UninstallMethod.StoreApp, plan.Method);
        Assert.Equal("powershell", plan.Executable);
        Assert.False(plan.Elevated);
        Assert.Equal(fullName, plan.Environment![UninstallPlanner.AppxPackageVariable]);
        Assert.DoesNotContain(fullName, string.Join(' ', plan.Arguments));
        Assert.Contains("Remove-AppxPackage", plan.Arguments[^1]);
    }

    [Theory]
    [InlineData("Contoso.Notes_2.1.0.0_x64__8wekyb3d8bbwe; calc")]
    [InlineData("Contoso.Notes_2.1.0.0_x64__8wekyb3d8bbwe\" -Force")]
    [InlineData("*")]
    [InlineData("")]
    [InlineData("Contoso.Notes")]
    [InlineData("$(calc)_1.0.0.0_x64__8wekyb3d8bbwe")]
    public void A_store_package_name_that_is_not_a_package_full_name_is_refused(string fullName)
    {
        var entry = new InstalledProgramEntry
        {
            Key = "appx:x", Kind = UninstallEntryKind.StoreApp, Name = "X", PackageFullName = fullName,
        };

        Assert.Equal(UninstallRefusal.InvalidId, Plan(entry).Refusal);
    }

    [Theory]
    [InlineData(UninstallEntryKind.Package)]
    [InlineData(UninstallEntryKind.Driver)]
    [InlineData(UninstallEntryKind.WindowsUpdate)]
    public void Other_kinds_have_no_registry_route(UninstallEntryKind kind)
    {
        var entry = Entry(uninstall: $@"""{Setup}""") with { Kind = kind };

        Assert.Equal(UninstallMethod.Refused, Plan(entry).Method);
    }

    [Fact]
    public void An_entry_with_no_command_at_all_is_refused()
    {
        Assert.Equal(UninstallRefusal.NoCommand, Plan(Entry()).Refusal);
    }
}
