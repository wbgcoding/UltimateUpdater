using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

/// <summary>
/// Only the quiet startup measuring pass runs in Windows background thread mode. A scan for a window
/// somebody is looking at runs at normal priority, and a window that attaches while the prescan still
/// runs lifts the targets that have not started yet.
/// </summary>
[Collection("UiLanguage")]
public class CleanupScanPriorityTests
{
    /// <summary>Records, per target, what priority the view model asked for. Optionally holds every scan
    /// at the door until the test lets them through.</summary>
    internal sealed class PriorityService(LoggingService logging, Task? gate = null) : CleanupService(logging)
    {
        public ConcurrentDictionary<string, bool> Background { get; } = new();
        public ConcurrentQueue<string> Order { get; } = new();

        public override async Task<CleanupScanResult> ScanAsync(
            CleanupTarget target, CancellationToken cancellationToken, int recycleBinMinAgeDays = 0)
        {
            Background[target.Id] = BackgroundRequestedForTest;
            Order.Enqueue(target.Id);
            if (gate is not null)
                await gate;
            return new CleanupScanResult(0, 0, 0);
        }
    }

    private static CleanupViewModel CreateViewModel(PriorityService service)
    {
        ElevationService.IsElevatedForTest.Value = false;
        Dialogs.SuppressInteractiveDialogs = true;

        var logging = new LoggingService();
        var host = new ElevatedHost(logging);
        host.SetRunningForTest(false);
        host.EnsureStartedOverrideForTest = _ => Task.FromResult(false);
        var settings = new SettingsService(
            logging, Path.Combine(Directory.CreateTempSubdirectory("wu-priority-").FullName, "settings.json"));
        var viewModel = new CleanupViewModel(service, logging, settings, elevated: host);
        WpfHarness.WaitForCleanupCatalog(viewModel);
        return viewModel;
    }

    [Fact]
    public async Task The_startup_prescan_asks_for_background_priority_on_every_target()
    {
        var service = new PriorityService(new LoggingService());
        var viewModel = CreateViewModel(service);

        await viewModel.PrescanAsync();

        Assert.NotEmpty(service.Background);
        Assert.All(service.Background, pair => Assert.True(pair.Value, pair.Key));
    }

    [Fact]
    public async Task A_scan_for_an_open_window_asks_for_normal_priority()
    {
        var service = new PriorityService(new LoggingService());
        var viewModel = CreateViewModel(service);
        viewModel.WindowAttached();

        await viewModel.StartOpenScanAsync();

        Assert.NotEmpty(service.Background);
        Assert.All(service.Background, pair => Assert.False(pair.Value, pair.Key));
    }

    [Fact]
    public async Task The_scan_for_what_a_prescan_left_out_runs_at_normal_priority()
    {
        var service = new PriorityService(new LoggingService());
        var viewModel = CreateViewModel(service);
        await viewModel.PrescanAsync();
        service.Background.Clear();
        viewModel.WindowAttached();

        await viewModel.StartOpenScanAsync();

        Assert.NotEmpty(service.Background);
        Assert.All(service.Background, pair => Assert.False(pair.Value, pair.Key));
    }

    [Fact]
    public async Task A_window_that_attaches_mid_prescan_lifts_the_targets_not_yet_started()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new PriorityService(new LoggingService(), gate.Task);
        var viewModel = CreateViewModel(service);
        var width = Math.Max(1, Math.Min(4, Environment.ProcessorCount));

        var prescan = viewModel.PrescanAsync();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (service.Order.Count < width && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.Equal(width, service.Order.Count);

        viewModel.WindowAttached();
        gate.SetResult();
        await prescan;

        var order = service.Order.ToList();
        Assert.True(order.Count > width, "the prescan has to reach more targets than it runs at once");
        Assert.All(order.Take(width), id => Assert.True(service.Background[id], id));
        Assert.All(order.Skip(width), id => Assert.False(service.Background[id], id));
    }

    [Fact]
    public async Task The_priority_choice_reaches_the_elevated_worker_in_the_command()
    {
        var logging = new LoggingService();
        var service = new PriorityService(logging);
        var seen = new List<bool>();

        foreach (var background in new[] { true, false })
        {
            var command = new CleanupCommand
            {
                Command = "scan",
                Language = "en",
                TargetIds = ["windows-temp"],
                BackgroundPriority = background,
            };
            using var stream = new MemoryStream();
            var writer = new StreamWriter(stream) { AutoFlush = true };
            await CleanupWorker.RunCommandAsync(
                command, writer, "job-1", logging, CancellationToken.None, new PauseGate(), service);
            seen.Add(service.Background["windows-temp"]);
        }

        Assert.Equal([true, false], seen);
    }

    [Fact]
    public void A_command_from_an_older_sender_scans_at_normal_priority()
    {
        var json = JsonSerializer.Serialize(
            new { command = "scan", language = "en", targetIds = new[] { "windows-temp" } });

        var command = JsonSerializer.Deserialize<CleanupCommand>(
            json, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        Assert.NotNull(command);
        Assert.False(command!.BackgroundPriority);
    }
}
