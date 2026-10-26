# Silent-input sleep (SilenceSleep.cs): output with --sleep must match the
# always-running engine wherever sound is present, the sleep must engage in
# long silences, and waking must not click.
# usage: python3 sleep_check.py <cs-harness>
import numpy as np, subprocess, sys, os, tempfile
exe = sys.argv[1]; tmp = tempfile.mkdtemp(); sr = 48000
rng = np.random.default_rng(3)
n = 10 * sr; t = np.arange(n) / sr
L = 0.4 * np.sin(2*np.pi*220*t) + 0.1 * rng.standard_normal(n)
L[2*sr:6*sr] = 0.0                       # 4 s of digital silence
L[int(7.5*sr):int(7.6*sr)] = 0.0         # 100 ms gap: too short to sleep
x = np.empty(2*n, np.float32); x[0::2] = 32768*0.5*L; x[1::2] = 32768*0.5*L
def run(extra):
    x.tofile(f'{tmp}/i'); subprocess.run([exe, f'{tmp}/i', f'{tmp}/o', f'{tmp}/m', str(sr), '256', '19=120'] + extra, check=True, capture_output=True)
    y = np.fromfile(f'{tmp}/o', np.float32); return y[0::2].astype(np.float64)
a = run([]); b = run(['--sleep'])
d = np.abs(a - b)
print(f'max |diff| outside the long silence: {max(d[:2*sr].max(), d[6*sr+200:].max()):.3g}')
print(f'max |diff| inside it (sleeping vs decaying tail): {d[2*sr:6*sr].max():.3g}  (both below 0.5 = -96 dBFS: {max(np.abs(a[3*sr:6*sr]).max(), np.abs(b[3*sr:6*sr]).max()) < 0.5})')
print(f'sleeping output exactly zero from 3.2 s to 6 s: {np.all(b[int(3.2*sr):6*sr] == 0)}')
w = b[6*sr - 50: 6*sr + 400]; aw = a[6*sr - 50: 6*sr + 400]
print(f'wake: first output sample after silence identical to the running engine: {np.allclose(w, aw, atol=1e-3)}')
