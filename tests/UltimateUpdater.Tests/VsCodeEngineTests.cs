using UltimateUpdater.Services.Sources;
using Xunit;

namespace UltimateUpdater.Tests;

/// <summary>
/// `code --install-extension --force` installs the newest version the installed VS Code can run, so the
/// version offered must be that one, or the row would return after every scan.
/// </summary>
public class VsCodeEngineTests
{
    private const string Json = """
        {
          "versions": [
            { "version": "3.0.0", "properties": [ { "key": "Microsoft.VisualStudio.Code.Engine", "value": "^1.99.0" } ] },
            { "version": "2.5.0", "properties": [ { "key": "Microsoft.VisualStudio.Code.Engine", "value": ">=1.96.0" } ] },
            { "version": "2.0.0", "properties": [ { "key": "Microsoft.VisualStudio.Code.Engine", "value": "^1.90.0" } ] }
          ]
        }
        """;

    [Fact]
    public void The_newest_version_needing_a_newer_engine_is_skipped()
    {
        var enrichment = PackageMetadataParsers.ParseVsMarketplaceExtension(Json, new Version(1, 95, 3));

        Assert.Equal("2.0.0", enrichment.AvailableVersion);
    }

    [Fact]
    public void A_newer_engine_takes_the_newest_version()
    {
        Assert.Equal("3.0.0", PackageMetadataParsers.ParseVsMarketplaceExtension(Json, new Version(1, 99, 0)).AvailableVersion);
        Assert.Equal("2.5.0", PackageMetadataParsers.ParseVsMarketplaceExtension(Json, new Version(1, 98, 2)).AvailableVersion);
    }

    [Fact]
    public void An_unknown_engine_keeps_the_old_behaviour()
    {
        Assert.Equal("3.0.0", PackageMetadataParsers.ParseVsMarketplaceExtension(Json).AvailableVersion);
    }

    [Fact]
    public void No_compatible_version_offers_none()
    {
        var enrichment = PackageMetadataParsers.ParseVsMarketplaceExtension(Json, new Version(1, 80, 0));

        Assert.Null(enrichment.AvailableVersion);
    }

    [Fact]
    public void A_version_without_an_engine_range_is_always_compatible()
    {
        const string json = """{ "versions": [ { "version": "1.2.3" } ] }""";

        Assert.Equal("1.2.3", PackageMetadataParsers.ParseVsMarketplaceExtension(json, new Version(1, 50, 0)).AvailableVersion);
    }

    [Theory]
    [InlineData("^1.95.0", "1.95.0", true)]
    [InlineData("^1.95.0", "1.94.9", false)]
    [InlineData(">=1.95.0", "1.100.0", true)]
    [InlineData(">=1.95.0", "1.9.0", false)]
    [InlineData("^1.95", "1.95.2", true)]
    [InlineData("1.95.x", "1.95.0", true)]
    [InlineData("*", "1.0.0", true)]
    [InlineData("garbage", "1.0.0", true)]
    [InlineData("^1.95.0-insider", "1.95.0", true)]
    public void Engine_ranges_compare_as_versions(string range, string installed, bool expected)
    {
        Assert.Equal(expected, PackageMetadataParsers.EngineRangeAccepts(range, Version.Parse(installed)));
    }

    [Theory]
    [InlineData("1.95.3\nabcdef0123\nx64\n", "1.95.3")]
    [InlineData("1.95.3", "1.95.3")]
    [InlineData("", null)]
    [InlineData("not a version\n", null)]
    public void The_first_line_of_code_version_is_the_engine(string output, string? expected)
    {
        var parsed = VsCodeExtensionSource.ParseCodeVersion(output);

        Assert.Equal(expected, parsed?.ToString());
    }
}
