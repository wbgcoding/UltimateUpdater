using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;
using Xunit;

namespace UltimateUpdater.Tests;

/// <summary>
/// The install ladder's last resort is a version-pinned attempt built at install time from
/// `winget show --id &lt;id&gt; --exact --versions`, never a `--force` rung, `--force` also disables
/// winget's own installer hash check, so it must not be reintroduced as a fallback. These tests cover
/// everything here that is a pure function; the version-pinned attempt itself only runs against a real
/// winget, so its trigger condition (0x8A15002B only) is proven structurally below and end-to-end only
/// manually.
/// </summary>
[Collection("UiLanguage")]
public class WingetInstallLadderTests : IDisposable
{
    private const uint DidNothingCode = 0x8A15002Bu;

    // The suite runs serially over the same LocalizationService singleton; a test below applies "de" and
    // must not leak it into whatever test runs next.
    public void Dispose() => LocalizationService.Instance.Apply("en");

    [Fact]
    public void The_install_ladder_no_longer_carries_a_force_rung()
    {
        var ladder = WingetSource.InstallLadder("Some.Package");

        Assert.DoesNotContain(ladder, rung => rung.Arguments.Contains("--force"));
        Assert.DoesNotContain(ladder, rung => rung.Label.Contains("force", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(3, ladder.Count);
    }

    [Fact]
    public void The_version_pinned_arguments_pin_an_exact_version_without_force()
    {
        var arguments = WingetSource.VersionPinnedInstallArguments("Some.Package", "1.2.3");

        Assert.Contains("--version", arguments);
        Assert.Contains("1.2.3", arguments);
        Assert.Contains("--exact", arguments);
        Assert.DoesNotContain("--force", arguments);
    }

    /// <summary>Winget is the only source with a proven version-pinned install path
    /// (<see cref="WingetSource.VersionPinnedInstallArguments"/>), so it is the only one that ever offers
    /// a rollback; every other source keeps the interface's own default (false).</summary>
    [Fact]
    public void Winget_supports_pinning_an_exact_version()
    {
        var source = new WingetSource(new LoggingService());

        Assert.True(source.SupportsVersionPinning);
    }

    /// <summary>A rollback target shaped like an option is refused before winget is ever started: it
    /// comes from history the unelevated user can edit and may reach the elevated worker's command line.</summary>
    [Fact]
    public async Task A_pinned_version_shaped_like_an_option_is_refused_before_winget_runs()
    {
        var source = new WingetSource(new LoggingService());
        var item = new UltimateUpdater.Models.UpdateItem
        {
            Id = "Some.Package",
            SourceId = "winget",
            Category = UltimateUpdater.Models.UpdateCategory.Software,
            Name = "Some Package",
        }.WithPinnedVersion("--manifest=C:\\x\\evil.yaml");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            source.InstallAsync(item, new Progress<UltimateUpdater.Models.InstallProgress>(), CancellationToken.None));
        Assert.Contains("not a plain version", ex.Message);
    }

    /// <summary>The explanatory suffix, naming the version winget itself offered and still refused,
    /// only ever appears for the exact exit code that triggers the version-pinned rung in InstallAsync.
    /// Any other failure keeps the plain per-rung detail, unchanged.</summary>
    [Fact]
    public void The_offered_version_explanation_only_appears_for_the_not_applicable_code()
    {
        var attempts = new[] { "[upgrade] winget install failed (exit 0x8A15002B)." };

        var withOffer = WingetSource.LadderFailure(attempts, unchecked((int)DidNothingCode), "9.9.9");
        Assert.Contains("9.9.9", withOffer);
        Assert.Contains("no version of this package that fits this installation", withOffer);

        var withoutOffer = WingetSource.LadderFailure(attempts, unchecked((int)DidNothingCode), null);
        Assert.Contains("every variant this app knows how to try was refused", withoutOffer);
        Assert.DoesNotContain("9.9.9", withoutOffer);

        // A genuinely different failure never gets the "not applicable" explanation, even when a caller
        // happened to pass an offered version along.
        var unrelatedFailure = WingetSource.LadderFailure(attempts, lastExitCode: 5, offeredVersion: "9.9.9");
        Assert.DoesNotContain("9.9.9", unrelatedFailure);
        Assert.DoesNotContain("every variant this app knows how to try was refused", unrelatedFailure);
        Assert.Equal(attempts[0], unrelatedFailure);
    }

    /// <summary>The terminal rung's other "nothing to act on" code (APPINSTALLER_CLI_ERROR_NO_APPLICATIONS_FOUND)
    /// must get the same explanation as <see cref="DidNothingCode"/>, <see cref="WingetSource.LadderException"/>
    /// already classifies both via IsInapplicableOutcome, LadderFailure must stay in step.</summary>
    [Fact]
    public void The_no_applications_found_code_also_gets_an_explanation()
    {
        const uint noApplicationsFoundCode = 0x8A150014u;
        var attempts = new[] { "[upgrade --scope user] winget install failed (exit 0x8A150014)." };

        var message = WingetSource.LadderFailure(attempts, unchecked((int)noApplicationsFoundCode), null);

        Assert.Contains("every variant this app knows how to try was refused", message);
    }

    [Fact]
    public void Winget_show_versions_are_read_newest_first_regardless_of_locale()
    {
        const string german = """
            Gefunden Git [Git.Git]
            Version
            --------
            2.55.0.3
            2.55.0.2
            2.55.0
            """;

        var versions = WingetUpgradeParser.ParseVersionsList(german);

        Assert.Equal(["2.55.0.3", "2.55.0.2", "2.55.0"], versions);
    }

    [Fact]
    public void Winget_show_versions_missing_or_garbage_yields_nothing()
    {
        Assert.Empty(WingetUpgradeParser.ParseVersionsList("Found Git [Git.Git]\nVersion\n(no separator here)\n1.0.0"));
        Assert.Empty(WingetUpgradeParser.ParseVersionsList(""));
    }

    /// <summary>
    /// The one deliberate exception to "never --interactive": proves the interactive arguments are the
    /// normal ladder's first rung, minus --silent and
    /// --disable-interactivity, plus --interactive, and nothing else, built by filtering
    /// InstallArguments so the two can never drift apart.
    /// </summary>
    [Fact]
    public void The_interactive_arguments_drop_silent_and_disable_interactivity_and_add_interactive()
    {
        var arguments = WingetSource.InteractiveInstallArguments("Some.Package");

        Assert.DoesNotContain("--silent", arguments);
        Assert.DoesNotContain("--disable-interactivity", arguments);
        Assert.Contains("--interactive", arguments);

        var expectedRemainder = WingetSource.InstallArguments("Some.Package")
            .Where(a => a is not ("--silent" or "--disable-interactivity"))
            .ToList();
        var actualRemainder = arguments.Where(a => a != "--interactive").ToList();

        Assert.Equal(expectedRemainder, actualRemainder);
    }

    /// <summary>
    /// Corsair iCUE's real exit code: 0xC0000409 (STATUS_STACK_BUFFER_OVERRUN), an NTSTATUS
    /// crash code, not one of winget's own 0x8A1xxxxx refusals. The band is 0xC0000000-0xC0000FFF exactly
    /// (FACILITY_NONE NTSTATUS errors), NOT a bare ">= 0xC0000000": that wider range also swallows every
    /// ordinary negative exit code, -1/0xFFFFFFFF among them, InstallShield's own documented -1/-3/-12
    /// all "crashed" under it too. This band also needs a STATUS_CONTROL_C_EXIT carve-out.
    /// </summary>
    [Theory]
    [InlineData(unchecked((int)0xC0000409), true)]   // STATUS_STACK_BUFFER_OVERRUN, iCUE, a real observed case
    [InlineData(unchecked((int)0xC0000000), true)]   // the band's lower boundary
    [InlineData(unchecked((int)0xC0000FFF), true)]   // the band's upper boundary
    [InlineData(unchecked((int)0xC0001000), false)]  // just past the upper boundary, facility 1, not a crash
    [InlineData(unchecked((int)0xBFFFFFFF), false)]  // just below the lower boundary
    [InlineData(unchecked((int)0xC000013A), false)]  // STATUS_CONTROL_C_EXIT, terminated from outside, in the band but not a crash
    [InlineData(-1, false)]                          // a plain installer failure, not a crash
    [InlineData(-3, false)]                          // InstallShield's own documented failure code
    [InlineData(unchecked((int)DidNothingCode), false)]  // winget's own refusal code, well below the range
    [InlineData(0, false)]
    [InlineData(3010, false)]
    public void Vendor_installer_crash_codes_are_recognised_by_the_facility_none_band(int exitCode, bool expected)
    {
        Assert.Equal(expected, WingetSource.IsVendorInstallerCrash(exitCode));
    }

    /// <summary>The friendly sentence leads, but winget's own real output tail still follows it, a crash
    /// sentence must never make the diagnostic text disappear.</summary>
    [Fact]
    public void A_crashed_vendor_installer_gets_plain_words_and_keeps_the_real_output_tail()
    {
        LocalizationService.Instance.Apply("en");
        var result = new ProcessResult(unchecked((int)0xC0000409), "some real winget diagnostic line", "");

        var detail = WingetSource.FailureDetail("install", "Corsair.iCUE.5", result);

        Assert.Contains("crashed", detail);
        Assert.Contains("0xC0000409", detail);
        Assert.Contains("some real winget diagnostic line", detail);

        LocalizationService.Instance.Apply("de");
        var detailDe = WingetSource.FailureDetail("install", "Corsair.iCUE.5", result);
        Assert.Contains("abgestürzt", detailDe);
        Assert.Contains("0xC0000409", detailDe);
        Assert.Contains("some real winget diagnostic line", detailDe);
    }

    /// <summary>A plain negative exit code (not a crash) keeps its own, differently-shaped failure text,
    /// and it, too, is never empty.</summary>
    [Fact]
    public void A_plain_negative_exit_code_is_not_reported_as_a_crash()
    {
        var result = new ProcessResult(-1, "installer said no", "");

        var detail = WingetSource.FailureDetail("install", "Some.Package", result);

        Assert.DoesNotContain("crashed", detail);
        Assert.Contains("installer said no", detail);

        // Terminated from outside sits inside the NTSTATUS band but is not the installer's fault either.
        var terminated = new ProcessResult(unchecked((int)0xC000013A), "winget was stopped", "");
        var terminatedDetail = WingetSource.FailureDetail("install", "Some.Package", terminated);

        Assert.DoesNotContain("crashed", terminatedDetail);
        Assert.Contains("winget was stopped", terminatedDetail);
    }
}
