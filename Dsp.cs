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
