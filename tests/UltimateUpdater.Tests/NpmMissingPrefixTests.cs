using System.Net.Http;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources.PackageManagers;
using Xunit;

namespace UltimateUpdater.Tests;

/// <summary>
/// A fresh Node install has no global prefix folder until the first global install, and `npm outdated -g`
/// then fails with ENOENT on that very folder. That is "nothing installed globally", not a broken npm. The
/// text below is the real shape npm prints. ENOENT for any other path stays a failure.
/// </summary>
public class NpmMissingPrefixTests
{
    private const int Enoent = -4058;
    private const string Prefix = @"C:\Users\someone\AppData\Roaming\npm";

    private static ProcessResult Failure(string path, string code = "ENOENT", string lead = "npm error") => new(
        Enoent,
        string.Empty,
        $"{lead} code {code}\n{lead} syscall lstat\n{lead} path {path}\n{lead} errno -4058\n"
        + $"{lead} enoent ENOENT: no such file or directory, lstat '{path}'\n");

    [Fact]
    public void Enoent_for_the_global_prefix_is_a_missing_prefix() =>
        Assert.True(NpmSource.IsMissingGlobalPrefix(Failure(Prefix), Prefix));

    [Fact]
    public void The_path_match_ignores_case_and_a_trailing_separator() =>
        Assert.True(NpmSource.IsMissingGlobalPrefix(Failure(Prefix.ToUpperInvariant() + @"\"), Prefix));

    [Fact]
    public void Enoent_for_the_node_modules_folder_of_the_prefix_is_a_missing_prefix() =>
        Assert.True(NpmSource.IsMissingGlobalPrefix(Failure(Prefix + @"\node_modules"), Prefix));

    [Fact]
    public void The_older_npm_log_prefix_is_read_too() =>
        Assert.True(NpmSource.IsMissingGlobalPrefix(Failure(Prefix, lead: "npm ERR!"), Prefix));

    [Theory]
    [InlineData(@"C:\Users\someone\project\package.json")]
    [InlineData(@"C:\Users\someone\AppData\Roaming\npm\node_modules\left-pad\package.json")]
    [InlineData(@"C:\Users\someone\AppData\Roaming\npm-other")]
    [InlineData(@"C:\Users\someone\AppData\Roaming")]
    public void Enoent_for_another_path_is_a_failure(string path) =>
        Assert.False(NpmSource.IsMissingGlobalPrefix(Failure(path), Prefix));

    [Fact]
    public void Another_error_code_for_the_prefix_is_a_failure() =>
        Assert.False(NpmSource.IsMissingGlobalPrefix(Failure(Prefix, code: "EACCES"), Prefix));

    [Fact]
    public void Without_a_known_prefix_nothing_is_a_missing_prefix() =>
        Assert.False(NpmSource.IsMissingGlobalPrefix(Failure(Prefix), null));

    [Fact]
    public void The_exit_code_alone_is_not_enough() =>
        Assert.False(NpmSource.IsMissingGlobalPrefix(new ProcessResult(Enoent, string.Empty, "something broke"), Prefix));

    [Fact]
    public void A_missing_prefix_scans_as_zero_rows_without_throwing()
    {
        var source = new NpmSource(new LoggingService(), new HttpClient());

        var items = source.ScanFromResult(Failure(Prefix), Prefix);

        Assert.Empty(items);
    }

    [Fact]
    public void Enoent_for_another_path_is_a_failed_scan()
    {
        var source = new NpmSource(new LoggingService(), new HttpClient());

        Assert.Throws<InvalidOperationException>(() =>
            source.ScanFromResult(Failure(@"C:\elsewhere\package.json"), Prefix));
    }

    [Fact]
    public void Outdated_json_with_exit_one_is_unchanged()
    {
        var source = new NpmSource(new LoggingService(), new HttpClient());
        var json = """{"left-pad":{"current":"1.0.0","wanted":"1.1.0","latest":"1.3.0","location":"x"}}""";

        var items = source.ScanFromResult(new ProcessResult(1, json, string.Empty), Prefix);

        var item = Assert.Single(items);
        Assert.Equal("left-pad", item.Id);
        Assert.Equal("1.3.0", item.AvailableVersion);
    }
}
