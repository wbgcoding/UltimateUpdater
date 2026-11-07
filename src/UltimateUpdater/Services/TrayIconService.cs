using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows.Controls.Primitives;
using System.Windows.Forms;
using System.Windows.Interop;
using WpfContextMenu = System.Windows.Controls.ContextMenu;
using WpfMenuItem = System.Windows.Controls.MenuItem;
using WpfSeparator = System.Windows.Controls.Separator;

namespace UltimateUpdater.Services;

/// <summary>
/// Tray presence: open / scan now / exit, plus the balloon used for interval-scan
/// results. Uses the icon embedded in the executable, so it follows whatever icon the build ships.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly LoggingService _logging;

    /// <summary>Only set when <see cref="LoadApplicationIcon"/> actually extracted one, never the
    /// shared, framework-owned <see cref="SystemIcons.Application"/> fallback, which must never be
    /// disposed since other code may still hold the same shared instance.</summary>
    private readonly Icon? _ownIcon;

    private WpfContextMenu? _menu;

    public event Action? OpenRequested;

    public event Action? ScanRequested;

    public event Action? ExitRequested;

    public TrayIconService(LoggingService logging)
    {
        _logging = logging;

        _ownIcon = LoadApplicationIcon();

        _notifyIcon = new NotifyIcon
        {
            Icon = _ownIcon ?? SystemIcons.Application,
            Visible = false,
            Text = LocalizationService.Instance["AppTitle"],
        };

        _notifyIcon.DoubleClick += (_, _) => OpenRequested?.Invoke();
        _notifyIcon.BalloonTipClicked += (_, _) => OpenRequested?.Invoke();
        _notifyIcon.MouseUp += OnNotifyIconMouseUp;
    }

    public void Show() => _notifyIcon.Visible = true;

    public void ShowBalloon(string title, string message)
    {
        try
        {
            _notifyIcon.Visible = true;
            _notifyIcon.ShowBalloonTip(10_000, title, message, ToolTipIcon.Info);
        }
        catch (InvalidOperationException ex)
        {
            _logging.Warn($"Could not show notification: {ex.Message}");
        }
    }

    /// <summary>
    /// Only the tooltip is kept here: the menu is built fresh on every right-click, so it is always in
    /// the current language, and its WPF surfaces follow the theme through the app's dynamic brushes.
    /// </summary>
    public void RefreshLocalization()
    {
        _notifyIcon.Text = LocalizationService.Instance["AppTitle"];
    }

    /// <summary>
    /// The menu is a WPF context menu, not a WinForms strip, so it takes the app's own menu style
    /// (rounded card, soft shadow, hover and press surfaces) and looks like the row menu in both
    /// themes. A WinForms strip could only be rounded with a hard-edged window region laid over its own
    /// square border and the system's corner rounding, which showed as ragged double corners.
    /// </summary>
    private void OnNotifyIconMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right)
            return;

        var menu = BuildMenu();

        // A popup only closes on a click elsewhere while its window is the foreground one, and the tray
        // icon's owner is not, so the menu is brought to the front once it is on screen.
        menu.Opened += (_, _) =>
        {
            if (System.Windows.PresentationSource.FromVisual(menu) is HwndSource source)
                SetForegroundWindow(source.Handle);
        };

        _menu = menu;
        menu.IsOpen = true;
    }

    /// <summary>The menu as it opens on a right-click; public to the tests so a render can check its look.</summary>
    internal WpfContextMenu BuildMenu()
    {
        var menu = new WpfContextMenu
        {
            Placement = PlacementMode.MousePoint,
            StaysOpen = false,
        };

        menu.Items.Add(CreateItem(LocalizationService.Instance["TrayOpen"], () => OpenRequested?.Invoke()));
        menu.Items.Add(CreateItem(LocalizationService.Instance["TrayScan"], () => ScanRequested?.Invoke()));
        menu.Items.Add(new WpfSeparator());
        menu.Items.Add(CreateItem(LocalizationService.Instance["TrayExit"], () => ExitRequested?.Invoke()));
        return menu;
    }

    private static WpfMenuItem CreateItem(string text, Action onClick)
    {
        var item = new WpfMenuItem { Header = text };
        item.Click += (_, _) => onClick();
        return item;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>Null means "use the shared <see cref="SystemIcons.Application"/> fallback": the
    /// caller must not dispose that shared instance, only whatever this method itself extracted.</summary>
    private static Icon? LoadApplicationIcon()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (exePath is not null && File.Exists(exePath))
                return Icon.ExtractAssociatedIcon(exePath);
        }
        catch (Exception)
        {
            // Intentionally broad: whatever goes wrong extracting the exe's own icon, the tray icon
            // must never end up blank: the SystemIcons.Application fallback always renders something.
        }

        return null;
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        if (_menu is { IsOpen: true })
            _menu.IsOpen = false;
        _notifyIcon.Dispose();
        _ownIcon?.Dispose();
    }
}
