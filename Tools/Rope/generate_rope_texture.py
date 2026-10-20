#!/usr/bin/env python3
"""
generate_rope_texture.py
Cuerda trenzada (16 cabos: 8 en cada sentido, 1 por encima / 1 por debajo) de cáñamo natural con
un cabo trazador marrón oscuro. Mosaico PERFECTAMENTE repetible en ambos ejes:
  u = una vuelta completa alrededor de la cuerda, v = un tramo de cuerda igual a su contorno.
Salidas: Rope_basecolor.png  Rope_normal.png  Rope_metallic_smoothness.png
Uso:     python generate_rope_texture.py --out ./out --size 1024 --strands 8
"""
from __future__ import annotations

import argparse
from pathlib import Path

import numpy as np
from PIL import Image

F = np.float32
HEMP = np.array([0.66, 0.53, 0.34], F)
TRACER = np.array([0.30, 0.17, 0.09], F)


def smoothstep(a, b, x):
    t = np.clip((x - a) / (b - a), 0, 1)
    return t * t * (3 - 2 * t)


def periodic_waves(x, rng, fmin, fmax, count):
    """Suma de senos con frecuencias enteras: periódica en x con periodo 1."""
    acc = np.zeros_like(x)
    for _ in range(count):
        f = rng.integers(fmin, fmax + 1)
        acc += np.sin(2 * np.pi * f * x + rng.uniform(0, 2 * np.pi)) / np.sqrt(f)
    return acc / np.abs(acc).max()


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", type=Path, default=Path("out"))
    ap.add_argument("--size", type=int, default=1024)
    ap.add_argument("--strands", type=int, default=8, help="cabos por sentido (entero)")
    ap.add_argument("--seed", type=int, default=5)
    a = ap.parse_args()
    a.out.mkdir(parents=True, exist_ok=True)
    rng = np.random.default_rng(a.seed)
    N, S = a.strands, a.size
    g = (np.arange(S, dtype=F) + 0.5) / S
    U, V = np.meshgrid(g, 1 - g)                       # fila 0 = arriba (v = 1)

    layers = []
    for sgn in (1, -1):
        A = N * (U + sgn * V)                           # coordenada a través de los cabos de este sentido
        idx = np.floor(A).astype(np.int64) % N
        fa = A - np.floor(A)
        along = (U - sgn * V)                           # a lo largo del cabo (periódica)
        B = N * (U - sgn * V)                           # cabos del otro sentido: define cruces
        ib = np.floor(B).astype(np.int64)
        fb = B - np.floor(B)
        over = ((np.floor(A).astype(np.int64) + ib) % 2 == (0 if sgn == 1 else 1)).astype(F)
        # el cabo sube por encima en sus cruces y baja por debajo; transición suave en el centro del cruce
        arch = 0.5 + (over - 0.5) * np.sin(np.pi * fb)                  # continuo: 0.5 entre cruces
        prof = np.sqrt(np.clip(1 - (2 * fa - 1) ** 2 / 0.92, 0, 1))      # sección redondeada del cabo
        # fibras: finas líneas a lo largo del cabo + leve torsión dentro del cabo
        fib = periodic_waves(N * (U + sgn * V) + 0.35 * along * N, rng, 40, 140, 24)
        lump = periodic_waves(along, rng, 3, 25, 10)
        h = prof * (0.35 + 0.65 * arch) + 0.05 * fib * prof + 0.04 * lump
        tone = 0.92 + 0.08 * ((idx * 37 + (3 if sgn == 1 else 11)) % 7) / 6.0
        col = np.where((idx % 4 == 0)[..., None], TRACER, HEMP) * tone[..., None]
        col = col * (0.85 + 0.15 * fib[..., None]) * (0.92 + 0.08 * lump[..., None])
        # pelusa de fibra suelta (más clara)
        fuzz = smoothstep(0.75, 1.0, fib) * 0.12
        col = col + fuzz[..., None] * np.array([0.9, 0.85, 0.7], F)
        layers.append((h, col))

    (h1, c1), (h2, c2) = layers
    top = (h1 >= h2)[..., None]
    height = np.maximum(h1, h2)
    col = np.where(top, c1, c2)
    ao = 0.35 + 0.65 * smoothstep(0.05, 0.75, height)                  # huecos entre cabos más oscuros
    col = np.clip(col * ao[..., None], 0, 1)

    # normal periódica (np.roll mantiene el mosaico sin costuras)
    gx = (np.roll(height, -1, 1) - np.roll(height, 1, 1)) * 0.5
    gy = (np.roll(height, 1, 0) - np.roll(height, -1, 0)) * 0.5
    st = S / 64.0
    n = np.stack([-gx * st, -gy * st, np.ones_like(height)], -1)
    n = n / np.linalg.norm(n, axis=-1, keepdims=True) * 0.5 + 0.5
    smooth = 0.18 + 0.12 * smoothstep(0.3, 0.9, height)

    def save(arr, p):
        Image.fromarray((np.clip(arr, 0, 1) * 255 + 0.5).astype(np.uint8)).save(p, optimize=True)
    save(col, a.out / "Rope_basecolor.png")
    save(n, a.out / "Rope_normal.png")
    z = np.zeros_like(smooth)
    save(np.stack([z, z, z, smooth], -1), a.out / "Rope_metallic_smoothness.png")
    print(f"Listo -> {a.out.resolve()}")


if __name__ == "__main__":
    main()
