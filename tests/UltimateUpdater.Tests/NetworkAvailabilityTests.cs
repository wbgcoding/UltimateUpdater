using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

/// <summary>
/// NetworkAvailability.IsOnline is a thin, fail-open wrapper around a COM probe; there is no fake network
/// adapter to drive it against here, so this only proves the contract every caller actually relies on:
/// it always returns rather than throwing or hanging, whatever this machine's real connectivity is.
/// The behavioural cases (offline skips network sources, online does not) are covered against an
/// injected probe in ScanOrchestratorTests, exactly as the plan's own acceptance criteria ask for.
/// </summary>
public class NetworkAvailabilityTests
{
    [Fact]
    public void IsOnline_returns_without_throwing()
    {
        var exception = Record.Exception(() => NetworkAvailability.IsOnline());

        Assert.Null(exception);
    }

    [Fact]
    public void IsOnline_is_stable_across_repeated_calls()
    {
        // The COM object is created and released fresh every call; nothing left behind should make a
        // second call behave differently from the first.
        var first = NetworkAvailability.IsOnline();
        var second = NetworkAvailability.IsOnline();

        Assert.Equal(first, second);
    }
}
