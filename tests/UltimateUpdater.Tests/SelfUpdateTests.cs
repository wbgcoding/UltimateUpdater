using System.Net;
using System.Net.Http;
using UltimateUpdater.Services;
using UltimateUpdater.Services.SelfUpdate;

namespace UltimateUpdater.Tests;

public class SelfUpdateTests
{
    private const string ReleaseJson = """
        {"tag_name":"v0.2.0","draft":false,"assets":[
          {"name":"UltimateUpdater-0.2.0-win-x64.exe","browser_download_url":"https://github.com/x/y/releases/download/v0.2.0/a.exe","url":"https://api.github.com/repos/x/y/releases/assets/1","size":1024},
          {"name":"SHA256SUMS.txt","browser_download_url":"https://github.com/x/y/releases/download/v0.2.0/SHA256SUMS.txt","url":"https://api.github.com/repos/x/y/releases/assets/2","size":100}
        ]}
        """;

    [Fact]
    public void Release_is_parsed_with_its_assets()
    {
        var release = UpdateManifest.ParseRelease(ReleaseJson);

        Assert.NotNull(release);
        Assert.Equal("0.2.0", release!.Version);
        Assert.Equal(2, release.Assets.Count);
        Assert.Contains(release.Assets, a => a.Name == "SHA256SUMS.txt");
    }

    /// <summary>The banner's "what is new" button shows exactly this text, so a release with no notes at
    /// all must come back as an empty string, never a null that would need special handling downstream.</summary>
    [Fact]
    public void Release_body_is_read_as_the_release_notes()
    {
        const string json = """
            {"tag_name":"v0.3.0","draft":false,"body":"### Fixed\n- a bug","assets":[]}
            """;

        var release = UpdateManifest.ParseRelease(json);

        Assert.Equal("### Fixed\n- a bug", release!.ReleaseNotes);
    }

    [Fact]
    public void A_release_with_no_body_has_empty_release_notes() =>
        Assert.Equal(string.Empty, UpdateManifest.ParseRelease(ReleaseJson)!.ReleaseNotes);

    [Fact]
    public void Release_list_skips_drafts()
    {
        const string json = """
            [{"tag_name":"v9.9.9","draft":true,"assets":[]},
             {"tag_name":"v0.3.0","draft":false,"assets":[]}]
            """;

        Assert.Equal("0.3.0", UpdateManifest.ParseRelease(json)!.Version);
    }

    /// <summary>
    /// A pre-release is the newest TAG but not an update for a stable install: the suffix has to survive
    /// comparison, or a pre-release strips down to a plain version bump and gets taken anyway.
    /// </summary>
    [Fact]
    public void The_public_channel_skips_prereleases_and_takes_the_newest_stable()
    {
        const string json = """
            [{"tag_name":"v0.4.0-beta.1","draft":false,"prerelease":true,"assets":[]},
             {"tag_name":"v0.3.0","draft":false,"prerelease":false,"assets":[]}]
            """;

        Assert.Equal("0.3.0", UpdateManifest.ParseRelease(json)!.Version);

        // A single pre-release object is no update at all on this channel.
        Assert.Null(UpdateManifest.ParseRelease(
            """{"tag_name":"v0.4.0-beta.1","prerelease":true,"assets":[]}"""));
    }

    /// <summary>A channel that allows pre-releases is the one place a pre-release IS the build to take.</summary>
    [Fact]
    public void A_channel_that_allows_prereleases_may_take_one()
    {
        const string json = """
            [{"tag_name":"v0.4.0-beta.1","draft":false,"prerelease":true,"assets":[]},
             {"tag_name":"v0.3.0","draft":false,"prerelease":false,"assets":[]}]
            """;

        var release = UpdateManifest.ParseRelease(json, allowPrereleases: true);

        // and the suffix survives, or the beta cannot be told from the release it precedes
        Assert.Equal("0.4.0-beta.1", release!.Version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"tag_name\":\"\"}")]
    public void Unreadable_release_yields_nothing(string input) =>
        Assert.Null(UpdateManifest.ParseRelease(input));

    /// <summary>A malformed entry anywhere in the releases feed (no tag_name here) must not hide every
    /// release after it: the loop has to keep going, not stop on the first parse failure.</summary>
    [Fact]
    public void A_malformed_entry_does_not_hide_a_valid_release_after_it()
    {
        const string json = """
            [{"draft":false,"assets":[]},
             {"tag_name":"v0.3.0","draft":false,"assets":[]}]
            """;

        Assert.Equal("0.3.0", UpdateManifest.ParseRelease(json)!.Version);
    }

    [Fact]
    public void Entries_that_are_not_objects_are_skipped_without_throwing()
    {
        const string json = """
            [null, "x", 5, {"tag_name":"v0.4.0","draft":false,"assets":[]}]
            """;

        Assert.Equal("0.4.0", UpdateManifest.ParseRelease(json)!.Version);
    }

    [Fact]
    public void An_asset_entry_that_is_not_an_object_is_skipped_and_the_valid_asset_kept()
    {
        const string json = """
            {"tag_name":"v0.4.0","assets":[null,
              {"name":"SHA256SUMS.txt","browser_download_url":"https://github.com/x/y/releases/download/v0.4.0/SHA256SUMS.txt","url":"","size":10}]}
            """;

        var release = UpdateManifest.ParseRelease(json)!;

        Assert.Equal("SHA256SUMS.txt", Assert.Single(release.Assets).Name);
    }

    [Fact]
    public void The_highest_version_wins_whatever_the_feed_order()
    {
        const string json = """
            [{"tag_name":"v0.3.1","assets":[]},
             {"tag_name":"v0.4.1","assets":[]},
             {"tag_name":"v0.4.0","assets":[]}]
            """;

        Assert.Equal("0.4.1", UpdateManifest.ParseRelease(json)!.Version);
    }

    [Fact]
    public void A_draft_or_prerelease_with_the_highest_version_is_still_skipped()
    {
        const string json = """
            [{"tag_name":"v0.9.0","draft":true,"assets":[]},
             {"tag_name":"v0.8.0-beta.1","prerelease":true,"assets":[]},
             {"tag_name":"v0.4.0","assets":[]}]
            """;

        Assert.Equal("0.4.0", UpdateManifest.ParseRelease(json)!.Version);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"error\"")]
    [InlineData("{}")]
    [InlineData("5")]
    [InlineData("{\"tag_name\":5}")]
    [InlineData("{\"message\":\"rate limit exceeded\"}")]
    public void A_readable_root_that_is_not_a_feed_is_a_failed_check(string json)
    {
        Assert.False(UpdateManifest.TryParseRelease(json, false, out var release));
        Assert.Null(release);
    }

    [Fact]
    public void An_empty_list_is_readable_and_yields_no_release()
    {
        Assert.True(UpdateManifest.TryParseRelease("[]", false, out var release));
        Assert.Null(release);
    }

    [Fact]
    public void A_single_release_object_is_readable_and_yields_the_release()
    {
        Assert.True(UpdateManifest.TryParseRelease("""{"tag_name":"v1.2.3","assets":[]}""", false, out var release));
        Assert.Equal("1.2.3", release!.Version);
    }

    [Fact]
    public void A_feed_with_only_unparseable_tags_is_readable_and_yields_no_release()
    {
        const string json = """[{"tag_name":"nightly","assets":[]},{"tag_name":"1.2","assets":[]}]""";

        Assert.True(UpdateManifest.TryParseRelease(json, false, out var release));
        Assert.Null(release);
    }

    [Theory]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("01.2.3")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-beta..1")]
    [InlineData("1.2.3-01")]
    [InlineData("1.2.3+")]
    public void Malformed_versions_are_never_newer(string candidate) =>
        Assert.False(UpdateManifest.IsNewer("0.1.0", candidate));

    [Theory]
    [InlineData("1.2.3-0")]
    [InlineData("1.2.3+001")]
    public void Edge_but_valid_versions_are_still_accepted(string candidate) =>
        Assert.True(UpdateManifest.IsNewer("0.1.0", candidate));

    [Fact]
    public void Checksums_are_parsed()
    {
        const string body = """
            e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855  UltimateUpdater-0.2.0-win-x64.exe
            0000000000000000000000000000000000000000000000000000000000000001 *Setup-UltimateUpdater-0.2.0.exe
            """;

        var checksums = UpdateManifest.ParseChecksums(body);

        Assert.Equal(2, checksums.Count);
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            checksums["UltimateUpdater-0.2.0-win-x64.exe"]);
        // the binary marker is not part of the filename
        Assert.True(checksums.ContainsKey("Setup-UltimateUpdater-0.2.0.exe"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("tooshort  file.exe")]
    [InlineData("zzzz0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855  file.exe")]
    public void Malformed_checksum_lines_are_ignored(string input) =>
        Assert.Empty(UpdateManifest.ParseChecksums(input));

    [Theory]
    [InlineData("0.1.0", "0.2.0", true)]
    [InlineData("0.1.0", "v0.2.0", true)]
    [InlineData("0.2.0", "0.1.0", false)]
    [InlineData("0.2.0", "0.2.0", false)]
    // Pre-release ordering (semver §11): the suffix must be part of the comparison, or every pair below
    // would read as "equal".
    [InlineData("1.2.0-beta.1", "1.2.0", true)]        // beta → the release it precedes IS an update
    [InlineData("1.2.0", "1.2.0-beta.1", false)]       // …and never the other way round
    [InlineData("1.2.0-beta.1", "1.2.0-beta.2", true)]
    [InlineData("1.2.0-beta.2", "1.2.0-beta.1", false)]
    [InlineData("1.2.0-alpha", "1.2.0-beta", true)]
    [InlineData("1.2.0-beta", "1.2.0-alpha", false)]
    [InlineData("1.2.0-beta", "1.2.0-beta.1", true)]   // fewer identifiers rank lower
    [InlineData("1.2.0-1", "1.2.0-alpha", true)]       // numeric identifiers rank below alphanumeric
    [InlineData("1.2.0+build.5", "1.2.0+build.9", false)]  // build metadata is not precedence
    // Both identifiers are larger than Int32.MaxValue (2147483647), so int.TryParse alone would fall
    // through to a plain string comparison, which sorts these the wrong way round.
    [InlineData("1.2.0-99999999999", "1.2.0-100000000000", true)]
    [InlineData("1.2.0-100000000000", "1.2.0-99999999999", false)]
    public void Newer_versions_are_detected(string current, string candidate, bool expected) =>
        Assert.Equal(expected, UpdateManifest.IsNewer(current, candidate));

    [Theory]
    [InlineData("0.1.0", "not-a-version")]
    [InlineData("not-a-version", "0.2.0")]
    [InlineData("0.1.0", "")]
    public void Unparseable_versions_fail_closed(string current, string candidate) =>
        Assert.False(UpdateManifest.IsNewer(current, candidate));

    [Theory]
    [InlineData("https://api.github.com/repos/x/y/releases", true)]
    [InlineData("https://objects.githubusercontent.com/thing", true)]
    [InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/1/2", true)]
    [InlineData("http://api.github.com/repos/x/y/releases", false)] // plain HTTP
    [InlineData("https://evil.example.com/payload.exe", false)] // off-allowlist host
    [InlineData("https://api.github.com.evil.example/x", false)] // lookalike host
    [InlineData("file:///C:/payload.exe", false)]
    [InlineData("not a url", false)]
    public void Only_https_allowlisted_hosts_are_accepted(string url, bool expected) =>
        Assert.Equal(expected, SelfUpdateServiceTestHook.IsAllowedUrl(url));

    /// <summary>
    /// The installer is one file for both architectures, so its name carries none. Matching on the
    /// architecture (as the portable path must) meant an installed copy never found its update.
    /// </summary>
    [Fact]
    public void The_installer_asset_is_found_although_its_name_has_no_architecture()
    {
        var release = UpdateManifest.ParseRelease("""
            {"tag_name":"v0.2.0","draft":false,"assets":[
              {"name":"UltimateUpdater-0.2.0-win-x64.exe","browser_download_url":"https://github.com/a","url":"https://api.github.com/b","size":10},
              {"name":"Setup-UltimateUpdater-0.2.0.exe","browser_download_url":"https://github.com/c","url":"https://api.github.com/d","size":10},
              {"name":"SHA256SUMS.txt","browser_download_url":"https://github.com/e","url":"https://api.github.com/f","size":10}
            ]}
            """);

        Assert.NotNull(release);

        var installer = release!.Assets.SingleOrDefault(a =>
            a.Name.StartsWith("Setup-", StringComparison.OrdinalIgnoreCase) &&
            a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        var portable = release.Assets.SingleOrDefault(a =>
            !a.Name.StartsWith("Setup-", StringComparison.OrdinalIgnoreCase) &&
            a.Name.Contains("x64", StringComparison.OrdinalIgnoreCase) &&
            a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(installer);
        Assert.NotNull(portable);
        Assert.NotEqual(installer!.Name, portable!.Name);
    }

    /// <summary>
    /// The host allowlist must be enforced on the download itself, not only on the metadata call,
    /// otherwise the binary could be fetched from any host over plain HTTP, and the development channel
    /// would send its GitHub token along with it.
    /// </summary>
    [Theory]
    [InlineData("https://evil.example/UltimateUpdater.exe")]
    [InlineData("http://github.com/UltimateUpdater.exe")]
    [InlineData("https://github.com.evil.example/UltimateUpdater.exe")]
    public async Task The_artifact_download_refuses_a_host_that_is_not_allowlisted(string url)
    {
        var target = Path.Combine(Path.GetTempPath(), $"wu-selfupdate-test-{Guid.NewGuid():N}.exe");
        var channel = new PublicUpdateChannel(new LoggingService());

        var accepted = await channel.DownloadAsync(new HttpClient(), url, target, 1024, CancellationToken.None);

        Assert.False(accepted);
        Assert.False(File.Exists(target), "a refused download must not leave a file behind");
    }

    /// <summary>
    /// The pre-request IsAllowedUrl check only covers where a call STARTS. The shared HttpClient follows
    /// redirects automatically, so a host on the allowlist could still hand the request off anywhere,
    /// exactly what RequestMessage.RequestUri (the address AFTER every redirect) has to catch. Simulated
    /// here with a handler that answers with a response whose RequestMessage points off-allowlist, the
    /// same shape a real redirect leaves behind.
    /// </summary>
    [Fact]
    public async Task A_download_that_redirected_off_the_allowlist_is_refused_even_though_it_started_on_it()
    {
        var target = Path.Combine(Path.GetTempPath(), $"wu-selfupdate-test-{Guid.NewGuid():N}.exe");
        var client = new HttpClient(new RedirectingHandler("https://evil.example.com/payload.exe"));
        var channel = new PublicUpdateChannel(new LoggingService());

        var accepted = await channel.DownloadAsync(
            client, "https://github.com/x/y/releases/download/v0.2.0/a.exe", target, 1024, CancellationToken.None);

        Assert.False(accepted);
        Assert.False(File.Exists(target), "a refused download must not leave a file behind");
    }

    /// <summary>The same redirect check applies to the metadata fetch, not only the artifact download:
    /// the checksum file travels the same way and must not be trusted from an off-allowlist host either.</summary>
    [Fact]
    public async Task Metadata_that_redirected_off_the_allowlist_is_refused_even_though_it_started_on_it()
    {
        var client = new HttpClient(new RedirectingHandler("https://evil.example.com/SHA256SUMS.txt"));
        var channel = new PublicUpdateChannel(new LoggingService());

        var body = await channel.GetStringAsync(
            client, "https://api.github.com/repos/x/y/releases", CancellationToken.None);

        Assert.Null(body);
    }

    /// <summary>Answers every request successfully, but with RequestMessage pointing somewhere other than
    /// the request that was sent (the shape HttpClient leaves behind after following a redirect).</summary>
    private sealed class RedirectingHandler(string finalUrl) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("irrelevant"),
                RequestMessage = new HttpRequestMessage(request.Method, finalUrl),
            };
            return Task.FromResult(response);
        }
    }

    /// <summary>
    /// The portable swap has to let go of the anti-swap lock to rename the running exe. A rollback that
    /// only put the file back left the rest of the session running unprotected, with the elevated worker
    /// still started by path. Rolling back retakes the lock, and Windows itself then refuses the rename
    /// again, which is the whole point of that handle.
    /// </summary>
    [Fact]
    public void A_rolled_back_portable_update_puts_the_image_back_and_locks_it_again()
    {
        var directory = Directory.CreateTempSubdirectory("wu-selfupdate-rollback-").FullName;
        var currentPath = Path.Combine(directory, "Ultimate Updater.exe");
        var backupPath = currentPath + ".old";

        // The state a failed swap leaves behind: the old image parked as .old, an unusable new one in place.
        File.WriteAllBytes(backupPath, [1]);
        File.WriteAllBytes(currentPath, [2]);

        try
        {
            SelfUpdateService.RollBack(backupPath, currentPath, new LoggingService());

            Assert.False(File.Exists(backupPath));
            Assert.Equal<byte>([1], File.ReadAllBytes(currentPath));
            Assert.ThrowsAny<IOException>(() => File.Move(currentPath, currentPath + ".swapped"));
        }
        finally
        {
            ElevationService.ReleaseSelfImageLock();
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// The swap gives up the anti-swap lock before it renames the running exe. When the rename itself fails
    /// (here a DIRECTORY planted at the backup path, which the stale-backup cleanup deliberately leaves
    /// alone) the session used to carry on with no lock at all, so the elevated worker, started by path,
    /// could be pointed at a swapped file. The lock must be held again, and Windows itself proves it: the
    /// rename of the image is refused.
    /// </summary>
    [Fact]
    public void A_failed_move_aside_returns_false_and_holds_the_image_locked_again()
    {
        var directory = Directory.CreateTempSubdirectory("wu-selfupdate-move-").FullName;
        var currentPath = Path.Combine(directory, "Ultimate Updater.exe");
        var verifiedPath = Path.Combine(directory, "new.bin");
        File.WriteAllBytes(currentPath, [1]);
        File.WriteAllBytes(verifiedPath, [2]);
        Directory.CreateDirectory(currentPath + ".old");
        File.WriteAllBytes(Path.Combine(currentPath + ".old", "keep"), [0]);

        try
        {
            var logging = new LoggingService();
            ElevationService.LockSelfImage(logging, currentPath);
            var service = new SelfUpdateService(new HttpClient(), logging, new PublicUpdateChannel(logging));

            var applied = service.ApplyPortable(verifiedPath, currentPath, new string('0', 64));

            Assert.False(applied);
            Assert.Equal<byte>([1], File.ReadAllBytes(currentPath));
            Assert.ThrowsAny<IOException>(() => File.Move(currentPath, currentPath + ".swapped"));
        }
        finally
        {
            ElevationService.ReleaseSelfImageLock();
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    [Fact]
    public void A_copy_that_does_not_match_the_verified_digest_is_rolled_back_and_the_image_is_locked_again()
    {
        var directory = Directory.CreateTempSubdirectory("wu-selfupdate-digest-").FullName;
        var currentPath = Path.Combine(directory, "Ultimate Updater.exe");
        var verifiedPath = Path.Combine(directory, "new.bin");
        File.WriteAllBytes(currentPath, [1]);
        File.WriteAllBytes(verifiedPath, [2]);

        try
        {
            var logging = new LoggingService();
            ElevationService.LockSelfImage(logging, currentPath);
            var service = new SelfUpdateService(new HttpClient(), logging, new PublicUpdateChannel(logging));

            var applied = service.ApplyPortable(verifiedPath, currentPath, new string('0', 64));

            Assert.False(applied);
            Assert.Equal<byte>([1], File.ReadAllBytes(currentPath));
            Assert.False(File.Exists(currentPath + ".old"));
            Assert.ThrowsAny<IOException>(() => File.Move(currentPath, currentPath + ".swapped"));
        }
        finally
        {
            ElevationService.ReleaseSelfImageLock();
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    /// <summary>With no known path for the running exe there is nothing to lock against replacement, so
    /// elevation is refused instead of the worker being started from a guessed path.</summary>
    [Fact]
    public void An_unknown_executable_path_refuses_elevation()
    {
        try
        {
            ElevationService.ResetElevationRefusalForTests();

            ElevationService.LockSelfImageAt(new LoggingService(), null);

            Assert.True(ElevationService.IsElevationRefused);
        }
        finally
        {
            ElevationService.ResetElevationRefusalForTests();
            ElevationService.ReleaseSelfImageLock();
        }
    }

    /// <summary>A rollback whose own move fails leaves an unverified file at the image path; locking that
    /// file would make it look trusted, so elevation is refused for the session instead.</summary>
    [Fact]
    public void A_rollback_that_cannot_restore_the_image_refuses_elevation_instead_of_locking()
    {
        var directory = Directory.CreateTempSubdirectory("wu-selfupdate-norestore-").FullName;
        var currentPath = Path.Combine(directory, "Ultimate Updater.exe");
        File.WriteAllBytes(currentPath, [2]);

        try
        {
            ElevationService.ResetElevationRefusalForTests();

            Assert.ThrowsAny<IOException>(() =>
                SelfUpdateService.RollBack(currentPath + ".old", currentPath, new LoggingService()));

            Assert.True(ElevationService.IsElevationRefused);
        }
        finally
        {
            ElevationService.ResetElevationRefusalForTests();
            ElevationService.ReleaseSelfImageLock();
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }
}

/// <summary>Exposes the internal allowlist check; the rule is security-relevant enough to test directly.</summary>
internal static class SelfUpdateServiceTestHook
{
    public static bool IsAllowedUrl(string url) => SelfUpdateService.IsAllowedUrl(url);

}
