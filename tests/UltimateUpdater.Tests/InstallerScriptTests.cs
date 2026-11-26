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

    /// <summary>
    /// Setup and the uninstaller run PowerShell elevated. Its module path starts with the user's own
    /// Documents folder, so every call has to go through the helper that limits it to the Windows modules.
    /// </summary>
    [Fact]
    public void Elevated_PowerShell_loads_only_the_modules_that_ship_with_Windows()
    {
        var script = Script();

        Assert.Contains("'$env:PSModulePath = $PSHOME + ''\\Modules''; ' + Script", script);
        var calls = Regex.Matches(script, @"Exec\(PowerShellPath,\s*(\S+)");
        Assert.NotEmpty(calls);
        Assert.All(calls, call => Assert.Equal("PowerShellArgs(Script),", call.Groups[1].Value));
        Assert.DoesNotContain("-Command \"' + Script", script);
    }

    /// <summary>
    /// Setup's [Run] entries start the installed exe before ssPostInstall. The marker that tells the app it is
    /// installed must already exist then, or that run takes itself for portable and writes a data folder
    /// beside the exe that the uninstaller leaves behind.
    /// </summary>
    [Fact]
    public void The_installed_marker_is_written_as_soon_as_the_exe_is_copied()
    {
        var script = Script();

        var installedExes = Regex.Matches(script,
            @"Source: ""\.\.\\build\\publish\\installer-win-[a-z0-9]+\\UltimateUpdater\.exe"".*?\r?\n.*?\r?\n", RegexOptions.None);
        Assert.Equal(2, installedExes.Count);
        Assert.All(installedExes, entry => Assert.Contains("AfterInstall: WriteInstalledMarker", entry.Value));
        Assert.DoesNotContain("ssPostInstall", script);
    }

    /// <summary>
    /// The elevated PowerShell inherits Setup's environment. A command planted in a user PATH folder or a
    /// .NET profiler variable would run as admin, so Setup resets both before every PowerShell call.
    /// </summary>
    [Fact]
    public void Elevated_PowerShell_starts_with_a_Windows_only_environment()
    {
        var script = Script();

        var args = Regex.Match(script, @"function PowerShellArgs.*?\r?\nend;", RegexOptions.Singleline);
        Assert.True(args.Success, "PowerShellArgs is gone");
        Assert.Contains("PrepareElevatedPowerShell;", args.Value);

        var prepare = Regex.Match(script, @"procedure PrepareElevatedPowerShell;.*?\r?\nend;", RegexOptions.Singleline);
        Assert.True(prepare.Success, "PrepareElevatedPowerShell is gone");
        Assert.Contains("SetEnvironmentVariable('Path', Sys + ';' + Sys + '\\Wbem;' + Sys + '\\WindowsPowerShell\\v1.0');", prepare.Value);
        Assert.Contains("SetEnvironmentVariable('PSModulePath', Sys + '\\WindowsPowerShell\\v1.0\\Modules');", prepare.Value);
        Assert.Contains("ClearCodeLoadingVariablesFrom('Environment');", prepare.Value);
        Assert.Contains("ClearCodeLoadingVariablesFrom('Volatile Environment');", prepare.Value);
        foreach (var name in new[] { "COR_ENABLE_PROFILING", "COR_PROFILER", "COR_PROFILER_PATH" })
            Assert.Contains($"ClearEnvironmentVariable('{name}', 0);", prepare.Value);

        // HKCU\Environment can also redirect Windows' own lookups; these four are pinned to Windows values.
        Assert.Contains("SetEnvironmentVariable('SystemRoot', ExpandConstant('{win}'));", prepare.Value);
        Assert.Contains("SetEnvironmentVariable('windir', ExpandConstant('{win}'));", prepare.Value);
        Assert.Contains("SetEnvironmentVariable('ComSpec', Sys + '\\cmd.exe');", prepare.Value);
        Assert.Contains("SetEnvironmentVariable('PATHEXT', '.COM;.EXE;", prepare.Value);

        var prefixes = Regex.Match(script, @"function IsCodeLoadingVariable.*?\r?\nend;", RegexOptions.Singleline);
        Assert.True(prefixes.Success, "IsCodeLoadingVariable is gone");
        foreach (var prefix in new[] { "COR_", "CORECLR_", "COMPLUS_", "DOTNET_" })
            Assert.Contains($"Pos('{prefix}', Upper) = 1", prefixes.Value);
    }

    /// <summary>
    /// A silent upgrade reuses the previous task choice, which switched autostart back on for a user who had
    /// turned it off in the app. Whether this is an upgrade is read once, before Setup writes anything.
    /// </summary>
    [Fact]
    public void A_silent_upgrade_keeps_the_apps_own_autostart_setting()
    {
        var script = Script();
        Assert.Contains("ClearCodeLoadingVariablesFromSubkeys('Volatile Environment');", prepare.Value);

        var init = Regex.Match(script, @"function InitializeSetup.*?\r?\nend;", RegexOptions.Singleline);
        Assert.True(init.Success, "InitializeSetup is gone");
        Assert.Contains("WasUpgrade := ExistingInstallDir <> ''", init.Value);

        var keeps = Regex.Match(script, @"function KeepsAutostartSetting.*?\r?\nend;", RegexOptions.Singleline);
        Assert.True(keeps.Success, "KeepsAutostartSetting is gone");
        Assert.Contains("WizardSilent and WasUpgrade", keeps.Value);

        var joined = script.Replace("\\\r\n", " ").Replace("\\\n", " ");
        var setLine = joined.Split('\n').Single(line => line.Contains("--set-autostart", StringComparison.Ordinal)
            && line.TrimStart().StartsWith("Filename:", StringComparison.Ordinal));
        Assert.Contains("(not KeepsAutostartSetting)", setLine);
        Assert.Contains("(not WantsRelaunch)", setLine);
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

    /// <summary>
    /// BringWizardToFront keeps asking on every page change until the wizard has really been the foreground
    /// window, then stops for good: a page change must never pull Setup back in front of a window the user
    /// switched to, and the top-most hold is released the moment the wizard is active.
    /// </summary>
    [Fact]
    public void BringWizardToFront_asks_until_the_wizard_is_in_front_and_then_stops()
    {
        var script = Script();
        var bringToFront = Regex.Match(script, @"procedure BringWizardToFront;\s*(?:var.*?)?^begin.*?^end;", RegexOptions.Singleline | RegexOptions.Multiline);
        Assert.True(bringToFront.Success, "BringWizardToFront procedure body not found");
        Assert.Contains("if FrontReached then", bringToFront.Value);
        Assert.Contains("Exit;", bringToFront.Value);
        Assert.Contains("GetForegroundWindow = WizardForm.Handle", bringToFront.Value);

        var changed = Regex.Match(script, @"procedure CurPageChanged\(CurPageID: Integer\);.*?^end;", RegexOptions.Singleline | RegexOptions.Multiline);
        Assert.True(changed.Success, "CurPageChanged procedure body not found");
        Assert.Contains("BringWizardToFront;", changed.Value);

        var init = Regex.Match(script, @"procedure InitializeWizard;.*?^end;", RegexOptions.Singleline | RegexOptions.Multiline);
        Assert.True(init.Success, "InitializeWizard procedure body not found");
        Assert.Contains("BringWizardToFront;", init.Value);
    }

    /// <summary>
    /// Inno's language picker shows before any script runs, so no script can raise it: the language comes from
    /// the Windows display language, and every shipped language stays selectable by /LANG.
    /// </summary>
    [Fact]
    public void Setup_picks_its_language_from_Windows_without_a_picker()
    {
        var script = Script();
        var setup = script[script.IndexOf("[Setup]", StringComparison.Ordinal)..script.IndexOf("[Languages]", StringComparison.Ordinal)];

        Assert.Matches(@"(?m)^ShowLanguageDialog=no\s*$", setup);
        Assert.Matches(@"(?m)^LanguageDetectionMethod=uilanguage\s*$", setup);
        Assert.Matches(@"(?m)^Name: ""en"";", script);
        Assert.Matches(@"(?m)^Name: ""de"";", script);
    }

    /// <summary>Setup lets the elevated copy it relaunches as take the foreground, un-elevated runs included.</summary>
    [Fact]
    public void InitializeSetup_allows_any_process_to_take_the_foreground()
    {
        var script = Script();
        Assert.Contains("AllowSetForegroundWindow(ProcessId: DWORD): BOOL;", script);
        Assert.Contains("ASFW_ANY = $FFFFFFFF;", script);

        var init = Regex.Match(script, @"function InitializeSetup: Boolean;.*?^end;", RegexOptions.Singleline | RegexOptions.Multiline);
        Assert.True(init.Success, "InitializeSetup function body not found");
        Assert.Contains("AllowSetForegroundWindow(ASFW_ANY);", init.Value);
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

    [Fact]
    public void Setup_ends_a_running_copy_by_path_before_the_restart_manager_looks()
    {
        var script = Script();
        var prepare = Regex.Match(script, @"function PrepareToInstall.*?\r?\nend;", RegexOptions.Singleline);

        Assert.True(prepare.Success, "PrepareToInstall is gone");
        Assert.Contains("StopRunningCopies", prepare.Value);
        // only the app's own /FROMAPP run skips the busy check; every other run checks it
        Assert.Contains("(not HasSwitch('/FROMAPP')) and InstallingCopyRuns", prepare.Value);

        var stop = Regex.Match(script, @"function StopRunningCopies.*?\r?\nend;", RegexOptions.Singleline);
        Assert.Contains("Stop-Process", stop.Value);
        Assert.Contains("Wait-Process -Timeout 5", stop.Value);
        // by path, never by image name: a portable copy elsewhere must survive
        Assert.DoesNotContain("taskkill", stop.Value, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("$_.Path", script);
    }

    /// <summary>
    /// A PowerShell that does not start or dies with another code says nothing about the running copies, and
    /// treating that as "closed" let a silent uninstall hang on an elevated copy again. Only the script's own
    /// verdict (0 or the still-running code) is trusted; everything else falls back to the exe write lock,
    /// read without PowerShell, and an exe still in use stops Setup and the uninstall.
    /// </summary>
    [Fact]
    public void A_stop_that_gave_no_verdict_falls_back_to_the_exe_lock_and_fails_closed()
    {
        var script = Script();
        var stop = Regex.Match(script, @"function StopRunningCopies.*??
end;", RegexOptions.Singleline);
        Assert.True(stop.Success, "StopRunningCopies is gone");

        Assert.Contains("Started := Exec(", stop.Value);
        Assert.Contains("Started and ((ResultCode = 0) or (ResultCode = StillRunningExitCode))", stop.Value);
        Assert.Contains("Result := not InstallFolderExeInUse", stop.Value);
        Assert.DoesNotContain("Result := ResultCode <> StillRunningExitCode;" + Environment.NewLine + "end;", stop.Value);

        var lockCheck = Regex.Match(script, @"function InstallFolderExeInUse.*??
end;", RegexOptions.Singleline);
        Assert.True(lockCheck.Success, "InstallFolderExeInUse is gone");
        Assert.True(
            lockCheck.Index < stop.Index, "InstallFolderExeInUse must be declared before StopRunningCopies");
        // opened for writing without truncating, sharing read and write like the script's own probe
        Assert.Contains("CreateFileW(", lockCheck.Value);
        Assert.Contains("GENERIC_WRITE", lockCheck.Value);
        Assert.Contains("OPEN_EXISTING", lockCheck.Value);
        Assert.Contains("{#AppExeName}", lockCheck.Value);
        Assert.Contains("{#PortableExeName}", lockCheck.Value);
    }

    [Fact]
    public void Both_callers_of_the_stop_end_the_run_with_a_log_line_when_a_copy_stays_open()
    {
        var script = Script();
        var uninstall = Regex.Match(script, @"function InitializeUninstall.*??
end;", RegexOptions.Singleline);
        var prepare = Regex.Match(script, @"function PrepareToInstall.*??
end;", RegexOptions.Singleline);

        // the uninstall: log, box only when not silent, then False (so a silent run ends non-zero)
        var failed = uninstall.Value[uninstall.Value.IndexOf("if not StopRunningCopies then", StringComparison.Ordinal)..];
        Assert.Contains("Log(", failed);
        Assert.Contains("if not UninstallSilent then", failed);
        Assert.Contains("Result := False;", failed);

        // Setup: the failure message makes Setup abort, silent or not
        var failedSetup = prepare.Value[prepare.Value.IndexOf("if not StopRunningCopies then", StringComparison.Ordinal)..];
        Assert.Contains("Log(", failedSetup);
        Assert.Contains("Result := CustomMessage('ElevatedCopyOpen')", failedSetup);
    }

    [Fact]
    public void A_silent_run_that_did_not_come_from_the_app_stops_on_a_busy_app_without_killing()
    {
        var prepare = Regex.Match(Script(), @"function PrepareToInstall.*?\r?\nend;", RegexOptions.Singleline);
        Assert.True(prepare.Success, "PrepareToInstall is gone");

        // the busy branch of a silent run returns a message (Setup aborts with a non-zero code) before any stop
        var busyBranch = prepare.Value.IndexOf("if WizardSilent then", StringComparison.Ordinal);
        var stop = prepare.Value.IndexOf("StopRunningCopies", StringComparison.Ordinal);
        Assert.True(busyBranch >= 0 && busyBranch < stop, "the silent busy branch must come before StopRunningCopies");
        var silent = prepare.Value[busyBranch..stop];
        Assert.Contains("Log(", silent);
        Assert.Contains("Result := CustomMessage('BusyDeclined')", silent);
        Assert.Contains("Exit;", silent);
    }

    [Fact]
    public void The_busy_probe_counts_every_failure_as_busy()
    {
        var probe = Regex.Match(Script(), @"function InstallingCopyRuns.*?\r?\nend;", RegexOptions.Singleline);
        Assert.True(probe.Success, "InstallingCopyRuns is gone");

        // no owner SID, mutex open error, PowerShell not started, any exit code but 0: all busy
        Assert.Contains("if (-not $sid) { $busy = $true; continue }", probe.Value);
        Assert.Contains("catch { $busy = $true }", probe.Value);
        Assert.Contains("Result := (not Started) or (ResultCode <> 0);", probe.Value);
    }

    [Fact]
    public void Setup_looks_for_the_same_busy_mutex_the_app_raises()
    {
        var script = Script();

        Assert.Contains(BusySignal.NamePrefix, script);
        Assert.Contains("TryOpenExisting", script);

        // a copy in another session keeps its mutex in that session's namespace: Local\ would miss it
        var probe = Regex.Match(script, @"function InstallingCopyRuns.*?\r?\nend;", RegexOptions.Singleline);
        Assert.True(probe.Success, "InstallingCopyRuns is gone");
        Assert.Contains("$c.SessionId -eq $s", probe.Value);
        Assert.Contains("''Session\\'' + $c.SessionId + ''\\UltimateUpdater-Busy-'' + $sid", probe.Value);
        Assert.Contains("GetCurrentProcess().SessionId", probe.Value);
    }

    /// <summary>
    /// An interactive uninstall asks before it closes an app that is installing or cleaning up, with the same
    /// text Setup uses; declining aborts the uninstall. A silent one never asks. The probe and the stop have
    /// to be declared above the function that calls them (Pascal Script).
    /// </summary>
    [Fact]
    public void Uninstall_asks_before_closing_a_busy_app_and_a_no_aborts_it()
    {
        var script = Script();
        var uninstall = Regex.Match(script, @"function InitializeUninstall.*?\r?\nend;", RegexOptions.Singleline);
        Assert.True(uninstall.Success, "InitializeUninstall is gone");

        Assert.True(
            script.IndexOf("function InstallingCopyRuns", StringComparison.Ordinal)
                < script.IndexOf("function InitializeUninstall", StringComparison.Ordinal),
            "InstallingCopyRuns must be declared before InitializeUninstall");
        Assert.Contains("(not UninstallSilent) and InstallingCopyRuns", uninstall.Value);
        Assert.Contains("CustomMessage('BusyPrompt')", uninstall.Value);
        Assert.Contains("<> IDYES", uninstall.Value);
        Assert.Contains("Result := False;", uninstall.Value);

        // a box that /SUPPRESSMSGBOXES cannot suppress would hang an unattended uninstall; suppressed, it
        // gives the silent answer (close the app)
        Assert.DoesNotContain(" MsgBox(", uninstall.Value);
        Assert.Contains("SuppressibleMsgBox(CustomMessage('BusyPrompt'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDYES)", uninstall.Value);

        // the prompt and its abort come before the app is stopped; the stop is the by-path one that waits
        Assert.True(
            script.IndexOf("procedure StopRunningCopies", StringComparison.Ordinal)
                < script.IndexOf("function InitializeUninstall", StringComparison.Ordinal),
            "StopRunningCopies must be declared before InitializeUninstall");
        Assert.True(
            uninstall.Value.IndexOf("Result := False;", StringComparison.Ordinal)
                < uninstall.Value.IndexOf("if not StopRunningCopies then", StringComparison.Ordinal));
        Assert.DoesNotContain("taskkill", uninstall.Value, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public void A_release_build_without_a_signing_key_fails_while_a_plain_build_only_warns()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "UltimateUpdater.slnx")))
            directory = directory.Parent;
        var build = File.ReadAllText(Path.Combine(directory!.FullName, "build.bat")).Replace("\r\n", "\n");

        var missing = build.IndexOf("if not exist \"%SIGNKEY%\" (", StringComparison.Ordinal);
        Assert.True(missing >= 0, "the missing-key branch is gone");
        var branch = build[missing..build.IndexOf("where py", missing, StringComparison.Ordinal)];

        var release = branch.IndexOf("if \"%WU_RELEASE%\"==\"1\" (", StringComparison.Ordinal);
        var warning = branch.IndexOf("WARNING: Unsigned build", StringComparison.Ordinal);
        Assert.True(release >= 0 && release < warning, "WU_RELEASE=1 must be checked before the warning path");
        Assert.Contains("goto :error", branch[release..warning]);
        Assert.Contains("goto :releasesigndone", branch[warning..]);
    }

    [Fact]
    public void The_installer_block_never_expands_the_compiler_path_unquoted_inside_parentheses()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "UltimateUpdater.slnx")))
            directory = directory.Parent;
        var build = File.ReadAllText(Path.Combine(directory!.FullName, "build.bat")).Replace("\r\n", "\n");

        // A machine-wide Inno path contains "(x86)"; its ")" ends a parenthesised block at parse time.
        var start = build.IndexOf("if not defined ISCC (", StringComparison.Ordinal);
        Assert.True(start >= 0, "the installer block is gone");
        var block = build[start..build.IndexOf("rem And the installer itself", start, StringComparison.Ordinal)];
        foreach (var line in block.Split('\n'))
        {
            var bare = line.Replace("\"%ISCC%\"", "");
            Assert.False(bare.Contains("%ISCC%") || bare.Contains("%PF86%"), "unquoted expansion: " + line.Trim());
        }
    }

    private static string ReadBuildScript()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "UltimateUpdater.slnx")))
            directory = directory.Parent;
        return File.ReadAllText(Path.Combine(directory!.FullName, "build.bat")).Replace("\r\n", "\n");
    }

    /// <summary>dir refuses a wildcard inside a path, so the old search never found a signtool.</summary>
    [Fact]
    public void The_signtool_search_walks_the_sdk_version_folders_instead_of_a_wildcard_in_the_middle_of_a_path()
    {
        var build = ReadBuildScript();
        var start = build.IndexOf("set SIGNTOOL=", StringComparison.Ordinal);
        Assert.True(start >= 0, "the signtool search is gone");
        var block = build[start..build.IndexOf(":havesigntool", start, StringComparison.Ordinal)];

        Assert.DoesNotContain("dir /b /s", block);
        Assert.DoesNotContain("bin\\*\\", block);
        Assert.Contains("for /d %%V in (", block);
        Assert.True(
            block.IndexOf("%%~V\\x64\\signtool.exe", StringComparison.Ordinal)
                < block.IndexOf("%%~V\\arm64\\signtool.exe", StringComparison.Ordinal),
            "x64 must be tried before arm64");
    }

    [Fact]
    public void A_missing_installer_compiler_never_marks_the_build_up_to_date_and_fails_a_release()
    {
        var build = ReadBuildScript();
        var start = build.IndexOf("if not defined ISCC (", StringComparison.Ordinal);
        Assert.True(start >= 0, "the installer block is gone");
        var missing = build[start..build.IndexOf(") else (", start, StringComparison.Ordinal)];

        var release = missing.IndexOf("if \"%WU_RELEASE%\"==\"1\" (", StringComparison.Ordinal);
        Assert.True(release >= 0, "WU_RELEASE=1 must be refused when ISCC is missing");
        Assert.Contains("goto :error", missing[release..missing.IndexOf("set \"NOSETUP=1\"", StringComparison.Ordinal)]);

        var save = build.IndexOf("-Save", StringComparison.Ordinal);
        var saveLine = build[build.LastIndexOf('\n', save)..save];
        Assert.Contains("if not defined NOSETUP", saveLine);
    }

    [Fact]
    public void Setup_logs_closes_dlls_too_and_refuses_to_compile_without_a_version()
    {
        var script = Script();

        Assert.Matches(@"(?m)^SetupLogging=yes\r?$", script);
        Assert.Matches(@"(?m)^CloseApplicationsFilter=\*\.exe,\*\.dll\r?$", script);

        var guard = Regex.Match(script, @"#ifndef AppVersion\r?\n(.*?)#endif", RegexOptions.Singleline);
        Assert.True(guard.Success, "the AppVersion guard is gone");
        Assert.Contains("#error", guard.Groups[1].Value);
        Assert.DoesNotContain("#define", guard.Groups[1].Value);
    }

    /// <summary>
    /// Inno replays the previous task choice on a reinstall, which switched autostart back on after the user had
    /// turned it off in the app. The Tasks page therefore starts from the real state of the Run key.
    /// </summary>
    [Fact]
    public void An_interactive_reinstall_starts_the_autostart_box_from_the_real_run_key()
    {
        var script = Script();

        var sync = Regex.Match(script, @"procedure SyncAutostartTask;.*?\r?\nend;", RegexOptions.Singleline);
        Assert.True(sync.Success, "SyncAutostartTask is gone");
        Assert.Contains("RegValueExists(HKEY_CURRENT_USER, 'Software\\Microsoft\\Windows\\CurrentVersion\\Run', 'UltimateUpdater')", sync.Value);
        Assert.Contains("WizardSelectTasks('autostart')", sync.Value);
        Assert.Contains("WizardSelectTasks('!autostart')", sync.Value);

        var page = Regex.Match(script, @"procedure CurPageChanged\(CurPageID: Integer\);.*?\r?\nend;", RegexOptions.Singleline);
        Assert.True(page.Success, "CurPageChanged is gone");
        Assert.Matches(@"if CurPageID = wpSelectTasks then\s+SyncAutostartTask;", page.Value);
    }

    /// <summary>
    /// A spaced hyphen between two words stands in for a dash. Subtraction in the Pascal code is not prose and
    /// is left out: such a line holds an assignment, a "div" or continues with a plus.
    /// </summary>
    [Fact]
    public void The_installer_files_use_no_spaced_hyphen_as_a_dash()
    {
        var root = RepoRootForScripts();
        var pattern = new Regex(@"(?<=[A-Za-z)]) - (?=[A-Za-z(])");
        var findings = new List<string>();

        foreach (var relative in new[] { @"installer\UltimateUpdater.iss", "build.bat", @"installer\license.en.txt", @"installer\license.de.txt" })
        {
            var lines = File.ReadAllLines(Path.Combine(root, relative));
            for (var i = 0; i < lines.Length; i++)
            {
                if (!IsArithmetic(lines[i]) && pattern.IsMatch(lines[i]))
                    findings.Add($"{relative}:{i + 1}: {lines[i].Trim()}");
            }
        }

        Assert.True(findings.Count == 0, "spaced hyphen found:" + Environment.NewLine + string.Join(Environment.NewLine, findings));
    }

    private static bool IsArithmetic(string line) =>
        line.Contains(" div ", StringComparison.Ordinal)
        || line.Contains(":=", StringComparison.Ordinal)
        || line.TrimEnd().EndsWith('+');

    private static string RepoRootForScripts()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "UltimateUpdater.slnx")))
            directory = directory.Parent;

        Assert.True(directory is not null, "the repository root is not above the test binaries any more");
        return directory!.FullName;
    }
}
