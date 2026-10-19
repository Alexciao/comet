"""Renders every concept SVG in this folder into a contact sheet (16, 32 and 256 px on light and dark).

Usage: py assets/icon/concepts/render_concepts.py   -> screenshots/icon/concepts/*.png
"""
import io
import sys
from pathlib import Path

import resvg_py
from PIL import Image, ImageDraw, ImageFont

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
OUT = ROOT / "screenshots" / "icon" / "concepts"
BGS = {"light": (0xF3, 0xF3, 0xF3), "dark": (0x20, 0x20, 0x20)}


def render(svg: Path, size: int) -> Image.Image:
    png = resvg_py.svg_to_bytes(svg_path=str(svg), width=size, height=size)
    return Image.open(io.BytesIO(bytes(png))).convert("RGBA")


def panel(svg: Path, bg) -> Image.Image:
    pad = 24
    w = pad + 16 + pad + 128 + pad + 32 + pad + 128 + pad + 256 + pad
    h = pad + 256 + pad
    img = Image.new("RGBA", (w, h), bg + (255,))
    x = pad
    cy = pad + 128
    for size, zoom in ((16, 8), (32, 4)):
        icon = render(svg, size)
        img.alpha_composite(icon, (x, cy - size // 2))
        x += size + pad
        big = Image.new("RGBA", (size, size), bg + (255,))
        big.alpha_composite(icon)
        big = big.resize((size * zoom, size * zoom), Image.NEAREST)
        img.alpha_composite(big, (x, cy - size * zoom // 2))
        x += size * zoom + pad
    img.alpha_composite(render(svg, 256), (x, pad))
    return img


def sheet(svgs) -> Image.Image:
    font = ImageFont.truetype("C:/Windows/Fonts/segoeui.ttf", 22)
    rows = []
    for svg in svgs:
        light, dark = panel(svg, BGS["light"]), panel(svg, BGS["dark"])
        row = Image.new("RGBA", (light.width * 2, light.height + 40), (255, 255, 255, 255))
        ImageDraw.Draw(row).text((24, 8), svg.stem, fill=(40, 40, 40), font=font)
        row.alpha_composite(light, (0, 40))
        row.alpha_composite(dark, (light.width, 40))
        rows.append(row)
    out = Image.new("RGBA", (rows[0].width, sum(r.height for r in rows)), (255, 255, 255, 255))
    y = 0
    for r in rows:
        out.alpha_composite(r, (0, y))
        y += r.height
    return out


if __name__ == "__main__":
    OUT.mkdir(parents=True, exist_ok=True)
    names = sys.argv[1:]
    svgs = sorted(HERE.glob("*.svg")) if not names else [HERE / n for n in names]
    for svg in svgs:
        sheet([svg]).convert("RGB").save(OUT / f"{svg.stem}.png", optimize=True)
    sheet(svgs).convert("RGB").save(OUT / "all.png", optimize=True)
    print("written to", OUT)
