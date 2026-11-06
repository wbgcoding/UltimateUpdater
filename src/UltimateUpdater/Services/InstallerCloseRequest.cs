using System.Windows.Interop;
using System.Windows.Threading;

namespace UltimateUpdater.Services;

/// <summary>
/// Setup (and any other installer using the Restart Manager) asks a running app to go away with
/// WM_QUERYENDSESSION and the ENDSESSION_CLOSEAPP flag. WPF turns that into the same session-ending event as
/// a real logoff or shutdown, so the app cannot tell them apart there. This reads the raw message instead:
/// a tray-resident app that merely hides on "close" would otherwise answer "fine" and stay alive, and the
/// installer would find its files still in use.
/// </summary>
internal static class InstallerCloseRequest
{
    /// <summary>What the app does with a window message.</summary>
    internal enum CloseAnswer
    {
        /// <summary>Not the installer's request: normal handling goes on.</summary>
        Ignore,

        /// <summary>The app is idle: agree and exit.</summary>
        Exit,

        /// <summary>Work is running: do not exit, answer "not now".</summary>
        Refuse,
    }

    private const int WmQueryEndSession = 0x0011;
    private const long EndSessionCloseApp = 0x1;

    /// <summary>True for the installer's request to close the app, false for every other message and for a
    /// plain logoff or shutdown, which keeps its own handling.</summary>
    internal static bool IsCloseRequest(int message, IntPtr lParam) =>
        message == WmQueryEndSession && (lParam.ToInt64() & EndSessionCloseApp) != 0;

    /// <summary>The answer to a message: a close request while work is running must not end the app, or an
    /// installer started by someone else would cut off a cleanup or an install halfway.</summary>
    internal static CloseAnswer Decide(int message, IntPtr lParam, bool busy)
    {
        if (!IsCloseRequest(message, lParam))
            return CloseAnswer.Ignore;

        return busy ? CloseAnswer.Refuse : CloseAnswer.Exit;
    }

    /// <summary>Applies <see cref="Decide"/> to one message and returns the window procedure result: 1 agrees
    /// to end the session, 0 refuses it.</summary>
    internal static IntPtr Respond(
        int message, IntPtr lParam, bool busy, Action exit, Action refused, ref bool handled)
    {
        switch (Decide(message, lParam, busy))
        {
            case CloseAnswer.Exit:
                handled = true;
                exit();
                return new IntPtr(1);
            case CloseAnswer.Refuse:
                handled = true;
                refused();
                return IntPtr.Zero;
            default:
                return IntPtr.Zero;
        }
    }

    /// <summary>Answers the request on <paramref name="source"/>'s window: agrees to end and has the app exit
    /// on the dispatcher once the message has returned, or, while <paramref name="isBusy"/> says work is
    /// running, refuses and calls <paramref name="refused"/> so the refusal is logged.</summary>
    internal static void Attach(
        HwndSource source, Dispatcher dispatcher, Action exit, Func<bool> isBusy, Action refused)
    {
        source.AddHook((IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            Respond(message, lParam, isBusy(), () => dispatcher.BeginInvoke(exit), refused, ref handled));
    }

    /// <summary>A hidden top-level window of its own, so the request reaches the app while the main window
    /// has never been shown (an autostart into the tray creates the window object but no handle).</summary>
    internal static HwndSource CreateListener(
        Dispatcher dispatcher, Action exit, Func<bool> isBusy, Action refused)
    {
        var source = new HwndSource(new HwndSourceParameters("UltimateUpdaterInstallerListener")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = 0x80, // WS_EX_TOOLWINDOW: keeps it out of the taskbar and Alt+Tab
        });
        Attach(source, dispatcher, exit, isBusy, refused);
        return source;
    }
}
