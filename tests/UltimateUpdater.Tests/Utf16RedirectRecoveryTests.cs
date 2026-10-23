using System.Text;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

/// <summary>
/// Sfc's (and DISM's) progress line came back stuck at 0 % once its output was redirected, because
/// it writes UTF-16LE there while <c>ProcessRunner</c> decodes every tool's stdout as UTF-8. These tests
/// build the exact byte sequence a real redirected run produces (a UTF-16LE string, decoded as UTF-8) rather
/// than a hand-written approximation, so a wrong assumption about the failure shape would show up here.
/// </summary>
public class Utf16RedirectRecoveryTests
{
    /// <summary>What ProcessRunner's UTF-8 StreamReader actually receives when a native console tool writes
    /// UTF-16LE ASCII text into a redirected pipe: byte-for-byte what a real run would decode.</summary>
    private static string MisdecodeAsIfUtf16(string original) =>
        Encoding.UTF8.GetString(Encoding.Unicode.GetBytes(original));

    [Fact]
    public void LooksLikeMisdecodedUtf16_recognizes_a_real_utf16_line_misread_as_utf8()
    {
        var misdecoded = MisdecodeAsIfUtf16("Verification 45% complete.");

        Assert.True(Utf16RedirectRecovery.LooksLikeMisdecodedUtf16(misdecoded));
    }

    [Fact]
    public void Recover_reconstructs_the_original_text_exactly()
    {
        const string original = "Verification 45% complete.";
        var misdecoded = MisdecodeAsIfUtf16(original);

        Assert.True(Utf16RedirectRecovery.LooksLikeMisdecodedUtf16(misdecoded));
        Assert.Equal(original, Utf16RedirectRecovery.Recover(misdecoded));
    }

    [Theory]
    [InlineData("")]
    [InlineData("x")]
    [InlineData("Verification 45% complete.")]
    [InlineData("DISM /Online /Cleanup-Image /RestoreHealth")]
    public void LooksLikeMisdecodedUtf16_leaves_ordinary_utf8_text_alone(string ordinary)
    {
        Assert.False(Utf16RedirectRecovery.LooksLikeMisdecodedUtf16(ordinary));
    }

    [Fact]
    public void A_single_stray_nul_is_not_mistaken_for_a_misdecoded_line()
    {
        // One NUL in an otherwise normal line must not trip the heuristic; only a run of them (roughly
        // every other character) does.
        Assert.False(Utf16RedirectRecovery.LooksLikeMisdecodedUtf16("progress\0"));
    }
}

/// <summary>When a repair step never reports a readable percentage at all, the row must
/// show the honest running state instead of sitting at 0 % forever.</summary>
public class NoProgressHintTests
{
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(8);

    [Fact]
    public void Never_shown_once_a_real_percentage_was_read()
    {
        Assert.False(CleanupService.ShouldShowNoProgressHint(
            hasReadableProgress: true, elapsed: TimeSpan.FromMinutes(5), nextHintAt: TimeSpan.Zero));
    }

    [Fact]
    public void Not_shown_before_the_grace_delay_has_passed()
    {
        Assert.False(CleanupService.ShouldShowNoProgressHint(
            hasReadableProgress: false, elapsed: Delay - TimeSpan.FromSeconds(1), nextHintAt: Delay));
    }

    [Fact]
    public void Shown_once_the_grace_delay_has_passed_with_nothing_readable()
    {
        Assert.True(CleanupService.ShouldShowNoProgressHint(
            hasReadableProgress: false, elapsed: Delay, nextHintAt: Delay));
    }

    [Fact]
    public void Not_shown_again_before_its_own_repeat_throttle()
    {
        // Simulates the caller having already shown it once and advanced nextHintAt by the repeat interval.
        var nextHintAt = Delay + TimeSpan.FromSeconds(1);

        Assert.False(CleanupService.ShouldShowNoProgressHint(
            hasReadableProgress: false, elapsed: nextHintAt - TimeSpan.FromMilliseconds(1), nextHintAt));
    }
}
