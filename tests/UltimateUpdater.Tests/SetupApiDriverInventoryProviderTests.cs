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
        var packages = provider.GatherPackages(CancellationToken.None);

        Assert.NotEmpty(expectedNames);
        Assert.Equal(expectedNames.Count, packages.Count);
        Assert.All(packages, p => Assert.Contains(p.PackageName, expectedNames));
    }
}
