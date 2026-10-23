using UltimateUpdater.Models;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

/// <summary>The row's own Publisher/HasPublisher and its widened tooltip: a winget id yields its
/// Publisher.Product vendor, Windows Update reports Microsoft (or a driver's real manufacturer), and
/// every other source shows nothing rather than a guess.</summary>
public class UpdateRowPublisherTests
{
    private static UpdateRowViewModel Row(
        string id, string sourceId = "winget", IReadOnlyDictionary<string, string>? meta = null) => new(new UpdateItem
    {
        Id = id,
        SourceId = sourceId,
        Category = UpdateCategory.Software,
        Name = "Mozilla Firefox",
        AvailableVersion = "129.0.1",
        Meta = meta ?? new Dictionary<string, string>(),
    });

    [Fact]
    public void A_winget_shaped_id_exposes_its_publisher()
    {
        var row = Row("Mozilla.Firefox");
        Assert.Equal("Mozilla", row.Publisher);
        Assert.True(row.HasPublisher);
    }

    [Fact]
    public void A_plain_id_has_no_publisher()
    {
        var row = Row("eslint", sourceId: "npm");
        Assert.Null(row.Publisher);
        Assert.False(row.HasPublisher);
    }

    [Fact]
    public void A_pip_id_with_an_embedded_python_path_has_no_publisher()
    {
        // Reproduces the reported bug: "anyio#C:\...\Python314\python.exe" has a dot too, but in the
        // interpreter path, not a "Publisher.Product" prefix.
        var row = Row("anyio#C:\\Users\\user\\AppData\\Local\\Programs\\Python\\Python314\\python.exe", sourceId: "pip");
        Assert.Null(row.Publisher);
        Assert.False(row.HasPublisher);
    }

    [Fact]
    public void A_windows_update_row_reports_microsoft()
    {
        var row = Row("KB5041585:1", sourceId: "windowsupdate");
        Assert.Equal("Microsoft", row.Publisher);
    }

    [Fact]
    public void A_windows_update_driver_row_reports_the_captured_manufacturer()
    {
        var row = Row("driver-guid:1", sourceId: "windowsupdate",
            meta: new Dictionary<string, string> { ["isDriverUpdate"] = "true", ["driverManufacturer"] = "NVIDIA" });

        Assert.Equal("NVIDIA", row.Publisher);
    }

    [Fact]
    public void A_windows_update_driver_row_with_no_captured_manufacturer_reports_nothing()
    {
        var row = Row("driver-guid:1", sourceId: "windowsupdate",
            meta: new Dictionary<string, string> { ["isDriverUpdate"] = "true" });

        Assert.Null(row.Publisher);
    }

    [Fact]
    public void The_tooltip_lists_name_id_and_source_always_and_publisher_only_when_known()
    {
        var withPublisher = Row("Mozilla.Firefox");
        Assert.Equal(
            $"Mozilla Firefox{Environment.NewLine}Mozilla.Firefox{Environment.NewLine}winget{Environment.NewLine}Mozilla",
            withPublisher.NameTooltip);

        var withoutPublisher = Row("eslint", sourceId: "npm");
        Assert.Equal(
            $"Mozilla Firefox{Environment.NewLine}eslint{Environment.NewLine}npm",
            withoutPublisher.NameTooltip);
    }

    [Fact]
    public void The_tooltip_never_leaks_a_pip_id_python_path_and_shows_it_as_its_own_line()
    {
        var row = Row("anyio#C:\\Python314\\python.exe", sourceId: "pip",
            meta: new Dictionary<string, string> { ["pipPackage"] = "anyio", ["pythonPath"] = "C:\\Python314\\python.exe" });

        Assert.DoesNotContain('#', row.NameTooltip);
        Assert.EndsWith("C:\\Python314\\python.exe", row.NameTooltip);
        Assert.StartsWith(
            $"Mozilla Firefox{Environment.NewLine}anyio{Environment.NewLine}pip{Environment.NewLine}",
            row.NameTooltip);
    }

    // ---- pip publisher, filled in by the background enrichment pass (PyPI author/maintainer) ----

    [Fact]
    public void A_pip_row_reports_the_publisher_the_enrichment_pass_found()
    {
        var row = Row("anyio", sourceId: "pip");
        Assert.False(row.HasPublisher);

        row.ApplyEnrichment(null, null, publisher: "Jane Doe");

        Assert.Equal("Jane Doe", row.Publisher);
        Assert.True(row.HasPublisher);
    }

    /// <summary>The column text is shortened, the full name still carries the tooltip and the raw
    /// Publisher fact.</summary>
    [Fact]
    public void A_pip_rows_publisher_display_text_is_shortened_but_the_tooltip_stays_full()
    {
        var row = Row("widgetlib", sourceId: "pip");
        row.ApplyEnrichment(null, null, publisher: "Widget Vendor Corporation");

        Assert.Equal("Widget Vendor Corporation", row.Publisher);
        Assert.Equal("Widget Vendor", row.PublisherDisplay);
        Assert.Contains("Widget Vendor Corporation", row.NameTooltip);
    }

    [Fact]
    public void A_pip_row_with_only_a_github_owner_reports_it_as_the_publisher()
    {
        var row = Row("flask", sourceId: "pip");
        row.ApplyEnrichment(null, null, publisher: "pallets");

        Assert.Equal("pallets", row.Publisher);
        Assert.True(row.HasPublisher);
    }

    [Fact]
    public void A_pip_row_the_enrichment_pass_never_reached_has_no_publisher()
    {
        var row = Row("anyio", sourceId: "pip");
        Assert.Null(row.Publisher);
        Assert.False(row.HasPublisher);
    }
}
