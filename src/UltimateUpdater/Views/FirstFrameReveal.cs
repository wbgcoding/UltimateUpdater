using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace UltimateUpdater.Views;

/// <summary>
/// Keeps a freshly opened window invisible until WPF has drawn its first real frame. Without this the
/// window's frame appears the moment it is shown, while its content is still being built and laid out,
/// so the user sees an empty (light or dark) rectangle flash up before the dialog fills in. DWM cloaking
/// hides the window from the screen without changing its size, position, activation or anything WPF
/// itself sees, so layout and the first render run exactly as before; the window is uncloaked just after
/// <see cref="Window.ContentRendered"/>, the first frame WPF has actually presented.
/// </summary>
internal static class FirstFrameReveal
{
    private const int DwmwaCloak = 13;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Call before <see cref="Window.Show"/>. A window whose handle already exists is left alone:
    /// it has been on screen before, there is no first frame left to wait for.</summary>
    public static void Attach(Window window, Action<string>? log = null)
    {
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
            return;

        var revealed = false;

        // Safety net: a window must never stay invisible because its first frame was missed or the reveal
        // below never got its turn. Normal priority, so a busy dispatcher cannot starve it.
        var fallback = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Normal, window.Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        fallback.Tick += (_, _) => Reveal("fallback");

        void Reveal(string how)
        {
            fallback.Stop();
            if (revealed)
                return;

            revealed = true;
            SetCloak(window, false);
            if (how != "first frame")
                log?.Invoke($"{window.GetType().Name} revealed by {how}");
        }

        // Not straight away: a dialog that fits its height to its content (DialogSizing) corrects its size
        // on this same event, and revealing now would show one frame at the old size before the jump.
        // Loaded priority runs right after that resize has been laid out and rendered, and above input,
        // so a stream of other work cannot hold it back.
        window.ContentRendered += (_, _) =>
            window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() => Reveal("first frame")));

        // A window closed before it ever rendered, or one that fails mid-build, must never stay cloaked
        // while still owning its taskbar slot or focus.
        window.Closed += (_, _) => Reveal("close");

        // The handle is created here rather than inside Show, so the cloak is in place before the window
        // can ever reach the screen, independent of the order in which handle events reach their handlers.
        // EnsureHandle raises SourceInitialized exactly as Show would; Show then reuses this handle.
        new WindowInteropHelper(window).EnsureHandle();
        if (SetCloak(window, true))
            fallback.Start();
        else
            revealed = true;
    }

    private static bool SetCloak(Window window, bool cloak)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return false;

        var value = cloak ? 1 : 0;

        // Best effort: on a system where DWM refuses (no composition), the window simply shows as before.
        return DwmSetWindowAttribute(handle, DwmwaCloak, ref value, sizeof(int)) == 0;
    }
}
