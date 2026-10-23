using System.Text.RegularExpressions;

namespace UltimateUpdater.Services;

/// <summary>
/// Strips the basic Markdown a GitHub release body is written in down to plain, readable text.
/// Deliberately not a Markdown renderer: headings, emphasis and links just lose their punctuation;
/// nothing is reformatted or reflowed. Pure and safe on anything, including text that never had any
/// Markdown in it at all.
/// </summary>
public static class ChangelogFormatter
{
    private static readonly Regex CodeFence = new(@"```[^\n]*\n?", RegexOptions.Compiled);
    private static readonly Regex Heading = new(@"^\s{0,3}#{1,6}\s*", RegexOptions.Compiled | RegexOptions.Multiline);
    // Underscore emphasis only counts at word boundaries (CommonMark's rule): "max_parallel_installs" or a URL
    // like ".../a_b_c" must keep its underscores. Asterisks may still emphasise inside a word.
    // Every span is bounded: an unbounded one made each unclosed opener scan the rest of its line, so a long
    // line of them took seconds to minutes. Emphasis or a link longer than the bound simply stays as written.
    private static readonly Regex BoldItalic = new(
        @"(\*\*\*|\*\*|\*)(?=\S)(.{0,500}?)(?<=\S)\1|(?<![\p{L}\p{N}_])(___|__|_)(?=\S)(.{0,500}?)(?<=\S)\3(?![\p{L}\p{N}_])",
        RegexOptions.Compiled);
    private static readonly Regex InlineCode = new(@"`([^`]*)`", RegexOptions.Compiled);
    private static readonly Regex CodePlaceholder = new("\u0001([0-9]+)\u0001", RegexOptions.Compiled);
    private static readonly Regex Link = new(@"\[([^\]]{0,500})\]\(([^)]{0,2000})\)", RegexOptions.Compiled);
    private static readonly Regex Bullet = new(@"^\s*[-*+]\s+", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex ExtraBlankLines = new(@"\n{3,}", RegexOptions.Compiled);

    // This text comes from wherever the source found it: a GitHub release body, a winget manifest
    // field. It is never typed by anyone in this app, so it is treated as hostile: control characters
    // (a terminal escape sequence, a stray null byte) are dropped, and a hard length cap keeps one
    // pathological entry from making the popup unusably long.
    private static readonly Regex ControlCharacters = new(@"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]", RegexOptions.Compiled);
    private const int MaxLength = 20_000;
    private const int MaxInputLength = 4 * MaxLength;

    public static string Strip(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return string.Empty;

        // Cut well before the passes below: the result is capped anyway, and nothing past this point of a
        // release body could still reach it.
        var text = markdown.Length > MaxInputLength ? markdown[..MaxInputLength] : markdown;
        text = text.Replace("\r\n", "\n");
        text = ControlCharacters.Replace(text, string.Empty);
        text = CodeFence.Replace(text, string.Empty);
        text = Heading.Replace(text, string.Empty);
        text = Link.Replace(text, m => m.Groups[2].Value.Length > 0 ? $"{m.Groups[1].Value} ({m.Groups[2].Value})" : m.Groups[1].Value);
        // Code spans are set aside while emphasis is stripped, so "`__init__`" keeps its underscores. The
        // placeholder uses a control character, which cannot occur here any more (dropped above).
        var codeSpans = new List<string>();
        text = InlineCode.Replace(text, m =>
        {
            codeSpans.Add(m.Groups[1].Value);
            return $"\u0001{codeSpans.Count - 1}\u0001";
        });
        text = BoldItalic.Replace(text, m => m.Groups[2].Success ? m.Groups[2].Value : m.Groups[4].Value);
        text = CodePlaceholder.Replace(text, m => codeSpans[int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)]);
        text = Bullet.Replace(text, "• ");
        text = ExtraBlankLines.Replace(text, "\n\n");
        text = text.Trim();

        return text.Length > MaxLength ? text[..MaxLength].TrimEnd() + "…" : text;
    }
}
