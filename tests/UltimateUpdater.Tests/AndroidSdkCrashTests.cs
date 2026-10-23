using System.Net.Http;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;
using Xunit;

namespace UltimateUpdater.Tests;

/// <summary>
/// WORK-IMPL.md item 3: sdkmanager.bat wraps a JVM, and a JVM that will not even start (a Java older than
/// the 17+ its cmdline-tools require) exits with a native crash code, not an ordinary sdkmanager error. A
/// bare "exited with code -1073740791" told the owner nothing; this must name the tool and hint at the
/// real cause instead.
/// </summary>
public class AndroidSdkCrashTests
{
    private static ProcessResult Result(int exitCode) => new(exitCode, string.Empty, string.Empty);

    [Fact]
    public void A_crash_exit_code_raises_the_tool_crashed_exception_not_the_generic_message()
    {
        var source = new AndroidSdkSource(new LoggingService(), new HttpClient());

        // STATUS_STACK_BUFFER_OVERRUN, the exact code seen on the owner's machine.
        var ex = Assert.Throws<ExternalToolCrashedException>(() => source.EnsureScanCompleted(Result(-1073740791)));

        Assert.Equal(-1073740791, ex.ExitCode);
        Assert.Contains("0xC0000409", ex.Message);
        Assert.DoesNotContain("-1073740791", ex.Message);
    }

    [Fact]
    public void An_ordinary_nonzero_exit_still_raises_the_generic_scan_failed_message()
    {
        var source = new AndroidSdkSource(new LoggingService(), new HttpClient());

        var ex = Assert.Throws<InvalidOperationException>(() => source.EnsureScanCompleted(Result(1)));

        Assert.Contains("exited with code 1", ex.Message);
    }

    [Fact]
    public void A_successful_result_still_passes_through_unchanged()
    {
        var source = new AndroidSdkSource(new LoggingService(), new HttpClient());
        var result = Result(0);

        Assert.Same(result, source.EnsureScanCompleted(result));
    }
}
