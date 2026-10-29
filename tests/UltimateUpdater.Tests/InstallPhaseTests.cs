using System.Text.Json;
using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.ViewModels;
using Xunit;

namespace UltimateUpdater.Tests;

public class InstallPhaseTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // ---- parser: the lines each tool prints when the download is over ----

    [Theory]
    [InlineData("Successfully verified installer hash")]
    [InlineData("Starting package install...")]
    [InlineData("Installer-Hash erfolgreich überprüft")]
    [InlineData("Der Installer-Hash wurde erfolgreich überprüft")]
    [InlineData("Paketinstallation wird gestartet...")]
    [InlineData("Installing collected packages: requests, urllib3")]
    [InlineData("Installiere gesammelte Pakete: requests")]
    [InlineData("Installing python3.12 v3.12.4")]
    [InlineData("  Extracting files...")]
    [InlineData("Installation of the package")]
    [InlineData("- Installing")]
    public void A_known_install_line_is_recognised(string line)
    {
        Assert.True(ProgressParser.IsInstallPhaseLine(line));

        var progress = ProgressParser.ToInstallProgress(line, "status");
        Assert.NotNull(progress);
        Assert.Equal(InstallPhase.Installing, progress!.Phase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Downloading https://example.com/Installing-tool.exe")]
    [InlineData("Collecting requests")]
    [InlineData("Installing build dependencies ... done")]
    [InlineData("C:\\Tools\\Installing\\app.exe")]
    [InlineData("Found Installer Pro [Vendor.InstallerPro] Version 1.0")]
    public void Other_lines_are_not_install_lines(string line)
    {
        Assert.False(ProgressParser.IsInstallPhaseLine(line));
    }

    [Fact]
    public void A_download_reading_is_marked_Downloading()
    {
        var progress = ProgressParser.ToInstallProgress("1.0 MB / 4.0 MB", "status");

        Assert.NotNull(progress);
        Assert.Equal(InstallPhase.Downloading, progress!.Phase);
        Assert.Equal(25, progress.PercentComplete, 1);
    }

    [Fact]
    public void An_install_line_carries_no_figure()
    {
        var progress = ProgressParser.ToInstallProgress("Installing collected packages: a", "status")!;

        Assert.Equal(0, progress.PercentComplete);
        Assert.Null(progress.BytesTotal);
    }

    // ---- pipe: a message without a phase still reads ----

    [Fact]
    public void The_phase_survives_the_pipe_and_a_missing_one_reads_as_Unknown()
    {
        var message = new InstallProgressMessage
        {
            Type = "progress", SourceId = "winget", ItemId = "x", Phase = InstallPhase.Installing,
        };

        var back = JsonSerializer.Deserialize<InstallProgressMessage>(JsonSerializer.Serialize(message, Json), Json)!;
        Assert.Equal(InstallPhase.Installing, back.Phase);

        var old = JsonSerializer.Deserialize<InstallProgressMessage>(
            """{"type":"progress","sourceId":"winget","itemId":"x","percent":10}""", Json)!;
        Assert.Equal(InstallPhase.Unknown, old.Phase);
    }

    // ---- row: what the user sees ----

    private sealed class Clocked
    {
        public DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        public UpdateRowViewModel Row { get; }

        public Clocked()
        {
            Row = new UpdateRowViewModel(new UpdateItem
            {
                Id = "x",
                SourceId = "winget",
                Category = UpdateCategory.Software,
                Name = "Example",
                InstalledVersion = "1.0",
                AvailableVersion = "1.1",
            });
            Row.Clock = () => Now;
            Row.BeginInstall();
        }

        public void Apply(double percent, long? received = null, long? total = null, InstallPhase phase = InstallPhase.Unknown)
        {
            Row.ApplyInstallStatus(new InstallItemStatus("winget", "x", percent, null, null, null)
            {
                BytesReceived = received, BytesTotal = total, Phase = phase,
            });
        }
    }

    [Fact]
    public void A_running_row_without_any_reading_is_indeterminate_and_shows_no_number()
    {
        var c = new Clocked();
        c.Apply(0);

        Assert.True(c.Row.ShowIndeterminateProgress);
        Assert.False(c.Row.ShowDeterminateProgress);
        Assert.StartsWith(LocalizationService.Instance["RowInstallingPhase"].Split('{')[0], c.Row.RunningProgressText);
    }

    [Fact]
    public void A_download_with_a_reading_shows_the_determinate_bar_and_the_real_percent()
    {
        var c = new Clocked();
        c.Apply(40, 4_000_000, 10_000_000, InstallPhase.Downloading);

        Assert.Equal(InstallPhase.Downloading, c.Row.Phase);
        Assert.True(c.Row.ShowDeterminateProgress);
        Assert.False(c.Row.ShowIndeterminateProgress);
        Assert.Equal(40, c.Row.InstallPercent);
        Assert.Equal(c.Row.InstallPercentText, c.Row.RunningProgressText);
    }

    [Fact]
    public void An_install_line_ends_the_download_for_good()
    {
        var c = new Clocked();
        c.Apply(60, 6_000_000, 10_000_000, InstallPhase.Downloading);
        c.Apply(0, phase: InstallPhase.Installing);

        Assert.Equal(InstallPhase.Installing, c.Row.Phase);
        Assert.True(c.Row.ShowIndeterminateProgress);

        // a late repaint of the download bar must not bring the number back
        c.Apply(60, 6_000_000, 10_000_000, InstallPhase.Downloading);
        Assert.Equal(InstallPhase.Installing, c.Row.Phase);
        Assert.False(c.Row.ShowDeterminateProgress);
    }

    [Fact]
    public void A_finished_download_with_nothing_after_it_turns_to_Installing_once_the_grace_has_passed()
    {
        var c = new Clocked();
        c.Apply(100, 10_000_000, 10_000_000, InstallPhase.Downloading);

        c.Now += TimeSpan.FromSeconds(1);
        c.Row.Tick();
        Assert.Equal(InstallPhase.Downloading, c.Row.Phase);

        c.Now += TimeSpan.FromSeconds(1);
        c.Row.Tick();
        Assert.Equal(InstallPhase.Installing, c.Row.Phase);
        Assert.True(c.Row.ShowIndeterminateProgress);
    }

    [Fact]
    public void A_stalled_download_below_100_stays_a_download()
    {
        var c = new Clocked();
        c.Apply(50, 5_000_000, 10_000_000, InstallPhase.Downloading);

        c.Now += TimeSpan.FromMinutes(2);
        c.Row.Tick();

        Assert.Equal(InstallPhase.Downloading, c.Row.Phase);
        Assert.True(c.Row.ShowDeterminateProgress);
    }

    [Fact]
    public void A_second_download_after_a_pause_takes_the_row_back_and_the_bar_never_dips()
    {
        var c = new Clocked();
        c.Apply(100, 2_000_000, 2_000_000, InstallPhase.Downloading);
        c.Now += TimeSpan.FromSeconds(2);
        c.Row.Tick();
        Assert.Equal(InstallPhase.Installing, c.Row.Phase);

        // pip opens the next file: 0 of 6 MB, then 3 of 6 MB
        c.Apply(1, 60_000, 6_000_000, InstallPhase.Downloading);
        Assert.Equal(InstallPhase.Downloading, c.Row.Phase);
        Assert.True(c.Row.InstallPercent >= 25, $"expected the first file to count, got {c.Row.InstallPercent}");

        c.Apply(50, 3_000_000, 6_000_000, InstallPhase.Downloading);
        Assert.Equal(62.5, c.Row.InstallPercent, 1);
        Assert.Equal(8_000_000, c.Row.BytesTotal);
    }

    [Fact]
    public void A_retry_starts_the_phase_over()
    {
        var c = new Clocked();
        c.Apply(0, phase: InstallPhase.Installing);
        Assert.Equal(InstallPhase.Installing, c.Row.Phase);

        c.Row.ApplyInstallStatus(new InstallItemStatus("winget", "x", 0, null, null, null) { IsAwaitingRetry = true });

        Assert.Equal(InstallPhase.Unknown, c.Row.Phase);
        Assert.False(c.Row.HasRealProgress);
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(5, "0:05")]
    [InlineData(83, "1:23")]
    [InlineData(600, "10:00")]
    [InlineData(3725, "1:02:05")]
    public void Elapsed_time_reads_as_minutes_and_seconds(int seconds, string expected)
    {
        Assert.Equal(expected, UpdateRowViewModel.FormatElapsed(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void The_installing_text_counts_up_with_the_timer()
    {
        var c = new Clocked();
        c.Apply(0, phase: InstallPhase.Installing);

        c.Now += TimeSpan.FromSeconds(83);
        c.Row.Tick();

        Assert.EndsWith("1:23", c.Row.RunningStatusText);
        Assert.Equal(c.Row.RunningStatusText, c.Row.RunningProgressText);
    }

    [Fact]
    public void A_finished_row_is_no_longer_indeterminate()
    {
        var c = new Clocked();
        c.Apply(0, phase: InstallPhase.Installing);
        c.Row.ApplyInstallStatus(new InstallItemStatus("winget", "x", 100, null, true, null));

        Assert.False(c.Row.ShowIndeterminateProgress);
        Assert.False(c.Row.IsIndeterminateRun);
    }
}
