using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

/// <summary>Drive optimisation, run once per execution through the elevated worker, one row per
/// fixed drive whose media type could actually be determined.</summary>
public class DriveDefragTests
{
    [Theory]
    [InlineData(3, DriveMediaType.Hdd)]
    [InlineData(4, DriveMediaType.Ssd)]
    [InlineData(0, DriveMediaType.Unspecified)]
    [InlineData(1, DriveMediaType.Unknown)]
    [InlineData(2, DriveMediaType.Unknown)]
    [InlineData(-1, DriveMediaType.Unknown)]
    public void FromWmiMediaType_maps_the_documented_MSFT_PhysicalDisk_values(int raw, DriveMediaType expected)
    {
        Assert.Equal(expected, DriveMediaTypeReader.FromWmiMediaType(raw));
    }

    /// <summary>A drive whose type could not be read must never surface as a row at all (this fix: "never
    /// guess between the two"); this is the one call site that turns Unknown into "not offered", proven
    /// against the real catalogue rather than a fake, since building the catalogue IS what decides this.</summary>
    [Fact]
    public void The_real_catalogue_never_offers_a_defrag_row_with_an_undetermined_media_type()
    {
        var targets = CleanupCatalog.Build().Where(t => t.Kind == CleanupKind.Defragmentation).ToList();

        Assert.All(targets, t => Assert.NotEqual(DriveMediaType.Unknown, t.MediaType));
    }

    /// <summary>Every defrag row starts unticked and needs elevation, whatever this machine's own drives
    /// turn out to be: a multi-hour run earns no default trust, same reasoning already applied to system
    /// repair and Disk Cleanup.</summary>
    [Fact]
    public void Every_real_defrag_target_starts_off_and_needs_elevation()
    {
        var targets = CleanupCatalog.Build().Where(t => t.Kind == CleanupKind.Defragmentation).ToList();

        Assert.All(targets, t =>
        {
            Assert.False(t.DefaultEnabled);
            Assert.True(t.NeedsElevation);
            Assert.NotNull(t.DriveLetter);
            Assert.Single(t.TitleArguments);
        });
    }

    [Fact]
    public void CleanupGroups_places_defragmentation_targets_with_the_other_windows_maintenance_rows()
    {
        var target = new CleanupTarget(
            "defrag-x", "CleanupDefragRowHdd", "MediaTypeHdd", CleanupKind.Defragmentation,
            DefaultEnabled: false, RiskLevel: CleanupRiskLevel.Considered, ConsequenceKey: "CleanupDefragConsequence",
            NeedsElevation: true);

        Assert.Equal(CleanupGroupKey.WindowsMaintenance, CleanupGroups.For(target));
    }

    /// <summary>The row's own title carries the action and a single display string (volume label plus
    /// drive letter); a target with no arguments (every OTHER kind) must still fall back to a
    /// plain key lookup, unaffected by this.</summary>
    [Theory]
    [InlineData(DriveMediaType.Hdd, "CleanupDefragRowHdd")]
    [InlineData(DriveMediaType.Ssd, "CleanupDefragRowSsd")]
    [InlineData(DriveMediaType.Unspecified, "CleanupDefragRowUnspecified")]
    public void The_row_title_formats_the_single_display_name(DriveMediaType mediaType, string titleKey)
    {
        var target = new CleanupTarget(
            "defrag-c", titleKey, "x", CleanupKind.Defragmentation,
            DefaultEnabled: false, RiskLevel: CleanupRiskLevel.Considered, ConsequenceKey: "x", NeedsElevation: true)
        {
            TitleArguments = ["Windows C:\\"],
            DriveLetter = "C:",
            MediaType = mediaType,
        };

        var expected = string.Format(LocalizationService.Instance[titleKey], "Windows C:\\");
        Assert.Equal(expected, new CleanupItemViewModel(target, () => { }).Title);
    }

    /// <summary>Every branch of <see cref="CleanupCatalog.BuildDriveDefragTargets"/>, driven from
    /// fabricated facts rather than a real drive or WMI call. Covers: SSD, HDD, Unspecified-not-USB,
    /// Unspecified-USB-Removable, USB-SSD (row present or absent, title key, exact title string with and
    /// without a volume label, exact sub-line key and arguments) and Unknown (no row at all).</summary>
    [Fact]
    public void BuildDriveDefragTargets_offers_a_fixed_ssd_with_a_volume_label()
    {
        var facts = new[] { new CleanupCatalog.DriveFacts("C:", "System", DriveType.Fixed, 2_000_000_000_000, DriveMediaType.Ssd, IsUsb: false) };

        var target = Assert.Single(CleanupCatalog.BuildDriveDefragTargets(facts));

        Assert.Equal("CleanupDefragRowSsd", target.TitleKey);
        Assert.Equal(["System C:\\"], target.TitleArguments);
        Assert.Equal("CleanupDriveSubline", target.DescriptionKey);
        Assert.Equal(["MediaTypeSsd"], target.DescriptionWordKeys);
        Assert.Equal(2_000_000_000_000, target.DescriptionBytes);
    }

    [Fact]
    public void BuildDriveDefragTargets_offers_a_fixed_hdd_without_a_volume_label()
    {
        var facts = new[] { new CleanupCatalog.DriveFacts("F:", "", DriveType.Fixed, 1_000_000_000_000, DriveMediaType.Hdd, IsUsb: false) };

        var target = Assert.Single(CleanupCatalog.BuildDriveDefragTargets(facts));

        Assert.Equal("CleanupDefragRowHdd", target.TitleKey);
        Assert.Equal(["F:\\"], target.TitleArguments);
        Assert.Equal("CleanupDriveSubline", target.DescriptionKey);
        Assert.Equal(["MediaTypeHdd"], target.DescriptionWordKeys);
    }

    [Fact]
    public void BuildDriveDefragTargets_offers_an_unspecified_non_usb_fixed_drive()
    {
        var facts = new[] { new CleanupCatalog.DriveFacts("G:", "", DriveType.Fixed, 500_000_000_000, DriveMediaType.Unspecified, IsUsb: false) };

        var target = Assert.Single(CleanupCatalog.BuildDriveDefragTargets(facts));

        Assert.Equal("CleanupDefragRowUnspecified", target.TitleKey);
        Assert.Equal("CleanupDriveSubline", target.DescriptionKey);
        Assert.Equal(["MediaTypeOtherDrive"], target.DescriptionWordKeys);
    }

    [Fact]
    public void BuildDriveDefragTargets_offers_an_unspecified_usb_removable_drive()
    {
        var facts = new[] { new CleanupCatalog.DriveFacts("D:", "DataNeu", DriveType.Removable, 14_900_000_000_000, DriveMediaType.Unspecified, IsUsb: true) };

        var target = Assert.Single(CleanupCatalog.BuildDriveDefragTargets(facts));

        Assert.Equal("CleanupDefragRowUnspecified", target.TitleKey);
        Assert.Equal(["DataNeu D:\\"], target.TitleArguments);
        Assert.Equal("CleanupDriveSubline", target.DescriptionKey);
        Assert.Equal(["MediaTypeUsbDrive"], target.DescriptionWordKeys);
        Assert.True(target.NeedsElevation);
        Assert.False(target.DefaultEnabled);
    }

    [Fact]
    public void BuildDriveDefragTargets_offers_a_usb_ssd_with_the_two_word_subline()
    {
        var facts = new[] { new CleanupCatalog.DriveFacts("E:", "Backup", DriveType.Removable, 1_800_000_000_000, DriveMediaType.Ssd, IsUsb: true) };

        var target = Assert.Single(CleanupCatalog.BuildDriveDefragTargets(facts));

        Assert.Equal("CleanupDefragRowSsd", target.TitleKey);
        Assert.Equal("CleanupDriveSublineUsb", target.DescriptionKey);
        Assert.Equal(["MediaTypeUsbDrive", "MediaTypeSsd"], target.DescriptionWordKeys);
    }

    [Fact]
    public void BuildDriveDefragTargets_never_offers_a_row_for_Unknown()
    {
        var facts = new[] { new CleanupCatalog.DriveFacts("H:", "", DriveType.Fixed, 100, DriveMediaType.Unknown, IsUsb: false) };

        Assert.Empty(CleanupCatalog.BuildDriveDefragTargets(facts));
    }

    /// <summary>A non-USB removable drive (a plain USB stick WMI could not identify as a USB bus, or a
    /// genuinely different removable medium) is still never offered, whatever its media type reads as
    /// (this fix's exception is USB specifically, not "removable" generally).</summary>
    [Fact]
    public void BuildDriveDefragTargets_never_offers_a_non_usb_removable_drive()
    {
        var facts = new[] { new CleanupCatalog.DriveFacts("I:", "", DriveType.Removable, 100, DriveMediaType.Ssd, IsUsb: false) };

        Assert.Empty(CleanupCatalog.BuildDriveDefragTargets(facts));
    }

    /// <summary>The reader used to swallow every failure silently; it now logs one line per drive,
    /// success or failure, through the swappable <see cref="DriveMediaTypeReader.Log"/> seam. Both seams are
    /// reset in <c>finally</c> so a failed assertion never leaks into a later test.</summary>
    [Fact]
    public void TryGetMediaType_logs_a_success_line_with_the_raw_value_and_the_mapped_type()
    {
        var lines = new List<string>();
        DriveMediaTypeReader.Log = lines.Add;
        DriveMediaTypeReader.QueryRawMediaType = _ => 4;

        try
        {
            var result = DriveMediaTypeReader.TryGetMediaType("Z-170-success:");

            Assert.Equal(DriveMediaType.Ssd, result);
            Assert.Contains(lines, l => l.Contains("Z-170-success:") && l.Contains("4") && l.Contains("Ssd"));
        }
        finally
        {
            DriveMediaTypeReader.Log = _ => { };
            DriveMediaTypeReader.QueryRawMediaType = DriveMediaTypeReader.RealQueryForTests;
        }
    }

    [Fact]
    public void TryGetMediaType_logs_when_no_physical_disk_could_be_resolved()
    {
        var lines = new List<string>();
        DriveMediaTypeReader.Log = lines.Add;
        DriveMediaTypeReader.QueryRawMediaType = _ => null;

        try
        {
            var result = DriveMediaTypeReader.TryGetMediaType("Z-170-nodisk:");

            Assert.Equal(DriveMediaType.Unknown, result);
            Assert.Contains(lines, l => l.Contains("Z-170-nodisk:") && l.Contains("no physical disk"));
        }
        finally
        {
            DriveMediaTypeReader.Log = _ => { };
            DriveMediaTypeReader.QueryRawMediaType = DriveMediaTypeReader.RealQueryForTests;
        }
    }

    /// <summary>Every exception kind the reader now catches (the original three plus the three this fix
    /// added) degrades to Unknown and still logs, instead of the query throwing out of the catalogue build.</summary>
    [Theory]
    [InlineData(typeof(System.Management.ManagementException))]
    [InlineData(typeof(System.Runtime.InteropServices.COMException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(FormatException))]
    [InlineData(typeof(InvalidCastException))]
    [InlineData(typeof(DllNotFoundException))]
    [InlineData(typeof(PlatformNotSupportedException))]
    public void TryGetMediaType_degrades_to_Unknown_and_logs_for_every_caught_exception_kind(Type exceptionType)
    {
        var lines = new List<string>();
        DriveMediaTypeReader.Log = lines.Add;
        var driveLetter = $"Z-170-{exceptionType.Name}:";
        DriveMediaTypeReader.QueryRawMediaType = _ =>
            throw (Exception)Activator.CreateInstance(exceptionType)!;

        try
        {
            var result = DriveMediaTypeReader.TryGetMediaType(driveLetter);

            Assert.Equal(DriveMediaType.Unknown, result);
            Assert.Contains(lines, l => l.Contains(driveLetter) && l.Contains(exceptionType.Name));
        }
        finally
        {
            DriveMediaTypeReader.Log = _ => { };
            DriveMediaTypeReader.QueryRawMediaType = DriveMediaTypeReader.RealQueryForTests;
        }
    }

    /// <summary><see cref="TypeInitializationException"/> has no parameterless constructor, so it gets its
    /// own test rather than the theory above.</summary>
    [Fact]
    public void TryGetMediaType_degrades_to_Unknown_and_logs_for_TypeInitializationException()
    {
        var lines = new List<string>();
        DriveMediaTypeReader.Log = lines.Add;
        DriveMediaTypeReader.QueryRawMediaType = _ =>
            throw new TypeInitializationException("SomeType", new InvalidOperationException("boom"));

        try
        {
            var result = DriveMediaTypeReader.TryGetMediaType("Z-170-TypeInit:");

            Assert.Equal(DriveMediaType.Unknown, result);
            Assert.Contains(lines, l => l.Contains("Z-170-TypeInit:") && l.Contains("TypeInitializationException"));
        }
        finally
        {
            DriveMediaTypeReader.Log = _ => { };
            DriveMediaTypeReader.QueryRawMediaType = DriveMediaTypeReader.RealQueryForTests;
        }
    }

    /// <summary>
    /// Before this fix, a drive pulled or a share turned restricted between <c>DriveInfo.IsReady</c> and a
    /// property read such as <c>TotalSize</c> threw an <see cref="IOException"/> or <see
    /// cref="UnauthorizedAccessException"/> straight out of <c>CleanupCatalog.DriveDefragTargets</c>,
    /// faulting the whole catalogue build (<c>CleanupViewModel.LoadCatalogAsync</c> had no try around it,
    /// rethrown in the window's async void <c>Loaded</c> handler, which took the app down). <see
    /// cref="CleanupCatalog.GatherDriveFacts{TDrive}"/> is the seam this proves against: <see
    /// cref="DriveInfo"/> is sealed and cannot be made to throw from a test, so this drives it from a plain
    /// fake list instead.
    /// </summary>
    [Fact]
    public void GatherDriveFacts_skips_and_logs_a_drive_that_throws_instead_of_faulting_the_whole_build()
    {
        var warnings = new List<string>();

        var facts = CleanupCatalog.GatherDriveFacts(
            ["C", "D", "E"],
            drive => drive switch
            {
                "C" => new CleanupCatalog.DriveFacts("C:", "System", DriveType.Fixed, 2_000_000_000_000, DriveMediaType.Ssd, IsUsb: false),
                "D" => throw new IOException("simulated: the drive was removed"),
                "E" => new CleanupCatalog.DriveFacts("E:", "Data", DriveType.Fixed, 1_000_000_000_000, DriveMediaType.Hdd, IsUsb: false),
                _ => throw new InvalidOperationException("unreachable"),
            },
            warnings.Add);

        // The good drives either side of the bad one both still made it through - the exception skipped
        // exactly one drive, not the rest of the gathering.
        Assert.Equal(2, facts.Count);
        Assert.Contains(facts, f => f.Letter == "C:");
        Assert.Contains(facts, f => f.Letter == "E:");

        Assert.Single(warnings);
        Assert.Contains("removed", warnings[0]);
    }

    /// <summary>The same skip for the other exception a restricted share raises.</summary>
    [Fact]
    public void GatherDriveFacts_also_skips_a_drive_that_throws_UnauthorizedAccessException()
    {
        var warnings = new List<string>();

        var facts = CleanupCatalog.GatherDriveFacts(
            ["C"], _ => throw new UnauthorizedAccessException("simulated: access denied"), warnings.Add);

        Assert.Empty(facts);
        Assert.Single(warnings);
    }

    /// <summary>A caller that passes no logger (every real call site except the view model's own) must not
    /// throw for that either - <c>logWarning</c> is genuinely optional.</summary>
    [Fact]
    public void GatherDriveFacts_never_requires_a_logger()
    {
        var facts = CleanupCatalog.GatherDriveFacts(
            ["C"], _ => throw new IOException("simulated"), logWarning: null);

        Assert.Empty(facts);
    }
}
