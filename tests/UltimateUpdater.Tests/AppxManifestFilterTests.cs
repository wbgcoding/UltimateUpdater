using UltimateUpdater.Services.Processes;
using UltimateUpdater.Services.Uninstall;

namespace UltimateUpdater.Tests;

/// <summary>
/// The two PowerShell filters of the Store app listing, run for real against manifest files shaped like those of an
/// installed package (the uap namespace, AppListEntry on VisualElements) and against package objects.
/// </summary>
[Trait("Layer", "Integration")]
public class AppxManifestFilterTests
{
    private static string Fixture(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "UltimateUpdater.slnx")))
            directory = directory.Parent;

        Assert.True(directory is not null, "the repository root is not above the test binaries any more");
        return Path.Combine(directory!.FullName, "tests", "UltimateUpdater.Tests", "Fixtures", name);
    }

    private static async Task<string> RunAsync(string command, string? fixturePath = null)
    {
        var environment = fixturePath is null ? null : new Dictionary<string, string> { ["UU_FIXTURE"] = fixturePath };
        var result = await ProcessRunner.RunAsync(
            "powershell", ["-NoProfile", "-NonInteractive", "-Command", command], TimeSpan.FromSeconds(60), CancellationToken.None, environment);
        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        return result.StandardOutput.Trim();
    }

    private static Task<string> VisibleAsync(string fixture) =>
        RunAsync("$m = [xml](Get-Content -Raw -LiteralPath $env:UU_FIXTURE); " + AppxPackageReader.VisibleAppExpression, Fixture(fixture));

    [Fact]
    public async Task A_package_with_one_openable_app_and_one_hidden_helper_is_visible()
    {
        Assert.Equal("True", await VisibleAsync("appx-manifest-visible-and-hidden.xml"));
    }

    [Fact]
    public async Task A_package_whose_only_app_has_AppListEntry_none_on_its_VisualElements_is_not_visible()
    {
        Assert.Equal("False", await VisibleAsync("appx-manifest-hidden-only.xml"));
    }

    [Fact]
    public async Task Frameworks_resource_packages_bundles_non_removable_and_system_signed_packages_are_dropped_before_the_manifest_is_read()
    {
        const string objects =
            "$packages = @(" +
            "[pscustomobject]@{ N='keep'; IsFramework=$false; IsResourcePackage=$false; IsBundle=$false; NonRemovable=$false; SignatureKind='Store' }," +
            "[pscustomobject]@{ N='framework'; IsFramework=$true; IsResourcePackage=$false; IsBundle=$false; NonRemovable=$false; SignatureKind='Store' }," +
            "[pscustomobject]@{ N='resource'; IsFramework=$false; IsResourcePackage=$true; IsBundle=$false; NonRemovable=$false; SignatureKind='Store' }," +
            "[pscustomobject]@{ N='bundle'; IsFramework=$false; IsResourcePackage=$false; IsBundle=$true; NonRemovable=$false; SignatureKind='Store' }," +
            "[pscustomobject]@{ N='fixed'; IsFramework=$false; IsResourcePackage=$false; IsBundle=$false; NonRemovable=$true; SignatureKind='Store' }," +
            "[pscustomobject]@{ N='system'; IsFramework=$false; IsResourcePackage=$false; IsBundle=$false; NonRemovable=$false; SignatureKind='System' }" +
            "); ";

        var kept = await RunAsync(
            objects + "($packages | Where-Object { " + AppxPackageReader.PackageFilter + " }).N");

        Assert.Equal("keep", kept);
    }
}
