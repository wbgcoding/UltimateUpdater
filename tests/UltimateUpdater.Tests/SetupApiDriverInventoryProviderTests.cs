using System.IO;
using System.Linq;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

/// <summary>
/// <see cref="SetupApiDriverInventoryProvider"/> is otherwise exercised only through a live
/// run (real hardware, no fake to stand in for SetupAPI), but this one call is worth a direct test anyway,
/// because it once failed on every single real machine. SetupDiGetDeviceProperty has no unsuffixed or ANSI
/// export in setupapi.dll, only SetupDiGetDevicePropertyW; the P/Invoke declared no CharSet, so the runtime
/// could not resolve it and every property read (class name, driver INF path, version, dates) threw
/// EntryPointNotFoundException, silently emptying the whole driver cleanup category on every machine.
/// </summary>
public class SetupApiDriverInventoryProviderTests
{
    /// <summary>A failing lookup is no proof of absence: only "no such devnode" (0x0D) makes a device
    /// non-present and thus a removal candidate; success and every other error keep it connected.</summary>
    [Theory]
    [InlineData(0x00, true)]    // CR_SUCCESS
    [InlineData(0x0D, false)]   // CR_NO_SUCH_DEVNODE
    [InlineData(0x03, true)]    // CR_OUT_OF_MEMORY
    [InlineData(0x16, true)]    // CR_NO_SUCH_VALUE
    [InlineData(0x1A, true)]    // CR_ACCESS_DENIED
    [InlineData(0x39, true)]    // CR_FAILURE
    public void Only_no_such_devnode_means_the_device_is_not_present(int result, bool expectedPresent) =>
        Assert.Equal(expectedPresent, SetupApiDriverInventoryProvider.PresenceFromLocateResult(result));

    /// <summary>A package counts as unused only when no device is bound to it, so packages are listed only
    /// after a complete device pass; before one, nothing may be offered at all.</summary>
    [Fact]
    public void GatherPackages_lists_nothing_before_a_complete_device_pass()
    {
        var provider = new SetupApiDriverInventoryProvider();

        Assert.Empty(provider.GatherPackages(CancellationToken.None));
    }

    [Theory]
    [InlineData(259, true)]   // ERROR_NO_MORE_ITEMS: the list is done
    [InlineData(0, false)]
    [InlineData(5, false)]    // ERROR_ACCESS_DENIED
    [InlineData(8, false)]    // ERROR_NOT_ENOUGH_MEMORY
    public void Only_no_more_items_ends_the_device_walk_cleanly(int lastError, bool expected) =>
        Assert.Equal(expected, SetupApiDriverInventoryProvider.EnumerationEndedCleanly(lastError));

    [Theory]
    [InlineData(true, 0x12, 0, true)]        // a string path
    [InlineData(true, 0x2012, 0, false)]     // a string list is no single path
    [InlineData(false, 0, 1168, true)]       // ERROR_NOT_FOUND: no driver bound
    [InlineData(false, 0, 122, false)]       // ERROR_INSUFFICIENT_BUFFER
    [InlineData(false, 0, 5, false)]         // ERROR_ACCESS_DENIED
    [InlineData(false, 0, 13, false)]        // ERROR_INVALID_DATA
    public void Only_a_string_or_not_found_settles_a_driver_binding(
        bool succeeded, int propertyType, int lastError, bool expected) =>
        Assert.Equal(expected, SetupApiDriverInventoryProvider.InfPathReadIsConclusive(succeeded, propertyType, lastError));

    [Fact]
    public void GatherDevices_resolves_every_device_property_without_throwing()
    {
        var provider = new SetupApiDriverInventoryProvider();

        var devices = provider.GatherDevices(CancellationToken.None);

        Assert.NotEmpty(devices);
        // A class name comes from a DEVPKEY read on every device; before the fix this call never
        // returned at all, so seeing it non-empty for at least one device proves the property API
        // actually resolved rather than every device silently falling back to the empty default.
        Assert.Contains(devices, d => !string.IsNullOrEmpty(d.ClassName));
    }

    /// <summary>
    /// Before the fix, this looked for an <c>oem*.inf</c> file INSIDE each DriverStore FileRepository
    /// folder, a name the folder never actually contains (it is named after the package's ORIGINAL inf,
    /// see <see cref="DriverStoreFolderNameParser"/>): the whole old-driver-packages group came back empty
    /// on every real install. Ground truth on the machine this test runs on is whatever
    /// <c>%windir%\INF\oem*.inf</c> actually holds right now: every one of those published files must come
    /// back, under its own published name (the only name pnputil accepts), never zero.
    /// </summary>
    [Fact]
    public void GatherPackages_finds_every_published_oem_inf_file()
    {
        var infDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");
        var expectedNames = Directory.GetFiles(infDirectory)
            .Select(Path.GetFileName)
            .Where(name => DriverCleanupDiscovery.IsOemPackageName(name!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var provider = new SetupApiDriverInventoryProvider();
        provider.GatherDevices(CancellationToken.None);
        var packages = provider.GatherPackages(CancellationToken.None);

        Assert.NotEmpty(expectedNames);
        Assert.Equal(expectedNames.Count, packages.Count);
        Assert.All(packages, p => Assert.Contains(p.PackageName, expectedNames));
    }
}
