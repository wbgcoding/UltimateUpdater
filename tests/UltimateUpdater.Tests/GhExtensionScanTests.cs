using System.Net.Http;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;
using Xunit;

namespace UltimateUpdater.Tests;

/// <summary>gh exits 1 both for "no extensions installed" and for every real error; only the first is an empty list.</summary>
public class GhExtensionScanTests
{
    private static GhExtensionSource Source() => new(new LoggingService(), new HttpClient());

    [Fact]
    public void No_installed_extensions_is_zero_rows()
    {
        var items = Source().ScanFromResult(new ProcessResult(1, string.Empty, "no installed extensions found\n"));

        Assert.Empty(items);
    }

    [Fact]
    public void The_sentence_is_matched_ignoring_case()
    {
        var items = Source().ScanFromResult(new ProcessResult(1, string.Empty, "No Installed Extensions Found"));

        Assert.Empty(items);
    }

    [Fact]
    public void Exit_one_with_nothing_printed_is_zero_rows()
    {
        Assert.Empty(Source().ScanFromResult(new ProcessResult(1, string.Empty, string.Empty)));
    }

    [Fact]
    public void Another_exit_one_fails_with_the_first_stderr_line()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Source().ScanFromResult(new ProcessResult(1, string.Empty, "failed to read config\nmore detail\n")));

        Assert.Contains("failed to read config", ex.Message);
        Assert.DoesNotContain("more detail", ex.Message);
    }

    [Fact]
    public void Exit_one_with_stdout_content_fails()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Source().ScanFromResult(new ProcessResult(1, "cs\tgh-foo\tabc\t\n", string.Empty)));
    }

    [Fact]
    public void Exit_zero_is_parsed()
    {
        var items = Source().ScanFromResult(new ProcessResult(0, string.Empty, string.Empty));

        Assert.Empty(items);
    }
}
