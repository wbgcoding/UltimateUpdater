using System.Globalization;
using System.Text.RegularExpressions;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Services;

/// <summary>
/// Splits a failed install/uninstall's raw detail text into coloured segments for the error-detail
/// dialog. Pure and read-only,
/// the string itself is never touched (it is also written to history/log verbatim), only formatted at
/// render time. Built around winget's own multi-rung <c>[label] winget install failed for '…'
/// (exit 0x…). …</c> shape, but degrades gracefully on the simpler <c>{source}: …</c> shape every
/// other CLI source throws, and to one Plain segment for anything neither pattern matches.
/// A tool-name segment deliberately does NOT carry the source's brand colour: this formatter only
/// ever sees free text, not a source id, and matching a brand colour by guessing which word in a
/// sentence names the tool would be exactly the kind of similarity match `BrandIcons` refuses to do
/// for icons. Tool names get a plain, consistent emphasis colour instead.
/// </summary>
public static class ErrorDetailFormatter
{
    public enum SegmentKind
    {
        Plain,
        RungLabel,
        ExitCodeFatal,
        ExitCodeRetryable,
        ToolName,
    }

    public readonly record struct Segment(string Text, SegmentKind Kind);

    /// <summary>Only at the START of a line, a lookbehind for the line start plus optional indent, so
    /// the bracket text itself (not the indent) is what gets highlighted. Otherwise any bracket pair a
    /// foreign tool happened to print mid-sentence was marked as if it were one of THIS app's own rung
    /// labels.</summary>
    private static readonly Regex RungLabelPattern = new(
        @"(?<=^[ \t]*)\[[^\]\r\n]+\]", RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex ExitCodePattern = new(
        @"\(exit (0x[0-9A-Fa-f]+|-?\d+)\)|exit(?:ed)?\s*(?:with)?\s*code\s*(0x[0-9A-Fa-f]+|-?\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Either a leading "identifier:", kept only when that identifier is an actual source id
    /// (<see cref="SourceNames.IsKnownId"/>), not just any word before a colon, or the literal word
    /// "winget" directly after its own rung label's closing bracket, never mid-sentence.</summary>
    private static readonly Regex ToolNamePattern = new(
        @"^\s*([a-zA-Z][\w.-]*)(?=:)|(?<=\][ \t]+)winget\b",
        RegexOptions.Compiled | RegexOptions.Multiline);

    public static IReadOnlyList<Segment> Format(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return [];

        var candidates = new List<(int Start, int Length, SegmentKind Kind)>();

        foreach (Match match in RungLabelPattern.Matches(raw))
            candidates.Add((match.Index, match.Length, SegmentKind.RungLabel));

        foreach (Match match in ExitCodePattern.Matches(raw))
        {
            var codeText = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            var retryable = TryParseExitCode(codeText, out var code)
                && (WingetSource.DidNothing(code) || WingetSource.NeedsUserScopeRetry(match.Value));
            candidates.Add((match.Index, match.Length, retryable ? SegmentKind.ExitCodeRetryable : SegmentKind.ExitCodeFatal));
        }

        foreach (Match match in ToolNamePattern.Matches(raw))
        {
            // group 1 only participates for the "identifier:" alternative, the winget-after-bracket
            // alternative has nothing to filter, it is already anchored to a rung label.
            if (match.Groups[1].Success && !SourceNames.IsKnownId(match.Groups[1].Value))
                continue;

            candidates.Add((match.Index, match.Length, SegmentKind.ToolName));
        }

        // Earliest start wins; a later match beginning inside an already-claimed span is dropped
        // rather than splitting it (this is why a rung label's own brackets can never be re-cut by
        // the tool-name pattern even though both can start close together).
        candidates.Sort((a, b) => a.Start != b.Start ? a.Start - b.Start : b.Length - a.Length);
        var chosen = new List<(int Start, int Length, SegmentKind Kind)>();
        var cursor = 0;
        foreach (var candidate in candidates)
        {
            if (candidate.Start < cursor)
                continue;

            chosen.Add(candidate);
            cursor = candidate.Start + candidate.Length;
        }

        var segments = new List<Segment>();
        var position = 0;
        foreach (var (start, length, kind) in chosen)
        {
            if (start > position)
                segments.Add(new Segment(raw[position..start], SegmentKind.Plain));

            segments.Add(new Segment(raw.Substring(start, length), kind));
            position = start + length;
        }

        if (position < raw.Length)
            segments.Add(new Segment(raw[position..], SegmentKind.Plain));

        return segments;
    }

    /// <summary>Hex digits parse as the exit code's raw bit pattern (matching how the app already
    /// prints it, "0x8A15002B"), the same way <see cref="int.Parse(string, NumberStyles)"/> with
    /// <see cref="NumberStyles.HexNumber"/> always has, it is not a magnitude, it is the bits.</summary>
    private static bool TryParseExitCode(string text, out int code) =>
        text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code)
            : int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out code);
}
