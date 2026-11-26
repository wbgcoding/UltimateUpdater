using UltimateUpdater.Services.Uninstall;

namespace UltimateUpdater.Tests;

/// <summary>The installed Windows updates read unelevated from the component store's package keys.</summary>
[Collection("UiLanguage")]
public class WindowsUpdateRegistryReaderTests
{
    private const string Kb = "Package_for_KB5054156~31bf3856ad364e35~amd64~~26100.3476.1.1";
    private const string InstalledRollup = "Package_for_RollupFix~31bf3856ad364e35~amd64~~26100.4061.1.9";
    private const string StagedRollup = "Package_for_RollupFix~31bf3856ad364e35~amd64~~26100.3900.1.5";
    private const string DotNetRollup = "Package_for_DotNetRollup_481~31bf3856ad364e35~amd64~~10.0.9300.4061.1.9";
    private const string ServicingStack = "Package_for_ServicingStack_3476~31bf3856ad364e35~amd64~~26100.3476.1.1";
    private const string NotAWrapper = "Microsoft-Windows-Foundation-Package~31bf3856ad364e35~amd64~~10.0.26100.1";

    private sealed class DwordRegistry : IRegistryView
    {
        private readonly FakeUninstallRegistry _inner = new();

        public DwordRegistry Package(string name, int state, int? high = null, int? low = null)
        {
            var path = WindowsUpdateDiscovery.CbsPackagesKey + "\\" + name;
            _inner.Set(path, "CurrentState", state.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (high is { } h)
                _inner.Set(path, "InstallTimeHigh", h.ToString(System.Globalization.CultureInfo.InvariantCulture));

            if (low is { } l)
                _inner.Set(path, "InstallTimeLow", l.ToString(System.Globalization.CultureInfo.InvariantCulture));

            return this;
        }

        public IReadOnlyList<string> SubKeyNames(string keyPath) => _inner.SubKeyNames(keyPath);

        public string? Value(string keyPath, string valueName) => _inner.Value(keyPath, valueName);
    }

    private static (int High, int Low) FileTimeOf(DateTime local)
    {
        var fileTime = local.ToFileTime();
        return (unchecked((int)(fileTime >> 32)), unchecked((int)(fileTime & 0xFFFFFFFF)));
    }

    [Fact]
    public void An_installed_kb_package_is_listed_removable_with_its_date_parsed_from_the_filetime()
    {
        var (high, low) = FileTimeOf(new DateTime(2025, 3, 11, 12, 0, 0, DateTimeKind.Local));
        var registry = new DwordRegistry().Package(Kb, 0x70, high, low);

        var row = Assert.Single(WindowsUpdateRegistryReader.Read(registry));

        Assert.Equal(Kb, row.Identity);
        Assert.True(row.Removable);
        Assert.Equal(WindowsUpdateRegistryReader.ReleaseType, row.ReleaseType);
        Assert.Equal(new DateOnly(2025, 3, 11), row.InstalledOn);
    }

    [Fact]
    public void A_staged_rollup_next_to_an_installed_one_is_skipped()
    {
        var registry = new DwordRegistry().Package(StagedRollup, 0x40).Package(InstalledRollup, 0x70);

        var row = Assert.Single(WindowsUpdateRegistryReader.Read(registry));

        Assert.Equal(InstalledRollup, row.Identity);
        Assert.False(row.Removable);
    }

    [Fact]
    public void The_dotnet_rollup_and_the_servicing_stack_are_listed_but_never_removable()
    {
        var registry = new DwordRegistry().Package(DotNetRollup, 0x70).Package(ServicingStack, 0x70);

        var rows = WindowsUpdateRegistryReader.Read(registry);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.False(row.Removable));
        Assert.Contains(rows, row => row.Identity == DotNetRollup);
    }

    [Fact]
    public void A_package_that_is_no_update_wrapper_is_skipped()
    {
        var registry = new DwordRegistry().Package(NotAWrapper, 0x70).Package(Kb, 0x70);

        var row = Assert.Single(WindowsUpdateRegistryReader.Read(registry));

        Assert.Equal(Kb, row.Identity);
    }

    [Fact]
    public void A_package_without_a_state_is_skipped()
    {
        var registry = new FakeUninstallRegistry().Set(WindowsUpdateDiscovery.CbsPackagesKey + "\\" + Kb, "Other", "1");

        Assert.Empty(WindowsUpdateRegistryReader.Read(registry));
    }

    [Fact]
    public void An_unreadable_key_lists_nothing()
    {
        Assert.Empty(WindowsUpdateRegistryReader.Read(new FakeUninstallRegistry()));
    }

    [Theory]
    [InlineData(null, 5)]
    [InlineData(5, null)]
    [InlineData(0, 0)]
    public void A_missing_or_empty_install_time_gives_no_date(int? high, int? low)
    {
        var registry = new DwordRegistry().Package(Kb, 0x70, high, low);

        var row = Assert.Single(WindowsUpdateRegistryReader.Read(registry));

        Assert.Null(row.InstalledOn);
    }

    [Theory]
    [InlineData("Package_for_DotNetRollup~31bf3856ad364e35~amd64~~10.0.1.1", true)]
    [InlineData("Package_for_DotNetRollup_481~31bf3856ad364e35~amd64~~10.0.1.1", true)]
    [InlineData("Package_for_DotNetRollupX~31bf3856ad364e35~amd64~~10.0.1.1", false)]
    [InlineData("Package_for_DotNetRollup_~31bf3856ad364e35~amd64~~10.0.1.1", false)]
    public void The_wrapper_pattern_knows_the_dotnet_rollup(string identity, bool wrapper)
    {
        Assert.Equal(wrapper, WindowsUpdateDiscovery.IsWrapper(identity));
    }

    [Fact]
    public void The_backend_names_the_dotnet_rollup_and_puts_the_updates_in_the_listing()
    {
        var registry = new DwordRegistry().Package(DotNetRollup, 0x70).Package(Kb, 0x70);

        var entries = UninstallerBackend.UpdateEntries(registry, [], key => "[" + key + "]");

        Assert.Equal(2, entries.Count);
        Assert.All(entries, entry => Assert.Equal(UninstallEntryKind.WindowsUpdate, entry.Kind));
        Assert.Contains(entries, entry => entry.Name == "[UninstallerUpdateDotNetRollup]");
    }
}
