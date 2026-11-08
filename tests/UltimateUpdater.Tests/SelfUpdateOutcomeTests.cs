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
        new(new HttpClient(), new LoggingService(), channel, publicKeyPem: TestKey.ExportSubjectPublicKeyInfoPem());

    private static readonly ECDsa TestKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>Every answer a release serves, signed with <see cref="TestKey"/>: the checksum file (with its
    /// version line), the signature of that file and the signature of the artifact.</summary>
    private static Func<string, CancellationToken, Task<string?>> Feed(byte[] listed, string name = "UltimateUpdater.exe") =>
        (url, _) => SignedFeed(TestKey, url, listed, listed, "999.0.0", name);

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
        new ReleaseAsset("UltimateUpdater.exe.sig", "https://github.com/x/y/a.exe.sig", "", 90),
        new ReleaseAsset("SHA256SUMS.txt.sig", "https://github.com/x/y/SHA256SUMS.txt.sig", "", 90));

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
            Get = Feed([1, 2, 3]),
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
            Get = Feed([1, 2, 3]),
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
            Get = Feed([1, 2, 3]),
            Download = (_, _) => throw new HttpRequestException("reset"),
        };

        var result = await Service(channel).DownloadVerifiedAsync(Update(), CancellationToken.None);

        Assert.Equal(SelfUpdateDownloadOutcome.NetworkFailed, result.Outcome);
    }

    [Theory]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(FormatException))]
    [InlineData(typeof(IOException))]
    public async Task A_download_that_throws_leaves_no_staging_folder_and_reads_as_failed(Type exception)
    {
        string? staged = null;
        var channel = new ScriptedChannel
        {
            Get = Feed([1, 2, 3]),
            Download = (target, _) =>
            {
                staged = target;
                File.WriteAllBytes(target, [1]);
                throw (Exception)Activator.CreateInstance(exception)!;
            },
        };

        var result = await Service(channel).DownloadVerifiedAsync(Update(), CancellationToken.None);

        Assert.Equal(SelfUpdateDownloadOutcome.NetworkFailed, result.Outcome);
        Assert.Null(result.Path);
        Assert.NotNull(staged);
        Assert.False(File.Exists(staged));
        Assert.False(Directory.Exists(Path.GetDirectoryName(staged)));
    }

    [Fact]
    public async Task A_second_download_removes_the_first_ones_file_and_folder()
    {
        var content = new byte[] { 1, 2, 3, 4 };
        var channel = new ScriptedChannel
        {
            Get = Feed(content),
            Download = (path, _) =>
            {
                File.WriteAllBytes(path, content);
                return Task.FromResult(true);
            },
        };
        var service = Service(channel);

        var first = await service.DownloadVerifiedAsync(Update(), CancellationToken.None);
        var second = await service.DownloadVerifiedAsync(Update(), CancellationToken.None);

        try
        {
            Assert.Equal(SelfUpdateDownloadOutcome.Downloaded, first.Outcome);
            Assert.Equal(SelfUpdateDownloadOutcome.Downloaded, second.Outcome);
            Assert.False(File.Exists(first.Path));
            Assert.False(Directory.Exists(Path.GetDirectoryName(first.Path)));
            Assert.True(File.Exists(second.Path));
        }
        finally
        {
            service.DiscardVerified(second.Path!);
        }
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
            Get = Feed([1, 2, 3], "some other file.exe"),
        };

        var result = await Service(channel).DownloadVerifiedAsync(Update(), CancellationToken.None);

        Assert.Equal(SelfUpdateDownloadOutcome.VerificationFailed, result.Outcome);
    }

    [Fact]
    public async Task An_oversized_asset_is_a_verification_failure()
    {
        var channel = new ScriptedChannel
        {
            Get = Feed([1, 2, 3]),
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
            Get = Feed([9, 9, 9]),
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
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var channel = new ScriptedChannel
        {
            Get = (url, _) => SignedFeed(key, url, content, content, "999.0.0", "UltimateUpdater.exe"),
            Readings = [(1, 4), (2, 4), (4, 4)],
            Download = (path, _) =>
            {
                File.WriteAllBytes(path, content);
                return Task.FromResult(true);
            },
        };
        var seen = new List<SelfUpdateProgress>();

        var service = new SelfUpdateService(new HttpClient(), new LoggingService(), channel, publicKeyPem: key.ExportSubjectPublicKeyInfoPem());
        var result = await service.DownloadVerifiedAsync(Update(), CancellationToken.None, new Collector(seen));
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

    /// <summary>The checksum file as the build writes it: a version line, then one digest line.</summary>
    internal static string ChecksumLine(byte[] content, string version = "999.0.0", string name = "UltimateUpdater.exe") =>
        $"# version {version}\r\n{Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant()}  {name}\r\n";

    internal static string SignText(ECDsa key, string text) => SignBytes(key, System.Text.Encoding.UTF8.GetBytes(text));

    internal static string SignBytes(ECDsa key, byte[] data) =>
        Convert.ToBase64String(key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    /// <summary>What a fully signed release answers for <paramref name="url"/>: the checksum file lists
    /// <paramref name="listed"/>, the artifact signature covers <paramref name="signedContent"/>.</summary>
    internal static Task<string?> SignedFeed(
        ECDsa key, string url, byte[] listed, byte[] signedContent, string version, string name)
    {
        var sums = ChecksumLine(listed, version, name);
        if (url.EndsWith("SHA256SUMS.txt.sig", StringComparison.Ordinal))
            return Task.FromResult<string?>(SignText(key, sums));

        return Task.FromResult<string?>(url.EndsWith(".sig", StringComparison.Ordinal) ? SignBytes(key, signedContent) : sums);
    }

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
