using System.Text;
using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

/// <summary>
/// The elevated worker rebuilds every item from the job alone, without a scan of its own: the winget source a row
/// was found in has to travel with the job, or an admin install asks every source again and one broken source
/// fails it.
/// </summary>
public class WingetSourceJobTests
{
    private sealed class CapturingSource(string id) : IUpdateSource
    {
        public List<UpdateItem> Installed { get; } = [];

        public string Id => id;

        public UpdateCategory Category => UpdateCategory.Software;

        public SourceTier Tier => SourceTier.A;

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public IAsyncEnumerable<UpdateItem> ScanAsync(CancellationToken cancellationToken) => AsyncEnumerable.Empty<UpdateItem>();

        public Task InstallAsync(UpdateItem item, IProgress<Models.InstallProgress> progress, CancellationToken cancellationToken)
        {
            Installed.Add(item);
            return Task.CompletedTask;
        }
    }

    private static StreamWriter NewWriter() =>
        new(new MemoryStream(), new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

    private static UpdateItem Row(string? source) => new()
    {
        Id = "Git.Git",
        SourceId = "winget",
        Category = UpdateCategory.Software,
        Name = "Git",
        Meta = source is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { [WingetSource.SourceMetaKey] = source },
    };

    [Fact]
    public void The_job_carries_the_source_the_scan_found()
    {
        var job = InstallQueue.ToJobItem(Row("winget"), InstallAction.Install);

        Assert.Equal("winget", job.PackageSource);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("-evil")]
    [InlineData("a b")]
    public void A_row_without_a_plain_source_name_sends_none(string? source)
    {
        Assert.Null(InstallQueue.ToJobItem(Row(source), InstallAction.Install).PackageSource);
    }

    [Fact]
    public async Task The_worker_hands_the_source_on_to_the_install()
    {
        var source = new CapturingSource("winget");
        var job = new InstallJobFile
        {
            Items = [new InstallJobItem { SourceId = "winget", ItemId = "Git.Git", Name = "Git", PackageSource = "winget" }],
        };

        await InstallWorker.RunJobAsync(job, NewWriter(), "job1", new LoggingService(), CancellationToken.None, [source]);

        var item = Assert.Single(source.Installed);
        Assert.Equal("winget", WingetSource.SafeSource(item));
    }

    [Fact]
    public async Task The_worker_drops_a_source_name_that_is_not_plain()
    {
        var source = new CapturingSource("winget");
        var job = new InstallJobFile
        {
            Items = [new InstallJobItem { SourceId = "winget", ItemId = "Git.Git", Name = "Git", PackageSource = "--force" }],
        };

        await InstallWorker.RunJobAsync(job, NewWriter(), "job1", new LoggingService(), CancellationToken.None, [source]);

        var item = Assert.Single(source.Installed);
        Assert.Null(WingetSource.SafeSource(item));
        Assert.False(item.Meta.ContainsKey(WingetSource.SourceMetaKey));
    }

    [Fact]
    public void A_row_of_another_source_sends_no_winget_source()
    {
        var pip = new UpdateItem
        {
            Id = "six",
            SourceId = "pip",
            Category = UpdateCategory.DeveloperTools,
            Name = "six",
            Meta = new Dictionary<string, string> { [WingetSource.SourceMetaKey] = "winget" },
        };

        Assert.Null(InstallQueue.ToJobItem(pip, InstallAction.Install).PackageSource);
    }

    [Fact]
    public void A_name_with_a_trailing_line_break_is_not_plain()
    {
        Assert.False(WingetSource.IsSafeSourceName("winget\n"));
        Assert.True(WingetSource.IsSafeSourceName("winget"));
    }
}
