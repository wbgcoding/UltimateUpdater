using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.Services.Uninstall;
using UltimateUpdater.ViewModels.Uninstall;

namespace UltimateUpdater.Tests;

/// <summary>How a driver package row looks and behaves in the uninstaller table: greyed with a note when a device
/// uses it, risky when it can be removed, and never scanned for program leftovers after its removal.</summary>
[Collection("UiLanguage")]
public class UninstallDriverRowTests
{
    private static UninstallListing DriverListing(string package, string? inUseBy)
    {
        var entry = new InstalledProgramEntry
        {
            Key = "driver:" + package, Kind = UninstallEntryKind.Driver, Name = "Acme Net (" + package + ")",
            Scope = UninstallScope.Machine, InUseBy = inUseBy,
        };
        return UninstallerBackend.Rate([entry], new NoFiles(), @"C:\Windows").Single();
    }

    private sealed class NoFiles : IUninstallFileProbe
    {
        public bool FileExists(string path) => false;

        public byte[]? ReadHead(string path, int count) => null;

        public bool IsTrustedForElevation(string path) => true;
    }

    [Fact]
    public void A_driver_in_use_is_greyed_without_a_checkbox_and_says_which_device_uses_it()
    {
        LocalizationService.Instance.Apply("en");
        var row = new UninstallRowViewModel(DriverListing("oem1.inf", "Wifi Card"));

        Assert.False(row.Selectable);
        Assert.True(row.HasNote);
        Assert.Equal("In use by Wifi Card, cannot be removed here.", row.NoteText);

        LocalizationService.Instance.Apply("de");
        try
        {
            Assert.Equal("Wird von Wifi Card benutzt, hier nicht entfernbar.", row.NoteText);
        }
        finally
        {
            LocalizationService.Instance.Apply("en");
        }
    }

    [Fact]
    public void An_unused_driver_can_be_ticked_and_is_rated_risky_with_no_note()
    {
        var row = new UninstallRowViewModel(DriverListing("oem2.inf", null));

        Assert.True(row.Selectable);
        Assert.False(row.HasNote);
        Assert.Equal(CleanupRiskLevel.Risky, row.RiskLevel);
    }

    [Fact]
    public async Task A_removed_driver_is_not_scanned_for_program_leftovers()
    {
        var (vm, backend) = await UninstallerViewModelTests.LoadedAsync(
            DriverListing("oem3.inf", null), UninstallerViewModelTests.Listing("Program"));
        vm.SelectAllCommand.Execute(null);
        vm.Confirm = (_, _, _) => true;
        IReadOnlyList<InstalledProgramEntry>? scanned = null;
        backend.Leftovers = removed =>
        {
            scanned ??= removed;
            return [];
        };

        await vm.UninstallSelectedCommand.ExecuteAsync(null);

        Assert.Equal(["Program"], scanned!.Select(entry => entry.Name));
    }
}
