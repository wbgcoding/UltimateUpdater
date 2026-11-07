using UltimateUpdater.Services.Sources;
using UltimateUpdater.Services.Sources.PackageManagers;
using Xunit;

namespace UltimateUpdater.Tests;

/// <summary>
/// pip and sdkmanager print nothing until their whole listing is ready. Both were reported as failed
/// on a real PC after a minute of silence while still working (pip measured at 107 s alone), so their
/// silence limit must sit well above the shared default and above that measurement.
/// </summary>
public class SilentListingTimeoutTests
{
    [Fact]
    public void Pip_waits_longer_than_the_measured_silent_listing()
    {
        Assert.True(PipSource.OutdatedListLimit > ExternalCallTimeouts.ScanIdle);
        Assert.True(PipSource.OutdatedListLimit >= TimeSpan.FromSeconds(107 * 3));
    }

    [Fact]
    public void Android_sdk_listing_gets_more_than_the_default_minute_of_silence()
    {
        Assert.True(AndroidSdkSource.ListingSilenceLimit > ExternalCallTimeouts.ScanIdle * 2);
    }

    [Fact]
    public void Both_limits_stay_inside_the_overall_source_deadline()
    {
        Assert.True(PipSource.OutdatedListLimit < ExternalCallTimeouts.ScanDeadline);
        Assert.True(AndroidSdkSource.ListingSilenceLimit < ExternalCallTimeouts.ScanDeadline);
    }
}
