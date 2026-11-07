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
    private const string FullName = "Zq.Codex_26.1.0.0_x64__2p2nqsd0c76g0";
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

    /// <summary>The ChatGPT shape: the first Application runs app\Zq.exe, two more sit under app\resources.</summary>
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
        new(new Table(new() { [FullName] = ("Zq Codex", root) }));

    [Theory]
    [InlineData("OpenAI.Codex_26.1.0.0_x64__2p2nqsd0c76g0", "OpenAI.Codex_2p2nqsd0c76g0")]
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

        Assert.Equal("Zq.Codex_2p2nqsd0c76g0", apps.Find("Zq Codex")?.FamilyName);
        Assert.Null(apps.Find("Zq"));
        Assert.Null(apps.Find("Zq Codex Pro"));
    }

    [Fact]
    public void The_first_application_with_an_executable_inside_the_package_is_taken()
    {
        var apps = Apps(Package());

        var application = apps.FirstApplication(apps.Find("Zq Codex")!);

        Assert.Equal("App", application?.Id);
        Assert.EndsWith(Path.Combine("app", "Zq.exe"), application?.ExecutablePath);
    }

    [Fact]
    public void An_application_the_filter_refuses_is_passed_over_for_the_next_one()
    {
        var apps = Apps(Package());

        var application = apps.FirstApplication(apps.Find("Zq Codex")!, path => !path.EndsWith("Zq.exe"));

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

        var application = apps.FirstApplication(apps.Find("Zq Codex")!);

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

        var application = apps.FirstApplication(apps.Find("Zq Codex")!);

        Assert.Equal("Cli", application?.Id);
        Directory.Delete(link);
    }

    [Fact]
    public void An_executable_that_is_not_there_or_not_an_exe_is_ignored()
    {
        var apps = Apps(Package(createFirst: false));

        Assert.Equal("Cli", apps.FirstApplication(apps.Find("Zq Codex")!)?.Id);
    }
}
