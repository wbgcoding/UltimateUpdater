using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

/// <summary>
/// Both fixtures below are real captures: `volta 2.0.2` installed via winget on this dev box and run
/// live, its own docs show no example of `list --format plain` at all, and Node's real
/// public release index, fetched live the same session.
/// </summary>
public class VoltaParsersTests
{
    private const string RealListAllPlainOutput =
        "runtime node@18.19.0 (default)\n" +
        "runtime node@20.11.0\n" +
        "package-manager yarn@1.22.19 (default)\n";

    private const string RealNodeReleaseIndexFixture = """
        [
        {"version":"v26.5.1","date":"2026-07-28","lts":false,"security":true},
        {"version":"v26.5.0","date":"2026-07-08","lts":false,"security":false},
        {"version":"v24.10.0","date":"2026-06-01","lts":"Krypton","security":false}
        ]
        """;

    [Fact]
    public void Only_the_default_entry_per_tool_is_read()
    {
        var tools = VoltaParsers.ParseDefaultTools(RealListAllPlainOutput);

        Assert.Equal(2, tools.Count);
        Assert.Contains(tools, t => t is { Kind: "runtime", Name: "node", Version: "18.19.0" });
        Assert.Contains(tools, t => t is { Kind: "package-manager", Name: "yarn", Version: "1.22.19" });

        // node@20.11.0 has no "(default)" suffix, an intentional side-by-side install, never a row.
        Assert.DoesNotContain(tools, t => t.Version == "20.11.0");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not the expected format at all")]
    public void Garbage_or_empty_list_output_yields_nothing(string? stdout) =>
        Assert.Empty(VoltaParsers.ParseDefaultTools(stdout));

    [Fact]
    public void The_newest_entry_in_the_matching_major_line_is_the_latest_version()
    {
        // Node's index is newest-first, the first entry whose major matches, not the global first entry.
        Assert.Equal("24.10.0", VoltaParsers.ParseLatestNodeVersion(RealNodeReleaseIndexFixture, "24"));
    }

    [Fact]
    public void A_newer_major_line_is_never_offered()
    {
        // Matching only by major matters: offering the newest release overall would silently jump a
        // "node@20 (default)" row to whatever major Node currently ships, no line here is major 22.
        Assert.Null(VoltaParsers.ParseLatestNodeVersion(RealNodeReleaseIndexFixture, "22"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    public void Garbage_release_index_yields_nothing(string? json) =>
        Assert.Null(VoltaParsers.ParseLatestNodeVersion(json, "20"));
}
