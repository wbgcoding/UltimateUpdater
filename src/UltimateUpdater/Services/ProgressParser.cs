using System.Globalization;
using System.Text.RegularExpressions;

namespace UltimateUpdater.Services;

/// <summary>A progress reading pulled from one segment of tool output; any field may be unknown.</summary>
public readonly record struct ProgressSample(double? Percent, long? BytesReceived, long? BytesTotal);

/// <summary>
/// Pulls a progress reading out of one line/segment of a package manager's console output. Pure and
/// defensive (SECURITY.md): unrecognised text yields null, never an exception. Segments are split on
/// '\r' as well as '\n' upstream (ProcessRunner.RunStreamingAsync), so the animated download bar is
/// sampled as it repaints rather than only once it ends.
/// </summary>
public static partial class ProgressParser
{
    // "1.23 MB / 45.6 MB"; winget-style, a size unit after each number.
    [GeneratedRegex(@"([\d.,]+)\s*(B|KB|KiB|MB|MiB|GB|GiB)\s*/\s*([\d.,]+)\s*(B|KB|KiB|MB|MiB|GB|GiB)", RegexOptions.IgnoreCase)]
    private static partial Regex TwoUnitTransferRegex();

    // "2.5/5.0 MB"; pip-style, one shared unit after the pair.
    [GeneratedRegex(@"([\d.,]+)\s*/\s*([\d.,]+)\s*(B|KB|KiB|MB|MiB|GB|GiB)", RegexOptions.IgnoreCase)]
    private static partial Regex SharedUnitTransferRegex();

    // A standalone percentage: "45%", "45 %", "45.0%", "66,6%". A comma is accepted as a decimal mark
    // because DISM prints its bar in the machine's own language, and a German Windows writes "66,6%";
    // without this the parser read the "6" after the comma and reported 6 %.
    [GeneratedRegex(@"(?<![\d.,])(\d{1,3}(?:[.,]\d+)?)\s*%")]
    private static partial Regex PercentRegex();

    // A comma immediately followed by exactly three digits with no FOURTH digit right after them (so
    // "1,024.0" and "1,234,567" both qualify, but "1,23", with only two digits, does not): a thousands
    // grouping, not a decimal mark. See TryBytes.
    [GeneratedRegex(@",(?=\d{3}(?!\d))")]
    private static partial Regex ThousandsCommaRegex();

    // Lines the tools print once the download is over and the real install has begun. Matched in English
    // and German because winget, pip and choco localise their output and the app runs on German Windows
    // as often as English. Contains-matches for the long phrases; the generic "Installing"/"Installation"
    // only at the start of a line (after a spinner glyph), so a path or a package name containing the word
    // is not read as a phase change. "Installing build dependencies" is pip preparing a source build in the
    // middle of its downloads, not the install step.
    [GeneratedRegex(
        @"Successfully\s+verified\s+installer\s+hash"
        + @"|Starting\s+package\s+install"
        + @"|Installer-?\s?Hash\b[^\r\n]{0,40}?(erfolgreich|überprüft|verifiziert)"
        + @"|Paketinstallation\s+wird\s+gestartet"
        + @"|Installing\s+collected\s+packages"
        + @"|Installiere\s+gesammelte\s+Pakete"
        + @"|^[\s\-\\|/]*(Installing(?!\s+build\s+dependencies)|Installation|Installiere|Extracting|Entpacke)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex InstallPhaseLineRegex();

    /// <summary>True when the segment is a known "download finished, install starting" line.</summary>
    public static bool IsInstallPhaseLine(string? segment) =>
        !string.IsNullOrWhiteSpace(segment) && InstallPhaseLineRegex().IsMatch(segment);

    public static ProgressSample? Parse(string? segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
            return null;

        var (received, total) = ParseTransfer(segment);

        double? percent = null;
        var pct = PercentRegex().Match(segment);
        if (pct.Success
            && double.TryParse(
                pct.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var p))
            percent = Math.Clamp(p, 0, 100);

        // when only the transfer was printed, derive the percentage from it
        if (percent is null && received is { } r && total is { } t && t > 0)
            percent = Math.Clamp(r * 100.0 / t, 0, 100);

        if (percent is null && received is null && total is null)
            return null;

        return new ProgressSample(percent, received, total);
    }

    /// <summary>
    /// Convenience for the streaming install path: turns one output segment straight into an install
    /// progress report, or null when the segment carries no reading. Keeps the byte/percent glue in
    /// one place instead of repeated in every source.
    /// </summary>
    public static Models.InstallProgress? ToInstallProgress(string? segment, string statusText)
    {
        var sample = Parse(segment);
        var installing = IsInstallPhaseLine(segment);
        if (sample is null && !installing)
            return null;

        // A bare install-phase line carries no figure: percent 0 and no bytes, which the row reads as a
        // status line that moves nothing but the phase.
        return new Models.InstallProgress
        {
            PercentComplete = sample?.Percent ?? 0,
            StatusText = statusText,
            BytesReceived = sample?.BytesReceived,
            BytesTotal = sample?.BytesTotal,
            Phase = installing ? Models.InstallPhase.Installing : Models.InstallPhase.Downloading,
        };
    }

    private static (long? Received, long? Total) ParseTransfer(string segment)
    {
        var two = TwoUnitTransferRegex().Match(segment);
        if (two.Success
            && TryBytes(two.Groups[1].Value, two.Groups[2].Value, out var r2)
            && TryBytes(two.Groups[3].Value, two.Groups[4].Value, out var t2)
            && t2 > 0 && r2 <= t2)
        {
            return (r2, t2);
        }

        var shared = SharedUnitTransferRegex().Match(segment);
        if (shared.Success
            && TryBytes(shared.Groups[1].Value, shared.Groups[3].Value, out var r1)
            && TryBytes(shared.Groups[2].Value, shared.Groups[3].Value, out var t1)
            && t1 > 0 && r1 <= t1)
        {
            return (r1, t1);
        }

        return (null, null);
    }

    private static bool TryBytes(string number, string unit, out long bytes)
    {
        bytes = 0;

        // Tool output runs in whatever locale the machine does (same reasoning as PercentRegex above): a
        // comma followed by exactly three digits is a thousands grouping ("1,234,567") and is dropped;
        // any other comma is a German-style decimal mark ("1,23 MB") and becomes a '.' instead; otherwise
        // "1,23 MB" reads as 123, a hundred times too large.
        var cleaned = ThousandsCommaRegex().Replace(number, string.Empty).Replace(',', '.');
        if (!double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value < 0)
            return false;

        var multiplier = unit.ToUpperInvariant() switch
        {
            "B" => 1d,
            "KB" or "KIB" => 1024d,
            "MB" or "MIB" => 1024d * 1024,
            "GB" or "GIB" => 1024d * 1024 * 1024,
            _ => 0d,
        };
        if (multiplier == 0d)
            return false;

        bytes = (long)(value * multiplier);
        return true;
    }
}
