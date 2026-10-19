#!/usr/bin/env python3
"""
generate_lane_texture.py
Textura PBR de pista de bolos: 39 tablones (maple en cabeza y pin deck, pino en el centro),
línea de falta, puntos guía, 7 flechas en "V", range finders y marcas de los pinos.

Las marcas reales (pista de 60 ft hasta el pino 1) se escalan a la distancia de TU pista:
  --head-pin  distancia en metros desde el extremo de entrada de la bola hasta el pino 1.

Orientación en la imagen (la usa LaneSetup.cs):
  columna 0  = extremo por donde entra la bola (línea de falta) · columna final = fondo (pin deck)
  fila 0     = lado -Z local de la pista · última fila = lado +Z

Salidas:
  Lane_basecolor.png  Lane_normal.png  Lane_metallic_smoothness.png (R = 0, A = smoothness)  Lane_preview.png

Uso (valores de la escena Maquina):
  python generate_lane_texture.py --length 8 --width 1.6 --head-pin 5.6 \\
      --pin-rows 3 --row-spacing 0.4 --pin-spacing 0.7 --pin-diameter 0.25
Requisitos: numpy, scipy, Pillow
"""
from __future__ import annotations

import argparse
from pathlib import Path

import numpy as np
from PIL import Image
from scipy import ndimage

F32 = np.float32
MAPLE = np.array([0.86, 0.72, 0.52], F32)
PINE = np.array([0.84, 0.66, 0.44], F32)
DECK = np.array([0.87, 0.76, 0.58], F32)
INLAY = np.array([0.10, 0.055, 0.03], F32)       # flechas / puntos (madera oscura incrustada)
SPOT = np.array([0.05, 0.04, 0.035], F32)        # marcas de pinos
FOUL = np.array([0.06, 0.05, 0.05], F32)
FT = 1.0 / 60.0                                   # fracción de la distancia línea-de-falta -> pino 1


def fbm(rng, h, w, cell, octaves=4, gain=0.5, aspect=(1.0, 1.0)):
    acc = np.zeros((h, w), F32)
    amp, tot = 1.0, 0.0
    for _ in range(octaves):
        ch, cw = max(cell * aspect[0], 1.5), max(cell * aspect[1], 1.5)
        gh, gw = int(h / ch) + 3, int(w / cw) + 3
        n = ndimage.zoom(rng.random((gh, gw)).astype(F32), (h / (gh - 1), w / (gw - 1)), order=3)[:h, :w]
        acc += amp * n
        tot += amp
        amp *= gain
        cell /= 2
    acc /= tot
    return (acc - acc.min()) / (np.ptp(acc) + 1e-9)


def smoothstep(a, b, x):
    t = np.clip((x - a) / (b - a), 0, 1)
    return t * t * (3 - 2 * t)


def mix(c, target, t):
    return c * (1 - t[..., None]) + target * t[..., None]


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", type=Path, default=Path("out"))
    ap.add_argument("--length", type=float, default=8.0, help="largo de la pista (X), metros")
    ap.add_argument("--width", type=float, default=1.6, help="ancho de la pista (Z), metros")
    ap.add_argument("--head-pin", type=float, default=5.6, help="metros desde la entrada hasta el pino 1")
    ap.add_argument("--pin-rows", type=int, default=3)
    ap.add_argument("--row-spacing", type=float, default=0.4)
    ap.add_argument("--pin-spacing", type=float, default=0.7)
    ap.add_argument("--pin-diameter", type=float, default=0.25)
    ap.add_argument("--boards", type=int, default=39)
    ap.add_argument("--px-per-m", type=int, default=1024)
    ap.add_argument("--seed", type=int, default=5)
    a = ap.parse_args()
    a.out.mkdir(parents=True, exist_ok=True)
    rng = np.random.default_rng(a.seed)

    W = int(round(a.length * a.px_per_m / 8)) * 8
    H = int(round(a.width * a.px_per_m / 8)) * 8
    sx, sz = a.length / W, a.width / H                      # metros por píxel
    print(f"Imagen {W}x{H}  ({a.px_per_m} px/m, tablón de {a.width / a.boards * 100:.1f} cm)")

    d = (np.arange(W, dtype=F32) + 0.5) * sx                 # distancia desde la entrada (m)
    z = (np.arange(H, dtype=F32) + 0.5) * sz - a.width / 2   # Z local (m), fila 0 = -Z
    D, Z = np.meshgrid(d, z)
    D2 = a.head_pin                                          # escala de marcas: 60 ft -> head_pin m
    ft = D2 * FT

    # ---------------- tablones ----------------
    bw = a.width / a.boards
    board_f = (Z + a.width / 2) / bw
    board = np.clip(board_f.astype(int), 0, a.boards - 1)
    in_board = board_f - board                               # 0-1 dentro del tablón
    tint = rng.uniform(0.90, 1.08, a.boards).astype(F32)[board]
    hue = rng.uniform(-0.04, 0.04, a.boards).astype(F32)[board]

    # secciones: maple (0-16 ft), pino (16 ft - 2.5 ft antes del pino 1), pin deck (resto)
    deck_start = D2 - 2.5 * ft
    t_pine = smoothstep(16 * ft - 0.004, 16 * ft + 0.004, D) * (1 - smoothstep(deck_start - 0.004, deck_start + 0.004, D))
    t_deck = smoothstep(deck_start - 0.004, deck_start + 0.004, D)
    base = mix(mix(np.broadcast_to(MAPLE, (H, W, 3)).astype(F32), PINE, t_pine), DECK, t_deck)

    # veta: rayas largas a lo largo del tablón + anillos alargados con fase por tablón
    streak = fbm(rng, H, W, 600, 5, aspect=(0.012, 1.0))
    lam = rng.uniform(0.35, 0.9, a.boards).astype(F32)[board]
    phase = rng.uniform(0, 1, a.boards).astype(F32)[board]
    warp = fbm(rng, H, W, 500, 3, aspect=(0.03, 1.0))     # cambia rápido entre tablones: sin "ondas"
    rings = 0.5 + 0.5 * np.sin(2 * np.pi * (D / lam + phase + 2.5 * warp + 3 * in_board * 0.2))
    rings = rings ** 4
    grain = 0.75 * streak + 0.25 * rings
    color = base * (tint * (0.86 + 0.2 * grain))[..., None]
    color[..., 0] *= 1 + hue
    color[..., 2] *= 1 - hue

    # juntas: costuras entre tablones + empalmes escalonados a lo largo de cada tablón
    seam_px = np.minimum(in_board, 1 - in_board) * bw / sz          # distancia al borde del tablón (px)
    seam = np.clip(1 - seam_px / 1.3, 0, 1)
    joint = np.zeros((H, W), F32)
    for b in range(a.boards):
        rows = board == b
        pos = rng.uniform(0.2, 1.6)
        while pos < a.length:
            c = int(pos / sx)
            if 0 < c < W:
                joint[:, max(0, c - 1):c + 1][rows[:, max(0, c - 1):c + 1]] = 1
            pos += rng.uniform(1.4, 3.0)
    joint = ndimage.gaussian_filter(joint, 0.6)
    color = color * (1 - 0.32 * np.maximum(seam, 0.8 * joint))[..., None]

    # ---------------- marcas ----------------
    ink = np.zeros((H, W), F32)                               # flechas, puntos, range finders
    def board_z(bnum):                                         # tablón 1 = lado +Z (derecha del jugador)
        return a.width / 2 - (bnum - 0.5) * bw

    # línea de falta
    foul = (np.abs(D - 0.03) < 0.006).astype(F32)
    # puntos guía a 7 ft
    for bnum in (3, 5, 8, 11, 14, 26, 29, 32, 35, 37):
        ink = np.maximum(ink, np.clip(1 - (np.hypot(D - 7 * ft, Z - board_z(bnum)) - bw * 0.3) / (1.2 * sz), 0, 1))
    # 7 flechas en V (el centro, tablón 20, es el más lejano)
    arrow_len = bw * 4.2
    for bnum in (5, 10, 15, 20, 25, 30, 35):
        n = abs(bnum - 20) / 5
        tip = (16 - 1.15 * n) * ft                               # punta hacia los pinos
        zc = board_z(bnum)
        along = (tip - D) / arrow_len                            # 0 en la punta, 1 en la base
        half = 0.5 * bw * 0.9 * np.clip(along, 0, 1)
        inside = (along >= 0) & (along <= 1) & (np.abs(Z - zc) <= half)
        edge = np.clip((half - np.abs(Z - zc)) / (1.0 * sz), 0, 1) * np.clip(along / 0.01, 0, 1)
        ink = np.maximum(ink, np.where(inside, edge, 0))
    # range finders (tablones 10 y 30, 34-43 ft)
    for bnum in (10, 30):
        rf = (np.abs(Z - board_z(bnum)) < bw * 0.18) & (D > 34 * ft) & (D < 43 * ft)
        ink = np.maximum(ink, rf.astype(F32))
    # marcas de pinos: mismas posiciones que PinRack (pino 1 en head_pin, filas hacia el fondo)
    spot_r = a.pin_diameter * 0.24
    spots = np.zeros((H, W), F32)
    for row in range(a.pin_rows):
        dx = D2 + row * a.row_spacing
        for i in range(row + 1):
            zc = -row * a.pin_spacing * 0.5 + i * a.pin_spacing
            r = np.hypot(D - dx, Z - zc)
            spots = np.maximum(spots, np.clip(1 - (r - spot_r) / (1.5 * sz), 0, 1))

    color = mix(color, INLAY * (0.9 + 0.2 * grain[..., None]), ink * 0.95)
    color = mix(color, FOUL, foul)
    color = mix(color, SPOT, spots)

    # ---------------- acabado (laca + aceite) ----------------
    edge_dist = a.width / 2 - np.abs(Z)
    dirt = (1 - smoothstep(0.0, 0.06, edge_dist)) * (0.5 + 0.5 * fbm(rng, H, W, 80, 3))
    color = color * (1 - 0.18 * dirt)[..., None]
    track_z = board_z(12)                                     # pista de la bola (tablones ~8-16)
    track = np.exp(-((Z - track_z) / (bw * 3.5)) ** 2) * smoothstep(0.3, 1.2, D) * (1 - t_deck)
    color = color * (1 - 0.06 * track * fbm(rng, H, W, 120, 3))[..., None]
    color = np.clip(color, 0, 1)

    oil = (1 - smoothstep(38 * ft, 42 * ft, D)) * np.exp(-((Z / (a.width * 0.42)) ** 6))
    smooth = 0.74 + 0.16 * oil - 0.08 * track - 0.12 * dirt - 0.15 * np.maximum(seam, joint)
    smooth += 0.03 * (fbm(rng, H, W, 40, 3) - 0.5)
    scuffs = smoothstep(0.78, 0.95, fbm(rng, H, W, 25, 4, aspect=(0.3, 1.0))) * (0.4 + 0.6 * track)
    smooth -= 0.12 * scuffs
    smooth = np.clip(smooth, 0, 1)

    height = -0.6 * seam - 0.4 * joint + 0.02 * grain + 0.03 * (tint - 1)
    p = np.pad(height, 1, mode="edge")
    gx = ndimage.sobel(p, axis=1)[1:-1, 1:-1] / 8.0
    gy = ndimage.sobel(p, axis=0)[1:-1, 1:-1] / 8.0
    n = np.stack([-gx * 2.5, gy * 2.5, np.ones_like(height)], -1)
    n /= np.linalg.norm(n, axis=-1, keepdims=True)
    normal = n * 0.5 + 0.5

    def save(arr, name):
        Image.fromarray((np.clip(arr, 0, 1) * 255 + 0.5).astype(np.uint8)).save(a.out / name, optimize=True)

    save(color, "Lane_basecolor.png")
    save(normal, "Lane_normal.png")
    zero = np.zeros_like(smooth)
    save(np.stack([zero, zero, zero, smooth], -1), "Lane_metallic_smoothness.png")
    prev = Image.fromarray((color * 255).astype(np.uint8))
    prev.thumbnail((2400, 2400))
    prev.save(a.out / "Lane_preview.png")
    print(f"Listo -> {a.out.resolve()}")


if __name__ == "__main__":
    main()
