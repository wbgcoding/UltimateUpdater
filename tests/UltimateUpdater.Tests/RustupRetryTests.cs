using System.Net.Http;
using System.Reflection;
using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources.PackageManagers;

namespace UltimateUpdater.Tests;

public class RustupRetryTests
{
    private const string MetadataFailure =
        "error: could not read metadata for file 'D:\\work\\rustup-init.exe': Access is denied. (os error 5)";

    [Theory]
    [InlineData(MetadataFailure, true)]
    [InlineData("error: Could Not Read Metadata For File 'rustup-init.exe'", true)]
    [InlineData("error: could not read metadata for file 'settings.toml'", false)]
    [InlineData("error: rustup-init.exe was blocked", false)]
    [InlineData("", false)]
    public void Only_the_failure_on_rustup_s_own_setup_file_is_recognized(string text, bool expected)
    {
        Assert.Equal(expected, RustupSource.IsInitMetadataFailure(text));
    }

    private static IReadOnlyList<string>? RetryFor(ProcessResult firstAttempt)
    {
        using var http = new HttpClient();
        var source = new RustupSource(new LoggingService(), http);
        var item = new UpdateItem { Id = "stable", SourceId = "rustup", Category = UpdateCategory.DeveloperTools, Name = "stable" };
        var method = typeof(RustupSource).GetMethod(
            "RetryInstallArguments", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (IReadOnlyList<string>?)method.Invoke(source, [item, firstAttempt]);
    }

    [Fact]
    public void The_metadata_failure_is_retried_once_with_the_same_command()
    {
        Assert.Equal(["update"], RetryFor(new ProcessResult(1, "", MetadataFailure)));
    }

    [Fact]
    public void Any_other_failure_is_reported_without_a_retry()
    {
        Assert.Null(RetryFor(new ProcessResult(1, "", "error: could not connect to server")));
    }
}
