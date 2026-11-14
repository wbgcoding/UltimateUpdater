using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources.PackageManagers;
using Xunit;

namespace UltimateUpdater.Tests;

/// <summary>
/// An npm cache whose index still names content files that are gone fails every npm call that reads the
/// entry, with ENOENT on a file inside `_cacache`. The text below is the shape npm 11 prints for exactly that
/// (reproduced by deleting the content files of a fresh cache). Only this shape triggers the repair.
/// </summary>
public class NpmBrokenCacheTests
{
    private const string MissingContent =
        @"C:\Users\someone\AppData\Local\npm-cache\_cacache\content-v2\sha512\fb\ca\2f552916ba7548a39fc87ddb7d";

    private static ProcessResult Failure(string path, string code = "ENOENT", string lead = "npm error") => new(
        1,
        "{\n  \"error\": {\n    \"code\": \"" + code + "\",\n    \"summary\": \"Invalid response body\"\n  }\n}\n",
        $"{lead} code {code}\n{lead} syscall stat\n{lead} path {path}\n{lead} errno {code}\n"
        + $"{lead} enoent Invalid response body while trying to fetch https://registry.npmjs.org/is-number: "
        + $"ENOENT: no such file or directory, stat '{path}'\n");

    [Fact]
    public void Enoent_on_a_content_file_of_the_cache_is_a_broken_cache() =>
        Assert.True(NpmSource.IsBrokenCache(Failure(MissingContent)));

    [Fact]
    public void The_older_npm_log_prefix_is_read_too() =>
        Assert.True(NpmSource.IsBrokenCache(Failure(MissingContent, lead: "npm ERR!")));

    [Fact]
    public void A_cache_moved_elsewhere_is_recognised_by_its_folder_name() =>
        Assert.True(NpmSource.IsBrokenCache(Failure(@"D:\caches\npm\_CACACHE\index-v5\ab\cd\entry")));

    [Theory]
    [InlineData(@"C:\Users\someone\AppData\Roaming\npm")]
    [InlineData(@"C:\Users\someone\project\package.json")]
    [InlineData(@"C:\Users\someone\AppData\Local\npm-cache\_cacache")]
    [InlineData(@"C:\Users\someone\AppData\Local\npm-cache\_cacache-old\content-v2\x")]
    public void Enoent_elsewhere_is_not_a_broken_cache(string path) =>
        Assert.False(NpmSource.IsBrokenCache(Failure(path)));

    [Fact]
    public void Another_error_code_inside_the_cache_is_not_a_broken_cache() =>
        Assert.False(NpmSource.IsBrokenCache(Failure(MissingContent, code: "EPERM")));

    [Fact]
    public void A_report_without_a_path_is_not_a_broken_cache() =>
        Assert.False(NpmSource.IsBrokenCache(new ProcessResult(1, "", "npm error code ENOENT\n")));
}
