#!/usr/bin/env python3
"""
generate_lever_texture.py
Tabla de la palanca: madera natural (misma madera 3D de los bloques) con dos bandas de
cinta de peligro amarilla/negra que envuelven la tabla con las rayas diagonales continuas.

Usa generate_block_textures.py (debe estar en la misma carpeta) para la madera y el layout.
Espacio canónico de la tabla: a = largo (Y de Unity), y = grosor (X de Unity), b = ancho (Z de Unity).

Salidas: Lever_basecolor.png  Lever_normal.png  Lever_metallic_smoothness.png  lever_layout.json
Uso:     python generate_lever_texture.py --out ./out --length 1.59 --thickness 0.15 --width 0.6
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import generate_block_textures as gb  # noqa: E402

F = np.float32
YELLOW = np.array([0.98, 0.76, 0.04], F)
BLACK = np.array([0.04, 0.04, 0.045], F)


def tape(face, S, T, dims, bands, rng, nz):
    """Máscara de cinta, color de la cinta, altura y suavidad para una cara larga."""
    L, H, W = dims
    a, y, b = gb.face_points(face, S, T)
    A, Y, B = a * L + L / 2, y * H, b * W                    # A: 0 en la base, L en la punta
    # coordenada alrededor del perímetro (continua de cara en cara)
    if face == "top":
        P = B + W / 2
    elif face == "sideP":
        P = W + (H / 2 - Y)
    elif face == "bottom":
        P = W + H + (W / 2 - B)
    else:                                                     # sideN
        P = 2 * W + H + (Y + H / 2)
    period = 0.09
    stripe = ((A + P) / period) % 1.0
    yellow = gb.smoothstep(0.49, 0.51, stripe) * (1 - gb.smoothstep(0.985, 1.0, stripe))   # mitad amarilla, mitad negra
    P3 = np.stack([A, Y, B], -1).astype(F)
    mask = np.zeros_like(A)
    for a0, a1 in bands:
        wob = 0.004 * (nz[0].fbm(P3 * 30, 2) - 0.5)          # borde de cinta ligeramente irregular
        m = gb.smoothstep(a0 - 0.0015, a0 + 0.0015, A + wob) * (1 - gb.smoothstep(a1 - 0.0015, a1 + 0.0015, A + wob))
        mask = np.maximum(mask, m)
    color = BLACK * (1 - yellow[..., None]) + YELLOW * yellow[..., None]
    # desgaste: rayones y zonas peladas donde se ve la madera
    worn = gb.smoothstep(0.78, 0.9, nz[1].fbm(P3 * np.array([8, 8, 8], F), 4))
    scuff = gb.smoothstep(0.6, 0.85, nz[2].fbm(P3 * np.array([60, 200, 200], F), 2)) * 0.25
    color = color * (1 - scuff[..., None]) + np.array([0.6, 0.6, 0.6], F) * scuff[..., None]
    mask = mask * (1 - worn)
    smooth = 0.62 - 0.25 * scuff
    height = 0.35 * mask                                     # la cinta queda levemente por encima
    return mask, color, smooth, height


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", type=Path, default=Path("out"))
    ap.add_argument("--size", type=int, default=2048)
    ap.add_argument("--length", type=float, default=1.59)
    ap.add_argument("--thickness", type=float, default=0.15)
    ap.add_argument("--width", type=float, default=0.6)
    ap.add_argument("--seed", type=int, default=31)
    a = ap.parse_args()
    a.out.mkdir(parents=True, exist_ok=True)
    dims = (a.length, a.thickness, a.width)
    L = a.length
    bands = [(L * 0.80, L * 0.80 + 0.16), (L * 0.20, L * 0.20 + 0.12)]   # punta y sobre el pivote

    R = gb.build_layout(a.size, *dims)
    pad = max(6, a.size // 160)
    rng = np.random.default_rng(a.seed)
    tree = gb.make_tree(rng)
    tree["early"] = tree["early"] * np.array([1.0, 0.97, 0.92])          # un poco más cálida que los bloques
    nz = [gb.Noise3D(a.seed * 10 + j) for j in range(4)]
    col = np.zeros((a.size, a.size, 3), F) + tree["early"].astype(F) * 0.8
    sm = np.full((a.size, a.size), 0.35, F)
    nrm = np.zeros((a.size, a.size, 3), F) + (0.5, 0.5, 1.0)
    for face in gb.FACES:
        x, y, w, h = R[face]
        c, s, hgt = gb.render_face(face, w, h, tree, nz, dims, rng)
        if face in ("top", "bottom", "sideP", "sideN"):
            sv = (np.arange(w, dtype=F) + 0.5) / w
            tv = 1 - (np.arange(h, dtype=F) + 0.5) / h
            S, T = np.meshgrid(sv, tv)
            m, tc, ts, th = tape(face, S, T, dims, bands, rng, nz)
            c = c * (1 - m[..., None]) + tc * m[..., None]
            s = s * (1 - m) + ts * m
            hgt = hgt + th
        gb.place(col, c, R[face], pad)
        gb.place(sm, s, R[face], pad)
        gb.place(nrm, gb.to_normal(hgt, 1.2), R[face], pad)
    gb.save(col, a.out / "Lever_basecolor.png")
    gb.save(nrm, a.out / "Lever_normal.png")
    z = np.zeros_like(sm)
    gb.save(np.stack([z, z, z, sm], -1), a.out / "Lever_metallic_smoothness.png")
    layout = {"atlasSize": a.size, "dimensions": list(dims)}
    for f in gb.FACES:
        x, y, w, h = R[f]
        layout[f] = [x / a.size, 1 - (y + h) / a.size, (x + w) / a.size, 1 - y / a.size]
    (a.out / "lever_layout.json").write_text(json.dumps(layout, indent=2))
    print(f"Listo -> {a.out.resolve()}")


if __name__ == "__main__":
    main()
