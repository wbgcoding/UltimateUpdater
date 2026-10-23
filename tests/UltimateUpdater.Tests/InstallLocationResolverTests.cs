using UltimateUpdater.Models;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

/// <summary>
/// Where an update is installed, for the row's "open folder" action. The registry and the filesystem
/// are both parameters, so these run against a table of keys and a set of "existing" paths rather than
/// whatever is installed on the machine running them.
/// </summary>
public class InstallLocationResolverTests
{
    private const string MachineUninstall = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string UserUninstall = @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private static UpdateItem Item(
        string name, string id = "id", string? openTarget = null, IReadOnlyDictionary<string, string>? meta = null,
        string sourceId = "winget", UpdateCategory category = UpdateCategory.Software) =>
        new()
        {
            Id = id,
            SourceId = sourceId,
            Category = category,
            Name = name,
            OpenTarget = openTarget,
            Meta = meta ?? new Dictionary<string, string>(),
        };

    /// <summary>The index is built once and reused, but a program installed after start (a long tray
    /// session) must be found once the index is dropped at the next scan.</summary>
    [Fact]
    public void A_program_installed_after_the_index_was_built_is_found_after_Invalidate()
    {
        const string folder = @"C:\Program Files\Late";
        var registry = new FakeRegistry();
        var resolver = new InstallLocationResolver(registry, dir => dir == folder, _ => true);
        var item = Item("Late App", id: "LateKey");

        Assert.Null(resolver.Resolve(item));

        registry.Set($@"{MachineUninstall}\LateKey", "DisplayName", "Late App")
            .Set($@"{MachineUninstall}\LateKey", "InstallLocation", folder);
        resolver.Invalidate();

        Assert.Equal(folder, resolver.Resolve(item));
    }

    // ---- "Start" for an ordinary package ----

    /// <summary>
    /// Most winget and Chocolatey entries register no DisplayIcon at all, which is why so many rows had no
    /// Start action. Their InstallLocation is known, so the program's own exe is looked for inside it.
    /// </summary>
    [Fact]
    public void A_launch_exe_is_found_in_the_install_directory_by_name()
    {
        const string folder = @"C:\Program Files\Notepad++";
        var registry = new FakeRegistry()
            .Set($@"{MachineUninstall}\NppKey", "DisplayName", "Notepad++ (64-bit x64)")
            .Set($@"{MachineUninstall}\NppKey", "InstallLocation", folder);

        var resolver = new InstallLocationResolver(
            registry,
            directoryExists: dir => dir == folder,
            fileExists: _ => true,
            executablesIn: _ => [$@"{folder}\updater.exe", $@"{folder}\notepad++.exe"]);

        Assert.Equal($@"{folder}\notepad++.exe",
            resolver.ResolveLaunchExe(Item("Notepad++ (64-bit x64)", id: "NppKey")));
    }

    [Fact]
    public void A_single_startable_executable_is_used_even_when_the_name_does_not_match()
    {
        const string folder = @"C:\Program Files\Some Vendor\Thing";
        var resolver = new InstallLocationResolver(
            new FakeRegistry(), dir => dir == folder, _ => true,
            executablesIn: _ => [$@"{folder}\launcher.exe"]);

        var item = Item("Vendor Thing", meta: new Dictionary<string, string> { ["installLocation"] = folder });

        Assert.Equal($@"{folder}\launcher.exe", resolver.ResolveLaunchExe(item));
    }

    /// <summary>Conservative on purpose: starting the WRONG program is worse than offering nothing.</summary>
    [Fact]
    public void Several_unrelated_executables_resolve_to_nothing()
    {
        const string folder = @"C:\Tools\Bundle";
        var resolver = new InstallLocationResolver(
            new FakeRegistry(), dir => dir == folder, _ => true,
            executablesIn: _ => [$@"{folder}\alpha.exe", $@"{folder}\beta.exe", $@"{folder}\gamma.exe"]);

        var item = Item("Some Bundle", meta: new Dictionary<string, string> { ["installLocation"] = folder });

        Assert.Null(resolver.ResolveLaunchExe(item));
    }

    [Fact]
    public void An_uninstaller_is_never_offered_as_the_way_to_start_a_program()
    {
        const string folder = @"C:\Program Files\Thing";
        var resolver = new InstallLocationResolver(
            new FakeRegistry(), dir => dir == folder, _ => true,
            executablesIn: _ => [$@"{folder}\unins000.exe", $@"{folder}\setup.exe"]);

        var item = Item("Thing", meta: new Dictionary<string, string> { ["installLocation"] = folder });

        Assert.Null(resolver.ResolveLaunchExe(item));
    }

    [Fact]
    public void A_location_the_source_recorded_wins()
    {
        const string folder = @"D:\SteamLibrary\steamapps\common\Some Game";
        var resolver = new InstallLocationResolver(new FakeRegistry(), dir => dir == folder, _ => false);

        var item = Item("Some Game", meta: new Dictionary<string, string> { ["installLocation"] = folder });

        Assert.Equal(folder, resolver.Resolve(item));
    }

    [Fact]
    public void A_recorded_location_that_is_gone_is_not_used()
    {
        var resolver = new InstallLocationResolver(new FakeRegistry(), _ => false, _ => false);
        var item = Item("Some Game", meta: new Dictionary<string, string> { ["installLocation"] = @"D:\gone" });

        Assert.Null(resolver.Resolve(item));
    }

    [Fact]
    public void An_apps_install_location_is_read_from_the_uninstall_registry()
    {
        const string folder = @"C:\Program Files\Foo";
        var registry = new FakeRegistry()
            .Set($@"{MachineUninstall}\Foo", "DisplayName", "Foo App")
            .Set($@"{MachineUninstall}\Foo", "InstallLocation", folder + @"\");

        var resolver = new InstallLocationResolver(registry, dir => dir == folder, _ => false);

        Assert.Equal(folder, resolver.Resolve(Item("Foo App")));
    }

    [Fact]
    public void The_folder_is_derived_from_the_display_icon_when_no_install_location_is_set()
    {
        const string exe = @"C:\Apps\Bar\bar.exe";
        const string folder = @"C:\Apps\Bar";
        var registry = new FakeRegistry()
            .Set($@"{MachineUninstall}\Bar", "DisplayName", "Bar")
            .Set($@"{MachineUninstall}\Bar", "DisplayIcon", $"{exe},0");

        var resolver = new InstallLocationResolver(registry, dir => dir == folder, _ => false);

        Assert.Equal(folder, resolver.Resolve(Item("Bar")));
    }

    [Fact]
    public void The_uninstall_key_name_matches_when_it_equals_the_package_id()
    {
        const string folder = @"C:\Program Files\Baz";
        var registry = new FakeRegistry()
            .Set($@"{UserUninstall}\Vendor.Baz", "InstallLocation", folder);

        var resolver = new InstallLocationResolver(registry, dir => dir == folder, _ => false);

        // no DisplayName match, the id is the uninstall key itself, as some winget ids are
        Assert.Equal(folder, resolver.Resolve(Item("Anything", id: "Vendor.Baz")));
    }

    [Fact]
    public void The_open_targets_own_folder_is_the_last_resort()
    {
        const string exe = @"D:\Launchers\Foo\foo.exe";
        const string folder = @"D:\Launchers\Foo";

        var resolver = new InstallLocationResolver(
            new FakeRegistry(), dir => dir == folder, file => file == exe);

        Assert.Equal(folder, resolver.Resolve(Item("Foo", openTarget: exe)));
    }

    [Fact]
    public void A_uri_open_target_is_not_treated_as_a_path()
    {
        var resolver = new InstallLocationResolver(new FakeRegistry(), _ => true, _ => true);

        Assert.Null(resolver.Resolve(Item("Steam", openTarget: "steam://open/downloads")));
    }

    [Fact]
    public void A_different_products_entry_is_not_mistaken_for_this_one()
    {
        var registry = new FakeRegistry()
            .Set($@"{MachineUninstall}\Foo", "DisplayName", "Foobar")
            .Set($@"{MachineUninstall}\Foo", "InstallLocation", @"C:\Program Files\Foobar");

        // matched on the full DisplayName, so "Foo" is not "Foobar"
        var resolver = new InstallLocationResolver(registry, _ => true, _ => false);

        Assert.Null(resolver.Resolve(Item("Foo")));
    }

    [Fact]
    public void Nothing_resolves_when_there_is_nothing_to_find()
    {
        var resolver = new InstallLocationResolver(new FakeRegistry(), _ => false, _ => false);

        Assert.Null(resolver.Resolve(Item("Foo")));
    }

    // ---- winget packages folder (no uninstall-registry entry) ----

    /// <summary>
    /// A portable or MSIX winget install that never registered InstallLocation/DisplayIcon still landed
    /// under its own "<PackageId>_<hash>" folder, winget's own naming convention.
    /// </summary>
    [Fact]
    public void A_winget_package_with_no_registry_entry_is_found_under_its_WinGet_packages_folder()
    {
        const string root = @"C:\Users\Me\AppData\Local\Microsoft\WinGet\Packages";
        const string folder = $@"{root}\Foo.Bar_8wekyb3d8bbwe";

        var resolver = new InstallLocationResolver(
            new FakeRegistry(), dir => dir == folder, _ => false,
            subDirectoriesOf: dir => dir == root ? [folder, $@"{root}\Other.Package_hash"] : [],
            winGetPackagesRoot: root);

        Assert.Equal(folder, resolver.Resolve(Item("Foo Bar", id: "Foo.Bar")));
    }

    [Fact]
    public void A_winget_packages_folder_is_never_matched_by_a_partial_id()
    {
        const string root = @"C:\Users\Me\AppData\Local\Microsoft\WinGet\Packages";

        var resolver = new InstallLocationResolver(
            new FakeRegistry(), _ => true, _ => false,
            subDirectoriesOf: dir => dir == root ? [$@"{root}\Foo.BarExtra_hash"] : [],
            winGetPackagesRoot: root);

        Assert.Null(resolver.Resolve(Item("Foo Bar", id: "Foo.Bar")));
    }

    [Fact]
    public void The_WinGet_packages_folder_is_never_consulted_for_a_non_winget_source()
    {
        const string root = @"C:\Users\Me\AppData\Local\Microsoft\WinGet\Packages";

        var resolver = new InstallLocationResolver(
            new FakeRegistry(), _ => true, _ => false,
            subDirectoriesOf: dir => dir == root ? [$@"{root}\Foo.Bar_hash"] : [],
            winGetPackagesRoot: root);

        Assert.Null(resolver.Resolve(Item("Foo Bar", id: "Foo.Bar", sourceId: "choco")));
    }

    // ---- developer-tool CLI packages resolved from PATH ----

    /// <summary>Most CLI package ids equal the console script the manager put on PATH.</summary>
    [Fact]
    public void A_developer_tool_package_is_found_by_its_own_name_on_PATH()
    {
        var pathDir = @"C:\Users\Me\AppData\Roaming\npm";
        const string exe = @"C:\Users\Me\AppData\Roaming\npm\eslint.cmd";

        var resolver = new InstallLocationResolver(
            new FakeRegistry(), dir => dir == pathDir, file => file == exe,
            pathDirectories: [pathDir]);

        var item = Item("eslint", id: "eslint", sourceId: "npm", category: UpdateCategory.DeveloperTools);

        Assert.Equal(pathDir, resolver.Resolve(item));
    }

    /// <summary>A package whose id differs from its own binary (the common case) resolves to nothing rather
    /// than a guess, conservative, same as everywhere else in this class.</summary>
    [Fact]
    public void A_developer_tool_package_whose_binary_is_named_differently_resolves_to_nothing()
    {
        var resolver = new InstallLocationResolver(
            new FakeRegistry(), _ => true, _ => false,
            pathDirectories: [@"C:\Users\Me\AppData\Roaming\npm"]);

        var item = Item("TypeScript", id: "typescript", sourceId: "npm", category: UpdateCategory.DeveloperTools);

        Assert.Null(resolver.Resolve(item));
    }

    [Fact]
    public void PATH_lookup_is_never_used_for_a_package_outside_developer_tools()
    {
        var pathDir = @"C:\Users\Me\AppData\Roaming\npm";
        const string exe = @"C:\Users\Me\AppData\Roaming\npm\foo.exe";

        var resolver = new InstallLocationResolver(
            new FakeRegistry(), dir => dir == pathDir, file => file == exe,
            pathDirectories: [pathDir]);

        Assert.Null(resolver.Resolve(Item("foo", id: "foo", sourceId: "npm", category: UpdateCategory.Software)));
    }

    /// <summary>A registry made of nothing but the keys a test puts in it.</summary>
    private sealed class FakeRegistry : IRegistryView
    {
        private readonly Dictionary<string, Dictionary<string, string>> _keys = new(StringComparer.OrdinalIgnoreCase);

        public FakeRegistry Set(string keyPath, string valueName, string value)
        {
            if (!_keys.TryGetValue(keyPath, out var values))
                _keys[keyPath] = values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            values[valueName] = value;
            return this;
        }

        public IReadOnlyList<string> SubKeyNames(string keyPath) =>
            _keys.Keys
                .Where(key => key.StartsWith(keyPath + @"\", StringComparison.OrdinalIgnoreCase))
                .Select(key => key[(keyPath.Length + 1)..].Split('\\')[0])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        public string? Value(string keyPath, string valueName) =>
            _keys.TryGetValue(keyPath, out var values) ? values.GetValueOrDefault(valueName) : null;
    }
}
