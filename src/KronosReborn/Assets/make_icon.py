"""Draws the Kronos Reborn icon, a glowing magic circle, and writes .ico and .png files."""
import math
import sys
from PIL import Image, ImageDraw, ImageFilter

S = 1024  # drawn large, then scaled down so edges stay smooth
C = S / 2


def polar(r, deg):
    a = math.radians(deg - 90)
    return (C + r * math.cos(a), C + r * math.sin(a))


def star(r_out, r_in, points, rot=0):
    return [polar(r_out if i % 2 == 0 else r_in, rot + i * 180 / points) for i in range(points * 2)]


def ring(d, r, width, fill):
    d.ellipse([C - r, C - r, C + r, C + r], outline=fill, width=width)


def circle_lines(d, glow, small, tiny=False):
    """The circle itself. small: fewer, thicker lines for 24 to 32 pixels; tiny: the ring alone, for 16."""
    line = (190, 225, 255, 255) if not glow else (150, 110, 255, 255)
    w = 34 if small else 16

    ring(d, 400, w, line)
    if not small:
        ring(d, 355, 8, line)

        # Twelve marks between the rings, like a clock's hours, alternating long and short.
        for h in range(12):
            r0 = 360 if h % 3 == 0 else 368
            d.line([polar(r0, h * 30), polar(396, h * 30)], fill=line, width=10 if h % 3 == 0 else 6)
        # Runes: small diamonds and dots between the marks.
        for h in range(12):
            a = h * 30 + 15
            x, y = polar(378, a)
            if h % 2 == 0:
                d.polygon([(x, y - 11), (x + 9, y), (x, y + 11), (x - 9, y)], fill=line)
            else:
                d.ellipse([x - 6, y - 6, x + 6, y + 6], fill=line)

    if tiny:
        return

    # Hexagram: two triangles inside the ring.
    tri_r = 330 if not small else 340
    for rot in (0, 60):
        pts = [polar(tri_r, rot + k * 120) for k in range(3)]
        d.polygon(pts, outline=line, width=w if small else 12)

    if not small:
        ring(d, 165, 10, line)
        # Dots where the triangles' points meet the ring.
        for k in range(6):
            x, y = polar(tri_r, k * 60)
            d.ellipse([x - 16, y - 16, x + 16, y + 16], fill=line)


def draw(small=False, tiny=False):
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))

    # Background: rounded tile, deep indigo with a violet centre.
    bg = Image.new("RGBA", (S, S))
    bd = ImageDraw.Draw(bg)
    inner, outer = (74, 36, 150), (16, 8, 44)
    for r in range(int(C * 1.45), 0, -4):
        t = min(r / (C * 1.45), 1)
        col = tuple(int(inner[i] + (outer[i] - inner[i]) * t) for i in range(3)) + (255,)
        bd.ellipse([C - r, C - r, C + r, C + r], fill=col)
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle([24, 24, S - 24, S - 24], radius=220, fill=255)
    img.paste(bg, (0, 0), mask)

    # Glow: the same lines in violet, blurred, under the sharp ones.
    glow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    circle_lines(ImageDraw.Draw(glow), glow=True, small=small, tiny=tiny)
    glow = glow.filter(ImageFilter.GaussianBlur(28))
    img = Image.alpha_composite(img, glow)
    img = Image.alpha_composite(img, glow)

    sharp = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    sd = ImageDraw.Draw(sharp)
    circle_lines(sd, glow=False, small=small, tiny=tiny)

    # Centre: a bright four-pointed star.
    sd.polygon(star(300 if tiny else 190 if small else 150, 80 if tiny else 50 if small else 34, 4),
               fill=(255, 236, 170, 255))
    sd.polygon(star(70, 18, 4, rot=45), fill=(255, 255, 255, 230))

    halo = sharp.filter(ImageFilter.GaussianBlur(6))
    img = Image.alpha_composite(img, halo)
    img = Image.alpha_composite(img, sharp)

    # Keep everything inside the tile.
    out = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    out.paste(img, (0, 0), mask)
    return out


def main(out_dir):
    full, simple, tiny = draw(), draw(small=True), draw(small=True, tiny=True)
    full.resize((256, 256), Image.LANCZOS).save(f"{out_dir}/kronos.png")
    full.save(f"{out_dir}/kronos-1024.png")
    simple.resize((256, 256), Image.LANCZOS).save(f"{out_dir}/kronos-small.png")

    # Each size drawn from the version that reads best at that size.
    frames = [(tiny if n <= 16 else simple if n <= 32 else full).resize((n, n), Image.LANCZOS) for n in (16, 24, 32, 48, 64, 128, 256)]
    frames[-1].save(f"{out_dir}/kronos.ico", format="ICO", sizes=[(f.width, f.height) for f in frames],
                    append_images=frames[:-1])


if __name__ == "__main__":
    main(sys.argv[1])
