using UltimateUpdater.Services;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

/// <summary>
/// TooltipText carries a translated "copy hint" suffix, so RefreshLocalizedText must announce it too,
/// otherwise a pill's error tooltip is left in the old language after a locale switch.
/// </summary>
public class ProviderStatusViewModelTests
{
    [Fact]
    public void RefreshLocalizedText_announces_TooltipText()
    {
        var pill = new ProviderStatusViewModel("winget") { ErrorMessage = "Something failed", Detail = "raw detail" };

        var raised = false;
        pill.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ProviderStatusViewModel.TooltipText))
                raised = true;
        };

        pill.RefreshLocalizedText();

        Assert.True(raised);
    }

    [Fact]
    public void RefreshLocalizedText_announces_StateText()
    {
        var pill = new ProviderStatusViewModel("winget");

        var raised = false;
        pill.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ProviderStatusViewModel.StateText))
                raised = true;
        };

        pill.RefreshLocalizedText();

        Assert.True(raised);
    }

    // ---- per-source scan progress + cancel ----

    [Fact]
    public void A_scanning_source_is_cancellable_and_first_in_running_order()
    {
        var pill = new ProviderStatusViewModel("winget");
        pill.Apply(new ProviderStatus { SourceId = "winget", State = ProviderScanState.Scanning });

        Assert.True(pill.IsCancellable);
        Assert.Equal(0, pill.RunningRank);
    }

    [Theory]
    [InlineData(ProviderScanState.Waiting)]
    [InlineData(ProviderScanState.Done)]
    [InlineData(ProviderScanState.Error)]
    [InlineData(ProviderScanState.Cancelled)]
    public void A_source_not_currently_scanning_is_not_cancellable_and_sorts_after_running_ones(ProviderScanState state)
    {
        var pill = new ProviderStatusViewModel("winget");
        pill.Apply(new ProviderStatus { SourceId = "winget", State = state });

        Assert.False(pill.IsCancellable);
        Assert.Equal(1, pill.RunningRank);
    }

    [Fact]
    public void Waiting_scanning_and_cancelled_each_show_their_own_state_word()
    {
        var pill = new ProviderStatusViewModel("winget");

        pill.Apply(new ProviderStatus { SourceId = "winget", State = ProviderScanState.Waiting });
        Assert.True(pill.HasStateText);

        pill.Apply(new ProviderStatus { SourceId = "winget", State = ProviderScanState.Scanning });
        Assert.True(pill.HasStateText);

        pill.Apply(new ProviderStatus { SourceId = "winget", State = ProviderScanState.Cancelled });
        Assert.True(pill.HasStateText);
    }

    /// <summary>A finished source already says enough with the found-count badge, no extra word needed, and
    /// the pill template would otherwise show empty text next to a full one.</summary>
    [Fact]
    public void Done_has_no_state_word()
    {
        var pill = new ProviderStatusViewModel("winget");
        pill.Apply(new ProviderStatus { SourceId = "winget", State = ProviderScanState.Done });

        Assert.False(pill.HasStateText);
        Assert.Null(pill.StateText);
    }

    /// <summary>A failed source says so in words (the old "!" meant nothing without its tooltip); the
    /// reason stays in the tooltip.</summary>
    [Fact]
    public void Error_shows_its_state_word()
    {
        var pill = new ProviderStatusViewModel("winget");
        pill.Apply(new ProviderStatus { SourceId = "winget", State = ProviderScanState.Error });

        Assert.True(pill.HasStateText);
        Assert.Equal(LocalizationService.Instance["ScanStateError"], pill.StateText);
        Assert.False(string.IsNullOrWhiteSpace(pill.StateText));
    }

    /// <summary>A cancelled source stays visible after the scan too, silently vanishing would look like
    /// the cancel click did nothing, the same reason an error stays visible.</summary>
    [Fact]
    public void A_cancelled_source_counts_as_noteworthy_so_it_stays_visible_after_the_scan()
    {
        var pill = new ProviderStatusViewModel("winget");
        pill.Apply(new ProviderStatus { SourceId = "winget", State = ProviderScanState.Cancelled });

        Assert.True(pill.IsNoteworthy);
    }

    // OnStateChanged stamps _scanningSince from the real clock (DateTimeOffset.UtcNow), not from a value
    // the test controls, so these measure elapsed time from a "since" captured right after Apply(),
    // rather than from an arbitrary fixed instant.

    [Fact]
    public void RefreshSlowState_flips_IsSlow_only_after_the_threshold_and_only_while_scanning()
    {
        var pill = new ProviderStatusViewModel("winget");
        pill.Apply(new ProviderStatus { SourceId = "winget", State = ProviderScanState.Scanning });
        var since = DateTimeOffset.UtcNow;

        pill.RefreshSlowState(since.AddSeconds(5));
        Assert.False(pill.IsSlow);

        pill.RefreshSlowState(since.AddSeconds(31));
        Assert.True(pill.IsSlow);
        Assert.Contains("…", pill.StateText); // still a running word, just the "slow" variant
    }

    /// <summary>Leaving Scanning resets the slow flag, a source that finishes and starts again (a
    /// second member of the same category aggregate) is timed fresh, not against its very first start.</summary>
    [Fact]
    public void Finishing_a_scan_resets_IsSlow()
    {
        var pill = new ProviderStatusViewModel("winget");
        pill.Apply(new ProviderStatus { SourceId = "winget", State = ProviderScanState.Scanning });
        var since = DateTimeOffset.UtcNow;
        pill.RefreshSlowState(since.AddSeconds(60));
        Assert.True(pill.IsSlow);

        pill.Apply(new ProviderStatus { SourceId = "winget", State = ProviderScanState.Done });

        Assert.False(pill.IsSlow);
    }

}
