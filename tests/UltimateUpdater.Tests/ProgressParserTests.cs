using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

public class ProgressParserTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Downloading the package")]
    [InlineData("Installing…")]
    [InlineData("resolved 12 packages in 3s")]
    public void Text_with_no_reading_yields_null(string segment)
    {
        Assert.Null(ProgressParser.Parse(segment));
    }

    [Theory]
    [InlineData("45%", 45)]
    [InlineData("  ██████            45.0 %", 45)]
    [InlineData("100%", 100)]
    public void A_bare_percentage_is_read(string segment, double expected)
    {
        var sample = ProgressParser.Parse(segment);

        Assert.NotNull(sample);
        Assert.Equal(expected, sample!.Value.Percent);
    }

    [Fact]
    public void Winget_style_two_unit_transfer_gives_bytes_and_a_derived_percent()
    {
        var sample = ProgressParser.Parse("  1.5 MB / 3.0 MB");

        Assert.NotNull(sample);
        Assert.Equal((long)(1.5 * 1024 * 1024), sample!.Value.BytesReceived);
        Assert.Equal((long)(3.0 * 1024 * 1024), sample.Value.BytesTotal);
        Assert.Equal(50, sample.Value.Percent!.Value, 1);
    }

    [Fact]
    public void Pip_style_shared_unit_transfer_is_read()
    {
        var sample = ProgressParser.Parse("━━━━━━ 2.5/5.0 MB 3.4 MB/s");

        Assert.NotNull(sample);
        Assert.Equal((long)(2.5 * 1024 * 1024), sample!.Value.BytesReceived);
        Assert.Equal((long)(5.0 * 1024 * 1024), sample.Value.BytesTotal);
        Assert.Equal(50, sample.Value.Percent!.Value, 1);
    }

    [Fact]
    public void A_thousands_separator_does_not_break_the_size()
    {
        var sample = ProgressParser.Parse("1,024.0 KB / 2,048.0 KB");

        Assert.NotNull(sample);
        Assert.Equal(1024L * 1024, sample!.Value.BytesReceived);
        Assert.Equal(2048L * 1024, sample.Value.BytesTotal);
    }

    /// <summary>
    /// A German-locale tool prints the decimal mark as a comma, same reasoning as the percentage below.
    /// Read as an English thousands separator "1,23 MB" became 123 MB, a hundred times too large.
    /// </summary>
    [Fact]
    public void A_comma_decimal_byte_size_is_not_read_as_a_thousands_separator()
    {
        var sample = ProgressParser.Parse("1,23 MB / 4,56 MB");

        Assert.NotNull(sample);
        Assert.Equal((long)(1.23 * 1024 * 1024), sample!.Value.BytesReceived);
        Assert.Equal((long)(4.56 * 1024 * 1024), sample.Value.BytesTotal);
    }

    /// <summary>A genuinely thousands-grouped value must still parse as one number, not fall apart at the
    /// comma just because it now doubles as a decimal mark elsewhere.</summary>
    [Fact]
    public void A_real_thousands_grouping_still_parses_as_one_number()
    {
        var sample = ProgressParser.Parse("1,234,567 B / 2,345,678 B");

        Assert.NotNull(sample);
        Assert.Equal(1_234_567L, sample!.Value.BytesReceived);
        Assert.Equal(2_345_678L, sample.Value.BytesTotal);
    }

    [Fact]
    public void A_percentage_wins_over_a_derived_one_when_both_are_present()
    {
        // the tool printed an explicit 30% next to a transfer that would derive 50%
        var sample = ProgressParser.Parse("30%  1.5 MB / 3.0 MB");

        Assert.NotNull(sample);
        Assert.Equal(30, sample!.Value.Percent!.Value, 1);
    }

    /// <summary>
    /// DISM draws its own bar and prints the figure inside it, reading that gives the cleanup dialog a
    /// real, granular percentage instead of a flat guess per step.
    /// </summary>
    [Theory]
    [InlineData("[===========================66.6%=========             ]", 66.6)]
    [InlineData("[==========================100.0%==========================]", 100)]
    [InlineData("Verification 12% complete.", 12)]
    public void The_dism_and_sfc_progress_lines_are_read(string line, double expected)
    {
        var sample = ProgressParser.Parse(line);

        Assert.NotNull(sample);
        Assert.Equal(expected, sample!.Value.Percent!.Value, 1);
    }

    /// <summary>
    /// DISM prints in the machine's own language, and a German Windows writes the decimal with a comma.
    /// Read as an English number this said 6 % instead of 66,6 %.
    /// </summary>
    [Fact]
    public void A_comma_decimal_percentage_is_read_as_one_number()
    {
        var sample = ProgressParser.Parse("[=========================66,6%==========          ]");

        Assert.NotNull(sample);
        Assert.Equal(66.6, sample!.Value.Percent!.Value, 1);
    }

    [Fact]
    public void ToInstallProgress_carries_the_name_and_bytes()
    {
        var progress = ProgressParser.ToInstallProgress("2.0 MB / 4.0 MB", "Contoso App");

        Assert.NotNull(progress);
        Assert.Equal("Contoso App", progress!.StatusText);
        Assert.Equal(2L * 1024 * 1024, progress.BytesReceived);
        Assert.Equal(4L * 1024 * 1024, progress.BytesTotal);
        Assert.Equal(50, progress.PercentComplete, 1);
    }
}
