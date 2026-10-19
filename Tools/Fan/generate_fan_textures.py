#!/usr/bin/env python3
"""
generate_fan_textures.py
Texturas REPETIBLES (sin costuras) para el ventilador vintage:
  FanBrass_*   latón cepillado (cepillado a lo largo de u) con leve pátina
  FanEnamel_*  hierro fundido esmaltado verde oscuro, con saltaduras que dejan ver el metal
Cada una: _basecolor.png, _normal.png, _metallic_smoothness.png (R = metallic, A = smoothness)
Uso: python generate_fan_textures.py --out ./out --size 1024
"""
from __future__ import annotations

import argparse
from pathlib import Path

import numpy as np
from PIL import Image
from scipy import ndimage

F = np.float32


def tnoise(rng, size, cells_y, cells_x, octaves=4):
    """Ruido fractal periódico (se repite sin costura en ambos ejes)."""
    acc, amp, tot = np.zeros((size, size), F), 1.0, 0.0
    cy, cx = cells_y, cells_x
    for _ in range(octaves):
        g = rng.random((int(cy), int(cx))).astype(F)
        z = ndimage.zoom(g, (size / int(cy), size / int(cx)), order=3, mode="grid-wrap", grid_mode=True)
        acc += amp * z[:size, :size]
        tot += amp
        amp *= 0.5
        cy, cx = min(cy * 2, size), min(cx * 2, size)
    acc /= tot
    return (acc - acc.min()) / (np.ptp(acc) + 1e-9)


def smoothstep(a, b, x):
    t = np.clip((x - a) / (b - a), 0, 1)
    return t * t * (3 - 2 * t)


def mix(c, col, t):
    return c * (1 - t[..., None]) + np.asarray(col, F) * t[..., None]


def normal_from(h, k):
    p = np.pad(h, 1, mode="wrap")
    gx = ndimage.sobel(p, axis=1)[1:-1, 1:-1] / 8.0
    gy = ndimage.sobel(p, axis=0)[1:-1, 1:-1] / 8.0
    n = np.stack([-gx * k, gy * k, np.ones_like(h)], -1)
    return n / np.linalg.norm(n, axis=-1, keepdims=True) * 0.5 + 0.5


def save(arr, path):
    Image.fromarray((np.clip(arr, 0, 1) * 255 + 0.5).astype(np.uint8)).save(path, optimize=True)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", type=Path, default=Path("out"))
    ap.add_argument("--size", type=int, default=1024)
    ap.add_argument("--seed", type=int, default=9)
    a = ap.parse_args()
    a.out.mkdir(parents=True, exist_ok=True)
    S = a.size
    rng = np.random.default_rng(a.seed)

    # ---------------- latón cepillado ----------------
    brush = 0.6 * tnoise(rng, S, 256, 4, 3) + 0.4 * tnoise(rng, S, 512, 8, 2)   # rayas a lo largo de u
    alloy = tnoise(rng, S, 6, 6, 4)
    col = mix(np.broadcast_to(np.array([0.82, 0.64, 0.36], F), (S, S, 3)).copy(), [0.68, 0.48, 0.26],
              smoothstep(0.4, 0.8, alloy) * 0.5)
    col *= (0.88 + 0.22 * brush)[..., None]
    patina = smoothstep(0.6, 0.85, tnoise(rng, S, 8, 8, 4))
    col = mix(col, [0.38, 0.28, 0.15], patina * 0.28)
    smooth = 0.66 + 0.15 * (brush - 0.5) - 0.3 * patina
    metal = 1 - 0.3 * patina
    save(col, a.out / "FanBrass_basecolor.png")
    save(normal_from(brush * 0.6, 1.2), a.out / "FanBrass_normal.png")
    save(np.stack([metal, metal, metal, np.clip(smooth, 0, 1)], -1), a.out / "FanBrass_metallic_smoothness.png")

    # ---------------- hierro esmaltado ----------------
    cast = tnoise(rng, S, 24, 24, 5)                                     # piel de fundición bajo el esmalte
    col = np.broadcast_to(np.array([0.06, 0.12, 0.09], F), (S, S, 3)).copy() * (0.85 + 0.3 * tnoise(rng, S, 10, 10))[..., None]
    chips_n = tnoise(rng, S, 40, 40, 4)
    chips = smoothstep(0.84, 0.88, chips_n)
    rust_edge = smoothstep(0.80, 0.84, chips_n) * (1 - chips)
    col = mix(col, [0.32, 0.30, 0.28], chips)                              # metal desnudo
    col = mix(col, [0.30, 0.16, 0.08], rust_edge * 0.6)                    # óxido en el borde de la saltadura
    smooth = 0.72 - 0.08 * cast - 0.35 * chips - 0.3 * rust_edge
    metal = 0.05 + 0.75 * chips
    height = 0.25 * cast - 1.0 * chips
    save(col, a.out / "FanEnamel_basecolor.png")
    save(normal_from(height, 1.5), a.out / "FanEnamel_normal.png")
    save(np.stack([metal, metal, metal, np.clip(smooth, 0, 1)], -1), a.out / "FanEnamel_metallic_smoothness.png")
    print(f"Listo -> {a.out.resolve()}")


if __name__ == "__main__":
    main()
