using System.Windows;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Color = System.Windows.Media.Color;
using SystemColors = System.Windows.SystemColors;

namespace UltimateUpdater.Services.App;

/// <summary>
/// Wraps WPF-UI theming so view models never need a Window reference. "system" follows the OS light/dark
/// setting live; an explicit choice detaches from it. The "hacker" (green) and "neon" (violet) themes keep
/// the Fluent dark base and only swap the accent colour. Windows high contrast overrides all of them (see
/// <see cref="ApplyHighContrastSurfaceOverrides"/>).
/// </summary>
public sealed class ThemeService
{
    /// <summary>Every window that has attached, so a theme change re-asserts the backdrop of each, not just
    /// the main window.</summary>
    private readonly List<Window> _windows = new();

    private string _requestedTheme = "system";

    /// <summary>True while subscribed to <see cref="SystemEvents.UserPreferenceChanged"/>, which happens only
    /// while the requested theme is "system"; guards against a second subscription.</summary>
    private bool _isWatching;
    private System.Windows.Threading.Dispatcher? _watchDispatcher;

    /// <summary>The OS theme and accent the last "system" pass was built from, so a settings broadcast that
    /// changed neither does not re-run the whole pass.</summary>
    private (SystemTheme Theme, Color Accent) _lastSystemAppearance;

    /// <summary>The <see cref="WindowBackdropType"/> the last <see cref="ApplyCore"/> pass applied to every
    /// open window (Mica in the light theme, None in the dark theme and under high contrast).</summary>
    public WindowBackdropType CurrentBackdrop { get; private set; } = WindowBackdropType.Mica;

    /// <summary>The backdrop the latest apply pass chose, readable without a service instance: windows opened
    /// after the pass set it in their constructor, since the pass only reaches windows already open.</summary>
    public static WindowBackdropType ActiveBackdrop { get; private set; } = WindowBackdropType.Mica;

    /// <summary>True once <see cref="ApplyHighContrastSurfaceOverrides"/> has written its own keys (the ones
    /// <see cref="ApplyAccentContrast"/> does not own) into <c>Application.Resources</c>, so leaving high
    /// contrast knows there is something to remove.</summary>
    private bool _highContrastSurfaceKeysApplied;

    public ThemeService()
    {
        // WPF raises this per SystemParameters property that actually changed, so watching it is enough to react
        // to high contrast being switched on or off while running, without a manual HWND message hook.
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
    /// Raised once the new theme's resources are in place. Anything that resolved a brush outside the
    /// resource system (a view model exposing a Brush, the WinForms tray menu) has to be told, because a
    /// DynamicResource updates itself and those do not.
    /// </summary>
    public event Action? ThemeChanged;

    /// <summary>
    /// The two named themes are the Fluent dark base with their own accent. The green is a shade deeper so it
    /// reads as a colour rather than a highlighter, and the violet a shade richer.
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
    /// The dark theme's own deep surfaces, opaque by design: WPF-UI's stock dark theme paints
    /// <c>CardBackgroundFillColorDefaultBrush</c>, <c>ControlFillColorDefaultBrush</c> and
    /// <c>ControlStrokeColorDefaultBrush</c> as semi-transparent white, which the app's contrast checking
    /// (<c>AccentPalette.Contrast</c>, ContrastSweepTests) cannot evaluate because it ignores alpha. Solid
    /// colours remove that blind spot at the source.
    ///
    /// <c>CardStrokeColorDefaultBrush</c> is a fifth key: already opaque, but tuned for the lighter stock card
    /// and under 3:1 against this deeper one. It shares <see cref="DarkStroke"/> with
    /// <c>ControlStrokeColorDefaultBrush</c>.
    ///
    /// The surfaces use the zinc palette of the sibling AI-Usage tool (card #202023, control fill #28282C) on
    /// the page colour #202020, which is what the main window shows: WindowBackgroundManager gives the
    /// attached windows the Fluent dark page as a local value, and the zinc page #18181B would make the other
    /// windows look much darker. <c>DarkStroke</c> is that palette's control border #71717B, not its softer
    /// card border #3A3A3F: a visible card edge must clear 3.0:1 and the softer one measures 1.44:1. The text
    /// keys move with the surfaces to keep their 4.5:1 margin.
    /// </summary>
    private static readonly Color DarkPageBackground = Color.FromRgb(0x20, 0x20, 0x20);
    private static readonly Color DarkCardBackground = Color.FromRgb(0x20, 0x20, 0x23);
    private static readonly Color DarkControlFill = Color.FromRgb(0x28, 0x28, 0x2C);
    private static readonly Color DarkStroke = Color.FromRgb(0x71, 0x71, 0x7B);
    private static readonly Color DarkTextPrimary = Color.FromRgb(0xF4, 0xF4, 0xF5);
    private static readonly Color DarkTextSecondary = Color.FromRgb(0xB4, 0xB4, 0xBA);
    private static readonly Color DarkTextTertiary = Color.FromRgb(0x8E, 0x8E, 0x95);

    /// <summary>
    /// Called once per window that should follow the theme. The theme is applied at once (a swap of resource
    /// dictionaries needs no shown window) and the pass runs once more when the window has loaded.
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

    /// <summary>Repaints everything filled with the accent colour, and the text on top of it; Fluent's dark
    /// theme paints both black-on-light-tint by default.</summary>
    internal static void ApplyAccentContrast(Color accent)
    {
        if (Application.Current is not { } application)
            return;

        var palette = AccentPalette.For(accent);
        var onAccent = palette.OnAccent;

        // Hover lifts, pressed sinks, the direction Fluent uses, measured from our own fill.
        Set("AccentButtonBackground", palette.Fill);
        Set("AccentButtonBackgroundPointerOver", palette.Hover);
        Set("AccentButtonBackgroundPressed", palette.Pressed);
        Set("AccentFillColorDefaultBrush", palette.Fill);
        Set("AccentFillColorSecondaryBrush", palette.Hover);
        Set("AccentFillColorTertiaryBrush", palette.Pressed);

        // Every accent-filled control surface, not only the buttons: WPF-UI paints toggle, checkbox and radio
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

        // Label, tick, dot and knob: the same colour whatever the control is doing.
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

        // Application level only: WPF-UI declares these keys again in its own theme dictionary, but the controls
        // use DynamicResource lookups and find the application-level entry first. The control family that
        // painted itself black reached that colour without any resource (see Views/ButtonLabelColour).
        void Set(string key, Color color, double opacity = 1.0) =>
            application.Resources[key] = Frozen(color, opacity);
    }

    /// <summary>
    /// A fresh frozen brush every time, never one borrowed from another dictionary: a WPF-UI brush resolves
    /// its Color through a DynamicResource in the dictionary it was declared in, and moving that instance
    /// elsewhere throws a NullReferenceException inside SolidColorBrush.get_Color.
    /// </summary>
    private static System.Windows.Media.SolidColorBrush Frozen(Color color, double opacity)
    {
        var brush = new System.Windows.Media.SolidColorBrush(color) { Opacity = opacity };
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// The surface/text/status keys <see cref="ApplyAccentContrast"/> does not own. Every value is one of
    /// WPF's own <see cref="SystemColors"/> brushes, assigned as the same shared instance, never a copy:
    /// those brushes update themselves when the OS palette changes, and in high contrast the exact scheme is
    /// the user's choice, not something this app can enumerate.
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
        // No semantic error/warning/success colour survives high contrast: colour alone must not carry the
        // meaning, so these use the one colour guaranteed readable against every surface above.
        ("SystemFillColorCriticalBrush", () => SystemColors.WindowTextBrush),
        ("SystemFillColorCriticalBackgroundBrush", () => SystemColors.WindowBrush),
        ("SystemFillColorCautionBrush", () => SystemColors.WindowTextBrush),
        ("SystemFillColorCautionBackgroundBrush", () => SystemColors.WindowBrush),
        ("SystemFillColorSuccessBrush", () => SystemColors.WindowTextBrush),
        // A ui:ToggleSwitch's track has no card surface under it in CleanupWindow, and in its Off state only the
        // white knob stayed visible against the flat high-contrast background.
        ("HighContrastToggleBorderBrush", () => SystemColors.WindowTextBrush),
    ];

    /// <summary>Writes every key in <see cref="HighContrastSurfaceKeys"/>. Static and internal like
    /// <see cref="ApplyAccentContrast"/>, so a test can load exactly this dictionary without a live
    /// <see cref="SystemParameters.HighContrast"/> session.</summary>
    internal static void ApplyHighContrastSurfaceOverrides()
    {
        if (Application.Current is not { } application)
            return;

        foreach (var (key, brush) in HighContrastSurfaceKeys)
            application.Resources[key] = brush();
    }

    /// <summary>The keys <see cref="ApplyDarkPaletteOverrides"/> writes, with the colour each one gets.
    /// Removed when the dark theme is left, so nothing here leaks into the light theme's own brushes.</summary>
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
    /// <see cref="GlassCardHighlight"/>): white at 6 percent in the dark theme, transparent elsewhere.
    /// Always written, never removed, because it has no merged theme dictionary entry to fall back to.</summary>
    private const string GlassTopHighlightKey = "GlassTopHighlightBrush";

    /// <summary>
    /// The third override step for the dark theme only (see <see cref="DarkPaletteSurfaceKeys"/>): deeper
    /// opaque page and card backgrounds, the control fill/stroke pair that moves with them, and the glass
    /// top-highlight brush. Static and internal like <see cref="ApplyAccentContrast"/> so a test can load
    /// exactly this dictionary without a live dark theme.
    /// </summary>
    internal static void ApplyDarkPaletteOverrides()
    {
        if (Application.Current is not { } application)
            return;

        foreach (var (key, color) in DarkPaletteSurfaceKeys)
            application.Resources[key] = Frozen(color, 1.0);

        application.Resources[GlassTopHighlightKey] = Frozen(System.Windows.Media.Colors.White, 0.06);
    }

    /// <summary>Removes every <see cref="DarkPaletteSurfaceKeys"/> override (each falls back to the Fluent theme dictionary's own
    /// light-mode entry) and resets the highlight brush to transparent rather than removing it: that key has
    /// no dictionary to fall back to, so absence would leave a dangling DynamicResource.</summary>
    internal static void RemoveDarkPaletteOverrides()
    {
        if (Application.Current is not { } application)
            return;

        foreach (var (key, _) in DarkPaletteSurfaceKeys)
            application.Resources.Remove(key);

        application.Resources[GlassTopHighlightKey] = Frozen(System.Windows.Media.Colors.Transparent, 1.0);
    }

    /// <summary>Removing, not overwriting with a guessed "normal" colour: every key here is only written
    /// while high contrast is on, so removing it lets the lookup fall back to the Fluent theme dictionary's
    /// own entry. Safe because every value written is this class's own reference (a shared SystemColors
    /// instance), unlike a borrowed brush, which must never be removed. Internal so tests can clear these
    /// between theme applications (see WpfHarness.ApplyTheme).</summary>
    internal static void RemoveHighContrastSurfaceOverrides()
    {
        if (Application.Current is { } application)
        {
            foreach (var (key, _) in HighContrastSurfaceKeys)
                application.Resources.Remove(key);
        }
    }

    private Window? PrimaryWindow => _windows.Count > 0 ? _windows[0] : null;

    /// <summary>Every open window, not only registered ones: a window's backdrop follows no DynamicResource
    /// and the dialogs never call AttachWindow.</summary>
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

        // Read once: accent and surfaces must be decided for the same mode.
        var highContrast = SystemParameters.HighContrast;
        var accent = ResolveAccent(theme, _useSystemAccent, highContrast);

        // High contrast overrides theme and accent alike: none of this app's own colour choices may compete with
        // a scheme the user turned on because the system's choices are the ones they can read.
        var backdrop = highContrast || applicationTheme == ApplicationTheme.Dark ? WindowBackdropType.None : WindowBackdropType.Mica;
        CurrentBackdrop = ActiveBackdrop = backdrop;

        if (highContrast)
        {
            // High contrast picks its own base: Windows does not say whether the scheme is light or dark, so it is
            // read from the luminance of the surface colour in play, never from the app's last theme choice, and
            // never with an accent tint on top.
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

            // Accent first, then the theme: the theme dictionary derives its accent brushes when applied, so the
            // other order leaves every derived brush computed from the previous accent.
            if (accent is { } color)
                ApplicationAccentColorManager.Apply(color, applicationTheme);

            // updateAccent only when no custom accent is set, so the named themes keep their colour.
            ApplicationThemeManager.Apply(applicationTheme, backdrop, updateAccent: accent is null);

            // After the theme: applying it re-merges the theme dictionary, and an accent override written before
            // that would lose to the merged entry.
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
    /// The final surface step of every apply pass, separate so a test can drive the live ordering without a
    /// real high-contrast session. High contrast and the dark palette share several keys, so under high
    /// contrast the dark palette is removed first and the high-contrast keys written after it; the other way
    /// round the removal would delete the brushes just written. Outside high contrast the dark palette
    /// follows the resolved theme, so the accented dark themes get the same deep surfaces, and it is always
    /// applied or removed, never left over.
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
    /// SystemThemeWatcher only re-applies its theme dictionary and a Mica backdrop, not this class's pass.
    /// Windows reports the switch as WM_SETTINGCHANGE "ImmersiveColorSet", which SystemEvents surfaces as the
    /// General category; that also fires for unrelated changes (an installer broadcasting a new PATH), so the
    /// pass only re-runs when the OS theme or accent differs from the last one applied. Posted to the
    /// dispatcher: the event may arrive on SystemEvents' own thread, and it lets the OS finish writing the
    /// setting first. No loop is possible, since applying a theme dictionary does not broadcast
    /// WM_SETTINGCHANGE.
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
        return IsDarkSystemTheme(_lastSystemAppearance.Theme) ? ApplicationTheme.Dark : ApplicationTheme.Light;
    }

    /// <summary>The dark Windows 11 built-in themes ("Glow", "Captured Motion") are reported as their own
    /// <see cref="SystemTheme"/> values, not as Dark; this is the set WPF-UI's own apply treats as dark.</summary>
    internal static bool IsDarkSystemTheme(SystemTheme theme) =>
        theme is SystemTheme.Dark or SystemTheme.Glow or SystemTheme.CapturedMotion;

    /// <summary>Re-reads the OS theme rather than WPF-UI's cached copy, which only refreshes when WPF-UI
    /// itself applies a theme.</summary>
    private static (SystemTheme Theme, Color Accent) ReadSystemAppearance()
    {
        SystemThemeManager.UpdateSystemThemeCache();
        return (ApplicationThemeManager.GetSystemTheme(), ApplicationAccentColorManager.GetColorizationColor());
    }
}
