using System.Net.Http;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;
using Xunit;

namespace UltimateUpdater.Tests;

/// <summary>A killed listing is a timeout, the Java home must be new enough when one is, and Studio's
/// settings folders are ordered by release number.</summary>
public class AndroidSdkJavaAndConfigTests
{
    private const string Listing =
        "Installed packages:\n  Path | Version | Description\n  ------- | ------- | -------\n" +
        "  platforms;android-34 | 1 | Android SDK Platform 34\n";

    [Fact]
    public void A_timed_out_run_with_a_recognised_listing_is_a_timeout()
    {
        var source = new AndroidSdkSource(new LoggingService(), new HttpClient());
        var result = new ProcessResult(-1, Listing, string.Empty) { TimedOut = true };

        Assert.Throws<TimeoutException>(() => source.BuildScanResult(result));
    }

    [Fact]
    public void A_new_enough_studio_runtime_beats_an_old_JAVA_HOME()
    {
        var majors = new Dictionary<string, int?> { ["C:\\jdk8"] = 8, ["C:\\studio\\jbr"] = 21 };

        var chosen = AndroidSdkSource.ChooseJavaHome(["C:\\jdk8", "C:\\studio\\jbr"], home => majors[home]);

        Assert.Equal("C:\\studio\\jbr", chosen);
    }

    [Fact]
    public void An_old_only_candidate_is_still_used() =>
        Assert.Equal("C:\\jdk11", AndroidSdkSource.ChooseJavaHome(["C:\\jdk11"], _ => 11));

    [Fact]
    public void No_candidate_is_no_java() =>
        Assert.Null(AndroidSdkSource.ChooseJavaHome([], _ => 21));

    [Theory]
    [InlineData("JAVA_VERSION=\"1.8.0_392\"", 8)]
    [InlineData("JAVA_VERSION=\"21.0.3\"", 21)]
    [InlineData("JAVA_VERSION=\"17\"", 17)]
    public void The_major_version_is_read_from_the_release_file(string line, int expected)
    {
        var home = Directory.CreateTempSubdirectory().FullName;
        try
        {
            File.WriteAllText(Path.Combine(home, "release"), "IMPLEMENTOR=\"x\"\n" + line + "\n");
            Assert.Equal(expected, AndroidSdkSource.ReadJavaMajor(home));
        }
        finally
        {
            Directory.Delete(home, true);
        }
    }

    [Fact]
    public void A_home_without_a_release_file_has_no_major()
    {
        var home = Directory.CreateTempSubdirectory().FullName;
        try
        {
            Assert.Null(AndroidSdkSource.ReadJavaMajor(home));
        }
        finally
        {
            Directory.Delete(home, true);
        }
    }

    [Fact]
    public void Studio_configurations_are_ordered_by_release_number()
    {
        var ordered = AndroidSdkLocator.OrderStudioConfigurations([
            @"C:\g\AndroidStudio4.2",
            @"C:\g\AndroidStudioPreview2024.1",
            @"C:\g\AndroidStudio2024.1",
            @"C:\g\AndroidStudioOdd",
        ]);

        Assert.Equal(
            [@"C:\g\AndroidStudio2024.1", @"C:\g\AndroidStudioPreview2024.1", @"C:\g\AndroidStudio4.2", @"C:\g\AndroidStudioOdd"],
            ordered);
    }
}
