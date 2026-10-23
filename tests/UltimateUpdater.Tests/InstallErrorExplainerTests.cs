using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

/// <summary>
/// Exercised against real winget failure text captured from actual installs (German and English), and
/// against text that carries no recognizable code at all, which must fall back honestly rather than
/// throw or guess.
/// </summary>
public class InstallErrorExplainerTests
{
    /// <summary>
    /// Every wrapper in this project formats <c>Process.ExitCode</c>, which is NEGATIVE for a crashed or
    /// abnormally terminated installer, so a pattern accepting digits only never matched the one shape
    /// its own producer emits, and the user was told nothing at all instead of being given the code.
    /// </summary>
    [Theory]
    [InlineData("chocolatey: install failed for 'x' (exit code -1978335189)", "-1978335189")]
    [InlineData("The tool exited with code -1073741819 and produced no output.", "-1073741819")]
    [InlineData("NVIDIA driver install returned exit code -1073741819.", "-1073741819")]
    [InlineData("pip: uninstall failed for 'y' (exit -5).", "-5")]
    public void A_negative_exit_code_is_still_explained_rather_than_passed_over(string text, string expected)
    {
        var explanation = InstallErrorExplainer.Explain("chocolatey", text);

        Assert.NotNull(explanation);
        Assert.Equal("InstallErrorGenericWhat", explanation!.WhatHappenedKey);
        Assert.Equal([expected], explanation.WhatHappenedArgs);
    }

    /// <summary>
    /// A vendor printing the same status unsigned (3221226505 is 0xC0000409) is past what a signed int
    /// holds. That used to abandon extraction outright, and because the first wording to MATCH ended the
    /// search, no later wording carrying a readable code was reached either.
    /// </summary>
    [Fact]
    public void An_exit_code_past_int_range_is_read_as_the_unsigned_value_it_is()
    {
        var explanation = InstallErrorExplainer.Explain("winget", "Installer failed with exit code: 3221226505");

        Assert.NotNull(explanation);
        Assert.Equal("InstallErrorGenericWhat", explanation!.WhatHappenedKey);
        Assert.Equal([unchecked((int)3221226505u).ToString()], explanation.WhatHappenedArgs);
    }

    /// <summary>A matched-but-unreadable code must not stop the remaining wordings from being tried.</summary>
    [Fact]
    public void An_unreadable_code_does_not_hide_a_readable_one_later_in_the_text()
    {
        var explanation = InstallErrorExplainer.Explain(
            "chocolatey",
            "Installer failed with exit code: 99999999999999999999 ... (exit code -42)");

        Assert.NotNull(explanation);
        Assert.Equal(["-42"], explanation!.WhatHappenedArgs);
    }

    /// <summary>Real-world reported failure text, verbatim.</summary>
    private const string RealGerman8006Text =
        "Fehler bei der Installation des benutzerdefinierten Installationsprogramms… Installation " +
        "fehlgeschlagen mit Exitcode: 8006";

    private const string EnglishEquivalent = "Custom install failed. Installer failed with exit code: 8006";

    [Fact]
    public void A_real_german_8006_text_is_recognized_and_offered_for_interactive_retry()
    {
        var explanation = InstallErrorExplainer.Explain("winget", RealGerman8006Text);

        Assert.NotNull(explanation);
        Assert.Equal(InstallErrorClassification.Recoverable, explanation!.Classification);
        Assert.True(explanation.AllowsInteractiveRetry);

        var formatted = explanation.Format(key => key);
        Assert.Contains("InstallErrorVs8006What", formatted);
        Assert.Contains("InstallErrorVs8006Now", formatted);
    }

    [Fact]
    public void The_english_wording_is_recognized_identically()
    {
        var explanation = InstallErrorExplainer.Explain("winget", EnglishEquivalent);

        Assert.NotNull(explanation);
        Assert.Equal(InstallErrorClassification.Recoverable, explanation!.Classification);
    }

    [Fact]
    public void Text_with_no_recognizable_code_returns_null_rather_than_guessing()
    {
        Assert.Null(InstallErrorExplainer.Explain("winget", "Something went wrong. No further detail."));
        Assert.Null(InstallErrorExplainer.Explain("winget", null));
        Assert.Null(InstallErrorExplainer.Explain("winget", "   "));
    }

    [Fact]
    public void Describe_returns_null_exactly_when_Explain_does()
    {
        Assert.Null(InstallErrorExplainer.Describe("winget", "nothing recognizable here", key => key));
        Assert.NotNull(InstallErrorExplainer.Describe("winget", RealGerman8006Text, key => key));
    }

    [Theory]
    [InlineData(8001)]
    [InlineData(8002)]
    [InlineData(8003)]
    [InlineData(8004)]
    [InlineData(8005)]
    public void Visual_studio_8001_through_8005_are_manual_work_not_retry_eligible(int code)
    {
        var text = $"Installer failed with exit code: {code}";
        var explanation = InstallErrorExplainer.Explain("winget", text);

        Assert.NotNull(explanation);
        Assert.Equal(InstallErrorClassification.ManualWork, explanation!.Classification);
        Assert.False(explanation.AllowsInteractiveRetry);
    }

    [Fact]
    public void Msi_1602_user_cancelled_is_recoverable()
    {
        var explanation = InstallErrorExplainer.Explain("winget", "Installer failed with exit code: 1602");

        Assert.NotNull(explanation);
        Assert.True(explanation!.AllowsInteractiveRetry);
    }

    /// <summary>
    /// 1618 (ERROR_INSTALL_ALREADY_RUNNING) used to be ManualWork; with several installs now able to run
    /// at once, it is an expected side effect of that rather than a real failure, and the queue already
    /// retries it once on its own (WingetSource.IsAnotherInstallationRunning), so it is Recoverable now,
    /// unlike the genuine ManualWork codes below.
    /// </summary>
    [Fact]
    public void Msi_1618_conflicting_install_is_recoverable()
    {
        var explanation = InstallErrorExplainer.Explain("winget", "Installer failed with exit code: 1618");

        Assert.NotNull(explanation);
        Assert.Equal(InstallErrorClassification.Recoverable, explanation!.Classification);
        Assert.True(explanation.AllowsInteractiveRetry);
        // Another installation already running is one of the causes safe to retry blindly.
        Assert.True(explanation.AllowsAutomaticRetry);
    }

    /// <summary>
    /// This fix's own eligibility test: true only for the causes named in the item spec (another
    /// installation already running, or the tool itself reporting busy/timed out), false for every other
    /// Recoverable cause and for the unmatched-code fallback, so an automatic retry never fires for a
    /// cause nobody has actually reviewed.
    /// </summary>
    [Theory]
    [InlineData("windowsupdate", "Windows Update is still busy with something else.", true)]
    [InlineData("npm", "npm: install timed out for 'eslint'.", true)]
    [InlineData("winget", "Installer failed with exit code: 1602", false)]
    [InlineData("winget", "Installer failed with exit code: 99999", false)]
    public void AllowsAutomaticRetry_is_true_only_for_the_named_safe_causes(string sourceId, string output, bool expected)
    {
        var explanation = InstallErrorExplainer.Explain(sourceId, output);

        Assert.Equal(expected, explanation?.AllowsAutomaticRetry ?? false);
    }

    /// <summary>The Windows Update HRESULT for "another installation running, or a restart is pending"
    /// is the same safe-to-retry-blindly cause as MSI 1618, just reached through a different code.</summary>
    [Fact]
    public void WindowsUpdate_hresult_80240016_allows_an_automatic_retry()
    {
        var explanation = InstallErrorExplainer.Explain("windowsupdate", "HRESULT 0x80240016");

        Assert.NotNull(explanation);
        Assert.True(explanation!.AllowsAutomaticRetry);
    }

    [Theory]
    [InlineData(1603)]
    [InlineData(1619)]
    [InlineData(1620)]
    [InlineData(1638)]
    public void Other_msi_codes_are_manual_work(int code)
    {
        var explanation = InstallErrorExplainer.Explain("winget", $"Installer failed with exit code: {code}");

        Assert.NotNull(explanation);
        Assert.Equal(InstallErrorClassification.ManualWork, explanation!.Classification);
    }

    [Theory]
    [InlineData(1641)]
    [InlineData(3010)]
    public void Reboot_codes_are_restart_needed_not_retry_eligible(int code)
    {
        var explanation = InstallErrorExplainer.Explain("winget", $"Installer failed with exit code: {code}");

        Assert.NotNull(explanation);
        Assert.Equal(InstallErrorClassification.RestartNeeded, explanation!.Classification);
        Assert.False(explanation.AllowsInteractiveRetry);
    }

    [Fact]
    public void Wingets_own_no_applicable_upgrade_code_is_recognized_from_hex_text()
    {
        var text = "winget install failed for 'Some.Package' (exit 0x8A15002B). No applicable upgrade found.";
        var explanation = InstallErrorExplainer.Explain("winget", text);

        Assert.NotNull(explanation);
        Assert.Equal(InstallErrorClassification.ManualWork, explanation!.Classification);
        Assert.False(explanation.AllowsInteractiveRetry);
    }

    /// <summary>The real logged failure: Microsoft.GameInput fails on every scan with this code because
    /// the installed package and the newer one use different install technologies, and winget refuses to
    /// bridge that gap on its own.</summary>
    [Fact]
    public void Wingets_different_install_technology_code_is_manual_work_not_retryable()
    {
        var text = "Installing Microsoft.GameInput failed (exit 0x8A15008E). "
            + "A newer version was found, but the install technology is different.";
        var explanation = InstallErrorExplainer.Explain("winget", text);

        Assert.NotNull(explanation);
        Assert.Equal(InstallErrorClassification.ManualWork, explanation!.Classification);
        Assert.False(explanation.AllowsInteractiveRetry);
        Assert.False(explanation.AllowsAutomaticRetry);
        Assert.Equal("InstallErrorWingetDifferentTechWhat", explanation.WhatHappenedKey);
    }

    /// <summary>winget has to remove the old version first and the old version registered no uninstall
    /// command (a real case: the VC++ 2010 redistributable).</summary>
    [Fact]
    public void Wingets_missing_uninstall_command_is_manual_work_with_its_own_sentence()
    {
        var text = "Installing Microsoft.VCRedist.2010.x86 failed (exit 0x8A15002F).";
        var explanation = InstallErrorExplainer.Explain("winget", text);

        Assert.NotNull(explanation);
        Assert.Equal(InstallErrorClassification.ManualWork, explanation!.Classification);
        Assert.False(explanation.AllowsInteractiveRetry);
        Assert.Equal("InstallErrorWingetNoUninstallWhat", explanation.WhatHappenedKey);
        Assert.Equal("InstallErrorWingetNoUninstallNow", explanation.WhatNowKey);
    }

    [Fact]
    public void Wingets_missing_uninstall_command_bucket_does_not_fire_for_a_different_source()
    {
        var explanation = InstallErrorExplainer.Explain("pip", "install failed (exit 0x8A15002F)");

        Assert.NotNull(explanation);
        Assert.NotEqual("InstallErrorWingetNoUninstallWhat", explanation!.WhatHappenedKey);
    }

    [Fact]
    public void Wingets_different_install_technology_bucket_does_not_fire_for_a_different_source()
    {
        var text = "install failed (exit 0x8A15008E)";
        var explanation = InstallErrorExplainer.Explain("pip", text);

        Assert.NotNull(explanation);
        Assert.NotEqual("InstallErrorWingetDifferentTechWhat", explanation!.WhatHappenedKey);
    }

    /// <summary>The Android CLI exits 127 for everything; the Java stack trace of a locked file is the only
    /// sign, and it gets the actionable sentence instead of the bare exit code.</summary>
    [Fact]
    public void An_android_locked_file_is_explained_as_recoverable()
    {
        var text = "android-sdk: install failed for 'emulator'. java.nio.file.AccessDeniedException: D:\\sdk\\emulator\\x.dll (exit code 127)";
        var explanation = InstallErrorExplainer.Explain("android-sdk", text);

        Assert.NotNull(explanation);
        Assert.Equal("InstallErrorAndroidLockedWhat", explanation!.WhatHappenedKey);
        Assert.Equal("InstallErrorAndroidLockedNow", explanation.WhatNowKey);
        Assert.Equal(InstallErrorClassification.Recoverable, explanation.Classification);
    }

    [Fact]
    public void The_android_locked_file_sentence_stays_with_the_android_source()
    {
        var explanation = InstallErrorExplainer.Explain("pip", "pip: install failed. AccessDeniedException (exit code 127)");

        Assert.NotNull(explanation);
        Assert.NotEqual("InstallErrorAndroidLockedWhat", explanation!.WhatHappenedKey);
    }

    [Fact]
    public void Wingets_own_appinstaller_bucket_does_not_fire_for_a_different_source()
    {
        // The same hex text, but not from winget: the winget-specific "no applicable package" bucket must
        // not fire for it, it still falls through to the honest generic fallback naming the code, never
        // silence and never a fabricated winget-specific explanation.
        var text = "install failed (exit 0x8A15002B)";
        var explanation = InstallErrorExplainer.Explain("pip", text);

        Assert.NotNull(explanation);
        Assert.NotEqual("InstallErrorWingetNoApplicableWhat", explanation!.WhatHappenedKey);
        Assert.Equal("InstallErrorGenericWhat", explanation.WhatHappenedKey);
    }

    /// <summary>A failed ladder lists every rung; the explanation must follow the rung that actually stopped
    /// it (the last), not the first hex code in the text.</summary>
    [Fact]
    public void A_winget_ladder_is_explained_by_its_last_rung()
    {
        var lastRung = "[install] winget install failed for 'Corsair.iCUE' (exit 0xC0000005). Access violation.";
        var ladder = "[upgrade] winget install failed for 'Corsair.iCUE' (exit 0x8A15002B). No applicable upgrade found."
                     + Environment.NewLine + lastRung;

        Assert.Equal(
            InstallErrorExplainer.Describe("winget", lastRung, key => key),
            InstallErrorExplainer.Describe("winget", ladder, key => key));
        Assert.NotEqual(
            InstallErrorExplainer.Describe("winget", ladder.Split(Environment.NewLine)[0], key => key),
            InstallErrorExplainer.Describe("winget", ladder, key => key));
    }

    [Fact]
    public void A_vendor_installer_crash_is_recognized_from_the_ntstatus_range()
    {
        var text = "winget install failed for 'Corsair.iCUE' (exit 0xC0000005). Access violation.";
        var explanation = InstallErrorExplainer.Explain("winget", text);

        Assert.NotNull(explanation);
        Assert.Equal(InstallErrorClassification.ManualWork, explanation!.Classification);
    }

    [Fact]
    public void An_unmatched_but_present_vendor_code_gets_the_honest_generic_fallback_not_a_fabricated_one()
    {
        var explanation = InstallErrorExplainer.Explain("winget", "Installer failed with exit code: 99999");

        Assert.NotNull(explanation);
        Assert.Equal(InstallErrorClassification.Recoverable, explanation!.Classification);
        Assert.Equal("InstallErrorGenericWhat", explanation.WhatHappenedKey);
        Assert.Contains("99999", explanation.WhatHappenedArgs);
    }

    [Fact]
    public void The_user_scope_wording_is_recognized_without_a_hex_code_present()
    {
        var text = "The requested operation is not permitted from an administrator context on packages installed within the user scope";
        var explanation = InstallErrorExplainer.Explain("winget", text);

        Assert.NotNull(explanation);
        Assert.Equal(InstallErrorClassification.ManualWork, explanation!.Classification);
    }

    /// <summary>Real-world reported result-code-5 text, now with the HRESULT VerifySucceeded adds.</summary>
    [Fact]
    public void A_real_result_code_5_text_is_recognized_as_aborted()
    {
        var text = "Windows Update install returned result code 5 (aborted), HRESULT 0x80070005.";
        var explanation = InstallErrorExplainer.Explain("windowsupdate", text);

        Assert.NotNull(explanation);
        Assert.Equal("InstallErrorWuAbortedWhat", explanation!.WhatHappenedKey);
    }

    [Theory]
    [InlineData(0, "InstallErrorWuNotStartedWhat")]
    [InlineData(1, "InstallErrorWuInProgressWhat")]
    [InlineData(4, "InstallErrorWuFailedWhat")]
    [InlineData(5, "InstallErrorWuAbortedWhat")]
    public void Every_windows_update_result_code_maps_to_its_own_explanation(int code, string expectedKey)
    {
        var text = $"Windows Update install returned result code {code} (whatever).";
        var explanation = InstallErrorExplainer.Explain("windowsupdate", text);

        Assert.NotNull(explanation);
        Assert.Equal(expectedKey, explanation!.WhatHappenedKey);
    }

    [Theory]
    [InlineData("0x80240016", "InstallErrorWuHresult80240016What")]
    [InlineData("0x80240044", "InstallErrorWuHresult80240044What")]
    [InlineData("0x80070422", "InstallErrorWuHresult80070422What")]
    [InlineData("0x8024002D", "InstallErrorWuHresult8024002DWhat")]
    [InlineData("0x80240022", "InstallErrorWuHresult80240022What")]
    public void Every_known_windows_update_hresult_is_recognized(string hex, string expectedKey)
    {
        var text = $"Windows Update install returned result code 4 (failed), HRESULT {hex}.";
        var explanation = InstallErrorExplainer.Explain("windowsupdate", text);

        Assert.NotNull(explanation);
        Assert.Equal(expectedKey, explanation!.WhatHappenedKey);
    }

    [Fact]
    public void An_unknown_windows_update_hresult_falls_back_to_the_result_code_bucket()
    {
        // No known HRESULT, but a recognized result code: the code-level explanation still applies.
        var text = "Windows Update install returned result code 4 (failed), HRESULT 0xDEADBEEF.";
        var explanation = InstallErrorExplainer.Explain("windowsupdate", text);

        Assert.NotNull(explanation);
        Assert.Equal("InstallErrorWuFailedWhat", explanation!.WhatHappenedKey);
    }

    [Fact]
    public void Windows_update_still_busy_is_recognized_and_retry_eligible()
    {
        var explanation = InstallErrorExplainer.Explain("windowsupdate", "Windows Update is still busy with another operation.");

        Assert.NotNull(explanation);
        Assert.Equal(InstallErrorClassification.Recoverable, explanation!.Classification);
    }

    [Fact]
    public void Windows_update_reboot_pending_is_recognized_as_restart_needed()
    {
        var explanation = InstallErrorExplainer.Explain(
            "windowsupdate", "A restart is pending before this update can install.");

        Assert.NotNull(explanation);
        Assert.Equal(InstallErrorClassification.RestartNeeded, explanation!.Classification);
    }

    [Fact]
    public void Windows_update_wording_does_not_fire_for_a_different_source()
    {
        // Same literal text, but not from the windowsupdate source: must not match the WU-scoped busy/
        // reboot-pending buckets, this exact wording is never emitted by any other source, but the scope
        // check itself must not silently vanish.
        var text = "Windows Update is still busy with another operation.";
        var explanation = InstallErrorExplainer.Explain("winget", text);

        Assert.Null(explanation);
    }

    [Fact]
    public void An_unrecognized_windows_update_result_code_falls_back_to_the_honest_generic()
    {
        var text = "Windows Update install returned result code 9 (unknown).";
        var explanation = InstallErrorExplainer.Explain("windowsupdate", text);

        Assert.NotNull(explanation);
        Assert.Equal("InstallErrorGenericWhat", explanation!.WhatHappenedKey);
        Assert.Contains("9", explanation.WhatHappenedArgs);
    }

    // ---- TryFindUnknownCode, the broader lookup used only once Explain() has already
    // returned null (see ErrorDetailViewModel's own constructor). ----

    [Fact]
    public void A_decimal_code_next_to_the_word_error_is_found_even_though_explain_recognizes_nothing()
    {
        var text = "Something failed with error code 2 and no further detail.";

        Assert.Null(InstallErrorExplainer.Explain("chocolatey", text));

        var found = InstallErrorExplainer.TryFindUnknownCode(text);

        Assert.NotNull(found);
        Assert.Equal(2, found!.Value.Code);
        Assert.False(found.Value.IsHresult);
    }

    [Fact]
    public void A_short_hex_token_explain_never_recognizes_is_still_found()
    {
        // Explain()'s own hex regex requires exactly 8 digits; this one has 4, so Explain() finds
        // nothing at all, but the broader lookup still reads it as an HRESULT-shaped value.
        var text = "HRESULT 0x7e failed.";

        Assert.Null(InstallErrorExplainer.Explain("somesource", text));

        var found = InstallErrorExplainer.TryFindUnknownCode(text);

        Assert.NotNull(found);
        Assert.Equal(0x7e, found!.Value.Code);
        Assert.True(found.Value.IsHresult);
    }

    [Fact]
    public void Plain_text_with_no_code_shaped_token_at_all_yields_nothing_to_look_up()
    {
        Assert.Null(InstallErrorExplainer.TryFindUnknownCode("Something went wrong. No further detail."));
        Assert.Null(InstallErrorExplainer.TryFindUnknownCode(null));
        Assert.Null(InstallErrorExplainer.TryFindUnknownCode("   "));
    }
}
