"""Regenerates every Inlay icon export from the SVG sources in this folder.

    py -m pip install --user resvg-py pyoxipng Pillow
    py assets/icon/build.py

Sources
    inlay.svg       the full mark (32 px and up)
    inlay-16/20/24  hand-tuned, pixel-aligned versions for the small sizes
Writes
    assets/icon/out/        PNGs, Inlay.ico, InlayTray.ico, InlayTrayPaused.ico, AppIcon*.scale-*.png, Inlay.png
    docs/images/            logo.svg, logo.png, social-preview.png
"""
import io
import shutil
import struct
from pathlib import Path

import oxipng
import resvg_py
from PIL import Image, ImageDraw, ImageFont

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
OUT = HERE / "out"
DOCS = ROOT / "docs" / "images"

MAIN = HERE / "inlay.svg"
SMALL = {16: HERE / "inlay-16.svg", 20: HERE / "inlay-20.svg", 24: HERE / "inlay-24.svg"}

APP_SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 256]
TRAY_SIZES = [16, 20, 24, 32, 40, 48]
LIGHT, DARK = (0xF3, 0xF3, 0xF3), (0x20, 0x20, 0x20)
TASKBAR_LIGHT, TASKBAR_DARK = (0xEE, 0xEE, 0xEE), (0x1C, 0x1C, 0x1C)


# ---------------------------------------------------------------- rendering

def render(svg: Path, size: int) -> Image.Image:
    png = resvg_py.svg_to_bytes(svg_path=str(svg), width=size, height=size)
    return Image.open(io.BytesIO(bytes(png))).convert("RGBA")


def app_icon(size: int) -> Image.Image:
    """The app icon: hand-tuned SVGs at 16/20/24, the full mark from 32 up."""
    return render(SMALL.get(size, MAIN), size)


def tray_icon(size: int) -> Image.Image:
    """The tray icon: always the simplified mark. 32/40/48 are the 16/20/24 drawings at 200 %,
    so every display scale shows the same bold shape on the same pixel grid."""
    if size in SMALL:
        return render(SMALL[size], size)
    base = {32: 16, 40: 20, 48: 24}[size]
    return render(SMALL[base], size)


def paused(img: Image.Image) -> Image.Image:
    """Desaturated and faded: grey tile, white gem, 70 % opacity."""
    out = Image.new("RGBA", img.size)
    src = img.load()
    dst = out.load()
    for y in range(img.height):
        for x in range(img.width):
            r, g, b, a = src[x, y]
            lum = round(0.299 * r + 0.587 * g + 0.114 * b)
            dst[x, y] = (lum, lum, lum, round(a * 0.70))
    return out


# ---------------------------------------------------------------- files

def png_bytes(img: Image.Image) -> bytes:
    buf = io.BytesIO()
    img.save(buf, "PNG")
    return bytes(oxipng.optimize_from_memory(buf.getvalue(), level=4, strip=oxipng.StripChunks.safe()))


def save_png(img: Image.Image, path: Path) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(png_bytes(img))


def dib_bytes(img: Image.Image) -> bytes:
    """A 32-bit BGRA DIB with an AND mask, as stored in .ico files (bottom-up rows)."""
    w, h = img.size
    px = img.load()
    xor = bytearray()
    for y in range(h - 1, -1, -1):
        for x in range(w):
            r, g, b, a = px[x, y]
            xor += bytes((b, g, r, a))
    stride = ((w + 31) // 32) * 4
    mask = bytearray()
    for y in range(h - 1, -1, -1):
        row = bytearray(stride)
        for x in range(w):
            if px[x, y][3] == 0:
                row[x // 8] |= 0x80 >> (x % 8)
        mask += row
    header = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, len(xor) + len(mask), 0, 0, 0, 0)
    return header + bytes(xor) + bytes(mask)


def write_ico(path: Path, images: list[Image.Image]) -> None:
    """Sizes below 256 as DIB entries, 256 as PNG."""
    blobs = [(img.width, png_bytes(img) if img.width >= 256 else dib_bytes(img)) for img in images]
    head = struct.pack("<HHH", 0, 1, len(blobs))
    offset = 6 + 16 * len(blobs)
    entries = b""
    for size, blob in blobs:
        dim = 0 if size >= 256 else size
        entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(blob), offset)
        offset += len(blob)
    path.write_bytes(head + entries + b"".join(b for _, b in blobs))


def read_ico_sizes(path: Path) -> list[tuple[int, str]]:
    data = path.read_bytes()
    _, kind, count = struct.unpack_from("<HHH", data, 0)
    assert kind == 1
    sizes = []
    for i in range(count):
        w, h, _, _, _, bpp, length, offset = struct.unpack_from("<BBBBHHII", data, 6 + 16 * i)
        fmt = "png" if data[offset:offset + 8] == b"\x89PNG\r\n\x1a\n" else "dib"
        sizes.append((w or 256, fmt))
    return sizes


# ---------------------------------------------------------------- sheets

def font(size: int, weight: int = 400, opsz: int = 10) -> ImageFont.FreeTypeFont:
    try:
        f = ImageFont.truetype("C:/Windows/Fonts/SegUIVar.ttf", size)
        f.set_variation_by_axes([weight, opsz])
        return f
    except OSError:
        return ImageFont.truetype("C:/Windows/Fonts/segoeui.ttf", size)


def zoom(img: Image.Image, factor: int, bg) -> Image.Image:
    flat = Image.new("RGBA", img.size, bg + (255,))
    flat.alpha_composite(img)
    return flat.resize((img.width * factor, img.height * factor), Image.NEAREST)


def sizes_sheet() -> Image.Image:
    """Every app icon size at 1:1, then 16/20/24/32 enlarged 8x so the pixels can be judged."""
    pad = 24
    label = font(16)
    w = pad + sum(s + pad for s in APP_SIZES)
    zw = pad + sum(s * 8 + pad for s in (16, 20, 24, 32))
    width = max(w, zw)
    band_h = pad + 256 + 28 + pad + 32 * 8 + pad
    sheet = Image.new("RGBA", (width, band_h * 2), (0, 0, 0, 255))
    for band, bg in enumerate((LIGHT, DARK)):
        top = band * band_h
        fg = (60, 60, 60) if bg == LIGHT else (200, 200, 200)
        sheet.paste(Image.new("RGBA", (width, band_h), bg + (255,)), (0, top))
        d = ImageDraw.Draw(sheet)
        x = pad
        for s in APP_SIZES:
            sheet.alpha_composite(app_icon(s), (x, top + pad + 256 - s))
            d.text((x, top + pad + 256 + 6), str(s), fill=fg, font=label)
            x += s + pad
        x = pad
        y = top + pad + 256 + 28 + pad
        for s in (16, 20, 24, 32):
            sheet.alpha_composite(zoom(app_icon(s), 8, bg), (x, y + (32 - s) * 8))
            x += s * 8 + pad
    return sheet


def tray_sheet() -> Image.Image:
    """The tray icon, normal and paused, on light and dark taskbars at 100/150/200 %."""
    scales = [(100, 16, 6), (150, 24, 4), (200, 32, 3)]  # display scale, icon px, enlargement
    pad = 24
    label = font(16)
    cell_w, cell_h = 3 * 96, 96 * 2 + 16  # a taskbar strip on top, the enlarged pixels below
    width = pad + len(scales) * (cell_w + pad)
    height = pad + 2 * (cell_h + 28 + pad)
    sheet = Image.new("RGBA", (width, height), (255, 255, 255, 255))
    d = ImageDraw.Draw(sheet)
    y = pad
    for bar in (TASKBAR_LIGHT, TASKBAR_DARK):
        x = pad
        for scale, s, factor in scales:
            bar_h = round(48 * scale / 100)  # the taskbar is 48 logical px tall
            cell = Image.new("RGBA", (cell_w, cell_h), (255, 255, 255, 0))
            strip = Image.new("RGBA", (cell_w, bar_h), bar + (255,))
            icons = (tray_icon(s), paused(tray_icon(s)))
            for i, img in enumerate(icons):
                strip.alpha_composite(img, (cell_w // 3 * (i + 1) - s // 2, (bar_h - s) // 2))
            cell.alpha_composite(strip, (0, (96 - bar_h) // 2))
            big_bg = Image.new("RGBA", (cell_w, 96 + 8), bar + (255,))
            for i, img in enumerate(icons):
                big = zoom(img, factor, bar)
                big_bg.alpha_composite(big, (cell_w // 3 * (i + 1) - big.width // 2, (104 - big.height) // 2))
            cell.alpha_composite(big_bg, (0, 104))
            sheet.alpha_composite(cell, (x, y))
            d.text((x, y + cell_h + 4), f"{scale}%, {s} px: normal, paused (below: {factor}x)", fill=(60, 60, 60), font=label)
            x += cell_w + pad
        y += cell_h + 28 + pad
    return sheet


def social_preview() -> Image.Image:
    """1280 x 640 GitHub social preview: mark, name and tagline on a calm background."""
    w, h = 1280, 640
    img = Image.new("RGBA", (w, h), (0xEC, 0xF3, 0xF2, 255))
    d = ImageDraw.Draw(img)
    title = font(150, 600, 36)
    tag = font(38, 400, 20)
    lines = ["Everything you reach for,", "right where you type."]
    mark_size, gap = 288, 56
    text_w = max(d.textlength("Inlay", font=title), *(d.textlength(t, font=tag) for t in lines))
    x = round((w - (mark_size + gap + text_w)) / 2)  # mark and text centred as one group
    img.alpha_composite(render(MAIN, mark_size), (x, (h - mark_size) // 2))
    tx = x + mark_size + gap
    d.text((tx - 6, 160), "Inlay", fill=(0x12, 0x2B, 0x29), font=title)
    d.text((tx, 364), lines[0], fill=(0x3E, 0x56, 0x53), font=tag)
    d.text((tx, 412), lines[1], fill=(0x3E, 0x56, 0x53), font=tag)
    return img


# ---------------------------------------------------------------- main

def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)

    app = {s: app_icon(s) for s in APP_SIZES}
    for s, img in app.items():
        save_png(img, OUT / f"Inlay-{s}.png")
    save_png(app[256], OUT / "Inlay.png")
    write_ico(OUT / "Inlay.ico", [app[s] for s in APP_SIZES])

    tray = {s: tray_icon(s) for s in TRAY_SIZES}
    write_ico(OUT / "InlayTray.ico", [tray[s] for s in TRAY_SIZES])
    write_ico(OUT / "InlayTrayPaused.ico", [paused(tray[s]) for s in TRAY_SIZES])

    for scale, factor in ((100, 1.0), (125, 1.25), (150, 1.5), (200, 2.0)):
        for logical in (16, 48):
            size = round(logical * factor)
            save_png(app_icon(size), OUT / f"AppIcon{logical}.scale-{scale}.png")

    DOCS.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(MAIN, DOCS / "logo.svg")
    save_png(render(MAIN, 192), DOCS / "logo.png")
    save_png(social_preview().convert("RGB"), DOCS / "social-preview.png")


    for ico in ("Inlay.ico", "InlayTray.ico", "InlayTrayPaused.ico"):
        path = OUT / ico
        print(f"{ico}: {path.stat().st_size // 1024} KB", ", ".join(f"{s}{'' if f == 'dib' else ' (png)'}" for s, f in read_ico_sizes(path)))
    print("done")


if __name__ == "__main__":
    main()
