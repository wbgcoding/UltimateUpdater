using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

/// <summary>The record is read from an explicit path, never from the real data folder.</summary>
public class RestorePointRecordReadTests
{
    [Fact]
    public void A_valid_record_returns_the_stored_timestamp_and_an_oversized_one_returns_null()
    {
        var directory = Directory.CreateTempSubdirectory("uu-restore-").FullName;
        try
        {
            var logging = new LoggingService(_ => { });
            var valid = Path.Combine(directory, "valid.json");
            File.WriteAllText(valid, """{"createdAt":"2026-09-01T08:00:00+00:00"}""");
            var huge = Path.Combine(directory, "huge.json");
            File.WriteAllText(huge, new string(' ', 10 * 1024));

            Assert.Equal(
                new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero),
                RestorePointThrottle.TryReadLastCreated(logging, valid));
            Assert.Null(RestorePointThrottle.TryReadLastCreated(logging, huge));
            Assert.Null(RestorePointThrottle.TryReadLastCreated(logging, Path.Combine(directory, "missing.json")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
