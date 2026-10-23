"""Builds the application icon.

This script is the authoritative source for the shipped icon: it draws the design at 4x and
downsamples, which gives clean antialiasing without pulling in an SVG rasterizer (Cairo on Windows
is not worth the dependency). Assets/icon.svg holds the same design in editable form for humans;
if you change one, change the other.

Design intent: a dark glass disc with a thin blue ring and a download arrow over a base bar.
The ring keeps the existing blue as the product colour (the family of the accent the UI itself
uses); the UI itself still follows the Windows system accent rather than a fixed brand hue.

Run:  python tools/make-icon.py
"""

from pathlib import Path

from PIL import Image, ImageDraw

SUPERSAMPLE = 4
SIZE = 256
CANVAS = SIZE * SUPERSAMPLE

# Glass disc: base almost black, vertical gradient top to bottom.
GLASS_TOP = (0x15, 0x18, 0x1D, 255)     # #15181D
GLASS_BOTTOM = (0x05, 0x07, 0x0A, 255)  # #05070A

# Ring: three stops of one hue, diagonal from bottom-left (deep) to top-right (bright). This keeps
# the existing blue as the product colour.
RING_DEEP = (0x0A, 0x54, 0xAD)   # #0A54AD, bottom-left
RING_MID = (0x3D, 0x9E, 0xF0)    # #3D9EF0, middle
RING_BRIGHT = (0x6F, 0xD3, 0xFF)  # #6FD3FF, top-right
RING_FLAT = (0x3D, 0x9E, 0xF0)   # flat colour used at 16/20px, where the gradient turns to mud

WHITE = (255, 255, 255, 255)

ICO_SIZES = [16, 20, 24, 32, 48, 64, 128, 256]

ASSETS = Path(__file__).resolve().parent.parent / "src" / "UltimateUpdater" / "Assets"


def scaled(value: float) -> int:
    """Design coordinates are expressed on a 256 grid."""
    return round(value * SUPERSAMPLE)


def vertical_gradient(top: tuple[int, int, int, int], bottom: tuple[int, int, int, int]) -> Image.Image:
    """A simple top-to-bottom gradient, one RGBA image the size of the canvas."""
    ramp = Image.linear_gradient("L").resize((CANVAS, CANVAS))
    channels = []
    for band in range(3):
        lut = [round(top[band] + (bottom[band] - top[band]) * v / 255) for v in range(256)]
        channels.append(ramp.point(lut))
    channels.append(Image.new("L", (CANVAS, CANVAS), 255))
    return Image.merge("RGBA", channels)


def diagonal_ramp_bl_to_tr() -> Image.Image:
    """
    A 0-255 ramp running bottom-left to top-right.

    Same construction as a plain diagonal gradient (build at double size, rotate, crop the middle,
    the crop has to come from the middle because a rotation leaves the corners empty and only the
    inscribed square is real gradient), but rotated the other way so the bright end lands in the
    top-right corner instead of the top-left, per the ring spec.
    """
    big = CANVAS * 2
    ramp = Image.linear_gradient("L").resize((big, big)).rotate(135, resample=Image.BICUBIC)
    offset = (big - CANVAS) // 2
    ramp = ramp.crop((offset, offset, offset + CANVAS, offset + CANVAS))
    # Stretch the compressed value range back out to the full 0-255 span.
    lo, hi = ramp.getextrema()
    if hi > lo:
        ramp = ramp.point([round((v - lo) * 255 / (hi - lo)) for v in range(256)])
    return ramp


def ring_gradient() -> Image.Image:
    """Three-stop blue ring gradient: deep at bottom-left, through mid, to bright at top-right."""
    ramp = diagonal_ramp_bl_to_tr()
    stops = [(0.0, RING_DEEP), (0.5, RING_MID), (1.0, RING_BRIGHT)]

    def lut_for(band: int) -> list[int]:
        lut = []
        for v in range(256):
            t = v / 255
            for (t0, c0), (t1, c1) in zip(stops, stops[1:]):
                if t <= t1 or (t0, c0) == stops[-2]:
                    span = (t1 - t0) or 1.0
                    frac = min(max((t - t0) / span, 0.0), 1.0)
                    lut.append(round(c0[band] + (c1[band] - c0[band]) * frac))
                    break
        return lut

    channels = [ramp.point(lut_for(band)) for band in range(3)]
    channels.append(Image.new("L", (CANVAS, CANVAS), 255))
    return Image.merge("RGBA", channels)


def ring_mask(outer_radius: float, inner_radius: float) -> Image.Image:
    """An annulus mask: white between inner_radius and outer_radius, black elsewhere."""
    mask = Image.new("L", (CANVAS, CANVAS), 0)
    draw = ImageDraw.Draw(mask)
    cx = cy = CANVAS / 2
    draw.ellipse([cx - outer_radius, cy - outer_radius, cx + outer_radius, cy + outer_radius], fill=255)
    draw.ellipse([cx - inner_radius, cy - inner_radius, cx + inner_radius, cy + inner_radius], fill=0)
    return mask


def disc_mask(radius: float) -> Image.Image:
    mask = Image.new("L", (CANVAS, CANVAS), 0)
    cx = cy = CANVAS / 2
    ImageDraw.Draw(mask).ellipse([cx - radius, cy - radius, cx + radius, cy + radius], fill=255)
    return mask


def draw_download_glyph(image: Image.Image, small: bool) -> None:
    """
    A download arrow standing over a short base bar: shaft and head in white, the bar in the ring's
    bright end. Round caps on shaft and bar; at small sizes everything gets thicker so the glyph
    does not dissolve into the disc.
    """
    layer = Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))
    draw = ImageDraw.Draw(layer)
    grow = 1.25 if small else 1.0

    def capped_line(x0: float, y0: float, x1: float, y1: float, width: float, colour) -> None:
        w = scaled(width)
        draw.line([(scaled(x0), scaled(y0)), (scaled(x1), scaled(y1))], fill=colour, width=w)
        for x, y in ((x0, y0), (x1, y1)):
            r = w / 2
            draw.ellipse([scaled(x) - r, scaled(y) - r, scaled(x) + r, scaled(y) + r], fill=colour)

    capped_line(128, 56, 128, 132, 28 * grow, WHITE)
    half = 52 * (1.08 if small else 1.0)
    draw.polygon([(scaled(128 - half), scaled(122)), (scaled(128), scaled(180)), (scaled(128 + half), scaled(122))],
                 fill=WHITE)
    capped_line(84, 202, 172, 202, 16 * grow, RING_BRIGHT + (255,))
    image.alpha_composite(layer)


def draw_icon(small: bool = False) -> Image.Image:
    image = Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))
    disc_radius = CANVAS / 2

    # 1. Glass disc: base almost black, vertical gradient.
    glass = vertical_gradient(GLASS_TOP, GLASS_BOTTOM)
    glass.putalpha(disc_mask(disc_radius))
    image.alpha_composite(glass)

    # 2. Ring: a thin band at the very edge, about 6% of the width.
    ring_width = scaled(15)
    mask = ring_mask(disc_radius, disc_radius - ring_width)
    if small:
        ring = Image.new("RGBA", (CANVAS, CANVAS), RING_FLAT + (255,))
    else:
        ring = ring_gradient()
    ring.putalpha(mask)
    image.alpha_composite(ring)

    # 3. Symbol: download arrow over a base bar.
    draw_download_glyph(image, small)

    return image.resize((SIZE, SIZE), Image.LANCZOS)


# Below this size the ring gradient turns to mud, so those entries use the flat-ring, thicker-arrow
# design. A .ico stores one image per size, so this costs nothing at large sizes.
SIMPLIFY_AT_OR_BELOW = 20


def main() -> None:
    ASSETS.mkdir(parents=True, exist_ok=True)

    full = draw_icon(small=False)
    small = draw_icon(small=True)

    png_path = ASSETS / "icon.png"
    full.save(png_path)

    frames = [
        (small if size <= SIMPLIFY_AT_OR_BELOW else full).resize((size, size), Image.LANCZOS)
        for size in ICO_SIZES
    ]

    ico_path = ASSETS / "app.ico"
    # The base image passed to save() must be the LARGEST frame: Pillow's ICO writer only ever
    # downsamples from it, so a smaller base silently drops every bigger requested size. It did:
    # frames[0] is 16x16, which produced a single-image .ico with everything above 16px missing.
    # append_images supplies the rest; each is matched to its requested size by exact pixel
    # dimensions, so our own pre-rendered, per-size-simplified frames are used verbatim.
    frames[-1].save(ico_path, format="ICO", sizes=[(s, s) for s in ICO_SIZES], append_images=frames[:-1])

    print(f"wrote {png_path}")
    print(f"wrote {ico_path} ({', '.join(str(s) for s in ICO_SIZES)}; simplified at/below {SIMPLIFY_AT_OR_BELOW}px)")


if __name__ == "__main__":
    main()
