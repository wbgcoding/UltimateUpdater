using System.IO;
using UltimateUpdater.Services.Uninstall;

namespace UltimateUpdater.Tests;

public class InstalledProgramInventoryTests
{
    private const string Msi = "{12345678-ABCD-4EF0-9A1B-0123456789AB}";

    private static InstalledProgramInventory Inventory(
        FakeUninstallRegistry registry, IReadOnlyList<InstalledProgramEntry>? appx = null, string? winget = null) =>
        new(registry, new FakeAppx(appx), new FakeWinget(winget));

    private sealed class FakeAppx(IReadOnlyList<InstalledProgramEntry>? packages) : IAppxPackageSource
    {
        public Task<IReadOnlyList<InstalledProgramEntry>?> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(packages);
    }

    private sealed class FakeWinget(string? text) : IWingetListSource
    {
        public Task<string?> ListAsync(CancellationToken cancellationToken) => Task.FromResult(text);
    }

    [Fact]
    public void A_plain_program_is_listed_with_all_its_fields()
    {
        var key = FakeUninstallRegistry.Hklm64 + @"\Foo";
        var registry = new FakeUninstallRegistry()
            .Program(FakeUninstallRegistry.Hklm64, "Foo", "Foo Editor", @"""C:\Program Files\Foo\unins000.exe""")
            .Set(key, "Publisher", " Foo Corp ")
            .Set(key, "DisplayVersion", "1.2.3")
            .Set(key, "InstallDate", "20240131")
            .Set(key, "EstimatedSize", "2048")
            .Set(key, "InstallLocation", @"C:\Program Files\Foo")
            .Set(key, "QuietUninstallString", @"""C:\Program Files\Foo\unins000.exe"" /SILENT")
            .Set(key, "DisplayIcon", @"C:\Program Files\Foo\foo.exe,0");

        var entry = Assert.Single(Inventory(registry).ReadRegistryPrograms());

        Assert.Equal(key, entry.Key);
        Assert.Equal("Foo Editor", entry.Name);
        Assert.Equal("Foo Corp", entry.Publisher);
        Assert.Equal("1.2.3", entry.Version);
        Assert.Equal(new DateOnly(2024, 1, 31), entry.InstallDate);
        Assert.Equal(2048L * 1024, entry.SizeBytes);
        Assert.Equal(@"C:\Program Files\Foo", entry.InstallLocation);
        Assert.Equal(UninstallScope.Machine, entry.Scope);
        Assert.Equal(UninstallEntryKind.Program, entry.Kind);
        Assert.Contains("/SILENT", entry.QuietUninstallString);
        Assert.Contains("unins000.exe", entry.UninstallString);
        Assert.Null(entry.MsiProductCode);
    }

    [Fact]
    public void A_system_component_is_skipped()
    {
        var registry = new FakeUninstallRegistry()
            .Program(FakeUninstallRegistry.Hklm64, "Hidden", "Hidden Thing")
            .Set(FakeUninstallRegistry.Hklm64 + @"\Hidden", "SystemComponent", "1")
            .Program(FakeUninstallRegistry.Hklm64, "Shown", "Shown Thing")
            .Set(FakeUninstallRegistry.Hklm64 + @"\Shown", "SystemComponent", "0");

        Assert.Equal(["Shown Thing"], Inventory(registry).ReadRegistryPrograms().Select(e => e.Name));
    }

    [Theory]
    [InlineData("ParentKeyName", "Microsoft Office")]
    [InlineData("ReleaseType", "Security Update")]
    [InlineData("ReleaseType", "Hotfix")]
    public void An_update_entry_is_skipped(string valueName, string value)
    {
        var registry = new FakeUninstallRegistry()
            .Program(FakeUninstallRegistry.Hklm64, "Patch", "Update for Something (KB123)")
            .Set(FakeUninstallRegistry.Hklm64 + @"\Patch", valueName, value);

        Assert.Empty(Inventory(registry).ReadRegistryPrograms());
    }

    [Fact]
    public void An_entry_without_a_display_name_is_skipped()
    {
        var registry = new FakeUninstallRegistry()
            .Set(FakeUninstallRegistry.Hklm64 + @"\NoName", "UninstallString", @"C:\x\unins000.exe")
            .Program(FakeUninstallRegistry.Hklm64, "Blank", "   ");

        Assert.Empty(Inventory(registry).ReadRegistryPrograms());
    }

    [Fact]
    public void An_entry_without_any_uninstall_command_is_skipped()
    {
        var registry = new FakeUninstallRegistry()
            .Set(FakeUninstallRegistry.Hklm64 + @"\NoCmd", "DisplayName", "Cannot Remove")
            .Set(FakeUninstallRegistry.Hklm64 + @"\Blank", "DisplayName", "Blank Command")
            .Set(FakeUninstallRegistry.Hklm64 + @"\Blank", "UninstallString", "  ");

        Assert.Empty(Inventory(registry).ReadRegistryPrograms());
    }

    [Fact]
    public void A_quiet_command_alone_is_enough()
    {
        var key = FakeUninstallRegistry.Hklm64 + @"\Quiet";
        var registry = new FakeUninstallRegistry()
            .Set(key, "DisplayName", "Quiet Only")
            .Set(key, "QuietUninstallString", @"C:\Program Files\Q\remove.exe /S");

        var entry = Assert.Single(Inventory(registry).ReadRegistryPrograms());
        Assert.Equal(string.Empty, entry.UninstallString);
        Assert.NotEmpty(entry.QuietUninstallString);
    }

    [Fact]
    public void Both_scopes_and_all_roots_are_read()
    {
        var registry = new FakeUninstallRegistry()
            .Program(FakeUninstallRegistry.Hklm64, "A", "Machine 64")
            .Program(FakeUninstallRegistry.Hklm32, "B", "Machine 32")
            .Program(FakeUninstallRegistry.Hkcu, "C", "User One");

        var entries = Inventory(registry).ReadRegistryPrograms();

        Assert.Equal(UninstallScope.Machine, entries.Single(e => e.Name == "Machine 64").Scope);
        Assert.Equal(UninstallScope.Machine, entries.Single(e => e.Name == "Machine 32").Scope);
        Assert.Equal(UninstallScope.User, entries.Single(e => e.Name == "User One").Scope);
        Assert.StartsWith("HKCU\\", entries.Single(e => e.Name == "User One").Key);
        Assert.Equal(3, entries.Count);
    }

    [Theory]
    [InlineData(@"MsiExec.exe /X" + Msi, null, Msi)]
    [InlineData(@"MsiExec.exe /I" + Msi, null, Msi)]
    [InlineData(@"""C:\Program Files\Foo\unins000.exe""", @"MsiExec.exe /X" + Msi + " /qn", Msi)]
    [InlineData(@"""C:\Program Files\Foo\unins000.exe""", null, null)]
    public void The_msi_product_code_comes_from_either_command(string normal, string? quiet, string? expected)
    {
        var key = FakeUninstallRegistry.Hklm64 + @"\Msi";
        var registry = new FakeUninstallRegistry()
            .Program(FakeUninstallRegistry.Hklm64, "Msi", "Msi Thing", normal);
        if (quiet is not null)
            registry.Set(key, "QuietUninstallString", quiet);

        Assert.Equal(expected, Assert.Single(Inventory(registry).ReadRegistryPrograms()).MsiProductCode);
    }

    [Theory]
    [InlineData("20240131", true)]
    [InlineData("2024-01-31", false)]
    [InlineData("20241399", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void An_install_date_is_read_only_in_the_registry_format(string? raw, bool valid)
    {
        Assert.Equal(valid, InstalledProgramInventory.ParseInstallDate(raw).HasValue);
    }

    [Fact]
    public async Task Store_apps_are_added_after_the_programs()
    {
        var app = new InstalledProgramEntry
        {
            Key = "appx:Foo_1.0.0.0_x64__abc", Kind = UninstallEntryKind.StoreApp, Name = "Foo.App",
        };
        var registry = new FakeUninstallRegistry().Program(FakeUninstallRegistry.Hklm64, "A", "Classic");

        var all = await Inventory(registry, [app]).ReadAsync(CancellationToken.None);

        Assert.Equal(["Classic", "Foo.App"], all.Select(e => e.Name));
    }

    [Fact]
    public async Task A_failed_appx_read_still_lists_the_programs()
    {
        var registry = new FakeUninstallRegistry().Program(FakeUninstallRegistry.Hklm64, "A", "Classic");

        var all = await Inventory(registry, appx: null).ReadAsync(CancellationToken.None);

        Assert.Equal(["Classic"], all.Select(e => e.Name));
    }

    [Fact]
    public async Task A_throwing_winget_reader_still_lists_the_programs()
    {
        var registry = new FakeUninstallRegistry().Program(FakeUninstallRegistry.Hklm64, "A", "Classic");
        var inventory = new InstalledProgramInventory(registry, new FakeAppx(null), new ThrowingWinget());

        Assert.Single(await inventory.ReadAsync(CancellationToken.None));
    }

    private sealed class ThrowingWinget : IWingetListSource
    {
        public Task<string?> ListAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("winget broke");
    }

    private const string WingetTable =
        "Name                 Id                    Version   Source\r\n" +
        "---------------------------------------------------------------\r\n" +
        "Foo Editor           Foo.Editor            1.2.3     winget\r\n" +
        "Bar Tool             Bar.Tool              2.0       winget\r\n" +
        "Bar Tool             Other.BarTool         2.0       winget\r\n" +
        "Same Name            Pub.SameName          1.0       winget\r\n" +
        "Legacy Thing         ARP\\Machine\\X64\\Legacy 3.1       \r\n";

    [Fact]
    public async Task A_winget_id_is_attached_only_on_an_exact_name_and_version_match()
    {
        var registry = new FakeUninstallRegistry();
        void Add(string key, string name, string version) =>
            registry.Program(FakeUninstallRegistry.Hklm64, key, name)
                .Set(FakeUninstallRegistry.Hklm64 + @"\" + key, "DisplayVersion", version);

        Add("Foo", "Foo Editor", "1.2.3");
        Add("FooOld", "Foo Editor Pro", "1.2.3");       // near name: no match
        Add("SameOld", "Same Name", "0.9");             // same name, other version: no match
        Add("Bar", "Bar Tool", "2.0");                  // two ids for one name and version: ambiguous
        Add("Legacy", "Legacy Thing", "3.1");           // an ARP id names no package

        var all = await Inventory(registry, winget: WingetTable).ReadAsync(CancellationToken.None);

        Assert.Equal("Foo.Editor", all.Single(e => e.Name == "Foo Editor").WingetId);
        Assert.Null(all.Single(e => e.Name == "Foo Editor Pro").WingetId);
        Assert.Null(all.Single(e => e.Name == "Same Name").WingetId);
        Assert.Null(all.Single(e => e.Name == "Bar Tool").WingetId);
        Assert.Null(all.Single(e => e.Name == "Legacy Thing").WingetId);
    }

    [Fact]
    public void The_winget_table_parser_survives_garbage()
    {
        Assert.Empty(WingetListParser.Parse(""));
        Assert.Empty(WingetListParser.Parse("No installed package found matching input criteria."));
        Assert.Empty(WingetListParser.Parse("\r-\r\\\r|\r\n"));
        Assert.Equal(4, WingetListParser.Parse(WingetTable).Count);
    }

    [Fact]
    public void A_truncated_winget_id_is_not_taken()
    {
        const string table =
            "Name        Id                    Version\r\n" +
            "---------------------------------------\r\n" +
            "Foo Editor  Foo.Edit…             1.2.3\r\n";

        Assert.Empty(WingetListParser.Parse(table));
    }

    private const string AppxArray = """
        [
          {"Name":"Contoso.Notes","PackageFullName":"Contoso.Notes_2.1.0.0_x64__8wekyb3d8bbwe","PackageFamilyName":"Contoso.Notes_8wekyb3d8bbwe",
           "Publisher":"CN=Contoso","Version":"2.1.0.0","IsFramework":false,"IsResourcePackage":false,"NonRemovable":false,"SignatureKind":3,
           "InstallLocation":"C:\\Program Files\\WindowsApps\\Contoso.Notes"},
          {"Name":"Microsoft.VCLibs.140.00","PackageFullName":"Microsoft.VCLibs.140.00_14.0.0.0_x64__8wekyb3d8bbwe","PackageFamilyName":"f",
           "Publisher":"CN=Microsoft","Version":"14.0.0.0","IsFramework":true,"IsResourcePackage":false,"NonRemovable":false,"SignatureKind":3},
          {"Name":"Contoso.Notes.Lang","PackageFullName":"Contoso.Notes.Lang_1.0.0.0_neutral_de_8wekyb3d8bbwe","PackageFamilyName":"f",
           "Publisher":"CN=Contoso","Version":"1.0.0.0","IsFramework":false,"IsResourcePackage":true,"NonRemovable":false,"SignatureKind":3},
          {"Name":"Windows.Shell","PackageFullName":"Windows.Shell_10.0.0.0_neutral__cw5n1h2txyewy","PackageFamilyName":"f",
           "Publisher":"CN=Microsoft Windows","Version":"10.0.0.0","IsFramework":false,"IsResourcePackage":false,"NonRemovable":true,"SignatureKind":4},
          {"Name":"Windows.Calc","PackageFullName":"Windows.Calc_1.0.0.0_x64__cw5n1h2txyewy","PackageFamilyName":"f",
           "Publisher":"CN=Microsoft Windows","Version":"1.0.0.0","IsFramework":false,"IsResourcePackage":false,"NonRemovable":false,"SignatureKind":4},
          {"Name":"Named.Kind","PackageFullName":"Named.Kind_1.0.0.0_x64__abc","PackageFamilyName":"f",
           "Publisher":"CN=X","Version":"1.0.0.0","IsFramework":false,"IsResourcePackage":false,"NonRemovable":false,"SignatureKind":"System"},
          {"Name":"Object.Version","PackageFullName":"Object.Version_3.4.5.6_x64__abc","PackageFamilyName":"f","Publisher":"CN=Y",
           "Version":{"Major":3,"Minor":4,"Build":5,"Revision":6,"MajorRevision":0,"MinorRevision":6},
           "IsFramework":false,"IsResourcePackage":false,"NonRemovable":false,"SignatureKind":1}
        ]
        """;

    [Fact]
    public void Appx_json_keeps_only_removable_user_packages()
    {
        var entries = AppxPackageReader.ParseJson(AppxArray)!;

        Assert.Equal(["Contoso.Notes", "Object.Version"], entries.Select(e => e.Name));

        var notes = entries[0];
        Assert.Equal(UninstallEntryKind.StoreApp, notes.Kind);
        Assert.Equal("appx:Contoso.Notes_2.1.0.0_x64__8wekyb3d8bbwe", notes.Key);
        Assert.Equal("Contoso.Notes_8wekyb3d8bbwe", notes.PackageFamilyName);
        Assert.Equal("2.1.0.0", notes.Version);
        Assert.Equal("Contoso", notes.Publisher);
        Assert.Equal("3.4.5.6", entries[1].Version);
    }

    [Fact]
    public void A_single_package_arrives_as_a_bare_object()
    {
        var one = """{"Name":"Solo.App","PackageFullName":"Solo.App_1.5.0.0_x64__abc","Version":"1.5.0.0","SignatureKind":3}""";

        Assert.Equal("Solo.App", Assert.Single(AppxPackageReader.ParseJson(one)!).Name);
    }

    [Fact]
    public void A_missing_version_is_taken_from_the_full_name()
    {
        var one = """{"Name":"Solo.App","PackageFullName":"Solo.App_1.5.0.0_x64__abc","SignatureKind":3}""";

        Assert.Equal("1.5.0.0", AppxPackageReader.ParseJson(one)!.Single().Version);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("[]", 0)]
    [InlineData("42", 0)]
    [InlineData("[1,\"x\",null]", 0)]
    [InlineData("""[{"Name":"NoFullName"}]""", 0)]
    public void Odd_but_valid_json_gives_an_empty_list(string json, int count)
    {
        Assert.Equal(count, AppxPackageReader.ParseJson(json)!.Count);
    }

    [Fact]
    public void Text_that_is_not_json_is_a_failed_read()
    {
        Assert.Null(AppxPackageReader.ParseJson("Get-AppxPackage : Access is denied"));
    }

    [Fact]
    public async Task A_stated_size_wins_and_a_missing_one_is_measured_from_the_install_folder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "uu-size-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "a.bin"), new byte[100]);
            File.WriteAllBytes(Path.Combine(dir, "sub", "b.bin"), new byte[50]);

            var measured = new InstalledProgramEntry { Key = "k", Name = "n", InstallLocation = dir };
            var stated = measured with { SizeBytes = 7 };
            var missing = measured with { InstallLocation = Path.Combine(dir, "nope") };
            var relative = measured with { InstallLocation = @"sub" };

            Assert.Equal(150, await InstalledProgramSize.ResolveAsync(measured, CancellationToken.None));
            Assert.Equal(7, await InstalledProgramSize.ResolveAsync(stated, CancellationToken.None));
            Assert.Null(await InstalledProgramSize.ResolveAsync(missing, CancellationToken.None));
            Assert.Null(await InstalledProgramSize.ResolveAsync(relative, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
