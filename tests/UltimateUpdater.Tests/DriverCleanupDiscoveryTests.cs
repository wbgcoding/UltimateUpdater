using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

/// <summary>
/// The pure judgment behind the driver cleanup category, driven entirely from fabricated device/package
/// lists: no real hardware, no SetupAPI call and no elevation required. Mirrors the discipline
/// <see cref="LeftoverInstallSafetyTests"/> already applies to its own "plausible, never proven" gate.
/// </summary>
public class DriverCleanupDiscoveryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    private static DriverDeviceInfo Device(
        string instanceId, string friendlyName, string className, bool present, string? infPath,
        DateTimeOffset? lastArrival = null, DateTimeOffset? lastRemoval = null, string? driverVersion = null,
        DateTimeOffset? driverDate = null, DateTimeOffset? installDate = null,
        DateTimeOffset? firstInstallDate = null, DateTimeOffset? registryWriteDate = null) =>
        new(instanceId, friendlyName, className, present, infPath, driverVersion, driverDate, lastArrival, lastRemoval,
            installDate, firstInstallDate, registryWriteDate);

    private static DriverPackageInfo Package(
        string packageName, string provider, string className, string? version, DateTimeOffset? date,
        string originalName, long sizeBytes = 1024, string? storePath = null) =>
        new(packageName, provider, className, version, date, originalName, sizeBytes, storePath);

    [Fact]
    public void A_non_present_device_on_a_third_party_package_is_offered()
    {
        var devices = new[]
        {
            Device("USB\\VID_1234", "Old Scanner", "Image", present: false, @"C:\Windows\INF\oem12.inf",
                lastRemoval: Now.AddDays(-30)),
        };

        var found = DriverCleanupDiscovery.DiscoverDisconnectedDevices(devices, []);

        Assert.Single(found);
        Assert.Equal("oem12.inf", found[0].PackageName);
    }

    [Fact]
    public void A_present_device_is_never_offered_even_on_a_third_party_package()
    {
        var devices = new[]
        {
            Device("USB\\VID_1234", "Connected Scanner", "Image", present: true, @"C:\Windows\INF\oem12.inf"),
        };

        Assert.Empty(DriverCleanupDiscovery.DiscoverDisconnectedDevices(devices, []));
    }

    [Fact]
    public void A_device_on_an_inbox_driver_is_never_offered()
    {
        // An inbox driver's own .inf never carries the oemN.inf shape pnputil gives a third-party package.
        var devices = new[]
        {
            Device("USB\\VID_5678", "Generic USB Hub", "USB", present: false, @"C:\Windows\INF\usb.inf",
                lastRemoval: Now.AddDays(-500)),
        };

        Assert.Empty(DriverCleanupDiscovery.DiscoverDisconnectedDevices(devices, []));
    }

    [Theory]
    [InlineData("System")]
    [InlineData("Computer")]
    [InlineData("Processor")]
    [InlineData("Volume")]
    [InlineData("SoftwareDevice")]
    public void A_device_of_an_excluded_class_is_never_offered_even_when_non_present(string excludedClass)
    {
        var devices = new[]
        {
            Device("ACPI\\SOMETHING", "System component", excludedClass, present: false, @"C:\Windows\INF\oem3.inf",
                lastRemoval: Now.AddDays(-500)),
        };

        Assert.Empty(DriverCleanupDiscovery.DiscoverDisconnectedDevices(devices, []));
    }

    [Fact]
    public void A_device_with_no_package_at_all_is_never_offered()
    {
        var devices = new[]
        {
            Device("ROOT\\PHANTOM", "Ghost device", "Unknown", present: false, infPath: null),
        };

        Assert.Empty(DriverCleanupDiscovery.DiscoverDisconnectedDevices(devices, []));
    }

    [Fact]
    public void Rows_sort_oldest_last_use_first_and_unknown_dates_sort_last()
    {
        var devices = new[]
        {
            Device("A", "Newer", "Image", false, @"C:\Windows\INF\oem1.inf", lastRemoval: Now.AddDays(-10)),
            Device("B", "Oldest", "Image", false, @"C:\Windows\INF\oem2.inf", lastRemoval: Now.AddDays(-900)),
            Device("C", "Unknown date", "Image", false, @"C:\Windows\INF\oem3.inf"),
            Device("D", "Uses arrival only", "Image", false, @"C:\Windows\INF\oem4.inf", lastArrival: Now.AddDays(-500)),
        };

        var found = DriverCleanupDiscovery.DiscoverDisconnectedDevices(devices, []);

        Assert.Equal(["B", "D", "A", "C"], found.Select(d => d.InstanceId));
    }

    [Fact]
    public void Last_removal_date_wins_over_last_arrival_date_when_both_are_present()
    {
        var devices = new[]
        {
            Device("A", "Device", "Image", false, @"C:\Windows\INF\oem1.inf",
                lastArrival: Now.AddDays(-5), lastRemoval: Now.AddDays(-800)),
        };

        var found = DriverCleanupDiscovery.DiscoverDisconnectedDevices(devices, []);

        Assert.Equal(Now.AddDays(-800), found[0].LastConnected);
    }

    /// <summary>Last-seen fallback chain: removal, then arrival, then install date, then first-install
    /// date, then the registry key's own last-write time, in that order, each only consulted when every
    /// earlier one is missing. The kind carried alongside says which family produced it, so the row's own
    /// text never claims "last connected" for what is really an install date.</summary>
    [Theory]
    [InlineData(true, true, true, true, DriverLastSeenKind.Connected)]
    [InlineData(false, true, true, true, DriverLastSeenKind.Connected)]
    [InlineData(false, false, true, true, DriverLastSeenKind.Installed)]
    [InlineData(false, false, false, true, DriverLastSeenKind.Installed)]
    [InlineData(false, false, false, false, DriverLastSeenKind.Installed)]
    public void The_last_seen_fallback_chain_tries_every_source_in_order(
        bool hasRemoval, bool hasArrival, bool hasInstall, bool hasFirstInstall, DriverLastSeenKind expectedKind)
    {
        // Every source that is "present" for this case carries a DIFFERENT date, so picking the wrong one
        // is distinguishable, not just picking the right kind by accident.
        var devices = new[]
        {
            Device("A", "Device", "Sound", present: false, @"C:\Windows\INF\oem1.inf",
                lastArrival: hasArrival ? Now.AddDays(-1) : null,
                lastRemoval: hasRemoval ? Now.AddDays(-2) : null,
                installDate: hasInstall ? Now.AddDays(-3) : null,
                firstInstallDate: hasFirstInstall ? Now.AddDays(-4) : null,
                registryWriteDate: Now.AddDays(-5)),
        };

        var found = DriverCleanupDiscovery.DiscoverDisconnectedDevices(devices, []);

        Assert.Equal(expectedKind, found[0].LastConnectedKind);
        var expectedDate = hasRemoval ? Now.AddDays(-2)
            : hasArrival ? Now.AddDays(-1)
            : hasInstall ? Now.AddDays(-3)
            : hasFirstInstall ? Now.AddDays(-4)
            : Now.AddDays(-5);
        Assert.Equal(expectedDate, found[0].LastConnected);
    }

    [Fact]
    public void A_device_with_no_date_anywhere_reports_kind_Unknown()
    {
        var devices = new[]
        {
            Device("A", "Device", "Sound", present: false, @"C:\Windows\INF\oem1.inf"),
        };

        var found = DriverCleanupDiscovery.DiscoverDisconnectedDevices(devices, []);

        Assert.Equal(DriverLastSeenKind.Unknown, found[0].LastConnectedKind);
        Assert.Null(found[0].LastConnected);
    }

    /// <summary>A disconnected device row's tooltip facts come from its own bound package,
    /// joined by package name, purely for display.</summary>
    [Fact]
    public void A_disconnected_devices_row_carries_its_bound_packages_facts_for_the_tooltip()
    {
        var devices = new[]
        {
            Device("A", "Old Scanner", "Image", present: false, @"C:\Windows\INF\oem12.inf", lastRemoval: Now.AddDays(-30)),
        };
        var packages = new[]
        {
            Package("oem12.inf", "Acme", "Image", "1.2.3.4", Now.AddYears(-1), "acmescan.inf", sizeBytes: 4096,
                storePath: @"C:\Windows\System32\DriverStore\FileRepository\acmescan.inf_amd64_abc123"),
        };

        var found = DriverCleanupDiscovery.DiscoverDisconnectedDevices(devices, packages);

        Assert.Equal("Acme", found[0].Provider);
        Assert.Equal("acmescan.inf", found[0].OriginalName);
        Assert.Equal(@"C:\Windows\System32\DriverStore\FileRepository\acmescan.inf_amd64_abc123", found[0].StorePath);
        Assert.Equal("1.2.3.4", found[0].Version);
        Assert.Equal(Now.AddYears(-1), found[0].Date);
        Assert.Equal(4096, found[0].SizeBytes);
    }

    [Fact]
    public void A_disconnected_devices_row_still_appears_when_its_bound_package_no_longer_resolves()
    {
        var devices = new[]
        {
            Device("A", "Old Scanner", "Image", present: false, @"C:\Windows\INF\oem12.inf", lastRemoval: Now.AddDays(-30)),
        };

        var found = DriverCleanupDiscovery.DiscoverDisconnectedDevices(devices, []);

        Assert.Single(found);
        Assert.Null(found[0].Provider);
        Assert.Equal(0, found[0].SizeBytes);
    }

    /// <summary>The "installed now" tooltip fact carries the confidently newer package's own
    /// version/date, both for a standalone superseded package row and for a disconnected device row whose
    /// bound package happens to be the superseded one - never guessed when nothing is confidently newer.</summary>
    [Fact]
    public void A_superseded_packages_row_carries_the_newer_packages_version_and_date()
    {
        var devices = new[]
        {
            Device("A", "Device", "Sound", present: false, @"C:\Windows\INF\oem7.inf"),
            Device("B", "Device", "Sound", present: true, @"C:\Windows\INF\oem9.inf"),
        };
        var packages = new[]
        {
            Package("oem7.inf", "Acme", "Sound", "1.0.0.0", Now.AddYears(-2), "acmesnd.inf"),
            Package("oem9.inf", "Acme", "Sound", "2.0.0.0", Now.AddMonths(-1), "acmesnd.inf"),
        };

        var found = DriverCleanupDiscovery.DiscoverOldPackages(devices, packages);

        var superseded = Assert.Single(found);
        Assert.Equal("oem7.inf", superseded.PackageName);
        Assert.Equal("2.0.0.0", superseded.NewerVersion);
        Assert.Equal(Now.AddMonths(-1), superseded.NewerDate);
    }

    /// <summary>A rolled-back or maker-pinned driver: the older package is the one a connected device runs on.</summary>
    [Fact]
    public void A_superseded_package_a_present_device_is_bound_to_is_never_offered()
    {
        var devices = new[]
        {
            Device("A", "Pinned device", "Sound", present: true, @"C:\Windows\INF\oem7.inf"),
            Device("B", "Other device", "Sound", present: false, @"C:\Windows\INF\oem9.inf"),
        };
        var packages = new[]
        {
            Package("oem7.inf", "Acme", "Sound", "1.0.0.0", Now.AddYears(-2), "acmesnd.inf"),
            Package("oem9.inf", "Acme", "Sound", "2.0.0.0", Now.AddMonths(-1), "acmesnd.inf"),
        };

        var found = DriverCleanupDiscovery.DiscoverOldPackages(devices, packages);

        Assert.DoesNotContain(found, c => c.PackageName == "oem7.inf");
    }

    [Fact]
    public void An_unbound_packages_row_never_carries_a_newer_version_when_nothing_supersedes_it()
    {
        var packages = new[] { Package("oem7.inf", "Acme", "Sound", "1.0.0.0", Now.AddYears(-2), "acmesnd.inf") };

        var found = DriverCleanupDiscovery.DiscoverOldPackages([], packages);

        var only = Assert.Single(found);
        Assert.Null(only.NewerVersion);
        Assert.Null(only.NewerDate);
    }

    [Fact]
    public void A_disconnected_devices_row_carries_the_newer_version_when_its_bound_package_is_superseded()
    {
        var devices = new[]
        {
            // Disconnected, still bound to the OLDER package of the group - the newer package (oem9.inf) is
            // bound to a different, present device, so the older one is genuinely offered as "disconnected".
            Device("A", "Old Device", "Sound", present: false, @"C:\Windows\INF\oem7.inf", lastRemoval: Now.AddDays(-30)),
            Device("B", "Device", "Sound", present: true, @"C:\Windows\INF\oem9.inf"),
        };
        var packages = new[]
        {
            Package("oem7.inf", "Acme", "Sound", "1.0.0.0", Now.AddYears(-2), "acmesnd.inf"),
            Package("oem9.inf", "Acme", "Sound", "2.0.0.0", Now.AddMonths(-1), "acmesnd.inf"),
        };

        var found = DriverCleanupDiscovery.DiscoverDisconnectedDevices(devices, packages);

        Assert.Equal("2.0.0.0", found[0].NewerVersion);
        Assert.Equal(Now.AddMonths(-1), found[0].NewerDate);
    }

    [Fact]
    public void A_package_bound_to_no_device_at_all_is_an_old_package()
    {
        var devices = Array.Empty<DriverDeviceInfo>();
        var packages = new[] { Package("oem7.inf", "Acme", "Sound", "1.0.0.0", Now.AddYears(-2), "acmesnd.inf") };

        var found = DriverCleanupDiscovery.DiscoverOldPackages(devices, packages);

        Assert.Single(found);
        Assert.Equal("oem7.inf", found[0].PackageName);
    }

    [Fact]
    public void A_package_bound_to_a_present_device_and_not_superseded_is_never_offered()
    {
        var devices = new[] { Device("A", "Device", "Sound", present: true, @"C:\Windows\INF\oem7.inf") };
        var packages = new[] { Package("oem7.inf", "Acme", "Sound", "1.0.0.0", Now.AddYears(-2), "acmesnd.inf") };

        Assert.Empty(DriverCleanupDiscovery.DiscoverOldPackages(devices, packages));
    }

    [Fact]
    public void An_older_version_of_a_package_still_bound_to_a_disconnected_device_is_offered_when_a_newer_one_exists()
    {
        // Both packages are bound to a device (never "unused" on their own); only the supersede-by-version
        // rule decides here, and the older one's device is not connected.
        var devices = new[]
        {
            Device("A", "Device", "Sound", present: false, @"C:\Windows\INF\oem7.inf"),
            Device("B", "Device", "Sound", present: true, @"C:\Windows\INF\oem9.inf"),
        };
        var packages = new[]
        {
            Package("oem7.inf", "Acme", "Sound", "1.0.0.0", Now.AddYears(-2), "acmesnd.inf"),
            Package("oem9.inf", "Acme", "Sound", "2.0.0.0", Now.AddMonths(-1), "acmesnd.inf"),
        };

        var found = DriverCleanupDiscovery.DiscoverOldPackages(devices, packages);

        Assert.Equal(["oem7.inf"], found.Select(f => f.PackageName));
    }

    [Fact]
    public void The_newest_package_of_a_group_is_never_offered_as_old()
    {
        // Both bound to a present device, so "unused" never applies; only the supersede rule decides.
        var devices = new[]
        {
            Device("A", "Device", "Sound", present: true, @"C:\Windows\INF\oem1.inf"),
            Device("B", "Device", "Sound", present: true, @"C:\Windows\INF\oem2.inf"),
        };
        var packages = new[]
        {
            Package("oem1.inf", "Acme", "Sound", "1.0.0.0", Now.AddYears(-2), "acmesnd.inf"),
            Package("oem2.inf", "Acme", "Sound", "2.0.0.0", Now.AddMonths(-1), "acmesnd.inf"),
        };

        var found = DriverCleanupDiscovery.DiscoverOldPackages(devices, packages);

        Assert.DoesNotContain(found, f => f.PackageName == "oem2.inf");
    }

    [Fact]
    public void Two_packages_from_different_providers_with_the_same_original_name_are_never_compared()
    {
        var packages = new[]
        {
            Package("oem1.inf", "Acme", "Sound", "1.0.0.0", Now.AddYears(-2), "driver.inf"),
            Package("oem2.inf", "OtherCo", "Sound", "2.0.0.0", Now.AddMonths(-1), "driver.inf"),
        };

        // Each is bound to no device, so both are offered as "unused"; neither is additionally flagged
        // "superseded" by the other provider's package.
        var found = DriverCleanupDiscovery.DiscoverOldPackages([], packages);

        Assert.Equal(2, found.Count);
    }

    [Fact]
    public void Packages_with_no_orderable_date_or_version_are_never_guessed_as_older_or_newer()
    {
        var devices = new[]
        {
            Device("A", "Device", "Sound", present: true, @"C:\Windows\INF\oem1.inf"),
            Device("B", "Device", "Sound", present: true, @"C:\Windows\INF\oem2.inf"),
        };
        var packages = new[]
        {
            Package("oem1.inf", "Acme", "Sound", null, null, "acmesnd.inf"),
            Package("oem2.inf", "Acme", "Sound", null, null, "acmesnd.inf"),
        };

        // Both are bound to a present device and neither can be confidently ordered against the other:
        // neither is offered.
        Assert.Empty(DriverCleanupDiscovery.DiscoverOldPackages(devices, packages));
    }

    [Theory]
    [InlineData("oem1.inf", true)]
    [InlineData("OEM42.INF", true)]
    [InlineData("oem.inf", false)]
    [InlineData("usb.inf", false)]
    [InlineData("oem1.pnf", false)]
    [InlineData("../oem1.inf", false)]
    [InlineData(null, false)]
    public void IsOemPackageName_only_accepts_the_pnputil_shape(string? name, bool expected) =>
        Assert.Equal(expected, DriverCleanupDiscovery.IsOemPackageName(name));
}
