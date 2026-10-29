using UltimateUpdater.Services;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

/// <summary>
/// Copying the filtered history to the clipboard as tab-separated text. Only
/// <see cref="HistoryViewModel.BuildClipboardText"/> is exercised directly, the pure part of the
/// feature; the clipboard call itself is not something a headless test can assert on.
/// </summary>
[Collection("UiLanguage")]
public class HistoryViewModelTests
{
    private static HistoryRowViewModel Row(
        string name, string? from, string? to, string sourceId, bool success = true, string? error = null) =>
        new(new HistoryEntry
        {
            Timestamp = new DateTimeOffset(2026, 1, 2, 10, 30, 0, TimeSpan.Zero),
            SourceId = sourceId,
            ItemId = name,
            Name = name,
            FromVersion = from,
            ToVersion = to,
            Success = success,
            Error = error,
        });

    private static HistoryRowViewModel Unchanged(int count) =>
        new(new HistoryEntry
        {
            Timestamp = DateTimeOffset.Now, SourceId = "winget", ItemId = "Vendor.App", Name = "App",
            FromVersion = "1.0", ToVersion = "2.0", Success = true, VersionUnchanged = true,
        }, unchangedCount: count);

    [Fact]
    public void An_unchanged_install_explains_itself_and_names_the_repeat_count_from_the_second_time()
    {
        var first = Unchanged(1).ResultTooltip;
        var third = Unchanged(3).ResultTooltip;

        Assert.False(string.IsNullOrWhiteSpace(first));
        Assert.DoesNotContain('\n', first!);
        Assert.StartsWith(first!, third);
        Assert.Contains("3", third!);
        Assert.Contains('\n', third!);
    }

    [Fact]
    public void A_failed_entry_keeps_its_error_as_the_result_tooltip()
    {
        Assert.Equal("boom", Row("App", "1.0", "2.0", "winget", success: false, error: "boom").ResultTooltip);
        Assert.Null(Row("App", "1.0", "2.0", "winget").ResultTooltip);
    }

    [Fact]
    public void The_header_line_names_every_column_in_order()
    {
        var text = HistoryViewModel.BuildClipboardText([]);
        var header = text.Split("\r\n")[0].Split('\t');

        Assert.Equal(6, header.Length);
        Assert.All(header, cell => Assert.False(string.IsNullOrWhiteSpace(cell)));
    }

    [Fact]
    public void An_empty_selection_yields_only_the_header()
    {
        var text = HistoryViewModel.BuildClipboardText([]);

        Assert.Single(text.Split("\r\n"));
    }

    [Fact]
    public void Each_row_becomes_one_tab_separated_line_with_its_own_cells()
    {
        var rows = new[]
        {
            Row("7-Zip", "23.0", "23.1", "winget"),
            Row("Notepad++", null, "8.6", "winget", success: false, error: "failed"),
        };

        var lines = HistoryViewModel.BuildClipboardText(rows).Split("\r\n");

        Assert.Equal(3, lines.Length); // header plus two rows
        var first = lines[1].Split('\t');
        Assert.Equal("7-Zip", first[0]);
        Assert.Equal("23.0", first[1]);
        Assert.Equal("23.1", first[2]);

        var second = lines[2].Split('\t');
        Assert.Equal("Notepad++", second[0]);
        Assert.Equal("-", second[1]); // no from version: the row's own hyphen placeholder, never blank
        Assert.Equal("8.6", second[2]);
    }

    /// <summary>A row the user stopped is neither a success nor a failure: it reads as cancelled, takes the
    /// caution styling and stays out of the "show only failed" filter.</summary>
    [Fact]
    public void A_cancelled_entry_reads_as_cancelled_and_not_as_a_failure()
    {
        var row = new HistoryRowViewModel(new HistoryEntry
        {
            Timestamp = new DateTimeOffset(2026, 1, 2, 10, 30, 0, TimeSpan.Zero),
            SourceId = "winget",
            ItemId = "git",
            Name = "git",
            Success = false,
            IsCancelled = true,
        });

        Assert.Equal(LocalizationService.Instance["RowCancelled"], row.Result);
        Assert.False(row.Failed);
        Assert.False(row.Succeeded);
        Assert.True(row.IsManualHandoff);
    }

    private static HistoryRowViewModel SuccessRow(string action = "install", bool isRollback = false) =>
        new(new HistoryEntry
        {
            Timestamp = new DateTimeOffset(2026, 1, 2, 10, 30, 0, TimeSpan.Zero),
            SourceId = "winget",
            ItemId = "git",
            Name = "git",
            Success = true,
            Action = action,
            IsRollback = isRollback,
        });

    /// <summary>A successful entry says what was done: updated, rolled back or uninstalled.</summary>
    [Fact]
    public void A_successful_entry_reads_as_updated_rolled_back_or_uninstalled()
    {
        var language = LocalizationService.Instance.CurrentLanguage;
        LocalizationService.Instance.Apply("en");
        try
        {
            Assert.Equal("Updated", SuccessRow().Result);
            Assert.Equal("Rolled back", SuccessRow(isRollback: true).Result);
            Assert.Equal("Uninstalled", SuccessRow(action: "uninstall").Result);
            Assert.False(SuccessRow(action: "uninstall").Failed);
            Assert.False(Row("x", null, null, "winget", success: false).Succeeded);
            Assert.True(SuccessRow().Succeeded);
            Assert.True(SuccessRow(isRollback: true).Succeeded);
            Assert.True(SuccessRow(action: "uninstall").Succeeded);
        }
        finally
        {
            LocalizationService.Instance.Apply(language);
        }
    }

    [Fact]
    public async Task CreateAsync_loads_the_same_entries_the_synchronous_constructor_does()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("wu-history-vm-").FullName, "history.jsonl");
        var history = new HistoryService(new LoggingService(), path);
        foreach (var name in new[] { "a", "b" })
        {
            history.Append(new HistoryEntry
            {
                Timestamp = DateTimeOffset.UtcNow,
                SourceId = "winget", ItemId = name, Name = name,
                FromVersion = "1.0", ToVersion = "2.0", Success = true,
            });
        }

        var viewModel = await HistoryViewModel.CreateAsync(history);

        Assert.Equal(2, viewModel.Entries.Count);
        Assert.Equal(new HistoryViewModel(history).Entries.Select(e => e.Name), viewModel.Entries.Select(e => e.Name));
    }
}
