#!/usr/bin/env python3
"""
generate_block_textures.py
Bloques de madera natural tipo Jenga: N variantes, cada una con su propio "tronco".

La madera se calcula en 3D (anillos de crecimiento alrededor de un eje de árbol casi paralelo al
largo del bloque) y se muestrea en las 6 caras, así que la veta sigue de una cara a otra por las
aristas y en las puntas se ven los anillos del corte transversal.

Espacio canónico del bloque (cubo unidad):  a = largo, y = alto, b = ancho  (todos en [-0.5, 0.5])
UV por cara (las MISMAS fórmulas están en BlockSetup.cs):
  top    (+y): s = a+.5,  t = b+.5          bottom (-y): s = a+.5, t = .5-b
  sideP  (+b): s = .5-a,  t = y+.5          sideN  (-b): s = a+.5, t = y+.5
  endP   (+a): s = b+.5,  t = y+.5          endN   (-a): s = .5-b, t = y+.5

Salidas por variante (Block_XX/): basecolor, normal, metallic_smoothness (A = smoothness)
Compartido: block_layout.json (rectángulos UV), blocks_preview.png

Uso:  python generate_block_textures.py --out ./out --count 8 --length 1.5 --height 0.3 --width 0.5
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path

import numpy as np
from PIL import Image
from scipy import ndimage

F = np.float32
FACES = ["top", "bottom", "sideP", "sideN", "endP", "endN"]


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
            acc = acc + amp * self(q + o * 19.3)
            tot += amp
            amp *= 0.5
            q = q * 2.03
        return acc / tot


def smoothstep(a, b, x):
    t = np.clip((x - a) / (b - a), 0, 1)
    return t * t * (3 - 2 * t)


# ----------------------------------------------------------------------------
# Layout
# ----------------------------------------------------------------------------
def build_layout(size, L, H, W):
    m, g = round(size * 0.02), round(size * 0.012)
    d = (size - 2 * m - g) / L
    while True:
        wl, wh, ww = round(d * L), round(d * H), round(d * W)
        rows = 2 * ww + 2 * wh + 3 * g
        if rows <= size - 2 * m and wl + g + ww <= size - 2 * m:
            break
        d *= 0.97
    y = m
    R = {}
    R["top"] = (m, y, wl, ww); y += ww + g
    R["bottom"] = (m, y, wl, ww); y += ww + g
    R["sideP"] = (m, y, wl, wh); y += wh + g
    R["sideN"] = (m, y, wl, wh)
    ex = m + wl + g
    R["endP"] = (ex, m, ww, wh)
    R["endN"] = (ex, m + wh + g, ww, wh)
    return R


def face_points(face, s, t):
    """(s, t) en [0,1] -> punto (a, y, b) del cubo unidad. Inversa de las fórmulas del docstring."""
    o = np.full_like(s, 0.5)
    if face == "top":    return s - .5, o, t - .5
    if face == "bottom": return s - .5, -o, .5 - t
    if face == "sideP":  return .5 - s, t - .5, o
    if face == "sideN":  return s - .5, t - .5, -o
    if face == "endP":   return o, t - .5, s - .5
    if face == "endN":   return -o, t - .5, .5 - s
    raise ValueError(face)


# ----------------------------------------------------------------------------
# Madera
# ----------------------------------------------------------------------------
def make_tree(rng):
    phi = rng.uniform(0, 2 * np.pi)
    dist = rng.uniform(0.7, 2.6)                               # el corazón del tronco queda fuera del bloque
    return dict(
        y0=dist * np.sin(phi), b0=dist * np.cos(phi),
        ty=rng.uniform(-0.08, 0.08), tb=rng.uniform(-0.08, 0.08),   # el eje no es perfectamente paralelo
        spacing=rng.uniform(0.032, 0.06),
        early=np.array([0.87, 0.72, 0.51]) * rng.uniform(0.93, 1.05),
        late=np.array([0.70, 0.50, 0.31]) * rng.uniform(0.9, 1.05),
        hue=rng.uniform(-0.03, 0.04),
    )


def wood(tree, nz, A, Y, B):
    """Color, smoothness y altura de la madera en puntos 3D (metros, espacio del bloque)."""
    P = np.stack([A, Y, B], -1).astype(F)
    wob = nz[0].fbm(P * np.array([0.8, 7, 7], F), 4) - 0.5
    r = np.hypot(Y - tree["y0"] - tree["ty"] * A, B - tree["b0"] - tree["tb"] * A) + 0.02 * wob
    ring = (r / tree["spacing"] + 1.3 * (nz[1].fbm(P * np.array([0.5, 3, 3], F), 3) - 0.5)) % 1.0
    late = smoothstep(0.70, 0.93, ring) * (1 - smoothstep(0.975, 1.0, ring))
    fiber = nz[2].fbm(P * np.array([1.5, 90, 90], F), 3)        # poros/fibras finas a lo largo
    color = tree["early"] * (1 - late[..., None] * 0.55) + tree["late"] * (late[..., None] * 0.55)
    color = color * (0.9 + 0.16 * fiber)[..., None]
    color[..., 0] *= 1 + tree["hue"]
    color[..., 2] *= 1 - tree["hue"]
    mineral = smoothstep(0.78, 0.92, nz[3].fbm(P * np.array([0.6, 4, 4], F), 4))
    color = color * (1 - 0.18 * mineral)[..., None]
    smooth = 0.42 - 0.08 * late + 0.04 * (fiber - 0.5)
    height = -0.25 * late - 0.15 * (fiber < 0.32)
    return color, smooth, height


def render_face(face, w, h, tree, nz, dims, rng):
    L, H, W = dims
    s = (np.arange(w, dtype=F) + 0.5) / w
    t = 1 - (np.arange(h, dtype=F) + 0.5) / h
    S, T = np.meshgrid(s, t)
    a, y, b = face_points(face, S, T)
    color, smooth, height = wood(tree, nz, a * L, y * H, b * W)
    end = face.startswith("end")
    if end:                                                       # veta de testa: más oscura, porosa, mate
        color = color * 0.82
        smooth = smooth - 0.18
        height = height + 0.2 * (nz[2].fbm(np.stack([a * L, y * H, b * W], -1).astype(F) * 140, 2) - 0.5)
    # bordes redondeados (~1 cm a esta escala): baja la altura y oscurece un poco
    du = np.minimum(S, 1 - S) * w
    dv = np.minimum(T, 1 - T) * h
    edge_px = max(4.0, 0.012 * w / (W if end else L))             # ~1.2 cm en píxeles de esta cara
    e = np.minimum(du, dv) / edge_px
    bevel = smoothstep(0, 1, e)
    height = height * bevel + (bevel - 1) * 4.0
    color = color * (0.86 + 0.14 * bevel)[..., None]
    smooth = smooth - 0.08 * (1 - bevel)
    # golpecitos y marcas de uso
    dents = smoothstep(0.83, 0.9, nz[3].fbm(np.stack([a * L, y * H, b * W], -1).astype(F) * 18, 3))
    color = color * (1 - 0.12 * dents)[..., None]
    height = height - 0.6 * dents
    return np.clip(color, 0, 1), np.clip(smooth, 0.05, 1), height


def to_normal(height, strength):
    p = np.pad(height, 1, mode="edge")
    gx = ndimage.sobel(p, axis=1)[1:-1, 1:-1] / 8.0
    gy = ndimage.sobel(p, axis=0)[1:-1, 1:-1] / 8.0
    n = np.stack([-gx * strength, gy * strength, np.ones_like(height)], -1)
    return n / np.linalg.norm(n, axis=-1, keepdims=True) * 0.5 + 0.5


def place(canvas, data, rect, pad):
    x, y, w, h = rect
    ext = ((pad, pad), (pad, pad)) + (((0, 0),) if data.ndim == 3 else ())
    big = np.pad(data, ext, mode="edge")
    canvas[y - pad:y + h + pad, x - pad:x + w + pad] = big


def save(arr, path):
    Image.fromarray((np.clip(arr, 0, 1) * 255 + 0.5).astype(np.uint8)).save(path, optimize=True)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", type=Path, default=Path("out"))
    ap.add_argument("--count", type=int, default=8)
    ap.add_argument("--size", type=int, default=2048, choices=[1024, 2048, 4096])
    ap.add_argument("--length", type=float, default=1.5)
    ap.add_argument("--height", type=float, default=0.3)
    ap.add_argument("--width", type=float, default=0.5)
    ap.add_argument("--seed", type=int, default=21)
    a = ap.parse_args()
    a.out.mkdir(parents=True, exist_ok=True)
    dims = (a.length, a.height, a.width)
    R = build_layout(a.size, *dims)
    pad = max(6, a.size // 160)
    previews = []
    for k in range(a.count):
        rng = np.random.default_rng([a.seed, k])
        tree = make_tree(rng)
        nz = [Noise3D(a.seed * 100 + k * 10 + j) for j in range(4)]
        col = np.zeros((a.size, a.size, 3), F) + tree["early"].astype(F) * 0.8
        sm = np.full((a.size, a.size), 0.35, F)
        nrm = np.zeros((a.size, a.size, 3), F) + (0.5, 0.5, 1.0)
        for face in FACES:
            x, y, w, h = R[face]
            c, s, hgt = render_face(face, w, h, tree, nz, dims, rng)
            place(col, c, R[face], pad)
            place(sm, s, R[face], pad)
            place(nrm, to_normal(hgt, 1.2), R[face], pad)
        name = f"Block_{k + 1:02d}"
        d = a.out / name
        d.mkdir(exist_ok=True)
        save(col, d / f"{name}_basecolor.png")
        save(nrm, d / f"{name}_normal.png")
        z = np.zeros_like(sm)
        save(np.stack([z, z, z, sm], -1), d / f"{name}_metallic_smoothness.png")
        x, y, w, h = R["top"]
        ex, ey, ew, eh = R["endP"]
        tile = np.ones((h + eh + 8, w, 3), F) * 0.15
        tile[:h] = col[y:y + h, x:x + w]
        tile[h + 8:h + 8 + eh, :ew] = col[ey:ey + eh, ex:ex + ew]
        previews.append(tile)
        print(f"  {name}: tronco a {np.hypot(tree['y0'], tree['b0']):.2f} m, anillos cada {tree['spacing'] * 100:.1f} cm")

    layout = {"atlasSize": a.size, "dimensions": list(dims)}
    for f in FACES:
        x, y, w, h = R[f]
        layout[f] = [x / a.size, 1 - (y + h) / a.size, (x + w) / a.size, 1 - y / a.size]
    (a.out / "block_layout.json").write_text(json.dumps(layout, indent=2))
    sheet = np.concatenate([np.concatenate([p, np.ones((p.shape[0], 10, 3), F) * 0.15], 1) for p in previews], 0)
    img = Image.fromarray((np.clip(sheet, 0, 1) * 255).astype(np.uint8))
    img.thumbnail((1400, 4000))
    img.save(a.out / "blocks_preview.png")
    print(f"Listo -> {a.out.resolve()}")


if __name__ == "__main__":
    main()
