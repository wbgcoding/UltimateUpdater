using System.Net.Http;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources.PackageManagers;
using Xunit;

namespace UltimateUpdater.Tests;

/// <summary>
/// A machine with the .NET runtime but no SDK has a `dotnet` that starts and then refuses every SDK
/// command. That is a tool that is not usable here, the same as a missing one: no row, no failed pill.
/// Any other non-zero exit stays a real failure.
/// </summary>
public class DotnetToolSdkMissingTests
{
    private static ProcessResult Result(int exitCode, string stdout = "", string stderr = "") => new(exitCode, stdout, stderr);

    [Theory]
    [InlineData(-2147450725)]
    [InlineData(-2147450735)]
    public void The_hostfxr_sdk_exit_codes_mean_no_sdk(int exitCode) =>
        Assert.True(DotnetToolSource.IsSdkMissing(Result(exitCode)));

    [Theory]
    [InlineData("It was not possible to find any installed .NET SDKs.")]
    [InlineData("No .NET SDKs were found.")]
    [InlineData("NO .NET SDKS WERE FOUND.")]
    public void The_sdk_not_found_text_means_no_sdk_whatever_the_exit_code(string text)
    {
        Assert.True(DotnetToolSource.IsSdkMissing(Result(1, string.Empty, text)));
        Assert.True(DotnetToolSource.IsSdkMissing(Result(1, text, string.Empty)));
    }

    [Fact]
    public void Another_failure_is_not_a_missing_sdk()
    {
        Assert.False(DotnetToolSource.IsSdkMissing(Result(1, string.Empty, "error: tool manifest is invalid")));
        Assert.False(DotnetToolSource.IsSdkMissing(Result(-1073740791)));
        Assert.False(DotnetToolSource.IsSdkMissing(Result(0, "Package Id  Version  Commands")));
    }

    [Fact]
    public async Task A_missing_sdk_scans_as_zero_rows_without_throwing()
    {
        var source = new DotnetToolSource(new LoggingService(), new HttpClient());

        var items = await source.ScanFromResultAsync(Result(-2147450725), CancellationToken.None);

        Assert.Empty(items);
    }

    [Fact]
    public async Task Another_nonzero_exit_is_still_a_failed_scan()
    {
        var source = new DotnetToolSource(new LoggingService(), new HttpClient());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            source.ScanFromResultAsync(Result(1, string.Empty, "something else broke"), CancellationToken.None));

        Assert.Contains("exited with code 1", ex.Message);
    }
}
