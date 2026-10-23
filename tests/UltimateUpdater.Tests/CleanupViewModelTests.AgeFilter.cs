using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

public partial class CleanupViewModelTests
{
    [Fact]
    public void The_age_filter_defaults_to_the_settings_value_of_30_days()
    {
        var recycleBin = Find(Create(CreateSettings()), "recycle-bin");

        Assert.Equal(30, recycleBin.RecycleBinMinAgeDays);
        Assert.True(recycleBin.RecycleBinAgeFilterEnabled);
    }

    /// <summary>Loading the initial value must not itself count as a change: it goes straight onto the
    /// row without going through the settings-save-and-rescan path.</summary>
    [Fact]
    public void Only_the_recycle_bin_row_carries_a_non_zero_default()
    {
        var viewModel = Create(CreateSettings());

        foreach (var item in viewModel.Items.Where(i => i.Target.Kind != CleanupKind.RecycleBin))
            Assert.Equal(0, item.RecycleBinMinAgeDays);
    }

    [Fact]
    public void Setting_the_day_count_is_saved_and_survives_reopening()
    {
        var settings = CreateSettings();
        var first = Create(settings);

        Find(first, "recycle-bin").RecycleBinMinAgeDays = 45;

        Assert.Equal(45, settings.Current.CleanupRecycleBinMinAgeDays);

        var second = Create(settings);
        Assert.Equal(45, Find(second, "recycle-bin").RecycleBinMinAgeDays);
    }

    [Fact]
    public void Zero_days_means_the_filter_checkbox_reads_as_off()
    {
        var recycleBin = Find(Create(CreateSettings()), "recycle-bin");

        recycleBin.RecycleBinMinAgeDays = 0;

        Assert.False(recycleBin.RecycleBinAgeFilterEnabled);
    }

    /// <summary>Unchecking the sub-row must not throw away the number the user had typed: it goes to 0
    /// while off, and comes straight back once the box is checked again.</summary>
    [Fact]
    public void Unchecking_then_rechecking_the_filter_restores_the_day_count_it_had()
    {
        var recycleBin = Find(Create(CreateSettings()), "recycle-bin");
        recycleBin.RecycleBinMinAgeDays = 60;

        recycleBin.RecycleBinAgeFilterEnabled = false;
        Assert.Equal(0, recycleBin.RecycleBinMinAgeDays);

        recycleBin.RecycleBinAgeFilterEnabled = true;
        Assert.Equal(60, recycleBin.RecycleBinMinAgeDays);
    }

    /// <summary>A settings.json that already carries 0 (an explicit "off" from a previous session) must
    /// still offer a sane, non-zero number the moment the checkbox is ticked, not a stuck 0.</summary>
    [Fact]
    public void Checking_the_filter_from_a_settings_file_already_at_zero_offers_the_default()
    {
        var settings = CreateSettings();
        settings.Current.CleanupRecycleBinMinAgeDays = 0;
        var recycleBin = Find(Create(settings), "recycle-bin");
        Assert.False(recycleBin.RecycleBinAgeFilterEnabled);

        recycleBin.RecycleBinAgeFilterEnabled = true;

        Assert.Equal(30, recycleBin.RecycleBinMinAgeDays);
    }

    /// <summary>
    /// StartScanAsync always runs fire-and-forget (the window's Loaded handler, RescanCommand),
    /// so nothing ever awaits its Task; an exception escaping it used to have nowhere to go but the
    /// process-wide unobserved-task handler, leaving IsScanning stuck true and the rescan button disabled
    /// for the rest of the session. A real scan against a target this process cannot see still completes:
    /// the per-item catch already caught individual target failures, and the run must always leave the
    /// dialog usable, not spinning, whatever any one target does.
    /// </summary>
    [Fact]
    public async Task A_scan_always_leaves_the_dialog_usable_even_when_a_target_cannot_be_measured()
    {
        var viewModel = Create(CreateSettings(), service: new InstantCleanupService(new LoggingService()));

        await viewModel.StartScanAsync();

        Assert.False(viewModel.IsScanning);
        Assert.False(viewModel.IsBusy);
        Assert.True(viewModel.RescanCommand.CanExecute(null));
    }

    /// <summary>
    /// The constructor used to build the whole catalogue inline, including a WMI lookup per
    /// drive and a folder-existence check for every candidate directory, blocking the cleanup window's
    /// first paint. It now only starts that work in the background and returns at once; this is the seam
    /// proving the constructor itself does none of it.
    /// </summary>
    [Fact]
    public void Construction_returns_before_the_catalogue_is_built()
    {
        var logging = new LoggingService();
        var viewModel = new CleanupViewModel(new CleanupService(logging), logging, CreateSettings());

        Assert.True(viewModel.IsCatalogLoading);
        Assert.Empty(viewModel.Items);
        Assert.Empty(viewModel.Groups);
    }

    /// <summary>The other half of the seam above: once the background build actually finishes, Items and
    /// Groups are populated and the loading state clears. Uses <see cref="WpfHarness.WaitForCleanupCatalog"/>
    /// rather than a bare await: the load task's own final step is queued onto this thread's own
    /// dispatcher, which nothing here pumps otherwise.</summary>
    [Fact]
    public void CatalogReadyTask_populates_items_and_groups()
    {
        var logging = new LoggingService();
        var viewModel = new CleanupViewModel(new CleanupService(logging), logging, CreateSettings());

        WpfHarness.WaitForCleanupCatalog(viewModel);

        Assert.False(viewModel.IsCatalogLoading);
        Assert.NotEmpty(viewModel.Items);
        Assert.NotEmpty(viewModel.Groups);
    }

    /// <summary>Disk Cleanup never reports its own freed bytes (cleanmgr says nothing), so its share is
    /// read back as whatever the real drive gained beyond what the other lanes already counted for sure.</summary>
    [Fact]
    public void ComputeLaneOverlapExtraBytes_is_the_drive_delta_minus_the_counted_total()
    {
        Assert.Equal(300L, CleanupViewModel.ComputeLaneOverlapExtraBytes(1_000L, 1_500L, 200L));
    }

    /// <summary>Either reading missing means no real measurement exists at all, never a guess.</summary>
    [Fact]
    public void ComputeLaneOverlapExtraBytes_is_null_when_either_reading_is_missing()
    {
        Assert.Null(CleanupViewModel.ComputeLaneOverlapExtraBytes(null, 1_500L, 200L));
        Assert.Null(CleanupViewModel.ComputeLaneOverlapExtraBytes(1_000L, null, 200L));
    }

    /// <summary>A regular target's size column always has something to show once scanning/waiting are
    /// both done: a figure or "nothing found", never a reserved-but-blank cell.</summary>
    [Fact]
    public void HasSizeCell_is_true_for_an_ordinary_target_whether_or_not_it_found_anything()
    {
        var viewModel = Create(CreateSettings());
        var item = viewModel.Items.First(i => i.Target.Kind == CleanupKind.Directories);
        item.IsScanning = false;

        item.Bytes = 0;
        Assert.True(item.HasSizeCell);

        item.Bytes = 1024;
        Assert.True(item.HasSizeCell);
    }

    /// <summary>Disk Cleanup and system repair never report a size in advance, so once scanning is done
    /// their size column has nothing to show at all and must collapse rather than sit there as an empty
    /// box beside the row's risk dot.</summary>
    [Fact]
    public void HasSizeCell_is_false_once_scanning_ends_for_a_target_with_no_advance_size()
    {
        var viewModel = Create(CreateSettings());
        var item = viewModel.Items.First(i => i.Target.Kind == CleanupKind.DiskCleanup);

        Assert.True(item.HasSizeCell, "still scanning: the measuring dots occupy the cell.");

        item.IsScanning = false;
        Assert.False(item.HasSizeCell);
    }

    /// <summary>The counted total can already account for everything the drive gained (or more, if
    /// something else grew the drive's usage in the meantime), a negative or zero result must never be
    /// shown as a positive "Disk Cleanup freed this" figure.</summary>
    [Fact]
    public void ComputeLaneOverlapExtraBytes_can_be_zero_or_negative()
    {
        Assert.Equal(0L, CleanupViewModel.ComputeLaneOverlapExtraBytes(1_000L, 1_200L, 200L));
        Assert.Equal(-100L, CleanupViewModel.ComputeLaneOverlapExtraBytes(1_000L, 1_100L, 200L));
    }
}
