using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

/// <summary>A fire-and-forget async start must never lose an exception silently.</summary>
public class BackgroundTests
{
    [Fact]
    public async Task A_throwing_task_is_caught_and_logged_once_with_no_rethrow()
    {
        var lines = new List<string>();
        var logging = new LoggingService(line => lines.Add(line));

        await Background.Run(ThrowAsync(), logging, "test task");

        var logged = Assert.Single(lines);
        Assert.Contains("test task", logged);
        Assert.Contains("boom", logged);
    }

    [Fact]
    public async Task A_cancelled_task_logs_nothing()
    {
        var lines = new List<string>();
        var logging = new LoggingService(line => lines.Add(line));

        await Background.Run(CancelAsync(), logging, "test task");

        Assert.Empty(lines);
    }

    [Fact]
    public async Task A_successful_task_logs_nothing()
    {
        var lines = new List<string>();
        var logging = new LoggingService(line => lines.Add(line));

        await Background.Run(Task.CompletedTask, logging, "test task");

        Assert.Empty(lines);
    }

    private static async Task ThrowAsync()
    {
        await Task.Yield();
        throw new InvalidOperationException("boom");
    }

    private static async Task CancelAsync()
    {
        await Task.Yield();
        throw new OperationCanceledException();
    }
}
