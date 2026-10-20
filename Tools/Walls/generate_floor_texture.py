#!/usr/bin/env python3
"""
generate_floor_texture.py
Suelo de tablones de roble envejecido (mosaico repetible de 4.8 m x 4.8 m).

  24 hileras de tablones de 20 cm a lo largo de X, con largos variables (0.8-2.4 m) y juntas desfasadas,
  veta propia por tablón, nudos ocasionales, clavos en los extremos, juntas oscuras con polvo, desgaste del
  barniz, rayones y manchas. Todo el ruido es periódico -> sin costuras.

Salidas: Floor_basecolor.png  Floor_normal.png  Floor_metallic_smoothness.png
Uso:     python generate_floor_texture.py --out ./out --size 2048 --seed 4
"""
from __future__ import annotations

import argparse
from pathlib import Path

import numpy as np
from PIL import Image
from scipy import ndimage

F = np.float32
TILE_M = 4.8
ROWS = 24


def pnoise(n, rng, scale_px, aniso=(1.0, 1.0)):
    fy = np.fft.fftfreq(n)[:, None] * aniso[0]
    fx = np.fft.fftfreq(n)[None, :] * aniso[1]
    f = np.sqrt(fx ** 2 + fy ** 2) + 1e-6
    amp = f ** -1.0 * (f >= 0.5 / scale_px)
    amp[0, 0] = 0
    y = np.real(np.fft.ifft2(amp * np.exp(1j * rng.uniform(0, 2 * np.pi, (n, n)))))
    return ((y - y.mean()) / (y.std() + 1e-9)).astype(F)


def smoothstep(a, b, x):
    t = np.clip((x - a) / (b - a), 0, 1)
    return t * t * (3 - 2 * t)


def to_normal(h, s):
    gx = (np.roll(h, -1, 1) - np.roll(h, 1, 1)) * 0.5
    gy = (np.roll(h, 1, 0) - np.roll(h, -1, 0)) * 0.5
    nrm = np.stack([-gx * s, -gy * s, np.ones_like(h)], -1)
    return nrm / np.linalg.norm(nrm, axis=-1, keepdims=True) * 0.5 + 0.5


def save(a, p):
    Image.fromarray((np.clip(a, 0, 1) * 255 + 0.5).astype(np.uint8)).save(p, optimize=True)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", type=Path, default=Path("out"))
    ap.add_argument("--size", type=int, default=2048)
    ap.add_argument("--seed", type=int, default=4)
    a = ap.parse_args()
    a.out.mkdir(parents=True, exist_ok=True)
    rng = np.random.default_rng(a.seed)
    n = a.size
    pxm = n / TILE_M
    yy, xx = np.mgrid[0:n, 0:n]
    row = (yy * ROWS) // n                                     # hilera (fila de la imagen)
    ly = (yy - row * n / ROWS) / (n / ROWS)                     # 0..1 a lo ancho del tablón

    # juntas a tope por hilera: cortes circulares con separación mínima
    plank_id = np.zeros((n, n), np.int64)
    lx = np.zeros((n, n), F)                                   # metros desde el inicio del tablón
    plen = np.zeros((n, n), F)
    joints = np.zeros((n, n), F)
    pid = 0
    for r in range(ROWS):
        while True:
            k = rng.integers(2, 6)
            cuts = np.sort(rng.uniform(0, TILE_M, k))
            gaps = np.diff(np.r_[cuts, cuts[0] + TILE_M])
            if gaps.min() > 0.8 and gaps.max() < 2.6:
                break
        y0, y1 = int(r * n / ROWS), int((r + 1) * n / ROWS)
        xm = (np.arange(n) + 0.5) / pxm
        seg = np.searchsorted(cuts, xm, side="right") - 1       # -1 = antes del primer corte (envuelve)
        start = np.where(seg >= 0, cuts[np.maximum(seg, 0)], cuts[-1] - TILE_M)
        length = np.where(seg >= 0, gaps[np.maximum(seg, 0)], gaps[-1])
        seg = np.where(seg < 0, k - 1, seg)
        plank_id[y0:y1] = (pid + seg)[None, :]
        lx[y0:y1] = (xm - start)[None, :]
        plen[y0:y1] = length[None, :]
        pid += k
    npl = pid
    d_end = np.minimum(lx, plen - lx) * pxm                    # px al extremo del tablón
    d_side = np.minimum(ly, 1 - ly) * (n / ROWS)                # px al canto
    gap = 1 - smoothstep(0.6, 2.2, np.minimum(d_end * 1.3, d_side))

    # veta: ruido muy estirado en X, desplazado por tablón para que cada uno tenga la suya
    grain_src = pnoise(n, rng, 300, aniso=(1.0, 18.0))
    fine_src = pnoise(n, rng, 40, aniso=(1.0, 30.0))
    shift_y = rng.integers(0, n, npl)[plank_id]
    shift_x = rng.integers(0, n, npl)[plank_id]
    gy = (yy + shift_y) % n
    gx = (xx + shift_x) % n
    grain = grain_src[gy, gx]
    fine = fine_src[gy, gx]
    rings = 0.5 + 0.5 * np.sin(grain * 3.2 + fine * 0.6)

    tone = rng.normal(0, 1, npl)[plank_id]
    warm = rng.uniform(-1, 1, npl)[plank_id]
    base = np.array([0.34, 0.22, 0.13], F)
    col = base * (1 + 0.12 * tone[..., None]) + np.array([0.03, 0.0, -0.02], F) * warm[..., None]
    col = col * (0.82 + 0.22 * rings[..., None]) * (0.93 + 0.07 * fine[..., None])

    # nudos ocasionales
    knot = np.zeros((n, n), F)
    for _ in range(14):
        cx, cy = rng.uniform(0, n), rng.uniform(0, n)
        rx, ry = rng.uniform(6, 14), rng.uniform(3, 6)
        dx = (xx - cx + n / 2) % n - n / 2
        dy = (yy - cy + n / 2) % n - n / 2
        r2 = (dx / rx) ** 2 + (dy / ry) ** 2
        knot = np.maximum(knot, np.exp(-r2) * (1 + 0.3 * np.sin(np.sqrt(r2) * 6)))
    col = col * (1 - 0.55 * np.clip(knot, 0, 1)[..., None])

    # desgaste del barniz (zonas de paso más claras y mates), manchas y rayones
    wear = smoothstep(0.2, 1.6, pnoise(n, rng, 500))
    stain = smoothstep(1.2, 2.2, pnoise(n, rng, 120)) * 0.35
    scratch = smoothstep(2.2, 3.2, pnoise(n, rng, 60, aniso=(1.0, 6.0)) + 0.6 * pnoise(n, rng, 15))
    col = col * (1 + 0.18 * wear[..., None]) * (1 - stain[..., None]) + 0.06 * scratch[..., None]

    # clavos cerca de los extremos (2 por extremo)
    nails = np.zeros((n, n), F)
    near_end = (d_end < 0.05 * pxm) & (d_end > 0.02 * pxm)
    for frac in (0.28, 0.72):
        band = np.abs(ly - frac) * (n / ROWS) < 2.2
        nails = np.maximum(nails, (near_end & band & (np.abs(d_end - 0.035 * pxm) < 2.2)).astype(F))
    nails = ndimage.gaussian_filter(nails, 0.8)

    dust = np.array([0.10, 0.08, 0.06], F)
    col = col * (1 - gap[..., None]) + dust * gap[..., None]
    col = col * (1 - 0.8 * nails[..., None]) + np.array([0.12, 0.12, 0.13], F) * 0.8 * nails[..., None]

    height = 1 - gap * 0.9 + 0.04 * fine + 0.02 * rings - 0.05 * scratch - 0.15 * nails
    cup = (ly - 0.5) ** 2 * 0.25                                # tablones ligeramente acopados por la edad
    height = height + cup
    smooth = (0.42 - 0.18 * wear - 0.12 * scratch - 0.1 * stain) * (1 - gap) + 0.08 * gap

    save(col, a.out / "Floor_basecolor.png")
    save(to_normal(height.astype(F), n / 300.0), a.out / "Floor_normal.png")
    z = np.zeros((n, n), F)
    save(np.stack([z, z, z, smooth], -1), a.out / "Floor_metallic_smoothness.png")
    print(f"Listo -> {a.out.resolve()}  (1 mosaico = {TILE_M} m, {npl} tablones)")


if __name__ == "__main__":
    main()
