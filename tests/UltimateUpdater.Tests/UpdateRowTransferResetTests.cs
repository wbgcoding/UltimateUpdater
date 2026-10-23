using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

/// <summary>A retry starts from nothing: the previous attempt's byte counts and throughput sample must not
/// show on the row, nor feed the first speed reading of the next attempt.</summary>
public class UpdateRowTransferResetTests
{
    private static UpdateItem Item() => new()
    {
        Id = "x",
        SourceId = "winget",
        Category = UpdateCategory.Software,
        Name = "Example",
        InstalledVersion = "1.0",
        AvailableVersion = "1.1",
    };

    private static InstallItemStatus Progress(long received, long total) =>
        new("winget", "x", 50, "downloading", null, null) { BytesReceived = received, BytesTotal = total };

    [Fact]
    public async Task BeginInstall_after_a_failed_attempt_clears_every_transfer_value()
    {
        var row = new UpdateRowViewModel(Item());
        row.BeginInstall();
        row.ApplyInstallStatus(Progress(10, 100));
        await Task.Delay(300);
        row.ApplyInstallStatus(Progress(60, 100));
        row.ApplyInstallStatus(new InstallItemStatus("winget", "x", 60, null, false, "failed"));

        Assert.True(row.BytesPerSecond > 0, "the attempt measured a speed");

        row.BeginInstall();

        Assert.Null(row.BytesReceived);
        Assert.Null(row.BytesTotal);
        Assert.Equal(0, row.BytesPerSecond);
        Assert.False(row.HasTransfer);
        Assert.Equal(string.Empty, row.TransferText);
        Assert.Equal(string.Empty, row.SpeedText);

        // The first reading of the new attempt has nothing to be compared with: no speed yet, not one
        // derived from the old attempt's last sample.
        row.ApplyInstallStatus(Progress(80, 100));
        Assert.Equal(0, row.BytesPerSecond);
    }
}
