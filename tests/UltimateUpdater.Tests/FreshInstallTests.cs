using System.Text;
using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

/// <summary>
/// The fresh install that finishes a reinstall: the winget calls, the elevated worker's refusals and the
/// queue's dispatch. The worker is trusted by one UAC prompt, so these pin what it will and will not run.
/// </summary>
public class FreshInstallTests
{
    private static (StreamWriter Writer, MemoryStream Buffer) NewWriter()
    {
        var buffer = new MemoryStream();
        var writer = new StreamWriter(buffer, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        return (writer, buffer);
    }

    private static string ReadAll(MemoryStream buffer)
    {
        buffer.Position = 0;
        return new StreamReader(buffer).ReadToEnd();
    }

    private sealed class RecordingSource(string id, bool freshCapable = true) : IFreshInstallSource
    {
        public List<string> Calls { get; } = [];

        public string Id => id;

        public UpdateCategory Category => UpdateCategory.Software;

        public SourceTier Tier => SourceTier.A;

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public IAsyncEnumerable<UpdateItem> ScanAsync(CancellationToken cancellationToken) => AsyncEnumerable.Empty<UpdateItem>();

        public Task InstallAsync(UpdateItem item, IProgress<Models.InstallProgress> progress, CancellationToken cancellationToken)
        {
            Calls.Add("install");
            return Task.CompletedTask;
        }

        public Task UninstallAsync(UpdateItem item, IProgress<Models.InstallProgress> progress, CancellationToken cancellationToken)
        {
            Calls.Add("uninstall");
            return Task.CompletedTask;
        }

        public Task<bool> CanFetchAsync(UpdateItem item, CancellationToken cancellationToken) => Task.FromResult(freshCapable);

        public Task FreshInstallAsync(UpdateItem item, IProgress<Models.InstallProgress> progress, CancellationToken cancellationToken)
        {
            Calls.Add("fresh");
            return Task.CompletedTask;
        }
    }

    /// <summary>A winget-shaped source WITHOUT the fresh-install capability.</summary>
    private sealed class PlainSource(string id) : IUpdateSource
    {
        public List<string> Calls { get; } = [];

        public string Id => id;

        public UpdateCategory Category => UpdateCategory.Software;

        public SourceTier Tier => SourceTier.A;

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public IAsyncEnumerable<UpdateItem> ScanAsync(CancellationToken cancellationToken) => AsyncEnumerable.Empty<UpdateItem>();

        public Task InstallAsync(UpdateItem item, IProgress<Models.InstallProgress> progress, CancellationToken cancellationToken)
        {
            Calls.Add("install");
            return Task.CompletedTask;
        }
    }

    private static InstallJobFile FreshJob(params InstallJobItem[] items) =>
        new() { Action = InstallAction.FreshInstall, Items = [.. items] };

    private static InstallJobItem Widget(string sourceId = "winget", string id = "Example.Widget") =>
        new() { SourceId = sourceId, ItemId = id, Name = "Example Widget" };

    [Fact]
    public async Task The_worker_runs_a_fresh_install_through_the_fresh_path_and_never_the_upgrade_path()
    {
        var (writer, buffer) = NewWriter();
        var source = new RecordingSource("winget");

        await InstallWorker.RunJobAsync(FreshJob(Widget()), writer, "job1", new LoggingService(), CancellationToken.None, [source]);

        Assert.Equal(["fresh"], source.Calls);
        Assert.Contains("\"success\":true", ReadAll(buffer));
    }

    [Fact]
    public async Task The_worker_refuses_a_fresh_install_of_more_than_one_package()
    {
        var (writer, buffer) = NewWriter();
        var source = new RecordingSource("winget");

        var connected = await InstallWorker.RunJobAsync(
            FreshJob(Widget(), Widget(id: "Example.Other")), writer, "job1", new LoggingService(), CancellationToken.None, [source]);

        Assert.True(connected);
        Assert.Empty(source.Calls);
        Assert.DoesNotContain("\"success\":true", ReadAll(buffer));
    }

    [Theory]
    [InlineData("chocolatey")]
    [InlineData("windowsupdate")]
    [InlineData("pip")]
    public async Task The_worker_refuses_a_fresh_install_for_any_source_but_winget(string sourceId)
    {
        var (writer, buffer) = NewWriter();
        var source = new RecordingSource(sourceId);

        await InstallWorker.RunJobAsync(FreshJob(Widget(sourceId)), writer, "job1", new LoggingService(), CancellationToken.None, [source]);

        Assert.Empty(source.Calls);
        Assert.DoesNotContain("\"success\":true", ReadAll(buffer));
    }

    [Fact]
    public async Task A_winget_source_without_the_capability_fails_closed_instead_of_installing_normally()
    {
        var (writer, buffer) = NewWriter();
        var source = new PlainSource("winget");

        await InstallWorker.RunJobAsync(FreshJob(Widget()), writer, "job1", new LoggingService(), CancellationToken.None, [source]);

        Assert.Empty(source.Calls);
        var output = ReadAll(buffer);
        Assert.Contains("\"success\":false", output);
        Assert.DoesNotContain("\"success\":true", output);
    }

    [Fact]
    public async Task The_queue_sends_a_fresh_install_to_the_fresh_path_only()
    {
        var logging = new LoggingService();
        var settings = new SettingsService(logging, Path.Combine(Directory.CreateTempSubdirectory("wu-fresh-test-").FullName, "settings.json"));
        var source = new RecordingSource("winget");
        var queue = new InstallQueue([source], settings, new HistoryService(logging), logging, isMockSession: true);
        var statuses = new List<InstallItemStatus>();
        queue.StatusChanged += statuses.Add;

        await queue.RunAsync(
            [new UpdateItem { Id = "Example.Widget", SourceId = "winget", Category = UpdateCategory.Software, Name = "Example Widget", InstalledVersion = "1.0", AvailableVersion = "2.0" }],
            CancellationToken.None,
            InstallAction.FreshInstall);

        Assert.Equal(["fresh"], source.Calls);
        Assert.Contains(statuses, s => s.Success == true);
    }

    [Theory]
    [InlineData("Example.Widget")]
    [InlineData("Vendor.App-2")]
    public void The_fresh_install_arguments_name_exactly_this_package_and_nothing_extra(string id)
    {
        var args = WingetSource.FreshInstallArguments(id);

        Assert.Equal(["install", "--id", id, "--exact"], args.Take(4));
        Assert.DoesNotContain("--scope", args);
        Assert.DoesNotContain("--force", args);
        Assert.DoesNotContain("--override", args);

        var show = WingetSource.ShowArguments(id);
        Assert.Equal(["show", "--id", id, "--exact"], show.Take(4));
    }

    [Theory]
    [InlineData("--source evil")]
    [InlineData("-h")]
    [InlineData("Example.Widget & calc")]
    [InlineData("Example.Widget\" --override \"x")]
    [InlineData("")]
    public async Task A_hostile_id_is_refused_before_any_process_starts(string id)
    {
        var source = new WingetSource(new LoggingService());
        var item = new UpdateItem { Id = id, SourceId = "winget", Category = UpdateCategory.Software, Name = "x" };

        Assert.False(await source.CanFetchAsync(item, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.FreshInstallAsync(item, new Progress<Models.InstallProgress>(), CancellationToken.None));
    }

    [Fact]
    public void Only_the_different_technology_explanation_offers_a_reinstall()
    {
        Assert.True(InstallErrorExplainer.Explain("winget", "install failed (exit 0x8A15008E)")?.OffersReinstall);
        Assert.False(InstallErrorExplainer.Explain("winget", "install failed (exit 0x8A15002F)")?.OffersReinstall ?? false);
        Assert.False(InstallErrorExplainer.Explain("winget", "install failed (exit 0x8A150011)")?.OffersReinstall ?? false);
        Assert.False(InstallErrorExplainer.Explain("winget", "install failed (exit 1618)")?.OffersReinstall ?? false);
    }
}
