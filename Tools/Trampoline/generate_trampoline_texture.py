#!/usr/bin/env python3
"""
generate_trampoline_texture.py
Lona de trampolín: polipropileno negro tejido, borde de cinta azul cosida y anillas metálicas
donde enganchan los resortes.

Proyección planar desde arriba (la usa TrampolineSetup.cs):
  u = x / (2 * matRadius) + 0.5      v = z / (2 * matRadius) + 0.5
  Resorte k en el ángulo  2*pi*k/N  (x = r cos, z = r sin)  -> anilla en ese mismo ángulo.

Salidas: Trampoline_basecolor.png  Trampoline_normal.png  Trampoline_metallic_smoothness.png
Uso:     python generate_trampoline_texture.py --out ./out --springs 36 --mat-radius 0.5
"""
from __future__ import annotations

import argparse
from pathlib import Path

import numpy as np
from PIL import Image
from scipy import ndimage

F = np.float32
BLACK = np.array([0.035, 0.035, 0.04], F)
BLUE = np.array([0.04, 0.22, 0.70], F)
THREAD = np.array([0.80, 0.80, 0.78], F)
STEEL = np.array([0.72, 0.72, 0.74], F)


def fbm(rng, h, w, cell, octaves=4):
    acc, amp, tot = np.zeros((h, w), F), 1.0, 0.0
    for _ in range(octaves):
        gh, gw = int(h / cell) + 3, int(w / cell) + 3
        acc += amp * ndimage.zoom(rng.random((gh, gw)).astype(F), (h / (gh - 1), w / (gw - 1)), order=3)[:h, :w]
        tot += amp
        amp *= 0.5
        cell = max(cell / 2, 1.5)
    acc /= tot
    return (acc - acc.min()) / (np.ptp(acc) + 1e-9)


def smoothstep(a, b, x):
    t = np.clip((x - a) / (b - a), 0, 1)
    return t * t * (3 - 2 * t)


def mix(c, col, t):
    return c * (1 - t[..., None]) + col * t[..., None]


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", type=Path, default=Path("out"))
    ap.add_argument("--size", type=int, default=2048)
    ap.add_argument("--mat-radius", type=float, default=0.5)
    ap.add_argument("--border", type=float, default=0.06, help="ancho de la cinta azul (m)")
    ap.add_argument("--springs", type=int, default=36)
    ap.add_argument("--seed", type=int, default=6)
    a = ap.parse_args()
    a.out.mkdir(parents=True, exist_ok=True)
    rng = np.random.default_rng(a.seed)
    S, R = a.size, a.mat_radius
    px = 2 * R / S                                               # metros por píxel

    c = (np.arange(S, dtype=F) + 0.5) / S
    X = (c - 0.5) * 2 * R
    Z = (0.5 - c) * 2 * R                                         # fila 0 = v 1 = +Z
    X, Z = np.meshgrid(X, Z)
    r = np.hypot(X, Z)
    th = np.arctan2(Z, X)
    xx, yy = np.meshgrid(np.arange(S), np.arange(S))

    # --- lona negra: tejido de cesta muy fino ---
    p = 5.0
    warp = 0.5 + 0.5 * np.sin(2 * np.pi * xx / p)
    weft = 0.5 + 0.5 * np.sin(2 * np.pi * yy / p)
    cell = ((xx // p + yy // p) % 2).astype(bool)
    weave = np.where(cell, warp, weft).astype(F)
    color = np.broadcast_to(BLACK, (S, S, 3)).astype(F) * (0.75 + 0.5 * weave)[..., None]
    smooth = 0.42 + 0.18 * weave
    height = 0.6 * weave
    # zona central más gastada (polvo y marcas de rebote)
    wear = np.exp(-(r / (R * 0.45)) ** 2) * fbm(rng, S, S, 90, 4)
    color = color + (0.05 * wear)[..., None]
    smooth -= 0.12 * wear

    # --- cinta azul del borde ---
    r_in = R - a.border
    band = smoothstep(r_in - px, r_in + px, r) * (1 - smoothstep(R - px, R + px, r))
    diag = 0.5 + 0.5 * np.sin(2 * np.pi * (xx + yy) / 4.0)          # sarga diagonal de la cinta
    blue = BLUE * (0.8 + 0.3 * diag)[..., None] * (0.9 + 0.2 * fbm(rng, S, S, 40, 3))[..., None]
    color = mix(color, blue, band)
    smooth = smooth * (1 - band) + (0.5 + 0.1 * diag) * band
    height = height * (1 - band) + (0.9 + 0.3 * diag) * band
    # costuras: dos líneas punteadas concéntricas
    for rr in (r_in + 0.008, R - 0.008):
        line = np.clip(1 - np.abs(r - rr) / (1.6 * px), 0, 1)
        dash = (np.sin(th * rr / 0.004) > -0.2).astype(F)          # puntadas de ~4 mm
        st = line * dash
        color = mix(color, THREAD, st * 0.85)
        height += 0.5 * st

    # --- anillas en D (una por resorte) ---
    metal = np.zeros((S, S), F)
    ring_r0, ring_r1 = R - 0.022, R + 0.002
    half_w = 0.014
    for k in range(a.springs):
        ang = 2 * np.pi * k / a.springs
        ca, sa = np.cos(ang), np.sin(ang)
        radial = X * ca + Z * sa
        tang = -X * sa + Z * ca
        outer = (radial > ring_r0) & (radial < ring_r1) & (np.abs(tang) < half_w)
        inner = (radial > ring_r0 + 0.005) & (radial < ring_r1 - 0.004) & (np.abs(tang) < half_w - 0.005)
        ring = (outer & ~inner).astype(F)
        tab = ((radial > ring_r0 - 0.018) & (radial < ring_r0 + 0.002) & (np.abs(tang) < half_w * 0.8)).astype(F)
        color = mix(color, BLUE * 0.6, tab)                         # lengüeta de cinta que sujeta la anilla
        height += 0.4 * tab
        metal = np.maximum(metal, ring)
    metal = ndimage.gaussian_filter(metal, 0.7)
    color = mix(color, STEEL * (0.85 + 0.2 * fbm(rng, S, S, 6, 2))[..., None], metal)
    smooth = smooth * (1 - metal) + 0.6 * metal
    height += 1.5 * metal

    outside = r > R + 0.004
    color[outside] = BLACK
    color = np.clip(color, 0, 1)
    smooth = np.clip(smooth, 0, 1)

    hp = np.pad(height, 1, mode="edge")
    gx = ndimage.sobel(hp, axis=1)[1:-1, 1:-1] / 8.0
    gy = ndimage.sobel(hp, axis=0)[1:-1, 1:-1] / 8.0
    n = np.stack([-gx * 0.9, gy * 0.9, np.ones_like(height)], -1)
    n /= np.linalg.norm(n, axis=-1, keepdims=True)

    def save(arr, name):
        Image.fromarray((np.clip(arr, 0, 1) * 255 + 0.5).astype(np.uint8)).save(a.out / name, optimize=True)

    save(color, "Trampoline_basecolor.png")
    save(n * 0.5 + 0.5, "Trampoline_normal.png")
    save(np.stack([metal, metal, metal, smooth], -1), "Trampoline_metallic_smoothness.png")
    print(f"Listo -> {a.out.resolve()}")


if __name__ == "__main__":
    main()
