#!/usr/bin/env python3
"""
generate_wall_textures.py
Pared de ladrillo envejecido (aparejo a soga) para la sala de la máquina.

  Brick_*      mosaico repetible de 2.25 m x 2.25 m: 10 ladrillos x 30 hiladas (ladrillo 21.5 x 6.5 cm, junta 1 cm).
               Variación por ladrillo (cocción, tono, desgaste), aristas desconchadas, picado, juntas hundidas.
  WallGrime    mapa de "detalle" de baja frecuencia (gris 0.5 = neutro, x2 en URP) con hollín, humedad y restos
               de cal; se repite cada ~14 m para romper la repetición del ladrillo.
Todo el ruido es periódico (FFT) -> sin costuras.

Uso: python generate_wall_textures.py --out ./out --size 2048 --seed 9
"""
from __future__ import annotations

import argparse
from pathlib import Path

import numpy as np
from PIL import Image
from scipy import ndimage

F = np.float32
TILE_M = 2.25
BRICKS_X, COURSES = 10, 30


def pnoise(n, rng, scale_px, aniso=(1.0, 1.0), power=2.0):
    """Ruido periódico de n x n: espectro ~ 1/f^power con corte a 'scale_px'."""
    fy = np.fft.fftfreq(n)[:, None] * aniso[0]
    fx = np.fft.fftfreq(n)[None, :] * aniso[1]
    f = np.sqrt(fx ** 2 + fy ** 2) + 1e-6
    amp = f ** (-power / 2)                     # fractal 1/f
    amp *= f >= 0.5 / scale_px                  # sin rasgos mayores que ~2x scale_px
    amp[0, 0] = 0
    X = amp * np.exp(1j * rng.uniform(0, 2 * np.pi, (n, n)))
    y = np.real(np.fft.ifft2(X))
    y = (y - y.mean()) / (y.std() + 1e-9)
    return y.astype(F)


def smoothstep(a, b, x):
    t = np.clip((x - a) / (b - a), 0, 1)
    return t * t * (3 - 2 * t)


def to_normal(h, strength):
    gx = (np.roll(h, -1, 1) - np.roll(h, 1, 1)) * 0.5
    gy = (np.roll(h, 1, 0) - np.roll(h, -1, 0)) * 0.5
    n = np.stack([-gx * strength, -gy * strength, np.ones_like(h)], -1)
    return n / np.linalg.norm(n, axis=-1, keepdims=True) * 0.5 + 0.5


def save(a, p):
    Image.fromarray((np.clip(a, 0, 1) * 255 + 0.5).astype(np.uint8)).save(p, optimize=True)


def bricks(n, rng):
    px_m = n / TILE_M
    yy, xx = np.mgrid[0:n, 0:n].astype(F)
    ym = (n - 1 - yy) / px_m                                   # metros desde abajo (v hacia arriba)
    xm = xx / px_m
    course_h = TILE_M / COURSES
    brick_w = TILE_M / BRICKS_X
    joint = 0.010
    row = np.floor(ym / course_h).astype(int) % COURSES
    xs = (xm + (row % 2) * brick_w * 0.5) % TILE_M
    col = np.floor(xs / brick_w).astype(int) % BRICKS_X
    lx = xs - col * brick_w                                    # posición dentro del ladrillo
    ly = ym - np.floor(ym / course_h) * course_h
    # desconchado de aristas: la junta "come" el ladrillo de forma irregular
    chip = pnoise(n, rng, 40) * 0.35 + pnoise(n, rng, 10) * 0.25
    d_edge = np.minimum(np.minimum(lx, brick_w - lx), np.minimum(ly, course_h - ly))   # m al borde
    inside = d_edge - joint * 0.5 - 0.0035 * np.maximum(chip, 0) - 0.0015
    brick_mask = smoothstep(-0.0008, 0.0012, inside)

    bid = row * BRICKS_X + col
    nb = COURSES * BRICKS_X
    tone = rng.normal(0, 1, nb)[bid]
    kind = rng.random(nb)[bid]
    palette_red = np.array([0.50, 0.22, 0.15], F)
    palette_orange = np.array([0.62, 0.33, 0.20], F)
    palette_dark = np.array([0.30, 0.15, 0.12], F)
    palette_pale = np.array([0.66, 0.48, 0.38], F)
    base = np.where((kind < 0.55)[..., None], palette_red,
           np.where((kind < 0.78)[..., None], palette_orange,
           np.where((kind < 0.92)[..., None], palette_dark, palette_pale)))
    base = base * (1 + 0.08 * tone[..., None])
    # superficie del ladrillo: moteado, picado, quemado en un extremo
    mott = pnoise(n, rng, 25)
    pits = smoothstep(1.6, 2.4, pnoise(n, rng, 4))
    flash = smoothstep(0.55, 1.0, (lx / brick_w) * rng.uniform(0.6, 1.4, nb)[bid]) * (rng.random(nb)[bid] < 0.25)
    bc = base * (1 + 0.10 * mott[..., None]) * (1 - 0.35 * pits[..., None]) * (1 - 0.35 * flash[..., None])
    # cal / eflorescencia pegada en algunos ladrillos (restos de una pintura vieja)
    wash = smoothstep(0.9, 1.6, pnoise(n, rng, 180) + 0.5 * pnoise(n, rng, 30)) * 0.55
    bc = bc * (1 - wash[..., None]) + np.array([0.80, 0.77, 0.70], F) * wash[..., None]

    mortar = np.array([0.55, 0.52, 0.47], F) * (1 + 0.12 * pnoise(n, rng, 8)[..., None])
    mortar = mortar * (1 - 0.25 * smoothstep(0.5, 1.5, pnoise(n, rng, 60))[..., None])   # juntas sucias
    color = mortar * (1 - brick_mask[..., None]) + bc * brick_mask[..., None]

    height = brick_mask * (1.0 + 0.06 * mott - 0.5 * pits) + (1 - brick_mask) * (0.15 + 0.08 * pnoise(n, rng, 6))
    height += 0.03 * pnoise(n, rng, 3)
    height = ndimage.uniform_filter(height, 3, mode="wrap")
    smooth = np.where(brick_mask > 0.5, 0.22 - 0.08 * pits + 0.05 * wash, 0.10).astype(F)
    return color, to_normal(height, n / 256.0), smooth


def grime(n, rng):
    g = 0.5 + 0.07 * pnoise(n, rng, n / 2)                     # variación muy amplia de tono
    soot = smoothstep(0.6, 1.8, pnoise(n, rng, n / 3, aniso=(0.35, 1.0))) * 0.20  # chorreones verticales
    damp = smoothstep(0.8, 2.0, pnoise(n, rng, n / 5)) * 0.12
    lime = smoothstep(1.0, 2.0, pnoise(n, rng, n / 4)) * 0.10
    g = g - soot - damp + lime
    return np.repeat(np.clip(g, 0.2, 0.8)[..., None], 3, -1)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", type=Path, default=Path("out"))
    ap.add_argument("--size", type=int, default=2048)
    ap.add_argument("--seed", type=int, default=9)
    a = ap.parse_args()
    a.out.mkdir(parents=True, exist_ok=True)
    rng = np.random.default_rng(a.seed)
    c, nrm, s = bricks(a.size, rng)
    save(c, a.out / "Brick_basecolor.png")
    save(nrm, a.out / "Brick_normal.png")
    z = np.zeros_like(s)
    save(np.stack([z, z, z, s], -1), a.out / "Brick_metallic_smoothness.png")
    save(grime(1024, rng), a.out / "WallGrime_detail.png")
    print(f"Listo -> {a.out.resolve()}  (1 mosaico = {TILE_M} m)")


if __name__ == "__main__":
    main()
