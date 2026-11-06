using System.Threading;
using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

public class InstallerCloseRequestTests
{
    private const int WmQueryEndSession = 0x0011;
    private const int WmEndSession = 0x0016;
    private const int WmClose = 0x0010;

    [Fact]
    public void The_installers_close_request_is_recognised()
    {
        Assert.True(InstallerCloseRequest.IsCloseRequest(WmQueryEndSession, new IntPtr(0x1)));
    }

    [Theory]
    [InlineData(0x0)]            // shutdown
    [InlineData(0x80000000L)]    // logoff
    [InlineData(0x40000000L)]    // critical
    public void A_plain_shutdown_or_logoff_is_not_an_installer_request(long flags)
    {
        Assert.False(InstallerCloseRequest.IsCloseRequest(WmQueryEndSession, new IntPtr(flags)));
    }

    [Fact]
    public void A_logoff_that_also_carries_the_close_flag_is_still_a_close_request()
    {
        Assert.True(InstallerCloseRequest.IsCloseRequest(WmQueryEndSession, new IntPtr(0x80000001L)));
    }

    [Theory]
    [InlineData(WmEndSession)]
    [InlineData(WmClose)]
    [InlineData(0)]
    public void Other_messages_are_ignored_even_with_the_flag_set(int message)
    {
        Assert.False(InstallerCloseRequest.IsCloseRequest(message, new IntPtr(0x1)));
    }

    [Fact]
    public void The_busy_signal_exists_exactly_while_it_is_raised()
    {
        var name = BusySignal.NamePrefix + "test-" + Guid.NewGuid().ToString("N");
        using var signal = new BusySignal(name[BusySignal.NamePrefix.Length..]);

        Assert.False(Exists(name));

        signal.Set(true);
        Assert.True(Exists(name));

        signal.Set(true);
        Assert.True(Exists(name));

        signal.Set(false);
        Assert.False(Exists(name));

        signal.Set(false);
        Assert.False(Exists(name));
    }

    [Fact]
    public void The_busy_signal_is_dropped_when_it_is_disposed_while_raised()
    {
        var sid = "test-" + Guid.NewGuid().ToString("N");
        var signal = new BusySignal(sid);
        signal.Set(true);

        signal.Dispose();

        Assert.False(Exists(BusySignal.NamePrefix + sid));
    }

    [Fact]
    public void The_busy_signal_can_be_dropped_from_another_thread()
    {
        var sid = "test-" + Guid.NewGuid().ToString("N");
        using var signal = new BusySignal(sid);
        signal.Set(true);

        var thread = new Thread(() => signal.Set(false));
        thread.Start();
        thread.Join();

        Assert.False(Exists(BusySignal.NamePrefix + sid));
    }

    [Fact]
    public void The_busy_signal_defaults_to_the_current_users_sid()
    {
        using var signal = new BusySignal();

        Assert.StartsWith(BusySignal.NamePrefix + "S-1-5-", signal.Name);
    }

    private static bool Exists(string name)
    {
        if (!Mutex.TryOpenExisting(name, out var found))
            return false;

        found.Dispose();
        return true;
    }
}
