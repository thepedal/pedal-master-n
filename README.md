# Pedal Master N (ReBuzz)

Mastering machine for **ReBuzz**, as a managed effect on .NET 10. Stereo in → stereo out;
put it last in the chain, just before Master.

**Status: v1.0.0**, the first ReBuzz release. It is a port of the Buzz 1503 native machine
Pedal Master N v0.4.0 (C++, 32-bit), and keeps all of it:

- the same 28 parameters, in the same order, with the same ranges and defaults;
- the same latency (111 samples at 48 kHz), reported to ReBuzz for delay compensation;
- the same sound: run side by side with the native C++ build on 16 scenarios (every section,
  live switching and sweeps, all dither modes, 44.1, 48 and 96 kHz), the output stays within
  −97 dBFS of it (−132 dBFS in most), and every meter reading within 0.1 % (see *Tests*);
- the parameter panel and the meter window, now inside the machine DLL.

What is new for ReBuzz:

- **About 3.5× less CPU at the defaults**, 1.5–2× less with everything in use:
  - Sections that are switched off, or exactly flat, are not computed at all. At the defaults
    the whole EQ is flat, Low Mono is off and the compressor is off, so none of it runs. A
    section restarts from rest when it is needed again, while its crossfade or gain is still at
    neutral, so this is inaudible (click-tested).
  - The limiter's 8× detector evaluates all 8 phases at once, and is skipped entirely when a
    simple bound shows that no peak in its window can reach the ceiling. That gives exactly the
    same gain, at a fraction of the cost.
  - The compressor's gain computer runs every 16 samples while the compressor is off, so it
    still switches on where it would have been. Parameter glides only run while something is
    moving, and the EQ targets are worked out once per block.
  - The limiter itself always runs, also while switched off, because restarting it cold would
    let a burst through when it is switched back on.
- **Lighter GUI:** text is cached instead of being rebuilt every frame, the meter window draws at
  30 fps (was up to 60), and the panel and window only redraw when something visible changed.
  Nothing redraws while the song is stopped.
- **Tilt glides smoothly.** In v0.4.0, moving Tilt left a small kink in the waveform every 16
  samples (about −38 dB below the signal, a faint tick on pure tones). Tilt is now a zero-delay
  one-pole filter with the same response, and moves without it.
- The GUI reads the meters with a direct call into the machine instead of a GUI message.
- **Sleep on silent input.** ReBuzz keeps calling effects while nothing plays, so the machine
  watches its input: after one second of digital silence (plus the latency) it stops processing
  and only keeps the meters moving. The first sound wakes it, with no difference in the output.
- The right-click menu has **Meters...** and **About...**.

Not yet checked live in ReBuzz: see *Verify in ReBuzz*.

## GUI

The panel is built into `Pedal Master N.NET.dll`, and ReBuzz shows it at the top of the
parameter window. All of its controls write through the machine's parameters, so
patterns, undo and saving stay in step.

- **Header:**
  - **METERS** opens the meter window (also in the machine's right-click menu as **Meters...**).
  - **MATCH** lights blue when engaged, and the header then shows what is being turned
    down, for example *A/B matched: master −11.1 dB*.
  - **BYPASS** lights amber when engaged.
- **Meters:**
  - Input L/R (sample peak) and output L/R (true peak), with a 3 s hold and clip lights.
  - **GR** bars for the compressor and the limiter, 0–24 dB, with a 3 s hold of the
    deepest reduction.
  - A loudness line: **M**, **S**, **I**, **LRA** and correlation.
  - Click the meters to clear holds. Click the loudness line to restart I and LRA.
- **Sections:** INPUT, EQ (with Slope, Low Dip and Low Shape), STEREO, COMP, LIMITER and
  OUTPUT (Dither).
  - EQ, COMP and LIMITER have an **ON** switch, and their faders dim while the section is off.
  - Each fader shows the machine's own value text.
  - Drag to change the value from where you grab it (a click alone never moves it; Ctrl-drag is fine),
    double-click to reset, and use the mouse wheel to step (Ctrl for single steps).
- **Meter window** (one per machine):
  - Big INPUT / OUTPUT (true peak) meters: RMS solid, peak translucent.
  - **GAIN RED.** bars hanging from the top.
  - A loudness strip:
    - **M** and **S** bars around the target.
    - **I** and its distance from the target.
    - **LRA** (EBU Tech 3342).
    - **PLR**: max true peak minus I.
    - Max S and max M.
    - Correlation.
  - **TARGET** cycles −23 / −16 / −14 LUFS.
  - **RESET** restarts I, LRA, PLR and the maxima.
  - **→ T** sets Lim Gain so I lands on the target. It measures from what you've played,
    restarts the measurement after the change, and tells you what it did. With heavy
    limiting the result can fall a little short, so press it again.
  - The OUTPUT meters and all loudness values measure **the master**: after the limiter,
    before Bypass, Match and Dither. Comparing sounds therefore never disturbs the
    measurement.
  - It closes with its machine or the song.

The panel and the window read separate meter slots, so neither takes peaks from the other.
Loudness integration runs from when each opens.

## Signal chain

```
In → DC blocker → Input → EQ → Glue compressor → Stereo (Low Mono, Width)
   → True-peak limiter → [master meters] → Match / Bypass → Dither → Out
```

Every section switches in and out with a 10 ms crossfade, and every level, frequency and
gain glides (≈10 ms), so automation and switching don't click. **Bypass** crossfades to
the dry input, delayed by the machine's latency, so A/B comparisons stay aligned.

- **DC blocker:** a 5 Hz second-order high-pass at the input, always on. It is −0.02 dB at
  20 Hz and only shifts phase slightly above that (see Tests).
- **Match (level-matched A/B):**
  - The machine keeps a running K-weighted loudness of the dry input and of the master
    (2 s averaging, skipping silence below −70 LUFS).
  - While Match is on, whichever of the two is louder is turned **down** to the other's
    loudness, so switching Bypass compares sound, not level.
  - Nothing is ever turned up, so matching can't push anything over 0 dBFS.
  - The gains glide over about 100 ms.
  - Switch Match off before rendering.
- **Dither:** the very last stage, for when the render is written at 16 or 24 bits.
  - **24-bit** and **16-bit** are TPDF (±1 LSB).
  - **16-bit shaped** adds E-weighted 3-tap noise shaping, which puts the noise where the ear
    is least sensitive: about 14 dB less noise below 4 kHz, more above 15 kHz.
  - In ReBuzz sample units (±32768), 1.0 is exactly one 16-bit LSB.
  - Leave Dither off unless this machine is the last thing before a 16- or 24-bit file,
    and keep Master's volume at 0 dB so nothing changes the level after it.
  - When the output is digital silence, ReBuzz receives silence (no dither noise).

- **Input:** trim, −24 … +24 dB in 0.1 dB steps.
- **EQ:**
  - **Low Cut:** 10–250 Hz or Off.
    - **Slope:** 24 dB/oct (4th-order Butterworth, the default) or 12 dB/oct.
    - Switching the slope crossfades.
  - **Low shelf:** 25–400 Hz.
    - **Low Shape:** **Clean** is a Q 0.707 shelf with no overshoot. **Vintage** raises the
      corner's Q to 1.2: at +6 dB that gives about +0.8 dB of bump below the corner and
      about −0.9 dB just above it, like a passive network. Switching morphs smoothly.
  - **Low Dip:** a broad bell cut (Q 0.9), 0–6 dB, centred at **3 × Low Freq** so it
    follows the shelf (Low Freq 60 Hz → dip at 180 Hz). Combined with a low-shelf boost,
    it lifts the deep bass and cleans up the low mids, for example Low Freq 57 Hz,
    +6 dB Vintage and Low Dip 4 dB: +6.7 dB at 30 Hz and −4.4 dB at 180 Hz. It is unity
    when Off, and switches off with the EQ.
  - **High shelf:** 1.25–20 kHz.
  - Filter frequencies are kept below 0.45 × the sample rate.
  - Both shelves are ±12 dB with slope S = 1.
  - **Tilt:** first-order, ±6 dB end to end around 1 kHz. For example, +6 means lows −3 dB and highs +3 dB.
  - The filters are state-variable (trapezoidal) with per-sample coefficient ramps, so
    frequencies can be swept live.
- **Glue compressor:**
  - Feed-forward and stereo-linked, with an RMS detector (5 ms, louder side) and a 6 dB soft knee.
  - **Ratio:** 1.5 / 2 / 3 / 4 / 6 / 10 : 1.
  - **Attack:** 0.1 / 0.3 / 1 / 3 / 10 / 30 ms.
  - **Release:** 0.1 / 0.3 / 0.6 / 1.2 s or **Auto**. Auto recovers from short peaks in about
    0.25 s and from sustained compression in about 1 s.
  - **SC HPF:** Off / 60 / 90 / 120 / 150 Hz, so the bass doesn't pump the mix.
  - **Makeup:** 0 … +20 dB.
  - **Comp Mix:** parallel compression, 0–100 %.
- **Stereo:**
  - **Low Mono** makes everything below 40–300 Hz mono. It uses a Linkwitz-Riley
    (LR4) crossover on the side channel, with the matching all-pass on the mid, so the
    image above the crossover is untouched (M/S phase match better than −70 dB at 1 kHz).
  - **Width:** 0–200 % (0 = mono, 100 = unchanged).
- **True-peak limiter:**
  - **Lim Gain:** 0 … +24 dB into the limiter.
  - **Ceiling:** −6.0 … 0.0 dBTP, default −1.0.
  - **Lim Release:** 10–1000 ms.
  - Uses a 2 ms look-ahead.
  - The detector is 8× oversampled with a 32-tap Kaiser-windowed sinc, stricter than
    the meter's BS.1770 interpolator, so cymbals and noise don't sneak inter-sample peaks
    past the ceiling. The gain is a sliding-minimum plus box-filter envelope, which
    guarantees sample peaks never exceed the ceiling. A sample-level clamp at the ceiling
    is the final safety net.

### Latency

The latency is 2 ms of look-ahead plus 15 samples of detector delay: **111 samples at
48 kHz** and **103 at 44.1 kHz**. It is constant, including with the limiter off or
bypassed, and it is reported through `GetLatency()`, which ReBuzz uses for machine delay
compensation.

## Parameters

| # | Name | Range | Default |
|---|---|---|---|
| 0 | Input | −24 … +24 dB (0.1 dB) | 0 dB |
| 1 | EQ | switch | On |
| 2 | Low Cut | Off, 10–250 Hz | Off |
| 3 | Low Freq | 25–400 Hz | 100 Hz |
| 4 | Low Gain | ±12 dB (0.1 dB) | 0 dB |
| 5 | High Freq | 1.25–20 kHz | 10 kHz |
| 6 | High Gain | ±12 dB (0.1 dB) | 0 dB |
| 7 | Tilt | ±6 dB (0.1 dB) | 0 dB |
| 8 | Comp | switch | Off |
| 9 | Threshold | −40 … 0 dB (0.5 dB) | −10 dB |
| 10 | Ratio | 1.5, 2, 3, 4, 6, 10 : 1 | 2:1 |
| 11 | Attack | 0.1 … 30 ms (6 steps) | 10 ms |
| 12 | Release | 0.1, 0.3, 0.6, 1.2 s, Auto | Auto |
| 13 | SC HPF | Off, 60, 90, 120, 150 Hz | Off |
| 14 | Makeup | 0 … +20 dB (0.1 dB) | 0 dB |
| 15 | Comp Mix | 0–100 % wet | 100 % |
| 16 | Low Mono | Off, 40–300 Hz | Off |
| 17 | Width | 0–200 % | 100 % |
| 18 | Limiter | switch | On |
| 19 | Lim Gain | 0 … +24 dB (0.1 dB) | 0 dB |
| 20 | Ceiling | −6.0 … 0.0 dBTP (0.1 dB) | −1.0 dBTP |
| 21 | Lim Release | 10–1000 ms | 100 ms |
| 22 | Bypass | switch | Off |
| 23 | Slope | 12, 24 dB/oct | 24 dB/oct |
| 24 | Match | switch | Off |
| 25 | Dither | Off, 24-bit, 16-bit, 16-bit shaped | Off |
| 26 | Low Dip | Off, 0.1–6.0 dB (0.1 dB), at 3 × Low Freq | Off |
| 27 | Low Shape | Clean, Vintage | Clean |

With the defaults, the machine is transparent until the signal reaches the −1 dBTP ceiling.
The output is a delayed copy of the input apart from the DC blocker, which has a flat level
response above 20 Hz and a small phase shift.

## Building

Requires the .NET 10 SDK (or Visual Studio 2022 17.14+ / 2026 with **.NET desktop development**) and ReBuzz
installed at `C:\Program Files\ReBuzz`. From a Developer PowerShell, elevated so the deploy can write to
Program Files:

```
dotnet build PedalMasterN.NET.csproj -c Release
```

- Output: `Pedal Master N.NET.dll`, deployed to `C:\Program Files\ReBuzz\Gear\Effects`.
  The `.NET` suffix is what routes it to ReBuzz's managed loader.
- Another install folder: `dotnet build -c Release -p:ReBuzzDir="D:\ReBuzz"`.
- Only the `.dll` is built and deployed: no `.pdb`, no `.deps.json`.
- `BuzzGUI.Interfaces` and `BuzzGUI.Common` are referenced from the ReBuzz folder and never copied.
- If ReBuzz is running, the copy fails quietly (the build stays green) because ReBuzz holds
  the old DLL open. Close ReBuzz, rebuild, and restart it.

### Files

| File | What it is |
|---|---|
| `PedalMasterN.cs` | The ReBuzz machine: parameters, `Work`, value texts, `GetLatency`, menu, meter link |
| `MasterEngine.cs` | The DSP, ported from the native `PedalMasterN.cpp` and optimised for ReBuzz (no host types) |
| `Dsp.cs` | Biquads, state-variable filters, the tilt filter, the true-peak and detector interpolators |
| `SilenceSleep.cs` | Sleep on silent input, and what `Work` returns to ReBuzz |
| `Gui/` | Parameter panel, meter window, loudness maths (EBU R128 / Tech 3342), theme |
| `tests/` | Headless tests (no ReBuzz needed) |

## Tests (headless, no ReBuzz)

`tests/run_all.sh` (Linux or WSL with the .NET 10 SDK and Python 3 with numpy and scipy) builds a
console harness around `MasterEngine` with the same command line as the native machine's C++
harness, so the native test scripts run unchanged. Results:

| Check | Result |
|---|---|
| **Against the native C++ build** (`equivalence.py`), 16 scenarios | all pass: output within −132 dBFS in most, −97 dBFS at worst (compressor switched on live), dither within its LSB; meters within 0.1 %; latency 103 / 111 / 207 at 44.1 / 48 / 96 kHz on both |
| Native regression script `t11.py` against the C++ build, 9 scenarios | defaults, EQ, and limiter and compressor off: identical; limiting: −132 dBFS; compressor switched on mid-way: −86 dBFS |
| Clicks (`t8.py`, and `t8b.py` for every switch-on that section skipping adds) | max Δ² after the change ≈ the steady value in every case (0.4–1.1); Tilt 0 → +6 dB 0.94, was 12.96 in v0.4.0 |
| Native test scripts t1 to t12 | the same numbers as the native build, line for line; e.g. transparency residual −43 dB at 1 kHz, Low Cut 30 Hz 24 dB/oct −49.3 / −24.4 / −3.1 dB at 7.5 / 15 / 30 Hz, limiter worst overshoot +0.21 dB (white noise, 20 dB GR), EBU 3341 cases 1–5 −22.99 … −22.98 LUFS, EBU 3342 LRA 10 / 5 / 20 / 15 LU |
| GUI loudness maths (`t9_loudness.py`) on the machine's own blocks | EBU 3341 I: −22.99, −32.99, −23.01, −23.01, −22.98; EBU 3342 LRA: 10.0, 5.0, 20.0, 15.0 |
| Sleep (`sleep_check.py`) | output exactly 0 while asleep; while awake identical to the always-running engine within 5e-4 raw units (−156 dBFS); wakes without a click |
| CPU, steady state, 48 kHz in 256-sample blocks (`ns` per stereo sample; before → after the optimisations) | defaults ~380 → ~105 (0.5 % of one core); comp + EQ + 12 dB into the limiter ~390 → ~215; everything on ~390 → ~260 |
| Compile check against the ReBuzz `BuzzGUI.Interfaces` sources | clean (WPF from stand-ins: layout and look need a live check) |

`./tests/run_all.sh <native-harness>` also runs the side-by-side check, given a harness built from
the Buzz 1503 source (`tests/harness.cpp` there).

## Verify in ReBuzz (v1.0.0)

1. **Loads:** *Pedal Master N* appears under Effects after a restart of ReBuzz; the Debug Console
   (Ctrl+D) shows no load exception.
2. **Panel:** opening the parameter window shows the panel above the sliders, the faders move the
   parameters (and the sliders move the faders), and nothing is cut off at 100 % and 125 % scaling.
3. **Meters:** IN, OUT, GR and the loudness line move while playing; **METERS** and the right-click
   **Meters...** open one meter window per machine, which closes when the machine is deleted.
4. **Latency:** with ReBuzz's machine delay compensation on, a parallel dry path stays in time
   (111 samples at 48 kHz).
5. **Sleep:** stop the song, wait two seconds, play: the start is clean and nothing is missing.
6. **A/B:** MATCH plus BYPASS compares at equal loudness, and switching clicks nowhere.

## License

MIT, as for the Buzz 1503 version (see `LICENSE`). ReBuzz's interface DLLs are referenced at build
time and are not included.
