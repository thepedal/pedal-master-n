# Side-by-side check: the C# engine against the native C++ machine it was ported from.
#
# usage: python3 equivalence.py <cpp-harness> <cs-harness>
#
# Both harnesses take the same command line (see tests/harness-src/Harness.cs). Every
# scenario feeds the same signal (music-like noise, tones, transients and silence)
# through both builds and compares the output samples and the meter readings.
#
# The ReBuzz engine is optimised (sections that are off or flat are skipped, the
# limiter detector evaluates all phases at once), so it is no longer bit-identical:
# the limiter's attenuation can round differently in the last bit. Pass criteria:
# output within 1 raw unit (-90 dBFS) of the native build, dithered output within
# 4 (a rounding difference moves the dither by an LSB, and shaping carries it on),
# and every meter reading within 0.1 %.
import numpy as np, subprocess, sys, os, tempfile

CPP, CS = sys.argv[1], sys.argv[2]
P = dict(INPUT=0, EQ=1, LOWCUT=2, LOWFREQ=3, LOWGAIN=4, HIGHFREQ=5, HIGHGAIN=6, TILT=7, COMP=8, THRESH=9,
         RATIO=10, ATTACK=11, RELEASE=12, SCHPF=13, MAKEUP=14, MIX=15, LOWMONO=16, WIDTH=17, LIMITER=18,
         LIMGAIN=19, CEILING=20, LIMREL=21, BYPASS=22, SLOPE=23, MATCH=24, DITHER=25, LOWDIP=26, LOWSHAPE=27)
tmp = tempfile.mkdtemp()

def signal(sr, secs=6.0, seed=1):
    rng = np.random.default_rng(seed)
    n = int(secs * sr); t = np.arange(n) / sr
    # pink-ish noise, a bass line, a kick every 0.5 s, a hat, 0.7 s of silence, a DC offset
    w = rng.standard_normal((2, n))
    b = np.cumsum(w, axis=1); b -= np.convolve(b[0], np.ones(64) / 64, 'same')
    noise = 0.15 * w + 0.02 * np.vstack([b[0], b[1]])
    bass = 0.5 * np.sin(2 * np.pi * 55 * t) * (0.6 + 0.4 * np.sin(2 * np.pi * 0.5 * t))
    kick = np.zeros(n)
    for k0 in range(0, n, sr // 2):
        m = min(n - k0, sr // 8); tt = np.arange(m) / sr
        kick[k0:k0 + m] += 1.2 * np.sin(2 * np.pi * (50 + 120 * np.exp(-tt * 40)) * tt) * np.exp(-tt * 18)
    L = noise[0] + bass + kick + 0.3 * np.sin(2 * np.pi * 1000 * t)
    R = noise[1] + 0.8 * bass + kick + 0.3 * np.sin(2 * np.pi * 1500 * t + 0.4)
    s0, s1 = int(3.0 * sr), int(3.7 * sr)
    L[s0:s1] = 0; R[s0:s1] = 0
    L += 0.01
    x = np.empty(2 * n, np.float32); x[0::2] = 0.6 * 32768 * L; x[1::2] = 0.6 * 32768 * R
    return x

def run(exe, x, sr, blk, args):
    fi = os.path.join(tmp, 'in.f32'); fo = os.path.join(tmp, 'out.f32'); fm = os.path.join(tmp, 'm.txt')
    x.tofile(fi)
    out = subprocess.run([exe, fi, fo, fm, str(sr), str(blk)] + args, capture_output=True, text=True, check=True).stdout
    return np.fromfile(fo, np.float32), int(out.split()[1]), [l.split() for l in open(fm)]

def args_of(d):
    a = []
    for k, v in d.items():
        if k == 'at':
            for fr, name, val in v: a.append(f'@{fr}:{P[name]}={val}')
        else: a.append(f'{P[k]}={v}')
    return a

S = 48000
scen = [
    ('defaults', 48000, 256, {}),
    ('defaults 44.1k blk 100', 44100, 100, {}),
    ('hard limiting +18 dB', 48000, 256, dict(LIMGAIN=180, CEILING=55, LIMREL=20)),
    ('limiter switched off/on live', 48000, 256, dict(LIMGAIN=120, at=[(S, 'LIMITER', 0), (2 * S, 'LIMITER', 1)])),
    ('EQ everything', 48000, 256, dict(LOWCUT=40, LOWGAIN=180, LOWDIP=40, LOWSHAPE=1, HIGHGAIN=60, TILT=90, HIGHFREQ=40)),
    ('EQ 12 dB/oct, sweeps', 48000, 256, dict(LOWCUT=20, SLOPE=0, at=[(S, 'LOWCUT', 90), (S, 'LOWFREQ', 90), (2 * S, 'HIGHFREQ', 0), (int(2.5 * S), 'SLOPE', 1), (4 * S, 'EQ', 0)])),
    ('comp auto release + SC + parallel', 48000, 256, dict(COMP=1, THRESH=40, RATIO=3, ATTACK=2, SCHPF=3, MAKEUP=60, MIX=50)),
    ('comp fixed release, switched live', 48000, 256, dict(THRESH=30, RATIO=5, RELEASE=1, at=[(S, 'COMP', 1), (3 * S, 'COMP', 0)])),
    ('low mono + width', 48000, 256, dict(LOWMONO=50, WIDTH=160, at=[(2 * S, 'WIDTH', 0), (3 * S, 'LOWMONO', 0)])),
    ('input trim sweep', 48000, 256, dict(at=[(S, 'INPUT', 400), (2 * S, 'INPUT', 100)])),
    ('bypass toggles', 48000, 256, dict(LIMGAIN=100, at=[(S, 'BYPASS', 1), (2 * S, 'BYPASS', 0)])),
    ('match + bypass', 48000, 256, dict(LIMGAIN=140, MATCH=1, at=[(4 * S, 'BYPASS', 1)])),
    ('dither 24-bit', 48000, 256, dict(DITHER=1)),
    ('dither 16-bit', 48000, 256, dict(DITHER=2)),
    ('dither 16-bit shaped, 0 dBTP', 48000, 256, dict(DITHER=3, CEILING=60, LIMGAIN=60)),
    ('96k everything on', 96000, 256, dict(COMP=1, LOWCUT=10, LOWMONO=30, LIMGAIN=90, DITHER=3, LOWDIP=60)),
]

fails = 0
print(f'{"scenario":40s} {"lat":>7s} {"identical":>10s} {"max |diff|":>12s} {"(dBFS)":>8s} {"meters":>10s}')
for name, sr, blk, d in scen:
    x = signal(sr)
    a = args_of(d)
    yc, lc, mc = run(CPP, x, sr, blk, a)
    ys, ls, ms = run(CS, x, sr, blk, a)
    diff = np.abs(yc.astype(np.float64) - ys.astype(np.float64))
    same = np.mean(yc == ys)
    # meters: compare every numeric field of every read
    mdiff = 0.0
    if len(mc) != len(ms): mdiff = float('inf')
    else:
        for rc, rs in zip(mc, ms):
            if len(rc) != len(rs): mdiff = float('inf'); break
            for u, v in zip(rc[1:], rs[1:]):
                fu, fv = float(u.lstrip('bc')), float(v.lstrip('bc'))
                mdiff = max(mdiff, abs(fu - fv) / max(1e-9, abs(fu), abs(fv)))
    lim = 4.0 if d.get('DITHER', 0) else 1.0
    ok = lc == ls and diff.max() <= lim and mdiff < 1e-3
    fails += not ok
    dbfs = 20 * np.log10(max(diff.max(), 1e-12) / 32768)
    print(f'{name:40s} {lc:>3d}/{ls:<3d} {100 * same:9.4f}% {diff.max():12.3g} {dbfs:8.1f} {mdiff:10.2g} {"" if ok else "  <-- FAIL"}')
print('ALL OK' if fails == 0 else f'{fails} FAILED')
sys.exit(1 if fails else 0)
