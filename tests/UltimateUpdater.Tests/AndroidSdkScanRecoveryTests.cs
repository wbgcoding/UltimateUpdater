using System.Net.Http;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;
using Xunit;

namespace UltimateUpdater.Tests;

/// <summary>
/// sdkmanager.bat can print a complete, correct listing and only then abort with a native crash exit code
/// (measured -1073740791 / 0xC0000409 in the field). A parse that already found package rows must win
/// over that exit code; only a genuinely empty parse still falls back to the crash/failure classification
/// `EnsureScanCompleted` already provides for every other CLI source.
/// </summary>
public class AndroidSdkScanRecoveryTests
{
    private static readonly string RealListing =
        "Installed packages:\n" +
        "  Path                | Version | Description\n" +
        "  -------              | -------  | -------\n" +
        "  platforms;android-34 | 1        | Android SDK Platform 34\n" +
        "\n" +
        "Available Updates:\n" +
        "  ID                     | Installed | Available\n" +
        "  -------                | -------   | -------\n" +
        "  platforms;android-34   | 1         | 2\n" +
        "  build-tools;34.0.0     | 1         | 2\n";

    private static ProcessResult Result(string stdout, int exitCode) => new(exitCode, stdout, string.Empty);

    [Fact]
    public void A_crash_exit_code_with_a_parsed_listing_still_returns_the_rows()
    {
        var source = new AndroidSdkSource(new LoggingService(), new HttpClient());

        // STATUS_STACK_BUFFER_OVERRUN, the exact code seen on the owner's machine, alongside a
        // complete, real listing captured from that same run.
        var items = source.BuildScanResult(Result(RealListing, -1073740791));

        Assert.Equal(2, items.Count);
        Assert.Contains(items, item => item.Id == "platforms;android-34" && item.AvailableVersion == "2");
        Assert.Contains(items, item => item.Id == "build-tools;34.0.0" && item.AvailableVersion == "2");
    }

    [Fact]
    public void The_same_crash_exit_code_with_no_parseable_rows_still_raises_the_crash_exception()
    {
        var source = new AndroidSdkSource(new LoggingService(), new HttpClient());

        var ex = Assert.Throws<ExternalToolCrashedException>(() => source.BuildScanResult(Result(string.Empty, -1073740791)));

        Assert.Equal(-1073740791, ex.ExitCode);
    }

    [Fact]
    public void A_clean_exit_with_a_parsed_listing_returns_the_rows_as_before()
    {
        var source = new AndroidSdkSource(new LoggingService(), new HttpClient());

        var items = source.BuildScanResult(Result(RealListing, 0));

        Assert.Equal(2, items.Count);
    }
}
