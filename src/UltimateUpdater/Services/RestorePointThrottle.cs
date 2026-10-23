using System.Globalization;
using System.IO;
using System.Text.Json;

namespace UltimateUpdater.Services;

/// <summary>
/// The at-most-once-per-24-hours restore point decision, shared by every caller that can trigger
/// one: installing (<see cref="InstallQueue"/>) and cleaning up registry entries or running
/// a system repair (<see cref="ViewModels.CleanupViewModel"/>). ONE timestamp file, not one per caller,
/// because the 24-hour rule is about how often THIS APP asks Windows for a restore point, not about which
/// feature happened to ask for it; a cleanup run five minutes after an install already got its restore
/// point has nothing new to protect.
/// Written by the APP only, never an elevated worker (the worker writes no files of its own beyond
/// cleanup's own deletions) - every worker only ever REPORTS that it created one, over the pipe, and this
/// class is what turns that report into the next decision.
/// </summary>
public static class RestorePointThrottle
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static readonly string TimestampPath = Path.Combine(AppPaths.DataDirectory, "restore-point.json");

    private sealed record RestorePointRecord(DateTimeOffset CreatedAt);

    /// <summary>
    /// Whether a NEW restore point is worth the wait: Windows itself refuses one anyway inside the same
    /// day, and each one costs up to a minute before the first real step starts. A pure function so it can
    /// be driven directly by a test. No record yet (never created, or the file could not be read) always
    /// says yes. A stored timestamp in the FUTURE (a corrupted file, a clock that jumped back,
    /// or a same-user edit of the unprivileged app's own state file) must read the same as no timestamp at
    /// all, never as "just created" - the naive subtraction below would otherwise go negative and this
    /// throttle would silently refuse every restore point until that future date arrives.
    /// </summary>
    public static bool ShouldCreate(DateTimeOffset? lastCreated, DateTimeOffset now) =>
        lastCreated is null || lastCreated.Value > now || now - lastCreated.Value >= TimeSpan.FromHours(24);

    /// <summary>Best effort: a missing, damaged or locked file reads the same as "never created", the safe
    /// direction to be wrong in (an extra restore point costs time, a skipped one costs nothing at all to
    /// actually have).</summary>
    public static DateTimeOffset? TryReadLastCreated(LoggingService logging) =>
        TryReadLastCreated(logging, TimestampPath);

    /// <summary>A record is a single timestamp; anything larger is corrupt, and reading it unbounded
    /// could exhaust memory right before an install or cleanup.</summary>
    internal const long MaxRecordFileBytes = 4096;

    internal static DateTimeOffset? TryReadLastCreated(LoggingService logging, string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            var text = AtomicFile.TryReadAllText(path, MaxRecordFileBytes);
            if (text is null)
            {
                logging.Info("Restore point timestamp file is missing or too large; treating it as never created.");
                return null;
            }

            var record = JsonSerializer.Deserialize<RestorePointRecord>(text, JsonOptions);
            return record?.CreatedAt;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logging.Info($"Could not read the last restore point timestamp: {ex.Message}");
            return null;
        }
    }

    /// <summary>Temp file then rename, the same atomic-write shape every other small state file in this
    /// app already uses (see <see cref="SettingsService"/>, <see cref="EnrichmentCache"/>).</summary>
    public static void TryWriteCreated(LoggingService logging, DateTimeOffset timestamp)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            AtomicFile.WriteAllText(TimestampPath, JsonSerializer.Serialize(new RestorePointRecord(timestamp), JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logging.Warn($"Could not record when the restore point was created: {ex.Message}");
        }
    }

    /// <summary>
    /// The one line the pre-flight dialog and the cleanup window header both show. The unelevated
    /// app cannot ask Windows directly whether a restore point exists (that needs admin, same as actually
    /// creating one) - this timestamp file, written only after THIS app itself created one, is the only
    /// thing it can honestly read back. No record at all reads as "never created by this program", not as
    /// "the system truly has none": Windows or another program may well have created points of their own
    /// that this file knows nothing about.
    /// </summary>
    public static string DescribeLastCreated(LoggingService logging) =>
        TryReadLastCreated(logging) is { } lastCreated
            ? string.Format(LocalizationService.Instance["RestorePointLast"], lastCreated.ToLocalTime().ToString("g", CultureInfo.CurrentUICulture))
            : LocalizationService.Instance["RestorePointNone"];
}
