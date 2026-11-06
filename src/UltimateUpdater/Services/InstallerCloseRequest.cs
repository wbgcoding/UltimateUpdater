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
    private const int WmQueryEndSession = 0x0011;
    private const long EndSessionCloseApp = 0x1;

    /// <summary>True for the installer's request to close the app, false for every other message and for a
    /// plain logoff or shutdown, which keeps its own handling.</summary>
    internal static bool IsCloseRequest(int message, IntPtr lParam) =>
        message == WmQueryEndSession && (lParam.ToInt64() & EndSessionCloseApp) != 0;

    /// <summary>Answers the request on <paramref name="source"/>'s window: agrees to end, and has the app
    /// exit on the dispatcher once the message has returned.</summary>
    internal static void Attach(HwndSource source, Dispatcher dispatcher, Action exit)
    {
        source.AddHook((IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (!IsCloseRequest(message, lParam))
                return IntPtr.Zero;

            handled = true;
            dispatcher.BeginInvoke(exit);
            return new IntPtr(1);
        });
    }

    /// <summary>A hidden top-level window of its own, so the request reaches the app while the main window
    /// has never been shown (an autostart into the tray creates the window object but no handle).</summary>
    internal static HwndSource CreateListener(Dispatcher dispatcher, Action exit)
    {
        var source = new HwndSource(new HwndSourceParameters("UltimateUpdaterInstallerListener")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = 0x80, // WS_EX_TOOLWINDOW: keeps it out of the taskbar and Alt+Tab
        });
        Attach(source, dispatcher, exit);
        return source;
    }
}
