# Builds sfx.wav from events.json (exported by promo.html's window.events()).
# Send / receive / key clicks come from the real iMessage recordings in this folder;
# the end-card whoosh and chime are synthesized.
import json, subprocess, wave
import numpy as np

SR = 48000
SENDRECV = 'Imessage send and receive.m4a'   # send swoop ~6.0s, receive ~12.5s
TYPING = 'Typing sound imessage.m4a'         # one key click every ~0.3-0.5s

data = json.load(open('events.json'))
dur = data['duration']
mix = np.zeros(int(SR * (dur + 1)))
rng = np.random.default_rng(7)

def load(path):
    raw = subprocess.run(['ffmpeg', '-v', 'error', '-i', path, '-ac', '1', '-ar', str(SR), '-f', 'f32le', '-'],
                         capture_output=True, check=True).stdout
    return np.frombuffer(raw, np.float32).astype(np.float64)

def cut(x, start, end, fade=0.01):
    y = x[int(start * SR):int(end * SR)].copy()
    n = int(fade * SR)
    y[:n] *= np.linspace(0, 1, n)
    y[-n:] *= np.linspace(1, 0, n)
    return y

def onset(y, frac=0.1):
    return np.argmax(np.abs(y) > np.abs(y).max() * frac) / SR

# --- recorded sounds -------------------------------------------------------
sr_rec = load(SENDRECV)
SEND = cut(sr_rec, 5.95, 6.45)
RECV = cut(sr_rec, 12.51, 13.01)
SEND /= np.abs(SEND).max(); RECV /= np.abs(RECV).max()

typ = load(TYPING)
env = np.convolve(np.abs(typ), np.ones(96) / 96, 'same')
thr = env.max() * 0.15
onsets, i = [], 0
while i < len(env):
    if env[i] > thr:
        onsets.append(i); i += int(0.08 * SR)
    else:
        i += 1
CLICKS = []
for o in onsets[:16]:
    c = cut(typ, o / SR - 0.004, o / SR + 0.07, fade=0.003)
    CLICKS.append(c / np.abs(c).max())

def key():
    return CLICKS[rng.integers(len(CLICKS))] * 0.32 * (0.85 + 0.3 * rng.random())

def send():    return SEND * 0.75
def receive(): return RECV * 0.75
def tapback(): return RECV * 0.4

# --- synthesized end-card sounds -------------------------------------------
def t(n): return np.arange(int(SR * n)) / SR

def tone(f, n, decay, harm=(1.0,), attack=0.003):
    x = t(n)
    y = sum(a * np.sin(2 * np.pi * f * (k + 1) * x) for k, a in enumerate(harm))
    return y * np.exp(-x / decay) * np.clip(x / attack, 0, 1)

def pop():
    x = t(0.12)
    f = 300 + 900 * np.exp(-x / 0.02)
    return np.sin(2 * np.pi * np.cumsum(f) / SR) * np.exp(-x / 0.03) * 0.35

def whoosh():
    n = 0.7
    x = t(n)
    noise = rng.standard_normal(len(x))
    out = np.zeros_like(noise)
    cut_f = 0.02 + 0.25 * np.sin(np.pi * x / n) ** 2      # sweeping one-pole low-pass
    acc = 0.0
    for k in range(len(x)):
        acc += cut_f[k] * (noise[k] - acc)
        out[k] = acc
    return out * np.sin(np.pi * x / n) ** 2 * 1.2

def chime():                                                 # C major arpeggio + pad
    y = np.zeros(int(SR * 3.2))
    for k, f in enumerate([523.25, 659.25, 783.99, 1046.5]):
        n = tone(f, 2.6, 0.7, (1, .3, .1, .05))
        o = int(SR * 0.09 * k)
        y[o:o + len(n)] += n * 0.22
    x = t(3.2)
    pad = sum(np.sin(2 * np.pi * f * x) for f in (261.63, 329.63, 392.0)) / 3
    y += pad * np.exp(-x / 1.4) * np.clip(x / 0.15, 0, 1) * 0.12
    return y

SOUNDS = {'key': key, 'send': send, 'receive': receive, 'tapback': tapback, 'pop': pop, 'whoosh': whoosh, 'chime': chime}
# recorded sounds are cut with a little lead-in; line their attack up with the on-screen event
LEAD = {'send': onset(SEND), 'receive': onset(RECV), 'tapback': onset(RECV), 'key': 0.004}

for e in data['events']:
    s = SOUNDS[e['kind']]()
    at = e['t'] - LEAD.get(e['kind'], 0) + 0.02                  # land just after the bubble pops
    if e['kind'] == 'key':
        at += rng.uniform(-0.015, 0.015)                          # human-ish timing
    o = max(0, int(at * SR))
    mix[o:o + len(s)] += s[:len(mix) - o]

mix = mix[:int(SR * dur)]
fade = int(SR * 1.2)
mix[-fade:] *= np.linspace(1, 0, fade)
mix /= max(1e-9, np.abs(mix).max()) / 0.9
pcm = (mix * 32767).astype(np.int16)
with wave.open('sfx.wav', 'wb') as w:
    w.setnchannels(1); w.setsampwidth(2); w.setframerate(SR)
    w.writeframes(pcm.tobytes())
print('wrote sfx.wav', len(pcm) / SR, 's,', len(CLICKS), 'click samples, send lead %.3fs, recv lead %.3fs' % (LEAD['send'], LEAD['receive']))
