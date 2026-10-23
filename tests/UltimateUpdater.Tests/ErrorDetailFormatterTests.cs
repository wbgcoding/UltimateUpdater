using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

/// <summary>
/// winget's failed-ladder dump was a flat wall of text,
/// these prove the segments a real ladder failure actually produces, not synthetic examples.
/// </summary>
public class ErrorDetailFormatterTests
{
    [Fact]
    public void Empty_or_null_input_yields_no_segments()
    {
        Assert.Empty(ErrorDetailFormatter.Format(null));
        Assert.Empty(ErrorDetailFormatter.Format(string.Empty));
    }

    [Fact]
    public void Plain_text_with_no_recognizable_shape_stays_one_plain_segment()
    {
        var segments = ErrorDetailFormatter.Format("something went wrong, no further details");

        var segment = Assert.Single(segments);
        Assert.Equal(ErrorDetailFormatter.SegmentKind.Plain, segment.Kind);
    }

    /// <summary>The exact shape a Bitcoin-Core-style ladder failure produces (WingetSource.LadderFailure).</summary>
    [Fact]
    public void A_winget_ladder_line_is_split_into_label_tool_name_and_a_retryable_exit_code()
    {
        const string line =
            "[upgrade] winget install failed for 'BitcoinCoreProject.BitcoinCore' (exit 0x8A15002B). No applicable upgrade found.";

        var segments = ErrorDetailFormatter.Format(line);

        Assert.Contains(segments, s => s.Kind == ErrorDetailFormatter.SegmentKind.RungLabel && s.Text == "[upgrade]");
        Assert.Contains(segments, s => s.Kind == ErrorDetailFormatter.SegmentKind.ToolName && s.Text == "winget");
        Assert.Contains(segments, s =>
            s.Kind == ErrorDetailFormatter.SegmentKind.ExitCodeRetryable && s.Text == "(exit 0x8A15002B)");

        // reassembling every segment's text must reproduce the original string exactly, formatting
        // never drops or duplicates a single character of the raw detail
        Assert.Equal(line, string.Concat(segments.Select(s => s.Text)));
    }

    [Fact]
    public void A_generic_access_denied_exit_code_is_fatal_not_retryable()
    {
        const string line = "[install] winget install failed for 'Contoso.App' (exit 0x80070005). Access is denied.";

        var segments = ErrorDetailFormatter.Format(line);

        Assert.Contains(segments, s =>
            s.Kind == ErrorDetailFormatter.SegmentKind.ExitCodeFatal && s.Text == "(exit 0x80070005)");
        Assert.DoesNotContain(segments, s => s.Kind == ErrorDetailFormatter.SegmentKind.ExitCodeRetryable);
    }

    /// <summary>The simpler shape every other CLI source's InvalidOperationException carries (CliUpdateSource).</summary>
    [Fact]
    public void A_plain_source_prefixed_message_still_gets_its_tool_name_highlighted()
    {
        const string line = "pip: install failed for 'headroom-ai'. ModuleNotFoundError: No module named 'pip' (exit code 1)";

        var segments = ErrorDetailFormatter.Format(line);

        Assert.Contains(segments, s => s.Kind == ErrorDetailFormatter.SegmentKind.ToolName && s.Text == "pip");
        Assert.Contains(segments, s =>
            (s.Kind == ErrorDetailFormatter.SegmentKind.ExitCodeFatal || s.Kind == ErrorDetailFormatter.SegmentKind.ExitCodeRetryable)
            && s.Text.Contains("exit code 1"));
    }

    /// <summary>
    /// A real OpenJDK error: a bracket pair mid-sentence from winget's OWN installer output, and
    /// the word "winget" appearing both mid-sentence and at a line start with no rung label before it,
    /// none of those are this app's own markers and none may be highlighted, or the highlighting turns
    /// indiscriminate.
    /// </summary>
    [Fact]
    public void Foreign_brackets_and_mid_sentence_winget_are_never_highlighted()
    {
        var text = string.Join(Environment.NewLine,
        [
            "[upgrade] winget install failed for 'Microsoft.OpenJDK.17' (exit 0x8A15002B). " +
                "Downloading https://example.com/jdk [mirror-1] failed, retrying via winget servers.",
            "winget offers no version of this package that fits this installation (architecture, " +
                "install type, or scope) \u2014 not even version 17.0.9, the newest one it knows about, " +
                "which was tried and refused for the same reason.",
        ]);

        var segments = ErrorDetailFormatter.Format(text);

        var rungLabels = segments.Where(s => s.Kind == ErrorDetailFormatter.SegmentKind.RungLabel).Select(s => s.Text).ToList();
        Assert.Equal(["[upgrade]"], rungLabels);
        Assert.DoesNotContain(rungLabels, l => l == "[mirror-1]");

        var toolNames = segments.Where(s => s.Kind == ErrorDetailFormatter.SegmentKind.ToolName).Select(s => s.Text).ToList();
        Assert.Equal(["winget"], toolNames);

        Assert.Equal(text, string.Concat(segments.Select(s => s.Text)));
    }

    /// <summary>A word that merely precedes a colon but is not a real source id (e.g. free text from a
    /// tool's own output) must not be mistaken for that tool's "id: message" prefix.</summary>
    [Fact]
    public void An_unrelated_word_before_a_colon_is_not_mistaken_for_a_source_id()
    {
        var segments = ErrorDetailFormatter.Format("Note: this may take a while.");

        Assert.DoesNotContain(segments, s => s.Kind == ErrorDetailFormatter.SegmentKind.ToolName);
    }

    /// <summary>A whole multi-rung ladder, every attempt on its own line, joined the way LadderFailure does.</summary>
    [Fact]
    public void Every_rung_of_a_multi_line_ladder_gets_its_own_label()
    {
        var ladder = string.Join(Environment.NewLine,
        [
            "[upgrade] winget install failed for 'Contoso.App' (exit 0x8A15002B). No applicable upgrade found.",
            "[upgrade (user scope)] winget install failed for 'Contoso.App' (exit 1603). Fatal error.",
            "[install] winget install failed for 'Contoso.App' (exit 1603). Fatal error.",
        ]);

        var segments = ErrorDetailFormatter.Format(ladder);

        var labels = segments.Where(s => s.Kind == ErrorDetailFormatter.SegmentKind.RungLabel).Select(s => s.Text).ToList();
        Assert.Equal(["[upgrade]", "[upgrade (user scope)]", "[install]"], labels);
        Assert.Equal(ladder, string.Concat(segments.Select(s => s.Text)));
    }
}
