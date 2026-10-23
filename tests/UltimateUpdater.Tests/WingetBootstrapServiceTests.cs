using System.Net.Http;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Security;

namespace UltimateUpdater.Tests;

public class WingetBootstrapServiceTests
{
    private const string ReleaseWithAsset = """
        {
            "tag_name": "v1.10.340",
            "draft": false,
            "prerelease": false,
            "assets": [
                {"name": "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle",
                 "browser_download_url": "https://github.com/microsoft/winget-cli/releases/download/v1.10.340/Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle",
                 "url": "https://api.github.com/repos/microsoft/winget-cli/releases/assets/1"},
                {"name": "DesktopAppInstaller_Dependencies.zip",
                 "browser_download_url": "https://github.com/microsoft/winget-cli/releases/download/v1.10.340/DesktopAppInstaller_Dependencies.zip",
                 "url": "https://api.github.com/repos/microsoft/winget-cli/releases/assets/2"}
            ]
        }
        """;

    [Fact]
    public void A_real_release_yields_the_bundle_url()
    {
        var url = WingetBootstrapService.SelectAssetUrl(ReleaseWithAsset);

        Assert.Equal(
            "https://github.com/microsoft/winget-cli/releases/download/v1.10.340/Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle",
            url);
    }

    [Fact]
    public void A_release_without_the_bundle_asset_yields_null()
    {
        const string release = """
            {"tag_name": "v1.10.340", "assets": [{"name": "readme.txt", "browser_download_url": "https://github.com/x"}]}
            """;

        Assert.Null(WingetBootstrapService.SelectAssetUrl(release));
    }

    [Fact]
    public void Malformed_json_yields_null_rather_than_throwing() =>
        Assert.Null(WingetBootstrapService.SelectAssetUrl("not json at all"));

    [Fact]
    public void A_draft_release_yields_null_even_if_it_carries_the_asset()
    {
        const string draft = """
            {"tag_name": "v1.11.0", "draft": true,
             "assets": [{"name": "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle",
                         "browser_download_url": "https://github.com/x"}]}
            """;

        Assert.Null(WingetBootstrapService.SelectAssetUrl(draft));
    }

    [Fact]
    public void The_real_Microsoft_signer_subject_is_recognised()
    {
        const string subject = "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";
        Assert.True(WingetBootstrapService.IsMicrosoftOrganization(subject));
    }

    [Fact]
    public void Microsoft_mentioned_only_in_an_unrelated_component_is_refused()
    {
        const string subject = "CN=Some Other App, OU=Microsoft Partner Network, O=SomeOtherCompany Inc, C=US";
        Assert.False(WingetBootstrapService.IsMicrosoftOrganization(subject));
    }

    [Fact]
    public void A_subject_with_no_microsoft_mention_at_all_is_refused()
    {
        const string subject = "CN=Contoso App, O=Contoso Ltd, C=US";
        Assert.False(WingetBootstrapService.IsMicrosoftOrganization(subject));
    }

    [Fact]
    public void Malformed_subject_text_is_refused_rather_than_throwing() =>
        Assert.False(WingetBootstrapService.IsMicrosoftOrganization("not a distinguished name at all ==="));

    [Fact]
    public void An_unoffered_missing_winget_needs_an_offer() =>
        Assert.True(WingetBootstrapService.ShouldOffer(alreadyOfferedThisSession: false, wingetIsPresent: false));

    [Fact]
    public void Already_offered_this_session_never_needs_another_offer_even_if_still_missing() =>
        Assert.False(WingetBootstrapService.ShouldOffer(alreadyOfferedThisSession: true, wingetIsPresent: false));

    [Fact]
    public void A_present_winget_never_needs_an_offer_even_if_never_asked() =>
        Assert.False(WingetBootstrapService.ShouldOffer(alreadyOfferedThisSession: false, wingetIsPresent: true));

    [Fact]
    public async Task OfferAsync_is_a_harmless_no_op_once_winget_is_present()
    {
        // winget IS present on the machine running this test, so NeedsOffer is false from the very start
        // and OfferAsync must return immediately without touching the network or showing a dialog.
        var service = new WingetBootstrapService(new HttpClient(), new LoggingService());

        Assert.False(service.NeedsOffer);
        await service.OfferAsync(CancellationToken.None);
    }

    [Fact]
    public void The_install_command_never_contains_the_package_path()
    {
        const string path = @"C:\T\a$(calc)`b\x.msixbundle";

        var (arguments, environment) = WingetBootstrapService.BuildInstallCommand(path);

        var command = arguments[^1];
        Assert.Equal("Add-AppxPackage -Path $env:UU_BOOTSTRAP_PACKAGE", command);
        Assert.DoesNotContain(path, command, StringComparison.Ordinal);
        Assert.DoesNotContain("$(", command, StringComparison.Ordinal);
        Assert.Equal(path, environment[WingetBootstrapService.PackagePathVariable]);
        Assert.False(CommandDenyList.IsDenied("powershell", arguments, out _));
    }
}
