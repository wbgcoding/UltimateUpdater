using System.Net.Http;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources.PackageManagers;

namespace UltimateUpdater.Tests;

/// <summary>
/// pnpm reports every error with exit code 1, the same code it uses for "outdated packages found", so the
/// exit code alone says nothing: the refusal to run before `pnpm setup` is an empty machine, a real
/// outdated object is a result, anything else is a failed scan.
/// </summary>
public class PnpmScanResultTests
{
    private const string Outdated = """{"left-pad":{"current":"1.0.0","latest":"1.3.0","wanted":"1.0.0"}}""";

    private static PnpmSource Source() => new(new LoggingService(), new HttpClient());

    [Theory]
    [InlineData("ERR_PNPM_NO_GLOBAL_BIN_DIR")]
    [InlineData("ERR_PNPM_GLOBAL_BIN_DIR_NOT_IN_PATH")]
    public void The_global_bin_directory_refusal_is_zero_rows_from_stderr(string code)
    {
        var result = new ProcessResult(1, string.Empty, $" ERR_PNPM  {code}  Unable to find the global bin directory ({code})");

        Assert.Empty(Source().BuildScanResult(result));
    }

    [Theory]
    [InlineData("ERR_PNPM_NO_GLOBAL_BIN_DIR")]
    [InlineData("ERR_PNPM_GLOBAL_BIN_DIR_NOT_IN_PATH")]
    public void The_global_bin_directory_refusal_is_zero_rows_from_stdout(string code)
    {
        var result = new ProcessResult(1, $$$"""{"error":{"code":"{{{code}}}","message":"not set up"}}""", string.Empty);

        Assert.Empty(Source().BuildScanResult(result));
    }

    [Fact]
    public void Exit_one_with_an_outdated_object_is_a_result()
    {
        var items = Source().BuildScanResult(new ProcessResult(1, Outdated, string.Empty));

        var item = Assert.Single(items);
        Assert.Equal("left-pad", item.Id);
        Assert.Equal("1.3.0", item.AvailableVersion);
    }

    [Fact]
    public void Exit_zero_is_parsed()
    {
        Assert.Single(Source().BuildScanResult(new ProcessResult(0, Outdated, string.Empty)));
        Assert.Empty(Source().BuildScanResult(new ProcessResult(0, string.Empty, string.Empty)));
    }

    [Fact]
    public void Exit_one_with_an_error_object_is_a_failed_scan() =>
        Assert.Throws<InvalidOperationException>(() => Source().BuildScanResult(
            new ProcessResult(1, """{"error":{"code":"ERR_PNPM_X","message":"boom"}}""", string.Empty)));

    [Fact]
    public void Exit_one_with_plain_text_is_a_failed_scan() =>
        Assert.Throws<InvalidOperationException>(() => Source().BuildScanResult(
            new ProcessResult(1, string.Empty, "something went wrong")));

    [Fact]
    public void Another_exit_code_is_a_failed_scan() =>
        Assert.Throws<InvalidOperationException>(() => Source().BuildScanResult(new ProcessResult(2, Outdated, string.Empty)));

    [Fact]
    public void A_timeout_is_a_failed_scan() =>
        Assert.Throws<TimeoutException>(() => Source().BuildScanResult(new ProcessResult(-1, Outdated, string.Empty) { TimedOut = true }));
}
