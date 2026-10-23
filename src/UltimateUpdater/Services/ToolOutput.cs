using System.Linq;

namespace UltimateUpdater.Services;

/// <summary>
/// Turns a failed tool's captured output into a short, USEFUL diagnostic. The real reason an install
/// failed is almost always the last thing the tool printed (winget in particular prints screens of
/// source-agreement and download-progress noise first), so a naive "first line" summary showed the
/// banner and hid the error. This keeps the tail, drops the progress-bar noise, and always ends with
/// the exit code: so a failed row's Details view, and the log, are never empty.
/// </summary>
public static class ToolOutput
{
    private const int MaxTailLines = 12;

    public static string Summarize(string? standardOutput, string? standardError, int exitCode, int maxChars = 800)
    {
        // stderr first (where a tool that separates its streams puts the actual error) then stdout.
        var lines = Lines(standardError)
            .Concat(Lines(standardOutput))
            .Where(line => line.Length > 0 && !IsNoise(line))
            .ToList();

        var tail = lines.Count > MaxTailLines ? lines.Skip(lines.Count - MaxTailLines) : lines;
        var body = string.Join(" · ", tail).Trim();

        if (body.Length > maxChars)
            body = "…" + body[^maxChars..];

        return body.Length == 0
            ? $"The tool exited with code {exitCode} and produced no output."
            : $"{body} (exit code {exitCode})";
    }

    private static IEnumerable<string> Lines(string? text) =>
        string.IsNullOrEmpty(text)
            ? []
            : text.Replace("\r\n", "\n").Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim());

    /// <summary>
    /// A repainted progress line (a run of bar glyphs, spinner characters or a bare percentage) carries
    /// no diagnostic value and would otherwise crowd out the real error in the tail.
    /// </summary>
    private static bool IsNoise(string line)
    {
        var meaningful = line.Where(c => !char.IsWhiteSpace(c)).ToList();
        if (meaningful.Count == 0)
            return true;

        // bars/spinners winget and others animate the download with
        return meaningful.All(c =>
            c is '-' or '\\' or '/' or '|' or '.' or '%' or '#' or '='
              or '█' or '▒' or '░' or '▓' or '▄' or '■' or '□'
            || char.IsDigit(c));
    }
}
