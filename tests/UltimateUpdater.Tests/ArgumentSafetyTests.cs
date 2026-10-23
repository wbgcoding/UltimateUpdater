using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

/// <summary>
/// Package names and launch targets both come from outside the app, tool output and provider
/// tables. These two guards are what stop such a value from being read as something other than the
/// plain name it is supposed to be.
/// </summary>
public class ArgumentSafetyTests
{
    [Theory]
    [InlineData("Mozilla.Firefox")]
    [InlineData("7zip.7zip")]
    [InlineData("eslint")]
    [InlineData("Microsoft.DotNet.SDK.9")]
    [InlineData("9WZDNCRFHVQM")]
    public void Ordinary_package_names_are_accepted(string packageId)
    {
        Assert.True(ProcessRunner.IsSafePositionalArgument(packageId));
    }

    /// <summary>npm scopes are the one legitimate leading '@', rejecting them would make every scoped global unupdatable.</summary>
    [Theory]
    [InlineData("@angular/cli")]
    [InlineData("@vue/cli")]
    [InlineData("@nestjs/cli")]
    public void Scoped_npm_packages_are_accepted(string packageId)
    {
        Assert.True(ProcessRunner.IsSafePositionalArgument(packageId));
    }

    [Theory]
    [InlineData("@response.txt")]
    [InlineData("@/leading")]
    [InlineData("@scope/")]
    [InlineData("@scope/../../etc")]
    [InlineData("@scope\\windows")]
    public void An_at_sign_that_is_not_a_scope_is_still_refused(string hostile)
    {
        Assert.False(ProcessRunner.IsSafePositionalArgument(hostile));
    }

    /// <summary>An argument array stops the shell, but the target tool still reads a leading dash as an option.</summary>
    [Theory]
    [InlineData("--index-url=http://attacker.example/simple")]
    [InlineData("-r")]
    [InlineData("/quiet")]
    [InlineData("../../windows/system32")]
    [InlineData("pkg --force")]
    [InlineData("pkg\"quoted")]
    [InlineData("pkg`command")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Values_that_would_be_read_as_options_are_rejected(string? hostile)
    {
        Assert.False(ProcessRunner.IsSafePositionalArgument(hostile));
    }

    /// <summary>
    /// npm, pnpm, composer, gem and pyenv are .cmd/.bat wrappers that run through cmd.exe, and .NET does not
    /// escape cmd's own metacharacters for them: "pkg&amp;calc" would start a second program.
    /// </summary>
    [Theory]
    [InlineData("pkg&calc")]
    [InlineData("pkg|calc")]
    [InlineData("pkg>out.txt")]
    [InlineData("pkg<in.txt")]
    [InlineData("pkg^")]
    [InlineData("%APPDATA%")]
    [InlineData("1.0.0&calc")]
    public void Cmd_metacharacters_are_rejected(string hostile)
    {
        Assert.False(ProcessRunner.IsSafePositionalArgument(hostile));
    }

    /// <summary>winget is a native executable, never run through cmd.exe, and its ids may contain those characters.</summary>
    [Theory]
    [InlineData("Publisher.R&D")]
    [InlineData("Publisher.100%Tool")]
    public void A_native_target_may_opt_out_of_the_cmd_check(string packageId)
    {
        Assert.True(ProcessRunner.IsSafePositionalArgument(packageId, allowCmdMetacharacters: true));
        Assert.False(ProcessRunner.IsSafePositionalArgument("-r", allowCmdMetacharacters: true));
    }

    /// <summary>Legitimate ids and versions of the batch-wrapped ecosystems keep working.</summary>
    [Theory]
    [InlineData("phpunit/phpunit")]
    [InlineData("platforms;android-34")]
    [InlineData("1.0.0+build.5")]
    [InlineData("2.0.0-beta.1")]
    [InlineData("3.12.0-win32")]
    public void Ordinary_ids_and_versions_still_pass_the_cmd_check(string value)
    {
        Assert.True(ProcessRunner.IsSafePositionalArgument(value));
    }

    [Theory]
    [InlineData("steam://validate/440")]
    [InlineData("ms-windows-store://downloadsandupdates")]
    [InlineData("battlenet://")]
    [InlineData("https://www.gog.com/galaxy")]
    public void Vendor_uris_are_launchable(string target)
    {
        Assert.True(UriLauncher.IsLaunchable(target));
    }

    /// <summary>
    /// A bare name would be resolved through ShellExecute's search order, which includes the working
    /// directory, so a planted executable of that name could be launched instead.
    /// </summary>
    [Theory]
    [InlineData("battlenet")]
    [InlineData("steam.exe")]
    [InlineData("")]
    [InlineData("   ")]
    public void Bare_names_are_refused(string target)
    {
        Assert.False(UriLauncher.IsLaunchable(target));
    }

    /// <summary>
    /// A scheme this app does not itself use must never reach the shell: a row's open target can come
    /// straight out of a vendor's API response, and any registered protocol handler would then be one click
    /// away. Only http(s) plus the launcher protocols the Tier B/C rows hand off to are allowed.
    /// </summary>
    [Theory]
    [InlineData("ms-settings:windowsupdate")]
    [InlineData("shell:AppsFolder")]
    [InlineData("search-ms:query=secrets")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("vscode://file/C:/x")]
    public void An_unknown_uri_scheme_is_refused(string target)
    {
        Assert.False(UriLauncher.IsLaunchable(target));
        Assert.False(UriLauncher.IsAllowedUri(target));
    }

    [Fact]
    public void An_absolute_path_is_launchable_only_when_it_exists()
    {
        Assert.True(UriLauncher.IsLaunchable(Path.GetTempPath()));
        Assert.False(UriLauncher.IsLaunchable(Path.Combine(Path.GetTempPath(), "does-not-exist-9d1f.exe")));
    }

    /// <summary>
    /// Everything a window opens goes through <see cref="UriLauncher.LaunchDetached"/>, never a shell-execute
    /// of its own: that call does not return until the shell has taken the target over, and on the UI thread
    /// that is the whole window frozen for as long as it takes (the cleanup dialog's "open folder" did
    /// exactly this with a cold Explorer). Checked on the source text, a freeze is not something a unit
    /// test can time reliably, but the call that causes it is plain to see.
    /// </summary>
    [Fact]
    public void No_window_code_behind_shell_executes_on_its_own()
    {
        var views = FindViewsDirectory();

        foreach (var file in Directory.GetFiles(views, "*.xaml.cs"))
        {
            var source = File.ReadAllText(file);
            Assert.False(
                source.Contains("Process.Start", StringComparison.Ordinal),
                $"{Path.GetFileName(file)} starts a process directly; hand the target to UriLauncher instead.");
        }
    }

    private static string FindViewsDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "UltimateUpdater", "Views");
            if (Directory.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Views directory from the test output path.");
    }
}
