using System.Diagnostics;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

/// <summary>
/// Store (MSIX) packages as the app reads them: the package family from the registry key, and the
/// application executable from the manifest, which is trusted only while it stays inside the package folder.
/// Every package is a temp folder behind a table-backed registry.
/// </summary>
public class PackagedAppsTests : IDisposable
{
    private const string FullName = "Zq.Pad_26.1.0.0_x64__2p2nqsd0c76g0";
    private readonly string _temp = Directory.CreateTempSubdirectory().FullName;

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    private sealed class Table(Dictionary<string, (string Name, string Root)> packages) : IRegistryView
    {
        public IReadOnlyList<string> SubKeyNames(string keyPath) =>
            keyPath == PackagedApps.PackagesKey ? [.. packages.Keys] : [];

        public string? Value(string keyPath, string valueName)
        {
            var key = keyPath[(PackagedApps.PackagesKey.Length + 1)..];
            return !packages.TryGetValue(key, out var package)
                ? null
                : valueName switch { "DisplayName" => package.Name, "PackageRootFolder" => package.Root, _ => null };
        }
    }

    /// <summary>The usual shape: the first Application runs app\Zq.exe, two more sit under app\resources.</summary>
    private string Package(string firstExecutable = "app/Zq.exe", bool createFirst = true)
    {
        var root = Path.Combine(_temp, "pkg");
        Directory.CreateDirectory(Path.Combine(root, "app", "resources"));
        File.WriteAllText(Path.Combine(root, "AppxManifest.xml"),
            $"""
             <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
               <Applications>
                 <Application Id="App" Executable="{firstExecutable}" />
                 <Application Id="Cli" Executable="app/resources/zq-cli.exe" />
                 <Application Id="Doc" Executable="app/resources/readme.txt" />
               </Applications>
             </Package>
             """);
        if (createFirst)
            File.WriteAllText(Path.Combine(root, "app", "Zq.exe"), "x");
        File.WriteAllText(Path.Combine(root, "app", "resources", "zq-cli.exe"), "x");
        return root;
    }

    private static PackagedApps Apps(string root) =>
        new(new Table(new() { [FullName] = ("Zq Pad", root) }));

    [Theory]
    [InlineData("Contoso.Pad_26.1.0.0_x64__2p2nqsd0c76g0", "Contoso.Pad_2p2nqsd0c76g0")]
    [InlineData("Microsoft.WindowsTerminal_1.21.0.0_neutral__8wekyb3d8bbwe", "Microsoft.WindowsTerminal_8wekyb3d8bbwe")]
    [InlineData("Name_1.0.0.0_x64_2p2nqsd0c76g0", null)]
    [InlineData("NoUnderscore", null)]
    [InlineData("Zq.Chat_1.0_x64__abc", null)]
    [InlineData("_1.0_x64__2p2nqsd0c76g0", null)]
    public void The_family_name_comes_from_the_registry_key(string key, string? family) =>
        Assert.Equal(family, PackagedApps.FamilyNameOf(key));

    [Fact]
    public void A_package_is_found_by_its_exact_display_name_and_carries_its_family()
    {
        var apps = Apps(Package());

        Assert.Equal("Zq.Pad_2p2nqsd0c76g0", apps.Find("Zq Pad")?.FamilyName);
        Assert.Null(apps.Find("Zq"));
        Assert.Null(apps.Find("Zq Pad Pro"));
    }

    /// <summary>The root folder is user-writable registry text: a bare drive or share root would make every
    /// process on it look like the package's, and a relative one follows the working directory.</summary>
    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"C:")]
    [InlineData(@"C:pkg")]
    [InlineData(@"\\server\share")]
    [InlineData(@"\\server\share\")]
    [InlineData(@"relative\pkg")]
    [InlineData(@"\pkg")]
    public void A_package_whose_folder_is_a_bare_root_or_relative_is_never_found(string root)
    {
        Assert.False(PackagedApps.IsPackageFolder(root));
        Assert.Null(Apps(root).Find("Zq Pad"));
    }

    [Fact]
    public void A_package_folder_below_a_root_is_found() =>
        Assert.True(PackagedApps.IsPackageFolder(@"C:\Program Files\WindowsApps\Zq.Pad_26.1.0.0_x64__2p2nqsd0c76g0"));

    [Fact]
    public void The_newest_version_wins_compared_as_a_version_not_as_text()
    {
        var apps = new PackagedApps(new Table(new()
        {
            ["Zq.Pad_9.0.0.0_x64__2p2nqsd0c76g0"] = ("Zq Pad", @"C:\Pkg\Old"),
            ["Zq.Pad_10.0.0.0_x64__2p2nqsd0c76g0"] = ("Zq Pad", @"C:\Pkg\New"),
        }));

        Assert.Equal(@"C:\Pkg\New", apps.Find("Zq Pad")?.Root);
        Assert.Equal([@"C:\Pkg\New", @"C:\Pkg\Old"], apps.FindAll("Zq Pad").Select(p => p.Root));
    }

    [Fact]
    public void The_first_application_with_an_executable_inside_the_package_is_taken()
    {
        var apps = Apps(Package());

        var application = apps.FirstApplication(apps.Find("Zq Pad")!);

        Assert.Equal("App", application?.Id);
        Assert.EndsWith(Path.Combine("app", "Zq.exe"), application?.ExecutablePath);
    }

    [Fact]
    public void An_application_the_filter_refuses_is_passed_over_for_the_next_one()
    {
        var apps = Apps(Package());

        var application = apps.FirstApplication(apps.Find("Zq Pad")!, path => !path.EndsWith("Zq.exe"));

        Assert.Equal("Cli", application?.Id);
    }

    [Theory]
    [InlineData("../outside/evil.exe")]
    [InlineData("..\\outside\\evil.exe")]
    [InlineData("app/../../outside/evil.exe")]
    [InlineData("C:\\Windows\\System32\\cmd.exe")]
    [InlineData("\\\\server\\share\\evil.exe")]
    [InlineData("/outside/evil.exe")]
    [InlineData("C:evil.exe")]
    public void An_executable_that_escapes_the_package_folder_is_ignored(string executable)
    {
        var root = Package(executable, createFirst: false);
        Directory.CreateDirectory(Path.Combine(_temp, "outside"));
        File.WriteAllText(Path.Combine(_temp, "outside", "evil.exe"), "x");
        var apps = Apps(root);

        var application = apps.FirstApplication(apps.Find("Zq Pad")!);

        Assert.Equal("Cli", application?.Id);
    }

    [Fact]
    public void An_executable_behind_a_junction_is_ignored()
    {
        var root = Package("linked/evil.exe", createFirst: false);
        var outside = Path.Combine(_temp, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "evil.exe"), "x");
        var link = Path.Combine(root, "linked");
        using (var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"")
               { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!)
        {
            process.StandardOutput.ReadToEnd();
            process.WaitForExit();
        }

        Assert.True(Directory.Exists(link), "the junction could not be created");
        var apps = Apps(root);

        var application = apps.FirstApplication(apps.Find("Zq Pad")!);

        Assert.Equal("Cli", application?.Id);
        Directory.Delete(link);
    }

    [Fact]
    public void An_executable_that_is_not_there_or_not_an_exe_is_ignored()
    {
        var apps = Apps(Package(createFirst: false));

        Assert.Equal("Cli", apps.FirstApplication(apps.Find("Zq Pad")!)?.Id);
    }

    [Fact]
    public void A_package_is_started_by_its_family_and_first_application_id()
    {
        var apps = Apps(Package());

        Assert.Equal(@"shell:AppsFolder\Zq.Pad_2p2nqsd0c76g0!App", apps.StartTargetOf(apps.Find("Zq Pad")!));
    }

    [Fact]
    public void An_application_without_an_executable_is_still_started_by_its_id()
    {
        var root = Package();
        File.WriteAllText(Path.Combine(root, "AppxManifest.xml"),
            """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Applications><Application Id="Main" /></Applications>
            </Package>
            """);
        var apps = Apps(root);

        Assert.Equal(@"shell:AppsFolder\Zq.Pad_2p2nqsd0c76g0!Main", apps.StartTargetOf(apps.Find("Zq Pad")!));
        Assert.Null(apps.FirstApplication(apps.Find("Zq Pad")!));
    }

    [Fact]
    public void No_start_target_without_a_family_or_an_application()
    {
        var root = Package();
        var noFamily = new PackagedApps(new Table(new() { ["Odd_key"] = ("Zq Odd", root) }));
        Assert.Null(noFamily.StartTargetOf(noFamily.Find("Zq Odd")!));

        File.Delete(Path.Combine(root, "AppxManifest.xml"));
        var apps = Apps(root);
        Assert.Null(apps.StartTargetOf(apps.Find("Zq Pad")!));
    }

    [Fact]
    public void A_manifest_id_that_is_not_a_plain_id_gives_no_start_target()
    {
        var root = Package();
        File.WriteAllText(Path.Combine(root, "AppxManifest.xml"),
            """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Applications><Application Id="..\..\Windows\x" Executable="app/Zq.exe" /></Applications>
            </Package>
            """);
        var apps = Apps(root);

        Assert.Null(apps.StartTargetOf(apps.Find("Zq Pad")!));
    }

    [Fact]
    public void The_package_list_is_read_again_after_the_cache_lifetime()
    {
        var packages = new Dictionary<string, (string Name, string Root)>();
        var now = DateTime.UtcNow;
        var apps = new PackagedApps(new Table(packages), () => now);
        Assert.Null(apps.Find("Zq Pad"));

        packages[FullName] = ("Zq Pad", Package());
        Assert.Null(apps.Find("Zq Pad"));

        now = now.AddMinutes(1);
        Assert.NotNull(apps.Find("Zq Pad"));
    }
}
