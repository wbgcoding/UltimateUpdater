using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;
using UltimateUpdater.ViewModels;
using Xunit;

namespace UltimateUpdater.Tests;

/// <summary>
/// The background-enrichment registry parsers (PyPI, npm) and the failed-tool output summarizer added
/// in the 2026-07-24 feature round. All pure, so directly testable and required to tolerate garbage.
/// </summary>
[Collection("UiLanguage")]
public class EnrichmentAndDiagnosticsTests
{
    /// <summary>Unlike IconCacheService's disk cache, TTL eviction here is passive-only
    /// (a key drops out only when looked up again after expiring), so a long-running instance could grow
    /// this dictionary without bound. Uses a fresh, non-persisted instance (never calls Save()) so this
    /// does not touch the on-disk enrichment-cache.json every other test in this suite shares.</summary>
    [Fact]
    public void The_cache_never_grows_past_its_hard_cap()
    {
        var cache = new EnrichmentCache(new LoggingService());

        for (var i = 0; i < 5010; i++)
            cache.Set($"cap-test:{i}", new UpdateEnrichment(null, null));

        var survivors = Enumerable.Range(0, 5010).Count(i => cache.TryGet($"cap-test:{i}", out _));

        Assert.True(survivors <= 5000, $"cache held {survivors} entries, expected at most 5000");
    }

    /// <summary>The in-memory icon cache has no public way to read a cache hit back (a miss and a hit
    /// both return null for an item with no resolvable icon), so its FIFO cap is checked through the
    /// private dictionary directly rather than through GetIcon's return value.</summary>
    [Fact]
    public void The_icon_memory_cache_never_grows_past_its_hard_cap()
    {
        var cache = new IconCacheService(new LoggingService());

        for (var i = 0; i < 400; i++)
            cache.GetIcon("cap-test-source", $"cap-test-item-{i}", $"Nonexistent App {i}");

        var field = typeof(IconCacheService).GetField("_memoryCache",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var count = ((System.Collections.IDictionary)field!.GetValue(cache)!).Count;

        Assert.True(count <= 300, $"memory cache held {count} entries, expected at most 300");
    }

    [Fact]
    public void PyPi_release_yields_earliest_date_and_the_wheel_size()
    {
        const string json = """
            {
              "info": { "name": "requests" },
              "urls": [
                { "packagetype": "sdist", "size": 120000, "upload_time_iso_8601": "2024-05-29T15:37:49.123456Z" },
                { "packagetype": "bdist_wheel", "filename": "requests-2.0-py3-none-any.whl", "size": 640000, "upload_time_iso_8601": "2024-05-29T15:37:50.000000Z" }
              ]
            }
            """;

        var enrichment = PackageMetadataParsers.ParsePyPiRelease(json);

        Assert.Equal(640000, enrichment.DownloadSize);
        Assert.NotNull(enrichment.PublishDate);
        Assert.Equal(2024, enrichment.PublishDate!.Value.Year);
        Assert.Equal(5, enrichment.PublishDate!.Value.Month);
    }

    [Fact]
    public void PyPi_prefers_a_changelog_link_and_refuses_non_http_urls()
    {
        const string json = """
            {
              "info": {
                "home_page": "https://example.org",
                "project_urls": { "Homepage": "https://example.org", "Changelog": "https://example.org/CHANGELOG" }
              },
              "urls": [ { "size": 100, "upload_time_iso_8601": "2024-01-01T00:00:00Z" } ]
            }
            """;

        Assert.Equal("https://example.org/CHANGELOG", PackageMetadataParsers.ParsePyPiRelease(json).ProjectUrl);

        const string unsafeJson = """
            { "info": { "home_page": "file:///etc/passwd" }, "urls": [ { "size": 1, "upload_time_iso_8601": "2024-01-01T00:00:00Z" } ] }
            """;
        Assert.Null(PackageMetadataParsers.ParsePyPiRelease(unsafeJson).ProjectUrl);
    }

    [Fact]
    public void PyPi_changelog_url_is_strict_and_does_not_fall_back_to_a_homepage()
    {
        const string json = """
            {
              "info": {
                "home_page": "https://example.org",
                "project_urls": { "Homepage": "https://example.org", "Changelog": "https://example.org/CHANGELOG" }
              },
              "urls": [ { "size": 100, "upload_time_iso_8601": "2024-01-01T00:00:00Z" } ]
            }
            """;

        Assert.Equal("https://example.org/CHANGELOG", PackageMetadataParsers.ParsePyPiRelease(json).ChangelogUrl);

        const string homepageOnly = """
            {
              "info": { "home_page": "https://example.org", "project_urls": { "Homepage": "https://example.org" } },
              "urls": [ { "size": 100, "upload_time_iso_8601": "2024-01-01T00:00:00Z" } ]
            }
            """;

        Assert.Null(PackageMetadataParsers.ParsePyPiRelease(homepageOnly).ChangelogUrl);
        Assert.Equal("https://example.org/", PackageMetadataParsers.ParsePyPiRelease(homepageOnly).ProjectUrl);
    }

    [Fact]
    public void PyPi_publisher_prefers_the_plain_author_field()
    {
        const string json = """
            { "info": { "author": "Jane Doe", "maintainer": "Someone Else" }, "urls": [] }
            """;

        Assert.Equal("Jane Doe", PackageMetadataParsers.ParsePyPiRelease(json).Publisher);
    }

    /// <summary>Modern packaging metadata (a pyproject.toml "authors" table) commonly leaves the plain
    /// "author" field blank and folds the name into "author_email" as RFC 5322's "Name &lt;email&gt;"
    /// instead; the address itself must never end up as the publisher.</summary>
    [Fact]
    public void PyPi_publisher_reads_the_name_out_of_an_author_email_field()
    {
        const string json = """
            { "info": { "author": "", "author_email": "Jane Doe <jane@example.com>" }, "urls": [] }
            """;

        Assert.Equal("Jane Doe", PackageMetadataParsers.ParsePyPiRelease(json).Publisher);
    }

    [Fact]
    public void PyPi_publisher_ignores_a_bare_email_with_no_name_and_falls_back_to_maintainer()
    {
        const string json = """
            {
              "info": {
                "author": "", "author_email": "jane@example.com",
                "maintainer": "Maintainers Guild"
              },
              "urls": []
            }
            """;

        Assert.Equal("Maintainers Guild", PackageMetadataParsers.ParsePyPiRelease(json).Publisher);
    }

    [Fact]
    public void PyPi_publisher_falls_back_to_the_github_owner_when_nothing_else_is_declared()
    {
        const string json = """
            {
              "info": {
                "project_urls": { "Homepage": "https://example.org", "Source": "https://github.com/pallets/flask" }
              },
              "urls": []
            }
            """;

        Assert.Equal("pallets", PackageMetadataParsers.ParsePyPiRelease(json).Publisher);
    }

    [Fact]
    public void PyPi_publisher_is_null_when_nothing_at_all_is_declared()
    {
        const string json = """{ "info": { "project_urls": { "Homepage": "https://example.org" } }, "urls": [] }""";

        Assert.Null(PackageMetadataParsers.ParsePyPiRelease(json).Publisher);
    }

    [Fact]
    public void Npm_normalises_a_git_repository_url()
    {
        const string json = """
            {
              "time": { "1.0.0": "2024-01-01T00:00:00Z" },
              "repository": { "type": "git", "url": "git+https://github.com/x/y.git" },
              "versions": { "1.0.0": { "dist": {} } }
            }
            """;

        Assert.Equal("https://github.com/x/y", PackageMetadataParsers.ParseNpmPackument(json, "1.0.0").ProjectUrl);
    }

    /// <summary>A registry response is hostile input: a field documented as a string can arrive as a
    /// number, null, or an object (real-world API inconsistency). Every one of these must degrade to "no
    /// value" for that field rather than throwing InvalidOperationException out of a pure parser.</summary>
    [Fact]
    public void A_non_string_field_degrades_instead_of_throwing()
    {
        const string npmJson = """
            {
              "time": { "1.0.0": 12345 },
              "homepage": 42,
              "versions": { "1.0.0": { "dist": {} } }
            }
            """;
        var npmEnrichment = PackageMetadataParsers.ParseNpmPackument(npmJson, "1.0.0");
        Assert.Null(npmEnrichment.PublishDate);
        Assert.Null(npmEnrichment.ProjectUrl);

        const string pypiJson = """
            {
              "info": {
                "home_page": { "nested": "object" },
                "project_urls": { "Changelog": 123 }
              },
              "urls": [ { "size": 1, "upload_time_iso_8601": "2024-01-01T00:00:00Z" } ]
            }
            """;
        var pypiEnrichment = PackageMetadataParsers.ParsePyPiRelease(pypiJson);
        Assert.Null(pypiEnrichment.ProjectUrl);
        Assert.Null(pypiEnrichment.ChangelogUrl);
    }

    [Fact]
    public void PyPi_missing_urls_yields_nothing()
    {
        Assert.Equal(default, PackageMetadataParsers.ParsePyPiRelease("{\"info\":{}}"));
        Assert.Equal(default, PackageMetadataParsers.ParsePyPiRelease("not json"));
        Assert.Equal(default, PackageMetadataParsers.ParsePyPiRelease(""));
    }

    [Fact]
    public void Npm_packument_reads_time_and_unpacked_size_for_the_version()
    {
        const string json = """
            {
              "name": "typescript",
              "time": { "5.4.5": "2024-04-10T13:40:00.000Z", "5.4.4": "2024-03-01T00:00:00.000Z" },
              "versions": { "5.4.5": { "dist": { "unpackedSize": 22000000 } } }
            }
            """;

        var enrichment = PackageMetadataParsers.ParseNpmPackument(json, "5.4.5");

        Assert.Equal(22000000, enrichment.DownloadSize);
        Assert.NotNull(enrichment.PublishDate);
        Assert.Equal(4, enrichment.PublishDate!.Value.Month);
    }

    [Fact]
    public void Npm_unknown_version_or_garbage_yields_nothing()
    {
        const string json = "{ \"time\": { \"1.0.0\": \"2024-01-01T00:00:00Z\" } }";

        Assert.Equal(default, PackageMetadataParsers.ParseNpmPackument(json, "2.0.0"));
        Assert.Equal(default, PackageMetadataParsers.ParseNpmPackument("garbage", "1.0.0"));
        Assert.Equal(default, PackageMetadataParsers.ParseNpmPackument(json, ""));
    }

    [Fact]
    public void Npm_tarball_url_is_read_for_the_exact_version_and_refuses_non_http()
    {
        const string json = """
            {
              "versions": {
                "5.4.5": { "dist": { "tarball": "https://registry.npmjs.org/typescript/-/typescript-5.4.5.tgz" } },
                "5.4.4": { "dist": { "tarball": "https://registry.npmjs.org/typescript/-/typescript-5.4.4.tgz" } }
              }
            }
            """;

        Assert.Equal(
            "https://registry.npmjs.org/typescript/-/typescript-5.4.5.tgz",
            PackageMetadataParsers.ParseNpmTarballUrl(json, "5.4.5"));
        Assert.Null(PackageMetadataParsers.ParseNpmTarballUrl(json, "9.9.9"));
        Assert.Null(PackageMetadataParsers.ParseNpmTarballUrl("garbage", "5.4.5"));

        const string unsafeJson = """{ "versions": { "1.0.0": { "dist": { "tarball": "file:///etc/passwd" } } } }""";
        Assert.Null(PackageMetadataParsers.ParseNpmTarballUrl(unsafeJson, "1.0.0"));
    }

    [Fact]
    public void Node_release_date_matches_the_exact_version_not_the_newest()
    {
        const string json = """
            [
              { "version": "v20.12.0", "date": "2024-03-26" },
              { "version": "v20.11.0", "date": "2024-01-09" }
            ]
            """;

        var date = VoltaParsers.ParseNodeReleaseDate(json, "20.11.0");

        Assert.NotNull(date);
        Assert.Equal(1, date!.Value.Month);
        Assert.Equal(9, date.Value.Day);
        Assert.Null(VoltaParsers.ParseNodeReleaseDate(json, "99.0.0"));
        Assert.Null(VoltaParsers.ParseNodeReleaseDate("garbage", "20.11.0"));
    }

    [Fact]
    public void Rustup_channel_date_reads_the_top_level_field_only()
    {
        const string toml = """
            manifest-version = "2"
            date = "2026-08-01"

            [pkg.rustc]
            version = "1.80.0 (abc 2026-08-01)"

            [pkg.rustc.target.x86_64-pc-windows-msvc]
            date = "2099-01-01"
            available = true
            """;

        var date = PackageMetadataParsers.ParseRustupChannelDate(toml);

        Assert.NotNull(date);
        Assert.Equal(2026, date!.Value.Year);
        Assert.Equal(8, date.Value.Month);
        Assert.Equal(1, date.Value.Day);
    }

    [Fact]
    public void Rustup_channel_date_missing_or_garbage_yields_nothing()
    {
        Assert.Null(PackageMetadataParsers.ParseRustupChannelDate("[pkg.rustc]\nversion = \"1.0.0\""));
        Assert.Null(PackageMetadataParsers.ParseRustupChannelDate(""));
        Assert.Null(PackageMetadataParsers.ParseRustupChannelDate("not toml at all"));
    }

    [Fact]
    public void Winget_show_release_date_reads_english_and_german_labels()
    {
        const string english = """
            Found Git [Git.Git]
            Version: 2.55.0.3
            Publisher: The Git Development Community
            Installer:
              Installer Type: inno
              Release Date: 2026-07-14
              Installer Url: https://github.com/git-for-windows/git/releases/download/v2.55.0.windows.3/Git-2.55.0.3-64-bit.exe
            """;

        // Captured live against a real winget install, 2026-08-08 (German OS display language).
        const string german = """
            Gefunden Git [Git.Git]
            Version: 2.55.0.3
            Herausgeber: The Git Development Community
            Installationsprogramm:
              Installertyp: inno
              Installer-URL: https://github.com/git-for-windows/git/releases/download/v2.55.0.windows.3/Git-2.55.0.3-64-bit.exe
              Freigabedatum: 2026-07-14
            """;

        foreach (var stdout in new[] { english, german })
        {
            var date = WingetUpgradeParser.ParseShowReleaseDate(stdout);
            Assert.NotNull(date);
            Assert.Equal(2026, date!.Value.Year);
            Assert.Equal(7, date.Value.Month);
            Assert.Equal(14, date.Value.Day);
        }
    }

    [Fact]
    public void Winget_show_release_date_missing_or_unknown_locale_yields_nothing()
    {
        Assert.Null(WingetUpgradeParser.ParseShowReleaseDate("Version: 1.0.0\nPublisher: Example"));
        Assert.Null(WingetUpgradeParser.ParseShowReleaseDate("Date de sortie: 2026-07-14"));
        Assert.Null(WingetUpgradeParser.ParseShowReleaseDate(""));
    }

    [Fact]
    public void Npm_latest_manifest_version_is_read()
    {
        const string json = """{ "name": "yarn", "version": "1.22.19", "dist": { "tarball": "https://registry.npmjs.org/yarn/-/yarn-1.22.19.tgz" } }""";

        Assert.Equal("1.22.19", PackageMetadataParsers.ParseNpmManifestVersion(json));
    }

    [Fact]
    public void Npm_latest_manifest_missing_or_garbage_yields_nothing()
    {
        Assert.Null(PackageMetadataParsers.ParseNpmManifestVersion("{}"));
        Assert.Null(PackageMetadataParsers.ParseNpmManifestVersion("""{ "version": 3 }"""));
        Assert.Null(PackageMetadataParsers.ParseNpmManifestVersion("not json"));
        Assert.Null(PackageMetadataParsers.ParseNpmManifestVersion(""));
    }

    [Fact]
    public void RubyGems_version_date_matches_by_number()
    {
        const string json = """
            [
              { "number": "8.1.3.1", "created_at": "2026-07-29T15:02:41.060Z" },
              { "number": "8.1.3.0", "created_at": "2026-06-01T00:00:00.000Z" }
            ]
            """;

        var date = PackageMetadataParsers.ParseRubyGemsVersionDate(json, "8.1.3.1");

        Assert.NotNull(date);
        Assert.Equal(7, date!.Value.Month);
        Assert.Equal(29, date.Value.Day);
    }

    [Fact]
    public void RubyGems_missing_version_or_garbage_yields_nothing()
    {
        const string json = """[{ "number": "1.0.0", "created_at": "2024-01-01T00:00:00Z" }]""";

        Assert.Null(PackageMetadataParsers.ParseRubyGemsVersionDate(json, "2.0.0"));
        Assert.Null(PackageMetadataParsers.ParseRubyGemsVersionDate("not json", "1.0.0"));
        Assert.Null(PackageMetadataParsers.ParseRubyGemsVersionDate(json, ""));
    }

    [Fact]
    public void Packagist_version_reads_time_and_dist_url_for_the_matched_version()
    {
        const string json = """
            {
              "packages": {
                "symfony/console": [
                  { "version": "v8.1.2", "version_normalized": "8.1.2.0",
                    "time": "2026-07-27T13:58:19+00:00", "dist": { "url": "https://api.github.com/repos/symfony/console/zipball/abc" } },
                  { "version": "v8.1.1", "version_normalized": "8.1.1.0",
                    "time": "2026-06-01T00:00:00+00:00", "dist": { "url": "https://example/old.zip" } }
                ]
              }
            }
            """;

        var (publishDate, downloadUrl) = PackageMetadataParsers.ParsePackagistVersion(json, "symfony/console", "8.1.2");

        Assert.NotNull(publishDate);
        Assert.Equal(7, publishDate!.Value.Month);
        Assert.Equal("https://api.github.com/repos/symfony/console/zipball/abc", downloadUrl);
    }

    /// <summary>
    /// A prefix match on version strings is unsafe: "1.2" must not match "1.20.0.0", or another
    /// release's date and download URL lands on the row.
    /// </summary>
    [Fact]
    public void Packagist_version_does_not_match_a_longer_numbered_release()
    {
        const string json = """
            {
              "packages": {
                "vendor/pkg": [
                  { "version": "v1.20.0", "version_normalized": "1.20.0.0",
                    "time": "2026-07-01T00:00:00+00:00", "dist": { "url": "https://example/wrong.zip" } },
                  { "version": "v1.2.0", "version_normalized": "1.2.0.0",
                    "time": "2026-01-01T00:00:00+00:00", "dist": { "url": "https://example/right.zip" } }
                ]
              }
            }
            """;

        var (publishDate, downloadUrl) = PackageMetadataParsers.ParsePackagistVersion(json, "vendor/pkg", "1.2");

        Assert.Equal("https://example/right.zip", downloadUrl);
        Assert.Equal(1, publishDate!.Value.Month);
    }

    // Real manifest fetched live from ScoopInstaller/Main (raw.githubusercontent.com/.../bucket/7zip.json,
    // 2026-07-31), verifies the parser against scoop's actual schema, not a guessed shape.
    private const string ScoopSevenZipManifest = """
        {
            "version": "26.02",
            "architecture": {
                "64bit": { "url": "https://github.com/ip7z/7zip/releases/download/26.02/7z2602-x64.msi", "hash": "db40" },
                "32bit": { "url": "https://github.com/ip7z/7zip/releases/download/26.02/7z2602.msi", "hash": "5517" },
                "arm64": { "url": "https://github.com/ip7z/7zip/releases/download/26.02/7z2602-arm64.exe", "hash": "7c6f" }
            }
        }
        """;

    [Fact]
    public void Scoop_manifest_picks_64bit_by_default()
    {
        Assert.Equal(
            "https://github.com/ip7z/7zip/releases/download/26.02/7z2602-x64.msi",
            PackageMetadataParsers.ParseScoopManifestDownloadUrl(ScoopSevenZipManifest, preferArm64: false));
    }

    [Fact]
    public void Scoop_manifest_prefers_arm64_when_asked()
    {
        Assert.Equal(
            "https://github.com/ip7z/7zip/releases/download/26.02/7z2602-arm64.exe",
            PackageMetadataParsers.ParseScoopManifestDownloadUrl(ScoopSevenZipManifest, preferArm64: true));
    }

    [Fact]
    public void Scoop_manifest_without_architecture_block_reads_the_flat_url()
    {
        const string json = """{ "version": "1.0", "url": "https://example.com/tool.zip", "hash": "abc" }""";

        Assert.Equal("https://example.com/tool.zip",
            PackageMetadataParsers.ParseScoopManifestDownloadUrl(json, preferArm64: false));
    }

    [Fact]
    public void Scoop_manifest_garbage_or_missing_url_yields_nothing()
    {
        Assert.Null(PackageMetadataParsers.ParseScoopManifestDownloadUrl("not json", preferArm64: false));
        Assert.Null(PackageMetadataParsers.ParseScoopManifestDownloadUrl("{}", preferArm64: false));
    }

    [Fact]
    public void Scoop_manifest_homepage_is_read_and_refuses_non_http()
    {
        const string json = """{ "version": "1.0", "homepage": "https://example.com/tool", "url": "https://example.com/tool.zip" }""";
        Assert.Equal("https://example.com/tool", PackageMetadataParsers.ParseScoopManifestHomepage(json));

        Assert.Null(PackageMetadataParsers.ParseScoopManifestHomepage("""{ "version": "1.0" }"""));
        Assert.Null(PackageMetadataParsers.ParseScoopManifestHomepage("not json"));
    }

    // Trimmed from the real manifest at dl.google.com/android/repository/repository2-3.xml, fetched
    // live 2026-07-31, same shape (multiple <archive> entries per OS, <complete><size> in bytes).
    private const string AndroidSdkManifest = """
        <sdk:sdk-repository xmlns:sdk="http://schemas.android.com/sdk/android/repo/repository2/3">
          <remotePackage path="build-tools;35.0.1">
            <archives>
              <archive>
                <complete><size>61959297</size></complete>
                <host-os>linux</host-os>
              </archive>
              <archive>
                <complete><size>59876477</size></complete>
                <host-os>windows</host-os>
              </archive>
              <archive>
                <complete><size>76857925</size></complete>
                <host-os>macosx</host-os>
              </archive>
            </archives>
          </remotePackage>
          <remotePackage path="platform-tools">
            <archives>
              <archive>
                <complete><size>12345678</size></complete>
              </archive>
            </archives>
          </remotePackage>
        </sdk:sdk-repository>
        """;

    [Fact]
    public void Android_sdk_size_picks_the_windows_archive()
    {
        Assert.Equal(59876477, PackageMetadataParsers.ParseAndroidSdkPackageSize(AndroidSdkManifest, "build-tools;35.0.1"));
    }

    [Fact]
    public void Android_sdk_size_accepts_an_os_agnostic_archive()
    {
        Assert.Equal(12345678, PackageMetadataParsers.ParseAndroidSdkPackageSize(AndroidSdkManifest, "platform-tools"));
    }

    [Fact]
    public void Android_sdk_size_unknown_package_or_garbage_yields_nothing()
    {
        Assert.Null(PackageMetadataParsers.ParseAndroidSdkPackageSize(AndroidSdkManifest, "build-tools;99.0.0"));
        Assert.Null(PackageMetadataParsers.ParseAndroidSdkPackageSize("not xml", "platform-tools"));
        Assert.Null(PackageMetadataParsers.ParseAndroidSdkPackageSize(AndroidSdkManifest, ""));
    }

    [Fact]
    public void Packagist_missing_package_or_version_yields_nothing()
    {
        const string json = """{ "packages": { "vendor/pkg": [ { "version": "1.0.0", "time": "2024-01-01T00:00:00Z" } ] } }""";

        Assert.Equal((null, null), PackageMetadataParsers.ParsePackagistVersion(json, "vendor/pkg", "9.9.9"));
        Assert.Equal((null, null), PackageMetadataParsers.ParsePackagistVersion(json, "other/pkg", "1.0.0"));
        Assert.Equal((null, null), PackageMetadataParsers.ParsePackagistVersion("not json", "vendor/pkg", "1.0.0"));
    }

    [Fact]
    public void Summarize_keeps_the_real_error_at_the_tail_not_the_banner()
    {
        var stdout = string.Join("\n",
            "Found FreeCAD [FreeCAD.FreeCAD] Version 1.0.0",
            "This application is licensed to you by its owner.",
            "Downloading https://example/freecad.exe",
            "  ██████████████  50%",
            "  ████████████████████████  100%",
            "Installer failed with exit code: 1603");

        var summary = ToolOutput.Summarize(stdout, standardError: null, exitCode: 1603);

        Assert.Contains("Installer failed with exit code: 1603", summary);
        Assert.Contains("(exit code 1603)", summary);
        // the progress-bar noise is dropped
        Assert.DoesNotContain("██", summary);
    }

    [Fact]
    public void Summarize_never_empty_even_with_no_output()
    {
        var summary = ToolOutput.Summarize(standardOutput: "", standardError: "   ", exitCode: 5);

        Assert.Contains("5", summary);
        Assert.NotEqual(string.Empty, summary.Trim());
    }

    [Fact]
    public void Row_date_follows_the_chosen_ui_language_short_date_order()
    {
        var published = new DateTimeOffset(2024, 3, 7, 12, 0, 0, TimeSpan.Zero);

        try
        {
            LocalizationService.Instance.Apply("de");
            var germanRow = new UpdateRowViewModel(TestItem(published: published));
            Assert.True(germanRow.HasPublishDate);
            Assert.Matches(new Regex(@"^\d{2}\.\d{2}\.\d{4}$"), germanRow.PublishDateText!);
        }
        finally
        {
            LocalizationService.Instance.Apply("en");
        }

        var englishRow = new UpdateRowViewModel(TestItem(published: published));
        Assert.True(englishRow.HasPublishDate);
        Assert.Equal(
            published.ToLocalTime().ToString("d", CultureInfo.CurrentUICulture),
            englishRow.PublishDateText);
        Assert.DoesNotMatch(new Regex(@"^\d{2}\.\d{2}\.\d{4}$"), englishRow.PublishDateText!);

        LocalizationService.Instance.Apply("en");
    }

    [Fact]
    public void Enrichment_fills_a_missing_date_and_size_but_never_overwrites_a_real_one()
    {
        var blank = new UpdateRowViewModel(TestItem());
        blank.ApplyEnrichment(new DateTimeOffset(2024, 1, 2, 0, 0, 0, TimeSpan.Zero), 12345);
        Assert.True(blank.HasPublishDate);
        Assert.True(blank.HasDownloadSize);

        var alreadyDated = new UpdateRowViewModel(
            TestItem(published: new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        var before = alreadyDated.PublishDateText;
        alreadyDated.ApplyEnrichment(new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero), null);
        Assert.Equal(before, alreadyDated.PublishDateText);
    }

    [Fact]
    public void Indeterminate_progress_creeps_while_running_then_a_real_reading_takes_over()
    {
        var row = new UpdateRowViewModel(TestItem());
        row.BeginInstall();
        row.ApplyInstallStatus(new InstallItemStatus("winget", "x", 0, "working", null, null));
        Assert.Equal(RowInstallState.Running, row.InstallState);

        row.AdvanceIndeterminate();
        row.AdvanceIndeterminate();
        Assert.True(row.InstallPercent is > 0 and <= 90);

        // a real percentage arrives and pins the bar; the creep stops honouring it
        row.ApplyInstallStatus(new InstallItemStatus("winget", "x", 42, null, null, null));
        Assert.Equal(42, row.InstallPercent);
        row.AdvanceIndeterminate();
        Assert.Equal(42, row.InstallPercent);
    }

    /// <summary>
    /// a package the manager has no fitting version for ends on its own terminal state,
    /// the row shows the word in caution colour and NOT the red failure icon, which is reserved for an
    /// install that actually went wrong. 22.08.2026: the column shows only "Failed" now, the sentence
    /// moved to the tooltip, and the state gained a working error-detail button (it never had one before,
    /// since Detail always arrives null for this path, only Status carries the reason).
    /// </summary>
    [Fact]
    public void A_not_applicable_result_leaves_the_row_stuck_rather_than_failed()
    {
        var row = new UpdateRowViewModel(TestItem());
        row.BeginInstall();
        row.ApplyInstallStatus(
            new InstallItemStatus("winget", "x", 100, "no applicable upgrade", null, null) { IsNotApplicable = true });

        Assert.Equal(RowInstallState.Stuck, row.InstallState);
        Assert.False(row.IsInstallFailed);
        Assert.True(row.ShowStateWord);
        Assert.Equal(LocalizationService.Instance["RowStuck"], row.InstallStateText);
        Assert.Contains(LocalizationService.Instance["RowStuckDetail"], row.InstallStateTooltip);
        Assert.Contains("no applicable upgrade", row.InstallStateTooltip);
        Assert.True(row.HasInstallError);
        Assert.Equal("no applicable upgrade", row.InstallErrorDetail);
        Assert.False(row.IsSelected);
    }

    /// <summary>The queue reports a stuck row with the translated sentence itself as its line and the
    /// source's raw text as Detail; the tooltip shows that sentence once, not twice.</summary>
    [Fact]
    public void A_stuck_row_reported_with_the_translated_line_does_not_repeat_it_in_the_tooltip()
    {
        var row = new UpdateRowViewModel(TestItem());
        row.BeginInstall();
        var stuck = LocalizationService.Instance["RowStuckDetail"];
        row.ApplyInstallStatus(
            new InstallItemStatus("winget", "x", 100, null, null, stuck) { IsNotApplicable = true, Detail = "raw ladder" });

        Assert.Equal(RowInstallState.Stuck, row.InstallState);
        Assert.Equal(stuck, row.InstallStateTooltip);
        Assert.Equal("raw ladder", row.InstallErrorDetail);
    }

    /// <summary>Same fix as the Stuck case above, for the other terminal state that is neither success
    /// nor failure, the vendor's own window took over and this program cannot see what happened next.</summary>
    [Fact]
    public void A_manual_handoff_result_leaves_the_row_handed_off_rather_than_failed()
    {
        var row = new UpdateRowViewModel(TestItem());
        row.BeginInstall();
        row.ApplyInstallStatus(
            new InstallItemStatus("epic", "x", 100, "opened the Epic Games Launcher", null, null) { IsManualHandoff = true });

        Assert.Equal(RowInstallState.HandedOff, row.InstallState);
        Assert.False(row.IsInstallFailed);
        Assert.True(row.ShowStateWord);
        Assert.Equal(LocalizationService.Instance["RowHandedOff"], row.InstallStateText);
        Assert.Contains(LocalizationService.Instance["RowHandedOffDetail"], row.InstallStateTooltip);
        Assert.Contains("opened the Epic Games Launcher", row.InstallStateTooltip);
        Assert.True(row.HasInstallError);
        Assert.Equal("opened the Epic Games Launcher", row.InstallErrorDetail);
        Assert.False(row.IsSelected);
    }

    /// <summary>A failed row's second line is the translated explainer text and replaces the source line;
    /// when no code is recognised it is the bare "failed" word, never the raw tool output (that stays behind
    /// the Details click).</summary>
    [Fact]
    public void A_failed_row_exposes_the_explainer_text_as_its_failure_line_and_never_raw_output()
    {
        var row = new UpdateRowViewModel(TestItem());
        Assert.True(row.ShowSourceText);

        row.BeginInstall();
        row.ApplyInstallStatus(new InstallItemStatus(row.SourceId, row.Item.Id, 0, "raw banner RAW-LINE", false, "raw banner RAW-LINE")
        {
            Detail = "Installer failed with exit code: 1602",
        });

        var explained = InstallErrorExplainer.Describe(
            row.SourceId, row.InstallErrorDetail, key => LocalizationService.Instance[key]);
        Assert.NotNull(explained);
        Assert.True(row.IsInstallFailed);
        Assert.Equal(explained, row.FailureLine);
        Assert.False(row.ShowSourceText);

        // Nothing recognisable: the bare word, not the raw status line the tooltip may fall back to.
        var unknown = new UpdateRowViewModel(TestItem());
        unknown.BeginInstall();
        unknown.ApplyInstallStatus(new InstallItemStatus(unknown.SourceId, unknown.Item.Id, 0, "raw banner RAW-LINE", false, "raw banner RAW-LINE")
        {
            Detail = "raw banner RAW-LINE",
        });
        Assert.Equal(LocalizationService.Instance["RowFailed"], unknown.FailureLine);
        Assert.DoesNotContain("RAW-LINE", unknown.FailureLine);
    }

    /// <summary>A cancelled row that carries its own reason (a blocked row the user skipped) shows that
    /// reason as tooltip and as the second line; a row the user stopped keeps the generic stop tooltip and
    /// the source line.</summary>
    [Fact]
    public void A_cancelled_row_with_a_reason_shows_it_as_tooltip_and_second_line()
    {
        var skipped = new UpdateRowViewModel(TestItem());
        Assert.False(skipped.ShowCancelledReason);
        skipped.BeginInstall();
        skipped.ApplyInstallStatus(new InstallItemStatus(skipped.SourceId, skipped.Item.Id, 0, "Skipped, Discord is still open", null, null)
        {
            IsCancelled = true,
        });

        Assert.Equal(RowInstallState.Cancelled, skipped.InstallState);
        Assert.Equal("Skipped, Discord is still open", skipped.InstallStateTooltip);
        Assert.True(skipped.ShowCancelledReason);
        Assert.Equal("Skipped, Discord is still open", skipped.CancelledReasonLine);
        Assert.False(skipped.ShowSourceText);

        var stopped = new UpdateRowViewModel(TestItem());
        stopped.BeginInstall();
        stopped.ApplyInstallStatus(new InstallItemStatus(stopped.SourceId, stopped.Item.Id, 100, null, null, null)
        {
            IsCancelled = true,
        });

        Assert.Equal(LocalizationService.Instance["RowCancelledTooltip"], stopped.InstallStateTooltip);
        Assert.False(stopped.ShowCancelledReason);
        Assert.True(stopped.ShowSourceText);
    }

    [Fact]
    public void Per_row_install_button_shows_only_for_an_idle_installable_row_and_invokes_the_action()
    {
        var actions = new CapturingActions();
        var row = new UpdateRowViewModel(TestItem(), actions: actions);

        Assert.True(row.ShowInstallButton);

        Assert.True(row.InstallCommand.CanExecute(null));
        row.InstallCommand.Execute(null);
        Assert.Same(row, actions.Installed);

        // once queued it is no longer offered as an idle install
        row.BeginInstall();
        Assert.False(row.ShowInstallButton);
    }

    [Fact]
    public void A_tier_c_open_row_offers_open_not_install()
    {
        var open = new UpdateItem
        {
            Id = "store", SourceId = "msstore", Category = UpdateCategory.Store, Name = "Store",
            Tier = SourceTier.C, OpenTarget = "ms-windows-store://",
        };
        var row = new UpdateRowViewModel(open);

        Assert.False(row.ShowInstallButton);
        Assert.True(row.ShowOpenButton);
    }

    [Fact]
    public void Ignore_flag_gates_install_and_retry_and_flips_the_menu_wording()
    {
        var actions = new CapturingActions();
        var row = new UpdateRowViewModel(TestItem(), actions: actions);

        Assert.True(row.CanInstall);
        Assert.False(row.ShowRetryButton);

        row.IsIgnored = true;
        Assert.False(row.CanInstall);
        Assert.False(row.ShowInstallButton);
        // menu wording toggles to "show again"
        Assert.Equal(row.UnignoreHeader, row.IgnoreHeader);

        // "Ignore all versions" now applies to THIS package only
        row.IgnoreAllVersionsCommand.Execute(null);
        Assert.Same(row, actions.IgnoredAllVersionsOf);
    }

    private sealed class CapturingActions : IRowActions
    {
        public UpdateRowViewModel? Installed { get; private set; }
        public UpdateRowViewModel? IgnoredAllVersionsOf { get; private set; }

        public bool CanStart(UpdateItem item) => false;
        public bool CanUninstall(UpdateItem item) => false;
        public void Start(UpdateRowViewModel row) { }
        public void Uninstall(UpdateRowViewModel row) { }
        public void InstallSingle(UpdateRowViewModel row) => Installed = row;
        public void IgnoreAllVersions(UpdateRowViewModel row) => IgnoredAllVersionsOf = row;
    }

    [Fact]
    public void NuGet_registration_leaf_yields_the_publish_date_and_drops_the_unlisted_sentinel()
    {
        const string listed = """{ "catalogEntry": { "version": "8.0.0", "published": "2023-11-14T17:00:00+00:00" } }""";
        var enrichment = PackageMetadataParsers.ParseNuGetPublished(listed);
        Assert.NotNull(enrichment.PublishDate);
        Assert.Equal(2023, enrichment.PublishDate!.Value.Year);

        const string unlisted = """{ "catalogEntry": { "published": "1900-01-01T00:00:00+00:00" } }""";
        Assert.Null(PackageMetadataParsers.ParseNuGetPublished(unlisted).PublishDate);
        Assert.Equal(default, PackageMetadataParsers.ParseNuGetPublished("not json"));
    }

    [Fact]
    public void NuGet_registration_leaf_reads_release_notes_when_present()
    {
        const string withNotes = """
            { "catalogEntry": { "version": "8.0.0", "published": "2023-11-14T17:00:00+00:00", "releaseNotes": "Fixed a crash." } }
            """;
        Assert.Equal("Fixed a crash.", PackageMetadataParsers.ParseNuGetPublished(withNotes).ChangelogBody);

        const string withoutNotes = """{ "catalogEntry": { "version": "8.0.0", "published": "2023-11-14T17:00:00+00:00" } }""";
        Assert.Null(PackageMetadataParsers.ParseNuGetPublished(withoutNotes).ChangelogBody);
    }

    [Fact]
    public void Chocolatey_odata_entry_yields_date_and_size_namespace_agnostically()
    {
        const string xml = """
            <entry xmlns:d="http://schemas.microsoft.com/ado/2007/08/dataservices"
                   xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata">
              <m:properties>
                <d:Published m:type="Edm.DateTime">2023-06-15T10:00:00</d:Published>
                <d:PackageSize m:type="Edm.Int64">1234567</d:PackageSize>
              </m:properties>
            </entry>
            """;

        var enrichment = PackageMetadataParsers.ParseChocolateyODataMeta(xml);
        Assert.Equal(1234567, enrichment.DownloadSize);
        Assert.NotNull(enrichment.PublishDate);
        Assert.Equal(6, enrichment.PublishDate!.Value.Month);

        Assert.Equal(default, PackageMetadataParsers.ParseChocolateyODataMeta("<broken"));
    }

    [Fact]
    public void Chocolatey_odata_entry_reads_release_notes_when_present()
    {
        const string withNotes = """
            <entry xmlns:d="http://schemas.microsoft.com/ado/2007/08/dataservices"
                   xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata">
              <m:properties>
                <d:Published m:type="Edm.DateTime">2023-06-15T10:00:00</d:Published>
                <d:ReleaseNotes>Fixed a crash.</d:ReleaseNotes>
              </m:properties>
            </entry>
            """;
        Assert.Equal("Fixed a crash.", PackageMetadataParsers.ParseChocolateyODataMeta(withNotes).ChangelogBody);

        const string withoutNotes = """
            <entry xmlns:d="http://schemas.microsoft.com/ado/2007/08/dataservices"
                   xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata">
              <m:properties><d:Published m:type="Edm.DateTime">2023-06-15T10:00:00</d:Published></m:properties>
            </entry>
            """;
        Assert.Null(PackageMetadataParsers.ParseChocolateyODataMeta(withoutNotes).ChangelogBody);
    }

    [Fact]
    public void Row_sort_keys_put_missing_date_and_size_last_when_ascending()
    {
        var dated = new UpdateRowViewModel(TestItem(published: new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        var undated = new UpdateRowViewModel(TestItem());

        Assert.True(dated.SortDate > undated.SortDate);
        Assert.Equal(0L, undated.SortSize);
        Assert.Equal("1.1", dated.SortVersion);
    }

    [Fact]
    public void Winget_reboot_codes_are_recognised_but_still_count_as_success()
    {
        Assert.True(UltimateUpdater.Services.Sources.WingetSource.IsRebootRequired(3010));
        Assert.True(UltimateUpdater.Services.Sources.WingetSource.IsRebootRequired(1641));
        Assert.True(UltimateUpdater.Services.Sources.WingetSource.IsRebootRequired(unchecked((int)0x8A150109u)));

        Assert.False(UltimateUpdater.Services.Sources.WingetSource.IsRebootRequired(0));
        Assert.False(UltimateUpdater.Services.Sources.WingetSource.IsRebootRequired(unchecked((int)0x8A15002Bu)));

        // reboot codes are still success, the update did install
        Assert.True(UltimateUpdater.Services.Sources.WingetSource.IsInstallSuccess(3010));
    }

    [Fact]
    public void Enrichment_cache_returns_stored_values_and_misses_unknown_keys()
    {
        var cache = new EnrichmentCache(new LoggingService());
        var key = EnrichmentCache.Key("pip", "requests", "2.32.0");

        Assert.False(cache.TryGet(key, out _));

        cache.Set(key, new UltimateUpdater.Services.Sources.UpdateEnrichment(
            new DateTimeOffset(2024, 5, 1, 0, 0, 0, TimeSpan.Zero), 640000));

        Assert.True(cache.TryGet(key, out var hit));
        Assert.Equal(640000, hit.DownloadSize);
        Assert.False(cache.TryGet(EnrichmentCache.Key("pip", "requests", "9.9.9"), out _));
    }

    /// <summary>
    /// two overlapping saves wrote the same file at the same time, so one of them was lost and a
    /// reader could catch a half-written file. Saving is serialised and lands through a temporary file, so
    /// what is on disk is always complete and no leftover temporary is left lying around.
    /// </summary>
    [Fact]
    public async Task Concurrent_cache_saves_leave_one_complete_file_and_no_leftovers()
    {
        var cache = new EnrichmentCache(new LoggingService());
        for (var i = 0; i < 50; i++)
        {
            cache.Set(
                EnrichmentCache.Key("pip", $"package{i}", "1.0.0"),
                new UltimateUpdater.Services.Sources.UpdateEnrichment(DateTimeOffset.UtcNow, 1234));
        }

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(cache.Save)));

        var path = Path.Combine(AppPaths.DataDirectory, "enrichment-cache.json");
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"), "a temporary file must never survive a save");

        // Every key written must have survived, but not "and nothing else": the cache file is shared with
        // whatever else used this data directory, and an absolute count made the test depend on that.
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var stored = document.RootElement.EnumerateObject().Select(entry => entry.Name).ToHashSet();

        for (var i = 0; i < 50; i++)
            Assert.Contains(EnrichmentCache.Key("pip", $"package{i}", "1.0.0"), stored);
    }

    /// <summary>
    /// The 8 MB ceiling is enforced on the stream, not on Content-Length: a chunked response carries
    /// no Content-Length at all, so the parser must never see an oversized body.
    /// </summary>
    [Fact]
    public async Task An_oversized_response_without_a_content_length_is_refused()
    {
        // Not seekable, so StreamContent cannot advertise a length, exactly a chunked transfer.
        var content = new System.Net.Http.StreamContent(new EndlessZeroStream(9L * 1024 * 1024));
        using var client = new System.Net.Http.HttpClient(new FixedResponseHandler(content));

        var parsed = false;
        var result = await RegistryEnrichment.FetchAsync(
            client,
            "https://registry.example/oversized",
            _ =>
            {
                parsed = true;
                return new UpdateEnrichment(null, 1);
            },
            new LoggingService(),
            "npm",
            "oversized",
            CancellationToken.None);

        Assert.Null(result);
        Assert.False(parsed);
    }

    /// <summary>Yields <paramref name="length"/> bytes and reports no length of its own.</summary>
    private sealed class EndlessZeroStream(long length) : Stream
    {
        private long _sent;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var take = (int)Math.Min(count, length - _sent);
            if (take <= 0)
                return 0;

            Array.Clear(buffer, offset, take);
            _sent += take;
            return take;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FixedResponseHandler(System.Net.Http.HttpContent content) : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content });
    }

    private static UpdateItem TestItem(DateTimeOffset? published = null) => new()
    {
        Id = "x",
        SourceId = "winget",
        Category = UpdateCategory.Software,
        Name = "Example",
        InstalledVersion = "1.0",
        AvailableVersion = "1.1",
        PublishDate = published,
    };
}
