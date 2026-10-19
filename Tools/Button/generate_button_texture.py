#!/usr/bin/env python3
"""
generate_button_texture.py
Tapa del botón: plástico rojo brillante con el texto "PUSH" en blanco y un filete blanco cerca del borde.

Proyección planar desde arriba con el texto DERECHO en la imagen (u = derecha, v = arriba).
ButtonSetup.cs gira esas UV para que el texto quede mirando a la Main Camera.

Salidas: Button_basecolor.png  Button_emission.png  Button_normal.png  Button_metallic_smoothness.png
Uso:     python generate_button_texture.py --out ./out --text PUSH
"""
from __future__ import annotations

import argparse
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont
from scipy import ndimage

F = np.float32
FONTS = ["/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
         "/usr/share/fonts/truetype/liberation/LiberationSans-Bold.ttf",
         "C:/Windows/Fonts/arialbd.ttf"]


def smoothstep(a, b, x):
    t = np.clip((x - a) / (b - a), 0, 1)
    return t * t * (3 - 2 * t)


def mix(c, col, t):
    return c * (1 - t[..., None]) + np.asarray(col, F) * t[..., None]


def text_mask(text, S, width_frac):
    font_path = next((f for f in FONTS if Path(f).is_file()), None)
    size = S
    while True:
        font = ImageFont.truetype(font_path, size) if font_path else ImageFont.load_default()
        img = Image.new("L", (S, S), 0)
        d = ImageDraw.Draw(img)
        b = d.textbbox((0, 0), text, font=font)
        if b[2] - b[0] <= S * width_frac or size < 10:
            break
        size = int(size * 0.95)
    d.text(((S - (b[2] - b[0])) / 2 - b[0], (S - (b[3] - b[1])) / 2 - b[1]), text, font=font, fill=255)
    return np.asarray(img.filter(ImageFilter.GaussianBlur(0.8)), F) / 255.0


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", type=Path, default=Path("out"))
    ap.add_argument("--size", type=int, default=1024)
    ap.add_argument("--text", default="PUSH")
    ap.add_argument("--seed", type=int, default=2)
    a = ap.parse_args()
    a.out.mkdir(parents=True, exist_ok=True)
    S = a.size
    rng = np.random.default_rng(a.seed)

    c = (np.arange(S, dtype=F) + 0.5) / S * 2 - 1                   # -1..1
    X, Yv = np.meshgrid(c, -c)                                        # Yv: +1 arriba de la imagen
    r = np.hypot(X, Yv)

    red = np.array([0.80, 0.03, 0.025], F)
    color = np.broadcast_to(red, (S, S, 3)).astype(F) * (1.0 - 0.18 * r ** 2)[..., None]   # más oscuro hacia el borde
    smooth = np.full((S, S), 0.9, F)
    height = np.zeros((S, S), F)

    txt = text_mask(a.text, S, 0.62)
    ring = smoothstep(0.012, 0.004, np.abs(r - 0.86))                 # filete blanco
    white = np.maximum(txt, ring)
    color = mix(color, [0.96, 0.95, 0.92], white)
    smooth = smooth * (1 - white) + 0.7 * white
    height += 1.0 * ndimage.gaussian_filter(white, 1.0)                # tinta en leve relieve

    # micro-rayones de uso (más en el centro, donde se presiona)
    scratch = np.zeros((S, S), F)
    for _ in range(90):
        x0, y0 = rng.normal(S / 2, S / 6, 2)
        ang, ln = rng.uniform(0, np.pi), rng.uniform(10, 60)
        ts = np.linspace(0, 1, int(ln))
        xs = np.clip((x0 + np.cos(ang) * ln * ts).astype(int), 0, S - 1)
        ys = np.clip((y0 + np.sin(ang) * ln * ts).astype(int), 0, S - 1)
        scratch[ys, xs] = rng.uniform(0.3, 1)
    scratch = ndimage.gaussian_filter(scratch, 0.5)
    smooth -= 0.25 * scratch
    height -= 0.4 * scratch

    # emisión: el rojo brilla suave y el texto/filete un poco más
    emission = mix(np.broadcast_to(red * 0.55, (S, S, 3)).astype(F), [1.0, 0.9, 0.85], white)

    p = np.pad(height, 1, mode="edge")
    gx = ndimage.sobel(p, axis=1)[1:-1, 1:-1] / 8.0
    gy = ndimage.sobel(p, axis=0)[1:-1, 1:-1] / 8.0
    n = np.stack([-gx * 1.2, gy * 1.2, np.ones_like(height)], -1)
    n /= np.linalg.norm(n, axis=-1, keepdims=True)

    def save(arr, name):
        Image.fromarray((np.clip(arr, 0, 1) * 255 + 0.5).astype(np.uint8)).save(a.out / name, optimize=True)

    save(np.clip(color, 0, 1), "Button_basecolor.png")
    save(emission, "Button_emission.png")
    save(n * 0.5 + 0.5, "Button_normal.png")
    z = np.zeros((S, S), F)
    save(np.stack([z, z, z, np.clip(smooth, 0, 1)], -1), "Button_metallic_smoothness.png")
    print(f"Listo -> {a.out.resolve()}")


if __name__ == "__main__":
    main()
