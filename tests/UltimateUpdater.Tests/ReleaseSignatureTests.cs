using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using UltimateUpdater.Services;
using UltimateUpdater.Services.SelfUpdate;

namespace UltimateUpdater.Tests;

public class ReleaseSignatureTests
{
    private static readonly byte[] Content = [10, 20, 30, 40, 50, 60];

    private static (string PublicPem, ECDsa Key) NewKey()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key.ExportSubjectPublicKeyInfoPem(), key);
    }

    private static string Sign(ECDsa key, byte[] data) =>
        Convert.ToBase64String(key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    private static bool Verify(byte[] data, string? signature, string? publicPem)
    {
        using var stream = new MemoryStream(data);
        return ReleaseSignature.Verify(stream, signature, publicPem);
    }

    [Fact]
    public void A_valid_signature_is_accepted()
    {
        var (pem, key) = NewKey();
        using (key)
            Assert.True(Verify(Content, Sign(key, Content), pem));
    }

    [Fact]
    public void A_flipped_byte_is_refused()
    {
        var (pem, key) = NewKey();
        using (key)
        {
            var signature = Sign(key, Content);
            var changed = (byte[])Content.Clone();
            changed[3] ^= 1;

            Assert.False(Verify(changed, signature, pem));
        }
    }

    [Fact]
    public void A_signature_from_another_key_is_refused()
    {
        var (pem, key) = NewKey();
        var (_, other) = NewKey();
        using (key)
        using (other)
            Assert.False(Verify(Content, Sign(other, Content), pem));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not base64 at all !!")]
    [InlineData("AAAA")]
    public void A_missing_or_malformed_signature_is_refused(string? signature)
    {
        var (pem, key) = NewKey();
        using (key)
            Assert.False(Verify(Content, signature, pem));
    }

    [Fact]
    public void Only_the_shipped_release_key_verifies_release_signatures()
    {
        var (_, key) = NewKey();
        using (key)
        {
            var signature = Sign(key, Content);

            Assert.False(ReleaseSignature.IsUsableKey(""));
            Assert.False(Verify(Content, signature, ""));
            Assert.True(ReleaseSignature.IsUsableKey(ReleaseSignature.ReleasePublicKeyPem));
            // a test key's signature never passes the shipped release key
            Assert.False(Verify(Content, signature, ReleaseSignature.ReleasePublicKeyPem));
            Assert.False(Verify(Content, signature, "-----BEGIN PUBLIC KEY-----\nnonsense\n-----END PUBLIC KEY-----"));
        }
    }

    [Fact]
    public void A_key_on_another_curve_is_not_usable()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP384);

        Assert.False(ReleaseSignature.IsUsableKey(key.ExportSubjectPublicKeyInfoPem()));
    }

    private const string ExeName = "UltimateUpdater.exe";

    private static SelfUpdateService Service(
        Func<string, Task<string?>> get, string? publicPem, byte[]? downloaded, Func<bool>? isInstalled = null) =>
        new(
            new HttpClient(),
            new LoggingService(),
            new SelfUpdateOutcomeTests.ScriptedChannel
            {
                Get = (url, _) => get(url),
                Download = (path, _) =>
                {
                    File.WriteAllBytes(path, downloaded ?? Content);
                    return Task.FromResult(true);
                },
            },
            publicKeyPem: publicPem,
            isInstalled: isInstalled);

    private static AvailableUpdate Update(
        bool withSignature = true, bool withSumsSignature = true, string version = "999.0.0", string name = ExeName) => new(
        version,
        new ReleaseAsset(name, "https://github.com/x/y/a.exe", "", 6),
        new ReleaseAsset("SHA256SUMS.txt", "https://github.com/x/y/SHA256SUMS.txt", "", 10),
        string.Empty,
        withSignature ? new ReleaseAsset(name + ".sig", "https://github.com/x/y/a.exe.sig", "", 90) : null,
        withSumsSignature ? new ReleaseAsset("SHA256SUMS.txt.sig", "https://github.com/x/y/SHA256SUMS.txt.sig", "", 90) : null);

    /// <summary>A release as the build publishes it, signed by <paramref name="sumsKey"/>; the named parts can be
    /// swapped for something else to play an attacker with write access to the release.</summary>
    private static Func<string, Task<string?>> Feed(
        byte[] listed, string? signature, ECDsa? sumsKey, string version = "999.0.0", string name = ExeName,
        string? sumsText = null, string? sumsSignature = null)
    {
        var sums = sumsText ?? SelfUpdateOutcomeTests.ChecksumLine(listed, version, name);
        var sumsSig = sumsSignature ?? (sumsKey is null ? null : SelfUpdateOutcomeTests.SignText(sumsKey, sums));
        return url => Task.FromResult(url.EndsWith("SHA256SUMS.txt.sig", StringComparison.Ordinal)
            ? sumsSig
            : url.EndsWith(".sig", StringComparison.Ordinal) ? signature : sums);
    }

    [Fact]
    public async Task A_correctly_signed_update_is_accepted()
    {
        var (pem, key) = NewKey();
        using (key)
        {
            var service = Service(Feed(Content, Sign(key, Content), key), pem, Content);
            var result = await service.DownloadVerifiedAsync(Update(), CancellationToken.None);

            Assert.Equal(SelfUpdateDownloadOutcome.Downloaded, result.Outcome);
            Assert.NotNull(result.Path);
            service.DiscardVerified(result.Path!);
        }
    }

    [Fact]
    public async Task A_download_with_a_flipped_byte_is_refused_and_removed()
    {
        var (pem, key) = NewKey();
        using (key)
        {
            // the checksum file was rewritten to match the served bytes; only the signature still describes the real content
            var served = (byte[])Content.Clone();
            served[0] ^= 1;
            var feed = Feed(served, Sign(key, Content), key);

            var result = await Service(feed, pem, served).DownloadVerifiedAsync(Update(), CancellationToken.None);

            Assert.Equal(SelfUpdateDownloadOutcome.SignatureInvalid, result.Outcome);
            Assert.Null(result.Path);
        }
    }

    [Fact]
    public async Task A_missing_signature_file_is_refused_before_anything_is_downloaded()
    {
        var (pem, key) = NewKey();
        using (key)
            await AssertRefusedBeforeDownloadAsync(
                Feed(Content, null, key), pem, Update(withSignature: false));
    }

    [Fact]
    public async Task A_signature_from_the_wrong_key_is_refused()
    {
        var (pem, key) = NewKey();
        var (_, other) = NewKey();
        using (key)
        using (other)
        {
            var result = await Service(Feed(Content, Sign(other, Content), key), pem, Content)
                .DownloadVerifiedAsync(Update(), CancellationToken.None);

            Assert.Equal(SelfUpdateDownloadOutcome.SignatureInvalid, result.Outcome);
        }
    }

    [Fact]
    public async Task With_no_release_key_built_in_every_update_is_refused()
    {
        var (_, key) = NewKey();
        using (key)
        {
            var result = await Service(Feed(Content, Sign(key, Content), key), publicPem: null, Content)
                .DownloadVerifiedAsync(Update(), CancellationToken.None);

            Assert.Equal(SelfUpdateDownloadOutcome.SignatureInvalid, result.Outcome);
        }
    }

    [Fact]
    public async Task A_signature_file_that_cannot_be_fetched_is_a_network_failure()
    {
        var (pem, key) = NewKey();
        using (key)
        {
            var result = await Service(Feed(Content, null, key), pem, Content)
                .DownloadVerifiedAsync(Update(), CancellationToken.None);

            Assert.Equal(SelfUpdateDownloadOutcome.NetworkFailed, result.Outcome);
        }
    }

    /// <summary>Runs one download that must be refused as a signature problem without fetching the artifact.</summary>
    private static async Task AssertRefusedBeforeDownloadAsync(
        Func<string, Task<string?>> feed, string pem, AvailableUpdate update, Func<bool>? isInstalled = null)
    {
        var downloads = 0;
        var service = new SelfUpdateService(
            new HttpClient(),
            new LoggingService(),
            new SelfUpdateOutcomeTests.ScriptedChannel
            {
                Get = (url, _) => feed(url),
                Download = (_, _) =>
                {
                    downloads++;
                    return Task.FromResult(true);
                },
            },
            publicKeyPem: pem,
            isInstalled: isInstalled);

        var result = await service.DownloadVerifiedAsync(update, CancellationToken.None);

        Assert.Equal(SelfUpdateDownloadOutcome.SignatureInvalid, result.Outcome);
        Assert.Equal(0, downloads);
    }

    [Fact]
    public async Task A_release_without_a_checksum_signature_is_refused()
    {
        var (pem, key) = NewKey();
        using (key)
            await AssertRefusedBeforeDownloadAsync(
                Feed(Content, Sign(key, Content), key), pem, Update(withSumsSignature: false));
    }

    [Fact]
    public async Task A_checksum_file_that_was_edited_after_signing_is_refused()
    {
        var (pem, key) = NewKey();
        using (key)
        {
            var signedSums = SelfUpdateOutcomeTests.ChecksumLine(Content);
            var edited = signedSums + signedSums.Split("\r\n")[1] + "\r\n";
            var feed = Feed(
                Content, Sign(key, Content), key, sumsText: edited,
                sumsSignature: SelfUpdateOutcomeTests.SignText(key, signedSums));

            await AssertRefusedBeforeDownloadAsync(feed, pem, Update());
        }
    }

    [Fact]
    public async Task A_checksum_file_signed_by_another_key_is_refused()
    {
        var (pem, key) = NewKey();
        var (_, other) = NewKey();
        using (key)
        using (other)
            await AssertRefusedBeforeDownloadAsync(Feed(Content, Sign(key, Content), other), pem, Update());
    }

    [Fact]
    public async Task A_checksum_file_without_a_version_line_is_refused()
    {
        var (pem, key) = NewKey();
        using (key)
        {
            var bare = SelfUpdateOutcomeTests.ChecksumLine(Content).Split("\r\n")[1] + "\r\n";
            var feed = Feed(Content, Sign(key, Content), key, sumsText: bare);

            await AssertRefusedBeforeDownloadAsync(feed, pem, Update());
        }
    }

    [Fact]
    public async Task An_older_signed_release_relabelled_as_newer_is_refused()
    {
        var (pem, key) = NewKey();
        using (key)
        {
            // a genuine, signed 998.0.0 checksum file and artifact, offered under the 999.0.0 tag
            var feed = Feed(Content, Sign(key, Content), key, version: "998.0.0");

            await AssertRefusedBeforeDownloadAsync(feed, pem, Update(version: "999.0.0"));
        }
    }

    [Fact]
    public async Task A_signed_version_that_is_not_newer_than_the_running_one_is_refused()
    {
        var (pem, key) = NewKey();
        using (key)
        {
            var feed = Feed(Content, Sign(key, Content), key, version: "0.0.1");

            await AssertRefusedBeforeDownloadAsync(feed, pem, Update(version: "0.0.1"));
        }
    }

    [Fact]
    public async Task The_portable_exe_renamed_to_a_setup_name_is_refused_on_a_portable_install()
    {
        var (pem, key) = NewKey();
        using (key)
        {
            const string name = "Setup-UltimateUpdater-999.0.0.exe";
            var feed = Feed(Content, Sign(key, Content), key, name: name);

            await AssertRefusedBeforeDownloadAsync(feed, pem, Update(name: name), isInstalled: () => false);
        }
    }

    [Fact]
    public async Task The_portable_exe_is_refused_on_an_installed_copy()
    {
        var (pem, key) = NewKey();
        using (key)
            await AssertRefusedBeforeDownloadAsync(
                Feed(Content, Sign(key, Content), key), pem, Update(), isInstalled: () => true);
    }

    [Fact]
    public async Task An_installed_copy_takes_only_the_setup_of_the_signed_version()
    {
        var (pem, key) = NewKey();
        using (key)
        {
            const string exact = "Setup-UltimateUpdater-999.0.0.exe";
            var service = Service(Feed(Content, Sign(key, Content), key, name: exact), pem, Content, () => true);
            var result = await service.DownloadVerifiedAsync(Update(name: exact), CancellationToken.None);

            Assert.Equal(SelfUpdateDownloadOutcome.Downloaded, result.Outcome);
            service.DiscardVerified(result.Path!);

            // another version's setup is refused even though it is signed and carries the Setup- prefix
            const string other = "Setup-UltimateUpdater-998.0.0.exe";
            await AssertRefusedBeforeDownloadAsync(
                Feed(Content, Sign(key, Content), key, name: other), pem, Update(name: other), () => true);
        }
    }

    [Theory]
    [InlineData(true, "999.0.0", "Setup-UltimateUpdater-999.0.0.exe")]
    [InlineData(true, "1.2.3-beta.1", "Setup-UltimateUpdater-1.2.3-beta.1.exe")]
    [InlineData(false, "999.0.0", "UltimateUpdater.exe")]
    public void The_expected_asset_name_follows_the_install_kind(bool installed, string version, string expected) =>
        Assert.Equal(expected, SelfUpdateService.ExpectedAssetName(installed, version));

    [Fact]
    public void The_signing_tool_writes_signatures_the_verifier_accepts()
    {
        // Needs Python with the cryptography package, the same thing the build uses; a machine without it
        // cannot sign a release either, so the check is skipped there.
        if (!PythonWithCryptography())
            return;

        var script = Path.Combine(RepoRoot(), "tools", "sign-release.py");
        var work = Path.Combine(Path.GetTempPath(), "uu-sign-" + Guid.NewGuid().ToString("N"));
        var dist = Path.Combine(work, "dist");
        Directory.CreateDirectory(dist);
        try
        {
            var keyPath = Path.Combine(work, ".signing", "release-key.pem");
            var publicPem = RunPython(script, "--generate", "--key", keyPath).Trim();
            Assert.StartsWith("-----BEGIN PUBLIC KEY-----", publicPem);
            Assert.DoesNotContain("PRIVATE", publicPem);

            File.WriteAllBytes(Path.Combine(dist, ExeName), Content);
            File.WriteAllText(Path.Combine(dist, "SHA256SUMS.txt"), "sums");
            File.WriteAllText(Path.Combine(dist, "notes.txt"), "not signed");
            RunPython(script, dist, "--key", keyPath);

            foreach (var name in new[] { ExeName, "SHA256SUMS.txt" })
            {
                using var data = File.OpenRead(Path.Combine(dist, name));
                Assert.True(ReleaseSignature.Verify(data, File.ReadAllText(Path.Combine(dist, name + ".sig")), publicPem), name);
            }

            Assert.False(File.Exists(Path.Combine(dist, "notes.txt.sig")));

            // a second --generate never replaces the existing key
            Assert.ThrowsAny<Exception>(() => RunPython(script, "--generate", "--key", keyPath));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    private static bool PythonWithCryptography()
    {
        try
        {
            RunPython("-c", "import cryptography");
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string RunPython(params string[] arguments)
    {
        var info = new ProcessStartInfo("py")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add("-3");
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"python exited {process.ExitCode}: {error}");

        return output;
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "UltimateUpdater.slnx")))
            directory = directory.Parent;

        Assert.True(directory is not null, "the repository root is not above the test binaries any more");
        return directory!.FullName;
    }
}
