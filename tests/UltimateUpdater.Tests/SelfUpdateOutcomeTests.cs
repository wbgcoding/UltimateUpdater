using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using UltimateUpdater.Services;
using UltimateUpdater.Services.SelfUpdate;

namespace UltimateUpdater.Tests;

/// <summary>
/// The self-update service says WHY a check or a download produced nothing, so the window can tell a
/// dead connection from a refused file and "up to date" from "could not look".
/// </summary>
public class SelfUpdateOutcomeTests
{
    private const string NewerRelease = """
        {"tag_name":"v999.0.0","draft":false,"body":"","assets":[
          {"name":"Ultimate Updater.exe","browser_download_url":"https://github.com/x/y/releases/download/v999.0.0/a.exe","url":"https://api.github.com/repos/x/y/releases/assets/1","size":10},
          {"name":"SHA256SUMS.txt","browser_download_url":"https://github.com/x/y/releases/download/v999.0.0/SHA256SUMS.txt","url":"https://api.github.com/repos/x/y/releases/assets/3","size":10}
        ]}
        """;

    private const string OlderRelease = """
        {"tag_name":"v0.0.1","draft":false,"body":"","assets":[]}
        """;

    private static SelfUpdateService Service(ScriptedChannel channel) =>
        new(new HttpClient(), new LoggingService(), channel);

    [Fact]
    public async Task A_check_that_gets_no_answer_is_failed_not_up_to_date()
    {
        var result = await Service(new ScriptedChannel { Get = (_, _) => Task.FromResult<string?>(null) })
            .CheckAsync(CancellationToken.None);

        Assert.Equal(SelfUpdateCheckOutcome.Failed, result.Outcome);
        Assert.Null(result.Update);
    }

    [Fact]
    public async Task A_check_that_throws_a_network_error_is_failed()
    {
        var result = await Service(new ScriptedChannel { Get = (_, _) => throw new HttpRequestException("down") })
            .CheckAsync(CancellationToken.None);

        Assert.Equal(SelfUpdateCheckOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task An_unreadable_feed_is_failed()
    {
        var result = await Service(new ScriptedChannel { Get = (_, _) => Task.FromResult<string?>("<html>not json") })
            .CheckAsync(CancellationToken.None);

        Assert.Equal(SelfUpdateCheckOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task A_readable_feed_without_a_newer_release_is_none()
    {
        var result = await Service(new ScriptedChannel { Get = (_, _) => Task.FromResult<string?>(OlderRelease) })
            .CheckAsync(CancellationToken.None);

        Assert.Equal(SelfUpdateCheckOutcome.None, result.Outcome);
        Assert.Null(result.Update);
    }

    [Fact]
    public async Task A_newer_release_is_found_with_its_update()
    {
        var result = await Service(new ScriptedChannel { Get = (_, _) => Task.FromResult<string?>(NewerRelease) })
            .CheckAsync(CancellationToken.None);

        Assert.Equal(SelfUpdateCheckOutcome.Found, result.Outcome);
        Assert.Equal("999.0.0", result.Update!.Version);
    }

    private static AvailableUpdate Update(long size = 10) => new(
        "999.0.0",
        new ReleaseAsset("Ultimate Updater.exe", "https://github.com/x/y/a.exe", "", size),
        new ReleaseAsset("SHA256SUMS.txt", "https://github.com/x/y/SHA256SUMS.txt", "", 10),
        string.Empty);

    [Fact]
    public async Task A_missing_checksum_file_is_a_network_failure()
    {
        var result = await Service(new ScriptedChannel { Get = (_, _) => Task.FromResult<string?>(null) })
            .DownloadVerifiedAsync(Update(), CancellationToken.None);

        Assert.Equal(SelfUpdateDownloadOutcome.NetworkFailed, result.Outcome);
        Assert.Null(result.Path);
    }

    [Fact]
    public async Task A_refused_download_is_a_network_failure()
    {
        var channel = new ScriptedChannel
        {
            Get = (_, _) => Task.FromResult<string?>($"{new string('a', 64)}  Ultimate Updater.exe"),
            Download = (_, _) => Task.FromResult(false),
        };

        var result = await Service(channel).DownloadVerifiedAsync(Update(), CancellationToken.None);

        Assert.Equal(SelfUpdateDownloadOutcome.NetworkFailed, result.Outcome);
    }

    /// <summary>GitHub stores an uploaded "Ultimate Updater.exe" as "Ultimate.Updater.exe".</summary>
    [Fact]
    public async Task A_release_whose_portable_asset_github_renamed_with_dots_is_found()
    {
        var release = NewerRelease.Replace("\"name\":\"Ultimate Updater.exe\"", "\"name\":\"Ultimate.Updater.exe\"");

        var result = await Service(new ScriptedChannel { Get = (_, _) => Task.FromResult<string?>(release) })
            .CheckAsync(CancellationToken.None);

        Assert.Equal(SelfUpdateCheckOutcome.Found, result.Outcome);
        Assert.Equal("Ultimate.Updater.exe", result.Update!.Asset.Name);
    }

    [Fact]
    public async Task A_dotted_asset_name_finds_its_checksum_under_the_spaced_name()
    {
        var update = Update() with { Asset = new ReleaseAsset("Ultimate.Updater.exe", "https://github.com/x/y/a.exe", "", 10) };
        var channel = new ScriptedChannel
        {
            Get = (_, _) => Task.FromResult<string?>($"{new string('a', 64)}  Ultimate Updater.exe"),
            Download = (_, _) => Task.FromResult(false),
        };

        var result = await Service(channel).DownloadVerifiedAsync(update, CancellationToken.None);

        // NetworkFailed comes from the refused download, so the checksum lookup before it succeeded;
        // a missing checksum would have stopped at VerificationFailed.
        Assert.Equal(SelfUpdateDownloadOutcome.NetworkFailed, result.Outcome);
    }

    [Fact]
    public void Checksum_names_match_across_spaces_and_dots_but_nothing_else()
    {
        var sums = UpdateManifest.ParseChecksums($"{new string('b', 64)}  Ultimate Updater.exe");

        Assert.True(sums.ContainsKey("Ultimate.Updater.exe"));
        Assert.True(sums.ContainsKey("ultimate updater.EXE"));
        Assert.False(sums.ContainsKey("UltimateUpdater.exe"));
        Assert.False(sums.ContainsKey("Ultimate_Updater.exe"));
    }

    [Fact]
    public async Task The_download_is_staged_in_its_own_folder_that_a_failed_download_removes()
    {
        string? staged = null;
        var channel = new ScriptedChannel
        {
            Get = (_, _) => Task.FromResult<string?>($"{new string('a', 64)}  Ultimate Updater.exe"),
            Download = (target, _) =>
            {
                staged = target;
                Assert.True(Directory.Exists(Path.GetDirectoryName(target)));
                File.WriteAllBytes(target, [1]);
                return Task.FromResult(false);
            },
        };

        await Service(channel).DownloadVerifiedAsync(Update(), CancellationToken.None);

        Assert.NotNull(staged);
        var folder = Path.GetDirectoryName(staged)!;
        Assert.StartsWith(SelfUpdateService.StagingFolderPrefix, Path.GetFileName(folder));
        Assert.Equal(
            Path.TrimEndingDirectorySeparator(Path.GetTempPath()), Path.GetDirectoryName(folder));
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task A_network_exception_during_the_download_is_a_network_failure()
    {
        var channel = new ScriptedChannel
        {
            Get = (_, _) => Task.FromResult<string?>($"{new string('a', 64)}  Ultimate Updater.exe"),
            Download = (_, _) => throw new HttpRequestException("reset"),
        };

        var result = await Service(channel).DownloadVerifiedAsync(Update(), CancellationToken.None);

        Assert.Equal(SelfUpdateDownloadOutcome.NetworkFailed, result.Outcome);
    }

    [Fact]
    public async Task A_cancelled_download_stays_a_cancellation()
    {
        using var cts = new CancellationTokenSource();
        var channel = new ScriptedChannel
        {
            Get = (_, token) =>
            {
                cts.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.FromResult<string?>(null);
            },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Service(channel).DownloadVerifiedAsync(Update(), cts.Token));
    }

    [Fact]
    public async Task A_checksum_file_that_does_not_list_the_asset_is_a_verification_failure()
    {
        var channel = new ScriptedChannel
        {
            Get = (_, _) => Task.FromResult<string?>($"{new string('a', 64)}  some other file.exe"),
        };

        var result = await Service(channel).DownloadVerifiedAsync(Update(), CancellationToken.None);

        Assert.Equal(SelfUpdateDownloadOutcome.VerificationFailed, result.Outcome);
    }

    [Fact]
    public async Task An_oversized_asset_is_a_verification_failure()
    {
        var channel = new ScriptedChannel
        {
            Get = (_, _) => Task.FromResult<string?>($"{new string('a', 64)}  Ultimate Updater.exe"),
        };

        var result = await Service(channel)
            .DownloadVerifiedAsync(Update(size: 301L * 1024 * 1024), CancellationToken.None);

        Assert.Equal(SelfUpdateDownloadOutcome.VerificationFailed, result.Outcome);
    }

    [Fact]
    public async Task A_digest_mismatch_is_a_verification_failure_and_leaves_nothing_behind()
    {
        string? written = null;
        var channel = new ScriptedChannel
        {
            Get = (_, _) => Task.FromResult<string?>($"{new string('a', 64)}  Ultimate Updater.exe"),
            Download = (path, _) =>
            {
                written = path;
                File.WriteAllBytes(path, [1, 2, 3]);
                return Task.FromResult(true);
            },
        };

        var result = await Service(channel).DownloadVerifiedAsync(Update(), CancellationToken.None);

        Assert.Equal(SelfUpdateDownloadOutcome.VerificationFailed, result.Outcome);
        Assert.NotNull(written);
        Assert.False(File.Exists(written));
    }

    /// <summary>The checksum line for <paramref name="content"/> under the portable asset name.</summary>
    internal static string ChecksumLine(byte[] content) =>
        $"{Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant()}  Ultimate Updater.exe";

    internal sealed class ScriptedChannel : IUpdateChannel
    {
        public Func<string, CancellationToken, Task<string?>> Get { get; init; } =
            (_, _) => Task.FromResult<string?>(null);

        public Func<string, CancellationToken, Task<bool>> Download { get; init; } = (_, _) => Task.FromResult(false);

        public bool IsAvailable => true;

        public Task<string?> GetStringAsync(HttpClient client, string url, CancellationToken cancellationToken) =>
            Get(url, cancellationToken);

        public Task<bool> DownloadAsync(
            HttpClient client, string url, string targetPath, long maxBytes, CancellationToken cancellationToken) =>
            Download(targetPath, cancellationToken);
    }
}
