#!/usr/bin/env python3
"""
generate_ball_texture.py
Textura de bola de bolos con remolino marmoleado (morado / negro), en proyección equirectangular.

El patrón se calcula con ruido 3D sobre la dirección de cada texel, así que NO tiene costura
en u = 0/1 ni pellizco en los polos (la malla usa UV equirectangular: u = longitud, v = latitud).

Salidas:
  BowlingBall_basecolor.png            sRGB
  BowlingBall_metallic_smoothness.png  Unity URP: R = metallic (perla), A = smoothness
  BowlingBall_normal.png               micro-rayones muy sutiles (OpenGL)
  BowlingBall_preview.png              vista rápida de la esfera

Uso:
  python generate_ball_texture.py --out ./out --width 4096 --seed 11
Requisitos: numpy, scipy, Pillow
"""
from __future__ import annotations

import argparse
from pathlib import Path

import numpy as np
from PIL import Image
from scipy import ndimage

# Paleta sRGB
BLACK = np.array([0.025, 0.018, 0.035])
DEEP = np.array([0.16, 0.035, 0.26])        # morado profundo
PURPLE = np.array([0.42, 0.10, 0.66])       # morado vivo
LILAC = np.array([0.78, 0.55, 0.95])        # vetas claras (perla)
SMOKE = np.array([0.09, 0.07, 0.12])        # gris humo entre el negro y el morado


# ----------------------------------------------------------------------------
# Ruido de valor 3D (vectorizado)
# ----------------------------------------------------------------------------
class Noise3D:
    def __init__(self, seed: int, size: int = 64):
        rng = np.random.default_rng(seed)
        self.size = size
        self.grid = rng.random((size, size, size)).astype(np.float32)

    def __call__(self, p: np.ndarray) -> np.ndarray:
        """p: (..., 3) en unidades de celda. Devuelve 0-1 con interpolación quintic."""
        n = self.size
        pi = np.floor(p).astype(np.int64)
        f = p - pi
        f = f * f * f * (f * (f * 6 - 15) + 10)
        x0, y0, z0 = (pi[..., 0] % n, pi[..., 1] % n, pi[..., 2] % n)
        x1, y1, z1 = (x0 + 1) % n, (y0 + 1) % n, (z0 + 1) % n
        g = self.grid
        fx, fy, fz = f[..., 0], f[..., 1], f[..., 2]
        c00 = g[x0, y0, z0] * (1 - fx) + g[x1, y0, z0] * fx
        c10 = g[x0, y1, z0] * (1 - fx) + g[x1, y1, z0] * fx
        c01 = g[x0, y0, z1] * (1 - fx) + g[x1, y0, z1] * fx
        c11 = g[x0, y1, z1] * (1 - fx) + g[x1, y1, z1] * fx
        c0 = c00 * (1 - fy) + c10 * fy
        c1 = c01 * (1 - fy) + c11 * fy
        return c0 * (1 - fz) + c1 * fz

    def fbm(self, p: np.ndarray, octaves: int = 5, gain: float = 0.5, lac: float = 2.03) -> np.ndarray:
        acc, amp, tot = 0.0, 1.0, 0.0
        q = p.copy()
        for o in range(octaves):
            acc = acc + amp * self(q + o * 17.31)
            tot += amp
            amp *= gain
            q = q * lac
        return acc / tot


def smoothstep(a, b, x):
    t = np.clip((x - a) / (b - a), 0, 1)
    return t * t * (3 - 2 * t)


def mix(a, b, t):
    return a * (1 - t[..., None]) + b * t[..., None]


# ----------------------------------------------------------------------------
def directions(w: int, h: int) -> np.ndarray:
    """Dirección unitaria por texel. u = longitud (0..1), v = latitud (abajo -90°, arriba +90°)."""
    u = (np.arange(w) + 0.5) / w
    v = 1 - (np.arange(h) + 0.5) / h                 # fila 0 = arriba de la imagen = v 1
    lon = (u - 0.5) * 2 * np.pi
    lat = (v - 0.5) * np.pi
    lon, lat = np.meshgrid(lon, lat)
    # misma convención que la malla: x = cos(lat)cos(lon), y = cos(lat)sin(lon), z = sin(lat)
    return np.stack([np.cos(lat) * np.cos(lon), np.cos(lat) * np.sin(lon), np.sin(lat)], -1).astype(np.float32)


def rotate(p: np.ndarray, axis: np.ndarray, ang: np.ndarray) -> np.ndarray:
    """Rodrigues: gira p alrededor de axis un ángulo ang (por punto)."""
    c, s_ = np.cos(ang)[..., None], np.sin(ang)[..., None]
    k = axis / np.linalg.norm(axis)
    return p * c + np.cross(k, p) * s_ + k * (p @ k)[..., None] * (1 - c)


def twist(d: np.ndarray, seed: int) -> np.ndarray:
    """Vórtices: cada uno gira la esfera alrededor de un eje, más fuerte cerca de su centro."""
    rng = np.random.default_rng(seed + 100)
    p = d.copy()
    for _ in range(4):
        a = rng.normal(size=3)
        a /= np.linalg.norm(a)
        strength = rng.uniform(2.5, 5.0) * rng.choice([-1, 1])
        width = rng.uniform(0.35, 0.7)
        dist2 = np.sum((p - a) ** 2, -1)
        p = rotate(p, a, strength * np.exp(-dist2 / width))
    return p


def swirl(d: np.ndarray, seed: int):
    n1, n2, n3, n4 = (Noise3D(seed + k) for k in range(4))
    t = twist(d, seed)
    base = t * 1.25 + 8.0                            # escala del patrón (más grande = remolinos más amplios)

    w1 = np.stack([n1.fbm(base, 3), n2.fbm(base + 3.1, 3), n3.fbm(base + 7.7, 3)], -1) - 0.5
    q = base + 1.1 * w1
    m = n4.fbm(q, 4)

    bands = np.sin(m * 14.0 + 2.0 * n1.fbm(q * 0.8, 2))     # vetas que siguen el remolino
    big = smoothstep(0.35, 0.65, n3.fbm(t * 0.8 + 2.0, 3))  # zonas negras vs moradas
    return m, bands, big, q


def build(w: int, seed: int):
    h = w // 2
    d = directions(w, h)
    m, bands, big, r = swirl(d, seed)

    t_purple = smoothstep(-0.65, 0.3, bands) * (0.55 + 0.45 * big)
    col = mix(np.broadcast_to(BLACK, (h, w, 3)), SMOKE, smoothstep(-0.9, 0.0, bands) * 0.6)
    col = mix(col, DEEP, t_purple)
    col = mix(col, PURPLE, smoothstep(0.15, 0.8, bands) * (0.45 + 0.55 * big))
    veins = np.exp(-((bands - 0.97) ** 2) / 0.0025) * (0.4 + 0.6 * big)
    col = mix(col, LILAC, veins * 0.75)
    dark_veins = np.exp(-((bands + 0.55) ** 2) / 0.004)
    col = mix(col, BLACK * 0.6, dark_veins * 0.6)

    # perla: destellos finos en las zonas moradas
    flake_n = Noise3D(seed + 9)(d * 420.0)
    flakes = smoothstep(0.86, 0.97, flake_n) * smoothstep(0.1, 0.7, t_purple)
    col = col + flakes[..., None] * np.array([0.25, 0.15, 0.35])
    col = np.clip(col, 0, 1)

    metallic = np.clip(0.08 + 0.35 * t_purple * big + 0.5 * flakes, 0, 1)
    scuff = Noise3D(seed + 21).fbm(d * 30.0, 4)
    smooth = np.clip(0.9 - 0.12 * smoothstep(0.62, 0.85, scuff) - 0.05 * flakes, 0, 1)

    # micro-rayones (muy sutiles; la bola es lisa y pulida)
    rng = np.random.default_rng(seed)
    scratch = np.zeros((h, w), np.float32)
    for _ in range(140):
        y0, x0 = rng.uniform(h * 0.08, h * 0.92), rng.uniform(0, w)
        ang, ln = rng.uniform(0, np.pi), rng.uniform(w * 0.004, w * 0.03)
        ts = np.linspace(0, 1, max(2, int(ln)))
        ys = np.clip((y0 + np.sin(ang) * ln * ts).astype(int), 0, h - 1)
        xs = ((x0 + np.cos(ang) * ln * ts).astype(int)) % w
        scratch[ys, xs] = np.maximum(scratch[ys, xs], rng.uniform(0.3, 1.0))
    scratch = ndimage.gaussian_filter(scratch, 0.6)
    smooth = np.clip(smooth - 0.08 * scratch, 0, 1)
    height = -scratch * 0.25
    p = np.pad(height, 1, mode="wrap")
    dx = ndimage.sobel(p, axis=1)[1:-1, 1:-1] / 8.0
    dy = ndimage.sobel(p, axis=0)[1:-1, 1:-1] / 8.0
    n = np.stack([-dx * 3, dy * 3, np.ones_like(height)], -1)
    n /= np.linalg.norm(n, axis=-1, keepdims=True)
    return col, metallic, smooth, n * 0.5 + 0.5


def preview(col: np.ndarray, size: int = 700) -> np.ndarray:
    """Esfera vista de frente (eje -y), sombreado simple, para revisar el patrón sin abrir Blender."""
    h, w, _ = col.shape
    yy, xx = np.mgrid[-1:1:size * 1j, -1:1:size * 1j]
    r2 = xx ** 2 + yy ** 2
    inside = r2 <= 1
    zf = np.sqrt(np.clip(1 - r2, 0, 1))
    x, y, z = xx, -zf, -yy                            # punto en la esfera mirando hacia -y
    lon = np.arctan2(y, x)
    lat = np.arcsin(np.clip(z, -1, 1))
    u = ((lon / (2 * np.pi)) + 0.5) % 1.0
    v = lat / np.pi + 0.5
    px = np.clip((u * w).astype(int), 0, w - 1)
    py = np.clip(((1 - v) * h).astype(int), 0, h - 1)
    img = col[py, px]
    light = np.clip(0.25 + 0.85 * (zf * 0.7 - yy * 0.3 - xx * 0.2), 0, 1.2)
    spec = np.clip(zf * 0.7 - yy * 0.5 - xx * 0.5, 0, 1) ** 40 * 1.5
    out = np.clip(img * light[..., None] + spec[..., None], 0, 1)
    out[~inside] = 0.12
    return out


def save(a, path):
    Image.fromarray((np.clip(a, 0, 1) * 255 + 0.5).astype(np.uint8)).save(path, optimize=True)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", type=Path, default=Path("out"))
    ap.add_argument("--width", type=int, default=4096, choices=[1024, 2048, 4096])
    ap.add_argument("--seed", type=int, default=11)
    a = ap.parse_args()
    a.out.mkdir(parents=True, exist_ok=True)
    col, metal, smooth, normal = build(a.width, a.seed)
    save(col, a.out / "BowlingBall_basecolor.png")
    save(np.stack([metal, metal, metal, smooth], -1), a.out / "BowlingBall_metallic_smoothness.png")
    save(normal, a.out / "BowlingBall_normal.png")
    save(preview(col), a.out / "BowlingBall_preview.png")
    print(f"Listo -> {a.out.resolve()}")


if __name__ == "__main__":
    main()
