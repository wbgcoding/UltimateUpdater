using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

/// <summary>
/// Where a launcher really is. Both things the lookup depends on, the registry and the file system,
/// are parameters of the resolver, so these run against a table of keys and a set of paths instead of
/// whatever happens to be installed on the machine running the tests.
/// </summary>
public class LauncherResolverTests
{
    private const string MachineUninstall = @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string UserUninstall = @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string AppPaths = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";

    /// <summary>The vendors' defaults are the LAST candidate, so this stands in for one.</summary>
    private const string DefaultPath = @"C:\Program Files (x86)\Battle.net\Battle.net Launcher.exe";

    [Fact]
    public void An_install_on_another_drive_is_found_through_the_uninstall_entry()
    {
        const string exe = @"D:\Games\Battle.net\Battle.net Launcher.exe";

        var registry = new FakeRegistry()
            .Set($@"{MachineUninstall}\Battle.net", "DisplayName", "Battle.net")
            .Set($@"{MachineUninstall}\Battle.net", "DisplayIcon", $"\"{exe}\",0");

        Assert.Equal(exe, LauncherResolver.Resolve(BattleNet(), registry, path => path == exe));
    }

    [Fact]
    public void An_entry_without_an_icon_falls_back_to_its_install_location()
    {
        const string directory = @"E:\Launchers\Battle.net";
        var exe = Path.Combine(directory, "Battle.net Launcher.exe");

        // key names are product codes on plenty of products, so the DisplayName is what is matched
        var registry = new FakeRegistry()
            .Set($@"{MachineUninstall}\{{9A2B-4C1D}}", "DisplayName", "battle.net ")
            .Set($@"{MachineUninstall}\{{9A2B-4C1D}}", "InstallLocation", directory + @"\");

        Assert.Equal(exe, LauncherResolver.Resolve(BattleNet(), registry, path => path == exe));
    }

    [Fact]
    public void A_per_user_launcher_is_found_in_the_user_hive()
    {
        const string exe = @"D:\Users\someone\AppData\Local\Amazon Games\App\Amazon Games.exe";

        var registry = new FakeRegistry()
            .Set($@"{UserUninstall}\Amazon Games", "DisplayName", "Amazon Games")
            .Set($@"{UserUninstall}\Amazon Games", "DisplayIcon", exe);

        var amazon = LauncherCatalog.Known().Single(d => d.Id == "amazongames");

        Assert.Equal(exe, LauncherResolver.Resolve(amazon, registry, path => path == exe));
    }

    [Fact]
    public void App_Paths_names_the_executable_outright()
    {
        const string exe = @"F:\Tools\Battle.net\Battle.net Launcher.exe";

        var registry = new FakeRegistry().Set($@"{AppPaths}\Battle.net Launcher.exe", string.Empty, $"\"{exe}\"");

        Assert.Equal(exe, LauncherResolver.Resolve(BattleNet(), registry, path => path == exe));
    }

    /// <summary>Ubisoft's own key holds a directory, with forward slashes and a trailing separator.</summary>
    [Fact]
    public void A_vendor_key_holding_the_install_directory_is_used()
    {
        const string directory = "D:/Games/Ubisoft/Ubisoft Game Launcher/";
        var exe = Path.Combine(directory, "UbisoftConnect.exe");

        var registry = new FakeRegistry()
            .Set(@"HKLM\SOFTWARE\WOW6432Node\Ubisoft\Launcher", "InstallDir", directory);

        var ubisoft = LauncherCatalog.Known().Single(d => d.Id == "ubisoft");

        Assert.Equal(exe, LauncherResolver.Resolve(ubisoft, registry, path => path == exe));
    }

    [Fact]
    public void The_vendors_default_path_is_the_last_resort()
    {
        Assert.Equal(DefaultPath, LauncherResolver.Resolve(BattleNet(), new FakeRegistry(), path => path == DefaultPath));
    }

    [Fact]
    public void What_the_registry_says_beats_the_default_path()
    {
        const string exe = @"D:\Games\Battle.net\Battle.net Launcher.exe";

        var registry = new FakeRegistry()
            .Set($@"{MachineUninstall}\Battle.net", "DisplayName", "Battle.net")
            .Set($@"{MachineUninstall}\Battle.net", "DisplayIcon", exe);

        // both are "on disk"; the one the machine actually reports must win
        Assert.Equal(exe, LauncherResolver.Resolve(BattleNet(), registry, path => path == exe || path == DefaultPath));
    }

    [Fact]
    public void A_registry_entry_pointing_nowhere_resolves_to_nothing()
    {
        var registry = new FakeRegistry()
            .Set($@"{MachineUninstall}\Battle.net", "DisplayName", "Battle.net")
            .Set($@"{MachineUninstall}\Battle.net", "DisplayIcon", @"D:\gone\Battle.net Launcher.exe");

        Assert.Null(LauncherResolver.Resolve(BattleNet(), registry, _ => false));
    }

    [Fact]
    public void Another_products_entry_is_not_mistaken_for_the_client()
    {
        const string exe = @"D:\Games\Battle.net Games\Battle.net Launcher.exe";

        var registry = new FakeRegistry()
            .Set($@"{MachineUninstall}\Whatever", "DisplayName", "Battle.net Games")
            .Set($@"{MachineUninstall}\Whatever", "DisplayIcon", exe);

        // the name is compared in full, so a longer name is a different product
        Assert.Null(LauncherResolver.Resolve(BattleNet(), registry, path => path == exe));
    }

    /// <summary>Riot's default install path lives on the system drive, not necessarily C:\, resolving
    /// it statically would miss real installs set up on a different drive.</summary>
    [Fact]
    public void Riot_is_found_at_its_real_default_location()
    {
        var riot = LauncherCatalog.Known().Single(d => d.Id == "riot");
        var systemDrive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!;
        var exe = Path.Combine(systemDrive, "Riot Games", "Riot Client", "RiotClientServices.exe");

        Assert.Equal(exe, LauncherResolver.Resolve(riot, new FakeRegistry(), path => path == exe));
    }

    [Fact]
    public void The_launchers_that_were_missing_are_in_the_catalog()
    {
        var ids = LauncherCatalog.Known().Select(d => d.Id).ToList();

        Assert.Contains("amazongames", ids);
        Assert.Contains("itch", ids);
        Assert.Contains("minecraft", ids);
        Assert.Contains("paradox", ids);
        Assert.Contains("roblox", ids);
    }

    [Fact]
    public void The_dev_tool_clients_are_categorised_as_developer_tools_not_games()
    {
        var known = LauncherCatalog.Known();

        foreach (var id in new[] { "jetbrains-toolbox", "docker-desktop", "unity-hub" })
        {
            var definition = known.Single(d => d.Id == id);
            Assert.Equal(UltimateUpdater.Models.UpdateCategory.DeveloperTools, definition.Category);
        }

        // everything else keeps the implicit default
        Assert.All(known.Where(d => d.Id is not ("jetbrains-toolbox" or "docker-desktop" or "unity-hub")),
            definition => Assert.Equal(UltimateUpdater.Models.UpdateCategory.Games, definition.Category));
    }

    [Fact]
    public void Every_known_launcher_has_something_to_look_for()
    {
        var known = LauncherCatalog.Known();

        Assert.Equal(known.Count, known.Select(d => d.Id).Distinct(StringComparer.Ordinal).Count());

        Assert.All(known, definition =>
        {
            Assert.NotEmpty(definition.Id);
            Assert.NotEmpty(definition.DisplayName);
            Assert.NotEmpty(definition.Executables);

            // a definition that looks nowhere could never produce a row
            var places = definition.UninstallDisplayNames.Count + definition.FallbackPaths.Count
                + definition.InstallDirectoryValues.Count + definition.ExecutablePathValues.Count;
            Assert.True(places > 0, $"{definition.Id} has no detection point");
        });
    }

    /// <summary>A launcher that is not installed produces no row at all, never a guessed one.</summary>
    [Fact]
    public void Nothing_resolves_on_a_machine_that_has_none_of_them()
    {
        var registry = new FakeRegistry();

        Assert.All(LauncherCatalog.Known(), definition =>
            Assert.Null(LauncherResolver.Resolve(definition, registry, _ => false)));
    }

    private static LauncherDefinition BattleNet() => new()
    {
        Id = "battlenet",
        DisplayName = "Battle.net",
        Executables = ["Battle.net Launcher.exe"],
        UninstallDisplayNames = ["Battle.net"],
        FallbackPaths = [DefaultPath],
    };

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
