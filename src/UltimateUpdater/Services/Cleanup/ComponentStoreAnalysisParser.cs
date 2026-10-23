using System.Globalization;

namespace UltimateUpdater.Services.Cleanup;

/// <summary>
/// Turns the text <c>dism.exe /Online /Cleanup-Image /AnalyzeComponentStore</c> prints into a <see
/// cref="ComponentStoreAnalysis"/>. DISM runs in the machine's own Windows display language, which this
/// app cannot choose, so every known line is matched against BOTH the English and the German wording
/// this project ships; a label this parser does not recognise, a value it cannot parse, or a run that
/// never reached a given line at all leaves that one field null rather than guessed. Pure and total:
/// arbitrary or truncated text never throws, it just leaves more fields null.
/// </summary>
public static class ComponentStoreAnalysisParser
{
    /// <summary>One field's every known label, one per shipped language, matched against the trimmed text
    /// before a line's LAST colon (DISM pads labels with spaces up to a fixed column).</summary>
    private static readonly string[] ActualSizeLabels =
    [
        "Actual Size of Component Store",
        "Tatsächliche Größe des Komponentenspeichers",
    ];

    private static readonly string[] SharedWithWindowsLabels =
    [
        "Shared with Windows",
        // Real DISM output on the lab VM prints this word order, not "Für Windows freigegeben"
        // as an earlier guess (never checked against a real run) had it; that wrong guess is why this
        // field came back null on the VM even once the encoding bug below was also fixed.
        "Freigegeben für Windows",
    ];

    private static readonly string[] BackupsLabels =
    [
        "Backups and Disabled Features",
        "Sicherungen und deaktivierte Features",
    ];

    private static readonly string[] RecommendedLabels =
    [
        "Component Store Cleanup Recommended",
        "Bereinigung des Komponentenspeichers empfohlen",
    ];

    private static readonly string[] YesWords = ["Yes", "Ja"];
    private static readonly string[] NoWords = ["No", "Nein"];

    private static readonly (string Unit, long Bytes)[] SizeUnits =
    [
        ("TB", 1024L * 1024 * 1024 * 1024),
        ("GB", 1024L * 1024 * 1024),
        ("MB", 1024L * 1024),
        ("KB", 1024L),
        ("B", 1L),
    ];

    public static ComponentStoreAnalysis Parse(string? dismOutput)
    {
        if (string.IsNullOrWhiteSpace(dismOutput))
            return ComponentStoreAnalysis.Unknown;

        long? actualSize = null;
        long? sharedWithWindows = null;
        long? backups = null;
        bool? recommended = null;

        foreach (var rawLine in dismOutput.Split('\n'))
        {
            var line = rawLine.Trim('\r', '\n', ' ', '\t');
            if (line.Length == 0)
                continue;

            var colon = line.LastIndexOf(':');
            if (colon < 0 || colon == line.Length - 1)
                continue;

            var label = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (label.Length == 0 || value.Length == 0)
                continue;

            if (actualSize is null && MatchesAny(label, ActualSizeLabels))
                actualSize = ParseSize(value);
            else if (sharedWithWindows is null && MatchesAny(label, SharedWithWindowsLabels))
                sharedWithWindows = ParseSize(value);
            else if (backups is null && MatchesAny(label, BackupsLabels))
                backups = ParseSize(value);
            else if (recommended is null && MatchesAny(label, RecommendedLabels))
                recommended = ParseYesNo(value);
        }

        return new ComponentStoreAnalysis(actualSize, sharedWithWindows, backups, recommended);
    }

    private static bool MatchesAny(string label, string[] knownLabels) =>
        knownLabels.Any(known => string.Equals(label, known, StringComparison.OrdinalIgnoreCase));

    private static bool? ParseYesNo(string value)
    {
        if (YesWords.Any(word => string.Equals(value, word, StringComparison.OrdinalIgnoreCase)))
            return true;

        if (NoWords.Any(word => string.Equals(value, word, StringComparison.OrdinalIgnoreCase)))
            return false;

        return null;
    }

    /// <summary>"4.13 GB" (English) or "4,13 GB" (German) into bytes, 1024-based like <see
    /// cref="ByteSize"/> already uses everywhere else in this app. Anything else (a missing unit, a
    /// number that will not parse) is unknown rather than guessed.</summary>
    private static long? ParseSize(string value)
    {
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
            return null;

        var (unit, multiplier) = SizeUnits.FirstOrDefault(
            u => string.Equals(u.Unit, parts[1], StringComparison.OrdinalIgnoreCase));
        if (unit is null)
            return null;

        // The decimal separator follows whichever language DISM printed in; a comma here is never a
        // thousands grouping, DISM's own figures never reach four digits before the point.
        var numberText = parts[0].Replace(',', '.');
        if (!double.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            || number < 0)
            return null;

        return (long)Math.Round(number * multiplier);
    }
}
