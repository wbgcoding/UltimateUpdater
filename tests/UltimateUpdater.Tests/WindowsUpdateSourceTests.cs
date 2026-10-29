using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

/// <summary>
/// A feature upgrade must be recognised by its category GUID, stable across every language, never by
/// matching an English title string like "Feature update", which never matches on a non-English Windows
/// install and would let a feature upgrade through undetected on exactly the machines that most need it
/// caught. When the category cannot be read at all, that must be treated as "unknown", not "safe to offer".
/// </summary>
public class WindowsUpdateSourceTests
{
    private const string UpgradesCategoryId = "3689bdc8-b205-4af4-8d4a-a63924c5e9d5";

    [Fact]
    public void A_known_feature_upgrade_category_is_always_excluded() =>
        Assert.True(WindowsUpdateSource.IsFeatureUpgrade([UpgradesCategoryId], categoriesReadSuccessfully: true));

    [Fact]
    public void An_ordinary_update_with_successfully_read_categories_is_not_excluded() =>
        Assert.False(WindowsUpdateSource.IsFeatureUpgrade(["some-other-category"], categoriesReadSuccessfully: true));

    [Fact]
    public void Categories_that_could_not_be_read_at_all_fail_closed_and_are_excluded() =>
        // Empty set AND the read itself failed, no signal either way, so it must not be offered rather
        // than falling back to an English-only title match.
        Assert.True(WindowsUpdateSource.IsFeatureUpgrade([], categoriesReadSuccessfully: false));

    [Fact]
    public void An_empty_but_SUCCESSFULLY_read_category_set_is_not_treated_as_a_feature_upgrade() =>
        // Genuinely has no categories (successfully confirmed), different from "could not read them".
        Assert.False(WindowsUpdateSource.IsFeatureUpgrade([], categoriesReadSuccessfully: true));
}

/// <summary>
/// A Defender "Definition Update" reports a MaxDownloadSize wildly larger than what it actually
/// transfers (measured 1.5 GB against a real download of a few MB), so its size is dropped rather than
/// shown wrong; every other Windows Update classification keeps whatever size WUA reported.
/// </summary>
public class WindowsUpdateDownloadSizeTests
{
    private const string DefinitionUpdatesCategoryId = "e0789628-ce08-4437-be74-2495b842f43b";

    [Fact]
    public void A_definition_update_size_is_dropped() =>
        Assert.Null(WindowsUpdateSource.DownloadSizeFor([DefinitionUpdatesCategoryId], 1_579_000_000));

    [Fact]
    public void An_ordinary_update_keeps_its_reported_size() =>
        Assert.Equal(50_000_000, WindowsUpdateSource.DownloadSizeFor(["some-other-category"], 50_000_000));

    [Fact]
    public void An_unknown_size_stays_unknown_either_way() =>
        Assert.Null(WindowsUpdateSource.DownloadSizeFor([DefinitionUpdatesCategoryId], null));

    [Fact]
    public void A_cumulative_update_reporting_every_payload_variant_shows_no_size() =>
        Assert.Null(WindowsUpdateSource.DownloadSizeFor(["some-other-category"], 97_172_000_000));

    [Fact]
    public void A_large_but_plausible_update_keeps_its_size() =>
        Assert.Equal(4_500_000_000, WindowsUpdateSource.DownloadSizeFor(["some-other-category"], 4_500_000_000));
}

/// <summary>
/// Windows Update re-evaluates continuously, so the revision it reports for an update can bump between
/// the scan that offered a row and the install that follows moments later. Matching on the GUID alone,
/// and taking whatever revision a fresh search currently reports, is what keeps that from failing a
/// perfectly installable update with "no longer offered" - the only case that message must still cover is
/// the GUID itself being gone.
/// </summary>
public class WindowsUpdateMatchingTests
{
    [Fact]
    public void Same_guid_with_a_higher_revision_picks_the_higher_revision()
    {
        var found = new List<(string UpdateId, int Revision)>
        {
            ("guid-1", 200),
            ("guid-2", 5),
        };

        var picked = WindowsUpdateMatching.PickInstallTarget("guid-1", found);

        Assert.Equal(200, picked);
    }

    [Fact]
    public void Same_guid_with_a_lower_revision_is_picked_when_it_is_the_only_match()
    {
        // The scan saw revision 5; the fresh search now reports only revision 3 under the same GUID.
        // Absence of any match is the only thing that means "gone" - a revision that moved, in either
        // direction, is not that.
        var found = new List<(string UpdateId, int Revision)>
        {
            ("guid-1", 3),
            ("guid-2", 9),
        };

        var picked = WindowsUpdateMatching.PickInstallTarget("guid-1", found);

        Assert.Equal(3, picked);
    }

    [Fact]
    public void Guid_absent_entirely_returns_null()
    {
        var found = new List<(string UpdateId, int Revision)>
        {
            ("guid-2", 9),
            ("guid-3", 1),
        };

        var picked = WindowsUpdateMatching.PickInstallTarget("guid-1", found);

        Assert.Null(picked);
    }

    [Fact]
    public void The_highest_of_several_matching_revisions_is_picked()
    {
        var found = new List<(string UpdateId, int Revision)>
        {
            ("guid-1", 1),
            ("guid-1", 7),
            ("guid-1", 4),
        };

        var picked = WindowsUpdateMatching.PickInstallTarget("guid-1", found);

        Assert.Equal(7, picked);
    }

    [Fact]
    public void An_empty_search_result_returns_null()
    {
        var picked = WindowsUpdateMatching.PickInstallTarget("guid-1", []);

        Assert.Null(picked);
    }
}

/// <summary>
/// WUA can hand back the same RCW (one shared wrapper per COM identity) for more than one call, so
/// ComScope must not queue the same reference for release twice, a second
/// Marshal.FinalReleaseComObject on an already-released RCW throws and would fail the whole scan. A real
/// COM object is not available in a unit test, so a plain object stands in: the property under test is
/// purely "the same reference is only tracked once", proven via TrackedCount rather than through Marshal
/// itself.
/// </summary>
public class ComScopeTests
{
    [Fact]
    public void The_same_reference_tracked_twice_is_only_queued_for_release_once()
    {
        using var scope = new WindowsUpdateSource.ComScope();
        var shared = new object();

        scope.Track(shared);
        scope.Track(shared);

        Assert.Equal(1, scope.TrackedCount);
    }

    [Fact]
    public void Disposing_a_scope_with_a_duplicate_reference_does_not_throw()
    {
        var scope = new WindowsUpdateSource.ComScope();
        var shared = new object();
        scope.Track(shared);
        scope.Track(shared);

        var exception = Record.Exception(scope.Dispose);

        Assert.Null(exception);
    }

    [Fact]
    public void Distinct_references_are_all_tracked()
    {
        using var scope = new WindowsUpdateSource.ComScope();

        scope.Track(new object());
        scope.Track(new object());

        Assert.Equal(2, scope.TrackedCount);
    }
}
