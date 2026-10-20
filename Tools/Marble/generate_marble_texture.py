#!/usr/bin/env python3
"""
generate_marble_texture.py
Canica de vidrio con espiral de color (estilo "ojo de gato" retorcido).

Dos juegos de texturas:
  Marble_Vanes_*  atlas 2048x1024 con 4 franjas (una por aleta interior): azul, rojo, amarillo, verde.
                  u = radio (0 = eje, 1 = borde), v = altura. Alfa = recorte del borde deshilachado.
  Marble_Glass_*  equirectangular 2048x1024 de la cáscara de vidrio: tinte, burbujas, rayones (alfa = opacidad).
Cada juego: _basecolor (RGBA), _normal, _metallic_smoothness (R metal, A suavidad).

Uso: python generate_marble_texture.py --out ./out --seed 7
"""
from __future__ import annotations

import argparse
from pathlib import Path

import numpy as np
from PIL import Image
from scipy import ndimage

F = np.float32
VANE_COLORS = [(0.04, 0.20, 0.78), (0.86, 0.07, 0.05), (0.99, 0.70, 0.04), (0.03, 0.58, 0.33)]


class Noise3D:
    def __init__(self, seed, size=64):
        self.n = size
        self.g = np.random.default_rng(seed).random((size, size, size)).astype(F)

    def __call__(self, p):
        n, g = self.n, self.g
        pi = np.floor(p).astype(np.int64)
        f = p - pi
        f = f * f * f * (f * (f * 6 - 15) + 10)
        x0, y0, z0 = pi[..., 0] % n, pi[..., 1] % n, pi[..., 2] % n
        x1, y1, z1 = (x0 + 1) % n, (y0 + 1) % n, (z0 + 1) % n
        fx, fy, fz = f[..., 0], f[..., 1], f[..., 2]
        c00 = g[x0, y0, z0] * (1 - fx) + g[x1, y0, z0] * fx
        c10 = g[x0, y1, z0] * (1 - fx) + g[x1, y1, z0] * fx
        c01 = g[x0, y0, z1] * (1 - fx) + g[x1, y0, z1] * fx
        c11 = g[x0, y1, z1] * (1 - fx) + g[x1, y1, z1] * fx
        return (c00 * (1 - fy) + c10 * fy) * (1 - fz) + (c01 * (1 - fy) + c11 * fy) * fz

    def fbm(self, p, octaves=4):
        acc, amp, tot, q = 0.0, 1.0, 0.0, p.astype(F)
        for o in range(octaves):
            acc = acc + amp * self(q + o * 19.3)
            tot += amp
            amp *= 0.5
            q = q * 2.03
        return acc / tot


def smoothstep(a, b, x):
    t = np.clip((x - a) / (b - a), 0, 1)
    return t * t * (3 - 2 * t)


def normal_from(h, strength, xscale=None):
    p = np.pad(h, 1, mode="edge")
    gx = ndimage.sobel(p, axis=1)[1:-1, 1:-1] / 8.0
    gy = ndimage.sobel(p, axis=0)[1:-1, 1:-1] / 8.0
    if xscale is not None:
        gx = gx * xscale
    n = np.stack([-gx * strength, gy * strength, np.ones_like(h)], -1)
    return n / np.linalg.norm(n, axis=-1, keepdims=True) * 0.5 + 0.5


def save(arr, path):
    Image.fromarray((np.clip(arr, 0, 1) * 255 + 0.5).astype(np.uint8)).save(path, optimize=True)


def vanes(W, H, nz):
    """Atlas de 4 franjas; cada franja w=W/4. Margen interno: la UV usa u in [k+0.016, k+0.984]/4."""
    sw = W // 4
    col = np.zeros((H, W, 4), F)
    hgt = np.zeros((H, W), F)
    sm = np.zeros((H, W), F)
    for k, base in enumerate(VANE_COLORS):
        xs = ((np.arange(sw, dtype=F) + 0.5) / sw - 0.016) / 0.968
        ys = 1 - (np.arange(H, dtype=F) + 0.5) / H
        X, Y = np.meshgrid(xs, ys)
        P = np.stack([X * 3, Y * 4, np.full_like(X, k * 7.1)], -1)
        warp = nz[0].fbm(P, 4) - 0.5
        streak = 0.5 + 0.5 * np.sin(2 * np.pi * (X * 6.5 + warp * 1.6))
        fine = nz[1].fbm(np.stack([X * 40, Y * 6, np.full_like(X, k * 3.3)], -1), 3)
        shade = 0.62 + 0.45 * streak + 0.25 * (fine - 0.5)
        c = np.array(base, F) * shade[..., None]
        core = 1 - smoothstep(0.0, 0.16, X)                       # cerca del eje: más claro, lechoso
        c = c * (1 - 0.45 * core[..., None]) + np.array([0.95, 0.93, 0.88], F) * 0.45 * core[..., None]
        c = c + 0.25 * smoothstep(0.75, 0.95, streak * fine)[..., None]   # destellos dentro de la cinta
        edge = 0.80 + 0.22 * (nz[2].fbm(np.stack([Y * 9, X * 2, np.full_like(X, k * 5.0)], -1), 4) - 0.5)
        a = 1 - smoothstep(edge - 0.06, edge + 0.01, X)
        holes = smoothstep(0.30, 0.42, nz[3].fbm(np.stack([X * 14, Y * 3, np.full_like(X, k)], -1), 3))
        a = a * (0.25 + 0.75 * holes) * (1 - smoothstep(0.985, 1.0, X))
        x0 = k * sw
        col[:, x0:x0 + sw, :3] = np.clip(c, 0, 1)
        col[:, x0:x0 + sw, 3] = a
        hgt[:, x0:x0 + sw] = streak * 0.6 + fine * 0.4
        sm[:, x0:x0 + sw] = 0.88 - 0.1 * fine
    return col, normal_from(hgt, 2.0), sm


def glass(W, H, nz, rng):
    u = (np.arange(W, dtype=F) + 0.5) / W
    v = 1 - (np.arange(H, dtype=F) + 0.5) / H
    U, V = np.meshgrid(u, v)
    phi, th = U * 2 * np.pi, V * np.pi
    D = np.stack([np.sin(th) * np.cos(phi), -np.cos(th), np.sin(th) * np.sin(phi)], -1).astype(F)
    col = np.zeros((H, W, 4), F)
    col[..., :3] = (0.74, 0.90, 0.92)
    alpha = np.full((H, W), 0.07, F)
    smooth = np.full((H, W), 0.96, F)
    # rayones y zonas esmeriladas por uso (más opacos, menos brillantes)
    scuff = smoothstep(0.62, 0.8, nz[0].fbm(D * 9 + 3, 4)) * smoothstep(0.5, 0.75, nz[1].fbm(D * np.array([90, 8, 90], F), 2))
    alpha += 0.22 * scuff
    smooth -= 0.35 * scuff
    col[..., :3] = col[..., :3] * (1 - 0.3 * scuff[..., None]) + 0.3 * scuff[..., None]
    # burbujas atrapadas en el vidrio
    flat = D.reshape(-1, 3)
    bub = np.zeros(flat.shape[0], F)
    for _ in range(220):
        c = rng.normal(size=3); c /= np.linalg.norm(c)
        r = rng.uniform(0.006, 0.022)
        ang = np.arccos(np.clip(flat @ c.astype(F), -1, 1))
        m = ang < r * 1.6
        if not m.any():
            continue
        d = ang[m] / r
        ring = np.exp(-((d - 1.0) / 0.18) ** 2) + 0.25 * (d < 1)
        bub[m] = np.maximum(bub[m], ring)
    bub = bub.reshape(H, W)
    alpha += 0.45 * np.clip(bub, 0, 1)
    col[..., :3] = col[..., :3] * (1 - 0.5 * bub[..., None]) + 0.5 * bub[..., None]
    col[..., 3] = np.clip(alpha, 0, 1)
    # ondulación leve del vidrio + rayones como altura
    h = 0.5 * nz[2].fbm(D * 4, 3) + 0.3 * scuff
    coslat = np.maximum(np.sin(th), 0.15)
    return col, normal_from(h, 3.0, xscale=1 / coslat), smooth


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", type=Path, default=Path("out"))
    ap.add_argument("--seed", type=int, default=7)
    a = ap.parse_args()
    a.out.mkdir(parents=True, exist_ok=True)
    rng = np.random.default_rng(a.seed)
    nz = [Noise3D(a.seed * 10 + j) for j in range(4)]
    for name, (c, n, s) in (("Vanes", vanes(2048, 1024, nz)), ("Glass", glass(2048, 1024, nz, rng))):
        save(c, a.out / f"Marble_{name}_basecolor.png")
        save(n, a.out / f"Marble_{name}_normal.png")
        z = np.zeros_like(s)
        save(np.stack([z, z, z, s], -1), a.out / f"Marble_{name}_metallic_smoothness.png")
    print(f"Listo -> {a.out.resolve()}")


if __name__ == "__main__":
    main()
