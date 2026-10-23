using System.Globalization;
using System.Text.RegularExpressions;
using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

/// <summary>
/// A percentage and a transfer size follow the chosen UI culture instead of a hand-glued " %" or "MB":
/// English reads "50%", German "50 %".
/// </summary>
public class PercentTextTests
{
    /// <summary>German puts a space before the percent sign. Which space depends on the globalization data the
    /// runtime uses (a no-break space from ICU, a plain one from the Windows NLS tables), so the three
    /// spaces that mean the same thing are accepted.</summary>
    internal static void AssertGermanPercent(string number, string actual) =>
        Assert.True(Regex.IsMatch(actual, $"^{number}[ \u00A0\u202F]%$"), $"'{actual}' is not a German percentage of {number}.");

    internal static void InCulture(string name, Action action)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
        try
        {
            action();
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Theory]
    [InlineData(50, "50%")]
    [InlineData(0, "0%")]
    [InlineData(100, "100%")]
    [InlineData(99.6, "100%")]
    public void The_helper_formats_a_percentage_for_english(double percent, string expected) =>
        InCulture("en-US", () => Assert.Equal(expected, PercentFormat.Format(percent)));

    [Fact]
    public void The_helper_puts_a_space_before_the_percent_sign_in_german() =>
        InCulture("de-DE", () => AssertGermanPercent("50", PercentFormat.Format(50)));

    private static UpdateRowViewModel Row() => new(new UpdateItem
    {
        Id = "x",
        SourceId = "winget",
        Category = UpdateCategory.Software,
        Name = "Example",
        InstalledVersion = "1.0",
        AvailableVersion = "1.1",
    });

    [Fact]
    public void A_running_rows_percent_text_follows_the_culture()
    {
        var row = Row();
        row.BeginInstall();
        row.ApplyInstallStatus(new InstallItemStatus("winget", "x", 50, "downloading", null, null));

        InCulture("en-US", () => Assert.Equal("50%", row.InstallPercentText));
        InCulture("de-DE", () => AssertGermanPercent("50", row.InstallPercentText));
    }

    [Fact]
    public void Transfer_and_speed_use_the_shared_size_format()
    {
        var row = Row();
        row.BeginInstall();
        row.ApplyInstallStatus(new InstallItemStatus("winget", "x", 50, "downloading", null, null)
        {
            BytesReceived = 30L * 1024 * 1024,
            BytesTotal = 60L * 1024 * 1024,
        });
        row.BytesPerSecond = 2.5 * 1024 * 1024;

        InCulture("en-US", () =>
        {
            Assert.Equal("30.0 MB / 60.0 MB", row.TransferText);
            Assert.Equal("2.5 MB/s", row.SpeedText);
        });
        InCulture("de-DE", () =>
        {
            Assert.Equal("30,0 MB / 60,0 MB", row.TransferText);
            Assert.Equal("2,5 MB/s", row.SpeedText);
        });
    }

    [Fact]
    public void A_cleanup_rows_percent_text_follows_the_culture()
    {
        var target = CleanupCatalog.Build().First();
        var item = new CleanupItemViewModel(target, () => { }) { Percent = 50 };

        InCulture("en-US", () => Assert.Equal("50%", item.PercentText));
        InCulture("de-DE", () => AssertGermanPercent("50", item.PercentText));
    }

    /// <summary>Every place that used to glue " %" onto a number by hand now goes through the helper.</summary>
    [Fact]
    public void No_view_model_glues_a_percent_sign_onto_a_number_by_hand()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "UltimateUpdater", "ViewModels")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        var offenders = Directory.EnumerateFiles(Path.Combine(directory!.FullName, "src", "UltimateUpdater", "ViewModels"), "*.cs")
            .SelectMany(file => File.ReadLines(file).Select((line, index) => (file, line, index)))
            .Where(x => x.line.Contains("} %\"", StringComparison.Ordinal))
            .Select(x => $"{Path.GetFileName(x.file)}:{x.index + 1}")
            .ToList();

        Assert.True(offenders.Count == 0, "Hand-formatted percent text: " + string.Join(", ", offenders));
    }
}
