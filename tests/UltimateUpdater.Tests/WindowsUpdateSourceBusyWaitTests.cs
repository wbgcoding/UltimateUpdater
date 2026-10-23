using System.Diagnostics;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

/// <summary>
/// <see cref="WindowsUpdateSource.WaitWhileBusy"/> backs off in 15-second steps while the shared WUA
/// installer is busy. A `Thread.Sleep(15s)` version only re-checks cancellation once the FULL sleep
/// has elapsed, so stopping a queued batch during this wait sat unresponsive for up to 15 seconds.
/// </summary>
public class WindowsUpdateSourceBusyWaitTests
{
    /// <summary>Late-bound like the real WUA installer object, WaitWhileBusy reads "IsBusy" via
    /// reflection (InvokeMember), which works on a plain property just as well as on a COM object.</summary>
    public sealed class AlwaysBusyInstaller
    {
        public bool IsBusy => true;
    }

    [Fact]
    public void A_cancellation_during_the_backoff_is_noticed_within_a_second_not_fifteen()
    {
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(200));

        var sw = Stopwatch.StartNew();
        Assert.Throws<OperationCanceledException>(() =>
            WindowsUpdateSource.WaitWhileBusy(new AlwaysBusyInstaller(), cts.Token));
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 2000,
            $"cancellation took {sw.ElapsedMilliseconds} ms to be noticed, expected well under one backoff step");
    }
}
