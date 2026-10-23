using UltimateUpdater.Models;

namespace UltimateUpdater.Services;

/// <summary>
/// The wording of everything the app records about what it did. Pure string building, kept in one place
/// so the log reads the same from every caller and so the shapes can be tested without a disk: a line is
/// meant to answer "what went wrong" and "when was this installed" months later, in as few characters as
/// the answer needs.
/// </summary>
public static class ActivityLog
{
    /// <summary>The categories a line can carry, short, because every line pays for them.</summary>
    public const string App = "app";
    public const string Scan = "scan";
    public const string Install = "inst";
    public const string Download = "dl";
    public const string Cleanup = "clean";
    public const string SelfUpdate = "self";

    /// <summary>Source and package together, an item id alone is not unique across sources.</summary>
    public static string Item(UpdateItem item) => Item(item.SourceId, item.Id);

    public static string Item(string sourceId, string itemId) => $"{sourceId}:{itemId}";

    public static string DownloadStarted(string sourceId, string itemId, long? totalBytes) =>
        $"start {Item(sourceId, itemId)}{(totalBytes is > 0 ? $" {ByteSize.FormatInvariant(totalBytes.Value)}" : string.Empty)}";

    public static string DownloadFinished(string sourceId, string itemId, long totalBytes, TimeSpan duration) =>
        duration.TotalSeconds >= 0.5
            ? $"done {Item(sourceId, itemId)} {ByteSize.FormatInvariant(totalBytes)} {Seconds(duration)} "
              + $"{ByteSize.FormatInvariant((long)(totalBytes / duration.TotalSeconds))}/s"
            : $"done {Item(sourceId, itemId)} {ByteSize.FormatInvariant(totalBytes)} {Seconds(duration)}";

    public static string InstallStarted(UpdateItem item, InstallAction action, bool elevated) =>
        $"start {Verb(action)} {Item(item)} {Versions(item, action)}{(elevated ? " admin" : string.Empty)}";

    public static string InstallSucceeded(UpdateItem item, InstallAction action, TimeSpan? duration) =>
        $"ok {Verb(action)} {Item(item)}{Took(duration)}";

    public static string InstallFailed(UpdateItem item, InstallAction action, TimeSpan? duration, string? detail) =>
        $"fail {Verb(action)} {Item(item)}{Took(duration)}{Suffix(detail)}";

    public static string InstallStuck(UpdateItem item, string? detail) =>
        $"stuck {Item(item)}{Suffix(detail)}";

    public static string BatchStarted(int items, bool elevated) =>
        $"batch {items} item{(items == 1 ? string.Empty : "s")}{(elevated ? " admin" : string.Empty)}";

    /// <summary>
    /// The run's own duration, not any one item's: per-item timing
    /// (<see cref="InstallSucceeded"/>/<see cref="InstallFailed"/>) does not answer "how long did the
    /// WHOLE run take" when a batch installs several items back to back.
    /// </summary>
    public static string BatchFinished(int items, TimeSpan duration) =>
        $"batch done {items} item{(items == 1 ? string.Empty : "s")} {Seconds(duration)}";

    public static string ScanFinished(int sources, int found, int failed, TimeSpan duration) =>
        $"done {sources} sources {found} updates{(failed > 0 ? $" {failed} failed" : string.Empty)} {Seconds(duration)}";

    /// <summary>Process start to the main window's first painted frame. The start point is
    /// the real process start (<c>Process.GetCurrentProcess().StartTime</c>), never <c>App.OnStartup</c>,
    /// which begins measurably later.</summary>
    public static string FirstPaint(DateTime processStart, DateTime paintedAt) =>
        $"first paint {Millis(processStart, paintedAt)}ms";

    /// <summary>Process start to the first row shown from last session's cached scan result,
    /// so a slow real scan is never what the window shows first.</summary>
    public static string FirstCachedRow(DateTime processStart, DateTime shownAt) =>
        $"first cached row {Millis(processStart, shownAt)}ms";

    /// <summary>Private working set after a given scan number in this session, read straight
    /// from <c>Process.GetCurrentProcess().WorkingSet64</c> at the moment the scan finished.</summary>
    public static string WorkingSetAfterScan(int scanNumber, long workingSetBytes) =>
        $"working set after scan {scanNumber} {ByteSize.FormatInvariant(workingSetBytes)}";

    private static long Millis(DateTime start, DateTime end) => (long)(end - start).TotalMilliseconds;

    public static string CleanupFinished(long freedBytes, int skippedFiles, bool stopped, TimeSpan duration) =>
        $"{(stopped ? "stopped" : "done")} freed {ByteSize.FormatInvariant(freedBytes)}"
        + $"{(skippedFiles > 0 ? $" skipped {skippedFiles}" : string.Empty)} {Seconds(duration)}";

    public static string SelfUpdateFound(string version) => $"found {version}";

    public static string SelfUpdateDownloaded(string assetName, long bytes) =>
        $"downloaded {assetName} {ByteSize.FormatInvariant(bytes)}";

    public static string SelfUpdateVerified(string version) => $"verified {version}";

    public static string SelfUpdateApplied(string version, bool portable) =>
        $"applied {version} {(portable ? "portable" : "installer")}";

    /// <summary>Left out entirely where nothing measured it, "0.0s" would be a made-up number.</summary>
    private static string Took(TimeSpan? duration) => duration is { } elapsed ? $" {Seconds(elapsed)}" : string.Empty;

    /// <summary>
    /// Whole seconds below a minute, then minutes, a log does not need three decimal places. Invariant,
    /// like every other figure in this file: a diagnostic read on another machine should not change its
    /// decimal separator with the reader's Windows language.
    /// </summary>
    private static string Seconds(TimeSpan duration) =>
        duration.TotalSeconds < 60
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{duration.TotalSeconds:0.0}s")
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{(int)duration.TotalMinutes}m{duration.Seconds:00}s");

    private static string Verb(InstallAction action) => action == InstallAction.Uninstall ? "remove" : "update";

    private static string Versions(UpdateItem item, InstallAction action) =>
        action == InstallAction.Uninstall
            ? item.InstalledVersion ?? "?"
            : $"{item.InstalledVersion ?? "?"}>{item.AvailableVersion ?? "?"}";

    /// <summary>
    /// A failure reason is the one place a line may run long, so it is cut: the full text is in the row's
    /// own details and in the history, and a log line that wraps three times helps nobody read the file.
    /// </summary>
    private static string Suffix(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return string.Empty;

        var single = detail.ReplaceLineEndings(" ").Trim();
        return " " + (single.Length <= 120 ? single : single[..120] + "…");
    }
}
