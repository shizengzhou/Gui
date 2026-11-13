"""Draws the Gui application icon.

The mark is a terminal prompt (chevron plus block cursor) inside the app's own pane tile,
which is what the window actually looks like. Everything is drawn at 4x and downsampled so
the 16 and 24 pixel sizes stay crisp.

Run from the repository root:  python installer/make-icon.py <out-dir> [--preview]
"""
import sys, os
from PIL import Image, ImageDraw, ImageFont

S = 1024            # master canvas
# White mark on the dark tile: the highest contrast the tile can offer, which is what the
# 16 px taskbar size needs.
MARK = (255, 255, 255)
BG_TOP = (27, 32, 41)
BG_BOTTOM = (16, 20, 26)
EDGE = (51, 62, 78)
CARD = (34, 42, 56)
# Ghostty frames its tile with a silver ring; the mark sits on the glass inside it. The ring
# is the icon's own silhouette, so it has to survive a dark taskbar and a light desktop alike.
FRAME_COLOR = (196, 202, 211)
SHOULDER = (9, 11, 15)
FRAME = 0.050   # ring thickness as a fraction of the icon width


def gradient(size, top, bottom):
    img = Image.new("RGB", (1, size), top)
    px = img.load()
    for y in range(size):
        t = y / max(1, size - 1)
        px[0, y] = tuple(round(top[i] + (bottom[i] - top[i]) * t) for i in range(3))
    return img.resize((size, size), Image.BILINEAR)


def framed_tile(radius_ratio=0.225, frame=FRAME, color=FRAME_COLOR):
    """The tile with the silver ring around it, drawn inside the full canvas."""
    radius = int(S * radius_ratio)
    ring = Image.new("RGBA", (S, S), color)
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, S - 1, S - 1], radius, fill=255)
    ring.putalpha(mask)

    t = max(1, round(S * frame))
    inner = radius - t
    glass = gradient(S, BG_TOP, BG_BOTTOM).convert("RGBA")
    gmask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(gmask).rounded_rectangle([t, t, S - 1 - t, S - 1 - t], inner, fill=255)
    glass.putalpha(gmask)
    ring.alpha_composite(glass, (0, 0))

    # The glass meets the ring behind a dark shoulder, which is what makes the frame read as a
    # frame instead of a pale halo.
    ImageDraw.Draw(ring).rounded_rectangle(
        [t, t, S - 1 - t, S - 1 - t], inner, outline=SHOULDER, width=max(1, int(S * 0.008)))
    return ring


def tile(radius_ratio=0.225):
    img = gradient(S, BG_TOP, BG_BOTTOM).convert("RGBA")
    mask = Image.new("L", (S, S), 0)
    radius = int(S * radius_ratio)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, S - 1, S - 1], radius, fill=255)
    img.putalpha(mask)
    # Hairline edge: keeps the tile from dissolving into a dark taskbar. It is sub-pixel
    # below 32 px, which is fine - the mark carries the icon there.
    ImageDraw.Draw(img).rounded_rectangle([1, 1, S - 2, S - 2], radius, outline=EDGE, width=int(S * 0.012))
    return img


def circle_tile():
    img = gradient(S, BG_TOP, BG_BOTTOM).convert("RGBA")
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).ellipse([0, 0, S - 1, S - 1], fill=255)
    img.putalpha(mask)
    return img


def prompt(draw, color, cx=0.5, cy=0.5, scale=1.0, stroke=0.075, cursor=True):
    """Chevron plus block cursor, centred on (cx, cy) as fractions of the canvas."""
    w = S * stroke * scale
    # Chevron: the ">" of a prompt, drawn with round caps so it survives 16 px.
    x0, x1 = S * (cx - 0.28 * scale), S * (cx - 0.10 * scale)
    y0, y1 = S * (cy - 0.20 * scale), S * (cy + 0.20 * scale)
    draw.line([(x0, y0), (x1, S * cy), (x0, y1)], fill=color, width=int(w), joint="curve")
    for point in ((x0, y0), (x0, y1), (x1, S * cy)):
        draw.ellipse([point[0] - w / 2, point[1] - w / 2, point[0] + w / 2, point[1] + w / 2], fill=color)

    # Cursor: a block sitting on the chevron's baseline.
    if not cursor:
        return
    bx0, bx1 = S * (cx + 0.02 * scale), S * (cx + 0.29 * scale)
    by1 = S * (cy + 0.19 * scale)
    by0 = by1 - S * 0.235 * scale
    draw.rounded_rectangle([bx0, by0, bx1, by1], radius=int(w * 0.4), fill=color)


def candidate_tile():
    img = tile()
    d = ImageDraw.Draw(img)
    inset = S * 0.105
    d.rounded_rectangle([inset, inset, S - inset, S - inset], int(S * 0.145), fill=CARD)
    prompt(d, MARK, cy=0.5, scale=1.06)
    return img


def candidate_circle():
    img = circle_tile()
    d = ImageDraw.Draw(img)
    prompt(d, MARK, cy=0.5, scale=0.95)
    return img


def prompt_g(draw, color, font_path, cx=0.5, cy=0.5, scale=1.0, stroke=0.0, cursor=True,
             cursor_height=0.84, cursor_width=0.19, gap=0.075):
    """The prompt is the app's initial: a bold G followed by the block cursor."""
    font = ImageFont.truetype(font_path, int(S * 0.60 * scale))
    sw = int(S * stroke)
    box = draw.textbbox((0, 0), "G", font=font, stroke_width=sw)
    gw, gh = box[2] - box[0], box[3] - box[1]

    cw = S * cursor_width * scale
    ch = gh * cursor_height
    gap = S * gap * scale
    total = gw + ((gap + cw) if cursor else 0)

    x = S * cx - total / 2
    y = S * cy - gh / 2
    draw.text((x - box[0], y - box[1]), "G", font=font, fill=color,
              stroke_width=sw, stroke_fill=color)
    if cursor:
        bx = x + gw + gap
        draw.rounded_rectangle([bx, y + gh - ch, bx + cw, y + gh],
                               radius=int(S * 0.018), fill=color)


def _ink_layer(draw_fn):
    layer = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    draw_fn(ImageDraw.Draw(layer))
    box = layer.getbbox()
    return layer.crop(box)


def _glyph_layer(font, text, color):
    """One character cropped to its ink, plus that ink's box.

    Every glyph is drawn from the same origin, so two of them keep their shared baseline as
    long as the crops are laid out by their boxes instead of by their tops.
    """
    layer = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(layer).text((0, 0), text, font=font, fill=color)
    box = layer.getbbox()
    return layer.crop(box), box


def prompt_mark(font_path, color, with_cursor=True, gap=0.22, bar_cursor=False):
    """The prompt of the icon: a "$" followed by the app's initial (and a cursor)."""
    font = ImageFont.truetype(font_path, int(S * 0.5))
    dollar, dollar_box = _glyph_layer(font, "$", color)
    glyph, glyph_box = _glyph_layer(font, "G", color)

    # Set at one size and drawn from one origin, the two characters already share a baseline;
    # the "$" simply reaches above and below it, so nothing has to be rescaled.
    top = min(dollar_box[1], glyph_box[1])
    height = max(dollar_box[3], glyph_box[3]) - top

    widths = dollar.width + int(gap * glyph.width) + glyph.width
    cursor_w = int(glyph.width * (0.88 if bar_cursor else 0.34))
    if with_cursor:
        widths += int(glyph.width * 0.18) + cursor_w

    group = Image.new("RGBA", (widths, height), (0, 0, 0, 0))
    group.alpha_composite(dollar, (0, dollar_box[1] - top))
    x = dollar.width + int(gap * glyph.width)
    group.alpha_composite(glyph, (x, glyph_box[1] - top))
    if with_cursor:
        x += glyph.width + int(glyph.width * 0.18)
        if bar_cursor:
            # The cmd-style underscore: a horizontal bar sitting on the baseline.
            bar_h = max(1, int(height * 0.12))
            ImageDraw.Draw(group).rounded_rectangle(
                [x, height - bar_h, x + int(glyph.width * 0.88), height],
                radius=max(1, bar_h // 2), fill=color)
        else:
            ch = int(height * 0.55)
            ImageDraw.Draw(group).rounded_rectangle(
                [x, height - ch, x + cursor_w, height], radius=max(2, cursor_w // 5), fill=color)

    return group


def candidate_dollar_g(font_path, with_cursor=False):
    img = tile()
    mark = prompt_mark(font_path, MARK, with_cursor=with_cursor)
    target = S * 0.60
    scaled = mark.resize((int(target), max(1, round(mark.height * target / mark.width))), Image.LANCZOS)
    img.alpha_composite(scaled, ((S - scaled.width) // 2, (S - scaled.height) // 2))
    return img


def candidate_dollar_g_corner(font_path, width=0.60, inset=0.145):
    """The three-part prompt (chevron, G, cursor) sitting in the tile's top-left corner."""
    img = tile()
    mark = prompt_mark(font_path, MARK, with_cursor=True)
    target = S * width
    scaled = mark.resize((round(target), max(1, round(mark.height * target / mark.width))), Image.LANCZOS)
    xy = (round(S * inset), round(S * inset))
    img.alpha_composite(scaled, xy)
    return img


def mark_metrics(px, corner):
    """Bigger mark (and, in the corner layout, a tighter inset) as the icon shrinks."""
    # The three-part mark stays under half the icon's width, so the tile keeps its margins.
    if px >= 64:
        return (0.44, 0.160) if corner else (0.46, 0.0)
    if px >= 32:
        return (0.46, 0.150) if corner else (0.48, 0.0)
    return (0.48, 0.140) if corner else (0.50, 0.0)


def render_icon(font_path, px, corner=False, bar_cursor=False, frame=FRAME,
                frame_color=FRAME_COLOR):
    """One icon size, drawn from scratch at that size's proportions.

    The ring is only worth drawing from 32 px up: below that it turns into a pale smear around
    the mark, which is all the 16 px taskbar size has room for.
    """
    width, inset = mark_metrics(px, corner)
    img = framed_tile(frame=frame, color=frame_color) if frame and px >= 32 else tile()
    mark = prompt_mark(font_path, MARK, with_cursor=True, bar_cursor=bar_cursor)
    target = S * width
    scaled = mark.resize((round(target), max(1, round(mark.height * target / mark.width))), Image.LANCZOS)
    if corner:
        xy = (round(S * inset), round(S * inset))
    else:
        xy = ((S - scaled.width) // 2, (S - scaled.height) // 2)
    img.alpha_composite(scaled, xy)
    return img.resize((px, px), Image.LANCZOS)


def write_ico(images, path):
    """Multi-size ICO with one PNG frame per size, so each size keeps its own tuning."""
    import struct
    header = struct.pack("<HHH", 0, 1, len(images))
    entries = b""
    blobs = []
    offset = 6 + 16 * len(images)
    for image in images:
        import io
        buf = io.BytesIO()
        image.save(buf, format="PNG")
        blob = buf.getvalue()
        w, h = image.size
        entries += struct.pack("<BBBBHHII", w % 256, h % 256, 0, 0, 1, 32, len(blob), offset)
        offset += len(blob)
        blobs.append(blob)
    with open(path, "wb") as fp:
        fp.write(header + entries + b"".join(blobs))


def candidate_glyph(font_path, stroke=0.0):
    img = tile()
    d = ImageDraw.Draw(img)
    prompt_g(d, MARK, font_path, cy=0.5, scale=1.02, stroke=stroke)
    return img


def candidate_glyph_small_cursor(font_path):
    img = tile()
    d = ImageDraw.Draw(img)
    prompt_g(d, MARK, font_path, cy=0.5, scale=1.06, stroke=0.011,
             cursor_height=0.60, cursor_width=0.135, gap=0.065)
    return img


def candidate_glyph_small_cursor_regular(font_path):
    img = tile()
    d = ImageDraw.Draw(img)
    prompt_g(d, MARK, font_path, cy=0.5, scale=1.06,
             cursor_height=0.60, cursor_width=0.135, gap=0.065)
    return img


def candidate_glyph_nocursor(font_path):
    img = tile()
    d = ImageDraw.Draw(img)
    prompt_g(d, MARK, font_path, cy=0.5, scale=1.12, cursor=False)
    return img


def candidate_monogram(font_path):
    img = tile()
    d = ImageDraw.Draw(img)
    font = ImageFont.truetype(font_path, int(S * 0.62))
    text = "G"
    box = d.textbbox((0, 0), text, font=font)
    w, h = box[2] - box[0], box[3] - box[1]
    d.text((S / 2 - w / 2 - box[0], S / 2 - h / 2 - box[1]), text, font=font, fill=MARK)
    # A cursor block under the monogram, so the mark still reads as a terminal.
    cw, ch = S * 0.16, S * 0.055
    d.rounded_rectangle([S / 2 - cw / 2, S * 0.755, S / 2 + cw / 2, S * 0.755 + ch],
                        radius=int(ch * 0.45), fill=MARK)
    return img


def bare_prompt():
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    prompt(ImageDraw.Draw(img), MARK, cy=0.52, scale=0.78, stroke=0.085)
    return img


ICO_SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]

# The MSIX logos are not used by the unpackaged build, but keeping them in step with the
# real mark costs nothing and the manifest still references them.
MSIX = {
    "Square44x44Logo.scale-200.png": (88, 88, 1.0),
    "Square44x44Logo.targetsize-24_altform-unplated.png": (24, 24, 1.0),
    "Square150x150Logo.scale-200.png": (300, 300, 0.82),
    "StoreLogo.png": (50, 50, 1.0),
    "LockScreenLogo.scale-200.png": (48, 48, 1.0),
    "Wide310x150Logo.scale-200.png": (620, 300, 0.62),
    "SplashScreen.scale-200.png": (1240, 600, 0.46),
}


def emit(render, out_dir):
    """Writes app.ico and the MSIX logos next to the app from one per-size renderer."""
    write_ico([render(n) for n in ICO_SIZES], os.path.join(out_dir, "app.ico"))

    for name, (w, h, scale) in MSIX.items():
        canvas = Image.new("RGBA", (w, h), (0, 0, 0, 0))
        side = int(min(w, h) * scale)
        art = render(side)
        canvas.alpha_composite(art, ((w - side) // 2, (h - side) // 2))
        canvas.save(os.path.join(out_dir, name))


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else "."
    os.makedirs(out, exist_ok=True)
    font = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..",
                        "Gui", "Gui", "Assets", "Fonts", "JetBrainsMonoNerdFont-Bold.ttf")

    font = os.path.normpath(font)
    candidates = {
        "a-tile": candidate_tile(),
        "b-circle": candidate_circle(),
        "c-monogram": candidate_monogram(font),
        "d-bare": bare_prompt(),
        "e-g": candidate_glyph(font),
        "f-g-bold": candidate_glyph(font, stroke=0.011),
        "g-g-solo": candidate_glyph_nocursor(font),
        "h-g-small-cursor": candidate_glyph_small_cursor(font),
        "i-g-small-regular": candidate_glyph_small_cursor_regular(font),
        "j-dollar-g": candidate_dollar_g(font),
        "k-dollar-g-cursor": candidate_dollar_g(font, with_cursor=True),
        "l-corner": candidate_dollar_g_corner(font),
        "m-center-bar": render_icon(font, S, corner=False, bar_cursor=True),
        "n-corner-bar": render_icon(font, S, corner=True, bar_cursor=True),
    }

    # Candidate masters only belong to a preview run; the app's Assets get the final set.
    if "--preview" in sys.argv or "--dark-preview" in sys.argv:
        for name, img in candidates.items():
            img.save(os.path.join(out, name + "-1024.png"))

    chosen = sys.argv[2] if len(sys.argv) > 2 and not sys.argv[2].startswith("--") else "n-corner-bar"
    corner = chosen.startswith(("l-", "n-"))
    bar = chosen.endswith("bar")
    emit(lambda px: render_icon(font, px, corner=corner, bar_cursor=bar), out)

    if "--dark-preview" in sys.argv:
        sizes = [256, 48, 32, 16]
        sheet = Image.new("RGBA", (sum(sizes) + 24 * len(sizes) + 24, 300), (32, 34, 37, 255))
        x = 24
        img = candidates["a-tile"]
        for size in sizes:
            sheet.alpha_composite(img.resize((size, size), Image.LANCZOS), (x, 20))
            x += size + 24
        sheet.save(os.path.join(out, "preview-dark.png"))

    if "--preview" in sys.argv:
        sizes = [256, 96, 48, 32, 16]
        rows = []
        for name, img in candidates.items():
            strip = Image.new("RGBA", (sum(sizes) + 20 * len(sizes) + 20, 280), (255, 255, 255, 255))
            x = 20
            for size in sizes:
                small = img.resize((size, size), Image.LANCZOS)
                strip.alpha_composite(small, (x, 12))
                strip.alpha_composite(small, (x, 288 - size - 12)) if False else None
                x += size + 20
            rows.append((name, strip))
        sheet = Image.new("RGBA", (rows[0][1].width, 280 * len(rows)), (255, 255, 255, 255))
        for i, (_, strip) in enumerate(rows):
            sheet.alpha_composite(strip, (0, i * 280))
        sheet.save(os.path.join(out, "preview.png"))
        print("preview", os.path.join(out, "preview.png"))


if __name__ == "__main__":
    main()