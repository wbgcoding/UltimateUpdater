using System.Windows;
using System.Windows.Controls;
using UltimateUpdater.Views.Main;

namespace UltimateUpdater.Tests;

public partial class WindowLayoutTests
{
    /// <summary>The main table's lane geometry of the first rendered row: each column's left edge and width, plus the row's own width.</summary>
    private static (double[] Left, double[] Width) ColumnLanes(Grid tableRow)
    {
        var left = new double[tableRow.ColumnDefinitions.Count];
        var width = new double[tableRow.ColumnDefinitions.Count];
        double x = 0;
        for (var i = 0; i < left.Length; i++)
        {
            left[i] = x;
            width[i] = tableRow.ColumnDefinitions[i].ActualWidth;
            x += width[i];
        }

        return (left, width);
    }

    /// <summary>At the window's minimum width the fixed lanes (check, icon, date, version, size, action, publisher) used to leave the starred Name column a few pixels, so the critical badge sat on top of the publisher. Now Publisher gives up its lane, and Date too while Name would still get under 180 px: Name keeps its minimum, nothing overlaps.</summary>
    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    public void Main_table_keeps_the_name_column_readable_at_minimum_width_with_publishers(string language)
    {
        RenderResult? result = null;
        double nameWidth = 0;
        double publisherWidth = 0;
        double dateWidth = 0;
        double badgeRight = 0;
        double nameCellRight = 0;
        double headerNameWidth = 0;
        var criticalRows = 0;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel(language);
            Populate(viewModel);
            var window = new MainWindow(viewModel, theme, settings);
            var content = (FrameworkElement)window.Content;
            result = WpfHarness.RenderWindow(window, $"main-table-narrow-{language}", (int)window.MinWidth, (int)window.MinHeight);

            var rows = FindAllByName<Grid>(content, "TableRow");
            Assert.NotEmpty(rows);

            foreach (var row in rows.Where(r => r.DataContext is UpdateRowViewModel { IsCritical: true }))
            {
                criticalRows++;
                var (left, width) = ColumnLanes(row);
                nameWidth = width[2];
                publisherWidth = width[3];
                dateWidth = width[4];
                nameCellRight = left[2] + width[2];

                var badge = FindByName<Border>(row, "RowCriticalBadge");
                Assert.NotNull(badge);
                badgeRight = badge!.TransformToAncestor(row).Transform(new Point(0, 0)).X + badge.ActualWidth;
            }

            var header = FindByName<Grid>(content, "MainTableHeader");
            Assert.NotNull(header);
            headerNameWidth = header!.ColumnDefinitions[2].ActualWidth;
        });

        AssertRendered(result);
        Assert.True(criticalRows > 0, "the fixture carries a critical row");
        var minimum = MainViewModel.NameWidthBeforeDateCollapses;
        Assert.True(nameWidth >= minimum, $"Name column is {nameWidth:0} px wide at minimum width (needs at least {minimum:0}).");
        Assert.Equal(nameWidth, headerNameWidth, 1);
        Assert.Equal(0, publisherWidth, 1);
        Assert.Equal(0, dateWidth, 1);
        Assert.True(badgeRight <= nameCellRight + 1,
            $"the critical badge ends at {badgeRight:0} px, past the Name column's edge at {nameCellRight:0} px.");
    }

    /// <summary>Rows with a publisher in a table wide enough for everything: the Publisher column shows with its normal width and the Name column stays far above the collapse threshold.</summary>
    [Fact]
    public void Main_table_shows_the_publisher_column_at_1280()
    {
        RenderResult? result = null;
        double nameWidth = 0;
        double publisherWidth = 0;
        double publisherTextWidth = 0;

        WpfHarness.OnUiThread(() =>
        {
            var (viewModel, theme, settings) = CreateMainViewModel("en");
            Populate(viewModel);
            var window = new MainWindow(viewModel, theme, settings);
            var content = (FrameworkElement)window.Content;
            result = WpfHarness.RenderWindow(window, "main-table-wide-en", 1280, 800);

            var row = FindAllByName<Grid>(content, "TableRow")
                .First(r => r.DataContext is UpdateRowViewModel { HasPublisher: true });
            var (_, width) = ColumnLanes(row);
            nameWidth = width[2];
            publisherWidth = width[3];
            publisherTextWidth = FindByName<TextBlock>(row, "RowPublisherText")!.ActualWidth;
        });

        AssertRendered(result);
        Assert.Equal(120, publisherWidth, 1);
        Assert.True(publisherTextWidth > 0, "the publisher text is laid out");
        Assert.True(nameWidth >= MainViewModel.NameWidthBeforePublisherCollapses, $"Name column is only {nameWidth:0} px wide at 1280.");
    }
}
