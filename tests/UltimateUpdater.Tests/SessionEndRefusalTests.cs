using Xunit;

namespace UltimateUpdater.Tests;

/// <summary>
/// An installer's close request reaches WPF's own hidden window as a session-end query, and WPF shuts
/// the app down unless the query is cancelled. A running install or cleanup must refuse it; a real
/// logoff or shutdown is never held up.
/// </summary>
public class SessionEndRefusalTests
{
    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    public void Only_a_close_request_during_running_work_is_refused(bool workRunning, bool sessionReallyEnding, bool refused) =>
        Assert.Equal(refused, App.ShouldRefuseSessionEnd(workRunning, sessionReallyEnding));
}
