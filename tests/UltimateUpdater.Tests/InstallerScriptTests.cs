using System.Text.RegularExpressions;

namespace UltimateUpdater.Tests;

/// <summary>
/// Guards the parts of the Inno script that no build error would ever catch, such as the autostart
/// value, which must stay written by the app, not by Setup.
/// </summary>
public class InstallerScriptTests
{
    private static string Script()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "UltimateUpdater.slnx")))
            directory = directory.Parent;

        Assert.True(directory is not null, "the repository root is not above the test binaries any more");
        return File.ReadAllText(Path.Combine(directory!.FullName, "installer", "UltimateUpdater.iss"));
    }

    private static string Csproj()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "UltimateUpdater.slnx")))
            directory = directory.Parent;

        Assert.True(directory is not null, "the repository root is not above the test binaries any more");
        return File.ReadAllText(Path.Combine(directory!.FullName, "src", "UltimateUpdater", "UltimateUpdater.csproj"));
    }

    /// <summary>
    /// The publisher name is spelled out independently in three places (csproj Company/Copyright,
    /// the .iss AppPublisher define) and drifted apart once already (wbgcoding vs. BGCoding), this
    /// pins all three to the same name so a future rename cannot silently update one and miss the others.
    /// </summary>
    [Fact]
    public void The_installer_publisher_matches_the_csprojs_company_and_copyright()
    {
        var publisherMatch = Regex.Match(Script(), @"#define AppPublisher ""([^""]+)""");
        var companyMatch = Regex.Match(Csproj(), @"<Company>([^<]+)</Company>");
        var copyrightMatch = Regex.Match(Csproj(), @"<Copyright>([^<]+)</Copyright>");

        Assert.True(publisherMatch.Success, "the installer no longer defines AppPublisher");
        Assert.True(companyMatch.Success, "the csproj no longer sets <Company>");
        Assert.True(copyrightMatch.Success, "the csproj no longer sets <Copyright>");

        Assert.Equal(companyMatch.Groups[1].Value, publisherMatch.Groups[1].Value);
        Assert.Contains(publisherMatch.Groups[1].Value, copyrightMatch.Groups[1].Value);
    }

    [Fact]
    public void Setup_never_writes_the_autostart_value_itself()
    {
        var runEntries = Regex.Matches(
            Script(),
            @"Root:\s*HKCU;[^\r\n]*CurrentVersion\\Run[^\r\n]*",
            RegexOptions.IgnoreCase);

        Assert.All(runEntries, entry => Assert.DoesNotContain("ValueData", entry.Value, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_app_is_asked_to_set_autostart_as_the_installing_user()
    {
        // Inno continues an entry over several lines with a trailing backslash; joining them first means
        // this reads one entry rather than a fragment of one.
        var entry = Script()
            .Replace("\\\r\n", " ")
            .Replace("\\\n", " ")
            .Split('\n')
            .FirstOrDefault(l => l.Contains("--set-autostart", StringComparison.OrdinalIgnoreCase)
                                 && !l.TrimStart().StartsWith(';'));

        Assert.True(entry is not null, "the installer no longer asks the app to set autostart");
        Assert.Contains("runasoriginaluser", entry!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Tasks: autostart", entry, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not IsPortableMode", entry, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Round 5 (measured 22.08. on the VM): the AttachThreadInput trick alone still lost the foreground
    /// on the very first page. ForceForeground now checks whether it actually worked and, only then,
    /// falls back to SwitchToThisWindow + a TOPMOST/NOTOPMOST toggle.
    /// </summary>
    [Fact]
    public void ForceForeground_falls_back_to_SwitchToThisWindow_when_SetForegroundWindow_alone_fails()
    {
        var script = Script();

        Assert.Contains("SwitchToThisWindow", script);
        Assert.Contains("HWND_TOPMOST", script);
        Assert.Contains("HWND_NOTOPMOST", script);

        var forceForeground = Regex.Match(script, @"procedure ForceForeground\(Wnd: HWND\);.*?^end;", RegexOptions.Singleline | RegexOptions.Multiline);
        Assert.True(forceForeground.Success, "ForceForeground procedure body not found");
        // The fallback must be GUARDED, only fired when SetForegroundWindow measurably did not win the
        // slot, never unconditionally (that would fight a window that already has the foreground).
        Assert.Contains("if GetForegroundWindow <> Wnd", forceForeground.Value);
        Assert.Contains("SwitchToThisWindow", forceForeground.Value);
    }

    /// <summary>BringWizardToFront must still fire exactly once, a repeated call would leave Setup
    /// topmost for the rest of the run (round 4's regression).</summary>
    [Fact]
    public void BringWizardToFront_guards_itself_with_FrontRequested()
    {
        var bringToFront = Regex.Match(Script(), @"procedure BringWizardToFront;.*?^end;", RegexOptions.Singleline | RegexOptions.Multiline);
        Assert.True(bringToFront.Success, "BringWizardToFront procedure body not found");
        Assert.Contains("if FrontRequested then", bringToFront.Value);
        Assert.Contains("Exit;", bringToFront.Value);
    }

    /// <summary>
    /// The Restart Manager restarts whatever it closed as soon as the files are copied, which
    /// is BEFORE the Finish page - the app came back while the wizard was still open. The two deliberate
    /// ways back (the Finish page's own checkbox, and the self-updater's explicit /RELAUNCH=1) must stay
    /// the only ones; CloseApplications stays "yes", that half is what makes the file replacement work.
    /// </summary>
    [Fact]
    public void Setup_never_restarts_the_app_through_the_restart_manager()
    {
        var script = Script();

        Assert.Contains("RestartApplications=no", script);
        Assert.DoesNotContain("RestartApplications=yes", script);
        Assert.Contains("CloseApplications=yes", script);
    }

    /// <summary>
    /// Install mode must not carry the self-extracting exe (its natives would unpack into a user-owned
    /// temp folder), portable mode must keep it, and the installer stage's .pdb must never be shipped.
    /// </summary>
    [Fact]
    public void Install_mode_takes_the_non_extracting_stage_and_portable_mode_keeps_the_single_file()
    {
        var joined = Script().Replace("\\\r\n", " ").Replace("\\\n", " ");
        var files = joined.Split('\n').Where(line => line.TrimStart().StartsWith("Source:", StringComparison.Ordinal)).ToList();

        var installerStage = files.Where(line => line.Contains("installer-win-", StringComparison.Ordinal)).ToList();
        Assert.Equal(4, installerStage.Count);
        Assert.All(installerStage, line => Assert.Contains("(not IsPortableMode)", line));
        Assert.All(installerStage, line => Assert.DoesNotContain(".pdb", line));
        Assert.All(installerStage, line => Assert.True(
            line.Contains("UltimateUpdater.exe", StringComparison.Ordinal) || line.Contains("*.dll", StringComparison.Ordinal),
            "only the exe and the DLLs of the installer stage may be listed"));

        var portable = files.Where(line => line.Contains("publish\\win-", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, portable.Count);
        Assert.All(portable, line => Assert.Contains("IsPortableMode and", line));
    }

    [Fact]
    public void The_build_publishes_the_installer_stage_without_self_extraction_and_signs_it()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "UltimateUpdater.slnx")))
            directory = directory.Parent;
        var build = File.ReadAllText(Path.Combine(directory!.FullName, "build.bat"));

        Assert.Contains("IncludeNativeLibrariesForSelfExtract=false", build);
        Assert.Contains("installer-win-%%a", build);
        Assert.Contains("installer-win-x64\\UltimateUpdater.exe\" \"%STAGE%\\installer-win-arm64\\UltimateUpdater.exe", build);
    }
}
