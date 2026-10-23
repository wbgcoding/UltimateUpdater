using System.IO;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

/// <summary>
/// The rule behind these: a tool that updates ITSELF only deserves a row when nothing else is going to
/// update it. An executable living inside a
/// package manager's own store belongs to that manager.
/// </summary>
public class PackageManagerOwnershipTests
{
    private static string LocalAppData =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static string UserProfile =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string ProgramData =>
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    [Fact]
    public void A_binary_in_wingets_package_store_belongs_to_winget()
    {
        var path = Path.Combine(
            LocalAppData, "Microsoft", "WinGet", "Packages",
            "DenoLand.Deno_Microsoft.Winget.Source_8wekyb3d8bbwe", "deno.exe");

        Assert.True(PackageManagerOwnership.IsManagedElsewhere(path));
    }

    /// <summary>
    /// winget's Links folder holds a stub per portable package, and PATH resolution finds the stub rather
    /// than the payload, so the stub has to count too. Measured on the dev host: `uv` resolves exactly
    /// there.
    /// </summary>
    [Fact]
    public void A_winget_shim_belongs_to_winget_too()
    {
        var path = Path.Combine(LocalAppData, "Microsoft", "WinGet", "Links", "uv.exe");

        Assert.True(PackageManagerOwnership.IsManagedElsewhere(path));
    }

    [Fact]
    public void Scoop_and_chocolatey_directories_count_as_well()
    {
        Assert.True(PackageManagerOwnership.IsManagedElsewhere(
            Path.Combine(UserProfile, "scoop", "apps", "deno", "current", "deno.exe")));

        Assert.True(PackageManagerOwnership.IsManagedElsewhere(
            Path.Combine(UserProfile, "scoop", "shims", "deno.exe")));

        Assert.True(PackageManagerOwnership.IsManagedElsewhere(
            Path.Combine(ProgramData, "chocolatey", "bin", "deno.exe")));
    }

    /// <summary>
    /// The case the self-update row exists FOR: installed by the tool's own script into the user's profile,
    /// where no package manager can see it.
    /// </summary>
    [Fact]
    public void A_binary_installed_by_its_own_script_belongs_to_nobody()
    {
        Assert.False(PackageManagerOwnership.IsManagedElsewhere(
            Path.Combine(UserProfile, ".bun", "bin", "bun.exe")));

        Assert.False(PackageManagerOwnership.IsManagedElsewhere(
            Path.Combine(UserProfile, ".cargo", "bin", "cargo.exe")));

        Assert.False(PackageManagerOwnership.IsManagedElsewhere(@"C:\flutter\bin\flutter.bat"));
    }

    /// <summary>A root must not match a sibling that merely starts with its name.</summary>
    [Fact]
    public void A_name_prefix_is_not_containment()
    {
        Assert.False(PackageManagerOwnership.IsManagedElsewhere(
            Path.Combine(UserProfile, "scoop", "appsomething", "deno.exe")));
    }

    /// <summary>Unknown beats wrong: no path means no claim, and the row is shown.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_unknown_path_claims_nothing(string? path)
    {
        Assert.False(PackageManagerOwnership.IsManagedElsewhere(path));
    }
}
