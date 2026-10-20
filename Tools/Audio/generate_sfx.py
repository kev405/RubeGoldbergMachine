#!/usr/bin/env python3
"""
generate_sfx.py
Sonidos realistas para la máquina de Rube Goldberg, sintetizados desde cero (sin muestras externas).

Técnicas:
  * Síntesis modal: un golpe = ruido/impulso corto que excita senos amortiguados en las frecuencias
    propias del material (madera, vidrio, acero, latón...). Cada variante cambia frecuencias y caídas
    unos pocos % para que dos golpes nunca suenen idénticos.
  * Bucles perfectos: ruido construido en el dominio de la frecuencia (fases aleatorias -> IFFT), que es
    periódico por construcción; toda modulación usa un número entero de ciclos por bucle. Sin clics al repetir.
  * Fricción stick-slip (crujido de cuerda): tren de impulsos de frecuencia variable filtrado por resonadores.

Salida: WAV mono 16 bit 44.1 kHz en <out>/<categoria>/<nombre>.wav  +  sfx_manifest.json
Uso:    python generate_sfx.py --out ./out --seed 11
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path

import numpy as np
from scipy import signal
from scipy.io import wavfile

SR = 44100
F = np.float64


# ----------------------------------------------------------------------------- utilidades
def t_axis(dur):
    return np.arange(int(dur * SR)) / SR


def modal(freqs, decays, amps, dur, rng, phase_rand=True):
    t = t_axis(dur)
    y = np.zeros_like(t)
    for f, d, a in zip(freqs, decays, amps):
        if f >= SR * 0.47:
            continue
        ph = rng.uniform(0, 2 * np.pi) if phase_rand else 0.0
        y += a * np.exp(-t / d) * np.sin(2 * np.pi * f * t + ph)
    return y


def jit(values, rng, amount):
    return [v * rng.uniform(1 - amount, 1 + amount) for v in values]


def bandpass(x, lo, hi, order=4):
    sos = signal.butter(order, [lo, min(hi, SR * 0.45)], "bandpass", fs=SR, output="sos")
    return signal.sosfilt(sos, x)


def lowpass(x, fc, order=4):
    return signal.sosfilt(signal.butter(order, fc, "lowpass", fs=SR, output="sos"), x)


def highpass(x, fc, order=4):
    return signal.sosfilt(signal.butter(order, fc, "highpass", fs=SR, output="sos"), x)


def burst(dur, decay, rng, lo=None, hi=None):
    """Ruido con envolvente exponencial (el 'contacto' del golpe)."""
    t = t_axis(dur)
    n = rng.normal(size=t.size) * np.exp(-t / decay)
    if lo and hi:
        n = bandpass(n, lo, hi)
    elif hi:
        n = lowpass(n, hi)
    elif lo:
        n = highpass(n, lo)
    return n


def mix(dur, *parts):
    """parts = (señal, inicio_s, ganancia)."""
    y = np.zeros(int(dur * SR))
    tail = int(.006 * SR)
    for s, start, g in parts:
        i = int(start * SR)
        n = min(len(s), len(y) - i)
        if n > 0:
            seg = g * s[:n].copy()
            k = min(tail, n)
            seg[-k:] *= np.linspace(1, 0, k)                         # sin cortes bruscos al final de cada capa
            y[i:i + n] += seg
    return y


def finish(y, peak, fade_in=0.0005, fade_out=0.01):
    y = y - np.mean(y)
    fi, fo = int(fade_in * SR), int(fade_out * SR)
    if fi:
        y[:fi] *= np.linspace(0, 1, fi)
    if fo:
        y[-fo:] *= np.linspace(1, 0, fo)
    m = np.max(np.abs(y))
    return y / m * peak if m > 0 else y


def trim_silence(y, thresh_db=-60):
    a = np.abs(y)
    th = np.max(a) * 10 ** (thresh_db / 20)
    idx = np.where(a > th)[0]
    end = min(len(y), idx[-1] + int(0.02 * SR)) if idx.size else len(y)
    return y[:end]


def periodic_noise(n, shape, rng):
    """Ruido exactamente periódico de n muestras con espectro |X(f)| = shape(f)."""
    f = np.fft.rfftfreq(n, 1 / SR)
    mag = shape(f)
    mag[0] = 0
    X = mag * np.exp(1j * rng.uniform(0, 2 * np.pi, f.size))
    y = np.fft.irfft(X, n)
    return y / np.std(y)


def peak_band(f, center, width, gain):
    return gain * np.exp(-0.5 * ((f - center) / width) ** 2)


def periodic_clicks(n, count, rng, click_fn):
    """Clics aleatorios dentro del bucle; el último se envuelve al inicio -> periódico."""
    y = np.zeros(n)
    for _ in range(count):
        c = click_fn(rng)
        i = rng.integers(0, n)
        idx = (i + np.arange(c.size)) % n
        y[idx] += c
    return y


def write(path, y):
    path.parent.mkdir(parents=True, exist_ok=True)
    wavfile.write(path, SR, (np.clip(y, -1, 1) * 32767).astype(np.int16))


# ----------------------------------------------------------------------------- golpes
def domino(rng):
    # fichas de dominó envejecidas (hueso/baquelita): clic seco y brillante + leve golpe de mesa
    body = modal(jit([2350, 3750, 5300, 7450, 9900], rng, .07), jit([.030, .021, .015, .010, .007], rng, .2),
                 [1, .7, .5, .35, .2], .2, rng)
    click = burst(.01, .0012, rng, lo=2500, hi=14000)
    table = modal(jit([620, 1100], rng, .1), [.018, .012], [.35, .2], .1, rng)
    return finish(mix(.2, (body, 0, 1), (click, 0, .9), (table, 0, 1)), .7)


def wood_block(rng):
    # bloque tipo Jenga: "toc" de madera dura
    m = modal(jit([820, 1460, 2380, 3500, 4700], rng, .08), jit([.060, .045, .030, .020, .012], rng, .2),
              [1, .75, .55, .35, .2], .3, rng)
    contact = burst(.02, .003, rng, lo=300, hi=6000)
    return finish(mix(.3, (m, 0, 1), (contact, 0, .8)), .75)


def book(rng):
    # libro: golpe sordo de tapa + palmada de papel
    thump = burst(.15, .035, rng, hi=420)
    body = modal(jit([135, 230, 410], rng, .1), [.06, .045, .03], [1, .6, .3], .2, rng)
    paper = burst(.05, .006, rng, lo=1800, hi=9000)
    flutter = burst(.12, .03, rng, lo=3000, hi=10000) * (0.5 + 0.5 * np.sin(2 * np.pi * rng.uniform(30, 55) * t_axis(.12)))
    return finish(mix(.25, (thump, 0, 1.4), (body, 0, .8), (paper, .002, .5), (flutter, .01, .12)), .8, fade_out=.03)


def pin(rng):
    # bolo de arce con recubrimiento: "clac" hueco y resonante; a veces rebota (segundo toque)
    def hit(g):
        m = modal(jit([610, 1180, 1960, 2900, 4100, 5600], rng, .06), jit([.12, .09, .07, .05, .035, .02], rng, .2),
                  [1, .8, .7, .5, .4, .25], .45, rng)
        return m + .9 * burst(.45, .0025, rng, lo=1500, hi=12000)
    parts = [(hit(1), 0, 1)]
    if rng.random() < .6:
        parts.append((hit(1), rng.uniform(.018, .05), rng.uniform(.25, .5)))
    return finish(mix(.5, *parts), .85, fade_out=.05)


def bowling_ball(rng):
    # bola de 4 kg cayendo/golpeando sobre tablones: golpe grave con caída de tono
    t = t_axis(.45)
    f = 95 * rng.uniform(.9, 1.1) * (0.75 + 0.25 * np.exp(-t / .03))
    thump = np.exp(-t / .12) * np.sin(2 * np.pi * np.cumsum(f) / SR)
    knock = modal(jit([260, 520, 910, 1500], rng, .08), [.07, .05, .035, .02], [.6, .45, .3, .15], .45, rng)
    contact = burst(.45, .012, rng, hi=900)
    return finish(mix(.45, (thump, 0, 1), (knock, 0, .8), (contact, 0, .6)), .9, fade_out=.06)


def steel_ball(rng):
    # bola de acero cromado contra madera/otra pieza: "clack" seco con brillo metálico
    ping = modal(jit([3150, 5100, 8300, 11800], rng, .06), jit([.045, .032, .02, .012], rng, .25),
                 [.7, .55, .4, .25], .3, rng)
    knock = modal(jit([420, 900, 1650], rng, .1), [.035, .025, .015], [1, .6, .35], .3, rng)
    click = burst(.3, .0009, rng, lo=3000, hi=16000)
    return finish(mix(.3, (ping, 0, 1), (knock, 0, 1), (click, 0, .9)), .8)


def marble(rng):
    # canica de vidrio: "tic" cristalino muy corto
    m = modal(jit([3800, 6150, 9300, 12600], rng, .06), jit([.035, .025, .015, .010], rng, .25),
              [1, .7, .45, .3], .15, rng)
    click = burst(.15, .0007, rng, lo=4000, hi=17000)
    wood = modal(jit([900, 1700], rng, .1), [.015, .01], [.35, .2], .15, rng)
    return finish(mix(.15, (m, 0, 1), (click, 0, .8), (wood, 0, 1)), .6)


def lever(rng):
    # tabla gruesa de la palanca/balancín: golpe de madera grave
    m = modal(jit([185, 420, 760, 1250, 2000], rng, .08), jit([.09, .07, .05, .035, .02], rng, .2),
              [1, .8, .6, .4, .2], .4, rng)
    thump = burst(.4, .02, rng, hi=1500)
    return finish(mix(.4, (m, 0, 1), (thump, 0, .9)), .85, fade_out=.05)


def car(rng):
    # carrito de juguete (lata pintada + plástico): golpe + traqueteo de piezas sueltas
    m = modal(jit([950, 1700, 2650, 3900, 5200], rng, .07), jit([.045, .032, .022, .015, .01], rng, .2),
              [1, .8, .6, .4, .25], .35, rng)
    parts = [(m, 0, 1), (burst(.35, .002, rng, lo=1200, hi=10000), 0, .7)]
    for _ in range(rng.integers(2, 5)):
        r = modal(jit([2800, 4600, 6900], rng, .15), [.008, .006, .004], [1, .6, .3], .05, rng)
        parts.append((r, rng.uniform(.015, .09), rng.uniform(.08, .25)))
    return finish(mix(.35, *parts), .8)


def bell(rng, soft=False):
    # campana de latón: parciales de campana (hum, prime, tercera menor, quinta, nominal...) con batidos
    prime = 520 * rng.uniform(.98, 1.02)
    ratios = [.5, 1, 1.2, 1.5, 2, 2.5, 3, 4.2, 5.4]
    decays = [5.0, 3.6, 3.0, 2.1, 2.6, 1.5, 1.2, .8, .5]
    amps = np.array([.35, .6, .5, .25, 1, .4, .35, .25, .15])
    if soft:
        amps = amps * np.array([1.2, 1.1, 1, .9, .8, .5, .4, .25, .15])
    t = t_axis(6.0)
    y = np.zeros_like(t)
    for r, d, a in zip(ratios, decays, amps):
        for split in (-.35, .35):                                     # doblete -> batido lento "wa-wa"
            f = prime * r + split * r
            y += a * .5 * np.exp(-t / d) * np.sin(2 * np.pi * f * t + rng.uniform(0, 6.28))
    strike = burst(6.0, .002, rng, lo=2000, hi=12000) * (.2 if soft else .5)
    clank = modal([3100, 4700], [.02, .015], [.3, .2], 6.0, rng)
    return finish(y + strike + clank * (.3 if soft else 1), .9, fade_out=.3)


def trampoline(rng):
    # lona tensa (golpe grave) + resortes que vibran y se desafinan (dispersión -> "sproing")
    t = t_axis(.9)
    f = 70 * rng.uniform(.9, 1.1) * (0.8 + 0.4 * np.exp(-t / .04))
    thump = np.exp(-t / .13) * np.sin(2 * np.pi * np.cumsum(f) / SR)
    canvas = burst(.9, .01, rng, lo=600, hi=3000)
    springs = np.zeros_like(t)
    for _ in range(4):
        f0, f1 = rng.uniform(1800, 2600), rng.uniform(450, 700)
        tau = rng.uniform(.04, .08)
        fi = f1 + (f0 - f1) * np.exp(-t / tau)
        wob = 1 + .03 * np.sin(2 * np.pi * rng.uniform(6, 11) * t)
        springs += np.exp(-t / rng.uniform(.18, .3)) * np.sin(2 * np.pi * np.cumsum(fi * wob) / SR)
    springs = bandpass(springs, 300, 6000, 2)
    rattle = modal([3300, 5200], [.01, .008], [1, .6], .9, rng)
    return finish(mix(.9, (thump, 0, 1), (canvas, 0, .3), (springs, .004, .22), (rattle, .002, .12)), .85, fade_out=.1)


def button(rng):
    # pulsador: clic al bajar + tope al fondo (plástico)
    a = modal(jit([2200, 3450, 5150], rng, .05), [.008, .006, .004], [1, .7, .4], .12, rng) + burst(.12, .0008, rng, lo=2500, hi=12000)
    b = modal(jit([1550, 2600], rng, .05), [.01, .007], [1, .6], .12, rng) + .5 * burst(.12, .001, rng, lo=1500, hi=8000)
    thunk = modal([310], [.012], [1], .12, rng)
    return finish(mix(.15, (a, 0, 1), (b, rng.uniform(.03, .045), .55), (thunk, .03, .4)), .7)


def creak(rng):
    # crujido de cuerda en el soporte: fricción stick-slip
    dur = rng.uniform(.55, .85)
    t = t_axis(dur)
    shape = np.sin(np.pi * t / dur) ** 1.5
    rate = 35 + rng.uniform(110, 170) * shape + 15 * np.sin(2 * np.pi * rng.uniform(2, 4) * t)
    phase = np.cumsum(rate) / SR
    imp = np.zeros_like(t)
    ticks = np.where(np.diff(np.floor(phase)) > 0)[0]
    imp[ticks] = rng.uniform(.5, 1, ticks.size) * shape[ticks]
    y = np.zeros_like(t)
    for fc, q, g in ((380, 6, 1), (920, 8, .7), (1750, 10, .45), (2900, 12, .25)):
        b, a = signal.iirpeak(fc * rng.uniform(.93, 1.07), q, fs=SR)
        y += g * signal.lfilter(b, a, imp)
    y += .012 * burst(dur, dur, rng, lo=1500, hi=6000) * shape
    return finish(y, .55, fade_in=.01, fade_out=.05)


def chime(rng):
    # sensor superado: nota suave de marimba (madera + tubo) y su quinta
    def note(f):
        return modal([f, f * 3.93, f * 9.2], [.55, .12, .03], [1, .35, .1], 1.2, rng) + .15 * burst(1.2, .002, rng, lo=800, hi=5000)
    return finish(mix(1.3, (note(784), 0, 1), (note(1175), .085, .8)), .5, fade_out=.2)


# ----------------------------------------------------------------------------- bucles
def loop_roll(rng, kind, seconds=3.0):
    n = int(seconds * SR)

    def pink(f):
        return 1 / np.sqrt(np.maximum(f, 20))

    if kind == "marble":          # vidrio rodando sobre madera: siseo agudo + micro-crujidos
        sh = lambda f: pink(f) * (bandshape(f, 250, 5000) + peak_band(f, 1700, 400, .6) + peak_band(f, 3800, 700, .4))
        y = periodic_noise(n, sh, rng)
        y += periodic_clicks(n, 140, rng, lambda r: r.uniform(.5, 2) * burst(.004, .0006, r, lo=3000, hi=12000))
    elif kind == "bowling":       # bola pesada sobre tablones: retumbo grave + juntas de tablones
        sh = lambda f: pink(f) * (bandshape(f, 35, 700) + peak_band(f, 110, 30, 1.2) + peak_band(f, 240, 60, .6))
        y = periodic_noise(n, sh, rng)
        y += periodic_clicks(n, 9, rng, lambda r: r.uniform(1, 3) * lowpass(burst(.06, .015, r), 500))
    elif kind == "steel":         # acero rodando: retumbo medio con zumbido metálico
        sh = lambda f: pink(f) * (bandshape(f, 120, 3500) + peak_band(f, 450, 90, .8) + peak_band(f, 2550, 250, .5))
        y = periodic_noise(n, sh, rng)
        y += periodic_clicks(n, 40, rng, lambda r: r.uniform(.5, 1.5) * burst(.005, .0008, r, lo=2500, hi=9000))
    else:                         # ruedas de juguete: rumor + traqueteo de eje
        sh = lambda f: pink(f) * (bandshape(f, 90, 2500) + peak_band(f, 300, 70, .9) + peak_band(f, 1300, 200, .4))
        y = periodic_noise(n, sh, rng)
        y += periodic_clicks(n, 60, rng, lambda r: r.uniform(.3, 1.2) * modal([2400, 3900], [.006, .004], [1, .5], .02, r))
    return y / np.sqrt(np.mean(y ** 2)) * .16


def bandshape(f, lo, hi):
    return 1 / (1 + (lo / np.maximum(f, 1)) ** 4) / (1 + (f / hi) ** 4)


def fan_layers(rng, n, rps_curve, blades=4):
    """Ventilador: soplido de banda ancha modulado por el paso de aspas + zumbido del motor."""
    t = np.arange(n) / SR
    air = periodic_noise(n, lambda f: 1 / np.sqrt(np.maximum(f, 30)) * bandshape(f, 150, 3500), rng)
    blade_phase = 2 * np.pi * blades * np.cumsum(rps_curve) / SR
    level = np.clip(rps_curve / rps_curve.max(), 0, 1)
    am = 1 + .35 * np.sin(blade_phase)
    hum = sum(a * np.sin(2 * np.pi * h * 60 * t) for h, a in ((2, .05), (4, .025), (6, .012)))
    whine_f = 1450 * level
    whine = .01 * np.sin(2 * np.pi * np.cumsum(whine_f) / SR)
    return air * .12 * am * level ** 1.5 + (hum + whine) * level


def fan_loop(rng, seconds=4.0):
    n = int(seconds * SR)                           # 2.5 rps * 4 aspas = 10 Hz -> 40 ciclos exactos en 4 s
    return fan_layers(rng, n, np.full(n, 2.5))


def fan_start(rng, seconds=2.5):
    n = int(seconds * SR)
    t = np.arange(n) / SR
    rps = 2.5 * (1 - np.exp(-t / .55)) / (1 - np.exp(-seconds / .55))
    y = fan_layers(rng, n, rps)
    clunk = modal([140, 330, 900], [.05, .03, .02], [1, .6, .3], .3, rng) + burst(.3, .004, rng, lo=800, hi=6000)
    y[:clunk.size] += .25 * clunk
    return y


# ----------------------------------------------------------------------------- catálogo
ONE_SHOTS = {
    "Dominoes/domino_click": (domino, 6),
    "Blocks/wood_block": (wood_block, 5),
    "Books/book_thud": (book, 4),
    "Pins/pin_clack": (pin, 5),
    "BowlingBall/bowling_ball_hit": (bowling_ball, 3),
    "SteelBall/steel_ball_hit": (steel_ball, 5),
    "Marble/marble_click": (marble, 5),
    "Lever/lever_knock": (lever, 4),
    "Car/car_bump": (car, 4),
    "Trampoline/trampoline_bounce": (trampoline, 3),
    "Button/button_click": (button, 3),
    "Pendulum/rope_creak": (creak, 4),
    "Sensor/sensor_chime": (chime, 1),
}


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", type=Path, default=Path("out"))
    ap.add_argument("--seed", type=int, default=11)
    a = ap.parse_args()
    rng = np.random.default_rng(a.seed)
    manifest = {}
    for key, (fn, count) in ONE_SHOTS.items():
        names = []
        for i in range(count):
            y = trim_silence(fn(rng))
            p = a.out / f"{key}_{i + 1:02d}.wav"
            write(p, y)
            names.append(str(p.relative_to(a.out)))
        manifest[key] = names
    for i, soft in enumerate((False, True)):
        p = a.out / f"Bell/bell_ring_{i + 1:02d}.wav"
        write(p, bell(rng, soft))
        manifest.setdefault("Bell/bell_ring", []).append(str(p.relative_to(a.out)))
    loops = {"Marble/marble_roll_loop": loop_roll(rng, "marble"), "BowlingBall/bowling_roll_loop": loop_roll(rng, "bowling"),
             "SteelBall/steel_roll_loop": loop_roll(rng, "steel"), "Car/car_wheels_loop": loop_roll(rng, "car"),
             "Fan/fan_loop": fan_loop(rng), "Fan/fan_start": fan_start(rng)}
    for key, y in loops.items():
        p = a.out / f"{key}.wav"
        write(p, np.clip(y, -1, 1))
        manifest[key] = [str(p.relative_to(a.out))]
    (a.out / "sfx_manifest.json").write_text(json.dumps(manifest, indent=2))
    print(f"Listo -> {a.out.resolve()}  ({sum(len(v) for v in manifest.values())} archivos)")


if __name__ == "__main__":
    main()
