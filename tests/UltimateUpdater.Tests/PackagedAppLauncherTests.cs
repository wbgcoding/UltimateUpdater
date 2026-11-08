using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

/// <summary>
/// A Store app starts through explorer with its application id. The launcher accepts exactly that one
/// shape and nothing else that starts with "shell:", because the shell scheme also reaches folders,
/// control panels and arbitrary verbs.
/// </summary>
public class PackagedAppLauncherTests
{
    private const string Valid = @"shell:AppsFolder\Contoso.Pad_2p2nqsd0c76g0!App";

    [Fact]
    public void A_family_and_application_id_make_the_apps_folder_target()
    {
        Assert.Equal(Valid, PackagedAppLauncher.TargetFor("Contoso.Pad_2p2nqsd0c76g0", "App"));
        Assert.True(PackagedAppLauncher.IsValidTarget(Valid));
    }

    [Theory]
    [InlineData(null, "App")]
    [InlineData("Contoso.Pad_2p2nqsd0c76g0", null)]
    [InlineData("Contoso.Pad_2p2nqsd0c76g0", "")]
    [InlineData("Contoso.Pad_2p2nqsd0c76g0", "App Name")]
    [InlineData("Contoso.Pad_2p2nqsd0c76g0", "App!Two")]
    [InlineData("Contoso.Pad_2p2nqsd0c76g0", "..\\x")]
    [InlineData("Contoso.Pad_2P2NQSD0C76G0", "App")]
    [InlineData("Contoso.Pad", "App")]
    [InlineData("Open AI_2p2nqsd0c76g0", "App")]
    public void A_missing_or_malformed_part_gives_no_target(string? family, string? applicationId) =>
        Assert.Null(PackagedAppLauncher.TargetFor(family, applicationId));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("shell:Desktop")]
    [InlineData("shell:AppsFolder")]
    [InlineData(@"shell:AppsFolder\\")]
    [InlineData(@"shell:AppsFolder\..\x")]
    [InlineData(@"shell:AppsFolder\Contoso.Pad_2p2nqsd0c76g0!App ")]
    [InlineData("shell:AppsFolder\\Contoso.Pad_2p2nqsd0c76g0!App\n")]
    [InlineData("shell:AppsFolder\\Contoso.Pad_2p2nqsd0c76g0!App\n--extra")]
    [InlineData(@"shell:AppsFolder\Contoso.Pad_2p2nqsd0c76g0!App C:\Windows\System32\cmd.exe")]
    [InlineData(@"shell:AppsFolder\Contoso.Pad_2p2nqsd0c76g0!App,/select")]
    [InlineData(@"shell:AppsFolder\Contoso.Pad_2P2NQSD0C76G0!App")]
    [InlineData(@"shell:AppsFolder\Contoso.Pad_2p2nqsd0c76g0")]
    [InlineData(@"shell:AppsFolder\Contoso.Pad_2p2nqsd0c76g0!")]
    [InlineData(@"shell:::{26EE0668-A00A-44D7-9371-BEB064C98683}")]
    [InlineData(@"shell:AppsFolder\Contoso.Pad_2p2nqsd0c76g0!App\..\..")]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    [InlineData(@"\\server\share\x.exe")]
    [InlineData("https://example.com/")]
    public void Anything_but_the_exact_application_id_form_is_refused(string? target)
    {
        Assert.False(PackagedAppLauncher.IsValidTarget(target));
        Assert.Null(PackagedAppLauncher.BuildStartInfo(target ?? "", @"C:\Windows"));
    }

    [Fact]
    public void The_scheme_casing_is_recognised_but_only_the_exact_pattern_is_started()
    {
        Assert.True(PackagedAppLauncher.IsPackagedAppTarget(Valid.ToUpperInvariant()));
        Assert.False(PackagedAppLauncher.IsPackagedAppTarget(@"shell:Desktop"));
        Assert.False(PackagedAppLauncher.IsPackagedAppTarget(@"C:\Tools\x.exe"));
        Assert.False(PackagedAppLauncher.IsPackagedAppTarget(null));
        Assert.False(PackagedAppLauncher.IsValidTarget(@"SHELL:APPSFOLDER\Contoso.Pad_2p2nqsd0c76g0!App"));
    }

    [Fact]
    public void The_start_is_explorer_from_the_windows_folder_with_the_target_as_the_only_argument()
    {
        var info = PackagedAppLauncher.BuildStartInfo(Valid, @"C:\Windows");

        Assert.NotNull(info);
        Assert.Equal(Path.Combine(@"C:\Windows", "explorer.exe"), info!.FileName);
        Assert.True(info.UseShellExecute);
        Assert.Equal(Valid, info.Arguments);
        Assert.Empty(info.ArgumentList);
        Assert.Empty(info.Verb);
    }

    [Fact]
    public void The_uri_launcher_still_refuses_the_shell_scheme()
    {
        Assert.False(UriLauncher.IsLaunchable(Valid));
        Assert.False(UriLauncher.IsLaunchable("shell:Desktop"));
    }

    [Fact]
    public void A_row_with_an_application_id_target_offers_start()
    {
        var row = new UpdateRowViewModel(new UpdateItem
        {
            Id = "Contoso.Pad",
            SourceId = "winget",
            Category = UpdateCategory.Software,
            Name = "Pad",
        })
        {
            LaunchTargetPath = Valid,
        };

        Assert.True(row.CanStart);
    }
}
