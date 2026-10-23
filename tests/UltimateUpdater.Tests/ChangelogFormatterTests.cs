using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

public class ChangelogFormatterTests
{
    [Fact]
    public void Strip_removes_headings_bold_italic_and_inline_code()
    {
        var result = ChangelogFormatter.Strip("# Heading\n**bold** and *italic* and `code`");

        Assert.Equal("Heading\nbold and italic and code", result);
    }

    [Fact]
    public void Strip_turns_a_markdown_link_into_text_with_the_url_in_parentheses()
    {
        var result = ChangelogFormatter.Strip("See [the docs](https://example.com/docs) for details.");

        Assert.Equal("See the docs (https://example.com/docs) for details.", result);
    }

    [Fact]
    public void Strip_turns_bullets_into_a_plain_marker()
    {
        var result = ChangelogFormatter.Strip("- first\n* second\n+ third");

        Assert.Equal("• first\n• second\n• third", result);
    }

    /// <summary>The fence markers go; the code itself stays as plain text, dropping a real code sample
    /// from a changelog would lose information the "just strip punctuation" design otherwise keeps.</summary>
    [Fact]
    public void Strip_removes_the_code_fence_markers_but_keeps_the_code_itself()
    {
        var result = ChangelogFormatter.Strip("before\n```csharp\nvar x = 1;\n```\nafter");

        Assert.DoesNotContain("```", result);
        Assert.Contains("var x = 1;", result);
    }

    [Fact]
    public void Strip_collapses_more_than_two_blank_lines()
    {
        var result = ChangelogFormatter.Strip("one\n\n\n\n\ntwo");

        Assert.Equal("one\n\ntwo", result);
    }

    [Fact]
    public void Strip_returns_empty_for_null_or_blank_input()
    {
        Assert.Equal(string.Empty, ChangelogFormatter.Strip(null));
        Assert.Equal(string.Empty, ChangelogFormatter.Strip("   "));
    }

    /// <summary>This text is fetched from the network, a terminal escape sequence or a stray control
    /// byte in a release body must not reach whatever eventually renders it.</summary>
    [Fact]
    public void Strip_removes_control_characters_but_keeps_newlines_and_tabs()
    {
        var withControlChars = "before\x1B[31mred\x1B[0m\x00null\tafter\nline2";

        var result = ChangelogFormatter.Strip(withControlChars);

        Assert.Equal("before[31mred[0mnull\tafter\nline2", result);
    }

    [Fact]
    public void Strip_caps_pathologically_long_text()
    {
        var huge = new string('a', 30_000);

        var result = ChangelogFormatter.Strip(huge);

        Assert.True(result.Length <= 20_001);
        Assert.EndsWith("…", result);
    }

    [Fact]
    public void Strip_does_not_truncate_text_under_the_cap()
    {
        var normal = string.Concat(Enumerable.Repeat("line\n", 100));

        var result = ChangelogFormatter.Strip(normal);

        Assert.DoesNotContain("…", result);
    }

    [Theory]
    [InlineData("Set max_parallel_installs to 2", "Set max_parallel_installs to 2")]
    [InlineData("See [docs](https://x.com/a_b_c)", "See docs (https://x.com/a_b_c)")]
    [InlineData("Fixed `__init__` import", "Fixed __init__ import")]
    [InlineData("This is _really_ and __very__ important", "This is really and very important")]
    [InlineData("**bold** and *italic*", "bold and italic")]
    public void Strip_only_treats_underscores_as_emphasis_at_word_boundaries(string input, string expected) =>
        Assert.Equal(expected, ChangelogFormatter.Strip(input));

    /// <summary>Third-party text full of unclosed openers must not stall the window it is shown in: every
    /// opener used to scan the rest of its line for a closer, quadratic in the line length.</summary>
    [Theory]
    [InlineData("*a ")]
    [InlineData("_a ")]
    [InlineData("[a ")]
    [InlineData("[a](")]
    public void Strip_stays_fast_on_a_long_line_of_unclosed_openers(string unit)
    {
        var input = string.Concat(Enumerable.Repeat(unit, 60_000 / unit.Length));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = ChangelogFormatter.Strip(input);
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"took {watch.Elapsed.TotalSeconds:0.0} s");
        Assert.NotEmpty(result);
    }
}
