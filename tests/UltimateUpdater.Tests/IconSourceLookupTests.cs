using System.Runtime.InteropServices;
using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

/// <summary>
/// The places an icon is found beyond the Uninstall registry: Start-menu shortcuts, executables on PATH
/// and the vendor tool of a driver row. Every location is a temp folder, so none of it depends on the
/// programs installed on the machine running the tests.
/// </summary>
public class IconSourceLookupTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory().FullName;
    private readonly List<string> _cachedIcons = [];

    public void Dispose()
    {
        foreach (var icon in _cachedIcons)
            File.Delete(icon);

        Directory.Delete(_root, recursive: true);
    }

    private static string ExeWithIcon => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    private string Folder(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private string CopyOfExeWithIcon(string folder, string fileName)
    {
        var path = Path.Combine(folder, fileName);
        File.Copy(ExeWithIcon, path);
        return path;
    }

    /// <summary>A file that is not a program at all, so it carries no icon resource.</summary>
    private static string ExeWithoutIcon(string folder, string fileName)
    {
        var path = Path.Combine(folder, fileName);
        File.WriteAllText(path, "not a program");
        return path;
    }

    /// <summary>Shell links can only be created on an apartment thread.</summary>
    private static void WriteShortcut(string linkPath, string target, string? iconPath = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            object? link = null;
            try
            {
                link = new StartMenuShortcuts.ShellLink();
                var shell = (StartMenuShortcuts.IShellLinkW)link;
                shell.SetPath(target);
                if (iconPath is not null)
                    shell.SetIconLocation(iconPath, 0);

                ((StartMenuShortcuts.IPersistFile)link).Save(linkPath, true);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                if (link is not null)
                    Marshal.ReleaseComObject(link);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
            throw failure;
    }

    private IconCacheService Cache(
        IReadOnlyList<string>? startMenu = null,
        DriverVendorTools? drivers = null,
        IReadOnlyList<string>? path = null) =>
        new(new LoggingService(), new StartMenuShortcuts(startMenu ?? []), drivers, path ?? []);

    private object? Icon(IconCacheService cache, string sourceId, string itemId, string name)
    {
        _cachedIcons.Add(Path.Combine(AppPaths.IconCacheDirectory, IconCacheService.CacheKey(sourceId, itemId) + ".png"));
        return cache.GetIcon(sourceId, itemId, name);
    }

    [Fact]
    public void A_start_menu_shortcut_is_found_by_the_app_name_in_a_nested_folder()
    {
        var menu = Folder("menu");
        WriteShortcut(Path.Combine(menu, "Vendor", "Zq Fixture App.lnk"), ExeWithIcon);

        var shortcut = new StartMenuShortcuts([menu]).Find("Zq Fixture App");

        Assert.NotNull(shortcut);
        Assert.Equal(ExeWithIcon, shortcut!.TargetPath, ignoreCase: true);
    }

    /// <summary>One subfolder the user may not list used to throw out of the whole enumeration, and the
    /// catch then dropped every shortcut of that root.</summary>
    [Fact]
    public void A_start_menu_folder_that_cannot_be_listed_does_not_hide_the_other_shortcuts()
    {
        var menu = Folder("menu");
        WriteShortcut(Path.Combine(menu, "Vendor", "Zq Fixture App.lnk"), ExeWithIcon);
        var locked = new DirectoryInfo(Folder(Path.Combine("menu", "Locked")));
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var deny = new System.Security.AccessControl.FileSystemAccessRule(
            sid, System.Security.AccessControl.FileSystemRights.ListDirectory,
            System.Security.AccessControl.InheritanceFlags.None, System.Security.AccessControl.PropagationFlags.None,
            System.Security.AccessControl.AccessControlType.Deny);

        // Listing this one folder is refused for the current user, like a Start menu folder another account made.
        var security = locked.GetAccessControl();
        security.AddAccessRule(deny);
        locked.SetAccessControl(security);
        try
        {
            var shortcut = new StartMenuShortcuts([menu]).Find("Zq Fixture App");

            Assert.NotNull(shortcut);
        }
        finally
        {
            security.RemoveAccessRule(deny);
            locked.SetAccessControl(security);
        }
    }

    [Fact]
    public void A_shortcut_that_only_opens_the_uninstaller_is_not_the_app()
    {
        var menu = Folder("menu");
        WriteShortcut(Path.Combine(menu, "Uninstall Zq Fixture App.lnk"), ExeWithIcon);

        Assert.Null(new StartMenuShortcuts([menu]).Find("Zq Fixture App"));
    }

    [Fact]
    public void A_note_in_brackets_does_not_hide_the_shortcut()
    {
        var menu = Folder("menu");
        WriteShortcut(Path.Combine(menu, "Zq Fixture Drive.lnk"), ExeWithIcon);

        Assert.NotNull(new StartMenuShortcuts([menu]).Find("Zq Fixture Drive (remove only)"));
    }

    [Fact]
    public void A_missing_start_menu_folder_is_just_empty()
    {
        Assert.Null(new StartMenuShortcuts([Path.Combine(_root, "missing")]).Find("Anything Goes"));
    }

    [Theory]
    [InlineData("Synology Drive Client (remove only)", "Synology Drive Client")]
    [InlineData("Mozilla Firefox 130.0 (x64 de)", "Mozilla Firefox 130.0")]
    [InlineData("Plain Name", "Plain Name")]
    [InlineData("(Only Brackets)", "(Only Brackets)")]
    public void CleanName_drops_trailing_bracketed_notes(string input, string expected)
    {
        Assert.Equal(expected, IconCacheService.CleanName(input));
    }

    [Fact]
    public void A_start_menu_shortcut_gives_the_row_its_icon()
    {
        var menu = Folder("menu");
        WriteShortcut(Path.Combine(menu, "Zq Shortcut Icon App.lnk"), ExeWithIcon);

        Assert.NotNull(Icon(Cache(startMenu: [menu]), "winget", "Zq.ShortcutIconApp", "Zq Shortcut Icon App"));
    }

    [Fact]
    public void A_shortcut_to_a_program_without_an_icon_gives_no_icon()
    {
        var menu = Folder("menu");
        var bare = ExeWithoutIcon(Folder("bin"), "zqbare.exe");
        WriteShortcut(Path.Combine(menu, "Zq Bare App.lnk"), bare);

        Assert.Null(Icon(Cache(startMenu: [menu]), "winget", "Zq.BareApp", "Zq Bare App"));
    }

    [Fact]
    public void A_command_line_tool_on_PATH_gives_the_row_its_icon()
    {
        var bin = Folder("bin");
        CopyOfExeWithIcon(bin, "zqtool.exe");

        Assert.NotNull(Icon(Cache(path: [bin]), "uv", "zqtool", "zqtool"));
    }

    [Fact]
    public void A_tool_on_PATH_is_found_by_the_last_part_of_a_dotted_id()
    {
        var bin = Folder("bin");
        CopyOfExeWithIcon(bin, "zqdotted.exe");

        Assert.NotNull(Icon(Cache(path: [bin]), "github-release", "zq-sh.zqdotted", "Dotted Release Tool"));
    }

    [Fact]
    public void A_tool_on_PATH_without_an_icon_resource_gives_no_icon()
    {
        var bin = Folder("bin");
        ExeWithoutIcon(bin, "zqnoicon.exe");

        Assert.Null(Icon(Cache(path: [bin]), "uv", "zqnoicon", "zqnoicon"));
    }

    [Theory]
    [InlineData("pip", "typer")]
    [InlineData("pip", "google-auth")]
    [InlineData("pipx", "zqlib")]
    [InlineData("npm", "zqlib")]
    [InlineData("pnpm", "zqlib")]
    [InlineData("gem", "zqlib")]
    [InlineData("composer", "zqlib")]
    [InlineData("dotnet-tool", "zqlib")]
    [InlineData("uv-tool", "zqlib")]
    [InlineData("psgallery", "zqlib")]
    public void A_library_row_never_borrows_an_icon_from_PATH_the_Start_menu_or_the_registry_apps(string sourceId, string itemId)
    {
        var bin = Folder("bin");
        CopyOfExeWithIcon(bin, itemId + ".exe");
        var menu = Folder("menu");
        WriteShortcut(Path.Combine(menu, itemId + ".lnk"), ExeWithIcon);

        Assert.Null(Icon(Cache(startMenu: [menu], path: [bin]), sourceId, itemId, itemId));
    }

    [Theory]
    [InlineData("pip", "typer", "python")]
    [InlineData("pip", "google-auth", "python")]
    [InlineData("pipx", "zqlib", "python")]
    [InlineData("npm", "zqlib", "npm")]
    [InlineData("pnpm", "zqlib", "pnpm")]
    [InlineData("gem", "zqlib", "ruby")]
    [InlineData("composer", "zqlib", "composer")]
    [InlineData("dotnet-tool", "zqlib", "dotnet")]
    [InlineData("uv-tool", "zqlib", "uv")]
    public void A_library_row_shows_its_ecosystem_mark(string sourceId, string itemId, string slug)
    {
        var expected = BrandIcons.Resolve(slug, null, null);

        Assert.NotNull(expected);
        Assert.Equal(expected!.ToString(), BrandIcons.Resolve(itemId, sourceId, itemId)!.ToString());
    }

    [Fact]
    public void The_uv_row_still_takes_its_own_executable_from_PATH()
    {
        var bin = Folder("bin");
        CopyOfExeWithIcon(bin, "uv.exe");

        Assert.NotNull(Icon(Cache(path: [bin]), "uv", "astral-sh.uv", "uv"));
    }

    [Fact]
    public void An_executable_in_a_Scripts_folder_is_never_chosen()
    {
        var scripts = Folder(Path.Combine("py", "Scripts"));
        CopyOfExeWithIcon(scripts, "zqscripted.exe");

        Assert.Null(Icon(Cache(path: [scripts]), "uv", "zqscripted", "zqscripted"));
        Assert.True(IconCacheService.IsScriptsLauncher(Path.Combine(scripts, "zqscripted.exe")));
        Assert.False(IconCacheService.IsScriptsLauncher(Path.Combine(Folder("bin"), "zqscripted.exe")));
    }

    [Fact]
    public void An_application_row_is_not_matched_against_PATH()
    {
        var bin = Folder("bin");
        CopyOfExeWithIcon(bin, "zqapp.exe");

        Assert.Null(Icon(Cache(path: [bin]), "winget", "zqapp", "zqapp"));
    }

    private static DriverVendorTools.Vendor Vendor(string key, string[] words, params string[] candidates) =>
        new(key, words, candidates, null);

    [Fact]
    public void The_first_existing_vendor_tool_wins()
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\b\second.exe", @"C:\c\third.exe" };
        var vendor = Vendor("nvidia", ["nvidia"], @"C:\a\first.exe", @"C:\b\second.exe", @"C:\c\third.exe");
        var tools = new DriverVendorTools(present.Contains, text => text, [vendor]);

        Assert.Equal(@"C:\b\second.exe", tools.FindExecutable(vendor));
    }

    [Fact]
    public void No_installed_vendor_tool_means_no_executable()
    {
        var vendor = Vendor("nvidia", ["nvidia"], @"C:\a\first.exe");
        var tools = new DriverVendorTools(_ => false, text => text, [vendor]);

        Assert.Null(tools.FindExecutable(vendor));
    }

    [Fact]
    public void Environment_variables_in_a_candidate_are_expanded_before_the_check()
    {
        var seen = new List<string>();
        var vendor = Vendor("amd", ["amd"], @"%ProgramFiles%\AMD\tool.exe");
        var tools = new DriverVendorTools(path => { seen.Add(path); return false; }, _ => @"D:\Programs\AMD\tool.exe", [vendor]);

        tools.FindExecutable(vendor);

        Assert.Equal([@"D:\Programs\AMD\tool.exe"], seen);
    }

    [Theory]
    [InlineData("nvidia-driver", "GeForce Game Ready Driver", "nvidia")]
    [InlineData("windowsupdate", "NVIDIA - Display - 31.0.15.5222", "nvidia")]
    [InlineData("windowsupdate", "Intel - System - 2430.1.3.5", "intel")]
    [InlineData("windowsupdate", "Intel(R) Corporation - Bluetooth", "intel")]
    [InlineData("windowsupdate", "Realtek Semiconductor Corp. - Extension - 6.0.9", "realtek")]
    [InlineData("windowsupdate", "AMD - Display - 31.0.21", "amd")]
    [InlineData("windowsupdate", "Advanced Micro Devices, Inc. - Display", "amd")]
    [InlineData("windowsupdate", "Logitech - HIDClass", "logitech")]
    [InlineData("windowsupdate", "2026-10 Cumulative Update for Windows 11", null)]
    [InlineData("windowsupdate", "Samsung - Disk drive - 1.0", null)]
    [InlineData("winget", "NVIDIA App", null)]
    public void A_driver_row_is_matched_to_its_vendor(string sourceId, string title, string? expected)
    {
        var vendor = new DriverVendorTools().VendorFor(sourceId, title);

        Assert.Equal(expected, vendor?.Key);
    }

    [Fact]
    public void The_vendor_table_names_the_known_tool_locations()
    {
        var all = DriverVendorTools.DefaultVendors.SelectMany(v => v.Candidates).ToList();

        Assert.Contains(@"%ProgramFiles%\NVIDIA Corporation\NVIDIA App\CEF\NVIDIA App.exe", all);
        Assert.Contains(@"%ProgramFiles%\NVIDIA Corporation\NVIDIA App\NVIDIA App.exe", all);
        Assert.Contains(@"%ProgramFiles%\AMD\CNext\CNext\RadeonSoftware.exe", all);
        Assert.Contains(@"%ProgramFiles%\LGHUB\lghub.exe", all);
    }

    [Fact]
    public void A_driver_row_gets_the_icon_of_the_vendor_tool()
    {
        var tool = CopyOfExeWithIcon(Folder("vendor"), "VendorTool.exe");
        var drivers = new DriverVendorTools(File.Exists, text => text, [Vendor("zqvendor", ["zqvendor"], tool)]);

        Assert.NotNull(Icon(Cache(drivers: drivers), "windowsupdate", "zq-driver-guid", "ZqVendor - Display - 1.2.3"));
    }

    [Fact]
    public void A_DisplayIcon_naming_a_file_without_an_icon_resource_is_not_used()
    {
        var bare = ExeWithoutIcon(Folder("display"), "zqbare.exe");

        Assert.Null(IconCacheService.ParseIconValue(bare));
        Assert.Null(IconCacheService.ParseIconValue($"\"{bare}\",0"));
    }

    [Fact]
    public void A_DisplayIcon_index_past_the_last_icon_is_not_used()
    {
        Assert.NotNull(IconCacheService.ParseIconValue($"{ExeWithIcon},0"));
        Assert.Null(IconCacheService.ParseIconValue($"{ExeWithIcon},9999"));
    }

    [Fact]
    public void A_launcher_executable_hint_without_an_icon_resource_gives_no_icon()
    {
        var bare = ExeWithoutIcon(Folder("hint"), "zqhint.exe");

        Assert.Null(IconCacheService.IconSourceFromExecutable(bare));
        Assert.NotNull(IconCacheService.IconSourceFromExecutable(ExeWithIcon));
    }

    [Fact]
    public void A_driver_row_without_an_installed_vendor_tool_gets_no_icon()
    {
        var drivers = new DriverVendorTools(_ => false, text => text, [Vendor("zqvendor", ["zqvendor"], @"C:\none.exe")]);

        Assert.Null(Icon(Cache(drivers: drivers), "windowsupdate", "zq-driver-guid-2", "ZqVendor - Display - 1.2.3"));
    }
}
