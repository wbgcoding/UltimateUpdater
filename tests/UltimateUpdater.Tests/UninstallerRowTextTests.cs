using UltimateUpdater.Services.Formatting;
using UltimateUpdater.Services.Uninstall;
using UltimateUpdater.ViewModels.Uninstall;
using static UltimateUpdater.Tests.UninstallerViewModelTests;

namespace UltimateUpdater.Tests;

/// <summary>The texts the redesigned row and section show: the detail line, the one method tag, the hover, the section total and the empty hint.</summary>
[Collection("UiLanguage")]
public class UninstallerRowTextTests
{
    private const string Dot = " \u00B7 ";

    private static UninstallerViewModel Loaded(params UninstallListing[] listings)
    {
        var backend = new FakeBackend(listings);
        var viewModel = new UninstallerViewModel(backend, new LoggingService()) { PostToUi = action => action() };
        viewModel.SetListings(backend.Current);
        return viewModel;
    }

    private static UninstallRowViewModel Row(UninstallListing listing) => Assert.Single(Loaded(listing).AllRows);

    [Fact]
    public void The_detail_line_joins_publisher_and_version_with_a_middle_dot() =>
        Assert.Equal("Acme" + Dot + "2.5", Row(Listing("Alpha", publisher: "Acme", version: "2.5")).DetailText);

    [Fact]
    public void The_detail_line_puts_the_launcher_between_publisher_and_version()
    {
        var listing = Listing("Apex", publisher: "Respawn", version: "1.2");
        listing = listing with { Entry = listing.Entry with { Key = "Steam App 1172470" } };

        Assert.Equal("Respawn" + Dot + "Steam" + Dot + "1.2", Row(listing).DetailText);
    }

    [Fact]
    public void The_detail_line_leaves_out_what_the_entry_does_not_have()
    {
        Assert.Equal("1.0", Row(Listing("Alpha", publisher: "", version: "1.0")).DetailText);
        Assert.Equal("Acme", Row(Listing("Alpha", publisher: "Acme", version: " ")).DetailText);

        var bare = Row(Listing("Alpha", publisher: "", version: ""));
        Assert.Equal(string.Empty, bare.DetailText);
        Assert.False(bare.HasDetail);
    }

    [Fact]
    public void Only_an_uninstaller_with_its_own_window_gets_the_method_tag()
    {
        var quiet = Listing("Quiet");
        var interactive = quiet with { Plan = new UninstallPlan { Method = UninstallMethod.Interactive, Executable = @"C:\Program Files\X\u.exe" } };
        var refused = Listing("Refused", runnable: false);

        Assert.False(Row(quiet).ShowsMethodTag);
        Assert.True(Row(interactive).ShowsMethodTag);
        Assert.False(Row(refused).ShowsMethodTag);
    }

    [Fact]
    public void The_row_hover_says_how_a_row_is_removed_or_why_it_cannot_be()
    {
        var loc = LocalizationService.Instance;
        var removable = Row(Listing("Alpha"));
        var locked = Row(Listing("Beta", runnable: false));

        Assert.Equal(removable.MethodText, removable.RowTooltip);
        Assert.NotEmpty(removable.RowTooltip);
        Assert.False(locked.Selectable);
        Assert.Equal(locked.UnavailableReason, locked.RowTooltip);
        Assert.Equal(loc["UninstallerUnavailableNoMethod"], locked.RowTooltip);
    }

    [Fact]
    public void A_section_header_carries_the_total_size_of_its_rows()
    {
        var viewModel = Loaded(
            Listing("Alpha", size: 1024L * 1024 * 1024),
            Listing("Beta", size: 512L * 1024 * 1024),
            Listing("Gamma"));

        var programs = viewModel.Sections.Single(section => section.Kind == UninstallSection.Programs);

        Assert.Equal(ByteSize.Format(1536L * 1024 * 1024), programs.TotalSizeText);
        Assert.True(programs.HasTotalSize);
    }

    [Fact]
    public void A_section_without_any_known_size_shows_no_total()
    {
        var programs = Loaded(Listing("Alpha"), Listing("Beta")).Sections.Single(section => section.Kind == UninstallSection.Programs);

        Assert.Equal(string.Empty, programs.TotalSizeText);
        Assert.False(programs.HasTotalSize);
    }

    [Fact]
    public void The_total_follows_the_search()
    {
        var viewModel = Loaded(Listing("Alpha", size: 1024L * 1024 * 1024), Listing("Beta", size: 512L * 1024 * 1024));
        var programs = viewModel.Sections.Single(section => section.Kind == UninstallSection.Programs);

        viewModel.SearchText = "Beta";

        Assert.Equal(ByteSize.Format(512L * 1024 * 1024), programs.TotalSizeText);
    }

    [Fact]
    public void An_empty_machine_explains_what_would_fill_the_table()
    {
        var viewModel = Loaded();

        Assert.True(viewModel.ShowsEmptyText);
        Assert.Equal(LocalizationService.Instance["UninstallerEmptyHint"], viewModel.EmptyHint);
        Assert.True(viewModel.HasEmptyHint);
    }

    [Fact]
    public void A_search_without_a_match_explains_how_to_get_the_rows_back()
    {
        var viewModel = Loaded(Listing("Alpha"));

        viewModel.SearchText = "no such program";

        Assert.True(viewModel.ShowsEmptyText);
        Assert.Equal(LocalizationService.Instance["UninstallerNoMatchHint"], viewModel.EmptyHint);
    }
}
