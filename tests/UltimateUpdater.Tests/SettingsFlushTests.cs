using System.IO;
using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

/// <summary>Flush is the exit path's guarantee that the file is current; a timer save that already cleared
/// the pending flag but has not written yet must not let it return early.</summary>
public sealed class SettingsFlushTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("wu-flush-test-").FullName;
    private readonly LoggingService _logging = new();

    private string SettingsPath => Path.Combine(_tempDir, "settings.json");

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    [Fact]
    public async Task Flush_does_not_return_while_a_timer_write_is_still_in_flight()
    {
        var service = new SettingsService(_logging, SettingsPath);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var calls = 0;
        service.BeforeWriteForTest = () =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(60));
            }
        };

        service.Current.Ui.Theme = "dark";
        service.SaveDeferred();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30)), "the timer save reached its write");

        try
        {
            var flush = Task.Run(service.Flush);
            await Task.Delay(400);
            Assert.False(flush.IsCompleted, "Flush returned while the timer's write was still waiting");

            release.Set();
            await flush.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            release.Set();
        }

        Assert.Contains("\"dark\"", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public async Task A_change_made_after_the_timer_write_started_is_on_disk_after_Flush()
    {
        var service = new SettingsService(_logging, SettingsPath);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var calls = 0;
        service.BeforeWriteForTest = () =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(60));
            }
        };

        service.Current.Ui.Theme = "dark";
        service.SaveDeferred();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));

        try
        {
            service.Current.Ui.Theme = "light";
            service.SaveDeferred();

            var flush = Task.Run(service.Flush);
            await Task.Delay(200);
            release.Set();
            await flush.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            release.Set();
        }

        // The older snapshot finished writing last, and must not have overwritten the newer one.
        await Task.Delay(500);
        Assert.Contains("\"light\"", File.ReadAllText(SettingsPath));
    }
}
