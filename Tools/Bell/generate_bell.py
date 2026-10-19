#!/usr/bin/env python3
"""
generate_bell.py
Campana de latón/bronce cepillado: perfil (fuente única para la malla) + texturas PBR.

bell_profile.json   perfil torneado en METROS respecto al centro del objeto CuerpoCampana:
                    lista de [radio, altura, v] que va: centro de la corona exterior -> flanco
                    exterior -> labio -> flanco interior -> centro del techo interior.
                    BellSetup.cs lo gira 360° y lo divide por la escala del objeto.
Bell_basecolor.png / Bell_normal.png / Bell_metallic_smoothness.png
                    u = ángulo alrededor del eje, v = posición a lo largo del perfil (0 arriba fuera).

Medidas por defecto = CuerpoCampana actual (cilindro de radio 0.4 m y alto 0.7 m).
Uso: python generate_bell.py --out ./out --radius 0.4 --height 0.7
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path

import numpy as np
from PIL import Image
from scipy import ndimage

F = np.float32
# Puntos de control (radio, altura desde el labio) para radio 0.4 y alto 0.7; se escalan.
CTRL = [
    (0.000, 0.700), (0.070, 0.700), (0.135, 0.698), (0.195, 0.690), (0.232, 0.668),     # corona
    (0.250, 0.630), (0.256, 0.560), (0.260, 0.480), (0.266, 0.400), (0.280, 0.310),     # hombro y cintura
    (0.305, 0.220), (0.338, 0.140), (0.368, 0.080), (0.388, 0.040), (0.398, 0.014),     # boca acampanada
    (0.396, 0.002), (0.384, -0.000), (0.368, 0.004), (0.358, 0.016),                     # labio redondeado
    (0.346, 0.050), (0.326, 0.100), (0.296, 0.170), (0.268, 0.260), (0.250, 0.350),     # flanco interior
    (0.240, 0.450), (0.236, 0.540), (0.222, 0.610), (0.180, 0.648), (0.100, 0.660), (0.000, 0.662),
]
GROOVES_H = [0.600, 0.578, 0.112, 0.094]          # anillos grabados en el exterior (altura desde el labio)
BRASS = np.array([0.80, 0.62, 0.34], F)
BRONZE = np.array([0.66, 0.45, 0.25], F)
TARNISH = np.array([0.36, 0.26, 0.14], F)
VERDIGRIS = np.array([0.30, 0.48, 0.40], F)


def catmull(points, per_seg=12):
    p = np.asarray(points, float)
    out = []
    for i in range(len(p) - 1):
        p0, p1, p2, p3 = p[max(i - 1, 0)], p[i], p[i + 1], p[min(i + 2, len(p) - 1)]
        for t in np.linspace(0, 1, per_seg, endpoint=False):
            t2, t3 = t * t, t * t * t
            out.append(0.5 * (2 * p1 + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t2 + (-p0 + 3 * p1 - 3 * p2 + p3) * t3))
    out.append(p[-1])
    return np.array(out)


def resample(poly, n):
    seg = np.linalg.norm(np.diff(poly, axis=0), axis=1)
    s = np.r_[0, np.cumsum(seg)]
    t = np.linspace(0, s[-1], n)
    return np.stack([np.interp(t, s, poly[:, 0]), np.interp(t, s, poly[:, 1])], 1), t / s[-1]


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
        acc, amp, tot, q = 0.0, 1.0, 0.0, p.copy()
        for o in range(octaves):
            acc = acc + amp * self(q + o * 23.1)
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
    ap.add_argument("--radius", type=float, default=0.4)
    ap.add_argument("--height", type=float, default=0.7)
    ap.add_argument("--size", type=int, default=2048)
    ap.add_argument("--seed", type=int, default=8)
    a = ap.parse_args()
    a.out.mkdir(parents=True, exist_ok=True)

    # ---------- perfil ----------
    sr, sh = a.radius / 0.4, a.height / 0.7
    ctrl = [(r * sr, h * sh) for r, h in CTRL]
    pts, v = resample(catmull(ctrl), 180)
    pts[:, 0] = np.clip(pts[:, 0], 0, a.radius)                      # nunca fuera del collider
    lip_i = int(np.argmin(pts[:, 1]))                                 # punto más bajo = labio
    v_lip = float(v[lip_i])
    profile = [[float(r), float(h - a.height / 2), float(vv)] for (r, h), vv in zip(pts, v)]
    (a.out / "bell_profile.json").write_text(json.dumps(                 # formato legible por JsonUtility
        {"radius": a.radius, "height": a.height, "lipV": v_lip,
         "r": [p[0] for p in profile], "h": [p[1] for p in profile], "v": [p[2] for p in profile]}, indent=1))

    def v_of_outer_h(h):                                              # v en el flanco exterior para una altura
        outer = pts[:lip_i + 1]
        k = np.argmin(np.abs(outer[:, 1] - h * sh))
        return float(v[k])

    # ---------- textura ----------
    S = a.size
    u = (np.arange(S, dtype=F) + 0.5) / S
    vv = 1 - (np.arange(S, dtype=F) + 0.5) / S                       # fila 0 = v 1 (centro del techo interior)
    U, V = np.meshgrid(u, vv)
    th = U * 2 * np.pi
    nz = [Noise3D(a.seed + k) for k in range(4)]
    Pc = np.stack([np.cos(th), np.sin(th), V * 6.0], -1).astype(F)     # sin costura en u

    inner = smoothstep(v_lip - 0.004, v_lip + 0.004, V)
    alloy = nz[0].fbm(Pc * np.array([1.2, 1.2, 1.0], F), 4)
    color = mix(np.broadcast_to(BRASS, (S, S, 3)).astype(F), BRONZE, smoothstep(0.35, 0.75, alloy) * 0.6)

    # cepillado circular (torneado): rayas finas a lo largo de u, cambian rápido en v
    brush = nz[1].fbm(Pc * np.array([3.0, 3.0, 220.0], F), 3)
    brush2 = nz[2].fbm(Pc * np.array([0.8, 0.8, 700.0], F), 2)
    streak = 0.6 * brush + 0.4 * brush2
    color = color * (0.9 + 0.2 * streak)[..., None]
    smooth = 0.66 + 0.16 * (streak - 0.5)

    # anillos grabados
    groove = np.zeros_like(V)
    px = 1.0 / S
    for gh in GROOVES_H:
        gv = v_of_outer_h(gh)
        groove = np.maximum(groove, np.clip(1 - np.abs(V - gv) / (2.2 * px), 0, 1))
    color = mix(color, TARNISH * 0.8, groove * 0.75)
    smooth -= 0.3 * groove

    # pátina: manchas suaves, más en el interior, en el hombro y dentro de los anillos
    blot = smoothstep(0.55, 0.85, nz[3].fbm(Pc * np.array([2.5, 2.5, 3.0], F), 4))
    tarnish = np.clip(0.16 * blot + 0.75 * inner + 0.4 * groove, 0, 1)
    color = mix(color, TARNISH, tarnish * 0.65)
    verd = smoothstep(0.78, 0.92, nz[2].fbm(Pc * np.array([5, 5, 8], F), 4)) * (0.3 * inner + 0.6 * groove + 0.15)
    color = mix(color, VERDIGRIS, verd * 0.7)
    smooth = smooth - 0.35 * tarnish - 0.4 * verd
    metal = np.clip(1 - 0.6 * verd - 0.25 * inner, 0, 1)
    # golpe del badajo: zona pulida y brillante en el labio interior
    strike = np.exp(-((V - (v_lip + 0.03)) / 0.012) ** 2) * (0.5 + 0.5 * nz[0](Pc * np.array([4, 4, 1], F)))
    color = mix(color, BRASS * 1.08, strike * 0.6)
    smooth = smooth + 0.25 * strike
    color = np.clip(color, 0, 1)
    smooth = np.clip(smooth, 0.08, 0.95)

    height = 0.5 * streak - 3.0 * groove - 0.8 * verd
    p = np.pad(height, 1, mode="wrap")
    gx = ndimage.sobel(p, axis=1)[1:-1, 1:-1] / 8.0
    gy = ndimage.sobel(p, axis=0)[1:-1, 1:-1] / 8.0
    n = np.stack([-gx * 1.5, gy * 1.5, np.ones_like(height)], -1)
    n /= np.linalg.norm(n, axis=-1, keepdims=True)

    def save(arr, name):
        Image.fromarray((np.clip(arr, 0, 1) * 255 + 0.5).astype(np.uint8)).save(a.out / name, optimize=True)

    save(color, "Bell_basecolor.png")
    save(n * 0.5 + 0.5, "Bell_normal.png")
    save(np.stack([metal, metal, metal, smooth], -1), "Bell_metallic_smoothness.png")
    print(f"Perfil: {len(profile)} puntos, labio en v={v_lip:.3f}, radio máx {pts[:, 0].max():.3f} m")
    print(f"Listo -> {a.out.resolve()}")


if __name__ == "__main__":
    main()
