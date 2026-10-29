using System.Windows;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Color = System.Windows.Media.Color;
using SystemColors = System.Windows.SystemColors;

namespace UltimateUpdater.Services;

/// <summary>
/// Wraps WPF-UI theming so view models never need a Window reference. "system" follows the OS
/// light/dark setting live; an explicit choice detaches from it. Beyond plain light/dark there are two
/// accented dark themes ("hacker" (green) and "neon" (violet)) which keep the Fluent dark base
/// and only swap the accent colour, so every semantic brush still resolves. Windows high contrast overrides
/// all of the above (see <see cref="ApplyHighContrastSurfaceOverrides"/>): the Fluent look and the
/// named/accent themes all stop painting anything of their own choosing the moment the OS setting is on.
/// </summary>
public sealed class ThemeService
{
    /// <summary>Every window that has attached, so a theme change re-asserts its backdrop, not just the
    /// main window. Without this a second window (Settings/History) kept the previous theme's chrome and
    /// looked "half switched" (black panes in light mode).</summary>
    private readonly List<Window> _windows = new();

    private string _requestedTheme = "system";

    /// <summary>True while subscribed to <see cref="SystemEvents.UserPreferenceChanged"/>, which happens
    /// only while the requested theme is "system"; guards against a second subscription.</summary>
    private bool _isWatching;
    private System.Windows.Threading.Dispatcher? _watchDispatcher;

    /// <summary>The OS theme and accent the last "system" pass was built from, so a settings broadcast that
    /// changed neither does not re-run the whole pass.</summary>
    private (SystemTheme Theme, Color Accent) _lastSystemAppearance;

    /// <summary>The <see cref="WindowBackdropType"/> the last <see cref="ApplyCore"/> pass actually applied
    /// to every open window (Mica in the light theme, None in the dark theme and under high contrast). Read by <c>MainWindow</c>'s activation
    /// timing log so it never has to re-derive the high-contrast check on its own.</summary>
    public WindowBackdropType CurrentBackdrop { get; private set; } = WindowBackdropType.Mica;

    /// <summary>The backdrop the latest apply pass chose, readable without a service instance: windows
    /// opened after the pass set it in their constructor, since the pass only reaches windows already
    /// open. None in the dark theme and under high contrast, Mica otherwise.</summary>
    public static WindowBackdropType ActiveBackdrop { get; private set; } = WindowBackdropType.Mica;

    /// <summary>True once <see cref="ApplyHighContrastSurfaceOverrides"/> has written its own keys (the
    /// ones <see cref="ApplyAccentContrast"/> does not already own) into <c>Application.Resources</c>, so
    /// leaving high contrast knows there is something of its own to remove rather than guessing.</summary>
    private bool _highContrastSurfaceKeysApplied;

    public ThemeService()
    {
        // WPF already listens for WM_SETTINGCHANGE/WM_THEMECHANGED to keep every SystemParameters
        // property current and raises this per property that actually changed; watching it here is the
        // idiomatic way to react to the user flipping Windows high contrast on or off while the app is
        // running, with no manual HWND message hook of this app's own to keep in step with WPF's.
        SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
    }

    private void OnSystemParameterChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SystemParameters.HighContrast))
            return;

        if (_windows.Count > 0)
            ApplyCore(_requestedTheme);
    }

    /// <summary>
    /// Raised once the new theme's resources are in place. Anything that resolved a brush OUTSIDE the
    /// resource system (a view model exposing a Brush property, the WinForms tray menu) has to be told,
    /// because a DynamicResource updates itself and those do not.
    /// </summary>
    public event Action? ThemeChanged;

    /// <summary>
    /// The two named themes are the Fluent DARK base with their own accent: nothing else. The green is
    /// a shade deeper so it reads as a colour rather than a highlighter, and the violet a shade richer.
    /// </summary>
    private static readonly Color HackerAccent = Color.FromRgb(0x19, 0xC0, 0x5C);
    private static readonly Color NeonAccent = Color.FromRgb(0x96, 0x5A, 0xFF);

    /// <summary>The Windows default blue: the accent of the light, dark and system themes on every PC
    /// unless the user opts into the Windows accent.</summary>
    internal static readonly Color DefaultAccent = Color.FromRgb(0x00, 0x78, 0xD4);

    private bool _useSystemAccent;

    /// <summary>Which accent a theme paints with: the fixed colour, or null for "the one Windows supplies"
    /// (high contrast, or the user's opt-in on the light, dark and system themes). The named themes keep
    /// their own colour whatever the setting says.</summary>
    internal static Color? ResolveAccent(string theme, bool useSystemAccent, bool highContrast)
    {
        if (highContrast)
            return null;

        return theme switch
        {
            "hacker" => HackerAccent,
            "neon" => NeonAccent,
            _ => useSystemAccent ? null : DefaultAccent,
        };
    }

    /// <summary>Switches between the app's own blue and the Windows accent; repaints at once.</summary>
    public void SetUseSystemAccent(bool value)
    {
        if (_useSystemAccent == value)
            return;

        _useSystemAccent = value;

        if (_windows.Count > 0)
            ApplyCore(_requestedTheme);
    }

    /// <summary>
    /// The dark theme's own deep surfaces, OPAQUE by design: WPF-UI's stock dark theme paints
    /// <c>CardBackgroundFillColorDefaultBrush</c>, <c>ControlFillColorDefaultBrush</c> and
    /// <c>ControlStrokeColorDefaultBrush</c> as semi-transparent white (5-7%), meant to be alpha-composited
    /// over whatever sits behind them. Nothing in this app's contrast checking
    /// (<c>AccentPalette.Contrast</c>, the sweep in ContrastSweepTests) looks at alpha, so a translucent
    /// "card" measured against a translucent "text" comes back as pale grey on pale grey, nowhere near the
    /// 4.5:1/3.0:1 WCAG floors. Real, solid colours remove the alpha blindness at the source instead of
    /// teaching the checker to composite.
    ///
    /// <c>CardStrokeColorDefaultBrush</c> is a fifth key: already opaque, but tuned for WPF-UI's much
    /// lighter stock dark card, and well under 3:1 against this deeper card and window. It shares
    /// <see cref="DarkStroke"/> with <c>ControlStrokeColorDefaultBrush</c>: both are "a border on a dark
    /// fill", and a WCAG ratio has no shortcut around how far a stroke must sit from a near-black fill to
    /// clear 3:1.
    ///
    /// The surfaces use the zinc palette of the sibling AI-Usage tool: page #18181B, card #202023, control
    /// fill #28282C. <c>DarkStroke</c> is that palette's control border #71717B, not its softer card border
    /// #3A3A3F: a visible card edge must clear 3.0:1 (3.4:1 against the card, 3.0:1 against the control
    /// fill), and the softer one measures 1.44:1. The text keys move with the surfaces so text keeps its
    /// 4.5:1 margin.
    /// </summary>
    private static readonly Color DarkPageBackground = Color.FromRgb(0x18, 0x18, 0x1B);
    private static readonly Color DarkCardBackground = Color.FromRgb(0x20, 0x20, 0x23);
    private static readonly Color DarkControlFill = Color.FromRgb(0x28, 0x28, 0x2C);
    private static readonly Color DarkStroke = Color.FromRgb(0x71, 0x71, 0x7B);
    private static readonly Color DarkTextPrimary = Color.FromRgb(0xF4, 0xF4, 0xF5);
    private static readonly Color DarkTextSecondary = Color.FromRgb(0xB4, 0xB4, 0xBA);
    private static readonly Color DarkTextTertiary = Color.FromRgb(0x8E, 0x8E, 0x95);

    /// <summary>
    /// Called once per window that should follow the theme. The theme itself is applied at once; it is
    /// a swap of resource dictionaries and needs no shown window. The pass runs once more when the window has
    /// loaded.
    /// </summary>
    public void AttachWindow(Window window)
    {
        if (!_windows.Contains(window))
        {
            _windows.Add(window);
            window.Closed += (_, _) => _windows.Remove(window);
        }

        ApplyCore(_requestedTheme);

        if (!window.IsLoaded)
            window.Loaded += OnWindowLoaded;
    }

    public void Apply(string theme)
    {
        _requestedTheme = theme;

        if (_windows.Count > 0)
            ApplyCore(theme);
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window)
            window.Loaded -= OnWindowLoaded;

        ApplyCore(_requestedTheme);
    }

    /// <summary>Repaints everything filled with the accent colour, and the text on top of it. Fluent's
    /// dark theme paints both black-on-light-tint by default.</summary>
    internal static void ApplyAccentContrast(Color accent)
    {
        if (Application.Current is not { } application)
            return;

        var palette = AccentPalette.For(accent);
        var onAccent = palette.OnAccent;

        // Hover lifts, pressed sinks: the direction Fluent uses, just measured from our own fill.
        Set("AccentButtonBackground", palette.Fill);
        Set("AccentButtonBackgroundPointerOver", palette.Hover);
        Set("AccentButtonBackgroundPressed", palette.Pressed);
        Set("AccentFillColorDefaultBrush", palette.Fill);
        Set("AccentFillColorSecondaryBrush", palette.Hover);
        Set("AccentFillColorTertiaryBrush", palette.Pressed);

        // Every accent-filled control surface, not only the buttons. WPF-UI paints toggle/checkbox/radio
        // parts black under a deepened accent.
        foreach (var key in new[]
                 {
                     "CheckBoxCheckBackgroundFillChecked",
                     "ToggleButtonBackgroundChecked",
                     "ToggleSwitchFillOn",
                     "ToggleSwitchStrokeOn",
                     "RadioButtonOuterEllipseCheckedStroke",
                 })
        {
            Set(key, palette.Fill);
        }

        foreach (var key in new[]
                 {
                     "CheckBoxCheckBackgroundFillCheckedPointerOver",
                     "ToggleSwitchFillOnPointerOver",
                     "ToggleSwitchStrokeOnPointerOver",
                     "RadioButtonOuterEllipseCheckedStrokePointerOver",
                 })
        {
            Set(key, palette.Hover);
        }

        foreach (var key in new[]
                 {
                     "CheckBoxCheckBackgroundFillCheckedPressed",
                     "ToggleButtonBackgroundCheckedPressed",
                     "ToggleSwitchFillOnPressed",
                     "ToggleSwitchStrokeOnPressed",
                 })
        {
            Set(key, palette.Pressed);
        }

        // Label, tick, dot and knob: the same colour whatever the control is doing, so nothing turns black
        // under the cursor or the finger.
        foreach (var key in new[]
                 {
                     "AccentButtonForeground",
                     "AccentButtonForegroundPointerOver",
                     "AccentButtonForegroundPressed",
                     "TextOnAccentFillColorPrimaryBrush",
                     "CheckBoxCheckGlyphForeground",
                     "RadioButtonCheckGlyphFill",
                     "ToggleButtonForegroundChecked",
                     "ToggleButtonForegroundCheckedPointerOver",
                     "ToggleButtonForegroundCheckedPressed",
                     "ToggleSwitchKnobFillOn",
                     "ToggleSwitchKnobFillOnPointerOver",
                     "ToggleSwitchKnobFillOnPressed",
                 })
        {
            Set(key, onAccent);
        }

        Set("TextOnAccentFillColorSecondaryBrush", onAccent, opacity: 0.78);

        // Application level only, deliberately. WPF-UI declares these keys again in its own theme
        // dictionary, but the controls use DynamicResource lookups and find the application-level entry
        // first, so a second write there would change nothing. The control family that painted itself
        // black reached that colour without any resource at all (see Views/ButtonLabelColour).
        void Set(string key, Color color, double opacity = 1.0) =>
            application.Resources[key] = Frozen(color, opacity);
    }

    /// <summary>
    /// A fresh frozen brush every time, never one borrowed from another dictionary: a WPF-UI brush resolves
    /// its own Color through a DynamicResource in the dictionary it was declared in, and moving that
    /// instance elsewhere kills rendering with a NullReferenceException inside SolidColorBrush.get_Color.
    /// </summary>
    private static System.Windows.Media.SolidColorBrush Frozen(Color color, double opacity)
    {
        var brush = new System.Windows.Media.SolidColorBrush(color) { Opacity = opacity };
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// The surface/text/status keys <see cref="ApplyAccentContrast"/> does not already own (that one
    /// covers only the accent-filled control family). Every value here is one of WPF's own
    /// <see cref="SystemColors"/> brushes, assigned as the SAME shared instance WPF hands out, never a
    /// copy: those brushes already update themselves when the OS palette changes (the reason this class
    /// otherwise has to freeze and rewrite its own brushes on every change), so leaving them live is both
    /// less code and more correct for high contrast specifically, where the exact scheme (Black, White, a
    /// custom one) is the user's own choice, not something this app can enumerate.
    /// </summary>
    private static readonly (string Key, Func<System.Windows.Media.SolidColorBrush> Brush)[] HighContrastSurfaceKeys =
    [
        ("ApplicationBackgroundBrush", () => SystemColors.WindowBrush),
        ("TextFillColorPrimaryBrush", () => SystemColors.WindowTextBrush),
        ("TextFillColorSecondaryBrush", () => SystemColors.WindowTextBrush),
        ("TextFillColorTertiaryBrush", () => SystemColors.GrayTextBrush),
        ("ControlFillColorDefaultBrush", () => SystemColors.WindowBrush),
        ("ControlFillColorSecondaryBrush", () => SystemColors.WindowBrush),
        ("CardBackgroundFillColorDefaultBrush", () => SystemColors.WindowBrush),
        ("CardBackgroundFillColorSecondaryBrush", () => SystemColors.WindowBrush),
        ("SolidBackgroundFillColorSecondaryBrush", () => SystemColors.WindowBrush),
        ("CardStrokeColorDefaultBrush", () => SystemColors.WindowFrameBrush),
        ("ControlStrokeColorDefaultBrush", () => SystemColors.WindowFrameBrush),
        // No semantic error/warning/success colour survives high contrast: the whole point of the mode is
        // that colour alone never carries the meaning, so these fall back to the one colour guaranteed
        // readable against every surface above rather than a red/orange/green this app cannot verify
        // against whatever scheme the user actually has on.
        ("SystemFillColorCriticalBrush", () => SystemColors.WindowTextBrush),
        ("SystemFillColorCriticalBackgroundBrush", () => SystemColors.WindowBrush),
        ("SystemFillColorCautionBrush", () => SystemColors.WindowTextBrush),
        ("SystemFillColorCautionBackgroundBrush", () => SystemColors.WindowBrush),
        ("SystemFillColorSuccessBrush", () => SystemColors.WindowTextBrush),
        // A ui:ToggleSwitch's track has no card surface under it in CleanupWindow, and in its Off state
        // only the white knob stayed visible against the flat high-contrast background above. One extra
        // key, same shape as the rest of this table, rather than a special case.
        ("HighContrastToggleBorderBrush", () => SystemColors.WindowTextBrush),
    ];

    /// <summary>Writes every key in <see cref="HighContrastSurfaceKeys"/>. Static and internal, like
    /// <see cref="ApplyAccentContrast"/>, so a test can load exactly this dictionary directly without
    /// needing a live <see cref="SystemParameters.HighContrast"/> session (see the contrast sweep test's
    /// own doc comment for what that can and cannot prove).</summary>
    internal static void ApplyHighContrastSurfaceOverrides()
    {
        if (Application.Current is not { } application)
            return;

        foreach (var (key, brush) in HighContrastSurfaceKeys)
            application.Resources[key] = brush();
    }

    /// <summary>The keys <see cref="ApplyDarkPaletteOverrides"/> writes, alongside the colour each one
    /// gets. Same shape as <see cref="HighContrastSurfaceKeys"/>, and removed the same way when the dark
    /// theme is left, so nothing here ever leaks into the light theme's own, already-correct brushes.</summary>
    private static readonly (string Key, Color Color)[] DarkPaletteSurfaceKeys =
    [
        ("ApplicationBackgroundBrush", DarkPageBackground),
        ("CardBackgroundFillColorDefaultBrush", DarkCardBackground),
        ("ControlFillColorDefaultBrush", DarkControlFill),
        ("ControlStrokeColorDefaultBrush", DarkStroke),
        ("CardStrokeColorDefaultBrush", DarkStroke),
        ("TextFillColorPrimaryBrush", DarkTextPrimary),
        ("TextFillColorSecondaryBrush", DarkTextSecondary),
        ("TextFillColorTertiaryBrush", DarkTextTertiary),
        ("SolidBackgroundFillColorBaseBrush", DarkPageBackground),
        ("LayerFillColorDefaultBrush", DarkCardBackground),
    ];

    /// <summary>The resource key a card or the toolbar draws its one-pixel top highlight from (see
    /// <see cref="Views.GlassCardHighlight"/>): white at 6 percent in the dark theme, transparent
    /// everywhere else. Always written, never removed, because unlike the surface keys above it has no
    /// merged theme dictionary entry of its own to fall back to.</summary>
    private const string GlassTopHighlightKey = "GlassTopHighlightBrush";

    /// <summary>
    /// The third override step for the DARK theme only (see <see cref="DarkPaletteSurfaceKeys"/>'s own
    /// doc comment for why these particular keys need it): deeper, OPAQUE page and card backgrounds plus
    /// the control fill/stroke pair that has to move with them, and the glass top-highlight brush cards
    /// and the toolbar draw a one-pixel line from. Static and internal like <see cref="ApplyAccentContrast"/>
    /// and <see cref="ApplyHighContrastSurfaceOverrides"/>, for the same reason: a test loads exactly this
    /// dictionary directly, no live dark theme needed.
    /// </summary>
    internal static void ApplyDarkPaletteOverrides()
    {
        if (Application.Current is not { } application)
            return;

        foreach (var (key, color) in DarkPaletteSurfaceKeys)
            application.Resources[key] = Frozen(color, 1.0);

        application.Resources[GlassTopHighlightKey] = Frozen(System.Windows.Media.Colors.White, 0.06);
    }

    /// <summary>Removes the four surface overrides (each falls straight back to the Fluent theme
    /// dictionary's own light-mode entry, exactly like <see cref="RemoveHighContrastSurfaceOverrides"/>
    /// does), and resets the highlight brush to transparent rather than removing it: that key has no
    /// dictionary of its own to fall back to, so leaving it absent would be a dangling DynamicResource
    /// instead of a themed one.</summary>
    internal static void RemoveDarkPaletteOverrides()
    {
        if (Application.Current is not { } application)
            return;

        foreach (var (key, _) in DarkPaletteSurfaceKeys)
            application.Resources.Remove(key);

        application.Resources[GlassTopHighlightKey] = Frozen(System.Windows.Media.Colors.Transparent, 1.0);
    }

    /// <summary>Removing, not overwriting with a guessed-at "normal" colour: every key here is one this
    /// class only ever WRITES while high contrast is on, so removing it lets the lookup fall straight back
    /// to the Fluent theme dictionary's own entry, exactly as if this class had never touched it. Safe
    /// specifically because every value written above is this class's OWN fresh brush reference (a shared
    /// SystemColors instance with no DynamicResource of its own pointing back into a theme dictionary),
    /// unlike the borrowed-brush case, which must never be removed. Internal, like its sibling overrides
    /// above, so the test harness can clear these between theme applications instead of leaving them for
    /// whatever test runs next (see WpfHarness.ApplyTheme).</summary>
    internal static void RemoveHighContrastSurfaceOverrides()
    {
        if (Application.Current is { } application)
        {
            foreach (var (key, _) in HighContrastSurfaceKeys)
                application.Resources.Remove(key);
        }
    }

    private Window? PrimaryWindow => _windows.Count > 0 ? _windows[0] : null;

    /// <summary>Every open window, not only registered ones: a window's backdrop follows no
    /// DynamicResource and the dialogs never call AttachWindow.</summary>
    private IEnumerable<Window> OpenWindows()
    {
        var open = Application.Current?.Windows.OfType<Window>() ?? [];
        return _windows.Concat(open).Distinct();
    }

    private static bool IsSystem(string theme) => theme is not ("light" or "dark" or "hacker" or "neon");

    /// <summary>The OS settings broadcast matters for the "system" theme (light/dark) and for any
    /// light/dark choice that follows the Windows accent.</summary>
    private bool NeedsWatch(string theme) => IsSystem(theme) || (_useSystemAccent && theme is "light" or "dark");

    private void ApplyCore(string theme)
    {
        if (PrimaryWindow is not { } primary)
            return;

        var applicationTheme = theme switch
        {
            "light" => ApplicationTheme.Light,
            "dark" or "hacker" or "neon" => ApplicationTheme.Dark,
            _ => ResolveSystemTheme(),
        };

        var accent = ResolveAccent(theme, _useSystemAccent, SystemParameters.HighContrast);

        // Windows high contrast overrides theme AND accent alike: none of this app's own colour choices,
        // named or otherwise, may compete with a scheme the user turned on specifically because the
        // system's own choices are the ones they can read ("never diagnose a colour by reasoning about
        // precedence, read the resolved value" applies here too - the resolved value in high contrast
        // must always be the OS's own).
        var highContrast = SystemParameters.HighContrast;
        var backdrop = highContrast || applicationTheme == ApplicationTheme.Dark ? WindowBackdropType.None : WindowBackdropType.Mica;
        CurrentBackdrop = ActiveBackdrop = backdrop;

        if (highContrast)
        {
            // High contrast picks its OWN base (the current scheme can be a light or a dark one; Windows
            // does not expose which, so it is read the same way this app already decides Light vs Dark
            // for anything else: the luminance of the surface colour actually in play), never the app's
            // last light/dark/named choice, and never an accent tint on top of it.
            applicationTheme = AccentPalette.IsDark(SystemColors.WindowColor) ? ApplicationTheme.Dark : ApplicationTheme.Light;
            ApplicationThemeManager.Apply(applicationTheme, backdrop, updateAccent: false);
            ApplyAccentContrast(SystemColors.HighlightColor);
            _highContrastSurfaceKeysApplied = true;
        }
        else
        {
            if (_highContrastSurfaceKeysApplied)
            {
                RemoveHighContrastSurfaceOverrides();
                _highContrastSurfaceKeysApplied = false;
            }

            // ACCENT FIRST, then the theme. The theme dictionary derives its accent brushes when it is
            // applied, so applying the theme and then the accent left every derived brush computed from
            // the PREVIOUS accent: which is why switching to "hacker" or "neon" did not repaint until
            // something else forced it. This way the theme is built on top of the colour it should use.
            if (accent is { } color)
                ApplicationAccentColorManager.Apply(color, applicationTheme);

            // updateAccent only when we are NOT setting a custom one, so the special themes keep their
            // colour.
            ApplicationThemeManager.Apply(applicationTheme, backdrop, updateAccent: accent is null);

            // AFTER the theme: applying it re-merges the theme dictionary, and an accent override written
            // before that would simply be the older entry of two: the merged one wins for anything looked
            // up later.
            ApplyAccentContrast(accent ?? ApplicationAccentColorManager.SystemAccent);
        }

        ApplySurfaceOverrides(highContrast, applicationTheme == ApplicationTheme.Dark);

        // Re-apply the backdrop to every open window so none is left painted in the previous theme.
        foreach (var window in OpenWindows())
            WindowBackgroundManager.UpdateBackground(window, applicationTheme, backdrop);

        ThemeChanged?.Invoke();

        var watch = NeedsWatch(theme);
        if (watch)
            _lastSystemAppearance = ReadSystemAppearance();

        if (watch && !_isWatching)
        {
            _watchDispatcher = primary.Dispatcher;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _isWatching = true;
        }
        else if (!watch && _isWatching)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _isWatching = false;
        }
    }

    /// <summary>
    /// The final surface step of every apply pass, kept separate so a test can drive the exact ordering
    /// the live path uses without a real high-contrast session. High contrast and the dark palette share
    /// several keys (the page, card, control and text brushes), so under high contrast the dark palette is
    /// removed FIRST and the high-contrast keys written after it; the other way round the removal deleted
    /// the high-contrast brushes it had just written. Outside high contrast the dark palette is keyed off
    /// the RESOLVED theme, not the theme string, so the two accented dark themes get the same deep
    /// surfaces, and it is always applied or removed, never left over from the previous theme.
    /// </summary>
    internal static void ApplySurfaceOverrides(bool highContrast, bool dark)
    {
        if (highContrast)
        {
            RemoveDarkPaletteOverrides();
            ApplyHighContrastSurfaceOverrides();
        }
        else if (dark)
        {
            ApplyDarkPaletteOverrides();
        }
        else
        {
            RemoveDarkPaletteOverrides();
        }
    }

    /// <summary>
    /// Follows the OS light/dark (and accent) setting live for the "system" theme. WPF-UI's own
    /// SystemThemeWatcher only re-applied its theme dictionary and a Mica backdrop, never this class's
    /// pass, so the dark palette overrides went stale, the backdrop came back as Mica in dark mode and
    /// <see cref="ThemeChanged"/> never fired. Windows reports the switch as WM_SETTINGCHANGE
    /// "ImmersiveColorSet", which SystemEvents surfaces as the General category. That category also fires
    /// for unrelated changes (an installer broadcasting a new PATH, for one), so the pass only re-runs when
    /// the OS theme or accent actually differs from the one last applied. Posted to the dispatcher rather
    /// than run inline: the event arrives on SystemEvents' own thread when no UI context was captured, and
    /// this also lets the OS finish writing the setting first. No loop is possible: applying a theme
    /// dictionary does not broadcast WM_SETTINGCHANGE.
    /// </summary>
    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General)
            _watchDispatcher?.BeginInvoke(ReapplyIfSystemAppearanceChanged);
    }

    private void ReapplyIfSystemAppearanceChanged()
    {
        if (!_isWatching || !NeedsWatch(_requestedTheme) || PrimaryWindow is null)
            return;

        if (ReadSystemAppearance() != _lastSystemAppearance)
            ApplyCore(_requestedTheme);
    }

    private ApplicationTheme ResolveSystemTheme()
    {
        _lastSystemAppearance = ReadSystemAppearance();
        return _lastSystemAppearance.Theme == SystemTheme.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light;
    }

    /// <summary>Re-reads the OS theme rather than WPF-UI's cached copy, which only refreshes when WPF-UI
    /// itself applies a theme.</summary>
    private static (SystemTheme Theme, Color Accent) ReadSystemAppearance()
    {
        SystemThemeManager.UpdateSystemThemeCache();
        return (ApplicationThemeManager.GetSystemTheme(), ApplicationAccentColorManager.GetColorizationColor());
    }
}
