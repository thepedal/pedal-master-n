// Pedal Master N — the mastering engine (pure C#, no host types).
//
// A line-by-line port of the Buzz 1503 native machine's DSP (PedalMasterN.cpp
// v0.4.0): same parameters, same arithmetic, same latency. The ReBuzz machine
// class (PedalMasterN.cs) only adapts the host's Sample[] buffers and
// parameter setters to this class; the test harness drives it directly.
//
// Signal chain (every section switches in and out click-free):
//
//   In → DC blocker → Input trim → EQ (Low Cut, Low Shelf, Low Dip, High Shelf, Tilt)
//      → Glue compressor (stereo-linked, SC high-pass, parallel Mix, Makeup)
//      → Stereo (Low Mono, Width — M/S)
//      → True-peak limiter (Gain, Ceiling in dBTP, Release, 2 ms look-ahead)
//      → [master meters] → Match / Bypass → Dither → Out
//
// Differences from the native machine, all below audibility:
//   - no MXCSR flush-to-zero (the host owns the thread); the two float
//     envelopes that can decay into the denormal range are flushed explicitly
//     (Core §30);
//   - the meter link is a direct method call (ReadMeters) instead of a GUI
//     message, filling the same values the protocol v2 reply carried.
//
// MIT License — Copyright (c) 2026 thepedal. See LICENSE.

using System;
using System.Threading;

namespace PedalMasterN
{
    // One meter reading: every value covers the time since that slot was last read.
    // The output values measure the master: after the limiter, before Bypass,
    // Match and Dither.
    public sealed class MeterFrame
    {
        public int   SampleRate, Latency;
        public float InPkL, InPkR, InMsL, InMsR;       // input: sample peak, mean square (1.0 = 0 dBFS)
        public float OutTpL, OutTpR, OutMsL, OutMsR;   // output: true peak, mean square
        public float CompGr, LimGr;                    // max gain reduction, dB (>= 0)
        public float MatchDb;                          // master loudness minus dry loudness, LU
        public readonly float[] Blocks = new float[MasterEngine.LOUD_BLOCK_CAP];
        public int   BlockCount, Dropped;
        public float LL, LR, RR;                       // output correlation means
    }

    public sealed class MasterEngine
    {
        // ── Parameter indices (order = the machine's parameter order) ─────────
        public const int P_INPUT = 0,
            P_EQ = 1, P_LOWCUT = 2, P_LOWFREQ = 3, P_LOWGAIN = 4, P_HIGHFREQ = 5, P_HIGHGAIN = 6, P_TILT = 7,
            P_COMP = 8, P_THRESH = 9, P_RATIO = 10, P_ATTACK = 11, P_RELEASE = 12, P_SCHPF = 13, P_MAKEUP = 14, P_MIX = 15,
            P_LOWMONO = 16, P_WIDTH = 17,
            P_LIMITER = 18, P_LIMGAIN = 19, P_CEILING = 20, P_LIMREL = 21,
            P_BYPASS = 22,
            P_SLOPE = 23, P_MATCH = 24, P_DITHER = 25,
            P_LOWDIP = 26, P_LOWSHAPE = 27,
            P_COUNT = 28;

        // Defaults, in parameter order (mirrors the ParameterDecls).
        public static readonly int[] Defaults =
        {
            240,                         // Input 0 dB
            1, 0, 50, 120, 75, 120, 60,  // EQ on, Low Cut off, 100 Hz, 0 dB, 10 kHz, 0 dB, flat
            0, 60, 1, 4, 4, 0, 0, 100,   // Comp off, -10 dB, 2:1, 10 ms, Auto, SC off, 0 dB, 100 %
            0, 100,                      // Low Mono off, Width 100 %
            1, 0, 50, 50,                // Limiter on, Gain 0 dB, Ceiling -1.0 dBTP, Release 100 ms
            0,                           // Bypass off
            1, 0, 0,                     // Slope 24 dB/oct, Match off, Dither off
            0, 0,                        // Low Dip off, Low Shape Clean
        };

        // ── Constants ─────────────────────────────────────────────────────────
        const float FULL_SCALE = 32768.0f;           // ±32768 → 1.0 = 0 dBFS
        const float SMOOTH_SECONDS = 0.010f;         // parameter glides, ~10 ms

        static readonly float[] RATIOS      = { 1.5f, 2.0f, 3.0f, 4.0f, 6.0f, 10.0f };
        static readonly float[] ATTACKS_MS  = { 0.1f, 0.3f, 1.0f, 3.0f, 10.0f, 30.0f };
        static readonly float[] RELEASES_MS = { 100.0f, 300.0f, 600.0f, 1200.0f };
        const int REL_AUTO = 4;
        static readonly float[] SC_HPF_HZ   = { 0.0f, 60.0f, 90.0f, 120.0f, 150.0f };

        // Vintage-style low end.
        const double DIP_RATIO = 3.0;                // dip centre = 3 × Low Freq
        const double DIP_Q = 0.9;                    // broad, like a passive network
        const double SHELF_K_CLEAN = 1.4142135623730951;   // sqrt(2): Q 0.707, no overshoot
        const double SHELF_K_VINTAGE = 1.0 / 1.2;          // Q 1.2: resonant corner

        // Limiter geometry.
        const double LOOKAHEAD_S = 0.002;
        const int TP_DELAY = Interp.DET_HALF - 1;    // detector interval delay
        const int RING = 2048;                       // delay rings (power of two)
        const int MAX_LOOK = 1000;                   // look-ahead cap (192 kHz → 384)
        const int EQ_STEP = 16;                      // samples between filter redesigns while gliding
        const double MATCH_TAU = 2.0;                // loudness averaging for Match, seconds
        const double MATCH_MAX_DB = 30.0;
        public const int MAX_BLOCK = 256;            // processing chunk (as MAX_BUFFER_LENGTH)

        public const int METER_SLOTS = 4;            // 0 = panel, 1..3 = meter windows
        public const int LOUD_BLOCK_CAP = 64;

        // ── Value mappings (shared with DescribeValue) ────────────────────────
        static double LogMap(int v, int vmin, int vmax, double lo, double hi)
        {
            double t = (double)(v - vmin) / (vmax - vmin);
            return lo * Math.Pow(hi / lo, t);
        }
        public static double LowCutHz   (int v) => LogMap(v, 1, 100, 10.0, 250.0);
        public static double LowShelfHz (int v) => LogMap(v, 0, 100, 25.0, 400.0);
        public static double HighShelfHz(int v) => LogMap(v, 0, 100, 1250.0, 20000.0);
        public static double LowMonoHz  (int v) => LogMap(v, 1, 100, 40.0, 300.0);
        public static double LimRelMs   (int v) => LogMap(v, 0, 100, 10.0, 1000.0);
        public static float InputDb  (int v) => (v - 240) * 0.1f;
        public static float DipDb    (int v) => v * 0.1f;
        public static float ShelfDb  (int v) => (v - 120) * 0.1f;
        public static float TiltDb   (int v) => (v - 60) * 0.1f;
        public static float ThreshDb (int v) => -40.0f + v * 0.5f;
        public static float MakeupDb (int v) => v * 0.1f;
        public static float LimGainDb(int v) => v * 0.1f;
        public static float CeilingDb(int v) => (v - 60) * 0.1f;
        static float DbToLin(float db) => MathF.Pow(10.0f, db / 20.0f);

        public static int LatencyFor(int rate)
        {
            if (rate <= 0) rate = 44100;
            int look = (int)(LOOKAHEAD_S * rate + 0.5);
            if (look < 1) look = 1;
            if (look > MAX_LOOK) look = MAX_LOOK;
            return look - 1 + TP_DELAY + 1;
        }

        // ── State ─────────────────────────────────────────────────────────────
        // Parameter values (written by the machine's setters, read in Process).
        public readonly int[] V = (int[])Defaults.Clone();

        int sr;
        volatile int latency;
        bool first = true;
        bool anyAudible;

        // Glided per-sample values.
        float gIn = 1, eqMix = 1, lcMix = 0, compOn = 0, makeup = 1, mix = 1;
        float lmMix = 0, width = 1, limOn = 1, limGain = 1, ceilLin = 1, bypass = 0;
        float slopeMix = 1, gMaster = 1, gDry = 1;          // 24 dB/oct share; Match gains
        float kGlide = 1;

        // EQ (block-rate glide of the design values).
        double lcHz = 20, lsHz = 100, lsDb = 0, hsHz = 10000, hsDb = 0, tiltDb = 0;
        double lsK = SHELF_K_CLEAN, dipDb = 0;               // shelf corner, dip depth (<= 0)
        double lmHz = 120;
        readonly Svf lowCut = new Svf(), lowCut24a = new Svf(), lowCut24b = new Svf();
        readonly Svf lowShelf = new Svf(), highShelf = new Svf(), lowDip = new Svf();
        readonly Svf dcBlock = new Svf();                     // 5 Hz, always on
        readonly Biquad tilt = new Biquad();                  // first order, fixed 1 kHz pivot
        readonly Svf lmLp1 = new Svf(), lmLp2 = new Svf(), lmHp1 = new Svf(), lmHp2 = new Svf(),
                     lmSHp1 = new Svf(), lmSHp2 = new Svf(); // Low Mono LR4 crossover
        readonly Svf[] lmAll;
        int scHpfSel = -1;
        readonly Biquad scHpf = new Biquad();

        // Compressor (gain reduction in dB, >= 0).
        float grFast, grSlow;
        double detMs;

        // Limiter.
        int look = 96;                                        // look-ahead, samples
        readonly Mirror hist = new Mirror(Interp.DET_TAPS);   // detector history
        float prevPk;
        readonly float[] dqVal = new float[RING];
        readonly int[] dqIdx = new int[RING];
        int dqHead, dqTail;                                   // monotonic min deque
        readonly float[] box = new float[RING];
        double boxSum; int boxPos;
        float relState = 1;
        int sampleIdx;
        readonly float[] dPreL = new float[RING], dPreR = new float[RING];
        readonly float[] dLimL = new float[RING], dLimR = new float[RING];
        readonly float[] dRawL = new float[RING], dRawR = new float[RING];
        int dw;
        float limRelK;

        // Output metering.
        readonly Mirror mh = new Mirror(Interp.TP_TAPS);      // output true-peak meter history
        readonly Biquad kS = new Biquad(), kH = new Biquad();
        int blockLen, blockFill;
        double blockSum;

        // Match: K-weighted loudness of the dry input vs the master, per 100 ms block.
        readonly Biquad kSi = new Biquad(), kHi = new Biquad();
        double inBlockSum;
        double avgIn, avgOut;
        volatile float matchDb;

        // Dither.
        uint rng = 0x9E3779B9u;
        float e0L, e1L, e2L, e0R, e1R, e2R;

        // Meter accumulators, one per reader slot.
        sealed class MeterAcc
        {
            public float inPkL, inPkR, outTpL, outTpR;
            public double inSqL, inSqR, outSqL, outSqR;
            public float compGr, limGr;
            public double count;
            public readonly float[] blocks = new float[LOUD_BLOCK_CAP];
            public int nBlocks, dropped;
            public double cLL, cLR, cRR;

            public void Clear()
            {
                inPkL = inPkR = outTpL = outTpR = 0.0f;
                inSqL = inSqR = outSqL = outSqR = 0.0;
                compGr = limGr = 0.0f;
                count = 0.0;
                nBlocks = dropped = 0;
                cLL = cLR = cRR = 0.0;
            }
        }
        readonly MeterAcc[] meterAcc = new MeterAcc[METER_SLOTS];
        int meterLock;
        readonly float[] doneBlocks = new float[4];

        void LockMeters()   { while (Interlocked.Exchange(ref meterLock, 1) != 0) Thread.SpinWait(1); }
        void UnlockMeters() { Volatile.Write(ref meterLock, 0); }

        public MasterEngine()
        {
            lmAll = new[] { lmLp1, lmLp2, lmHp1, lmHp2, lmSHp1, lmSHp2 };
            for (int s = 0; s < METER_SLOTS; s++) { meterAcc[s] = new MeterAcc(); meterAcc[s].Clear(); }
        }

        public int SampleRate => sr;
        public int Latency => latency;
        public float MatchDb => matchDb;

        // ── Configure: everything that depends on the sample rate ─────────────
        // Redesigns the fixed filters and resets the limiter, delay lines and
        // loudness blocks.
        public void Configure(int rate)
        {
            if (rate <= 0) rate = 44100;
            sr = rate;
            kGlide = 1.0f - MathF.Exp(-1.0f / (SMOOTH_SECONDS * sr));

            look = (int)(LOOKAHEAD_S * sr + 0.5);
            if (look < 1) look = 1;
            if (look > MAX_LOOK) look = MAX_LOOK;
            latency = look - 1 + TP_DELAY + 1;

            Array.Clear(dPreL); Array.Clear(dPreR); Array.Clear(dLimL); Array.Clear(dLimR);
            Array.Clear(dRawL); Array.Clear(dRawR);
            for (int j = 0; j < RING; j++) box[j] = 1.0f;
            boxSum = look; boxPos = 0; dqHead = dqTail = 0; relState = 1; prevPk = 0;
            hist.Reset(); mh.Reset(); dw = 0; sampleIdx = 0;

            kS.KShelf(sr); kH.KHighPass(sr); kS.Reset(); kH.Reset();
            kSi.KShelf(sr); kHi.KHighPass(sr); kSi.Reset(); kHi.Reset();
            inBlockSum = 0; avgIn = avgOut = 0; matchDb = 0.0f;
            e0L = e1L = e2L = e0R = e1R = e2R = 0.0f;
            dcBlock.HighPass(sr, 5.0, 0.70710678); dcBlock.Apply(0); dcBlock.Reset();
            blockLen = (sr + 5) / 10; blockFill = 0; blockSum = 0;

            lowCut.Reset(); lowCut24a.Reset(); lowCut24b.Reset(); lowShelf.Reset(); highShelf.Reset(); tilt.Reset();
            lowDip.Reset();
            foreach (var f in lmAll) f.Reset();
            scHpf.Reset(); scHpfSel = -1;
            first = true;
        }

        // After a stretch of idling (see Idle): land every glide straight on its
        // target, so a parameter changed in the silence doesn't sweep through
        // the first notes.
        public void Wake() { first = true; }

        // ── Block-rate glide of the filter design values ──────────────────────
        void UpdateEq(int n, bool snap)
        {
            double k = snap ? 1.0 : 1.0 - Math.Exp(-(double)n / (SMOOTH_SECONDS * sr));

            // A frequency whose section is fully faded out jumps straight to its target,
            // so switching the section on never sweeps the filter through the audio.
            bool dirty = snap;
            if (V[P_LOWCUT] > 0)
            {
                double t = LowCutHz(V[P_LOWCUT]);
                if (lcMix == 0.0f && lcHz != t) { lcHz = t; dirty = true; }
                else if (GlideLog(ref lcHz, t, k)) dirty = true;
            }
            dirty |= GlideLog(ref lsHz, LowShelfHz(V[P_LOWFREQ]), k);
            dirty |= GlideLin(ref lsDb, ShelfDb(V[P_LOWGAIN]), k);
            dirty |= GlideLog(ref hsHz, HighShelfHz(V[P_HIGHFREQ]), k);
            dirty |= GlideLin(ref hsDb, ShelfDb(V[P_HIGHGAIN]), k);
            dirty |= GlideLin(ref tiltDb, TiltDb(V[P_TILT]), k);
            dirty |= GlideLin(ref lsK, V[P_LOWSHAPE] != 0 ? SHELF_K_VINTAGE : SHELF_K_CLEAN, k);   // Clean ↔ Vintage morphs
            dirty |= GlideLin(ref dipDb, -DipDb(V[P_LOWDIP]), k);
            int ramp = snap ? 0 : n;               // coefficients move linearly across the step
            if (dirty)
            {
                double fmax = 0.45 * sr;           // keep designs below Nyquist at low sample rates
                double fc = lcHz < fmax ? lcHz : fmax;
                lowCut.HighPass(sr, fc, 0.70710678);      lowCut.Apply(ramp);
                lowCut24a.HighPass(sr, fc, 0.54119610);   lowCut24a.Apply(ramp);   // 4th-order Butterworth
                lowCut24b.HighPass(sr, fc, 1.30656296);   lowCut24b.Apply(ramp);
                lowShelf.LowShelf(sr, lsHz < fmax ? lsHz : fmax, lsDb, lsK);    lowShelf.Apply(ramp);
                double fd = DIP_RATIO * lsHz;
                lowDip.Bell(sr, fd < fmax ? fd : fmax, dipDb, DIP_Q);           lowDip.Apply(ramp);
                highShelf.HighShelf(sr, hsHz < fmax ? hsHz : fmax, hsDb);       highShelf.Apply(ramp);
                tilt.Tilt(sr, 1000.0, tiltDb);
            }

            bool lmDirty = snap;
            if (V[P_LOWMONO] > 0)
            {
                double t = LowMonoHz(V[P_LOWMONO]);
                if (lmMix == 0.0f && lmHz != t) { lmHz = t; lmDirty = true; }
                else if (GlideLog(ref lmHz, t, k)) lmDirty = true;
            }
            if (lmDirty)
            {
                const double q = 0.70710678;       // Butterworth² = Linkwitz-Riley 4th order
                for (int i = 0; i < 6; i++)
                {
                    if (i < 2) lmAll[i].LowPass(sr, lmHz, q); else lmAll[i].HighPass(sr, lmHz, q);
                    lmAll[i].Apply(ramp);
                }
            }

            int sel = V[P_SCHPF];
            if (sel != scHpfSel)
            {
                if (sel > 0 && sel < SC_HPF_HZ.Length) scHpf.HighPass(sr, SC_HPF_HZ[sel], 0.70710678);
                scHpf.Reset();
                scHpfSel = sel;
            }
        }

        static bool GlideLog(ref double cur, double tgt, double k)
        {
            if (cur == tgt) return false;
            double lc = Math.Log(cur), lt = Math.Log(tgt);
            lc += (lt - lc) * k;
            cur = Math.Abs(lt - lc) < 1e-4 ? tgt : Math.Exp(lc);
            return true;
        }

        static bool GlideLin(ref double cur, double tgt, double k)
        {
            if (cur == tgt) return false;
            cur += (tgt - cur) * k;
            if (Math.Abs(tgt - cur) < 0.005) cur = tgt;
            return true;
        }

        static float Glide(float cur, float tgt, float k)
        {
            cur += (tgt - cur) * k;
            return MathF.Abs(tgt - cur) < 1e-4f ? tgt : cur;
        }

        // ── Work ──────────────────────────────────────────────────────────────
        // io: interleaved L,R (±32768), n frames, processed in place.
        // read = false treats the input as silence (no WM_READ).
        // Returns true if the (pre-dither) output is audible.
        public bool Work(Span<float> io, int n, int rate, bool read)
        {
            if (!read) io.Slice(0, 2 * n).Clear();
            if (rate > 0 && rate != sr) Configure(rate);
            if (sr == 0) Configure(44100);

            bool audible = false;
            for (int done = 0; done < n; done += MAX_BLOCK)
            {
                int m = n - done < MAX_BLOCK ? n - done : MAX_BLOCK;
                Process(io.Slice(2 * done, 2 * m), m);
                if (anyAudible) audible = true;
            }
            return audible;
        }

        int Clamp(int v, int max) => v < 0 ? 0 : v > max ? max : v;

        void Process(Span<float> ps, int n)
        {
            var v = V;
            // ── Targets ──
            float tIn     = DbToLin(InputDb(v[P_INPUT]));
            float tEq     = v[P_EQ] != 0 ? 1.0f : 0.0f;
            float tLc     = (v[P_EQ] != 0 && v[P_LOWCUT] > 0) ? 1.0f : 0.0f;
            float tComp   = v[P_COMP] != 0 ? 1.0f : 0.0f;
            float tMakeup = DbToLin(MakeupDb(v[P_MAKEUP]));
            float tMix    = v[P_MIX] * 0.01f;
            float tLm     = v[P_LOWMONO] > 0 ? 1.0f : 0.0f;
            float tWidth  = v[P_WIDTH] * 0.01f;
            float tLim    = v[P_LIMITER] != 0 ? 1.0f : 0.0f;
            float tLimG   = DbToLin(LimGainDb(v[P_LIMGAIN]));
            float tCeil   = FULL_SCALE * DbToLin(CeilingDb(v[P_CEILING]));
            float tByp    = v[P_BYPASS] != 0 ? 1.0f : 0.0f;
            float tSlope  = v[P_SLOPE] != 0 ? 1.0f : 0.0f;

            // Match: turn the louder of master and dry down to the other's loudness.
            float mdb = v[P_MATCH] != 0 ? matchDb : 0.0f;
            float tGM = mdb > 0 ? DbToLin(-mdb) : 1.0f;
            float tGD = mdb < 0 ? DbToLin(mdb) : 1.0f;
            float kMatch = 1.0f - MathF.Exp(-1.0f / (0.1f * sr));      // 100 ms glide

            int   dither = v[P_DITHER];
            float qStep  = dither == 1 ? 1.0f / 256.0f : 1.0f;         // ±32768 units: 1.0 = one 16-bit LSB
            float qInv   = 1.0f / qStep;

            bool snapEq = first;
            if (first)
            {
                gIn = tIn; eqMix = tEq; lcMix = tLc; compOn = tComp; makeup = tMakeup; mix = tMix;
                lmMix = tLm; width = tWidth; limOn = tLim; limGain = tLimG; ceilLin = tCeil; bypass = tByp;
                slopeMix = tSlope; gMaster = tGM; gDry = tGD;
                first = false;
            }

            // Compressor constants.
            float thr   = ThreshDb(v[P_THRESH]);
            float ratio = RATIOS[Clamp(v[P_RATIO], 5)];
            float slope = 1.0f - 1.0f / ratio;
            const float knee = 6.0f;
            float aAtt  = 1.0f - MathF.Exp(-1.0f / (ATTACKS_MS[Clamp(v[P_ATTACK], 5)] * 0.001f * sr));
            bool  autoRel = v[P_RELEASE] >= REL_AUTO;
            float aRel  = 1.0f - MathF.Exp(-1.0f / ((autoRel ? 100.0f : RELEASES_MS[Clamp(v[P_RELEASE], 3)]) * 0.001f * sr));
            // Auto release: a second, slow envelope only builds up under sustained
            // compression (0.8 s) and lets go over 0.4 s, so short peaks recover at the
            // fast rate (~0.1 s) and long passages at roughly 1 s.
            float aSlowAtt = 1.0f - MathF.Exp(-1.0f / (0.8f * sr));
            float aSlowRel = 1.0f - MathF.Exp(-1.0f / (0.4f * sr));
            bool  useSc = v[P_SCHPF] > 0;
            double aDet = 1.0 - Math.Exp(-1.0 / (0.005 * sr));

            limRelK = 1.0f - MathF.Exp(-1.0f / ((float)LimRelMs(v[P_LIMREL]) * 0.001f * sr));
            float invLook = 1.0f / look;
            int   D = look - 1 + TP_DELAY + 1;              // output delay, = latency

            float k = kGlide;
            float inPkL = 0, inPkR = 0, outTpL = 0, outTpR = 0;
            double inSqL = 0, inSqR = 0, outSqL = 0, outSqR = 0;
            float maxCompGr = 0, minLimG = 1;
            anyAudible = false;
            int nDone = 0;
            double cLL = 0, cLR = 0, cRR = 0;
            const double invFs = 1.0 / FULL_SCALE;
            const int RM = RING - 1;
            float[] detRev = Interp.DetRev, tpRev = Interp.TpRev;

            for (int s = 0; s < n; s++)
            {
                // Filter designs glide in steps of EQ_STEP samples: small enough that a
                // live frequency sweep doesn't jolt the filters.
                if ((s % EQ_STEP) == 0) UpdateEq(EQ_STEP, snapEq && s == 0);

                float inL = ps[2 * s], inR = ps[2 * s + 1];

                float a = MathF.Abs(inL), b = MathF.Abs(inR);
                if (a > inPkL) inPkL = a;
                if (b > inPkR) inPkR = b;
                inSqL += (double)inL * inL;
                inSqR += (double)inR * inR;

                gIn = Glide(gIn, tIn, k);         eqMix = Glide(eqMix, tEq, k);     lcMix = Glide(lcMix, tLc, k);
                compOn = Glide(compOn, tComp, k); makeup = Glide(makeup, tMakeup, k); mix = Glide(mix, tMix, k);
                lmMix = Glide(lmMix, tLm, k);     width = Glide(width, tWidth, k);
                limOn = Glide(limOn, tLim, k);    limGain = Glide(limGain, tLimG, k); ceilLin = Glide(ceilLin, tCeil, k);
                bypass = Glide(bypass, tByp, k);  slopeMix = Glide(slopeMix, tSlope, k);
                gMaster = Glide(gMaster, tGM, kMatch); gDry = Glide(gDry, tGD, kMatch);

                // ── Dry loudness for Match (K-weighted, per 100 ms block) ──
                {
                    double nl = inL / FULL_SCALE, nr = inR / FULL_SCALE;
                    double kl = kHi.Run(0, kSi.Run(0, nl)), kr = kHi.Run(1, kSi.Run(1, nr));
                    inBlockSum += kl * kl + kr * kr;
                }

                // ── Input trim, DC blocker (5 Hz, 2nd order, always on) ──
                double xl = inL * gIn, xr = inR * gIn;
                dcBlock.Run2(ref xl, ref xr);

                // ── EQ ──  (filters always run, so switching never starts from stale state)
                {
                    double hl = xl, hr = xr, ql = xl, qr = xr;
                    lowCut.Run2(ref hl, ref hr);                  // 12 dB/oct
                    lowCut24a.Run2(ref ql, ref qr);               // 24 dB/oct
                    lowCut24b.Run2(ref ql, ref qr);
                    hl += (ql - hl) * slopeMix;
                    hr += (qr - hr) * slopeMix;
                    double yl = xl + (hl - xl) * lcMix, yr = xr + (hr - xr) * lcMix;
                    lowShelf.Run2(ref yl, ref yr);
                    lowDip.Run2(ref yl, ref yr);                  // unity while Low Dip is Off
                    highShelf.Run2(ref yl, ref yr);
                    yl = tilt.Run(0, yl);
                    yr = tilt.Run(1, yr);
                    xl += (yl - xl) * eqMix;
                    xr += (yr - xr) * eqMix;
                }

                // ── Glue compressor (feed-forward, log domain, stereo-linked) ──
                // The detector always runs, so switching the compressor on lands straight
                // on its settled gain reduction; only the gain maths is skipped while off.
                {
                    double sl = xl, sr2 = xr;
                    if (useSc) { sl = scHpf.Run(0, xl); sr2 = scHpf.Run(1, xr); }
                    // RMS detector (5 ms), louder side wins: steady on low frequencies
                    // where a raw peak detector would ripple.
                    double ql = sl / FULL_SCALE, qr = sr2 / FULL_SCALE;
                    double q  = ql * ql > qr * qr ? ql * ql : qr * qr;
                    detMs += (q - detMs) * aDet;
                    float db  = detMs > 1e-12 ? 10.0f * (float)Math.Log10(detMs) : -120.0f;
                    float over = db - thr, gr;
                    if (2 * over < -knee)     gr = 0.0f;
                    else if (2 * over > knee) gr = over * slope;
                    else { float t = over + knee / 2; gr = slope * t * t / (2 * knee); }

                    grFast += (gr - grFast) * (gr > grFast ? aAtt : aRel);
                    if (grFast < 1e-20f) grFast = 0.0f;                 // denormal guard (Core §30)
                    float grTot = grFast;
                    if (autoRel)
                    {
                        grSlow += (gr - grSlow) * (gr > grSlow ? aSlowAtt : aSlowRel);
                        if (grSlow < 1e-20f) grSlow = 0.0f;
                        if (grSlow > grTot) grTot = grSlow;
                    }
                    if (compOn > 0.0f || tComp > 0.0f)                  // fully off = exactly unity gain
                    {
                        float gc  = DbToLin(-grTot);
                        float eff = makeup * (1.0f - mix + mix * gc);
                        float g   = 1.0f + (eff - 1.0f) * compOn;
                        xl *= g; xr *= g;
                        float shown = grTot * compOn * mix;
                        if (shown > maxCompGr) maxCompGr = shown;
                    }
                }

                // ── Stereo: Low Mono (LR4 on the side channel) and Width ──
                {
                    double M = (xl + xr) * 0.5, S = (xl - xr) * 0.5;
                    double mAp = lmLp2.Run1(lmLp1.Run1(M)) + lmHp2.Run1(lmHp1.Run1(M));   // LR4 sum = all-pass
                    double sHp = lmSHp2.Run1(lmSHp1.Run1(S));
                    M += (mAp - M) * lmMix;
                    S += (sHp - S) * lmMix;
                    S *= width;
                    xl = M + S; xr = M - S;
                }

                // ── True-peak limiter ──
                // Timing, for the sample j that enters now:
                //   the detector sees the interval [j-H, j-H+1) (H = DET_HALF samples of interpolation delay);
                //   need(j-H) = max(interval(j-H), interval(j-H-1)) → attenuation a(j-H);
                //   s = min of a over the last `look` samples, r = s with release, G = mean of r over `look`;
                //   G at this step is safe for sample (j-H) - (look-1), which is what we output,
                //   so the latency is look - 1 + H.
                float pl = (float)xl, pr = (float)xr;
                float gl = pl * limGain, gr2 = pr * limGain;
                // The detector runs even while the limiter is off, so switching it on lands
                // on a settled gain envelope instead of letting the first peaks through.
                hist.Push(gl, gr2);
                int wo = hist.WinOffset;
                float ipk  = Interp.WindowPeak(hist.L, wo, detRev, Interp.DET_TAPS, Interp.DET_PHASES, Interp.DET_HALF);
                float ipk2 = Interp.WindowPeak(hist.R, wo, detRev, Interp.DET_TAPS, Interp.DET_PHASES, Interp.DET_HALF);
                if (ipk2 > ipk) ipk = ipk2;
                float need = ipk > prevPk ? ipk : prevPk;
                prevPk = ipk;
                float att = need > ceilLin ? ceilLin / need : 1.0f;

                int idx = sampleIdx++;
                while (dqTail != dqHead && dqVal[(dqTail - 1) & RM] >= att) dqTail = (dqTail - 1) & RM;
                dqVal[dqTail] = att; dqIdx[dqTail] = idx; dqTail = (dqTail + 1) & RM;
                while (idx - dqIdx[dqHead] >= look) dqHead = (dqHead + 1) & RM;
                float smin = dqVal[dqHead];

                relState += (1.0f - relState) * limRelK;
                if (smin < relState) relState = smin;

                boxSum += relState - box[boxPos];
                box[boxPos] = relState;
                boxPos = (boxPos + 1) % look;
                float G = (float)(boxSum * invLook);
                if (G > 1.0f) G = 1.0f;

                dPreL[dw] = pl;  dPreR[dw] = pr;
                dLimL[dw] = gl;  dLimR[dw] = gr2;
                dRawL[dw] = inL; dRawR[dw] = inR;
                int rd = (dw - D) & RM;
                dw = (dw + 1) & RM;

                float ol = dLimL[rd] * G, orr = dLimR[rd] * G;
                if (limOn >= 1.0f)           // sample-level safety net at the ceiling
                {
                    if (ol >  ceilLin) ol =  ceilLin; else if (ol < -ceilLin) ol = -ceilLin;
                    if (orr > ceilLin) orr = ceilLin; else if (orr < -ceilLin) orr = -ceilLin;
                }
                float pdl = dPreL[rd], pdr = dPreR[rd];
                ol  = pdl + (ol  - pdl) * limOn;
                orr = pdr + (orr - pdr) * limOn;
                if (limOn > 0.0f && G < minLimG) minLimG = G;

                // ── Output metering: the master (before Bypass, Match and Dither) ──
                outSqL += (double)ol * ol;
                outSqR += (double)orr * orr;
                mh.Push(ol, orr);
                int mo = mh.WinOffset;
                float t0 = Interp.WindowPeak(mh.L, mo, tpRev, Interp.TP_TAPS, Interp.TP_PHASES, Interp.TP_TAPS / 2);
                float t1 = Interp.WindowPeak(mh.R, mo, tpRev, Interp.TP_TAPS, Interp.TP_PHASES, Interp.TP_TAPS / 2);
                if (t0 > outTpL) outTpL = t0;
                if (t1 > outTpR) outTpR = t1;

                {
                    double nl = ol * invFs, nr = orr * invFs;
                    cLL += nl * nl; cLR += nl * nr; cRR += nr * nr;
                    double kl = kH.Run(0, kS.Run(0, nl)), kr = kH.Run(1, kS.Run(1, nr));
                    blockSum += kl * kl + kr * kr;
                }
                if (++blockFill >= blockLen)
                {
                    double eOut = blockSum / blockLen, eIn = inBlockSum / blockLen;
                    if (nDone < 4) doneBlocks[nDone++] = (float)eOut;
                    UpdateMatch(eIn, eOut);
                    blockSum = 0; inBlockSum = 0; blockFill = 0;
                }

                // ── Bypass (to the delayed dry input), level-matched when Match is on ──
                float rl = dRawL[rd] * gDry, rr = dRawR[rd] * gDry;
                ol  *= gMaster;
                orr *= gMaster;
                ol  += (rl - ol)  * bypass;
                orr += (rr - orr) * bypass;

                if (MathF.Abs(ol) > 0.5f || MathF.Abs(orr) > 0.5f) anyAudible = true;

                // ── Dither (last): TPDF at the target word length, optionally noise-shaped ──
                if (dither > 0)
                {
                    ol  = DitherOne(ol,  dither, qStep, qInv, ref e0L, ref e1L, ref e2L);
                    orr = DitherOne(orr, dither, qStep, qInv, ref e0R, ref e1R, ref e2R);
                }

                ps[2 * s] = ol; ps[2 * s + 1] = orr;
            }

            float limGrDb = minLimG < 1.0f ? -20.0f * MathF.Log10(minLimG > 1e-6f ? minLimG : 1e-6f) : 0.0f;

            LockMeters();
            for (int sl = 0; sl < METER_SLOTS; sl++)
            {
                var m = meterAcc[sl];
                if (inPkL  > m.inPkL)  m.inPkL  = inPkL;
                if (inPkR  > m.inPkR)  m.inPkR  = inPkR;
                if (outTpL > m.outTpL) m.outTpL = outTpL;
                if (outTpR > m.outTpR) m.outTpR = outTpR;
                m.inSqL += inSqL;  m.inSqR += inSqR;
                m.outSqL += outSqL; m.outSqR += outSqR;
                if (maxCompGr > m.compGr) m.compGr = maxCompGr;
                if (limGrDb   > m.limGr)  m.limGr  = limGrDb;
                m.count += n;
                for (int bi = 0; bi < nDone; bi++)
                {
                    if (m.nBlocks < LOUD_BLOCK_CAP) m.blocks[m.nBlocks++] = doneBlocks[bi];
                    else                            m.dropped++;
                }
                m.cLL += cLL; m.cLR += cLR; m.cRR += cRR;
            }
            UnlockMeters();
        }

        float DitherOne(float x, int dither, float qStep, float qInv, ref float e0, ref float e1, ref float e2)
        {
            rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
            float r1 = (rng & 0xFFFF) * (1.0f / 65536.0f), r2 = (rng >> 16) * (1.0f / 65536.0f);
            float tpdf = (r1 - r2) * qStep;                     // triangular, ±1 LSB
            float want = x;
            if (dither == 3)                                     // E-weighted 3-tap error feedback
                want -= 1.623f * e0 - 0.982f * e1 + 0.109f * e2;
            float q = MathF.Floor((want + tpdf) * qInv + 0.5f) * qStep;
            if (dither == 3)
            {
                e2 = e1; e1 = e0;
                e0 = q - want;                                   // shaping error from the unclamped value
            }
            float hi = FULL_SCALE - qStep;                       // 16-bit: +32767, 24-bit: +32767.996
            if (q > hi) q = hi; else if (q < -FULL_SCALE) q = -FULL_SCALE;
            return q;
        }

        // Running K-weighted loudness of dry and master (100 ms blocks, τ = MATCH_TAU),
        // updated only while both are above -70 LUFS so silence doesn't skew it.
        void UpdateMatch(double eIn, double eOut)
        {
            const double gate = 1.17e-7;                        // -70 LUFS as block energy
            if (eIn < gate || eOut < gate) return;
            double a = 1.0 - Math.Exp(-0.1 / MATCH_TAU);
            if (avgIn <= 0 || avgOut <= 0) { avgIn = eIn; avgOut = eOut; }
            else { avgIn += (eIn - avgIn) * a; avgOut += (eOut - avgOut) * a; }
            double d = 10.0 * Math.Log10(avgOut / avgIn);
            if (d >  MATCH_MAX_DB) d =  MATCH_MAX_DB;
            if (d < -MATCH_MAX_DB) d = -MATCH_MAX_DB;
            matchDb = (float)d;
        }

        // ── Idle: the machine is asleep on silent input ───────────────────────
        // Keeps the meters moving (silent loudness blocks, zero levels) without
        // running the DSP.
        public void Idle(int n)
        {
            if (blockLen <= 0 || n <= 0) return;
            int nDone = 0;
            blockFill += n;
            while (blockFill >= blockLen)
            {
                if (nDone < 4) doneBlocks[nDone++] = (float)(blockSum / blockLen);
                blockSum = 0; inBlockSum = 0; blockFill -= blockLen;
            }
            LockMeters();
            for (int sl = 0; sl < METER_SLOTS; sl++)
            {
                var m = meterAcc[sl];
                m.count += n;
                for (int bi = 0; bi < nDone; bi++)
                {
                    if (m.nBlocks < LOUD_BLOCK_CAP) m.blocks[m.nBlocks++] = doneBlocks[bi];
                    else                            m.dropped++;
                }
            }
            UnlockMeters();
        }

        // ── Meter link ────────────────────────────────────────────────────────
        // Copies slot `slot` into f and clears the slot. Safe from any thread.
        public bool ReadMeters(int slot, MeterFrame f)
        {
            if (f == null || slot < 0 || slot >= METER_SLOTS) return false;
            const float inv = 1.0f / FULL_SCALE;
            LockMeters();
            var a = meterAcc[slot];
            double norm = a.count > 0 ? 1.0 / ((double)FULL_SCALE * FULL_SCALE * a.count) : 0.0;
            double cn   = a.count > 0 ? 1.0 / a.count : 0.0;
            f.InPkL  = a.inPkL * inv;  f.InPkR  = a.inPkR * inv;
            f.InMsL  = (float)(a.inSqL * norm);  f.InMsR  = (float)(a.inSqR * norm);
            f.OutTpL = a.outTpL * inv; f.OutTpR = a.outTpR * inv;
            f.OutMsL = (float)(a.outSqL * norm); f.OutMsR = (float)(a.outSqR * norm);
            f.CompGr = a.compGr;
            f.LimGr  = a.limGr;
            int nb = Math.Min(a.nBlocks, f.Blocks.Length);
            Array.Copy(a.blocks, f.Blocks, nb);
            f.BlockCount = nb;
            f.Dropped = a.dropped + (a.nBlocks - nb);
            f.LL = (float)(a.cLL * cn);
            f.LR = (float)(a.cLR * cn);
            f.RR = (float)(a.cRR * cn);
            a.Clear();
            UnlockMeters();
            f.SampleRate = sr;
            f.Latency = latency;
            f.MatchDb = matchDb;
            return true;
        }
    }
}
