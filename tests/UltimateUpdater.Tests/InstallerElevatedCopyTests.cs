using System.Text.RegularExpressions;
using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

/// <summary>
/// Setup and the uninstaller cannot see or close a copy started as administrator, so they ask it through the
/// exit event and read the exe's lock. These pin the parts of the Inno script that no compile error catches.
/// </summary>
public class InstallerElevatedCopyTests
{
    private static string Script()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "UltimateUpdater.slnx")))
            directory = directory.Parent;

        Assert.True(directory is not null, "the repository root is not above the test binaries any more");
        return File.ReadAllText(Path.Combine(directory!.FullName, "installer", "UltimateUpdater.iss"));
    }

    private static string Block(string pattern)
    {
        var match = Regex.Match(Script(), pattern + @".*?\r?\nend;", RegexOptions.Singleline);
        Assert.True(match.Success, pattern + " is gone");
        return match.Value;
    }

    [Fact]
    public void The_busy_probe_opens_the_mutex_with_the_single_right_the_app_grants()
    {
        var probe = Block("function InstallingCopyRuns");

        // the app's DACL gives the user Synchronize alone; the default open asks for modify and is refused
        Assert.Contains("TryOpenExisting($n, [System.Security.AccessControl.MutexRights]::Synchronize, [ref]$m)", probe);
        Assert.DoesNotContain("TryOpenExisting($n, [ref]$m)", probe);
        Assert.Contains("foreach ($c in $watch)", probe);
    }

    [Fact]
    public void A_hidden_copy_counts_as_ours_only_while_an_exe_of_the_install_folder_is_in_use()
    {
        var shared = Block("function RunningCopiesScript");

        Assert.Contains("-not $_.Path", shared);
        // a running exe refuses a write open with a sharing violation (32): a test that needs no rights on the process
        Assert.Contains("[System.IO.File]::Open($f, ''Open'', ''Write'', ''ReadWrite'')", shared);
        Assert.Contains("-band 0xFFFF) -eq 32", shared);
        // any other open failure (read-only exe, a deny entry) proves nothing either way: like the Pascal check,
        // it counts as in use while a hidden copy exists, so an elevated copy is never left running
        // asked again each time, not the snapshot from the start: a copy that has exited since must not hold setup
        Assert.Contains("catch { if ((Hidden).Count -gt 0) { return $true } }", shared);
        Assert.Contains("$hidden = Hidden;", shared);
        Assert.DoesNotContain("catch { } } }; return $false", shared);
        Assert.Contains("$hidden.Count -gt 0 -and (Running)", shared);
        // by image name alone would reach a portable copy in another folder
        Assert.DoesNotContain("Stop-Process", shared);
    }

    [Fact]
    public void Setup_signals_the_event_the_app_creates_by_the_same_name_with_modify_rights_only()
    {
        var script = Script();
        var stop = Block("function StopRunningCopies");

        Assert.EndsWith(@"-Exit-", ExitRequestListener.NamePrefix);
        Assert.Contains("''Local\\UltimateUpdater-Exit-'' + $sid + ''-'' + $hash", stop);
        Assert.Contains("''Session\\'' + $h.SessionId + ''\\UltimateUpdater-Exit-'' + $sid + ''-'' + $hash", stop);
        // the same hash as ExitRequestListener.FolderHash: SHA256 of the lower-case folder without a trailing separator
        Assert.Contains("[System.Security.Cryptography.SHA256]::Create()", stop);
        Assert.Contains("[System.Text.Encoding]::UTF8.GetBytes(", stop);
        Assert.Contains("TrimEnd([char[]]''\\/'').ToLowerInvariant()", stop);
        Assert.Contains("[System.BitConverter]::ToString($x, 0, 8).Replace(''-'', '''').ToLowerInvariant()", stop);
        Assert.Contains("PsQuote(ExpandConstant('{app}'))", stop);
        Assert.Contains("EventWaitHandleRights]::Modify", stop);
        Assert.DoesNotContain("OpenExisting($n)", stop);
        Assert.Contains("UltimateUpdater-Exit-", script);
    }

    [Fact]
    public void Setup_waits_ten_seconds_for_the_copies_and_reports_one_that_stays()
    {
        var stop = Block("function StopRunningCopies");

        Assert.Contains("$w.Elapsed.TotalSeconds -lt 10", stop);
        Assert.Contains("if (Running) { exit ' + IntToStr(StillRunningExitCode) + ' }", stop);
        Assert.Matches(@"Result := ResultCode <> StillRunningExitCode\s+else", stop);
        Assert.DoesNotContain("taskkill", stop, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_copy_that_stays_stops_setup_with_the_message_and_a_log_line()
    {
        var prepare = Block("function PrepareToInstall");
        var uninstall = Block("function InitializeUninstall");

        // install: the message is the failure, so a silent run exits non-zero and an interactive one shows it
        var afterStop = prepare[prepare.IndexOf("if not StopRunningCopies then", StringComparison.Ordinal)..];
        Assert.Contains("Log(", afterStop);
        Assert.Contains("Result := CustomMessage('ElevatedCopyOpen');", afterStop);

        // uninstall: shown unless silent, never in a box that /SUPPRESSMSGBOXES cannot suppress, then aborted
        var afterUninstallStop = uninstall[uninstall.IndexOf("if not StopRunningCopies then", StringComparison.Ordinal)..];
        Assert.Contains("Log(", afterUninstallStop);
        Assert.Contains("if not UninstallSilent then", afterUninstallStop);
        Assert.Contains("SuppressibleMsgBox(CustomMessage('ElevatedCopyOpen'), mbError, MB_OK, IDOK)", afterUninstallStop);
        Assert.Contains("Result := False;", afterUninstallStop);
    }

    [Fact]
    public void Copies_are_ended_only_after_the_busy_mutex_was_looked_at_once_more()
    {
        var script = Script();
        var recheck = Block("function StopBusyRecheckScript");
        var stop = Block("function StopRunningCopies");

        // the same name and the same Synchronize-only open as the first check, for every copy about to be ended
        Assert.Contains("foreach ($c in $copies)", recheck);
        Assert.Contains("''Local\\UltimateUpdater-Busy-'' + $o.Sid", recheck);
        Assert.Contains("''Session\\'' + $c.SessionId + ''\\UltimateUpdater-Busy-'' + $o.Sid", recheck);
        Assert.Contains("TryOpenExisting($n, [System.Security.AccessControl.MutexRights]::Synchronize, [ref]$m)", recheck);
        // an unreadable owner or a failed open counts as busy, and busy ends the script before anything is stopped
        Assert.Contains("if (-not ($o -and $o.Sid)) { $busy = $true; continue }", recheck);
        Assert.Contains("catch { $busy = $true }", recheck);
        Assert.Contains("if ($busy) { exit ' + IntToStr(BusyExitCode) + ' }", recheck);
        Assert.True(
            stop.IndexOf("StopBusyRecheckScript", StringComparison.Ordinal) < stop.IndexOf("Stop-Process", StringComparison.Ordinal),
            "the busy look has to come before the copies are stopped");

        // a busy verdict is not "could not be closed": it is its own result
        Assert.Contains("ResultCode = BusyExitCode", stop);
        Assert.Contains("StopFoundBusy := True;", stop);
        Assert.Contains("StopFoundBusy := False;", stop);
        Assert.Contains("if not StopRecheckBusy then", recheck);
        Assert.Contains("StopRecheckBusy: Boolean;", script);
    }

    [Fact]
    public void A_busy_verdict_at_the_last_look_declines_setup_and_the_uninstall_but_not_the_apps_own_update()
    {
        var prepare = Block("function PrepareToInstall");
        var uninstall = Block("function InitializeUninstall");

        // never for the app's own updater, which made sure nothing runs; never after a yes to the busy question
        Assert.Contains("StopRecheckBusy := not HasSwitch('/FROMAPP');", prepare);
        Assert.Matches(@"InstallingCopyRuns then\s+begin\s+StopRecheckBusy := False;", prepare);
        var afterStop = prepare[prepare.IndexOf("if not StopRunningCopies then", StringComparison.Ordinal)..];
        Assert.Matches(@"if StopFoundBusy then\s+begin\s+Log\([^\n]*\);\s+Result := CustomMessage\('BusyDeclined'\);", afterStop);

        // a silent uninstall closes the app by design; an interactive one that was told "busy" and said yes too
        Assert.Contains("StopRecheckBusy := not UninstallSilent;", uninstall);
        Assert.Matches(@"InstallingCopyRuns then\s+begin\s+StopRecheckBusy := False;", uninstall);
        var afterUninstallStop = uninstall[uninstall.IndexOf("if not StopRunningCopies then", StringComparison.Ordinal)..];
        Assert.Matches(@"if StopFoundBusy then\s+begin\s+Log\(.*?SuppressibleMsgBox\(CustomMessage\('BusyDeclined'\), mbError, MB_OK, IDOK\)", afterUninstallStop.Replace("\n", " "));
    }

    [Fact]
    public void The_app_raises_the_busy_signal_the_moment_an_install_or_cleanup_starts()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "UltimateUpdater.slnx")))
            directory = directory.Parent;

        var app = File.ReadAllText(Path.Combine(directory!.FullName, "src", "UltimateUpdater", "App.xaml.cs"));

        Assert.Matches(
            @"PropertyChanged \+= .*?nameof\(MainViewModel\.IsInstalling\) or nameof\(MainViewModel\.IsCleanupRunning\).*?_busySignal\?\.Set\(mainViewModel\.WorkIsRunning\)",
            app.Replace("\r\n", " ").Replace("\n", " "));
    }

    [Fact]
    public void PsQuote_doubles_every_character_powershell_ends_a_single_quoted_string_with()
    {
        var quote = Block("function PsQuote");

        // PowerShell also reads the typographic single quotes as a quote: a folder named with one (an apostrophe in
        // a user name) ended the string, broke every script and let the rest of the name run as code
        var doubled = Regex.Matches(quote, @"StringChangeEx\(Result, (''''|#\$([0-9A-Fa-f]{4})), (''''''|#\$\2#\$\2), True\)")
            .Select(m => m.Groups[2].Success ? Convert.ToInt32(m.Groups[2].Value, 16) : '\'')
            .ToHashSet();

        Assert.Equal(new HashSet<int> { '\'', 0x2018, 0x2019, 0x201A, 0x201B }, doubled);
    }

    [Fact]
    public void The_still_running_message_exists_in_english_and_german_and_only_suspects_admin_rights()
    {
        var script = Script();

        // any write lock (antivirus, backup) can trip the exe check, so the text must not claim to know the cause
        Assert.Contains(
            "en.ElevatedCopyOpen={#AppName} is still running and did not close. It may be running with administrator rights. Close it, then try again.",
            script);
        Assert.Contains(
            "de.ElevatedCopyOpen={#AppName} l\u00e4uft noch und hat sich nicht beendet, m\u00f6glicherweise mit Administratorrechten. Bitte schlie\u00dfen Sie es und versuchen Sie es dann erneut.",
            script);
    }

    /// <summary>
    /// A lock on the exe from an antivirus scan or a backup is not a running copy. The lock is therefore asked
    /// again, a second apart, after the ten seconds of waiting before it counts, in the script and in the
    /// PowerShell-less fallback alike.
    /// </summary>
    [Fact]
    public void The_exe_lock_must_hold_across_several_checks_before_it_counts_as_a_running_copy()
    {
        var stop = Block("function StopRunningCopies");

        var confirm = stop.IndexOf("for ($i = 0; $i -lt 3 -and (Running); $i++) { Start-Sleep -Seconds 1 }", StringComparison.Ordinal);
        var verdict = stop.IndexOf("if (Running) { exit ' + IntToStr(StillRunningExitCode) + ' }", StringComparison.Ordinal);
        Assert.True(confirm >= 0 && confirm < verdict, "the lock must be re-checked a few times before the verdict");

        var fallback = stop[stop.IndexOf("checking the exe lock instead", StringComparison.Ordinal)..];
        Assert.Contains("Sleep(1000)", fallback);
        Assert.Contains("InstallFolderExeInUse", fallback);
    }
}
