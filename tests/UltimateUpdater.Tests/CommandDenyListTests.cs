using UltimateUpdater.Services.Security;
using Xunit;

namespace UltimateUpdater.Tests;

/// <summary>
/// ProcessRunner's hard backstop: this app must never issue a command that formats/partitions
/// a drive, touches boot config, disables Defender, or deletes inside the Windows system directory,
/// regardless of caller. Every current caller only ever issues package-manager-style commands, so none
/// of this should fire in practice; these tests exist so it is proven, not assumed.
/// </summary>
public class CommandDenyListTests
{
    [Theory]
    [InlineData(@"C:\Windows\System32\format.com")]
    [InlineData("format.exe")]
    [InlineData(@"C:\Windows\System32\diskpart.exe")]
    [InlineData("bcdedit.exe")]
    [InlineData("bootrec.exe")]
    [InlineData("vssadmin.exe")]
    public void Always_denied_executables_are_refused_regardless_of_arguments(string executable)
    {
        Assert.True(CommandDenyList.IsDenied(executable, ["/help"], out var reason));
        Assert.NotEmpty(reason);
    }

    [Theory]
    [InlineData("powershell.exe", "Set-MpPreference -DisableRealtimeMonitoring $true")]
    [InlineData("pwsh.exe", "Set-MpPreference -DisableIOAVProtection $true")]
    [InlineData("sc.exe", "config WinDefend start= disabled")]
    [InlineData("sc.exe", "stop WinDefend")]
    public void Defender_disabling_command_lines_are_refused(string executable, string argument)
    {
        Assert.True(CommandDenyList.IsDenied(executable, [argument], out var reason));
        Assert.Contains("defender", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Deleting_inside_the_windows_directory_via_powershell_is_refused()
    {
        var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.True(CommandDenyList.IsDenied(
            "powershell.exe", [$"Remove-Item -Recurse -Force {windir}\\System32"], out var reason));
        Assert.Contains("system directory", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("winget.exe", "upgrade --id Mozilla.Firefox --silent")]
    [InlineData("choco.exe", "upgrade firefox -y")]
    [InlineData("powershell.exe", "Update-Module -Name PSReadLine")]
    [InlineData("npm.cmd", "update -g eslint")]
    public void Ordinary_package_manager_commands_are_never_denied(string executable, string argumentLine)
    {
        Assert.False(CommandDenyList.IsDenied(executable, argumentLine.Split(' '), out _));
    }

    [Fact]
    public void Removing_an_ordinary_user_file_via_powershell_is_not_denied()
    {
        // "remove-item" alone must not be enough, only the combination with a Windows-directory path.
        Assert.False(CommandDenyList.IsDenied(
            "powershell.exe", [@"Remove-Item -Force C:\Users\Someone\AppData\Local\Temp\update.tmp"], out _));
    }

    [Theory]
    [InlineData("diskpart")]
    [InlineData("DISKPART")]
    [InlineData("diskpart.exe")]
    [InlineData("diskpart.exe.")]
    [InlineData("diskpart.exe::$DATA")]
    [InlineData(@"C:\Windows\System32\diskpart")]
    [InlineData("format")]
    [InlineData("format.com")]
    [InlineData("vssadmin")]
    [InlineData("bcdedit")]
    public void Denied_names_match_with_or_without_an_extension_or_trailing_noise(string executable)
    {
        Assert.True(CommandDenyList.IsDenied(executable, ["/help"], out _));
    }

    [Theory]
    [InlineData("sc", "stop WinDefend")]
    [InlineData("sc.exe", "stop WinDefend")]
    [InlineData("powershell", "Set-MpPreference -DisableRealtimeMonitoring $true")]
    [InlineData("pwsh.exe", "Set-MpPreference -DisableRealtimeMonitoring $true")]
    public void Defender_disabling_is_refused_for_names_without_an_extension(string executable, string argument)
    {
        Assert.True(CommandDenyList.IsDenied(executable, [argument], out _));
    }

    [Theory]
    [InlineData("diskpartx.exe", "/help")]
    [InlineData("format-helper.exe", "/help")]
    [InlineData("winget", "upgrade --all")]
    [InlineData("scoop.cmd", "update")]
    [InlineData("sc.exe", "query")]
    public void Look_alike_names_are_not_denied(string executable, string argumentLine)
    {
        Assert.False(CommandDenyList.IsDenied(executable, argumentLine.Split(' '), out _));
    }
}
