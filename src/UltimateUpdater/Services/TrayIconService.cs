using System.Drawing;
using System.IO;
using System.Runtime.Versioning;
using System.Windows.Forms;

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

    /// <summary>Built once and reused across every <see cref="BuildMenu"/> call (language/theme
    /// changes rebuild the menu itself but never need a different font) instead of leaking one GDI
    /// font handle per rebuild.</summary>
    private readonly Font _menuFont = new("Segoe UI", 9.75f);

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
        _notifyIcon.ContextMenuStrip = BuildMenu();
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
    /// Rebuilt on language change so the menu is never left in the previous language, and on theme
    /// change, because a WinForms menu paints from colours read when it was built and would otherwise keep
    /// the previous theme until the next language switch.
    /// </summary>
    public void RefreshLocalization()
    {
        _notifyIcon.Text = LocalizationService.Instance["AppTitle"];
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.ContextMenuStrip = BuildMenu();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip
        {
            // Themed to match the app's Fluent surfaces rather than the classic grey.
            Renderer = new TrayMenuRenderer(IsDarkTheme()),
            Font = _menuFont,
            ShowImageMargin = false,
            Padding = new Padding(2, 4, 2, 4),
        };

        // Rounded corners, to match the app's own menus. Applied every time the drop-down opens (it is
        // laid out fresh each time and its client size is only known then); the renderer draws a
        // rounded hover to match. The previous Region is disposed before replacing it, otherwise every
        // single open of this SAME menu (there can be many, across a long tray session) leaked one GDI
        // region handle.
        menu.Opened += (_, _) =>
        {
            if (menu.ClientSize.Width <= 0 || menu.ClientSize.Height <= 0)
                return;

            using var path = TrayMenuRenderer.RoundedRect(
                new Rectangle(0, 0, menu.ClientSize.Width, menu.ClientSize.Height), 8);
            var oldRegion = menu.Region;
            menu.Region = new Region(path);
            oldRegion?.Dispose();
        };

        AddItem(menu, LocalizationService.Instance["TrayOpen"], () => OpenRequested?.Invoke());
        AddItem(menu, LocalizationService.Instance["TrayScan"], () => ScanRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        AddItem(menu, LocalizationService.Instance["TrayExit"], () => ExitRequested?.Invoke());
        return menu;
    }

    private static void AddItem(ContextMenuStrip menu, string text, Action onClick)
    {
        var item = new ToolStripMenuItem(text) { Padding = new Padding(6, 3, 6, 3) };
        item.Click += (_, _) => onClick();
        menu.Items.Add(item);
    }

    /// <summary>The theme WPF-UI currently has applied, so the tray menu matches the windows.</summary>
    private static bool IsDarkTheme() =>
        Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme() == Wpf.Ui.Appearance.ApplicationTheme.Dark;

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
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
        _ownIcon?.Dispose();
        _menuFont.Dispose();
    }
}
