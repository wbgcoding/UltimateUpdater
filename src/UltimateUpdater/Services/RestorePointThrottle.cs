using System.Globalization;
using System.IO;
using System.Text.Json;

namespace UltimateUpdater.Services;

/// <summary>
/// The at-most-once-per-24-hours restore point decision, shared by every caller that can trigger one:
/// installing (<see cref="InstallQueue"/>) and cleaning up registry entries or running a system repair
/// (<see cref="ViewModels.CleanupViewModel"/>). One timestamp file, because the rule is about how often
/// this app asks Windows for a restore point, not about which feature asked. Written by the app only,
/// never by an elevated worker: the worker reports over the pipe that it created one, and this class
/// turns that report into the next decision.
/// </summary>
public static class RestorePointThrottle
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static readonly string TimestampPath = Path.Combine(AppPaths.DataDirectory, "restore-point.json");

    private sealed record RestorePointRecord(DateTimeOffset CreatedAt);

    /// <summary>
    /// Whether a new restore point is worth the wait: Windows refuses one inside the same day anyway, and
    /// each costs up to a minute. Pure, for tests. No record (never created, or unreadable) says yes. A
    /// timestamp in the future (corrupt file, clock jumped back, a same-user edit) reads as no timestamp,
    /// never as "just created", or the throttle would refuse every restore point until that date.
    /// </summary>
    public static bool ShouldCreate(DateTimeOffset? lastCreated, DateTimeOffset now) =>
        lastCreated is null || lastCreated.Value > now || now - lastCreated.Value >= TimeSpan.FromHours(24);

    /// <summary>Best effort: a missing, damaged or locked file reads as "never created", the safe direction to
    /// be wrong in (an extra restore point costs time, a skipped one costs nothing).</summary>
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

    /// <summary>Temp file then rename, the atomic-write shape of the app's other small state files.</summary>
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
    /// The one line the pre-flight dialog and the cleanup window header both show. The unelevated app cannot
    /// ask Windows whether a restore point exists (that needs admin), so the only thing it can honestly read
    /// back is this timestamp file, written after this app created one. No record reads as "never created by
    /// this program", not "the system has none".
    /// </summary>
    public static string DescribeLastCreated(LoggingService logging) =>
        TryReadLastCreated(logging) is { } lastCreated
            ? string.Format(LocalizationService.Instance["RestorePointLast"], lastCreated.ToLocalTime().ToString("g", CultureInfo.CurrentUICulture))
            : LocalizationService.Instance["RestorePointNone"];
}
