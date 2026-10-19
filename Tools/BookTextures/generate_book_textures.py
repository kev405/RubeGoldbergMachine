#!/usr/bin/env python3
"""
generate_book_textures.py
Genera N libros antiguos distintos (texturas PBR) para una caja de libro con UV en atlas.

Cada libro (carpeta Book_XX/):
  Book_XX_basecolor.png            sRGB
  Book_XX_normal.png               OpenGL (Y+)
  Book_XX_metallic_smoothness.png  Unity URP: R = metallic, A = smoothness
  Book_XX_ao.png
Compartido:
  book_layout.json   rectángulos UV (los lee BookSetup.cs para construir la malla)
  books_preview.png  lomos de todos los libros lado a lado

Atlas (por defecto 2048 px):
  ┌─────────────────────────────────────────────┐
  │ tapa trasera │ lomo │ tapa delantera        │  ← una sola pieza continua (envoltura)
  ├──────────────────────────┬──────────────────┘
  │ canto delantero (páginas)│
  ├──────────────┬───────────┴──┐
  │ canto cabeza │ canto pie    │
  └──────────────┴──────────────┘

Estilos: cuero entero (con nervios y tejuelo), tela (título vertical), media piel (lomo y puntas
de cuero, tapas de papel marmolado). Cantos: natural, dorado, salpicado o teñido.

Uso:
  python generate_book_textures.py --out ./out --count 12 --seed 3
  python generate_book_textures.py --thickness 0.25 --height 1.29 --depth 0.9   # medidas de BookRow
Requisitos: numpy, scipy, Pillow
"""
from __future__ import annotations

import argparse
import json
import zlib
from dataclasses import dataclass, field
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont
from scipy import ndimage

HERE = Path(__file__).resolve().parent

# ----------------------------------------------------------------------------
# Catálogo
# ----------------------------------------------------------------------------
TITLES = [
    ("DON QUIJOTE", "CERVANTES"), ("LA ODISEA", "HOMERO"), ("LA ILÍADA", "HOMERO"),
    ("PRINCIPIA", "NEWTON"), ("ORIGEN DE LAS ESPECIES", "DARWIN"), ("LA DIVINA COMEDIA", "DANTE"),
    ("ELEMENTOS", "EUCLIDES"), ("FAUSTO", "GOETHE"), ("LA REPÚBLICA", "PLATÓN"),
    ("MECÁNICA CELESTE", "LAPLACE"), ("EL PRÍNCIPE", "MAQUIAVELO"), ("MARÍA", "JORGE ISAACS"),
    ("LA VORÁGINE", "J. E. RIVERA"), ("TRATADO DE FÍSICA", "GANOT"), ("ATLAS CELESTE", "FLAMSTEED"),
    ("HISTORIA NATURAL", "PLINIO"), ("DIÁLOGOS", "GALILEO"), ("ÓPTICA", "NEWTON"),
]
LEATHER = [(0.34, 0.08, 0.06), (0.11, 0.19, 0.12), (0.10, 0.12, 0.22), (0.31, 0.18, 0.09),
           (0.52, 0.33, 0.17), (0.09, 0.07, 0.06), (0.40, 0.11, 0.16)]
CLOTH = [(0.45, 0.10, 0.10), (0.13, 0.20, 0.33), (0.17, 0.28, 0.19), (0.60, 0.46, 0.19),
         (0.33, 0.37, 0.42), (0.11, 0.29, 0.31), (0.38, 0.26, 0.36), (0.50, 0.40, 0.30)]
LABELS = [(0.07, 0.06, 0.05), (0.46, 0.08, 0.06), (0.10, 0.22, 0.13), (0.12, 0.14, 0.28)]
MARBLE = [[(0.16, 0.22, 0.42), (0.75, 0.62, 0.38), (0.55, 0.12, 0.10)],
          [(0.12, 0.30, 0.25), (0.82, 0.76, 0.60), (0.35, 0.18, 0.12)],
          [(0.40, 0.10, 0.12), (0.20, 0.28, 0.50), (0.85, 0.80, 0.68)],
          [(0.25, 0.20, 0.35), (0.70, 0.55, 0.30), (0.15, 0.15, 0.15)]]
GOLD = np.array([0.95, 0.74, 0.36])
PAPER = np.array([0.90, 0.85, 0.72])
FONT_CANDIDATES = [  # serif con acentos latinos (todas OFL / libres)
    "LiberationSerif-Bold.ttf", "Lora-Variable.ttf", "DejaVuSerifCondensed-Bold.ttf",
]
SYSTEM_FONT_DIRS = [HERE / "fonts", Path("/usr/share/fonts/truetype/liberation"),
                    Path("/usr/share/fonts/truetype/google-fonts"), Path("/usr/share/fonts/truetype/dejavu"),
                    Path("C:/Windows/Fonts")]
WINDOWS_FALLBACK = ["timesbd.ttf", "georgiab.ttf", "palab.ttf"]


def find_fonts() -> list[str]:
    found: list[str] = []
    for name in FONT_CANDIDATES + WINDOWS_FALLBACK:
        for d in SYSTEM_FONT_DIRS:
            if (d / name).is_file():
                found.append(str(d / name))
                break
    if not found:
        raise FileNotFoundError("No encontré fuentes serif. Copia una .ttf en ./fonts/")
    return found


def load_font(path: str, size: int) -> ImageFont.FreeTypeFont:
    font = ImageFont.truetype(path, size)
    try:                                               # fuentes variables (Lora): usar el peso Bold
        font.set_variation_by_name("Bold")
    except (OSError, ValueError, AttributeError):
        pass
    return font


# ----------------------------------------------------------------------------
# Layout del atlas
# ----------------------------------------------------------------------------
@dataclass(frozen=True)
class Rect:
    x: int
    y: int
    w: int
    h: int

    def uv(self, size: int) -> list[float]:
        return [self.x / size, 1 - (self.y + self.h) / size, (self.x + self.w) / size, 1 - self.y / size]


def build_layout(size: int, th: float, hgt: float, dep: float) -> dict[str, Rect]:
    margin, gap = round(size * 0.02), round(size * 0.024)
    d = (size - 2 * margin) / (2 * dep + th)                       # px por metro
    while True:
        wd, wt, wh = round(d * dep), round(d * th), round(d * hgt)
        fore_y = margin + wh + gap
        top_y = fore_y + wt + gap
        fits = (top_y + wt <= size - margin and round(d * hgt) <= size - 2 * margin
                and 2 * wd + gap <= size - 2 * margin)
        if fits:
            break
        d *= 0.97
    return {
        "back": Rect(margin, margin, wd, wh),
        "spine": Rect(margin + wd, margin, wt, wh),
        "front": Rect(margin + wd + wt, margin, wd, wh),
        "wrap": Rect(margin, margin, 2 * wd + wt, wh),
        "fore": Rect(margin, fore_y, wh, wt),
        "top": Rect(margin, top_y, wd, wt),
        "bottom": Rect(margin + wd + gap, top_y, wd, wt),
    }


# ----------------------------------------------------------------------------
# Ruido y utilidades
# ----------------------------------------------------------------------------
def fbm(rng, h, w, cell, octaves=4, gain=0.5, aspect=(1.0, 1.0)):
    acc, amp, tot = np.zeros((h, w)), 1.0, 0.0
    for _ in range(octaves):
        ch, cw = max(cell * aspect[0], 1.5), max(cell * aspect[1], 1.5)
        gh, gw = int(h / ch) + 3, int(w / cw) + 3
        n = ndimage.zoom(rng.random((gh, gw)), (h / (gh - 1), w / (gw - 1)), order=3)[:h, :w]
        acc += amp * n
        tot += amp
        amp *= gain
        cell /= 2
    acc /= tot
    return (acc - acc.min()) / (np.ptp(acc) + 1e-9)


def smoothstep(a, b, x):
    t = np.clip((x - a) / (b - a), 0, 1)
    return t * t * (3 - 2 * t)


def lerp3(c, target, t):
    t = np.asarray(t)[..., None] if np.ndim(t) else t
    return c * (1 - t) + np.asarray(target) * t


def desaturate(c, amount):
    gray = c.mean(axis=-1, keepdims=True)
    return c * (1 - amount) + gray * amount


@dataclass
class Surf:
    color: np.ndarray
    height: np.ndarray
    rough: np.ndarray
    metal: np.ndarray

    @staticmethod
    def new(h, w, col, rough=0.6):
        return Surf(np.ones((h, w, 3)) * np.asarray(col), np.full((h, w), 0.5),
                    np.full((h, w), rough), np.zeros((h, w)))


# ----------------------------------------------------------------------------
# Materiales base
# ----------------------------------------------------------------------------
def leather(rng, h, w, col, wear) -> Surf:
    s = Surf.new(h, w, col)
    pebble = 1 - np.abs(2 * fbm(rng, h, w, 9, 3) - 1)               # grano en relieve
    mott = fbm(rng, h, w, 220, 4)
    s.color = np.asarray(col) * ((0.78 + 0.38 * mott) * (0.9 + 0.14 * pebble))[..., None]
    s.height += 0.02 * pebble + 0.01 * (mott - 0.5)
    s.rough = 0.42 + 0.18 * (1 - pebble) + 0.1 * wear * (1 - mott)
    return s


def cloth(rng, h, w, col, wear) -> Surf:
    s = Surf.new(h, w, col, 0.88)
    yy, xx = np.mgrid[0:h, 0:w]
    p = 4.0
    warp = 0.5 + 0.5 * np.sin(2 * np.pi * xx / p)
    weft = 0.5 + 0.5 * np.sin(2 * np.pi * yy / p)
    checker = ((xx // p + yy // p) % 2).astype(bool)
    weave = np.where(checker, warp, weft) * (0.7 + 0.3 * fbm(rng, h, w, 3, 2))
    mott = fbm(rng, h, w, 180, 4)
    s.color = np.asarray(col) * ((0.82 + 0.3 * mott) * (0.86 + 0.2 * weave))[..., None]
    s.height += 0.018 * weave
    s.rough = 0.82 + 0.1 * (1 - weave)
    return s


def marbled(rng, h, w, palette) -> Surf:
    """Papel marmolado tipo 'piedra': gotas de color con vetas oscuras sobre un fondo peinado."""
    s = Surf.new(h, w, palette[0], 0.55)
    yy, xx = np.mgrid[0:h, 0:w].astype(float)
    c0, c1, c2 = (np.asarray(c) for c in palette)
    warp = fbm(rng, h, w, 70, 3)
    comb = 0.5 + 0.5 * np.sin((xx + 45 * warp) / 3.2)                  # fondo peinado fino
    col = lerp3(np.broadcast_to(c0, (h, w, 3)), c0 * 0.75, comb * 0.4)
    for k, (cell, thr, c) in enumerate(((34, 0.58, c1), (20, 0.62, c2 * 1.1), (12, 0.66, c1 * 0.9))):
        st = fbm(rng, h, w, cell, 3)
        st = ndimage.map_coordinates(st, [np.clip(yy + 14 * (warp - 0.5), 0, h - 1),
                                          np.clip(xx + 24 * (warp - 0.5), 0, w - 1)], order=1)
        inside = smoothstep(thr, thr + 0.025, st)
        vein = np.exp(-((st - thr) ** 2) / 0.00012)
        shade = 0.85 + 0.25 * smoothstep(thr, thr + 0.12, st)
        col = lerp3(col, c * shade[..., None], inside * (0.95 - 0.15 * k))
        col = lerp3(col, c2 * 0.45, vein * 0.8)
    s.color = np.clip(col * (0.92 + 0.1 * fbm(rng, h, w, 5, 2))[..., None], 0, 1)
    s.height += 0.003 * warp
    return s


# ----------------------------------------------------------------------------
# Texto y ornamentos (máscaras 0-1)
# ----------------------------------------------------------------------------
def text_mask(lines: list[str], font_path: str, max_w: int, max_h: int,
              line_gap: float = 0.15, rotate: int = 0) -> np.ndarray:
    """Máscara del texto centrado; reduce el tamaño hasta que entre en max_w x max_h."""
    bw, bh = (max_h, max_w) if rotate else (max_w, max_h)       # caja antes de rotar
    size = int(bh / max(1, len(lines)) * 0.9)
    while size > 6:
        font = load_font(font_path, size)
        boxes = [font.getbbox(t) for t in lines]
        widths = [b[2] - b[0] for b in boxes]
        lh = size * (1 + line_gap)
        if max(widths) <= bw * 0.92 and lh * len(lines) <= bh * 0.95:
            break
        size -= 1
    img = Image.new("L", (bw, bh), 0)
    draw = ImageDraw.Draw(img)
    total_h = lh * len(lines) - size * line_gap
    y = (bh - total_h) / 2
    for t, b in zip(lines, boxes):
        draw.text(((bw - (b[2] - b[0])) / 2 - b[0], y - b[1] + (size - (b[3] - b[1])) * 0.3), t,
                  font=font, fill=255)
        y += lh
    if rotate:
        img = img.rotate(rotate, expand=True)
    return np.asarray(img, dtype=float) / 255.0


def wrap_title(title: str, max_lines: int = 3) -> list[str]:
    words = title.split()
    if len(words) <= max_lines:
        return words if len(words) > 1 else [title]
    lines, cur = [], ""
    target = len(title) / max_lines
    for w in words:
        if cur and len(cur) + len(w) + 1 > target * 1.25:
            lines.append(cur)
            cur = w
        else:
            cur = f"{cur} {w}".strip()
    lines.append(cur)
    return lines[:max_lines]


def ornament(size: int, kind: int) -> np.ndarray:
    """Florón dorado sencillo dibujado con primitivas."""
    img = Image.new("L", (size, size), 0)
    d = ImageDraw.Draw(img)
    c, r = size / 2, size / 2 * 0.9
    if kind == 0:                                   # rombo con puntos
        d.polygon([(c, c - r), (c + r * 0.55, c), (c, c + r), (c - r * 0.55, c)], fill=255)
        d.polygon([(c, c - r * 0.55), (c + r * 0.28, c), (c, c + r * 0.55), (c - r * 0.28, c)], fill=0)
        for dx, dy in ((-0.8, 0), (0.8, 0)):
            d.ellipse([c + dx * r - r * 0.12, c - r * 0.12, c + dx * r + r * 0.12, c + r * 0.12], fill=255)
    elif kind == 1:                                 # roseta de 6 pétalos
        for k in range(6):
            a = k * np.pi / 3
            px, py = c + np.cos(a) * r * 0.5, c + np.sin(a) * r * 0.5
            d.ellipse([px - r * 0.3, py - r * 0.3, px + r * 0.3, py + r * 0.3], fill=255)
        d.ellipse([c - r * 0.22, c - r * 0.22, c + r * 0.22, c + r * 0.22], fill=0)
        d.ellipse([c - r * 0.1, c - r * 0.1, c + r * 0.1, c + r * 0.1], fill=255)
    else:                                           # estrella de 8 puntas
        pts = []
        for k in range(16):
            a = k * np.pi / 8
            rr = r if k % 2 == 0 else r * 0.42
            pts.append((c + np.cos(a) * rr, c + np.sin(a) * rr))
        d.polygon(pts, fill=255)
    return np.asarray(img.filter(ImageFilter.GaussianBlur(0.6)),
                      dtype=float) / 255.0


def paste(dst: np.ndarray, src: np.ndarray, cx: float, cy: float) -> None:
    """Combina (max) una máscara src centrada en (cx, cy) dentro de dst, recortando bordes."""
    h, w = src.shape
    x0, y0 = int(round(cx - w / 2)), int(round(cy - h / 2))
    xa, ya = max(0, x0), max(0, y0)
    xb, yb = min(dst.shape[1], x0 + w), min(dst.shape[0], y0 + h)
    if xb > xa and yb > ya:
        dst[ya:yb, xa:xb] = np.maximum(dst[ya:yb, xa:xb], src[ya - y0:yb - y0, xa - x0:xb - x0])


def hline(mask, y, x0, x1, width):
    yy = np.arange(mask.shape[0])[:, None]
    band = np.clip(1 - np.abs(yy - y) / (width / 2), 0, 1)
    mask[:, x0:x1] = np.maximum(mask[:, x0:x1], np.repeat(band, x1 - x0, axis=1))


def frame(mask, x0, y0, x1, y1, width):
    """Marco rectangular (filete)."""
    yy, xx = np.mgrid[0:mask.shape[0], 0:mask.shape[1]]
    inside = (xx >= x0) & (xx <= x1) & (yy >= y0) & (yy <= y1)
    d = np.minimum.reduce([np.abs(xx - x0), np.abs(xx - x1), np.abs(yy - y0), np.abs(yy - y1)])
    mask[:] = np.maximum(mask, np.clip(1 - d / (width / 2), 0, 1) * inside)


def apply_gold(rng, s: Surf, mask: np.ndarray, wear: float, depth: float = 0.035) -> None:
    h, w = mask.shape
    flake = smoothstep(0.12, 0.42, fbm(rng, h, w, 5, 3) + 0.35 - 0.3 * wear)
    rub = 1 - 0.5 * wear * smoothstep(0.55, 0.9, fbm(rng, h, w, 40, 3))
    m = np.clip(mask * flake * rub, 0, 1)
    tone = 0.82 + 0.3 * fbm(rng, h, w, 8, 2)
    s.color = lerp3(s.color, GOLD * tone[..., None], m)
    s.metal = np.maximum(s.metal, m * 0.95)
    s.rough = s.rough * (1 - m) + (0.26 + 0.18 * (1 - tone)) * m
    s.height -= depth * mask                          # el dorado va hundido (estampado en caliente)


def apply_blind(s: Surf, mask: np.ndarray, depth: float = 0.05) -> None:
    s.color = s.color * (1 - 0.18 * mask[..., None])
    s.height -= depth * mask
    s.rough = s.rough * (1 - mask) + np.clip(s.rough - 0.12, 0.2, 1) * mask


# ----------------------------------------------------------------------------
# Envoltura: tapas + lomo
# ----------------------------------------------------------------------------
@dataclass
class BookSpec:
    idx: int
    style: str
    title: str
    author: str
    volume: str
    cover: tuple
    spine_mat: tuple
    label: tuple
    marble: list
    bands: int
    edge: str          # natural | gilt | sprinkled | tinted
    tint: tuple
    title_dir: int     # 90 = de abajo hacia arriba, -90 = de arriba hacia abajo
    orn: int
    wear: float
    fade: float
    extras: dict = field(default_factory=dict)


def make_spec(rng, idx: int, title_pool: list) -> BookSpec:
    style = ["leather", "cloth", "half"][(idx + int(rng.integers(0, 1))) % 3]       # reparte los 3 estilos
    title, author = title_pool.pop(int(rng.integers(len(title_pool))))
    leather_col = LEATHER[int(rng.integers(len(LEATHER)))]
    cloth_col = CLOTH[int(rng.integers(len(CLOTH)))]
    label = LABELS[int(rng.integers(len(LABELS)))]
    if np.abs(np.subtract(label, leather_col)).sum() < 0.12:
        label = LABELS[(LABELS.index(label) + 1) % len(LABELS)]
    vol = f"TOMO {['I', 'II', 'III', 'IV'][int(rng.integers(4))]}" if rng.random() < 0.35 else ""
    edge = ["natural", "sprinkled", "gilt", "natural", "tinted"][(idx * 2 + int(rng.integers(2))) % 5]
    if style == "cloth" and edge == "gilt":
        edge = "natural"
    jitter = lambda c: tuple(np.clip(np.asarray(c) * rng.uniform(0.85, 1.15), 0, 1))
    return BookSpec(
        idx=idx, style=style, title=title, author=author, volume=vol,
        cover=jitter(cloth_col if style == "cloth" else leather_col),
        spine_mat=jitter(leather_col if style != "cloth" else cloth_col),
        label=label, marble=MARBLE[int(rng.integers(len(MARBLE)))],
        bands=int(rng.choice([4, 5])), edge=edge,
        tint=[(0.55, 0.15, 0.12), (0.20, 0.30, 0.45), (0.65, 0.55, 0.25)][int(rng.integers(3))],
        title_dir=int(rng.choice([90, -90])), orn=int(rng.integers(3)),
        wear=float(rng.uniform(0.55, 1.1)), fade=float(rng.uniform(0.1, 0.45)),
    )


def build_wrap(rng, spec: BookSpec, L: dict[str, Rect], fonts: list[str]) -> Surf:
    W, H = L["wrap"].w, L["wrap"].h
    wd, wt = L["back"].w, L["spine"].w
    sx0, sx1 = wd, wd + wt                               # lomo dentro de la envoltura
    font = fonts[spec.idx % len(fonts)]
    xx = np.arange(W)[None, :].repeat(H, 0)
    yy = np.arange(H)[:, None].repeat(W, 1)

    # --- material base ---
    if spec.style == "leather":
        s = leather(rng, H, W, spec.cover, spec.wear)
    elif spec.style == "cloth":
        s = cloth(rng, H, W, spec.cover, spec.wear)
    else:                                                # media piel: tapas marmoladas + cuero
        s = marbled(rng, H, W, spec.marble)
        lea = leather(rng, H, W, spec.spine_mat, spec.wear)
        ext = wt * 0.55                                   # cuero que entra en las tapas desde el lomo
        corner = wd * 0.26                                # puntas de cuero
        on_spine = (xx > sx0 - ext) & (xx < sx1 + ext)
        dxo = np.minimum(xx, W - 1 - xx)                  # distancia al borde exterior de las tapas
        dyo = np.minimum(yy, H - 1 - yy)
        on_corner = (dxo + dyo) < corner
        lm = (on_spine | on_corner).astype(float)
        lm = ndimage.gaussian_filter(lm, 0.8)
        for attr in ("color", "height", "rough", "metal"):
            a, b = getattr(s, attr), getattr(lea, attr)
            setattr(s, attr, a * (1 - (lm[..., None] if a.ndim == 3 else lm)) + b * (lm[..., None] if b.ndim == 3 else lm))
        s.height += 0.03 * lm                             # el cuero queda algo más alto que el papel
        edge_line = np.zeros((H, W))
        edge_line[np.abs(ndimage.sobel(lm, 1)) + np.abs(ndimage.sobel(lm, 0)) > 0.6] = 1
        apply_gold(rng, s, ndimage.gaussian_filter(edge_line, 0.7) * 0.9, spec.wear, 0.02)

    gold = np.zeros((H, W))
    blind = np.zeros((H, W))
    spine_w = sx1 - sx0
    cx = (sx0 + sx1) / 2

    # --- lomo ---
    if spec.style in ("leather", "half"):
        head, tail = H * 0.075, H * 0.925
        band_y = np.linspace(head, tail, spec.bands + 2)[1:-1]
        bh = H * 0.012
        bump = np.zeros((H, W))
        for by in band_y:
            prof = np.clip(1 - ((yy - by) / bh) ** 2, 0, 1) ** 0.6
            bump = np.maximum(bump, prof)
        across = smoothstep(sx0 - 2, sx0 + spine_w * 0.08, xx) * smoothstep(sx1 + 2, sx1 - spine_w * 0.08, xx)
        s.height += 0.14 * bump * across
        s.color = s.color * (1 + 0.08 * (bump * across)[..., None])
        # filetes dorados a ambos lados de cada nervio y en cabeza/pie
        x0, x1 = int(sx0 + spine_w * 0.06), int(sx1 - spine_w * 0.06)
        for by in band_y:
            for off in (-bh * 1.9, bh * 1.9):
                hline(gold, by + off, x0, x1, 3.2)
        for y in (head * 0.55, head * 0.75, H - head * 0.55, H - head * 0.75):
            hline(gold, y, x0, x1, 2.6)
        panels = [(a, b) for a, b in zip(np.r_[head, band_y], np.r_[band_y, tail])]
        # tejuelo con el título (segundo panel)
        p0, p1 = panels[1]
        title_lines = wrap_title(spec.title)
        m = 3.2 if len(title_lines) < 3 else 2.4                      # títulos largos: tejuelo más alto
        ly0, ly1 = int(p0 + bh * m), int(p1 - bh * m)
        lx0, lx1 = int(sx0 + spine_w * 0.1), int(sx1 - spine_w * 0.1)
        lab = leather(rng, ly1 - ly0, lx1 - lx0, spec.label, spec.wear)
        s.color[ly0:ly1, lx0:lx1] = lab.color
        s.rough[ly0:ly1, lx0:lx1] = lab.rough
        s.height[ly0:ly1, lx0:lx1] = lab.height + 0.01
        frame(gold, lx0 + 4, ly0 + 4, lx1 - 5, ly1 - 5, 2.2)
        label_font = fonts[0] if len(title_lines) >= 3 else font      # la más legible para 3 líneas
        t = text_mask(title_lines, label_font, lx1 - lx0 - 12, ly1 - ly0 - 12, line_gap=0.08)
        paste(gold, t, cx, (ly0 + ly1) / 2)
        # autor o tomo en el tercer panel
        p0, p1 = panels[2]
        sub = spec.volume or spec.author
        t = text_mask([sub], font, int(spine_w * 0.74), int((p1 - p0) * 0.32))
        paste(gold, t, cx, (p0 + p1) / 2)
        # florones en el resto
        for k, (p0, p1) in enumerate(panels):
            if k in (1, 2):
                continue
            o = ornament(int(min(spine_w * 0.45, (p1 - p0) * 0.5)), spec.orn)
            paste(gold, o, cx, (p0 + p1) / 2)
    else:
        x0, x1 = int(sx0 + spine_w * 0.04), int(sx1 - spine_w * 0.04)
        for y in (H * 0.05, H * 0.062, H * 0.938, H * 0.95):
            hline(gold, y, x0, x1, 3.0)
        title_len = int(H * 0.6)
        t = text_mask([spec.title], font, int(spine_w * 0.5), title_len, rotate=spec.title_dir)
        a = text_mask([spec.author], font, int(spine_w * 0.34), int(H * 0.2), rotate=spec.title_dir)
        ty = H * 0.42 if spec.title_dir == -90 else H * 0.58
        ay = H * 0.82 if spec.title_dir == -90 else H * 0.18
        paste(gold, t, cx, ty)
        paste(gold, a, cx, ay)
        paste(gold, ornament(int(spine_w * 0.35), spec.orn), cx, H * 0.10 if spec.title_dir == -90 else H * 0.90)

    # --- tapas ---
    for (c0, c1, is_front) in ((0, wd, False), (sx1, W, True)):
        inset = wd * 0.07
        fx0, fx1 = c0 + inset, c1 - inset
        if spec.style == "half":
            continue                                       # el marmolado ya decora la tapa
        if spec.style == "leather" and is_front:
            frame(gold, fx0, inset, fx1, H - inset, 3.2)
            frame(gold, fx0 + 10, inset + 10, fx1 - 10, H - inset - 10, 1.8)
            for (ox, oy) in ((fx0 + 28, inset + 28), (fx1 - 28, inset + 28),
                             (fx0 + 28, H - inset - 28), (fx1 - 28, H - inset - 28)):
                paste(gold, ornament(34, spec.orn), ox, oy)
        else:
            frame(blind, fx0, inset, fx1, H - inset, 4.0)
            frame(blind, fx0 + 12, inset + 12, fx1 - 12, H - inset - 12, 2.0)
        if spec.style == "cloth" and is_front:
            t = text_mask(wrap_title(spec.title, 2), font, int((fx1 - fx0) * 0.7), int(H * 0.16))
            paste(gold, t, (fx0 + fx1) / 2, H * 0.3)
            paste(gold, ornament(int(wd * 0.12), spec.orn), (fx0 + fx1) / 2, H * 0.55)

    apply_blind(s, ndimage.gaussian_filter(blind, 0.6))
    apply_gold(rng, s, ndimage.gaussian_filter(gold, 0.5), spec.wear)

    # --- envejecimiento ---
    on_spine = smoothstep(sx0 - 3, sx0 + 3, xx) * smoothstep(sx1 + 3, sx1 - 3, xx)
    fade = spec.fade * (0.75 + 0.5 * fbm(rng, H, W, 120, 3)) * on_spine            # lomo descolorido por el sol
    faded = desaturate(s.color, 0.5) * 1.18 + 0.03
    s.color = lerp3(s.color, faded, fade * (1 - s.metal))
    s.color = np.clip(s.color, 0, 1)

    d_edge = np.minimum(yy, H - 1 - yy).astype(float)
    d_hinge = np.minimum(np.abs(xx - sx0), np.abs(xx - sx1)).astype(float)
    d_out = np.minimum(xx, W - 1 - xx).astype(float)
    rub = (1 - smoothstep(0, 22, d_edge)) + 0.7 * (1 - smoothstep(0, 10, d_hinge)) \
        + (1 - smoothstep(0, 18, d_out))
    rub = np.clip(rub, 0, 1) * spec.wear * (0.4 + 0.6 * fbm(rng, H, W, 30, 3))
    worn_col = desaturate(s.color, 0.6) * 0.75 + np.array([0.20, 0.15, 0.10]) * 0.35
    s.color = lerp3(s.color, worn_col, rub * 0.75)
    s.rough = np.clip(s.rough + 0.25 * rub, 0, 1)
    s.metal *= 1 - rub
    # puntas golpeadas (se ve el cartón)
    corner_d = np.minimum(np.hypot(xx - 0, yy - 0), np.hypot(xx - 0, yy - (H - 1)))
    corner_d = np.minimum(corner_d, np.minimum(np.hypot(xx - (W - 1), yy), np.hypot(xx - (W - 1), yy - (H - 1))))
    bumped = (1 - smoothstep(4, 26 * spec.wear + 6, corner_d)) * smoothstep(0.3, 0.7, fbm(rng, H, W, 10, 3))
    s.color = lerp3(s.color, np.array([0.42, 0.33, 0.24]), bumped * 0.85)
    s.height -= 0.06 * bumped
    s.rough = np.maximum(s.rough, bumped * 0.9)
    # rayones y suciedad
    grime = smoothstep(0.62, 0.9, fbm(rng, H, W, 90, 4)) * 0.25 * spec.wear
    s.color = s.color * (1 - grime[..., None])
    for _ in range(int(30 * spec.wear)):
        y0, x0 = rng.uniform(0, H), rng.uniform(0, W)
        ang, ln = rng.normal(0.2, 0.6), rng.uniform(20, 140)
        ts = np.linspace(0, 1, int(ln))
        ys = np.clip((y0 + np.sin(ang) * ln * ts).astype(int), 0, H - 1)
        xs = np.clip((x0 + np.cos(ang) * ln * ts).astype(int), 0, W - 1)
        s.color[ys, xs] = lerp3(s.color[ys, xs], s.color[ys, xs] * 1.25 + 0.04, 0.6)
        s.height[ys, xs] -= 0.012
        s.rough[ys, xs] = np.minimum(1, s.rough[ys, xs] + 0.2)
    s.color = np.clip(s.color, 0, 1)
    return s


# ----------------------------------------------------------------------------
# Cantos de páginas
# ----------------------------------------------------------------------------
def pages(rng, spec: BookSpec, w: int, h: int, region: str) -> Surf:
    """u (columnas) = eje largo, v (filas) = grosor del libro. Las tapas quedan en las filas extremas."""
    paper = PAPER * rng.uniform(0.92, 1.02) * (1 - 0.12 * spec.wear * np.array([0.0, 0.3, 0.9]))
    s = Surf.new(h, w, paper, 0.8)
    board = max(3, round(h * 0.035))
    rows = np.arange(h)
    # hojas: rayas finas paralelas al eje largo, con ondulación suave
    shift = (fbm(rng, 1, w, 60, 3)[0] - 0.5) * 3.0
    leaves_1d = fbm(rng, h * 4, 1, 1.2, 2)[:, 0]
    yy, xx = np.mgrid[0:h, 0:w]
    sample = np.clip(((yy + shift[None, :]) * 4).astype(int), 0, h * 4 - 1)
    leaves = leaves_1d[sample]
    s.color = s.color * (0.88 + 0.16 * leaves)[..., None]
    s.height = 0.47 + 0.012 * leaves
    gathered = fbm(rng, h, w, 25, 3)
    s.color *= (0.94 + 0.08 * gathered)[..., None]

    if spec.edge == "gilt" and region in ("top", "fore"):
        gm = np.ones((h, w)) * (0.9 + 0.1 * leaves)
        apply_gold(rng, s, gm, spec.wear * 0.25, depth=0.0)       # canto dorado: liso, poco gastado
    elif spec.edge == "sprinkled":
        spr = (rng.random((h, w)) < 0.025).astype(float)
        spr = ndimage.gaussian_filter(spr, 0.7) * 3
        s.color = lerp3(s.color, np.array(spec.tint) * 0.8, np.clip(spr, 0, 0.9))
    elif spec.edge == "tinted":
        s.color = lerp3(s.color, np.array(spec.tint), 0.55)

    # manchas (foxing) y polvo en la cabeza
    fox = smoothstep(0.78, 0.95, fbm(rng, h, w, 12, 3)) * spec.wear
    s.color = lerp3(s.color, np.array([0.55, 0.38, 0.22]), fox * 0.5)
    if region == "top":
        s.color *= (1 - 0.18 * spec.wear * fbm(rng, h, w, 40, 3))[..., None]

    # tapas (cartón forrado) a ambos lados del bloque de páginas
    if spec.style == "cloth":
        bcol = np.asarray(spec.cover)
    elif spec.style == "half":
        bcol = np.asarray(spec.marble[0]) * 0.8
    else:
        bcol = np.asarray(spec.cover)
    shade = np.clip(1 - np.abs(rows - (h - 1) / 2) / (h / 2), 0, 1)       # leve concavidad del canto
    s.height += 0.006 * shade[:, None]
    for sl in (np.s_[:board], np.s_[h - board:]):
        s.color[sl] = bcol * 0.75 * (0.9 + 0.2 * rng.random((s.color[sl].shape[0], w, 1)))
        s.height[sl] = 0.53
        s.rough[sl] = 0.7
        s.metal[sl] = 0
    # sombra entre tapa y páginas (cejas)
    gap = (np.abs(rows - board) < 2) | (np.abs(rows - (h - board - 1)) < 2)
    s.height[gap] = 0.42

    # cabeza/pie: el lomo de cuero cubre el extremo u=0 y lleva cabezada de colores
    if region in ("top", "bottom"):
        cap = max(4, round(w * 0.035))
        spine_c = np.asarray(spec.spine_mat)
        s.color[:, :cap] = spine_c * (0.8 + 0.25 * rng.random((h, cap, 1)))
        s.height[:, :cap] = 0.55
        s.rough[:, :cap] = 0.6
        s.metal[:, :cap] = 0
        hb = max(3, round(w * 0.012))
        stripes = ((rows // 3) % 2).astype(bool)
        c1, c2 = np.array(spec.label) * 1.4 + 0.05, np.array([0.85, 0.80, 0.65])
        s.color[:, cap:cap + hb] = np.where(stripes[:, None, None], c1, c2)
        s.height[:, cap:cap + hb] = 0.52
    s.color = np.clip(s.color, 0, 1)
    return s


# ----------------------------------------------------------------------------
# Derivados y salida
# ----------------------------------------------------------------------------
def height_to_normal(h, strength):
    p = np.pad(h, 1, mode="edge")
    dx = ndimage.sobel(p, axis=1)[1:-1, 1:-1] / 8.0
    dy = ndimage.sobel(p, axis=0)[1:-1, 1:-1] / 8.0
    n = np.stack([-dx * strength, dy * strength, np.ones_like(h)], -1)
    n /= np.linalg.norm(n, axis=-1, keepdims=True)
    return n * 0.5 + 0.5


def height_to_ao(h, strength=1.0):
    c1 = np.clip(ndimage.gaussian_filter(h, 2.5, mode="nearest") - h, 0, None)
    c2 = np.clip(ndimage.gaussian_filter(h, 9, mode="nearest") - h, 0, None)
    return np.clip(1 - strength * (c1 * 5 + c2 * 3), 0.2, 1)


def place(canvas, data, r: Rect, pad: int):
    extra = ((pad, pad), (pad, pad)) + (((0, 0),) if data.ndim == 3 else ())
    big = np.pad(data, extra, mode="edge")
    y0, x0 = r.y - pad, r.x - pad
    canvas[y0:y0 + big.shape[0], x0:x0 + big.shape[1]] = big


def save(arr, path, mode):
    a = (np.clip(arr, 0, 1) * 255 + 0.5).astype(np.uint8)
    Image.fromarray(a).save(path, optimize=True)


def build_book(spec: BookSpec, L: dict[str, Rect], size: int, seed: int, fonts, normal_strength):
    rng = np.random.default_rng([seed, spec.idx, zlib.crc32(spec.title.encode())])
    color = np.zeros((size, size, 3)) + 0.3
    height = np.full((size, size), 0.5)
    rough = np.full((size, size), 0.7)
    metal = np.zeros((size, size))
    normal = np.zeros((size, size, 3)) + (0.5, 0.5, 1.0)
    ao = np.ones((size, size))
    pad = max(8, size // 128)
    parts = [("wrap", build_wrap(rng, spec, L, fonts))]
    for reg in ("fore", "top", "bottom"):
        r = L[reg]
        parts.append((reg, pages(rng, spec, r.w, r.h, reg)))
    for name, s in parts:
        r = L[name]
        place(color, s.color, r, pad)
        place(rough, s.rough, r, pad)
        place(metal, s.metal, r, pad)
        place(normal, height_to_normal(s.height, normal_strength), r, pad)
        place(ao, height_to_ao(s.height), r, pad)
    return color, rough, metal, normal, ao


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", type=Path, default=Path("out"))
    ap.add_argument("--count", type=int, default=12)
    ap.add_argument("--size", type=int, default=2048, choices=[1024, 2048, 4096])
    ap.add_argument("--seed", type=int, default=3)
    ap.add_argument("--thickness", type=float, default=0.25)
    ap.add_argument("--height", type=float, default=1.29)
    ap.add_argument("--depth", type=float, default=0.9)
    ap.add_argument("--normal-strength", type=float, default=10.0)
    args = ap.parse_args()

    fonts = find_fonts()
    L = build_layout(args.size, args.thickness, args.height, args.depth)
    args.out.mkdir(parents=True, exist_ok=True)
    rng = np.random.default_rng(args.seed)
    pool = list(TITLES)
    specs = []
    for i in range(args.count):
        if not pool:
            pool = list(TITLES)
        specs.append(make_spec(rng, i, pool))

    spines = []
    for spec in specs:
        name = f"Book_{spec.idx + 1:02d}"
        d = args.out / name
        d.mkdir(exist_ok=True)
        color, rough, metal, normal, ao = build_book(spec, L, args.size, args.seed, fonts, args.normal_strength)
        save(color, d / f"{name}_basecolor.png", "RGB")
        save(normal, d / f"{name}_normal.png", "RGB")
        save(ao, d / f"{name}_ao.png", "L")
        ms = np.stack([metal, metal, metal, 1 - rough], -1)
        save(ms, d / f"{name}_metallic_smoothness.png", "RGBA")
        r = L["spine"]
        spines.append(color[r.y:r.y + r.h, r.x:r.x + r.w])
        print(f"  {name}: {spec.style:7s} | {spec.title} — {spec.author} | cantos {spec.edge}")

    layout = {"atlasSize": args.size,
              "dimensions": [args.thickness, args.height, args.depth],
              **{k: L[k].uv(args.size) for k in ("back", "spine", "front", "fore", "top", "bottom")},
              "books": [{"name": f"Book_{s.idx + 1:02d}", "title": s.title, "author": s.author,
                         "style": s.style, "edges": s.edge} for s in specs]}
    (args.out / "book_layout.json").write_text(json.dumps(layout, indent=2, ensure_ascii=False), encoding="utf-8")
    gap = np.ones((spines[0].shape[0], 12, 3)) * 0.12
    strip = np.concatenate([x for s in spines for x in (s, gap)][:-1], axis=1)
    save(strip, args.out / "books_preview.png", "RGB")
    print(f"Listo -> {args.out.resolve()}")


if __name__ == "__main__":
    main()
