// Pedal Master N — DSP building blocks (pure C#, no host types).
//
// Straight ports of the filters and interpolators in the Buzz 1503 native
// machine (PedalMasterN.cpp v0.4.0). The arithmetic is kept in the same order
// and the same precision as the C++ so the two produce the same samples; see
// tests/ for the side-by-side check.
//
// MIT License — Copyright (c) 2026 thepedal. See LICENSE.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace PedalMasterN
{
    // ── Biquad (double precision, transposed direct form II, 2 channels) ──────
    internal sealed class Biquad
    {
        public double b0 = 1, b1, b2, a1, a2;
        double z1L, z1R, z2L, z2R;

        public void Reset() { z1L = z1R = z2L = z2R = 0.0; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double Run(int ch, double x)
        {
            if (ch == 0)
            {
                double y = b0 * x + z1L;
                z1L = b1 * x - a1 * y + z2L;
                z2L = b2 * x - a2 * y;
                return y;
            }
            else
            {
                double y = b0 * x + z1R;
                z1R = b1 * x - a1 * y + z2R;
                z2R = b2 * x - a2 * y;
                return y;
            }
        }

        public void Set(double B0, double B1, double B2, double A0, double A1, double A2)
        {
            b0 = B0 / A0; b1 = B1 / A0; b2 = B2 / A0; a1 = A1 / A0; a2 = A2 / A0;
        }

        // RBJ cookbook high-pass.
        public void HighPass(double fs, double f, double q)
        {
            double w = 2 * Math.PI * f / fs, cw = Math.Cos(w), al = Math.Sin(w) / (2 * q);
            Set((1 + cw) / 2, -(1 + cw), (1 + cw) / 2, 1 + al, -2 * cw, 1 - al);
        }

        // First-order tilt around f0: gain 1/A below, A above, exactly 0 dB at f0.
        // Analog H(s) = (A·s + w0) / (s + A·w0), bilinear with prewarping.
        public void Tilt(double fs, double f0, double db)
        {
            double A = Math.Pow(10.0, db / 40.0);
            double k = Math.Tan(Math.PI * f0 / fs);
            Set(A + k, k - A, 0, 1 + A * k, A * k - 1, 0);
        }

        // K-weighting (ITU-R BS.1770-4) stage 1: high shelf.
        public void KShelf(double rate)
        {
            double f0 = 1681.974450955533, G = 3.999843853973347, Q = 0.7071752369554196;
            double K = Math.Tan(Math.PI * f0 / rate), Vh = Math.Pow(10.0, G / 20.0), Vb = Math.Pow(Vh, 0.4996667741545416);
            double a0 = 1.0 + K / Q + K * K;
            b0 = (Vh + Vb * K / Q + K * K) / a0; b1 = 2.0 * (K * K - Vh) / a0; b2 = (Vh - Vb * K / Q + K * K) / a0;
            a1 = 2.0 * (K * K - 1.0) / a0;       a2 = (1.0 - K / Q + K * K) / a0;
        }

        // K-weighting stage 2: RLB high-pass.
        public void KHighPass(double rate)
        {
            double f0 = 38.13547087602444, Q = 0.5003270373238773;
            double K = Math.Tan(Math.PI * f0 / rate), a0 = 1.0 + K / Q + K * K;
            b0 = 1.0; b1 = -2.0; b2 = 1.0;
            a1 = 2.0 * (K * K - 1.0) / a0; a2 = (1.0 - K / Q + K * K) / a0;
        }
    }

    // ── State-variable filter (trapezoidal, after A. Simper, Cytomic) ─────────
    // Same responses as the RBJ biquads, but it stays well-behaved while its
    // frequency and gain move, so the EQ and Low Mono can glide without clicks.
    internal sealed class Svf
    {
        // c = current coefficients {a1, a2, a3, m0, m1, m2}; t = target; d = per-sample step.
        double c0 = 1, c1, c2, c3 = 1, c4, c5;
        readonly double[] t = { 1, 0, 0, 1, 0, 0 };
        readonly double[] d = new double[6];
        int left;                                          // samples of ramp remaining
        double ic1L, ic1R, ic2L, ic2R;

        public void Reset() { ic1L = ic1R = ic2L = ic2R = 0.0; }

        // True while the design is exactly unity (output = input, bit for bit) and
        // not ramping: a flat shelf or a 0 dB bell. Such a filter can be skipped.
        public bool IsUnity => left == 0 && c3 == 1.0 && c4 == 0.0 && c5 == 0.0;

        void Core(double g, double k, double m0, double m1, double m2)
        {
            double a1 = 1.0 / (1.0 + g * (g + k));
            t[0] = a1; t[1] = g * a1; t[2] = g * g * a1; t[3] = m0; t[4] = m1; t[5] = m2;
        }

        public void LowPass(double fs, double f, double q)  { Core(Math.Tan(Math.PI * f / fs), 1 / q, 0, 0, 1); }
        public void HighPass(double fs, double f, double q) { double k = 1 / q; Core(Math.Tan(Math.PI * f / fs), k, 1, -k, -1); }

        // k = 1/Q of the shelf corner: sqrt(2) = Q 0.707 (slope S = 1, no overshoot);
        // smaller k = a resonant corner (a bump on the boosted side, a dip just past it).
        public void LowShelf(double fs, double f, double db, double k)
        {
            double A = Math.Pow(10.0, db / 40.0);
            Core(Math.Tan(Math.PI * f / fs) / Math.Sqrt(A), k, 1, k * (A - 1), A * A - 1);
        }

        // Peaking bell, constant-Q style (as the RBJ peaking EQ). 0 dB = exact unity.
        public void Bell(double fs, double f, double db, double q)
        {
            double A = Math.Pow(10.0, db / 40.0), k = 1.0 / (q * A);
            Core(Math.Tan(Math.PI * f / fs), k, 1, k * (A * A - 1), 0);
        }

        public void HighShelf(double fs, double f, double db)
        {
            double A = Math.Pow(10.0, db / 40.0), k = Math.Sqrt(2.0);
            Core(Math.Tan(Math.PI * f / fs) * Math.Sqrt(A), k, A * A, k * (1 - A) * A, 1 - A * A);
        }

        // Move to the new design: at once (n = 0) or linearly over n samples.
        public void Apply(int n)
        {
            if (n <= 0)
            {
                c0 = t[0]; c1 = t[1]; c2 = t[2]; c3 = t[3]; c4 = t[4]; c5 = t[5];
                for (int i = 0; i < 6; i++) d[i] = 0;
                left = 0;
                return;
            }
            d[0] = (t[0] - c0) / n; d[1] = (t[1] - c1) / n; d[2] = (t[2] - c2) / n;
            d[3] = (t[3] - c3) / n; d[4] = (t[4] - c4) / n; d[5] = (t[5] - c5) / n;
            left = n;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void Step()
        {
            if (left <= 0) return;
            if (--left == 0) { c0 = t[0]; c1 = t[1]; c2 = t[2]; c3 = t[3]; c4 = t[4]; c5 = t[5]; }
            else { c0 += d[0]; c1 += d[1]; c2 += d[2]; c3 += d[3]; c4 += d[4]; c5 += d[5]; }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        double TickL(double v0)
        {
            double v3 = v0 - ic2L;
            double v1 = c0 * ic1L + c1 * v3;
            double v2 = ic2L + c1 * ic1L + c2 * v3;
            ic1L = 2 * v1 - ic1L;
            ic2L = 2 * v2 - ic2L;
            return c3 * v0 + c4 * v1 + c5 * v2;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        double TickR(double v0)
        {
            double v3 = v0 - ic2R;
            double v1 = c0 * ic1R + c1 * v3;
            double v2 = ic2R + c1 * ic1R + c2 * v3;
            ic1R = 2 * v1 - ic1R;
            ic2R = 2 * v2 - ic2R;
            return c3 * v0 + c4 * v1 + c5 * v2;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double Run1(double x) { Step(); return TickL(x); }                          // one channel

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Run2(ref double l, ref double r) { Step(); l = TickL(l); r = TickR(r); } // stereo pair
    }

    // ── Two-lane (stereo) versions ────────────────────────────────────────────
    // The same filters with left and right in the two lanes of a Vector128<double>:
    // one instruction does both channels. Every lane performs exactly the scalar
    // operations in the same order (no FMA), so the results are bit-identical to
    // the scalar classes above. SvfV can also give each lane its own design
    // (Low Mono runs the mid all-pass and the side high-pass side by side).

    internal sealed class BiquadV
    {
        Vector128<double> b0 = Vector128.Create(1.0), b1, b2, a1, a2;
        Vector128<double> z1, z2;

        public void Reset() { z1 = z2 = Vector128<double>.Zero; }

        void Set(Biquad q)
        {
            b0 = Vector128.Create(q.b0); b1 = Vector128.Create(q.b1); b2 = Vector128.Create(q.b2);
            a1 = Vector128.Create(q.a1); a2 = Vector128.Create(q.a2);
        }
        public void HighPass(double fs, double f, double qf) { var q = new Biquad(); q.HighPass(fs, f, qf); Set(q); }
        public void KShelf(double rate)    { var q = new Biquad(); q.KShelf(rate); Set(q); }
        public void KHighPass(double rate) { var q = new Biquad(); q.KHighPass(rate); Set(q); }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector128<double> Run(Vector128<double> x)
        {
            var y = b0 * x + z1;
            z1 = b1 * x - a1 * y + z2;
            z2 = b2 * x - a2 * y;
            return y;
        }
    }

    // Four lanes: the K-weighting of the dry input (L, R) and of the master (L, R)
    // share their coefficients, so both run as one filter.
    internal sealed class BiquadV4
    {
        Vector256<double> b0 = Vector256.Create(1.0), b1, b2, a1, a2;
        Vector256<double> z1, z2;

        public void Reset() { z1 = z2 = Vector256<double>.Zero; }

        void Set(Biquad q)
        {
            b0 = Vector256.Create(q.b0); b1 = Vector256.Create(q.b1); b2 = Vector256.Create(q.b2);
            a1 = Vector256.Create(q.a1); a2 = Vector256.Create(q.a2);
        }
        public void KShelf(double rate)    { var q = new Biquad(); q.KShelf(rate); Set(q); }
        public void KHighPass(double rate) { var q = new Biquad(); q.KHighPass(rate); Set(q); }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector256<double> Run(Vector256<double> x)
        {
            var y = b0 * x + z1;
            z1 = b1 * x - a1 * y + z2;
            z2 = b2 * x - a2 * y;
            return y;
        }
    }

    internal sealed class SvfV
    {
        static readonly Vector128<double> Two = Vector128.Create(2.0);
        Vector128<double> c0 = Vector128.Create(1.0), c1, c2, c3 = Vector128.Create(1.0), c4, c5;
        Vector128<double> t0 = Vector128.Create(1.0), t1, t2, t3 = Vector128.Create(1.0), t4, t5;
        Vector128<double> d0, d1, d2, d3, d4, d5;
        int left;
        Vector128<double> ic1, ic2;

        public void Reset() { ic1 = ic2 = Vector128<double>.Zero; }

        public bool IsUnity => left == 0 && Vector128.EqualsAll(c3, Vector128.Create(1.0))
                               && Vector128.EqualsAll(c4, Vector128<double>.Zero) && Vector128.EqualsAll(c5, Vector128<double>.Zero);

        static Vector128<double> Put(Vector128<double> v, int lane, double x) => lane < 0 ? Vector128.Create(x) : v.WithElement(lane, x);

        // lane = -1: both channels; 0 or 1: that lane only.
        void Core(int lane, double g, double k, double m0, double m1, double m2)
        {
            double a1 = 1.0 / (1.0 + g * (g + k));
            t0 = Put(t0, lane, a1); t1 = Put(t1, lane, g * a1); t2 = Put(t2, lane, g * g * a1);
            t3 = Put(t3, lane, m0); t4 = Put(t4, lane, m1); t5 = Put(t5, lane, m2);
        }

        public void LowPass(double fs, double f, double q, int lane = -1)  { Core(lane, Math.Tan(Math.PI * f / fs), 1 / q, 0, 0, 1); }
        public void HighPass(double fs, double f, double q, int lane = -1) { double k = 1 / q; Core(lane, Math.Tan(Math.PI * f / fs), k, 1, -k, -1); }
        // 2nd-order all-pass: LP + HP of a Butterworth pair squared (an LR4 crossover's sum).
        public void AllPass(double fs, double f, double q, int lane = -1)  { double k = 1 / q; Core(lane, Math.Tan(Math.PI * f / fs), k, 1, -2 * k, 0); }
        // Output = input, exactly (a lane that only passes through).
        public void Through(double fs, double f, double q, int lane = -1)  { Core(lane, Math.Tan(Math.PI * f / fs), 1 / q, 1, 0, 0); }

        public void LowShelf(double fs, double f, double db, double k)
        {
            double A = Math.Pow(10.0, db / 40.0);
            Core(-1, Math.Tan(Math.PI * f / fs) / Math.Sqrt(A), k, 1, k * (A - 1), A * A - 1);
        }
        public void Bell(double fs, double f, double db, double q)
        {
            double A = Math.Pow(10.0, db / 40.0), k = 1.0 / (q * A);
            Core(-1, Math.Tan(Math.PI * f / fs), k, 1, k * (A * A - 1), 0);
        }
        public void HighShelf(double fs, double f, double db)
        {
            double A = Math.Pow(10.0, db / 40.0), k = Math.Sqrt(2.0);
            Core(-1, Math.Tan(Math.PI * f / fs) * Math.Sqrt(A), k, A * A, k * (1 - A) * A, 1 - A * A);
        }

        public void Apply(int n)
        {
            if (n <= 0)
            {
                c0 = t0; c1 = t1; c2 = t2; c3 = t3; c4 = t4; c5 = t5;
                d0 = d1 = d2 = d3 = d4 = d5 = Vector128<double>.Zero;
                left = 0;
                return;
            }
            var inv = Vector128.Create((double)n);
            d0 = (t0 - c0) / inv; d1 = (t1 - c1) / inv; d2 = (t2 - c2) / inv;
            d3 = (t3 - c3) / inv; d4 = (t4 - c4) / inv; d5 = (t5 - c5) / inv;
            left = n;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector128<double> Run(Vector128<double> v0)
        {
            if (left > 0)
            {
                if (--left == 0) { c0 = t0; c1 = t1; c2 = t2; c3 = t3; c4 = t4; c5 = t5; }
                else { c0 += d0; c1 += d1; c2 += d2; c3 += d3; c4 += d4; c5 += d5; }
            }
            var v3 = v0 - ic2;
            var v1 = c0 * ic1 + c1 * v3;
            var v2 = ic2 + c1 * ic1 + c2 * v3;
            ic1 = Two * v1 - ic1;
            ic2 = Two * v2 - ic2;
            return c3 * v0 + c4 * v1 + c5 * v2;
        }
    }

    internal sealed class Tilt1V
    {
        Vector128<double> G, mH = Vector128.Create(1.0), mL = Vector128.Create(1.0);
        double tG, tH = 1, tL = 1, cG, cH = 1, cL = 1, dG, dH, dL;
        int left;
        Vector128<double> s;

        public void Reset() { s = Vector128<double>.Zero; }

        public void Design(double fs, double f0, double db)
        {
            double A = Math.Pow(10.0, db / 40.0);
            double g = A * Math.Tan(Math.PI * f0 / fs);
            tG = g / (1.0 + g); tH = A; tL = 1.0 / A;
        }

        public void Apply(int n)
        {
            if (n <= 0) { cG = tG; cH = tH; cL = tL; left = 0; Load(); return; }
            dG = (tG - cG) / n; dH = (tH - cH) / n; dL = (tL - cL) / n;
            left = n;
        }

        void Load() { G = Vector128.Create(cG); mH = Vector128.Create(cH); mL = Vector128.Create(cL); }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector128<double> Run(Vector128<double> x)
        {
            if (left > 0)
            {
                if (--left == 0) { cG = tG; cH = tH; cL = tL; }
                else { cG += dG; cH += dH; cL += dL; }
                Load();
            }
            var v = (x - s) * G;
            var lp = v + s;
            s = lp + v;
            return mH * (x - lp) + mL * lp;
        }
    }

    // ── First-order tilt (trapezoidal one-pole, per-sample coefficient ramps) ──
    // Same response as the bilinear design H(s) = (A·s + w0) / (s + A·w0) prewarped
    // at f0 (gain 1/A below, A above, exactly 0 dB at f0), written as
    // A·HP + LP/A around a zero-delay one-pole at A·w0. Unlike a direct-form
    // biquad it stays smooth while the tilt moves.
    internal sealed class Tilt1
    {
        double G, mH = 1, mL = 1;                          // current
        double tG, tH = 1, tL = 1, dG, dH, dL;             // target and per-sample step
        int left;
        double sL, sR;

        public void Reset() { sL = sR = 0.0; }

        public void Design(double fs, double f0, double db)
        {
            double A = Math.Pow(10.0, db / 40.0);
            double g = A * Math.Tan(Math.PI * f0 / fs);    // the pole A·w0, prewarped as the biquad was
            tG = g / (1.0 + g); tH = A; tL = 1.0 / A;
        }

        public void Apply(int n)
        {
            if (n <= 0) { G = tG; mH = tH; mL = tL; left = 0; return; }
            dG = (tG - G) / n; dH = (tH - mH) / n; dL = (tL - mL) / n;
            left = n;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Run2(ref double l, ref double r)
        {
            if (left > 0)
            {
                if (--left == 0) { G = tG; mH = tH; mL = tL; }
                else { G += dG; mH += dH; mL += dL; }
            }
            double v = (l - sL) * G, lp = v + sL; sL = lp + v; l = mH * (l - lp) + mL * lp;
            v = (r - sR) * G; lp = v + sR; sR = lp + v; r = mH * (r - lp) + mL * lp;
        }
    }

    // ── Interpolated peak search (limiter detector and true-peak meter) ───────
    // Each channel keeps a mirrored history: every sample is written twice, T
    // apart, so the last T samples are always contiguous (oldest first). The
    // coefficients are stored reversed to match, and each phase is one
    // contiguous dot product, four lanes at a time. Phase 0 falls exactly on a
    // sample, so its value is that sample: no arithmetic needed.
    internal sealed class Mirror
    {
        public readonly int T;
        public readonly float[] L, R;
        public int Pos;

        public Mirror(int taps) { T = taps; L = new float[2 * taps]; R = new float[2 * taps]; }

        public void Reset() { Array.Clear(L); Array.Clear(R); Pos = 0; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Push(float l, float r)
        {
            Pos = Pos + 1 == T ? 0 : Pos + 1;
            L[Pos] = L[Pos + T] = l;
            R[Pos] = R[Pos + T] = r;
        }

        public int WinOffset => Pos + 1;            // [0] oldest … [T-1] newest
    }

    internal static class Interp
    {
        // True-peak meter (BS.1770): 4 phases, 12-tap Blackman-windowed sinc.
        public const int TP_PHASES = 4, TP_TAPS = 12;
        // Limiter detector: 8 phases, 32-tap Kaiser-windowed sinc (beta 8).
        public const int DET_PHASES = 8, DET_TAPS = 32, DET_HALF = DET_TAPS / 2;
        const double DET_BETA = 8.0;

        public static readonly float[] TpRev  = new float[TP_PHASES * TP_TAPS];    // reversed, row-major [phase][tap]
        public static readonly float[] DetRev = new float[DET_PHASES * DET_TAPS];

        // Transposed for the all-phases-at-once evaluation: [tap][phase], reversed taps,
        // phase 0 replaced by an exact unit tap on the sample it falls on.
        public static readonly float[] TpT  = new float[TP_TAPS * TP_PHASES];
        public static readonly float[] DetT = new float[DET_TAPS * DET_PHASES];
        // Largest Σ|coefficient| over the phases (with a rounding margin): the
        // interpolated peak can never exceed this times the window's largest sample.
        public static readonly float TpBound, DetBound;

        static Interp()
        {
            var tp = new float[TP_PHASES, TP_TAPS];
            for (int ph = 0; ph < TP_PHASES; ph++)
            {
                double f = (double)ph / TP_PHASES, sum = 0;
                var c = new double[TP_TAPS];
                for (int j = 0; j < TP_TAPS; j++)
                {
                    double dd = (TP_TAPS / 2) - j - f;
                    double sinc = Math.Abs(dd) < 1e-12 ? 1.0 : Math.Sin(Math.PI * dd) / (Math.PI * dd);
                    double w = Math.Abs(dd) >= TP_TAPS / 2 ? 0.0
                             : 0.42 + 0.5 * Math.Cos(Math.PI * dd / (TP_TAPS / 2)) + 0.08 * Math.Cos(2 * Math.PI * dd / (TP_TAPS / 2));
                    c[j] = sinc * w;
                    sum += c[j];
                }
                for (int j = 0; j < TP_TAPS; j++) tp[ph, j] = (float)(c[j] / sum);
            }

            var det = new float[DET_PHASES, DET_TAPS];
            double i0b = BesselI0(DET_BETA);
            for (int ph = 0; ph < DET_PHASES; ph++)
            {
                double f = (double)ph / DET_PHASES, sum = 0;
                var c = new double[DET_TAPS];
                for (int j = 0; j < DET_TAPS; j++)
                {
                    double dd = DET_HALF - j - f;
                    double sinc = Math.Abs(dd) < 1e-12 ? 1.0 : Math.Sin(Math.PI * dd) / (Math.PI * dd);
                    double r = dd / DET_HALF;
                    double w = Math.Abs(r) >= 1.0 ? 0.0 : BesselI0(DET_BETA * Math.Sqrt(1.0 - r * r)) / i0b;
                    c[j] = sinc * w;
                    sum += c[j];
                }
                for (int j = 0; j < DET_TAPS; j++) det[ph, j] = (float)(c[j] / sum);
            }

            for (int ph = 0; ph < TP_PHASES; ph++)
                for (int k = 0; k < TP_TAPS; k++) TpRev[ph * TP_TAPS + k] = tp[ph, TP_TAPS - 1 - k];
            for (int ph = 0; ph < DET_PHASES; ph++)
                for (int k = 0; k < DET_TAPS; k++) DetRev[ph * DET_TAPS + k] = det[ph, DET_TAPS - 1 - k];

            TpBound  = Transpose(TpRev,  TpT,  TP_TAPS,  TP_PHASES,  TP_TAPS / 2);
            DetBound = Transpose(DetRev, DetT, DET_TAPS, DET_PHASES, DET_HALF);
            DetFarBound = FarBound();
            double tw = 0;
            for (int ph = 1; ph < TP_PHASES; ph++)
            {
                double far = 0;
                for (int k = 0; k < TP_TAPS; k++)
                    if (k < TP_NEAR_LO || k >= TP_NEAR_HI) far += Math.Abs(TpT[k * TP_PHASES + ph]);
                if (far > tw) tw = far;
            }
            TpFarBound = (float)(tw * 1.0001);
        }

        static float Transpose(float[] rev, float[] tr, int taps, int phases, int H)
        {
            double bound = 1.0;
            for (int ph = 0; ph < phases; ph++)
            {
                double sum = 0;
                for (int k = 0; k < taps; k++)
                {
                    float c = ph == 0 ? (k == taps - 1 - H ? 1.0f : 0.0f) : rev[ph * taps + k];
                    tr[k * phases + ph] = c;
                    sum += Math.Abs(c);
                }
                if (sum > bound) bound = sum;
            }
            return (float)(bound * 1.0001);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static float HMax(Vector128<float> m)
        {
            m = Vector128.Max(m, Vector128.Shuffle(m, Vector128.Create(2, 3, 0, 1)));
            m = Vector128.Max(m, Vector128.Shuffle(m, Vector128.Create(1, 0, 3, 2)));
            return m.ToScalar();
        }

        // Largest |sample| in a window of `taps` floats (taps a multiple of 4).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float MaxAbs(float[] a, int off, int taps)
        {
            ref float p = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(a), off);
            if ((taps & 7) == 0 && Vector256.IsHardwareAccelerated)
            {
                Vector256<float> w = Vector256.Abs(Vector256.LoadUnsafe(ref p));
                for (int k = 8; k < taps; k += 8) w = Vector256.Max(w, Vector256.Abs(Vector256.LoadUnsafe(ref p, (nuint)k)));
                return HMax(Vector128.Max(w.GetLower(), w.GetUpper()));
            }
            Vector128<float> m = Vector128.Abs(Vector128.LoadUnsafe(ref p));
            for (int k = 4; k < taps; k += 4) m = Vector128.Max(m, Vector128.Abs(Vector128.LoadUnsafe(ref p, (nuint)k)));
            return HMax(m);
        }

        // Limiter detector: the peak over all 8 phases of both channels' windows.
        // One broadcast multiply-add per tap evaluates all 8 phases at once.
        public static void DetPeak(float[] l, float[] r, int off, out float pkL, out float pkR)
        {
            ref float wl = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(l), off);
            ref float wr = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(r), off);
            ref float t = ref MemoryMarshal.GetArrayDataReference(DetT);
            if (Vector256.IsHardwareAccelerated)
            {
                Vector256<float> aL0 = Vector256<float>.Zero, aL1 = aL0, aR0 = aL0, aR1 = aL0;
                for (int k = 0; k < DET_TAPS; k += 2)
                {
                    var c0 = Vector256.LoadUnsafe(ref t, (nuint)(k * 8));
                    var c1 = Vector256.LoadUnsafe(ref t, (nuint)(k * 8 + 8));
                    aL0 += Vector256.Create(Unsafe.Add(ref wl, k)) * c0;
                    aR0 += Vector256.Create(Unsafe.Add(ref wr, k)) * c0;
                    aL1 += Vector256.Create(Unsafe.Add(ref wl, k + 1)) * c1;
                    aR1 += Vector256.Create(Unsafe.Add(ref wr, k + 1)) * c1;
                }
                var mL = Vector256.Abs(aL0 + aL1); var mR = Vector256.Abs(aR0 + aR1);
                pkL = HMax(Vector128.Max(mL.GetLower(), mL.GetUpper()));
                pkR = HMax(Vector128.Max(mR.GetLower(), mR.GetUpper()));
            }
            else
            {
                Vector128<float> l0 = Vector128<float>.Zero, l1 = l0, r0 = l0, r1 = l0;
                for (int k = 0; k < DET_TAPS; k++)
                {
                    var c0 = Vector128.LoadUnsafe(ref t, (nuint)(k * 8));
                    var c1 = Vector128.LoadUnsafe(ref t, (nuint)(k * 8 + 4));
                    var xl = Vector128.Create(Unsafe.Add(ref wl, k)); var xr = Vector128.Create(Unsafe.Add(ref wr, k));
                    l0 += xl * c0; l1 += xl * c1; r0 += xr * c0; r1 += xr * c1;
                }
                pkL = HMax(Vector128.Max(Vector128.Abs(l0), Vector128.Abs(l1)));
                pkR = HMax(Vector128.Max(Vector128.Abs(r0), Vector128.Abs(r1)));
            }
        }

        // Tiered limiter detector. The plain bound (window max × DetBound, +7.6 dB)
        // is too loose for a loud master, so before the full search: the 16 central
        // taps exactly, for all phases and both channels, plus the rest bounded by
        // DetFarBound × the window max (0.061 of it). If that stays below the
        // ceiling the attenuation is exactly 1 and the bound is returned; otherwise
        // the outer 16 taps are added to the same sums for the exact peak.
        public const int NEAR_LO = 8, NEAR_HI = 24;            // taps [8, 24) are "near"
        public static readonly float DetFarBound;              // set in the static constructor, after DetT
        public const int TP_NEAR_LO = 3, TP_NEAR_HI = 9;       // meter: taps [3, 9) are "near"
        public static readonly float TpFarBound;

        static float FarBound()
        {
            double worst = 0;
            for (int ph = 1; ph < DET_PHASES; ph++)
            {
                double far = 0;
                for (int k = 0; k < DET_TAPS; k++)
                    if (k < NEAR_LO || k >= NEAR_HI) far += Math.Abs(DetT[k * DET_PHASES + ph]);
                if (far > worst) worst = far;
            }
            return (float)(worst * 1.0001);
        }

        public static float DetPeakTiered(float[] l, float[] r, int off, float ceil, float windowMax)
        {
            ref float wl = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(l), off);
            ref float wr = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(r), off);
            ref float t = ref MemoryMarshal.GetArrayDataReference(DetT);
            if (Vector256.IsHardwareAccelerated)
            {
                Vector256<float> aL0 = Vector256<float>.Zero, aL1 = aL0, aR0 = aL0, aR1 = aL0;
                for (int k = NEAR_LO; k < NEAR_HI; k += 2)
                {
                    var c0 = Vector256.LoadUnsafe(ref t, (nuint)(k * 8));
                    var c1 = Vector256.LoadUnsafe(ref t, (nuint)(k * 8 + 8));
                    aL0 += Vector256.Create(Unsafe.Add(ref wl, k)) * c0;
                    aR0 += Vector256.Create(Unsafe.Add(ref wr, k)) * c0;
                    aL1 += Vector256.Create(Unsafe.Add(ref wl, k + 1)) * c1;
                    aR1 += Vector256.Create(Unsafe.Add(ref wr, k + 1)) * c1;
                }
                var m = Vector256.Max(Vector256.Abs(aL0 + aL1), Vector256.Abs(aR0 + aR1));
                float near = HMax(Vector128.Max(m.GetLower(), m.GetUpper()));
                float bound = (near + DetFarBound * windowMax) * 1.00001f;
                if (bound < ceil) return bound;
                for (int k = 0; k < DET_TAPS; k += 2)
                {
                    if (k == NEAR_LO) k = NEAR_HI;
                    var c0 = Vector256.LoadUnsafe(ref t, (nuint)(k * 8));
                    var c1 = Vector256.LoadUnsafe(ref t, (nuint)(k * 8 + 8));
                    aL0 += Vector256.Create(Unsafe.Add(ref wl, k)) * c0;
                    aR0 += Vector256.Create(Unsafe.Add(ref wr, k)) * c0;
                    aL1 += Vector256.Create(Unsafe.Add(ref wl, k + 1)) * c1;
                    aR1 += Vector256.Create(Unsafe.Add(ref wr, k + 1)) * c1;
                }
                m = Vector256.Max(Vector256.Abs(aL0 + aL1), Vector256.Abs(aR0 + aR1));
                return HMax(Vector128.Max(m.GetLower(), m.GetUpper()));
            }
            else
            {
                Vector128<float> l0 = Vector128<float>.Zero, l1 = l0, r0 = l0, r1 = l0;
                for (int k = NEAR_LO; k < NEAR_HI; k++)
                {
                    var c0 = Vector128.LoadUnsafe(ref t, (nuint)(k * 8));
                    var c1 = Vector128.LoadUnsafe(ref t, (nuint)(k * 8 + 4));
                    var xl = Vector128.Create(Unsafe.Add(ref wl, k)); var xr = Vector128.Create(Unsafe.Add(ref wr, k));
                    l0 += xl * c0; l1 += xl * c1; r0 += xr * c0; r1 += xr * c1;
                }
                float near = HMax(Vector128.Max(Vector128.Max(Vector128.Abs(l0), Vector128.Abs(l1)),
                                                Vector128.Max(Vector128.Abs(r0), Vector128.Abs(r1))));
                float bound = (near + DetFarBound * windowMax) * 1.00001f;
                if (bound < ceil) return bound;
                for (int k = 0; k < DET_TAPS; k++)
                {
                    if (k == NEAR_LO) k = NEAR_HI;
                    var c0 = Vector128.LoadUnsafe(ref t, (nuint)(k * 8));
                    var c1 = Vector128.LoadUnsafe(ref t, (nuint)(k * 8 + 4));
                    var xl = Vector128.Create(Unsafe.Add(ref wl, k)); var xr = Vector128.Create(Unsafe.Add(ref wr, k));
                    l0 += xl * c0; l1 += xl * c1; r0 += xr * c0; r1 += xr * c1;
                }
                return HMax(Vector128.Max(Vector128.Max(Vector128.Abs(l0), Vector128.Abs(l1)),
                                          Vector128.Max(Vector128.Abs(r0), Vector128.Abs(r1))));
            }
        }

        // True-peak meter, tiered like the detector: the 6 central taps exactly, the
        // rest bounded by TpFarBound × the window max (0.05 of it). Returns the
        // window's true peak if it can exceed `floor`, else 0 (no new peak).
        public static float TpPeakTiered(float[] h, int off, float floor, float windowMax)
        {
            ref float w = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(h), off);
            ref float t = ref MemoryMarshal.GetArrayDataReference(TpT);
            Vector128<float> a0 = Vector128<float>.Zero, a1 = a0;
            for (int k = TP_NEAR_LO; k < TP_NEAR_HI; k += 2)
            {
                a0 += Vector128.Create(Unsafe.Add(ref w, k)) * Vector128.LoadUnsafe(ref t, (nuint)(k * 4));
                a1 += Vector128.Create(Unsafe.Add(ref w, k + 1)) * Vector128.LoadUnsafe(ref t, (nuint)(k * 4 + 4));
            }
            float near = HMax(Vector128.Abs(a0 + a1));
            if ((near + TpFarBound * windowMax) * 1.00001f <= floor) return 0.0f;
            for (int k = 0; k < TP_NEAR_LO; k++)                        // the outer taps
                a0 += Vector128.Create(Unsafe.Add(ref w, k)) * Vector128.LoadUnsafe(ref t, (nuint)(k * 4));
            for (int k = TP_NEAR_HI; k < TP_TAPS; k++)
                a1 += Vector128.Create(Unsafe.Add(ref w, k)) * Vector128.LoadUnsafe(ref t, (nuint)(k * 4));
            return HMax(Vector128.Abs(a0 + a1));
        }

        // True-peak meter: the peak over the 4 phases of one channel's window.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float TpPeak(float[] h, int off)
        {
            ref float w = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(h), off);
            ref float t = ref MemoryMarshal.GetArrayDataReference(TpT);
            Vector128<float> a0 = Vector128<float>.Zero, a1 = a0;
            for (int k = 0; k < TP_TAPS; k += 2)
            {
                a0 += Vector128.Create(Unsafe.Add(ref w, k)) * Vector128.LoadUnsafe(ref t, (nuint)(k * 4));
                a1 += Vector128.Create(Unsafe.Add(ref w, k + 1)) * Vector128.LoadUnsafe(ref t, (nuint)(k * 4 + 4));
            }
            return HMax(Vector128.Abs(a0 + a1));
        }

        static double BesselI0(double x)
        {
            double sum = 1, term = 1;
            for (int k = 1; k < 40; k++) { term *= (x / (2 * k)) * (x / (2 * k)); sum += term; }
            return sum;
        }

        // Four-lane dot product, summed in the same order as the C++ SSE code:
        // per-lane running sums, then (lane0 + lane2) + (lane1 + lane3).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static float Dot(ref float a, ref float w, int taps)
        {
            Vector128<float> acc = Vector128<float>.Zero;
            for (int k = 0; k < taps; k += 4)
                acc = acc + Vector128.LoadUnsafe(ref a, (nuint)k) * Vector128.LoadUnsafe(ref w, (nuint)k);
            return (acc.GetElement(0) + acc.GetElement(2)) + (acc.GetElement(1) + acc.GetElement(3));
        }

        // Peak over the interval [age H, age H-1) of one channel's window: phase 0
        // is the sample at age H itself, phases 1..P-1 are interpolated.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float WindowPeak(float[] hist, int off, float[] rc, int taps, int phases, int H)
        {
            float peak = MathF.Abs(hist[off + taps - 1 - H]);
            ref float a = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(hist), off);
            ref float w0 = ref MemoryMarshal.GetArrayDataReference(rc);
            for (int ph = 1; ph < phases; ph++)
            {
                float v = MathF.Abs(Dot(ref a, ref Unsafe.Add(ref w0, ph * taps), taps));
                if (v > peak) peak = v;
            }
            return peak;
        }
    }
}
