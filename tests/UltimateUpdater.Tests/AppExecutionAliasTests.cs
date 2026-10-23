using System.IO;
using System.Text;
using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

/// <summary>
/// The elevated launch of winget through its app-execution alias: the alias is a reparse point any process
/// of this user can create, so its CONTENT decides.
/// </summary>
public class AppExecutionAliasTests
{
    // A complete REPARSE_DATA_BUFFER read from a real winget alias with FSCTL_GET_REPARSE_POINT: tag
    // 0x8000001B, data length 0x18E, version 3, then package family, AUMID, target path and app type "0".
    private const string RealAliasHex = "1b0000808e010000030000004d006900630072006f0073006f00660074002e004400650073006b0074006f00700041007000700049006e007300740061006c006c00650072005f003800770065006b0079006200330064003800620062007700650000004d006900630072006f0073006f00660074002e004400650073006b0074006f00700041007000700049006e007300740061006c006c00650072005f003800770065006b007900620033006400380062006200770065002100770069006e00670065007400000043003a005c00500072006f006700720061006d002000460069006c00650073005c00570069006e0064006f007700730041007000700073005c004d006900630072006f0073006f00660074002e004400650073006b0074006f00700041007000700049006e007300740061006c006c00650072005f0031002e00320039002e003300380030002e0030005f007800360034005f005f003800770065006b007900620033006400380062006200770065005c00770069006e006700650074002e00650078006500000030000000";

    private const string Family = "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe";
    private const string RealTarget = @"C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_1.29.380.0_x64__8wekyb3d8bbwe\winget.exe";

    private static readonly string[] DefaultRoots = [@"C:\Program Files\WindowsApps"];

    private static byte[] Real() => Convert.FromHexString(RealAliasHex);

    /// <summary>Builds a forged buffer in the same shape as the real one.</summary>
    private static byte[] Forge(uint tag = 0x8000001B, uint version = 3, params string[] strings)
    {
        var data = new List<byte>(BitConverter.GetBytes(version));
        foreach (var text in strings)
        {
            data.AddRange(Encoding.Unicode.GetBytes(text));
            data.AddRange([0, 0]);
        }

        var buffer = new List<byte>(BitConverter.GetBytes(tag));
        buffer.AddRange(BitConverter.GetBytes((ushort)data.Count));
        buffer.AddRange([0, 0]);
        buffer.AddRange(data);
        return [.. buffer];
    }

    private static AppExecutionAlias.Link? Parse(byte[] buffer)
    {
        return AppExecutionAlias.TryParse(buffer, out var link) ? link : null;
    }

    [Fact]
    public void The_real_alias_buffer_is_accepted()
    {
        var link = Parse(Real());

        Assert.NotNull(link);
        Assert.Equal(Family, link!.PackageFamily);
        Assert.Equal(Family + "!winget", link.AppUserModelId);
        Assert.Equal(RealTarget, link.Target);
        Assert.True(AppExecutionAlias.IsWingetLink(link, DefaultRoots));
    }

    [Fact]
    public void A_buffer_with_another_reparse_tag_is_rejected()
    {
        var buffer = Real();
        BitConverter.GetBytes(0xA0000003u).CopyTo(buffer, 0);   // IO_REPARSE_TAG_MOUNT_POINT

        Assert.Null(Parse(buffer));
    }

    [Theory]
    [InlineData(2u)]
    [InlineData(4u)]
    [InlineData(0u)]
    public void A_buffer_of_another_version_is_rejected(uint version)
    {
        Assert.Null(Parse(Forge(version: version, strings: [Family, Family + "!winget", RealTarget, "0"])));
    }

    [Fact]
    public void A_buffer_with_fewer_than_three_strings_is_rejected()
    {
        Assert.Null(Parse(Forge(strings: [Family, Family + "!winget"])));
    }

    [Fact]
    public void A_buffer_with_an_empty_string_is_rejected()
    {
        Assert.Null(Parse(Forge(strings: [Family, string.Empty, RealTarget, "0"])));
    }

    [Fact]
    public void A_truncated_or_overlong_buffer_is_rejected()
    {
        var real = Real();

        Assert.Null(Parse(real[..7]));
        Assert.Null(Parse(real[..(real.Length - 40)]));   // data length claims more than is there
        Assert.Null(Parse([]));

        // The last string is not terminated inside the declared data.
        var unterminated = Forge(strings: [Family, Family + "!winget", RealTarget])[..^2];
        BitConverter.GetBytes((ushort)(unterminated.Length - 8)).CopyTo(unterminated, 4);
        Assert.Null(Parse(unterminated));
    }

    [Fact]
    public void Surplus_strings_after_the_third_are_ignored()
    {
        var link = Parse(Forge(strings: [Family, Family + "!winget", RealTarget, "0", "extra", "more"]));

        Assert.NotNull(link);
        Assert.True(AppExecutionAlias.IsWingetLink(link!, DefaultRoots));
    }

    [Theory]
    [InlineData("Microsoft.DesktopAppInstaller_8wekyb3d8bbwf")]
    [InlineData("Evil.DesktopAppInstaller_8wekyb3d8bbwe")]
    [InlineData("Microsoft.WindowsTerminal_8wekyb3d8bbwe")]
    public void A_link_to_another_package_family_is_rejected(string family)
    {
        var link = new AppExecutionAlias.Link(family, family + "!winget", RealTarget);

        Assert.False(AppExecutionAlias.IsWingetLink(link, DefaultRoots));
    }

    [Fact]
    public void An_app_user_model_id_that_is_not_the_familys_winget_is_rejected()
    {
        var link = new AppExecutionAlias.Link(Family, Family + "!Other", RealTarget);

        Assert.False(AppExecutionAlias.IsWingetLink(link, DefaultRoots));
    }

    [Theory]
    [InlineData(@"C:\Users\Public\Microsoft.DesktopAppInstaller_1.0.0.0_x64__8wekyb3d8bbwe\winget.exe")]
    [InlineData(@"C:\Program Files\WindowsApps\Evil_1.0.0.0_x64__8wekyb3d8bbwe\winget.exe")]
    [InlineData(@"C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_1.0.0.0_x64__8wekyb3d8bbwe\evil.exe")]
    [InlineData(@"C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_1.0.0.0_x64__8wekyb3d8bbwe\sub\winget.exe")]
    [InlineData(@"C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_1.0.0.0_x64__8wekyb3d8bbwe\..\..\Evil\winget.exe")]
    [InlineData(@"C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_1.0.0.0_x64__8wekyb3d8bbwe\..\winget.exe")]
    [InlineData(@"C:\Program Files\WindowsApps\Sub\Microsoft.DesktopAppInstaller_1.0.0.0_x64__8wekyb3d8bbwe\winget.exe")]
    [InlineData(@"\\server\share\WindowsApps\Microsoft.DesktopAppInstaller_1.0.0.0_x64__8wekyb3d8bbwe\winget.exe")]
    [InlineData(@"\\?\C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_1.0.0.0_x64__8wekyb3d8bbwe\winget.exe")]
    [InlineData(@"WindowsApps\Microsoft.DesktopAppInstaller_1.0.0.0_x64__8wekyb3d8bbwe\winget.exe")]
    [InlineData("")]
    public void A_link_whose_target_is_not_winget_in_the_package_store_is_rejected(string target)
    {
        var link = new AppExecutionAlias.Link(Family, Family + "!winget", target);

        Assert.False(AppExecutionAlias.IsWingetLink(link, DefaultRoots));
    }

    [Fact]
    public void A_package_store_moved_to_another_volume_is_accepted_only_when_it_is_a_known_root()
    {
        const string target = @"D:\WindowsApps\Microsoft.DesktopAppInstaller_1.29.380.0_x64__8wekyb3d8bbwe\winget.exe";
        var link = new AppExecutionAlias.Link(Family, Family + "!winget", target);

        Assert.False(AppExecutionAlias.IsWingetLink(link, DefaultRoots));
        Assert.True(AppExecutionAlias.IsWingetLink(link, [@"C:\Program Files\WindowsApps", @"D:\WindowsApps\"]));
    }

    [Fact]
    public void No_known_root_accepts_nothing()
    {
        Assert.False(AppExecutionAlias.IsWingetLink(Parse(Real())!, []));
        Assert.False(AppExecutionAlias.IsWingetLink(Parse(Real())!, [string.Empty]));
    }

    [Fact]
    public void A_plain_file_is_not_an_alias()
    {
        var directory = Directory.CreateTempSubdirectory("wu-alias-");
        try
        {
            var file = Path.Combine(directory.FullName, "winget.exe");
            File.WriteAllText(file, "not a reparse point");

            using var handle = AppExecutionAlias.TryOpen(file, out var buffer);

            Assert.Null(handle);
            Assert.Null(buffer);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public void The_real_alias_on_this_machine_reads_and_verifies_against_the_known_roots()
    {
        var alias = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");
        if (!File.Exists(alias))
            return;   // winget is not installed on this machine

        using var handle = AppExecutionAlias.TryOpen(alias, out var buffer);

        Assert.NotNull(handle);
        Assert.True(AppExecutionAlias.TryParse(buffer!, out var link));
        Assert.True(AppExecutionAlias.IsWingetLink(link!, AppExecutionAlias.PackageRoots()));
    }

    [Fact]
    public async Task The_real_alias_still_launches_while_its_handle_is_held_open()
    {
        var alias = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");
        if (!File.Exists(alias))
            return;   // winget is not installed on this machine

        // The handle that keeps the checked content from being swapped must not block the launch itself.
        using var handle = AppExecutionAlias.TryOpen(alias, out _);
        Assert.NotNull(handle);

        var result = await ProcessRunner.RunAsync(alias, ["--version"], TimeSpan.FromSeconds(60), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.StartsWith("v", result.StandardOutput.Trim());
    }

    [Fact]
    public void An_alias_launch_still_starts_in_system32_and_both_pins_are_released_on_dispose()
    {
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var targetPin = new FileStream(cmd, FileMode.Open, FileAccess.Read, FileShare.Read);
        var aliasPin = File.OpenHandle(cmd, FileMode.Open, FileAccess.Read, FileShare.Read);

        var executable = new ResolvedExecutable(
            Path.Combine(Path.GetTempPath(), "alias", "winget.exe"), null, targetPin, aliasPin);

        var info = ProcessRunner.BuildStartInfo(
            executable, ["--version"], null, Encoding.UTF8, elevated: true, () => string.Empty, _ => true);

        Assert.Equal(Environment.SystemDirectory, info.WorkingDirectory, ignoreCase: true);

        executable.Dispose();
        Assert.True(aliasPin.IsClosed);
        Assert.False(targetPin.CanRead);
    }
}
