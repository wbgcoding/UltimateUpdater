using UltimateUpdater.Models;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

/// <summary>
/// An update that is installed and only waits for a restart still reports IsInstalled=0, so the search
/// lists it again; offering it a second time made an installed update look missing.
/// </summary>
public class WindowsUpdateRestartPendingTests
{
    private static UpdateItem Item(string id, bool rebootRequired) => new()
    {
        Id = id, SourceId = "windowsupdate", Category = UpdateCategory.SystemAndDrivers, Name = id,
        Meta = rebootRequired ? new Dictionary<string, string> { ["rebootRequired"] = "true" } : new Dictionary<string, string>(),
    };

    [Fact]
    public void An_update_waiting_for_a_restart_is_not_offered_again()
    {
        var (offered, waiting) = WindowsUpdateSource.SplitRestartPending(
            [Item("kb1", rebootRequired: true), Item("kb2", rebootRequired: false), Item("kb3", rebootRequired: true)]);

        Assert.Equal(["kb2"], offered.Select(i => i.Id));
        Assert.Equal(["kb1", "kb3"], waiting.Select(i => i.Id));
    }

    [Fact]
    public void Updates_without_a_pending_restart_are_all_offered()
    {
        var (offered, waiting) = WindowsUpdateSource.SplitRestartPending(
            [Item("kb1", rebootRequired: false), Item("kb2", rebootRequired: false)]);

        Assert.Equal(2, offered.Count);
        Assert.Empty(waiting);
    }
}
