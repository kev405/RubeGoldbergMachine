#!/usr/bin/env python3
"""
generate_jazz.py
Música de fondo original: trío de jazz suave (Rhodes, contrabajo caminante, batería con escobillas) con
vibráfono en la melodía. Compuesta por reglas y sintetizada desde cero: sin muestras ni derechos de terceros.

Forma: AABA de 32 compases en Fa mayor, tocada dos veces (2.º coro con melodía variada), swing ~88 bpm.
El final enlaza con el inicio: la cola de la reverb del último compás se suma al principio -> bucle sin corte.

Salida: <out>/jazz_loop.wav (estéreo 44.1 kHz) y, si hay ffmpeg, jazz_loop.ogg
Uso:    python generate_jazz.py --out ./out --bpm 88 --seed 3
"""
from __future__ import annotations

import argparse
import shutil
import subprocess
from pathlib import Path

import numpy as np
from scipy import signal
from scipy.io import wavfile

SR = 44100

# ----------------------------------------------------------------------------- armonía
QUAL = {"maj7": [0, 4, 7, 11], "7": [0, 4, 7, 10], "m7": [0, 3, 7, 10]}
SCALE = {"maj7": [0, 2, 4, 5, 7, 9, 11], "7": [0, 2, 4, 5, 7, 9, 10], "m7": [0, 2, 3, 5, 7, 9, 10]}
VOICE_A = {"maj7": [4, 7, 11, 14], "7": [4, 9, 10, 14], "m7": [3, 7, 10, 14]}     # 3-5/13-7-9
VOICE_B = {"maj7": [11, 14, 16, 19], "7": [10, 14, 16, 21], "m7": [10, 14, 15, 19]}
PC = {"C": 0, "Db": 1, "D": 2, "Eb": 3, "E": 4, "F": 5, "Gb": 6, "G": 7, "Ab": 8, "A": 9, "Bb": 10, "B": 11}


def ch(s):
    for q in ("maj7", "m7", "7"):
        if s.endswith(q):
            return PC[s[:-len(q)]], q
    raise ValueError(s)


A_SEC = [["Fmaj7"], ["D7"], ["Gm7"], ["C7"], ["Am7"], ["D7"], ["Gm7", "C7"], ["Fmaj7"]]
A_END = [["Fmaj7"], ["D7"], ["Gm7"], ["C7"], ["Am7"], ["D7"], ["Gm7", "C7"], ["Fmaj7", "Cm7"]]   # gira al puente
B_SEC = [["Cm7"], ["F7"], ["Bbmaj7"], ["Bbmaj7"], ["Dm7"], ["G7"], ["Gm7"], ["C7"]]
A_LAST = [["Fmaj7"], ["D7"], ["Gm7"], ["C7"], ["Am7"], ["D7"], ["Gm7", "C7"], ["Am7", "C7"]]  # vuelve al inicio
FORM = A_SEC + A_END + B_SEC + A_LAST


def chord_timeline(bars):
    """Lista de (inicio_en_tiempos, duración, raíz, calidad)."""
    out, b = [], 0.0
    for bar in bars:
        d = 4.0 / len(bar)
        for c in bar:
            r, q = ch(c)
            out.append((b, d, r, q))
            b += d
    return out


def chord_at(timeline, beat):
    for s, d, r, q in timeline:
        if s <= beat < s + d:
            return r, q
    return timeline[-1][2], timeline[-1][3]


def midi_hz(m):
    return 440.0 * 2 ** ((m - 69) / 12)


# ----------------------------------------------------------------------------- instrumentos
def env_adsr(n, a, rel_start, rel, decay_tau):
    t = np.arange(n) / SR
    e = np.minimum(1, t / max(a, 1e-4)) * np.exp(-t / decay_tau)
    r0 = int(rel_start * SR)
    if r0 < n:
        e[r0:] *= np.exp(-(t[r0:] - rel_start) / rel)
    return e


def rhodes(m, dur, vel, rng):
    f = midi_hz(m)
    n = int((dur + 1.2) * SR)
    t = np.arange(n) / SR
    idx = (1.2 + 1.4 * vel) * np.exp(-t / .35) + .25
    tone = np.sin(2 * np.pi * f * t + idx * np.sin(2 * np.pi * f * t))
    tine = .12 * vel * np.exp(-t / .05) * np.sin(2 * np.pi * f * 7.03 * t)
    bark = .2 * vel * np.exp(-t / .2) * np.sin(2 * np.pi * 2 * f * t)
    y = (tone + tine + bark) * env_adsr(n, .003, dur, .12, 2.2 - min(1.4, (m - 48) * .03))
    return y * vel


def vibes(m, dur, vel, rng):
    f = midi_hz(m)
    n = int((dur + 2.5) * SR)
    t = np.arange(n) / SR
    y = (np.sin(2 * np.pi * f * t) * np.exp(-t / 2.4) + .35 * np.sin(2 * np.pi * f * 4 * t) * np.exp(-t / .5)
         + .12 * np.sin(2 * np.pi * f * 10 * t) * np.exp(-t / .06))
    y += .3 * np.exp(-t / .004) * rng.normal(size=n) * .3                 # golpe de baqueta suave
    trem = 1 - .28 * (0.5 + 0.5 * np.sin(2 * np.pi * 5.2 * t + rng.uniform(0, 6.28)))
    damp = np.ones(n)
    r0 = int((dur + .25) * SR)                                              # pedal: deja sonar un poco
    damp[r0:] = np.exp(-(t[r0:] - t[r0]) / .25)
    return y * trem * damp * vel


def bass(m, dur, vel, rng):
    f = midi_hz(m)
    n = int((dur + .3) * SR)
    t = np.arange(n) / SR
    y = np.zeros(n)
    for k in range(1, 9):
        y += (1 / k ** 1.3) * np.exp(-t / (1.1 / (1 + .55 * k))) * np.sin(2 * np.pi * f * k * t + rng.uniform(0, .5))
    thump = signal.sosfilt(signal.butter(2, 260, "lowpass", fs=SR, output="sos"), rng.normal(size=n)) * np.exp(-t / .02)
    y = y + .6 * thump
    e = np.minimum(1, t / .004)
    r0 = int(dur * SR)
    e[r0:] *= np.exp(-(t[r0:] - t[r0]) / .05)
    y = signal.sosfilt(signal.butter(2, 1100, "lowpass", fs=SR, output="sos"), y * e)
    return y * vel


def make_ride(rng):
    n = int(1.6 * SR)
    t = np.arange(n) / SR
    y = np.zeros(n)
    for f in rng.uniform(380, 9500, 60):
        y += np.sin(2 * np.pi * f * t + rng.uniform(0, 6.28)) * np.exp(-t / rng.uniform(.4, 1.4)) / np.sqrt(f / 400)
    ping = signal.sosfilt(signal.butter(2, [3000, 9000], "bandpass", fs=SR, output="sos"), rng.normal(size=n)) * np.exp(-t / .03)
    y = y / np.abs(y).max() + .6 * ping
    return signal.sosfilt(signal.butter(2, 300, "highpass", fs=SR, output="sos"), y)


def brush_swish(beat_s, rng):
    n = int(beat_s * SR)
    t = np.arange(n) / SR
    noise = signal.sosfilt(signal.butter(2, [900, 7000], "bandpass", fs=SR, output="sos"), rng.normal(size=n))
    return noise * np.sin(np.pi * t / (n / SR)) ** 2 * .35


def brush_slap(rng):
    n = int(.25 * SR)
    t = np.arange(n) / SR
    noise = signal.sosfilt(signal.butter(2, [700, 6000], "bandpass", fs=SR, output="sos"), rng.normal(size=n))
    body = np.sin(2 * np.pi * 190 * t) * np.exp(-t / .05) * .4
    return (noise * np.exp(-t / .06) + body)


def hat_chick(rng):
    n = int(.12 * SR)
    t = np.arange(n) / SR
    noise = signal.sosfilt(signal.butter(2, [5000, 12000], "bandpass", fs=SR, output="sos"), rng.normal(size=n))
    return noise * np.exp(-t / .025) + .3 * np.sin(2 * np.pi * 420 * t) * np.exp(-t / .01)


def kick_feather(rng):
    n = int(.3 * SR)
    t = np.arange(n) / SR
    f = 52 + 30 * np.exp(-t / .02)
    return np.sin(2 * np.pi * np.cumsum(f) / SR) * np.exp(-t / .12)


# ----------------------------------------------------------------------------- composición
class Song:
    def __init__(self, bpm, seed, choruses=2):
        self.bpm = bpm
        self.beat = 60.0 / bpm
        self.rng = np.random.default_rng(seed)
        self.tl = chord_timeline(FORM * choruses)
        self.beats = 32 * 4 * choruses
        self.length = self.beats * self.beat
        n = int((self.length + 4.0) * SR)
        self.buf = np.zeros((n, 2))
        self.choruses = choruses

    def t(self, beat):
        """Tiempo en segundos con swing en las corcheas (2:1) y pequeña humanización."""
        whole = np.floor(beat + 1e-6)
        frac = beat - whole
        if abs(frac - 0.5) < 1e-3:
            frac = 2 / 3
        return (whole + frac) * self.beat + self.rng.normal(0, .006)

    def put(self, start_s, mono, pan, gain):
        i = max(0, int(start_s * SR))
        n = min(len(mono), len(self.buf) - i)
        if n <= 0:
            return
        l, r = np.cos((pan + 1) * np.pi / 4), np.sin((pan + 1) * np.pi / 4)
        self.buf[i:i + n, 0] += mono[:n] * l * gain
        self.buf[i:i + n, 1] += mono[:n] * r * gain

    # --- contrabajo caminante -------------------------------------------------------------------------
    def walk(self):
        rng, prev = self.rng, 41
        for idx, (s, d, r, q) in enumerate(self.tl):
            nxt = self.tl[(idx + 1) % len(self.tl)][2]
            nbeats = int(round(d))
            notes = []
            root = min((r + 12 * o for o in range(2, 5)), key=lambda m: abs(m - prev) + (6 if m > 50 else 0))
            notes.append(root)
            for k in range(1, nbeats):
                last = k == nbeats - 1
                if last:
                    tgt = min((nxt + 12 * o for o in range(2, 5)), key=lambda m: abs(m - notes[-1]))
                    notes.append(tgt + rng.choice([-1, 1]) if rng.random() < .7 else tgt + 7 - 12 * (tgt + 7 > 50))
                else:
                    tones = [root + i for i in QUAL[q][1:]] + [root + 12]
                    notes.append(min(tones, key=lambda m: abs(m - notes[-1]) + rng.uniform(0, 4)))
            for k, m in enumerate(notes):
                m = int(np.clip(m, 28, 52))
                self.put(self.t(s + k), bass(m, self.beat * .92, .9 + rng.uniform(-.1, .1) + (.1 if k == 0 else 0), rng), -.15, .2)
                prev = m

    # --- Rhodes: acordes sin fundamental con enlace de voces ------------------------------------------
    def comp(self):
        rng = self.rng
        patterns = [[(0, 1.4)], [(0, .6), (1.5, 1.2)], [(1.5, 1.8)], [(.5, .5), (2.5, 1.2)], [(0, 2.8)], [(-.5, 1.5), (2, .6)], []]
        weights = np.array([3, 4, 3, 2, 2, 2, 1], float)
        prev_v = None
        for s, d, r, q in self.tl:
            cands = []
            for base in (VOICE_A[q], VOICE_B[q]):
                for o in range(3, 6):
                    v = [12 * o + r + i for i in base]
                    if 50 <= min(v) and max(v) <= 76:
                        cands.append(v)
            v = min(cands, key=lambda c: 0 if prev_v is None else sum(abs(a - b) for a, b in zip(c, prev_v)))
            prev_v = v
            pats = patterns if d >= 4 else [[(0, 1.4)], [(-.5, 1.5)], [(.5, 1.2)]]
            w = weights if d >= 4 else np.ones(3)
            for on, du in pats[rng.choice(len(pats), p=w / w.sum())]:
                start = s + on
                if start < 0:
                    continue
                vel = .45 + rng.uniform(-.08, .08)
                for j, m in enumerate(v):                                     # leve arpegiado humano
                    self.put(self.t(start) + j * .006, rhodes(m, du * self.beat, vel, rng), -.25 + .17 * j, .2)

    # --- batería con escobillas ------------------------------------------------------------------------
    def drums(self):
        rng = self.rng
        rides = [make_ride(rng) for _ in range(4)]
        slaps = [brush_slap(rng) for _ in range(3)]
        for bar in range(self.beats // 4):
            b0 = bar * 4
            for on, acc in ((0, .7), (1, 1), (1.5, .55), (2, .7), (3, 1), (3.5, .55)):     # "ding, ding-da-ding"
                self.put(self.t(b0 + on), rides[rng.integers(4)], .35, .075 * acc * rng.uniform(.85, 1.1))
            for k in range(4):
                self.put(self.t(b0 + k) - .02, brush_swish(self.beat, rng), -.05 + .1 * (k % 2), .07)
                self.put(self.t(b0 + k), kick_feather(rng), 0, .14)
            for k in (1, 3):
                self.put(self.t(b0 + k), hat_chick(rng), .2, .095)
                self.put(self.t(b0 + k), slaps[rng.integers(3)], -.1, .065 * rng.uniform(.7, 1.1))
            if bar % 8 == 7:                                                              # remate suave de fin de frase
                self.put(self.t(b0 + 3.5), slaps[0], -.1, .08)

    # --- melodía (vibráfono) ---------------------------------------------------------------------------
    def melody(self):
        rhythms = [
            [(0, 1), (1, .5), (1.5, 1.5), (4.5, .5), (5, 1), (6, 1.5)],
            [(.5, .5), (1, .5), (1.5, .5), (2, 2), (5, .5), (5.5, .5), (6, 2)],
            [(0, 1.5), (1.5, .5), (2, .5), (2.5, 1.5), (5.5, 2)],
            [(1, .5), (1.5, .5), (2, 1), (3, 1), (4, 3)],
        ]

        def phrase_line(start_beat, rhythm, rng, contour, landing=None):
            notes, pos = [], 72
            for k, (on, du) in enumerate(rhythm):
                b = start_beat + on
                r, q = chord_at(self.tl, b)
                strong = abs(on - round(on)) < 1e-6
                pcs = [(r + i) % 12 for i in (QUAL[q] if strong else SCALE[q])]
                cands = [m for m in range(65, 81) if m % 12 in pcs]
                aim = 72 + contour * np.sin(np.pi * (k + 1) / (len(rhythm) + 1))
                if landing is not None and k == len(rhythm) - 1:
                    cands = [m for m in cands if m % 12 == landing] or cands
                pos = min(cands, key=lambda m: abs(m - pos) * 1.0 + .35 * abs(m - aim) + rng.uniform(0, 1.2))
                notes.append((b, du, pos))
            return notes

        def a_section(seed, start):
            rng = np.random.default_rng(seed)
            out = []
            for p in range(4):
                rh = rhythms[(p + seed) % len(rhythms)]
                landing = 9 if p == 3 else None                                  # cierra en La (3.ª de Fa)
                out += phrase_line(start + p * 8, rh, rng, (5, 3, 6, -2)[p], landing)
            return out

        for c in range(self.choruses):
            base = c * 128
            seed_a = 101 + 17 * c
            sections = [(0, seed_a), (32, seed_a), (64, None), (96, seed_a)]
            for off, sd in sections:
                if sd is None:                                                    # puente: el Rhodes respira, vibráfono sobrio
                    rng = np.random.default_rng(55 + c)
                    notes = phrase_line(base + off, rhythms[3], rng, 2) + phrase_line(base + off + 16, rhythms[2], rng, -3)
                else:
                    notes = a_section(sd, base + off)
                for b, du, m in notes:
                    vel = .55 + self.rng.uniform(-.07, .07)
                    self.put(self.t(b), vibes(m, du * self.beat, vel, self.rng), .2, .27)

    # --- mezcla ------------------------------------------------------------------------------------------
    def render(self):
        self.walk()
        self.comp()
        self.drums()
        self.melody()
        dry = self.buf
        ir_n = int(2.2 * SR)
        t = np.arange(ir_n) / SR
        rev = np.zeros_like(dry)
        for chn in range(2):
            ir = self.rng.normal(size=ir_n) * np.exp(-t / (1.9 / 6.9))
            ir = signal.sosfilt(signal.butter(2, 4500, "lowpass", fs=SR, output="sos"), ir)
            ir[: int(.012 * SR)] = 0                                            # pre-delay
            rev[:, chn] = signal.oaconvolve(dry[:, chn], ir)[: len(dry)] / np.sqrt(np.sum(ir ** 2)) * .06
        mix = dry + rev
        # bucle: lo que sobra tras el último compás (colas) se suma al inicio
        L = int(round(self.length * SR))
        loop = mix[:L].copy()
        tail = mix[L:]
        loop[: len(tail)] += tail
        # tono cálido + compresión suave
        loop = signal.sosfilt(signal.butter(1, 9000, "lowpass", fs=SR, output="sos"), loop, axis=0)
        rms = np.sqrt(np.mean(loop ** 2))
        loop = loop / rms * 10 ** (-20 / 20)
        loop = np.tanh(loop * 1.5) / 1.5
        peak = np.abs(loop).max()
        if peak > 10 ** (-1 / 20):
            loop *= 10 ** (-1 / 20) / peak
        return loop


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", type=Path, default=Path("out"))
    ap.add_argument("--bpm", type=float, default=88)
    ap.add_argument("--seed", type=int, default=3)
    a = ap.parse_args()
    a.out.mkdir(parents=True, exist_ok=True)
    song = Song(a.bpm, a.seed)
    y = song.render()
    wav = a.out / "jazz_loop.wav"
    wavfile.write(wav, SR, (np.clip(y, -1, 1) * 32767).astype(np.int16))
    print(f"{len(y) / SR:.1f} s, pico {20 * np.log10(np.abs(y).max()):.1f} dBFS, rms {20 * np.log10(np.sqrt(np.mean(y ** 2))):.1f} dBFS")
    if shutil.which("ffmpeg"):
        subprocess.run(["ffmpeg", "-loglevel", "error", "-y", "-i", str(wav), "-c:a", "libvorbis", "-q:a", "6",
                        str(a.out / "jazz_loop.ogg")], check=True)
    print(f"Listo -> {a.out.resolve()}")


if __name__ == "__main__":
    main()
