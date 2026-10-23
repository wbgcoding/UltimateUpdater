using UltimateUpdater.Services;
using Color = System.Windows.Media.Color;

namespace UltimateUpdater.Tests;

/// <summary>
/// An accent-filled button must never be labelled in black.
/// Fluent's dark theme fills such a button with a LIGHT tint of the accent and writes the label in black,
/// genuinely Windows' own look, and the thing flagged as wrong four times over ("Icons werden immer noch
/// schwarz beim Drücken", 2026-07-27). It was never the pressed state: a snapshot of the shipped build showed the
/// label and the icon black at rest as well. Three earlier attempts missed because they reasoned about which
/// style setter wins instead of reading the colour that comes out, which is what this does.
/// </summary>
public class AccentContrastTests
{
    /// <summary>The Windows default blue, the app's own icon blue, and both named themes.</summary>
    [Theory]
    [InlineData(0x00, 0x78, 0xD4)]   // Windows default accent
    [InlineData(0x3D, 0x9E, 0xF0)]   // the app icon's lighter blue
    [InlineData(0x19, 0xC0, 0x5C)]   // "hacker" green
    [InlineData(0x96, 0x5A, 0xFF)]   // "neon" violet
    public void The_label_on_an_accent_fill_is_readable_and_not_black(byte r, byte g, byte b)
    {
        var palette = AccentPalette.For(Color.FromRgb(r, g, b));

        Assert.True(
            AccentPalette.Contrast(palette.OnAccent, palette.Fill) >= 4.5,
            $"{palette.OnAccent} on {palette.Fill} is only {AccentPalette.Contrast(palette.OnAccent, palette.Fill):0.0}:1.");

        Assert.True(
            AccentPalette.Luminance(palette.OnAccent) > 0.4,
            $"The label came out near-black ({palette.OnAccent}) on {palette.Fill}.");
    }

    /// <summary>
    /// The fill is the accent itself, not a pale tint of it. Painting a light tint is exactly what forced
    /// black text in the first place, so the fill has to stay dark enough to carry a white label.
    /// </summary>
    [Fact]
    public void The_fill_is_the_accent_itself()
    {
        var accent = Color.FromRgb(0x00, 0x78, 0xD4);
        var palette = AccentPalette.For(accent);

        Assert.Equal(accent, palette.Fill);
        Assert.True(AccentPalette.Luminance(palette.Hover) > AccentPalette.Luminance(palette.Fill));
        Assert.True(AccentPalette.Luminance(palette.Pressed) < AccentPalette.Luminance(palette.Fill));
    }

    /// <summary>A pale accent still gets a dark label, readability wins over "never black".</summary>
    [Fact]
    public void A_pale_accent_keeps_a_dark_label()
    {
        var palette = AccentPalette.For(Color.FromRgb(0xFF, 0xD7, 0x00));

        Assert.True(AccentPalette.Luminance(palette.OnAccent) < 0.1);
        Assert.True(AccentPalette.Contrast(palette.OnAccent, palette.Fill) >= 4.5);
    }

    /// <summary>Shifting must not wrap around at the ends of the range.</summary>
    [Theory]
    [InlineData(0xFF, 0.5)]
    [InlineData(0x00, -0.5)]
    public void Shifting_a_colour_stays_inside_the_range(byte channel, double amount)
    {
        var shifted = AccentPalette.Shift(Color.FromRgb(channel, channel, channel), amount);

        Assert.InRange(shifted.R, (byte)0, (byte)255);
        Assert.Equal(channel, shifted.R);
    }
}
