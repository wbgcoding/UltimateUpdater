using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

/// <summary>
/// A winget row is a Store app only with proof: winget's own MSIX id, or a package of that exact name with
/// no classic (Uninstall) entry of the same name. A normal install that merely shares a package's name is not.
/// </summary>
public class PackagedAppWingetRowTests
{
    private const string Full = "Contoso.ChatterPkg_26.1.0.0_x64__2p2nqsd0c76g0";
    private const string Root = @"C:\Program Files\WindowsApps\Contoso.ChatterPkg_26.1.0.0_x64__2p2nqsd0c76g0";
    private const string ArpRoot = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private sealed class Table(params string[] classicNames) : IRegistryView
    {
        public IReadOnlyList<string> SubKeyNames(string keyPath) =>
            keyPath == PackagedApps.PackagesKey ? [Full]
            : keyPath == ArpRoot ? [.. classicNames.Select((_, i) => "App" + i)]
            : [];

        public string? Value(string keyPath, string valueName)
        {
            if (keyPath.StartsWith(PackagedApps.PackagesKey, StringComparison.Ordinal))
                return valueName switch { "DisplayName" => "Chatter", "PackageRootFolder" => Root, _ => null };

            return keyPath.StartsWith(ArpRoot + @"\App", StringComparison.Ordinal) && valueName == "DisplayName"
                ? classicNames[int.Parse(keyPath[(ArpRoot.Length + 4)..])]
                : null;
        }
    }

    [Fact]
    public void The_real_chatgpt_case_stays_packaged_though_its_winget_id_names_another_package()
    {
        var apps = new PackagedApps(new Table("Git", "Visual Studio Code"));

        var package = apps.FindForWingetRow("Contoso.Chatter", "Chatter");

        Assert.NotNull(package);
        Assert.Equal("Contoso.ChatterPkg_2p2nqsd0c76g0", package!.FamilyName);
    }

    [Fact]
    public void A_classic_install_with_the_name_of_a_package_is_not_packaged()
    {
        var apps = new PackagedApps(new Table("Chatter", "Git"));

        Assert.Null(apps.FindForWingetRow("Vendor.ChatterDesktop", "Chatter"));
        Assert.Empty(apps.FindAllForWingetRow("Vendor.ChatterDesktop", "Chatter (64-bit)"));
    }

    [Fact]
    public void A_winget_msix_id_names_the_package_even_when_the_display_name_differs()
    {
        var apps = new PackagedApps(new Table("Chatter"));

        var package = apps.FindForWingetRow(@"MSIX\" + Full, "ChatterPkg");

        Assert.Equal(Root, package?.Root);
    }

    [Fact]
    public void A_winget_msix_id_of_an_unknown_package_falls_back_to_the_name_rule()
    {
        Assert.NotNull(new PackagedApps(new Table()).FindForWingetRow(@"MSIX\Other.App_1.0.0.0_x64__2p2nqsd0c76g0", "Chatter"));
        Assert.Null(new PackagedApps(new Table("Chatter")).FindForWingetRow(@"MSIX\Other.App_1.0.0.0_x64__2p2nqsd0c76g0", "Chatter"));
    }
}
