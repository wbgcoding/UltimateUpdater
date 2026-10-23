using UltimateUpdater.Models;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

/// <summary>
/// The same package can be reachable through more than one source, a real case had graphify installed
/// through pip and through uv's tools at once. The table shows one row for that, names both sources on it
/// and lets the better-ranked one do the work. What counts as "the same" is deliberately strict, because
/// merging two genuinely different installations would hide one of them.
/// </summary>
public class DuplicateSourceTests
{
    private static UpdateItem Item(
        string id, string sourceId, string? installed = "1.0", string? available = "2.0",
        SourceTier tier = SourceTier.A) => new()
    {
        Id = id,
        SourceId = sourceId,
        Category = UpdateCategory.DeveloperTools,
        Name = id,
        InstalledVersion = installed,
        AvailableVersion = available,
        Tier = tier,
    };

    [Fact]
    public void The_same_package_from_two_sources_shares_one_key()
    {
        var pip = SourceRank.DuplicateKey(Item("graphify", "pip"));
        var uv = SourceRank.DuplicateKey(Item("graphify", "uv-tool"));

        Assert.NotNull(pip);
        Assert.Equal(pip, uv);
    }

    /// <summary>PyPI itself treats these three spellings as one name.</summary>
    [Theory]
    [InlineData("ast-grep-cli", "ast_grep_cli")]
    [InlineData("Ruamel.Yaml", "ruamel-yaml")]
    public void Registry_spelling_differences_do_not_split_a_package(string left, string right) =>
        Assert.Equal(SourceRank.DuplicateKey(Item(left, "pip")), SourceRank.DuplicateKey(Item(right, "uv-tool")));

    /// <summary>
    /// Two installations at different versions are two different pieces of work, chocolatey and scoop both
    /// ship `git`, and folding those together would leave one of them silently un-updated.
    /// </summary>
    [Fact]
    public void Different_installed_versions_stay_apart()
    {
        var a = SourceRank.DuplicateKey(Item("git", "chocolatey", installed: "2.43.0"));
        var b = SourceRank.DuplicateKey(Item("git", "scoop", installed: "2.44.0"));

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Different_available_versions_stay_apart()
    {
        var a = SourceRank.DuplicateKey(Item("git", "chocolatey", available: "2.45.0"));
        var b = SourceRank.DuplicateKey(Item("git", "scoop", available: "2.46.0"));

        Assert.NotEqual(a, b);
    }

    /// <summary>A row without versions cannot be compared, and an uncomparable row never merges.</summary>
    [Theory]
    [InlineData(null, "2.0")]
    [InlineData("1.0", null)]
    public void A_row_without_both_versions_has_no_key(string? installed, string? available) =>
        Assert.Null(SourceRank.DuplicateKey(Item("graphify", "pip", installed, available)));

    /// <summary>A Tier C row installs nothing, so there is nothing to choose between.</summary>
    [Fact]
    public void A_manual_row_has_no_key() =>
        Assert.Null(SourceRank.DuplicateKey(Item("steam", "steam", tier: SourceTier.C)));

    [Fact]
    public void Uv_tools_outranks_pip_and_winget_outranks_both()
    {
        Assert.True(SourceRank.Of("uv-tool") < SourceRank.Of("pip"));
        Assert.True(SourceRank.Of("winget") < SourceRank.Of("uv-tool"));
        Assert.True(SourceRank.Of("windowsupdate") < SourceRank.Of("winget"));
    }

    /// <summary>A self-updating tool is the last resort: a package manager that owns it does it better.</summary>
    [Fact]
    public void A_self_updating_tool_never_beats_a_package_manager()
    {
        Assert.True(SourceRank.Of("chocolatey") < SourceRank.Of("bun"));
        Assert.True(SourceRank.Of("scoop") < SourceRank.Of("deno"));
    }

    /// <summary>An unknown source sits in the middle: better than nothing, never ahead of a known one.</summary>
    [Fact]
    public void An_unknown_source_gets_a_middling_rank()
    {
        Assert.True(SourceRank.Of("something-new") > SourceRank.Of("winget"));
        Assert.True(SourceRank.Of("something-new") < SourceRank.Of(Item("steam", "steam", tier: SourceTier.C)));
    }
}
