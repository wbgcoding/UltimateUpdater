using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

/// <summary>
/// <see cref="SourceRegistry.KnownIds"/> exists so settings loading can drop a stale source id without
/// paying for <see cref="SourceRegistry.CreateAll"/>'s construction cost (a shared HTTP client, a registry
/// view, a launcher catalogue walk) inside the elevated worker process too. Pinning it here means the two
/// lists can never silently drift apart: adding a source without adding its id to KnownIds would make its
/// own settings key look "unknown" and get pruned on the very next load.
/// </summary>
public class SourceRegistryKnownIdsTests
{
    [Fact]
    public void KnownIds_matches_every_id_CreateAll_actually_produces()
    {
        var actual = SourceRegistry.CreateAll(new LoggingService()).Select(s => s.Id).ToHashSet();

        Assert.Equal(actual, SourceRegistry.KnownIds.ToHashSet());
    }

    [Fact]
    public void KnownIds_has_no_duplicate_and_no_blank_entry()
    {
        Assert.All(SourceRegistry.KnownIds, id => Assert.False(string.IsNullOrWhiteSpace(id)));
        Assert.Equal(SourceRegistry.KnownIds.Count, SourceRegistry.KnownIds.Distinct().Count());
    }

    /// <summary>
    /// The README promises "more than N sources". The promise must stay true: removing sources until the
    /// registry no longer exceeds N fails here, and so does rewording the line so the claim can't be found.
    /// </summary>
    [Fact]
    public void Readme_source_count_claim_is_covered_by_the_registry()
    {
        var readme = File.ReadAllText(Path.Combine(RepoRoot(), "README.md"));
        var match = System.Text.RegularExpressions.Regex.Match(
            readme, @"more than (\d+) sources", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        Assert.True(match.Success, "README.md no longer states 'more than N sources'");
        var claimed = int.Parse(match.Groups[1].Value);
        Assert.True(SourceRegistry.KnownIds.Count > claimed,
            $"README claims more than {claimed} sources but the registry has {SourceRegistry.KnownIds.Count}");
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "UltimateUpdater.slnx")))
            directory = directory.Parent;

        Assert.True(directory is not null, "the repository root is not above the test binaries any more");
        return directory!.FullName;
    }

    /// <summary>
    /// The explicit local-only table: matched by id, never by tier or by guessing. A source not in the
    /// table defaults to needing network, the safe direction.
    /// </summary>
    [Theory]
    [InlineData("wsl")]
    [InlineData("android-studio")]
    [InlineData("msstore")]
    [InlineData("epic")]
    [InlineData("gog")]
    public void NeedsNetwork_is_false_for_every_explicit_local_only_source(string sourceId)
    {
        Assert.False(SourceRegistry.NeedsNetwork(sourceId));
    }

    [Theory]
    [InlineData("winget")]
    [InlineData("windowsupdate")]
    [InlineData("pip")]
    [InlineData("steam")]
    [InlineData("some-unknown-future-source")]
    public void NeedsNetwork_defaults_to_true_for_everything_else(string sourceId)
    {
        Assert.True(SourceRegistry.NeedsNetwork(sourceId));
    }

    [Fact]
    public void Every_launcher_catalog_entry_is_local_only()
    {
        foreach (var launcher in LauncherCatalog.Known())
            Assert.False(SourceRegistry.NeedsNetwork(launcher.Id));
    }
}
