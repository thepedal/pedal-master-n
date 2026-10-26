// Pedal Master N — pieces shared by the parameter panel and the meter window:
// the meter link to the machine, the meter scale and the colours.
// ReBuzz v1.0.0: ported from the Buzz 1503 GUI (v0.4.0) to .NET 10; the meter
//         link is a direct call on the machine instead of a GUI message.
// v0.4.0: Low Dip and Low Shape rows in the EQ section (protocol unchanged).
// v0.3.0: protocol v2 (match gain); MATCH button and status; Slope and Dither
//         rows; meter window "→ T" (Lim Gain to the loudness target) and PLR.
// v0.2.1: panel faders drag relative to the grab point (no jump on click),
//         "TP" marks the true-peak OUT rows, tighter spacing above the loudness line.

using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using BuzzGUI.Interfaces;

namespace PedalMasterN
{
    // ══════════════════════════════════════════════════════════════════════════
    // MeterLink — reads one meter slot of the machine (MeterFrame, MasterEngine.cs).
    // The GUI lives in the machine's own DLL, so this is a direct call on the
    // machine object; it replaces the native build's GUI message (protocol v2)
    // and fills the same values. Every value covers the time since that slot
    // was last read. The output values measure the master: after the limiter,
    // before Bypass, Match and Dither.
    // ══════════════════════════════════════════════════════════════════════════
    public sealed class MeterLink
    {
        readonly int slot;
        public readonly MeterFrame Frame = new MeterFrame();

        public MeterLink(int slot) { this.slot = slot; }

        // true = Frame holds a fresh reading. Never throws.
        public bool Poll(IMachine machine)
        {
            try
            {
                var m = machine?.ManagedMachine as PedalMasterNMachine;
                return m != null && m.ReadMeters(slot, Frame);
            }
            catch { return false; }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Level meter ballistics (IEC 60268-18 style), shared by panel and window.
    // ══════════════════════════════════════════════════════════════════════════
    public sealed class LevelBallistics
    {
        public const double DB_MIN        = -60.0;
        const double FALL_DB_PER_S = 20.0 / 1.7;
        const double HOLD_SECONDS  = 3.0;
        const double RMS_TAU       = 0.300;
        const double CLIP_SECONDS  = 2.0;

        public double PeakDb = DB_MIN, HoldDb = DB_MIN, MeanSq;
        double holdAge, clipAge;
        public bool Clipped;

        public void Reset() { HoldDb = DB_MIN; holdAge = 0; clipAge = 0; Clipped = false; }

        public void Update(float peakLin, float meanSq, double dt)
        {
            double pk   = peakLin > 1e-6f ? 20.0 * Math.Log10(peakLin) : DB_MIN;
            double fall = FALL_DB_PER_S * dt;

            PeakDb = Math.Max(pk, Math.Max(PeakDb - fall, DB_MIN));
            if (pk >= HoldDb) { HoldDb = pk; holdAge = 0; }
            else if ((holdAge += dt) > HOLD_SECONDS) HoldDb = Math.Max(HoldDb - fall, DB_MIN);

            if (peakLin >= 1.0f) { Clipped = true; clipAge = 0; }
            else if (Clipped && (clipAge += dt) > CLIP_SECONDS) Clipped = false;

            double a = 1.0 - Math.Exp(-dt / RMS_TAU);
            MeanSq += (meanSq - MeanSq) * a;
            if (MeanSq < 1e-12) MeanSq = 0;
        }

        public double RmsDb => MeanSq > 1e-12 ? 10.0 * Math.Log10(MeanSq) : DB_MIN;

        // What the meters show, rounded to what can be seen (for change detection).
        public int Shown => HashCode.Combine(Theme.Q(PeakDb), Theme.Q(HoldDb), Theme.Q(RmsDb), Clipped);
    }

    // Gain-reduction meter: follows the machine's max-since-last-read directly
    // (it is already an envelope), with a 3 s hold of the deepest reduction.
    public sealed class GrBallistics
    {
        const double HOLD_SECONDS = 3.0, FALL_DB_PER_S = 20.0;
        public double Db, HoldDb;
        double holdAge;

        public void Reset() { HoldDb = 0; holdAge = 0; }

        public void Update(float grDb, double dt)
        {
            Db = grDb > 0 ? grDb : 0;
            if (Db >= HoldDb) { HoldDb = Db; holdAge = 0; }
            else if ((holdAge += dt) > HOLD_SECONDS) HoldDb = Math.Max(Db, HoldDb - FALL_DB_PER_S * dt);
        }

        public int Shown => HashCode.Combine(Theme.Q(Db), Theme.Q(HoldDb));
    }

    // ══════════════════════════════════════════════════════════════════════════
    // MeterScale — the Pedal Gain Multi N scale shape and zone colours.
    // ══════════════════════════════════════════════════════════════════════════
    public static class MeterScale
    {
        static readonly double[] Db = { -60, -50,  -40,  -30,  -24,  -18,  -12,   -9,   -6,   -3,    0,   3 };
        static readonly double[] Fr = { 0.0, 0.06, 0.14, 0.25, 0.33, 0.43, 0.56, 0.64, 0.73, 0.84, 0.95, 1.0 };

        public static double Frac(double db)
        {
            if (db <= Db[0]) return 0;
            if (db >= Db[Db.Length - 1]) return 1;
            for (int i = 1; i < Db.Length; i++)
                if (db <= Db[i])
                    return Fr[i - 1] + (db - Db[i - 1]) / (Db[i] - Db[i - 1]) * (Fr[i] - Fr[i - 1]);
            return 1;
        }

        public static double PanelFrac(double db) => Math.Min(1.0, Frac(db) / Frac(0));

        // Gain reduction scale: 0 .. GR_MAX dB, linear.
        public const double GR_MAX = 24.0;
        public static double GrFrac(double gr) => Math.Max(0, Math.Min(1, gr / GR_MAX));

        public static readonly Color Green  = Color.FromRgb( 60, 200,  90);
        public static readonly Color Yellow = Color.FromRgb(230, 200,  50);
        public static readonly Color Red    = Color.FromRgb(235,  60,  45);
        public const double YellowFromDb = -12, RedFromDb = -3;

        public static LinearGradientBrush ZoneBrush(Point from, Point to, Func<double, double> frac, byte alpha = 255)
        {
            var b = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = from, EndPoint = to };
            double fy = frac(YellowFromDb), fr = frac(RedFromDb);
            Color g = Color.FromArgb(alpha, Green.R,  Green.G,  Green.B);
            Color y = Color.FromArgb(alpha, Yellow.R, Yellow.G, Yellow.B);
            Color r = Color.FromArgb(alpha, Red.R,    Red.G,    Red.B);
            b.GradientStops.Add(new GradientStop(g, 0));
            b.GradientStops.Add(new GradientStop(g, fy));
            b.GradientStops.Add(new GradientStop(y, fy));
            b.GradientStops.Add(new GradientStop(y, fr));
            b.GradientStops.Add(new GradientStop(r, fr));
            b.GradientStops.Add(new GradientStop(r, 1));
            b.Freeze();
            return b;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Theme — frozen brushes, pens and text helpers.
    // ══════════════════════════════════════════════════════════════════════════
    public static class Theme
    {
        public static readonly Brush Background = B(24, 24, 28);
        public static readonly Brush Panel      = B(30, 30, 36);
        public static readonly Brush Track      = B(40, 40, 46);
        public static readonly Brush TextBrush  = B(200, 200, 205);
        public static readonly Brush Dim        = B(125, 125, 135);
        public static readonly Brush Faint      = B(78, 78, 88);
        public static readonly Brush Heading    = B(150, 150, 165);
        public static readonly Brush Fader      = B(70, 130, 200);
        public static readonly Brush FaderOff   = B(70, 80, 95);
        public static readonly Brush Gr         = B(235, 150, 45);
        public static readonly Brush GrDim      = A(110, 235, 150, 45);
        public static readonly Brush Amber      = B(235, 180, 60);
        public static readonly Brush ClipOn     = B(235, 45, 35);
        public static readonly Brush ClipOff    = B(46, 46, 52);
        public static readonly Brush Hold       = B(255, 255, 255);
        public static readonly Brush ButtonBg   = B(52, 52, 58);
        public static readonly Brush ButtonEdge = B(90, 90, 98);
        public static readonly Brush OnBg       = B(60, 170, 110);    // section switched on
        public static readonly Brush OnFg       = B(20, 20, 25);
        public static readonly Brush BypassBg   = B(235, 185, 40);    // bypass engaged
        public static readonly Brush MatchBg    = B(80, 170, 220);    // level-matched A/B engaged
        public static readonly Brush HeadroomTint = A(34, 235, 60, 45);
        public static readonly Brush Under      = B(70, 140, 210);
        public static readonly Brush Over       = B(235, 170, 50);
        public static readonly Brush CorrPos    = B(60, 190, 90);
        public static readonly Brush CorrNeg    = B(225, 60, 45);

        public static readonly Pen Tick     = P(A(48, 255, 255, 255));
        public static readonly Pen Zero     = P(A(160, 255, 90, 80));
        public static readonly Pen Divider  = P(A(70, 255, 255, 255));
        public static readonly Pen ClipEdge = P(A(90, 235, 60, 45));

        public static readonly FontFamily MonoFamily = new FontFamily("Consolas");
        public static readonly Typeface Mono     = new Typeface(MonoFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        public static readonly Typeface MonoBold = new Typeface(MonoFamily, FontStyles.Normal, FontWeights.Bold,   FontStretches.Normal);

        static Brush B(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }
        static Brush A(byte a, byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromArgb(a, r, g, b)); x.Freeze(); return x; }
        static Pen P(Brush b) { var p = new Pen(b, 1); p.Freeze(); return p; }

        // Building a FormattedText is expensive, and the meters draw dozens per frame,
        // most of them the same strings again (scale labels, readouts that hold still).
        // They are cached by content; UI thread only.
        readonly struct TextKey : IEquatable<TextKey>
        {
            readonly string s; readonly double size, ppd; readonly Brush brush; readonly bool bold;
            public TextKey(string s, double size, Brush brush, bool bold, double ppd)
            { this.s = s; this.size = size; this.brush = brush; this.bold = bold; this.ppd = ppd; }
            public bool Equals(TextKey o) => s == o.s && size == o.size && ReferenceEquals(brush, o.brush) && bold == o.bold && ppd == o.ppd;
            public override bool Equals(object o) => o is TextKey k && Equals(k);
            public override int GetHashCode() => HashCode.Combine(s, size, brush, bold, ppd);
        }
        static readonly System.Collections.Generic.Dictionary<TextKey, FormattedText> textCache =
            new System.Collections.Generic.Dictionary<TextKey, FormattedText>();

        public static FormattedText Text(Visual v, string s, double size, Brush brush, bool bold = false)
        {
            double ppd = 1.0;
            try { ppd = VisualTreeHelper.GetDpi(v).PixelsPerDip; } catch { }
            s ??= "";
            var key = new TextKey(s, size, brush, bold, ppd);
            if (textCache.TryGetValue(key, out var ft)) return ft;
            if (textCache.Count >= 4096) textCache.Clear();
            ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                   bold ? MonoBold : Mono, size, brush, ppd);
            textCache[key] = ft;
            return ft;
        }

        // Rounds a value to what the meters can show, for change detection.
        public static int Q(double v, double step = 0.1) =>
            double.IsNaN(v) ? int.MinValue : double.IsInfinity(v) ? (v > 0 ? int.MaxValue : int.MinValue + 1)
            : (int)Math.Round(Math.Max(-1e6, Math.Min(1e6, v)) / step);

        public static string Db(double db) =>
            db <= LevelBallistics.DB_MIN + 0.5 ? "-∞"
            : (db > 0.05 ? "+" : "") + db.ToString("F1", CultureInfo.InvariantCulture);

        public static string F1(double v) => double.IsNaN(v) || double.IsInfinity(v) ? "--"
            : (v > 0.05 ? "+" : "") + v.ToString("F1", CultureInfo.InvariantCulture);
    }
}
