#!/usr/bin/env python3
"""
generate_pin_texture.py
Textura de pino de bolos: blanco lacado con dos franjas rojas en el cuello, marcas de golpes
de la bola en la panza, pequeños desconchones y la base algo sucia.

Mapeo (lo usa PinSetup.cs):  u = ángulo alrededor del eje (0..1)
                             v = altura / 15 in  (0 = base, 1 = punta)
El ruido se evalúa en 3D sobre el cilindro (cos, sin, altura), así que no hay costura en u = 0/1.

Salidas: Pin_basecolor.png  Pin_normal.png  Pin_metallic_smoothness.png (A = smoothness)
Uso:     python generate_pin_texture.py --out ./out --seed 4
"""
from __future__ import annotations

import argparse
from pathlib import Path

import numpy as np
from PIL import Image
from scipy import ndimage

PIN_H = 15.0                                    # pulgadas (reglamento)
STRIPES = [(9.20, 9.70), (10.15, 10.65)]        # dos franjas en el cuello (pulgadas desde la base)
WHITE = np.array([0.93, 0.925, 0.90], np.float32)
RED = np.array([0.72, 0.035, 0.045], np.float32)
SCUFF = np.array([0.42, 0.40, 0.40], np.float32)
DIRT = np.array([0.55, 0.50, 0.44], np.float32)


class Noise3D:
    def __init__(self, seed, size=64):
        self.n = size
        self.g = np.random.default_rng(seed).random((size, size, size)).astype(np.float32)

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
        acc, amp, tot, q = 0.0, 1.0, 0.0, p.copy()
        for o in range(octaves):
            acc = acc + amp * self(q + o * 13.7)
            tot += amp
            amp *= 0.5
            q = q * 2.03
        return acc / tot


def smoothstep(a, b, x):
    t = np.clip((x - a) / (b - a), 0, 1)
    return t * t * (3 - 2 * t)


def mix(c, t_col, t):
    return c * (1 - t[..., None]) + t_col * t[..., None]


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", type=Path, default=Path("out"))
    ap.add_argument("--width", type=int, default=1024)
    ap.add_argument("--seed", type=int, default=4)
    a = ap.parse_args()
    a.out.mkdir(parents=True, exist_ok=True)
    W, H = a.width, a.width * 2
    rng = np.random.default_rng(a.seed)

    u = (np.arange(W, dtype=np.float32) + 0.5) / W
    h = (1 - (np.arange(H, dtype=np.float32) + 0.5) / H) * PIN_H   # fila 0 = punta (v = 1)
    U, Hh = np.meshgrid(u, h)
    th = U * 2 * np.pi
    # punto en un cilindro de radio ~2.4 in (escala en pulgadas): ruido sin costura alrededor
    P = np.stack([np.cos(th) * 2.4, np.sin(th) * 2.4, Hh], -1).astype(np.float32)
    n1, n2, n3, n4 = (Noise3D(a.seed + k) for k in range(4))

    color = np.broadcast_to(WHITE, (H, W, 3)).astype(np.float32).copy()
    color *= (0.985 + 0.03 * n1.fbm(P * 1.5, 3))[..., None]          # leve variación del lacado
    smooth = np.full((H, W), 0.86, np.float32)
    height = np.zeros((H, W), np.float32)

    # franjas rojas con borde nítido (antialias de ~1 px)
    px = PIN_H / H
    for lo, hi in STRIPES:
        s = np.clip((Hh - lo) / px + 0.5, 0, 1) * np.clip((hi - Hh) / px + 0.5, 0, 1)
        color = mix(color, RED * (0.95 + 0.08 * n2.fbm(P * 4, 2))[..., None], s)
        smooth = smooth * (1 - s) + 0.82 * s
        height += 0.15 * s                                          # pintura apenas en relieve

    # marcas de bola: manchas grises alargadas en horizontal, sobre todo en la panza (2-7 in)
    belly = np.exp(-((Hh - 4.6) / 2.2) ** 2)
    for _ in range(18):
        cu, ch = rng.uniform(0, 1), rng.normal(4.6, 1.6)
        w_u, w_h = rng.uniform(0.04, 0.10), rng.uniform(0.15, 0.35)
        du = np.minimum(np.abs(U - cu), 1 - np.abs(U - cu))           # distancia circular en u
        m = np.exp(-(du / w_u) ** 2 - ((Hh - ch) / w_h) ** 2)
        streaks = n3.fbm(P * np.array([2.5, 2.5, 40.0], np.float32) + rng.uniform(0, 50), 3)
        m = m * smoothstep(0.5, 0.8, streaks)                        # rayas horizontales finas
        strength = rng.uniform(0.2, 0.5)
        color = mix(color, SCUFF, m * strength)
        smooth -= 0.35 * m * strength
    # desconchones pequeños (se ve el material debajo, más mate y hundido)
    chips = smoothstep(0.80, 0.86, n4.fbm(P * 9, 3)) * (0.3 + 0.7 * belly)
    color = mix(color, np.array([0.80, 0.77, 0.70], np.float32), chips * 0.8)
    smooth -= 0.4 * chips
    height -= 0.6 * chips
    # base sucia por arrastre en la pista
    base = 1 - smoothstep(0.0, 0.55, Hh)
    color = mix(color, DIRT, base * (0.5 + 0.5 * n1.fbm(P * 8, 3)) * 0.7)
    smooth -= 0.3 * base
    smooth = np.clip(smooth + 0.03 * (n2.fbm(P * 20, 2) - 0.5), 0, 1)
    color = np.clip(color, 0, 1)

    height = ndimage.gaussian_filter(height, 0.8, mode=("nearest", "wrap"))
    p = np.pad(height, 1, mode="wrap")
    gx = ndimage.sobel(p, axis=1)[1:-1, 1:-1] / 8.0
    gy = ndimage.sobel(p, axis=0)[1:-1, 1:-1] / 8.0
    n = np.stack([-gx * 3, gy * 3, np.ones_like(height)], -1)
    n /= np.linalg.norm(n, axis=-1, keepdims=True)

    def save(arr, name):
        Image.fromarray((np.clip(arr, 0, 1) * 255 + 0.5).astype(np.uint8)).save(a.out / name, optimize=True)

    save(color, "Pin_basecolor.png")
    save(n * 0.5 + 0.5, "Pin_normal.png")
    z = np.zeros_like(smooth)
    save(np.stack([z, z, z, smooth], -1), "Pin_metallic_smoothness.png")
    print(f"Listo -> {a.out.resolve()}")


if __name__ == "__main__":
    main()
