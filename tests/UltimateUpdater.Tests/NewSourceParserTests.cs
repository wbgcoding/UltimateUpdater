using System.Net.Http;
using System.Reflection;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;
using UltimateUpdater.Services.Sources.PackageManagers;
using Xunit;

namespace UltimateUpdater.Tests;

/// <summary>
/// Parsers for the sources added in the 2026-07-24 round (RubyGems, Composer, rustup) and the
/// 2026-07-25 round (Android SDK, GitHub CLI extensions, pipx, VS Code extensions).
/// WSL has no parser, its source deliberately never parses anything (see WslSource).
/// </summary>
public class NewSourceParserTests
{
    /// <summary>
    /// the packument URL was built with Replace("/", "%2f"), which encodes exactly one character
    /// and pastes everything else in raw. Escaping is now the framework's job.
    /// </summary>
    [Theory]
    [InlineData("@babel/core", "https://registry.npmjs.org/%40babel%2Fcore")]
    [InlineData("typescript", "https://registry.npmjs.org/typescript")]
    [InlineData("weird name?x=1", "https://registry.npmjs.org/weird%20name%3Fx%3D1")]
    public void Npm_packument_urls_are_fully_escaped(string packageName, string expected) =>
        Assert.Equal(expected, NpmEnrichment.PackumentUrl(packageName));

    [Fact]
    public void Gem_outdated_lines_parse_into_name_and_versions()
    {
        const string output = """
            rails (7.0.4 < 7.1.0)
            nokogiri (1.14.0 < 1.15.2)
            """;

        var packages = PackageManagerParsers.ParseGemOutdated(output);

        Assert.Collection(packages,
            p => { Assert.Equal("rails", p.Id); Assert.Equal("7.0.4", p.Installed); Assert.Equal("7.1.0", p.Available); },
            p => { Assert.Equal("nokogiri", p.Id); Assert.Equal("1.15.2", p.Available); });
    }

    [Fact]
    public void Gem_noise_lines_are_skipped()
    {
        Assert.Empty(PackageManagerParsers.ParseGemOutdated("Updating rubygems-update\nFetching: ...\n"));
    }

    [Fact]
    public void Composer_json_returns_only_the_outdated_packages()
    {
        const string json = """
            {"installed":[
              {"name":"phpunit/phpunit","version":"9.5.0","latest":"10.0.0","latest-status":"update-possible"},
              {"name":"psy/psysh","version":"0.11.0","latest":"0.11.0","latest-status":"up-to-date"}
            ]}
            """;

        var packages = PackageManagerParsers.ParseComposerJson(json);

        var only = Assert.Single(packages);
        Assert.Equal("phpunit/phpunit", only.Id);
        Assert.Equal("9.5.0", only.Installed);
        Assert.Equal("10.0.0", only.Available);
        Assert.True(only.BeyondConstraint);
    }

    /// <summary>
    /// Only a direct dependency's new major moves its constraint; a semver-safe update or a transitive
    /// package keeps the plain `composer update`.
    /// </summary>
    [Fact]
    public void Composer_json_marks_only_direct_updates_beyond_the_constraint()
    {
        const string json = """
            {"installed":[
              {"name":"a/safe","direct-dependency":true,"version":"1.0.0","latest":"1.2.0","latest-status":"semver-safe-update"},
              {"name":"b/major","direct-dependency":true,"version":"1.0.0","latest":"2.0.0","latest-status":"update-possible"},
              {"name":"c/transitive","direct-dependency":false,"version":"1.0.0","latest":"2.0.0","latest-status":"update-possible"}
            ]}
            """;

        var packages = PackageManagerParsers.ParseComposerJson(json);

        Assert.Equal([false, true, false], packages.Select(p => p.BeyondConstraint));
    }

    [Fact]
    public void Composer_garbage_yields_nothing()
    {
        Assert.Empty(PackageManagerParsers.ParseComposerJson("Do not run Composer as root\n"));
    }

    [Fact]
    public void Rustup_check_reads_only_the_update_available_lines()
    {
        const string output = """
            stable-x86_64-pc-windows-msvc - Update available : 1.75.0 -> 1.76.0
            nightly-x86_64-pc-windows-msvc - Up to date : 1.78.0-nightly
            rustup - Update available : 1.26.0 -> 1.27.0
            """;

        var packages = PackageManagerParsers.ParseRustupCheck(output);

        Assert.Collection(packages,
            p =>
            {
                Assert.Equal("stable-x86_64-pc-windows-msvc", p.Id);
                Assert.Equal("1.75.0", p.Installed);
                Assert.Equal("1.76.0", p.Available);
            },
            p =>
            {
                Assert.Equal("rustup", p.Id);
                Assert.Equal("1.27.0", p.Available);
            });
    }

    /// <summary>Literal `rustup check` transcript measured 22.08. on the host, real commit hash + date suffix.</summary>
    [Fact]
    public void Rustup_check_strips_commit_hash_and_date_from_real_output()
    {
        const string output = """
            stable-x86_64-pc-windows-msvc - update available: 1.97.1 (8bab26f4f 2026-07-14) -> 1.98.0 (88d9e12ae 2026-08-18)
            rustup - up to date : 1.29.0
            """;

        var packages = PackageManagerParsers.ParseRustupCheck(output);

        var only = Assert.Single(packages);
        Assert.Equal("stable-x86_64-pc-windows-msvc", only.Id);
        Assert.Equal("1.97.1", only.Installed);
        Assert.Equal("1.98.0", only.Available);
    }

    /// <summary>`rustup check` exits 100 whenever any toolchain has an update, measured 22.08., not a failure.</summary>
    [Fact]
    public void RustupSource_accepts_exit_code_100_but_not_1()
    {
        var source = new RustupSource(new LoggingService(), new HttpClient());
        var method = typeof(RustupSource).GetMethod("IsAcceptedScanExitCode", BindingFlags.NonPublic | BindingFlags.Instance)!;

        Assert.True((bool)method.Invoke(source, [0])!);
        Assert.True((bool)method.Invoke(source, [100])!);
        Assert.False((bool)method.Invoke(source, [1])!);
    }

    /// <summary>Real transcript from Microsoft's own vcpkg command reference (learn.microsoft.com), not invented.</summary>
    [Fact]
    public void Vcpkg_update_reads_port_triplet_and_both_versions()
    {
        const string output = """
            Using local portfile versions. To update the local portfiles, use `git pull`.
            The following packages differ from their port versions:
                    corrade:x64-windows              2020.06#4 -> 2020.06#5
                    magnum:x64-windows                2020.06#11 -> 2020.06#12
                    openal-soft:x64-windows           1.22.2#5 -> 1.23.0
            To update these packages and all dependencies, run
            .\vcpkg upgrade
            """;

        var packages = PackageManagerParsers.ParseVcpkgUpdate(output);

        Assert.Collection(packages,
            p =>
            {
                Assert.Equal("corrade:x64-windows", p.Id);
                Assert.Equal("corrade", p.Name);
                Assert.Equal("2020.06#4", p.Installed);
                Assert.Equal("2020.06#5", p.Available);
            },
            p => Assert.Equal("magnum:x64-windows", p.Id),
            p =>
            {
                Assert.Equal("openal-soft:x64-windows", p.Id);
                Assert.Equal("1.22.2#5", p.Installed);
                Assert.Equal("1.23.0", p.Available);
            });
    }

    [Fact]
    public void Vcpkg_update_with_nothing_outdated_yields_no_rows() =>
        Assert.Empty(PackageManagerParsers.ParseVcpkgUpdate("All installed packages are up-to-date with the local portfile versions."));

    [Fact]
    public void SdkManagerList_reads_rows_after_the_available_updates_header()
    {
        const string output = """
            Installed packages:
              build-tools;34.0.0 | 34.0.0

            Available Updates:
              ID                     | Installed | Available
              -------                | --------- | ---------
              build-tools;34.0.0     | 34.0.0    | 34.0.1
              platform-tools         | 34.0.4    | 35.0.0
            """;

        var packages = PackageManagerParsers.ParseSdkManagerList(output);

        Assert.Collection(packages,
            p =>
            {
                Assert.Equal("build-tools;34.0.0", p.Id);
                Assert.Equal("34.0.0", p.Installed);
                Assert.Equal("34.0.1", p.Available);
            },
            p =>
            {
                Assert.Equal("platform-tools", p.Id);
                Assert.Equal("34.0.4", p.Installed);
                Assert.Equal("35.0.0", p.Available);
            });
    }

    [Theory]
    [InlineData("")]
    [InlineData("Installed packages:\n  build-tools;34.0.0 | 34.0.0\n")]
    public void SdkManagerList_without_the_updates_header_yields_nothing(string input) =>
        Assert.Empty(PackageManagerParsers.ParseSdkManagerList(input));

    /// <summary>The newer sdkmanager layout has no "Available Updates:" table at all, two whitespace-
    /// column sections instead; a genuinely newer available version is still reported.</summary>
    [Fact]
    public void SdkManagerList_new_layout_reports_a_genuinely_newer_available_version()
    {
        const string output = """
            Installed packages:
              platforms/android-33      2.0.0      Android SDK Platform 33

            Available packages:
              platforms/android-33      3.0.0 (+1)      Android SDK Platform 33
            """;

        var packages = PackageManagerParsers.ParseSdkManagerList(output, out var recognized);

        Assert.True(recognized);
        var package = Assert.Single(packages);
        Assert.Equal("platforms/android-33", package.Id);
        Assert.Equal("2.0.0", package.Installed);
        Assert.Equal("3.0.0", package.Available);
    }

    /// <summary>A "(+n)" marker means further versions exist, not that the shown one is newer: when the
    /// shown available version is actually OLDER than what is installed, that is honestly zero updates,
    /// not an error, matching what this fix measured on the owner's own machine.</summary>
    [Fact]
    public void SdkManagerList_new_layout_a_plus_marker_over_an_older_version_is_not_an_update()
    {
        const string output = """
            Installed packages:
              platforms/android-34      3.0.0      Android SDK Platform 34

            Available packages:
              platforms/android-34      2.5.0 (+1)      Android SDK Platform 34
            """;

        var packages = PackageManagerParsers.ParseSdkManagerList(output, out var recognized);

        Assert.True(recognized);
        Assert.Empty(packages);
    }

    /// <summary>Output that matched neither known layout stays unrecognized, so a caller still treats an
    /// empty parse as a possible failure rather than an honest "nothing to report".</summary>
    [Fact]
    public void SdkManagerList_garbage_output_is_not_recognized()
    {
        var packages = PackageManagerParsers.ParseSdkManagerList("not sdkmanager output at all\n", out var recognized);

        Assert.False(recognized);
        Assert.Empty(packages);
    }

    [Fact]
    public void GhExtensionList_returns_only_rows_marked_with_an_update()
    {
        const string output = "gh copilot\tgithub/gh-copilot\tv1.0.5\n*\tgh dash\tdlvhdr/gh-dash\tv4.2.0\n";

        var packages = PackageManagerParsers.ParseGhExtensionList(output);

        var only = Assert.Single(packages);
        Assert.Equal("dlvhdr/gh-dash", only.Id);
        Assert.Equal("v4.2.0", only.Installed);
        Assert.Null(only.Available);
    }

    [Theory]
    [InlineData("")]
    [InlineData("gh copilot\tgithub/gh-copilot\tv1.0.5\n")]
    [InlineData("*\tonlyonefield\n")]
    public void GhExtensionList_never_throws_on_bad_input(string input) =>
        Assert.Empty(PackageManagerParsers.ParseGhExtensionList(input));

    [Fact]
    public void PipxJson_reads_installed_packages_with_no_available_version()
    {
        const string json = """
            {"venvs": {"black": {"metadata": {"main_package": {"package": "black", "package_version": "24.1.0"}}}}}
            """;

        var packages = PackageManagerParsers.ParsePipxJson(json);

        var only = Assert.Single(packages);
        Assert.Equal("black", only.Id);
        Assert.Equal("24.1.0", only.Installed);
        Assert.Null(only.Available);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"venvs\": {}}")]
    public void PipxJson_never_throws_on_bad_input(string input) =>
        Assert.Empty(PackageManagerParsers.ParsePipxJson(input));

    [Fact]
    public void VsCodeExtensions_split_on_the_last_at_sign()
    {
        const string output = "ms-python.python@2024.2.0\nesbenp.prettier-vscode@10.1.0\n";

        var packages = PackageManagerParsers.ParseVsCodeExtensions(output);

        Assert.Collection(packages,
            p => { Assert.Equal("ms-python.python", p.Id); Assert.Equal("2024.2.0", p.Installed); Assert.Null(p.Available); },
            p => { Assert.Equal("esbenp.prettier-vscode", p.Id); Assert.Equal("10.1.0", p.Installed); });
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-at-sign-here\n")]
    [InlineData("@1.2.3\n")]
    public void VsCodeExtensions_never_throws_on_bad_input(string input) =>
        Assert.Empty(PackageManagerParsers.ParseVsCodeExtensions(input));

    // --- 2026-07-25 enrichment round: pipx (PyPI, version-less), VS Code extensions (Marketplace),
    // gh extensions (GitHub releases), see PipxSource/VsCodeExtensionSource/GhExtensionSource.

    [Fact]
    public void PyPiLatest_reads_version_date_size_and_project_url()
    {
        const string json = """
            {
              "info": {
                "version": "24.1.0",
                "project_urls": { "Homepage": "https://black.example/", "Changelog": "https://black.example/changes" }
              },
              "releases": {
                "24.1.0": [
                  { "packagetype": "sdist", "size": 1000, "upload_time_iso_8601": "2024-01-10T10:00:00Z" },
                  { "packagetype": "bdist_wheel", "filename": "black-24.1.0-py3-none-any.whl", "size": 5000, "upload_time_iso_8601": "2024-01-10T12:00:00Z" }
                ]
              }
            }
            """;

        var enrichment = PackageMetadataParsers.ParsePyPiLatest(json);

        Assert.Equal("24.1.0", enrichment.AvailableVersion);
        Assert.Equal(new DateTimeOffset(2024, 1, 10, 12, 0, 0, TimeSpan.Zero), enrichment.PublishDate);
        Assert.Equal(5000, enrichment.DownloadSize);
        Assert.Equal("https://black.example/changes", enrichment.ProjectUrl);
    }

    [Fact]
    public void PyPiLatest_falls_back_to_sdist_size_when_no_wheel()
    {
        const string json = """
            {
              "info": { "version": "1.0.0" },
              "releases": { "1.0.0": [ { "packagetype": "sdist", "size": 2000 } ] }
            }
            """;

        var enrichment = PackageMetadataParsers.ParsePyPiLatest(json);

        Assert.Equal("1.0.0", enrichment.AvailableVersion);
        Assert.Equal(2000, enrichment.DownloadSize);
    }

    private static string Wheel(string tag, long size) =>
        $$"""{ "packagetype": "bdist_wheel", "filename": "pkg-1.0-cp312-cp312-{{tag}}.whl", "size": {{size}} }""";

    [Fact]
    public void PyPi_size_comes_from_the_wheel_for_this_platform_not_the_largest_file()
    {
        var wanted = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture
            == System.Runtime.InteropServices.Architecture.Arm64 ? "win_arm64" : "win_amd64";
        var files = string.Join(",", Wheel("manylinux_2_17_x86_64.manylinux2014_x86_64", 9000), Wheel("macosx_11_0_arm64", 8000),
            Wheel(wanted, 3000), Wheel("any", 2000), """{ "packagetype": "sdist", "filename": "pkg-1.0.tar.gz", "size": 7000 }""");

        var latest = PackageMetadataParsers.ParsePyPiLatest($$"""{ "info": { "version": "1.0" }, "releases": { "1.0": [{{files}}] } }""");
        var release = PackageMetadataParsers.ParsePyPiRelease($$"""{ "info": { "version": "1.0" }, "urls": [{{files}}] }""");

        Assert.Equal(3000, latest.DownloadSize);
        Assert.Equal(3000, release.DownloadSize);
    }

    [Fact]
    public void PyPi_size_falls_back_to_a_universal_wheel_then_the_sdist_then_nothing()
    {
        var universal = string.Join(",", Wheel("manylinux_2_17_x86_64", 9000), Wheel("any", 2000),
            """{ "packagetype": "sdist", "filename": "pkg-1.0.tar.gz", "size": 7000 }""");
        var sdistOnly = string.Join(",", Wheel("manylinux_2_17_x86_64", 9000),
            """{ "packagetype": "sdist", "filename": "pkg-1.0.tar.gz", "size": 7000 }""");
        var foreignOnly = Wheel("macosx_11_0_arm64", 8000);

        Assert.Equal(2000, PackageMetadataParsers.ParsePyPiRelease($$"""{ "info": {}, "urls": [{{universal}}] }""").DownloadSize);
        Assert.Equal(7000, PackageMetadataParsers.ParsePyPiRelease($$"""{ "info": {}, "urls": [{{sdistOnly}}] }""").DownloadSize);
        Assert.Null(PackageMetadataParsers.ParsePyPiRelease($$"""{ "info": {}, "urls": [{{foreignOnly}}] }""").DownloadSize);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"info\": {}}")]
    public void PyPiLatest_never_throws_on_bad_input(string input)
    {
        var enrichment = PackageMetadataParsers.ParsePyPiLatest(input);

        Assert.Null(enrichment.AvailableVersion);
        Assert.Null(enrichment.PublishDate);
        Assert.Null(enrichment.DownloadSize);
        Assert.Null(enrichment.ProjectUrl);
    }

    [Fact]
    public void VsMarketplaceExtension_reads_version_date_size_and_project_url()
    {
        const string json = """
            {
              "versions": [
                {
                  "version": "10.2.0",
                  "lastUpdated": "2024-03-05T08:00:00Z",
                  "files": [ { "assetType": "Microsoft.VisualStudio.Services.VSIXPackage", "size": 654321 } ],
                  "properties": [ { "key": "Microsoft.VisualStudio.Services.Links.Source", "value": "https://github.com/example/ext" } ]
                }
              ]
            }
            """;

        var enrichment = PackageMetadataParsers.ParseVsMarketplaceExtension(json);

        Assert.Equal("10.2.0", enrichment.AvailableVersion);
        Assert.Equal(new DateTimeOffset(2024, 3, 5, 8, 0, 0, TimeSpan.Zero), enrichment.PublishDate);
        Assert.Equal(654321, enrichment.DownloadSize);
        Assert.Equal("https://github.com/example/ext", enrichment.ProjectUrl);
    }

    [Fact]
    public void VsMarketplaceExtension_version_only_is_not_discarded()
    {
        const string json = """{ "versions": [ { "version": "1.0.0" } ] }""";

        var enrichment = PackageMetadataParsers.ParseVsMarketplaceExtension(json);

        Assert.Equal("1.0.0", enrichment.AvailableVersion);
        Assert.Null(enrichment.PublishDate);
        Assert.Null(enrichment.DownloadSize);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"versions\": []}")]
    [InlineData("{\"versions\": [ \"not-an-object\" ]}")]
    public void VsMarketplaceExtension_never_throws_on_bad_input(string input)
    {
        var enrichment = PackageMetadataParsers.ParseVsMarketplaceExtension(input);

        Assert.Null(enrichment.AvailableVersion);
        Assert.Null(enrichment.PublishDate);
        Assert.Null(enrichment.DownloadSize);
        Assert.Null(enrichment.ProjectUrl);
    }

    [Fact]
    public void GitHubLatestRelease_reads_version_date_size_and_project_url()
    {
        const string json = """
            {
              "tag_name": "v4.2.0",
              "published_at": "2024-05-01T00:00:00Z",
              "html_url": "https://github.com/dlvhdr/gh-dash/releases/tag/v4.2.0",
              "assets": [
                { "name": "gh-dash_linux.tar.gz", "browser_download_url": "https://example.invalid/linux.tar.gz", "size": 1000 },
                { "name": "gh-dash_windows.zip", "browser_download_url": "https://example.invalid/windows.zip", "size": 3000 }
              ]
            }
            """;

        var enrichment = PackageMetadataParsers.ParseGitHubLatestRelease(json);

        Assert.Equal("4.2.0", enrichment.AvailableVersion);
        Assert.Equal(new DateTimeOffset(2024, 5, 1, 0, 0, 0, TimeSpan.Zero), enrichment.PublishDate);
        Assert.Equal(3000, enrichment.DownloadSize);
        Assert.Equal("https://github.com/dlvhdr/gh-dash/releases/tag/v4.2.0", enrichment.ProjectUrl);
    }

    [Fact]
    public void GitHubLatestRelease_keeps_tag_without_a_leading_v_as_is()
    {
        const string json = """{ "tag_name": "release-2.0" }""";

        Assert.Equal("release-2.0", PackageMetadataParsers.ParseGitHubLatestRelease(json).AvailableVersion);
    }

    [Fact]
    public void GitHubLatestRelease_with_no_assets_leaves_size_null()
    {
        const string json = """{ "tag_name": "v1.0.0" }""";

        Assert.Null(PackageMetadataParsers.ParseGitHubLatestRelease(json).DownloadSize);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"assets\": \"not-an-array\"}")]
    public void GitHubLatestRelease_never_throws_on_bad_input(string input)
    {
        var enrichment = PackageMetadataParsers.ParseGitHubLatestRelease(input);

        Assert.Null(enrichment.AvailableVersion);
        Assert.Null(enrichment.PublishDate);
        Assert.Null(enrichment.DownloadSize);
        Assert.Null(enrichment.ProjectUrl);
    }

    /// <summary>
    /// Captured verbatim from `uv tool list` on the dev host: a tool line, then one indented line per
    /// executable it installs. Only the tool lines are packages.
    /// </summary>
    [Fact]
    public void Uv_tool_list_reads_the_tool_lines_only()
    {
        const string Output = """
            graphifyy v0.9.26
            - graphify
            - graphify-mcp
            ruff v0.15.20
            - ruff
            """;

        var packages = PackageManagerParsers.ParseUvToolList(Output);

        Assert.Equal(2, packages.Count);
        Assert.Equal("graphifyy", packages[0].Id);
        Assert.Equal("0.9.26", packages[0].Installed);
        Assert.Equal("ruff", packages[1].Id);
        Assert.Equal("0.15.20", packages[1].Installed);
        Assert.All(packages, p => Assert.Null(p.Available));
    }

    [Fact]
    public void Uv_tool_list_skips_anything_that_is_not_a_tool_line()
    {
        Assert.Empty(PackageManagerParsers.ParseUvToolList("No tools installed"));
        Assert.Empty(PackageManagerParsers.ParseUvToolList(string.Empty));
        Assert.Empty(PackageManagerParsers.ParseUvToolList("- graphify"));
    }

    /// <summary>
    /// A release TAG is not a version. Bun tags "bun-v1.3.14", and comparing that against an installed
    /// "1.3.14" parsed as neither, so the table offered an update to the version already installed. Found
    /// by the source probe.
    /// </summary>
    [Theory]
    [InlineData("bun-v1.3.14", "1.3.14")]
    [InlineData("v2.9.4", "2.9.4")]
    [InlineData("deno 2.9.4 (stable, release, x86_64-pc-windows-msvc)", "2.9.4")]
    [InlineData("uv 0.9.7 (abc1234 2026-01-01)", "0.9.7")]
    [InlineData("1.3.14", "1.3.14")]
    [InlineData("0.15.20-beta.2", "0.15.20-beta.2")]
    public void A_version_token_is_found_wherever_it_sits(string text, string expected)
    {
        Assert.Equal(expected, PackageManagerParsers.FirstVersionToken(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("nightly")]
    [InlineData("v")]
    public void Text_with_no_version_gives_none(string? text)
    {
        Assert.Null(PackageManagerParsers.FirstVersionToken(text));
    }

    /// <summary>
    /// Captured verbatim from `gcloud components list --format=json` on a clean VM, trimmed to the fields the
    /// parser reads. Only INSTALLED components that are actually behind become rows: a null
    /// current_version_string is an offer to add a component, not an update.
    /// </summary>
    [Fact]
    public void Gcloud_components_lists_only_installed_components_that_are_behind()
    {
        const string Json = """
            Your current Google Cloud CLI version is: 577.0.0
            [
              { "id": "app-engine-go", "name": "App Engine Go Extensions",
                "current_version_string": null, "latest_version_string": "1.9.78",
                "size": 5691463, "state": { "name": "Not Installed" } },
              { "id": "bq", "name": "BQ Command Line Tool",
                "current_version_string": "2.1.35", "latest_version_string": "2.1.35",
                "size": 1973488, "state": { "name": "Installed" } },
              { "id": "gsutil", "name": "Cloud Storage Command Line Tool",
                "current_version_string": "5.36", "latest_version_string": "5.37",
                "size": 12967034, "state": { "name": "Update Available" } }
            ]
            """;

        var components = PackageManagerParsers.ParseGcloudComponents(Json);

        var only = Assert.Single(components);
        Assert.Equal("gsutil", only.Id);
        Assert.Equal("Cloud Storage Command Line Tool", only.Name);
        Assert.Equal("5.36", only.Installed);
        Assert.Equal("5.37", only.Available);
        Assert.Equal(12967034, only.Size);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("[{\"id\":\"x\"}]")]
    public void Malformed_gcloud_output_yields_nothing(string output)
    {
        Assert.Empty(PackageManagerParsers.ParseGcloudComponents(output));
    }
}
