using Color = System.Windows.Media.Color;

namespace UltimateUpdater.Services.Branding;

/// <summary>
/// The colours an accent-filled surface is painted with; worked out from the accent alone, so it can be
/// tested without a WPF Application in the room.
/// Fluent's dark theme fills an accent button with a light tint and a black label; here the fill is the
/// accent itself (the same mid blue the app icon uses) with a white label, deepened until it meets 4.5:1.
/// </summary>
public readonly record struct AccentPalette(Color Fill, Color Hover, Color Pressed, Color OnAccent)
{
    /// <summary>Near-black rather than black: a pure black label on a pale accent reads as a hole.</summary>
    private static readonly Color DarkLabel = Color.FromRgb(0x1A, 0x1A, 0x1A);

    /// <summary>WCAG AA for body text. Button labels are small, so the threshold is enforced.</summary>
    private const double MinimumContrast = 4.5;

    public static AccentPalette For(Color accent)
    {
        // A pale accent (a yellow, a mint) cannot carry a white label at all, and no amount of deepening
        // would leave it recognisable. Those keep a dark label; everything else is deepened until white is
        // genuinely readable on it, which for the app's own lighter blue means a few steps down. The hue
        // never moves, so the theme still looks like the colour the user chose.
        if (!IsDark(accent))
            return new(accent, Shift(accent, 0.10), Shift(accent, -0.12), DarkLabel);

        var fill = accent;
        for (var step = 0; step < 12 && Contrast(System.Windows.Media.Colors.White, fill) < MinimumContrast; step++)
            fill = Shift(fill, -0.08);

        return new(fill, Shift(fill, 0.10), Shift(fill, -0.12), System.Windows.Media.Colors.White);
    }

    /// <summary>Rec. 709 luma, the usual "is this colour dark?" test.</summary>
    internal static bool IsDark(Color color) =>
        (((0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B)) / 255.0) < 0.62;

    /// <summary>Moves a colour a fraction of the way towards white (positive) or black (negative).</summary>
    internal static Color Shift(Color color, double amount)
    {
        byte Channel(byte value) => amount >= 0
            ? (byte)(value + ((255 - value) * amount))
            : (byte)(value * (1 + amount));

        return Color.FromRgb(Channel(color.R), Channel(color.G), Channel(color.B));
    }

    /// <summary>WCAG relative luminance, used to check the label actually stands out from the fill.</summary>
    public static double Luminance(Color color)
    {
        static double Channel(byte value)
        {
            var v = value / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(color.R)) + (0.7152 * Channel(color.G)) + (0.0722 * Channel(color.B));
    }

    /// <summary>WCAG contrast ratio between two colours, 1:1 (identical) to 21:1 (black on white).</summary>
    public static double Contrast(Color a, Color b)
    {
        var high = Math.Max(Luminance(a), Luminance(b));
        var low = Math.Min(Luminance(a), Luminance(b));
        return (high + 0.05) / (low + 0.05);
    }
}
