using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

public partial class WindowLayoutTests
{
    /// <summary>
    /// The tray menu is the app's own menu card, like the row menu, not a WinForms strip. Rendered next to a
    /// menu that mixes ticked items with a plain reset entry, so the PNG shows both, and checked for the
    /// two defects the menus had: a card whose shadow is cut off at its edge (no margin for it), and a
    /// plain entry whose label sat left of the ticked labels above it.
    /// </summary>
    [Theory]
    [InlineData("dark")]
    [InlineData("light")]
    public void App_menus_share_one_card_with_room_for_the_shadow_and_aligned_labels(string themeName)
    {
        try
        {
            var rendered = false;
            var failures = new List<string>();

            WpfHarness.OnUiThread(() =>
            {
                LocalizationService.Instance.Apply("de");
                ApplyTheme(themeName);

                using var tray = new TrayIconService(new LoggingService());
                var trayMenu = tray.BuildMenu();

                var mixed = new ContextMenu();
                mixed.Items.Add(new MenuItem { Header = "Name", IsCheckable = true, IsChecked = true });
                mixed.Items.Add(new MenuItem { Header = "Größe", IsCheckable = true, IsChecked = false });
                mixed.Items.Add(new MenuItem { Header = "Datum", IsCheckable = true, IsChecked = true });
                mixed.Items.Add(new Separator());
                mixed.Items.Add(new MenuItem { Header = "Zurücksetzen" });

                var sub = new MenuItem { Header = "Erinnern in" };
                sub.Items.Add(new MenuItem { Header = "1 Stunde" });
                var withSubmenu = new ContextMenu();
                withSubmenu.Items.Add(new MenuItem { Header = "Öffnen", InputGestureText = "Strg+O" });
                withSubmenu.Items.Add(sub);

                // A context menu cannot be parented, so each one is laid out on its own and drawn into one
                // picture, side by side on the window colour.
                var menus = new[] { trayMenu, mixed, withSubmenu };
                var origins = new List<double>();
                double x = 16, height = 0;
                foreach (var menu in menus)
                {
                    menu.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    menu.Arrange(new Rect(new Point(0, 0), menu.DesiredSize));
                    menu.UpdateLayout();
                    origins.Add(x);
                    x += menu.DesiredSize.Width + 24;
                    height = Math.Max(height, menu.DesiredSize.Height);
                }

                var picture = new RenderTargetBitmap((int)x + 16, (int)height + 32, 96, 96, PixelFormats.Pbgra32);
                var backdrop = new DrawingVisual();
                using (var dc = backdrop.RenderOpen())
                    dc.DrawRectangle((Brush)Application.Current.FindResource("ApplicationBackgroundBrush"), null,
                        new Rect(0, 0, picture.Width, picture.Height));
                picture.Render(backdrop);
                for (var i = 0; i < menus.Length; i++)
                {
                    var tile = new DrawingVisual();
                    using (var dc = tile.RenderOpen())
                        dc.DrawRectangle(new VisualBrush(menus[i]), null, new Rect(origins[i], 16, menus[i].DesiredSize.Width, menus[i].DesiredSize.Height));
                    picture.Render(tile);
                }

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(picture));
                var path = Path.Combine(WpfHarness.OutputDirectory, $"app-menus-{themeName}.png");
                using (var file = File.Create(path))
                    encoder.Save(file);
                rendered = File.Exists(path);

                static Border Card(ContextMenu menu) => (Border)VisualTreeHelper.GetChild(menu, 0);

                foreach (var (name, menu) in new[] { ("tray", trayMenu), ("mixed", mixed), ("shortcut", withSubmenu) })
                {
                    var card = Card(menu);
                    var shadow = card.Effect as DropShadowEffect;
                    if (card.CornerRadius != new CornerRadius(7))
                        failures.Add($"{name}: card radius {card.CornerRadius}");
                    if (shadow is null)
                    {
                        failures.Add($"{name}: no shadow");
                        continue;
                    }

                    if (card.Margin.Left < shadow.BlurRadius || card.Margin.Right < shadow.BlurRadius
                        || card.Margin.Top < shadow.BlurRadius - shadow.ShadowDepth
                        || card.Margin.Bottom < shadow.BlurRadius + shadow.ShadowDepth)
                        failures.Add($"{name}: margin {card.Margin} leaves the shadow no room");
                }

                // Labels of the mixed menu in one line: a ticked, an unticked and the plain reset entry.
                double LabelX(MenuItem item)
                {
                    var menu = (ContextMenu)ItemsControl.ItemsControlFromItemContainer(item);
                    var label = FindFirstDescendant<ContentPresenter>(item)!;
                    return label.TransformToAncestor(menu).Transform(new Point(0, 0)).X;
                }

                var ticked = LabelX((MenuItem)mixed.Items[0]);
                var unticked = LabelX((MenuItem)mixed.Items[1]);
                var reset = LabelX((MenuItem)mixed.Items[4]);
                if (Math.Abs(ticked - unticked) > 0.5 || Math.Abs(ticked - reset) > 0.5)
                    failures.Add($"labels not in one line: {ticked}, {unticked}, {reset}");

                // A menu with no ticked item gets no check gutter.
                var plainLabel = LabelX((MenuItem)trayMenu.Items[0]);
                var trayCardLeft = Card(trayMenu).TransformToAncestor(trayMenu).Transform(new Point(0, 0)).X;
                if (plainLabel - trayCardLeft > 20)
                    failures.Add($"tray menu keeps a check gutter: label {plainLabel - trayCardLeft}px in");
            });

            Assert.True(rendered, "the menu picture was not written");
            Assert.True(failures.Count == 0, $"{themeName}: {string.Join("; ", failures)}");
        }
        finally
        {
            WpfHarness.OnUiThread(() => ApplyTheme("dark"));
        }
    }
}
