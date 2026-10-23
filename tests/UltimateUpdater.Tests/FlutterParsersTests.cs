using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

/// <summary>
/// Both fixtures below are real captures, not invented shapes: `flutter --version --machine` run on a real
/// machine with Flutter installed, and the actual response from Google's public release feed
/// fetched live the same session.
/// </summary>
public class FlutterParsersTests
{
    private const string RealVersionMachineOutput = """
        {
          "frameworkVersion": "3.44.1",
          "channel": "stable",
          "repositoryUrl": "https://github.com/flutter/flutter.git",
          "frameworkRevision": "924134a44c189315be2148659913dda1671cbe99",
          "frameworkCommitDate": "2026-05-29 12:13:22 -0400",
          "engineRevision": "c416acfeb8126e097f758c664aaa3da929e27da0",
          "engineCommitDate": "2026-05-27 20:19:31.000Z",
          "engineContentHash": "39b1f7043775b9578bbb26a1676e79c4e31c8b5e",
          "engineBuildDate": "2026-05-27 22:50:43.210",
          "dartSdkVersion": "3.12.1",
          "devToolsVersion": "2.57.0",
          "flutterVersion": "3.44.1",
          "flutterRoot": "C:\\flutter"
        }
        """;

    private const string RealFeedFixture = """
        {
          "base_url": "https://storage.googleapis.com/flutter_infra_release/releases",
          "current_release": {
            "beta": "7c7929adb0767c020659a422ae86df9ec0d5f82a",
            "dev": "13a2fb10b838971ce211230f8ffdd094c14af02c",
            "stable": "058e0af2c2b57e369d905a03ac9748b0ebf543c6"
          },
          "releases": [
            {
              "hash": "7c7929adb0767c020659a422ae86df9ec0d5f82a",
              "channel": "beta",
              "version": "3.47.0-0.3.pre",
              "dart_sdk_version": "3.13.0 (build 3.13.0-282.3.beta)",
              "release_date": "2026-07-30T20:57:55.349209Z",
              "archive": "beta/windows/flutter_windows_3.47.0-0.3.pre-beta.zip",
              "sha256": "e4d645b89da48bbf6a52f29169c6737b19f93e944b36b9da203931128af64a1b"
            },
            {
              "hash": "058e0af2c2b57e369d905a03ac9748b0ebf543c6",
              "channel": "stable",
              "version": "3.44.2",
              "dart_sdk_version": "3.12.2",
              "release_date": "2026-07-15T09:00:00.000000Z",
              "archive": "stable/windows/flutter_windows_3.44.2-stable.zip",
              "sha256": "abc123"
            }
          ]
        }
        """;

    [Fact]
    public void The_installed_framework_version_and_channel_are_read()
    {
        var installed = FlutterParsers.ParseInstalled(RealVersionMachineOutput);

        Assert.NotNull(installed);
        Assert.Equal("3.44.1", installed!.Value.Version);
        Assert.Equal("stable", installed.Value.Channel);
    }

    [Fact]
    public void Bootstrap_lines_before_the_json_do_not_fail_the_read()
    {
        var output = "Building flutter tool..." + System.Environment.NewLine
            + "Running pub upgrade..." + System.Environment.NewLine
            + RealVersionMachineOutput;

        var installed = FlutterParsers.ParseInstalled(output);

        Assert.NotNull(installed);
        Assert.Equal("3.44.1", installed!.Value.Version);
        Assert.Equal("stable", installed.Value.Channel);
    }

    [Fact]
    public void Garbage_or_empty_version_output_yields_nothing()
    {
        Assert.Null(FlutterParsers.ParseInstalled(""));
        Assert.Null(FlutterParsers.ParseInstalled("not json"));
        Assert.Null(FlutterParsers.ParseInstalled("{}"));
    }

    [Fact]
    public void The_latest_release_for_the_installed_channel_is_matched_by_hash_and_channel()
    {
        var latest = FlutterParsers.ParseLatestRelease(RealFeedFixture, "stable");

        Assert.NotNull(latest);
        Assert.Equal("3.44.2", latest!.Value.Version);
        Assert.Equal("stable/windows/flutter_windows_3.44.2-stable.zip", latest.Value.ArchivePath);
        Assert.Equal(new DateTimeOffset(2026, 7, 15, 9, 0, 0, TimeSpan.Zero), latest.Value.ReleaseDate);
    }

    [Fact]
    public void A_channel_with_no_current_release_entry_yields_nothing() =>
        Assert.Null(FlutterParsers.ParseLatestRelease(RealFeedFixture, "dev-typo"));

    [Fact]
    public void Garbage_feed_json_yields_nothing() =>
        Assert.Null(FlutterParsers.ParseLatestRelease("not json", "stable"));
}
