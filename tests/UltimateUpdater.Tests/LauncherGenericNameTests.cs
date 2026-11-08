using UltimateUpdater.Services.Sources;
using Xunit;

namespace UltimateUpdater.Tests;

/// <summary>
/// `Launcher.exe` is a name any product can register under App Paths, so it must not stand for one vendor's
/// launcher unless the path also sits in that vendor's folder.
/// </summary>
public class LauncherGenericNameTests
{
    private const string MachineUninstall = @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string AppPaths = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";

    private static LauncherDefinition Known(string id) => LauncherCatalog.Known().Single(d => d.Id == id);

    [Fact]
    public void A_foreign_launcher_exe_under_app_paths_is_not_rockstar()
    {
        const string foreign = @"C:\Other\Launcher.exe";
        var registry = new FakeRegistry().Set($@"{AppPaths}\Launcher.exe", string.Empty, foreign);

        Assert.Null(LauncherResolver.Resolve(Known("rockstar"), registry, path => path == foreign));
    }

    [Fact]
    public void A_foreign_launcher_exe_from_an_uninstall_entry_outside_the_vendor_folder_is_not_rockstar()
    {
        const string foreign = @"C:\Other\Launcher\Launcher.exe";
        var registry = new FakeRegistry()
            .Set($@"{MachineUninstall}\Rockstar Games Launcher", "DisplayName", "Rockstar Games Launcher")
            .Set($@"{MachineUninstall}\Rockstar Games Launcher", "DisplayIcon", foreign);

        Assert.Null(LauncherResolver.Resolve(Known("rockstar"), registry, path => path == foreign));
    }

    [Fact]
    public void An_uninstall_entry_under_the_vendor_folder_is_rockstar()
    {
        const string exe = @"D:\Games\Rockstar Games\Launcher\Launcher.exe";
        var registry = new FakeRegistry()
            .Set($@"{MachineUninstall}\Rockstar Games Launcher", "DisplayName", "Rockstar Games Launcher")
            .Set($@"{MachineUninstall}\Rockstar Games Launcher", "DisplayIcon", $"\"{exe}\",0");

        Assert.Equal(exe, LauncherResolver.Resolve(Known("rockstar"), registry, path => path == exe));
    }

    [Fact]
    public void The_vendor_folder_is_matched_as_a_whole_directory_name_ignoring_case()
    {
        const string lookalike = @"D:\Not Rockstar Games Inc\Launcher\Launcher.exe";
        const string exe = @"D:\games\ROCKSTAR GAMES\Launcher\Launcher.exe";
        var lookalikeRegistry = new FakeRegistry()
            .Set($@"{MachineUninstall}\R", "DisplayName", "Rockstar Games Launcher")
            .Set($@"{MachineUninstall}\R", "DisplayIcon", lookalike);
        var exactRegistry = new FakeRegistry()
            .Set($@"{MachineUninstall}\R", "DisplayName", "Rockstar Games Launcher")
            .Set($@"{MachineUninstall}\R", "DisplayIcon", exe);

        Assert.Null(LauncherResolver.Resolve(Known("rockstar"), lookalikeRegistry, path => path == lookalike));
        Assert.Equal(exe, LauncherResolver.Resolve(Known("rockstar"), exactRegistry, path => path == exe));
    }

    [Fact]
    public void Hoyoplay_in_a_custom_folder_is_still_found_through_its_uninstall_entry()
    {
        const string exe = @"E:\My Games\HoYoPlay\launcher.exe";
        var registry = new FakeRegistry()
            .Set($@"{MachineUninstall}\HoYoPlay", "DisplayName", "HoYoPlay")
            .Set($@"{MachineUninstall}\HoYoPlay", "InstallLocation", @"E:\My Games\HoYoPlay");

        Assert.Equal(exe, LauncherResolver.Resolve(Known("hoyoplay"), registry, path => path == exe));
    }

    [Fact]
    public void Hoyoplay_does_not_take_a_foreign_launcher_exe_from_app_paths()
    {
        const string foreign = @"C:\Other\launcher.exe";
        var registry = new FakeRegistry().Set($@"{AppPaths}\launcher.exe", string.Empty, foreign);

        Assert.Null(LauncherResolver.Resolve(Known("hoyoplay"), registry, path => path == foreign));
    }

    [Fact]
    public void A_specific_executable_still_resolves_through_app_paths()
    {
        const string exe = @"D:\Anywhere\HYP.exe";
        var registry = new FakeRegistry().Set($@"{AppPaths}\HYP.exe", string.Empty, exe);

        Assert.Equal(exe, LauncherResolver.Resolve(Known("hoyoplay"), registry, path => path == exe));
    }

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
