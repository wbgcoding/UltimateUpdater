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
          {"name":"UltimateUpdater.exe","browser_download_url":"https://github.com/x/y/releases/download/v999.0.0/a.exe","url":"https://api.github.com/repos/x/y/releases/assets/1","size":10},
          {"name":"SHA256SUMS.txt","browser_download_url":"https://github.com/x/y/releases/download/v999.0.0/SHA256SUMS.txt","url":"https://api.github.com/repos/x/y/releases/assets/3","size":10}
        ]}
        """;

    private const string OlderRelease = """
        {"tag_name":"v0.0.1","draft":false,"body":"","assets":[]}
        """;

    private static SelfUpdateService Service(ScriptedChannel channel) =>
        new(new HttpClient(), new LoggingService(), channel, publicKeyPem: TestKeyPem);

    private static readonly string TestKeyPem =
        System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256).ExportSubjectPublicKeyInfoPem();

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
        new ReleaseAsset("UltimateUpdater.exe", "https://github.com/x/y/a.exe", "", size),
        new ReleaseAsset("SHA256SUMS.txt", "https://github.com/x/y/SHA256SUMS.txt", "", 10),
        string.Empty,
        new ReleaseAsset("UltimateUpdater.exe.sig", "https://github.com/x/y/a.exe.sig", "", 90));

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
            Get = (_, _) => Task.FromResult<string?>($"{new string('a', 64)}  UltimateUpdater.exe"),
            Download = (_, _) => Task.FromResult(false),
        };

        var result = await Service(channel).DownloadVerifiedAsync(Update(), CancellationToken.None);

        Assert.Equal(SelfUpdateDownloadOutcome.NetworkFailed, result.Outcome);
    }

    [Fact]
    public async Task The_download_is_staged_in_its_own_folder_that_a_failed_download_removes()
    {
        string? staged = null;
        var channel = new ScriptedChannel
        {
            Get = (_, _) => Task.FromResult<string?>($"{new string('a', 64)}  UltimateUpdater.exe"),
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
            Get = (_, _) => Task.FromResult<string?>($"{new string('a', 64)}  UltimateUpdater.exe"),
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
            Get = (_, _) => Task.FromResult<string?>($"{new string('a', 64)}  UltimateUpdater.exe"),
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
            Get = (_, _) => Task.FromResult<string?>($"{new string('a', 64)}  UltimateUpdater.exe"),
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

    [Fact]
    public async Task Progress_runs_from_download_to_verification_and_never_steps_back()
    {
        var content = new byte[] { 1, 2, 3, 4 };
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var signature = Convert.ToBase64String(key.SignData(
            content,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        var channel = new ScriptedChannel
        {
            Get = (url, _) => Task.FromResult<string?>(url.EndsWith(".sig", StringComparison.Ordinal) ? signature : ChecksumLine(content)),
            Readings = [(1, 4), (2, 4), (4, 4)],
            Download = (path, _) =>
            {
                File.WriteAllBytes(path, content);
                return Task.FromResult(true);
            },
        };
        var seen = new List<SelfUpdateProgress>();

        var service = new SelfUpdateService(new HttpClient(), new LoggingService(), channel, publicKeyPem: key.ExportSubjectPublicKeyInfoPem());
        var update = Update() with { SignatureAsset = new ReleaseAsset("UltimateUpdater.exe.sig", "https://github.com/x/y/a.exe.sig", "", 90) };
        var result = await service.DownloadVerifiedAsync(update, CancellationToken.None, new Collector(seen));
        if (result.Path is not null)
            service.DiscardVerified(result.Path);

        Assert.Equal(SelfUpdateDownloadOutcome.Downloaded, result.Outcome);
        var downloading = seen.Where(p => p.Phase == SelfUpdatePhase.Downloading && p.Read > 0).ToList();
        Assert.Equal([1L, 2L, 4L], downloading.Select(p => p.Read).ToArray());
        Assert.Equal(downloading.Select(p => p.Read).Order().ToArray(), downloading.Select(p => p.Read).ToArray());
        Assert.Equal(downloading[^1].Total, downloading[^1].Read);
        Assert.Equal(SelfUpdatePhase.Verifying, seen[^1].Phase);
    }

    private sealed class Collector(List<SelfUpdateProgress> target) : IProgress<SelfUpdateProgress>
    {
        public void Report(SelfUpdateProgress value) => target.Add(value);
    }

    /// <summary>The checksum line for <paramref name="content"/> under the portable asset name.</summary>
    internal static string ChecksumLine(byte[] content) =>
        $"{Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant()}  UltimateUpdater.exe";

    internal sealed class ScriptedChannel : IUpdateChannel
    {
        public Func<string, CancellationToken, Task<string?>> Get { get; init; } =
            (_, _) => Task.FromResult<string?>(null);

        public Func<string, CancellationToken, Task<bool>> Download { get; init; } = (_, _) => Task.FromResult(false);

        /// <summary>Byte readings the fake download reports before it answers.</summary>
        public (long Read, long? Total)[] Readings { get; init; } = [];

        public bool IsAvailable => true;

        public Task<string?> GetStringAsync(HttpClient client, string url, CancellationToken cancellationToken) =>
            Get(url, cancellationToken);

        public Task<bool> DownloadAsync(
            HttpClient client, string url, string targetPath, long maxBytes, CancellationToken cancellationToken,
            IProgress<(long Read, long? Total)>? progress = null)
        {
            foreach (var reading in Readings)
                progress?.Report(reading);

            return Download(targetPath, cancellationToken);
        }
    }
}
