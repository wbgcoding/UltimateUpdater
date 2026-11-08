using System.Threading;
using UltimateUpdater.Services;
using static UltimateUpdater.Services.InstallerCloseRequest;

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
    public void An_idle_app_agrees_to_the_installers_close_request()
    {
        Assert.Equal(CloseAnswer.Exit, InstallerCloseRequest.Decide(WmQueryEndSession, new IntPtr(0x1), busy: false));
    }

    [Fact]
    public void A_busy_app_refuses_the_installers_close_request()
    {
        Assert.Equal(CloseAnswer.Refuse, InstallerCloseRequest.Decide(WmQueryEndSession, new IntPtr(0x1), busy: true));
    }

    [Theory]
    [InlineData(WmQueryEndSession, 0x0L, true)]
    [InlineData(WmQueryEndSession, 0x80000000L, true)]
    [InlineData(WmEndSession, 0x1L, true)]
    [InlineData(WmClose, 0x1L, false)]
    public void Everything_else_is_left_to_the_normal_handling(int message, long flags, bool busy)
    {
        Assert.Equal(CloseAnswer.Ignore, InstallerCloseRequest.Decide(message, new IntPtr(flags), busy));
    }

    [Fact]
    public void A_busy_app_answers_the_request_with_zero_and_stays_up()
    {
        var exits = 0;
        var refusals = 0;
        var handled = false;

        var result = InstallerCloseRequest.Respond(
            WmQueryEndSession, new IntPtr(0x1), busy: true, exit: () => exits++, refused: () => refusals++, ref handled);

        Assert.True(handled);
        Assert.Equal(IntPtr.Zero, result);
        Assert.Equal(0, exits);
        Assert.Equal(1, refusals);
    }

    [Fact]
    public void An_idle_app_answers_the_request_with_one_and_exits()
    {
        var exits = 0;
        var handled = false;

        var result = InstallerCloseRequest.Respond(
            WmQueryEndSession, new IntPtr(0x1), busy: false, exit: () => exits++, refused: () => { }, ref handled);

        Assert.True(handled);
        Assert.Equal(new IntPtr(1), result);
        Assert.Equal(1, exits);
    }

    [Fact]
    public void An_unrelated_message_is_not_handled()
    {
        var handled = false;

        var result = InstallerCloseRequest.Respond(
            WmClose, new IntPtr(0x1), busy: true, exit: () => { }, refused: () => { }, ref handled);

        Assert.False(handled);
        Assert.Equal(IntPtr.Zero, result);
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

    /// <summary>Another object of a different kind already uses the name: raising the signal must not throw
    /// (it runs on the UI timer), and it works again once the name is free.</summary>
    [Fact]
    public void A_squatted_busy_signal_name_does_not_crash_and_recovers_when_freed()
    {
        var sid = "test-" + Guid.NewGuid().ToString("N");
        using var signal = new BusySignal(sid);
        var squatter = new EventWaitHandle(false, EventResetMode.ManualReset, BusySignal.NamePrefix + sid);

        signal.Set(true);
        signal.Set(true);

        squatter.Dispose();
        signal.Set(false);
        signal.Set(true);
        Assert.True(Exists(BusySignal.NamePrefix + sid));
        signal.Set(false);
    }

    [Fact]
    public void The_busy_signal_defaults_to_the_current_users_sid()
    {
        using var signal = new BusySignal();

        Assert.StartsWith(BusySignal.NamePrefix + "S-1-5-", signal.Name);
    }

    private static bool Exists(string name)
    {
        // The installer's probe opens with the same single right; the DACL grants the user nothing more.
        if (!System.Threading.MutexAcl.TryOpenExisting(name, System.Security.AccessControl.MutexRights.Synchronize, out var found))
            return false;

        found.Dispose();
        return true;
    }
}
