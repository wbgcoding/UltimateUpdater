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
        Assert.Contains("Result := ResultCode <> StillRunningExitCode;", stop);
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
    public void The_elevated_copy_message_exists_in_english_and_german()
    {
        var script = Script();

        Assert.Matches(@"(?m)^en\.ElevatedCopyOpen=\{#AppName\} .*administrator", script);
        Assert.Matches(@"(?m)^de\.ElevatedCopyOpen=\{#AppName\} .*Administrator", script);
    }
}
