using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.Versioning;
using System.Windows.Forms;

namespace UltimateUpdater.Services;

/// <summary>
/// A dark/light renderer for the tray <see cref="ContextMenuStrip"/> so it matches the app's Fluent
/// surfaces instead of the flat classic WinForms grey. WinForms menus do not follow the OS (or the
/// app's) dark theme on their own; this supplies the colours, a ROUNDED hover highlight and rounded
/// menu corners that read the same as the rest of the app.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class TrayMenuRenderer : ToolStripProfessionalRenderer
{
    private readonly Color _text;
    private readonly Color _disabledText;
    private readonly Color _hover;

    public TrayMenuRenderer(bool dark) : base(new TrayColors(dark))
    {
        _text = dark ? Color.FromArgb(240, 240, 240) : Color.FromArgb(26, 26, 26);
        _disabledText = dark ? Color.FromArgb(140, 140, 140) : Color.FromArgb(150, 150, 150);
        _hover = dark ? Color.FromArgb(61, 61, 61) : Color.FromArgb(234, 234, 234);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? _text : _disabledText;
        base.OnRenderItemText(e);
    }

    /// <summary>A rounded, inset highlight instead of the classic full-width square selection.</summary>
    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var pressed = (e.Item as ToolStripMenuItem)?.Pressed ?? false;
        if (!e.Item.Selected && !pressed)
            return;

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = new Rectangle(4, 1, e.Item.Width - 8, e.Item.Height - 2);
        using var path = RoundedRect(rect, 5);
        using var brush = new SolidBrush(_hover);
        g.FillPath(brush, path);
    }

    /// <summary>The rounded outline for the whole drop-down window (used with a matching Region).</summary>
    internal static GraphicsPath RoundedRect(Rectangle rect, int radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        if (d <= 0)
        {
            path.AddRectangle(rect);
            return path;
        }

        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private sealed class TrayColors(bool dark) : ProfessionalColorTable
    {
        private readonly Color _surface = dark ? Color.FromArgb(43, 43, 43) : Color.FromArgb(249, 249, 249);
        private readonly Color _hover = dark ? Color.FromArgb(61, 61, 61) : Color.FromArgb(234, 234, 234);
        private readonly Color _border = dark ? Color.FromArgb(64, 64, 64) : Color.FromArgb(224, 224, 224);
        private readonly Color _separator = dark ? Color.FromArgb(70, 70, 70) : Color.FromArgb(222, 222, 222);

        public override Color ToolStripDropDownBackground => _surface;
        public override Color ImageMarginGradientBegin => _surface;
        public override Color ImageMarginGradientMiddle => _surface;
        public override Color ImageMarginGradientEnd => _surface;
        public override Color MenuBorder => _border;
        public override Color MenuItemBorder => _hover;
        public override Color MenuItemSelected => _hover;
        public override Color MenuItemSelectedGradientBegin => _hover;
        public override Color MenuItemSelectedGradientEnd => _hover;
        public override Color MenuItemPressedGradientBegin => _hover;
        public override Color MenuItemPressedGradientEnd => _hover;
        public override Color SeparatorDark => _separator;
        public override Color SeparatorLight => _separator;
    }
}
