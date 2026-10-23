using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using UltimateUpdater.Services.SelfUpdate;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Services;

/// <summary>
/// Builds a plain-text, offline error report for one failed row, everything a bug report
/// needs without a screenshot: the package, its version change, the exit code (read the same way
/// <see cref="InstallErrorExplainer"/> itself does, so both surfaces agree on the same number), the
/// tool's own output capped at <see cref="MaxOutputLines"/> lines, the app version, the Windows version
/// and the CPU architecture. Pure formatting only, no network (this app's own rule: a source never
/// fetches anything just to check itself, and that includes reporting on itself). The tool output is
/// redacted the SAME way the log already is (<see cref="LoggingService.Redact"/>) before it ever
/// reaches the clipboard or disk, so no user path leaves the machine either way.
/// </summary>
public static class ErrorReportBuilder
{
    internal const int MaxOutputLines = 200;

    private const string ReportFilePrefix = "error-report-";
    private const string ReportFileSuffix = ".txt";

    /// <summary>Last ten reports live here, oldest pruned on every save.</summary>
    public static string ReportsDirectory { get; } = Path.Combine(AppPaths.DataDirectory, "reports");

    public static string Build(string name, string sourceDisplay, string? versionChange, string? toolOutput)
    {
        var lines = new List<string> { $"Package: {name} ({sourceDisplay})" };

        if (!string.IsNullOrEmpty(versionChange))
            lines.Add($"Version: {versionChange}");

        if (InstallErrorExplainer.TryExtractCode(toolOutput) is { } exitCode)
            lines.Add($"Exit code: {exitCode}");

        lines.Add($"App version: {SelfUpdateService.CurrentVersion}");
        lines.Add($"Windows: {WindowsVersionText()}");
        lines.Add($"Architecture: {RuntimeInformation.OSArchitecture}");
        lines.Add(string.Empty);
        lines.Add("Output:");
        lines.AddRange(FormatOutput(toolOutput));

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Writes the report to <see cref="ReportsDirectory"/> and prunes down to the last ten, oldest
    /// first (the file name itself is the sort key: a fixed-width timestamp sorts chronologically as
    /// plain text, no need to read each file's own metadata back). Best-effort: a full disk or a
    /// permissions problem here must never block the copy the user actually asked for.
    /// </summary>
    public static void Save(string report, LoggingService logging, string? directory = null)
    {
        var dir = directory ?? ReportsDirectory;
        try
        {
            Directory.CreateDirectory(dir);
            var fileName = $"{ReportFilePrefix}{DateTime.Now:yyyyMMdd-HHmmss-fff}{ReportFileSuffix}";
            File.WriteAllText(Path.Combine(dir, fileName), report);
            Prune(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logging.Warn($"Could not save the error report: {ex.Message}");
        }
    }

    /// <summary>Keeps only the ten newest reports in <paramref name="directory"/>; internal so the
    /// pruning rule itself is testable without going through a real save.</summary>
    internal static void Prune(string directory, int keep = 10)
    {
        var stale = Directory.GetFiles(directory, $"{ReportFilePrefix}*{ReportFileSuffix}")
            .OrderByDescending(path => path, StringComparer.Ordinal)
            .Skip(keep);

        foreach (var path in stale)
            File.Delete(path);
    }

    private static IEnumerable<string> FormatOutput(string? toolOutput)
    {
        if (string.IsNullOrWhiteSpace(toolOutput))
            yield break;

        var redacted = LoggingService.Redact(toolOutput);
        foreach (var line in redacted.Replace("\r\n", "\n").Split('\n').Take(MaxOutputLines))
            yield return line;
    }

    /// <summary>
    /// <see cref="Environment.OSVersion"/> alone reports the same build number for every Windows 10/11
    /// feature update; the actual per-update revision (what Settings calls "OS build") is the registry's
    /// own UBR value. Best-effort: a locked-down
    /// machine that refuses the read still gets a report, just without the last ".UBR" segment.
    /// </summary>
    private static string WindowsVersionText()
    {
        var version = Environment.OSVersion.Version;
        var ubr = ReadUbr();
        return ubr is { } value
            ? $"{version.Major}.{version.Minor}.{version.Build}.{value}"
            : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    private static int? ReadUbr()
    {
        try
        {
            return Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "UBR", null) as int?;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
