#!/usr/bin/env python3
"""
generate_car.py
Carrito de carreras clásico (ruedas descubiertas): perfiles para la malla + texturas PBR.

car_profile.json    estaciones a lo largo de X (metros, relativo al centro de 'Cuerpo'):
                    x, medio ancho w (Z), base yb, techo yt, exponente n de la sección superelíptica.
                    Frente del carro = -X (hacia donde avanza la máquina).
wheel_profile.json  perfil del neumático (radio, y, v) alrededor del eje Y local de la rueda.

UV de la carrocería (las usa CarSetup.cs):
    s = (x - xMin) / (xMax - xMin)
    t = posición alrededor de la sección: 0 abajo, 0.25 lado +Z, 0.5 arriba, 0.75 lado -Z
    punto: a = 2*pi*t - pi/2;  z = w*sgn(cos a)|cos a|^(2/n);  y = yc + h*sgn(sin a)|sin a|^(2/n)

Salidas: Car_basecolor/normal/metallic_smoothness.png, Tire_basecolor/normal/metallic_smoothness.png
Uso:     python generate_car.py --out ./out --number 7
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont
from scipy import ndimage

F = np.float32
RED = np.array([0.72, 0.035, 0.03], F)
WHITE = np.array([0.92, 0.91, 0.88], F)
BLACK = np.array([0.03, 0.03, 0.035], F)
FONTS = ["/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
         "/usr/share/fonts/truetype/liberation/LiberationSans-Bold.ttf",
         "C:/Windows/Fonts/arialbd.ttf"]


# ----------------------------------------------------------------------------
# Perfil de la carrocería
# ----------------------------------------------------------------------------
def body_profile(n_st=49, x0=-0.4, x1=0.4):
    xs = np.linspace(x0, x1, n_st)
    W, YB, YT, N = [], [], [], []
    for x in xs:
        if x < -0.18:                                  # morro cónico
            t = (x - x0) / (-0.18 - x0)
            w = 0.135 * (0.12 + 0.88 * np.sin(t * np.pi / 2) ** 0.8)
        elif x > 0.30:                                 # cola
            t = (x1 - x) / (x1 - 0.30)
            w = 0.135 * (0.62 + 0.38 * t)
        else:
            w = 0.135
        yb = -0.12 + (0.035 * ((-0.30 - x) / 0.10) ** 2 if x < -0.30 else 0.0)
        if x < -0.12:
            t = (x - x0) / (-0.12 - x0)
            yt = -0.03 + 0.08 * np.sin(t * np.pi / 2)
        elif x < 0.14:
            yt = 0.05
        elif x < 0.33:                                 # joroba del reposacabezas
            yt = 0.05 + 0.055 * np.sin(np.pi * (x - 0.14) / 0.19) ** 0.7
        else:
            yt = 0.05 - 0.02 * (x - 0.33) / (x1 - 0.33)
        n = 2.4 if x < -0.25 else 3.0
        W.append(w); YB.append(yb); YT.append(yt); N.append(n)
    return xs, np.array(W), np.array(YB), np.array(YT), np.array(N)


def section_point(t, w, yb, yt, n):
    a = 2 * np.pi * t - np.pi / 2
    c, s = np.cos(a), np.sin(a)
    yc, h = (yt + yb) / 2, (yt - yb) / 2
    z = w * np.sign(c) * np.abs(c) ** (2 / n)
    y = yc + h * np.sign(s) * np.abs(s) ** (2 / n)
    return y, z


# ----------------------------------------------------------------------------
# Perfil del neumático (alrededor del eje Y local; radio exterior 0.07, ancho 0.06)
# ----------------------------------------------------------------------------
TIRE_CTRL = [(0.046, 0.026), (0.058, 0.0295), (0.066, 0.028), (0.0698, 0.022), (0.0702, 0.012),
             (0.0702, -0.012), (0.0698, -0.022), (0.066, -0.028), (0.058, -0.0295), (0.046, -0.026)]


def catmull(p, per=10):
    p = np.asarray(p, float)
    out = []
    for i in range(len(p) - 1):
        p0, p1, p2, p3 = p[max(i - 1, 0)], p[i], p[i + 1], p[min(i + 2, len(p) - 1)]
        for t in np.linspace(0, 1, per, endpoint=False):
            out.append(0.5 * (2 * p1 + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t * t
                              + (-p0 + 3 * p1 - 3 * p2 + p3) * t ** 3))
    out.append(p[-1])
    poly = np.array(out)
    seg = np.linalg.norm(np.diff(poly, axis=0), axis=1)
    s = np.r_[0, np.cumsum(seg)]
    return poly, s / s[-1]


# ----------------------------------------------------------------------------
def smoothstep(a, b, x):
    t = np.clip((x - a) / (b - a), 0, 1)
    return t * t * (3 - 2 * t)


def mix(c, col, t):
    return c * (1 - t[..., None]) + col * t[..., None]


def fbm(rng, h, w, cell, octaves=3):
    acc, amp, tot = np.zeros((h, w), F), 1.0, 0.0
    for _ in range(octaves):
        gh, gw = int(h / cell) + 3, int(w / cell) + 3
        acc += amp * ndimage.zoom(rng.random((gh, gw)).astype(F), (h / (gh - 1), w / (gw - 1)), order=3)[:h, :w]
        tot += amp
        amp *= 0.5
        cell = max(cell / 2, 1.5)
    acc /= tot
    return (acc - acc.min()) / (np.ptp(acc) + 1e-9)


def glyph_mask(text, px=512):
    font_path = next((f for f in FONTS if Path(f).is_file()), None)
    font = ImageFont.truetype(font_path, int(px * 0.8)) if font_path else ImageFont.load_default()
    img = Image.new("L", (px, px), 0)
    d = ImageDraw.Draw(img)
    b = d.textbbox((0, 0), text, font=font)
    d.text(((px - (b[2] - b[0])) / 2 - b[0], (px - (b[3] - b[1])) / 2 - b[1]), text, font=font, fill=255)
    return np.asarray(img, F) / 255.0


def sample_glyph(g, gx, gy, size):
    """g: máscara cuadrada; (gx, gy) en metros desde el centro, +gx derecha, +gy arriba; size = lado (m)."""
    px = g.shape[0]
    cx = (gx / size + 0.5) * (px - 1)
    cy = (0.5 - gy / size) * (px - 1)
    inside = (cx >= 0) & (cx <= px - 1) & (cy >= 0) & (cy <= px - 1)
    out = ndimage.map_coordinates(g, [np.clip(cy, 0, px - 1), np.clip(cx, 0, px - 1)], order=1)
    return out * inside


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", type=Path, default=Path("out"))
    ap.add_argument("--number", default="7")
    ap.add_argument("--size", type=int, default=2048)
    ap.add_argument("--seed", type=int, default=12)
    a = ap.parse_args()
    a.out.mkdir(parents=True, exist_ok=True)
    rng = np.random.default_rng(a.seed)

    xs, W, YB, YT, N = body_profile()
    (a.out / "car_profile.json").write_text(json.dumps(
        {"x": xs.tolist(), "w": W.tolist(), "yb": YB.tolist(), "yt": YT.tolist(), "n": N.tolist()}, indent=1))
    tire, tv = catmull(TIRE_CTRL)
    (a.out / "wheel_profile.json").write_text(json.dumps(
        {"r": tire[:, 0].tolist(), "y": tire[:, 1].tolist(), "v": tv.tolist()}, indent=1))

    # ---------------- carrocería ----------------
    S = a.size
    s = (np.arange(S, dtype=F) + 0.5) / S
    t = 1 - (np.arange(S, dtype=F) + 0.5) / S                 # fila 0 = t 1
    Sg, Tg = np.meshgrid(s, t)
    X = xs[0] + Sg * (xs[-1] - xs[0])
    w = np.interp(X, xs, W); yb = np.interp(X, xs, YB); yt = np.interp(X, xs, YT); n = np.interp(X, xs, N)
    Y, Z = section_point(Tg, w, yb, yt, n)
    yc, hh = (yt + yb) / 2, (yt - yb) / 2
    up = (Y - yc) / np.maximum(hh, 1e-4)                       # -1 abajo, +1 arriba
    side = np.abs(Z) / np.maximum(w, 1e-4)                    # 1 en los costados

    color = np.broadcast_to(RED, (S, S, 3)).astype(F).copy()
    color *= (0.96 + 0.06 * fbm(rng, S, S, 200))[..., None]
    smooth = np.full((S, S), 0.88, F)
    metal = np.zeros((S, S), F)
    height = np.zeros((S, S), F)

    # franjas de carrera: dos bandas blancas sobre el lomo, de punta a cola
    top = smoothstep(0.35, 0.6, up)
    az = np.abs(Z)
    stripe = (smoothstep(0.010, 0.012, az) * (1 - smoothstep(0.036, 0.038, az))) * top
    color = mix(color, WHITE, stripe)

    # cabina (abertura negra con asiento y volante insinuados)
    ck = ((X - 0.045) / 0.085) ** 2 + (Z / 0.085) ** 2
    cockpit = (1 - smoothstep(0.92, 1.0, ck)) * smoothstep(0.5, 0.7, up)
    seat = (1 - smoothstep(0.0, 0.25, np.abs(X - 0.10) / 0.06)) * cockpit
    wheel_r = np.abs(np.hypot((X + 0.005) / 0.012, Z / 0.04) - 1.0)
    steer = (1 - smoothstep(0.08, 0.2, wheel_r)) * cockpit
    color = mix(color, BLACK, cockpit)
    color = mix(color, np.array([0.12, 0.06, 0.04], F), seat * 0.8)
    color = mix(color, np.array([0.25, 0.25, 0.26], F), steer)
    smooth = smooth * (1 - cockpit) + 0.35 * cockpit
    height -= 1.5 * smoothstep(0.85, 1.0, 1 - np.abs(ck - 1.0) / 0.08) * smoothstep(0.5, 0.7, up)   # borde

    # rejilla del morro
    grille = (1 - smoothstep(-0.385, -0.375, X)) * (1 - smoothstep(0.55, 0.7, np.abs(up)))
    color = mix(color, BLACK, grille)
    smooth = smooth * (1 - grille) + 0.4 * grille

    # números en ambos costados: dibujados en el plano (x, y) de cada lado, legibles desde fuera
    g = glyph_mask(a.number)
    cx, cy, rr = 0.02, -0.03, 0.062
    on_side = smoothstep(0.75, 0.9, side) * (1 - smoothstep(0.75, 0.95, np.abs(up)))
    d = np.hypot(X - cx, Y - cy)
    disc = (1 - smoothstep(rr - 0.0015, rr, d)) * on_side
    ring = (smoothstep(rr - 0.007, rr - 0.0055, d) * (1 - smoothstep(rr - 0.0045, rr - 0.003, d))) * on_side
    plus_z = Z > 0
    gx = np.where(plus_z, -(X - cx), X - cx)                    # lado +Z: la derecha del observador es -X
    num = sample_glyph(g, gx, Y - cy, 0.095) * on_side * (d < rr)
    color = mix(color, WHITE, disc)
    color = mix(color, BLACK, ring)
    color = mix(color, BLACK, num)

    # desgaste: pequeñas saltaduras abajo y polvo en los bajos
    chips = smoothstep(0.86, 0.93, fbm(rng, S, S, 6)) * smoothstep(-0.5, -0.85, up)
    color = mix(color, np.array([0.55, 0.55, 0.55], F), chips)
    dust = smoothstep(-0.3, -0.9, up) * 0.35
    color = mix(color, np.array([0.35, 0.28, 0.22], F), dust)
    smooth -= 0.4 * dust + 0.4 * chips
    height -= 0.8 * chips
    color = np.clip(color, 0, 1)

    def nmap(hgt, k, wrap_u=False):
        p = np.pad(hgt, 1, mode="edge")
        if wrap_u:
            p[1:-1, 0], p[1:-1, -1] = hgt[:, -1], hgt[:, 0]     # continuidad alrededor de la llanta
        gx_ = ndimage.sobel(p, axis=1)[1:-1, 1:-1] / 8.0
        gy_ = ndimage.sobel(p, axis=0)[1:-1, 1:-1] / 8.0
        nn = np.stack([-gx_ * k, gy_ * k, np.ones_like(hgt)], -1)
        return nn / np.linalg.norm(nn, axis=-1, keepdims=True) * 0.5 + 0.5

    def save(arr, name):
        Image.fromarray((np.clip(arr, 0, 1) * 255 + 0.5).astype(np.uint8)).save(a.out / name, optimize=True)

    save(color, "Car_basecolor.png")
    save(nmap(height, 1.0), "Car_normal.png")
    save(np.stack([metal, metal, metal, np.clip(smooth, 0, 1)], -1), "Car_metallic_smoothness.png")

    # ---------------- neumático ----------------
    TW, TH = 1024, 512
    u = (np.arange(TW, dtype=F) + 0.5) / TW
    v = 1 - (np.arange(TH, dtype=F) + 0.5) / TH
    U, V = np.meshgrid(u, v)
    R = np.interp(V, tv, tire[:, 0]); Yw = np.interp(V, tv, tire[:, 1])
    tread = smoothstep(0.0688, 0.0696, R)
    tc = np.broadcast_to(BLACK, (TH, TW, 3)).astype(F) * (0.9 + 0.2 * fbm(rng, TH, TW, 30))[..., None]
    ts = np.full((TH, TW), 0.28, F)
    th_ = np.zeros((TH, TW), F)
    grooves = (np.abs(np.abs(Yw) - 0.008) < 0.0016).astype(F) * tread
    sipes = ((np.sin(U * 2 * np.pi * 48 + np.sign(Yw) * 0.6) > 0.82) & (np.abs(Yw) > 0.0095)).astype(F) * tread
    th_ -= 2.0 * np.maximum(grooves, sipes)
    tc = mix(tc, BLACK * 0.5, np.maximum(grooves, sipes))
    sw_ring = (np.abs(R - 0.062) < 0.0009).astype(F) * (1 - tread)    # aro en relieve del flanco
    th_ += 1.0 * sw_ring
    ts = ts + 0.1 * sw_ring - 0.08 * tread
    save(tc, "Tire_basecolor.png")
    save(nmap(ndimage.gaussian_filter(th_, 0.7), 1.2, wrap_u=True), "Tire_normal.png")
    z0 = np.zeros_like(ts)
    save(np.stack([z0, z0, z0, ts], -1), "Tire_metallic_smoothness.png")
    print(f"Listo -> {a.out.resolve()}")


if __name__ == "__main__":
    main()
