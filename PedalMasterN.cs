// Pedal Master N — mastering machine for ReBuzz (managed effect, .NET 10).
//
// Stereo in → stereo out; put it last in the chain, just before Master.
// Port of the Buzz 1503 native machine "Pedal Master N" v0.4.0 (C++). The DSP
// lives in MasterEngine.cs and produces the same samples as the native build;
// this file is the ReBuzz side: parameters, Work, value texts, latency,
// About, and the meter link the GUI reads.
//
//   In → DC blocker → Input → EQ → Glue compressor → Stereo (Low Mono, Width)
//      → True-peak limiter → [master meters] → Match / Bypass → Dither → Out
//
// Latency: 2 ms of look-ahead plus 15 samples of detector delay (111 samples
// at 48 kHz, 103 at 44.1 kHz), constant, reported through GetLatency() for
// ReBuzz's machine delay compensation.
//
// v1.0.0 — first ReBuzz release (= native v0.4.0, all 28 parameters in the same
//          order with the same ranges and defaults). Panel and meter window
//          ported to WPF on .NET 10 inside the machine DLL; the meter link is
//          a direct call instead of a GUI message; sleeps on silent input;
//          About entry in the right-click menu.
//
// MIT License — Copyright (c) 2026 thepedal. See LICENSE.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using E = PedalMasterN.MasterEngine;

namespace PedalMasterN
{
    [MachineDecl(Name = "Pedal Master N", ShortName = "PMasterN", Author = "thepedal")]
    public class PedalMasterNMachine : IBuzzMachine
    {
        internal const string Version = "1.0.0";

        readonly IBuzzMachineHost host;
        readonly MasterEngine engine = new MasterEngine();
        readonly SilenceSleep sleeper = new SilenceSleep();
        int[] V => engine.V;

        public PedalMasterNMachine(IBuzzMachineHost host)
        {
            this.host = host;
        }

        // ── Parameters ───────────────────────────────────────────────────────
        // Order, ranges and defaults are those of the native machine (index =
        // MasterEngine.P_*). New parameters must be appended at the end.

        [ParameterDecl(Name = "Input", Description = "Input trim, -24 to +24 dB (240 = 0 dB, 0.1 dB steps)",
                       MinValue = 0, MaxValue = 480, DefValue = 240)]
        public int Input { get => V[E.P_INPUT]; set => V[E.P_INPUT] = value; }

        [ParameterDecl(Name = "EQ", Description = "EQ section on or off", DefValue = true)]
        public bool EQ { get => V[E.P_EQ] != 0; set => V[E.P_EQ] = value ? 1 : 0; }

        [ParameterDecl(Name = "Low Cut", Description = "Low cut: 0 = Off, 1 to 100 = 10 to 250 Hz (slope: Slope)",
                       MinValue = 0, MaxValue = 100, DefValue = 0)]
        public int LowCut { get => V[E.P_LOWCUT]; set => V[E.P_LOWCUT] = value; }

        [ParameterDecl(Name = "Low Freq", Description = "Low shelf frequency, 25 to 400 Hz (50 = 100 Hz)",
                       MinValue = 0, MaxValue = 100, DefValue = 50)]
        public int LowFreq { get => V[E.P_LOWFREQ]; set => V[E.P_LOWFREQ] = value; }

        [ParameterDecl(Name = "Low Gain", Description = "Low shelf gain, -12 to +12 dB (120 = 0 dB)",
                       MinValue = 0, MaxValue = 240, DefValue = 120)]
        public int LowGain { get => V[E.P_LOWGAIN]; set => V[E.P_LOWGAIN] = value; }

        [ParameterDecl(Name = "High Freq", Description = "High shelf frequency, 1.25 to 20 kHz (75 = 10 kHz)",
                       MinValue = 0, MaxValue = 100, DefValue = 75)]
        public int HighFreq { get => V[E.P_HIGHFREQ]; set => V[E.P_HIGHFREQ] = value; }

        [ParameterDecl(Name = "High Gain", Description = "High shelf gain, -12 to +12 dB (120 = 0 dB)",
                       MinValue = 0, MaxValue = 240, DefValue = 120)]
        public int HighGain { get => V[E.P_HIGHGAIN]; set => V[E.P_HIGHGAIN] = value; }

        [ParameterDecl(Name = "Tilt", Description = "Tilt around 1 kHz, -6 to +6 dB end to end (60 = flat); +6 = lows -3 dB, highs +3 dB",
                       MinValue = 0, MaxValue = 120, DefValue = 60)]
        public int Tilt { get => V[E.P_TILT]; set => V[E.P_TILT] = value; }

        [ParameterDecl(Name = "Comp", Description = "Glue compressor on or off", DefValue = false)]
        public bool Comp { get => V[E.P_COMP] != 0; set => V[E.P_COMP] = value ? 1 : 0; }

        [ParameterDecl(Name = "Threshold", Description = "Compressor threshold, -40 to 0 dB (0.5 dB steps)",
                       MinValue = 0, MaxValue = 80, DefValue = 60)]
        public int Threshold { get => V[E.P_THRESH]; set => V[E.P_THRESH] = value; }

        [ParameterDecl(Name = "Ratio", Description = "Compressor ratio: 1.5, 2, 3, 4, 6 or 10 to 1",
                       MinValue = 0, MaxValue = 5, DefValue = 1)]
        public int Ratio { get => V[E.P_RATIO]; set => V[E.P_RATIO] = value; }

        [ParameterDecl(Name = "Attack", Description = "Compressor attack: 0.1, 0.3, 1, 3, 10 or 30 ms",
                       MinValue = 0, MaxValue = 5, DefValue = 4)]
        public int Attack { get => V[E.P_ATTACK]; set => V[E.P_ATTACK] = value; }

        [ParameterDecl(Name = "Release", Description = "Compressor release: 0.1, 0.3, 0.6, 1.2 s or Auto",
                       MinValue = 0, MaxValue = 4, DefValue = 4)]
        public int Release { get => V[E.P_RELEASE]; set => V[E.P_RELEASE] = value; }

        [ParameterDecl(Name = "SC HPF", Description = "Sidechain high-pass: Off, 60, 90, 120 or 150 Hz",
                       MinValue = 0, MaxValue = 4, DefValue = 0)]
        public int ScHpf { get => V[E.P_SCHPF]; set => V[E.P_SCHPF] = value; }

        [ParameterDecl(Name = "Makeup", Description = "Compressor makeup gain, 0 to +20 dB",
                       MinValue = 0, MaxValue = 200, DefValue = 0)]
        public int Makeup { get => V[E.P_MAKEUP]; set => V[E.P_MAKEUP] = value; }

        [ParameterDecl(Name = "Comp Mix", Description = "Compressor dry and wet (parallel) mix, percent",
                       MinValue = 0, MaxValue = 100, DefValue = 100)]
        public int CompMix { get => V[E.P_MIX]; set => V[E.P_MIX] = value; }

        [ParameterDecl(Name = "Low Mono", Description = "Mono below: 0 = Off, 1 to 100 = 40 to 300 Hz",
                       MinValue = 0, MaxValue = 100, DefValue = 0)]
        public int LowMono { get => V[E.P_LOWMONO]; set => V[E.P_LOWMONO] = value; }

        [ParameterDecl(Name = "Width", Description = "Stereo width, percent (100 = unchanged, 0 = mono)",
                       MinValue = 0, MaxValue = 200, DefValue = 100)]
        public int Width { get => V[E.P_WIDTH]; set => V[E.P_WIDTH] = value; }

        [ParameterDecl(Name = "Limiter", Description = "True-peak limiter on or off", DefValue = true)]
        public bool Limiter { get => V[E.P_LIMITER] != 0; set => V[E.P_LIMITER] = value ? 1 : 0; }

        [ParameterDecl(Name = "Lim Gain", Description = "Gain into the limiter, 0 to +24 dB",
                       MinValue = 0, MaxValue = 240, DefValue = 0)]
        public int LimGain { get => V[E.P_LIMGAIN]; set => V[E.P_LIMGAIN] = value; }

        [ParameterDecl(Name = "Ceiling", Description = "Limiter ceiling, -6.0 to 0.0 dBTP (60 = 0 dBTP)",
                       MinValue = 0, MaxValue = 60, DefValue = 50)]
        public int Ceiling { get => V[E.P_CEILING]; set => V[E.P_CEILING] = value; }

        [ParameterDecl(Name = "Lim Release", Description = "Limiter release, 10 to 1000 ms",
                       MinValue = 0, MaxValue = 100, DefValue = 50)]
        public int LimRelease { get => V[E.P_LIMREL]; set => V[E.P_LIMREL] = value; }

        [ParameterDecl(Name = "Bypass", Description = "Bypass (latency-compensated, click-free)", DefValue = false)]
        public bool Bypass { get => V[E.P_BYPASS] != 0; set => V[E.P_BYPASS] = value ? 1 : 0; }

        [ParameterDecl(Name = "Slope", Description = "Low cut slope: 0 = 12 dB per octave, 1 = 24 dB per octave",
                       MinValue = 0, MaxValue = 1, DefValue = 1)]
        public int Slope { get => V[E.P_SLOPE]; set => V[E.P_SLOPE] = value; }

        [ParameterDecl(Name = "Match", Description = "Level-matched A and B: the louder of master and dry is turned down to match", DefValue = false)]
        public bool Match { get => V[E.P_MATCH] != 0; set => V[E.P_MATCH] = value ? 1 : 0; }

        [ParameterDecl(Name = "Dither", Description = "Final-render dither: Off, 24-bit, 16-bit, 16-bit shaped",
                       MinValue = 0, MaxValue = 3, DefValue = 0)]
        public int Dither { get => V[E.P_DITHER]; set => V[E.P_DITHER] = value; }

        [ParameterDecl(Name = "Low Dip", Description = "Broad cut at 3x Low Freq, 0 = Off to 6.0 dB; with a Low Gain boost = vintage boost-and-attenuate",
                       MinValue = 0, MaxValue = 60, DefValue = 0)]
        public int LowDip { get => V[E.P_LOWDIP]; set => V[E.P_LOWDIP] = value; }

        [ParameterDecl(Name = "Low Shape", Description = "Low shelf corner: 0 = Clean, 1 = Vintage (resonant)",
                       MinValue = 0, MaxValue = 1, DefValue = 0)]
        public int LowShape { get => V[E.P_LOWSHAPE]; set => V[E.P_LOWSHAPE] = value; }

        // ── Work ─────────────────────────────────────────────────────────────
        public bool Work(Sample[] output, Sample[] input, int n, WorkModes mode)
        {
            bool read = (mode & WorkModes.WM_READ) != 0;
            if (read) Array.Copy(input, output, n);
            Span<float> io = MemoryMarshal.Cast<Sample, float>(output.AsSpan(0, n));
            int rate = host.MasterInfo != null ? host.MasterInfo.SamplesPerSec : 0;
            return sleeper.Work(engine, io, n, rate, read);
        }

        // ── Latency (machine delay compensation) ─────────────────────────────
        public int GetLatency()
        {
            int l = engine.Latency;
            if (l > 0) return l;
            int rate = 0;
            try { rate = host.MasterInfo?.SamplesPerSec ?? 0; } catch { }
            if (rate <= 0) try { rate = host.Machine?.Graph?.Buzz?.SelectedAudioDriverSampleRate ?? 0; } catch { }
            return MasterEngine.LatencyFor(rate);
        }

        // ── Meter link (read by the GUI, any thread) ─────────────────────────
        // Slot 0 = the parameter panel, 1..3 = meter windows; each read resets
        // its own slot only, so readers never take each other's peaks.
        public bool ReadMeters(int slot, MeterFrame frame) => engine.ReadMeters(slot, frame);

        // ── Value texts ──────────────────────────────────────────────────────
        static readonly string[] Ratios  = { "1.5:1", "2:1", "3:1", "4:1", "6:1", "10:1" };
        static readonly string[] Attacks = { "0.1 ms", "0.3 ms", "1 ms", "3 ms", "10 ms", "30 ms" };
        static readonly string[] Rels    = { "0.1 s", "0.3 s", "0.6 s", "1.2 s", "Auto" };
        static readonly string[] ScHpfs  = { "Off", "60 Hz", "90 Hz", "120 Hz", "150 Hz" };
        static readonly string[] Dithers = { "Off", "24-bit", "16-bit", "16-bit shaped" };

        static string F(double v, string fmt) => v.ToString(fmt, CultureInfo.InvariantCulture);
        static string Pick(string[] a, int v) => v >= 0 && v < a.Length ? a[v] : null;

        // Three significant figures: 42.1 Hz, 101 Hz, 1.25 kHz, 10.0 kHz.
        static string Hz(double f) =>
              f >= 10000.0 ? F(f / 1000.0, "F1") + " kHz"
            : f >= 1000.0  ? F(f / 1000.0, "F2") + " kHz"
            : f >= 100.0   ? F(f, "F0") + " Hz"
            :                F(f, "F1") + " Hz";

        static string Db(float d, string unit) => (d >= 0 ? "+" : "-") + F(Math.Abs(d), "F1") + " " + unit;

        public string DescribeValue(IParameter parameter, int value)
        {
            switch (parameter?.Name)
            {
                case "EQ": case "Comp": case "Limiter": case "Bypass": case "Match":
                    return value != 0 ? "On" : "Off";
                case "Slope":       return value != 0 ? "24 dB/oct" : "12 dB/oct";
                case "Low Shape":   return value != 0 ? "Vintage" : "Clean";
                case "Low Dip":     return value <= 0 ? "Off" : "-" + F(E.DipDb(value), "F1") + " dB";
                case "Dither":      return Pick(Dithers, value);
                case "Input":       return Db(E.InputDb(value), "dB");
                case "Low Cut":     return value <= 0 ? "Off" : Hz(E.LowCutHz(value));
                case "Low Freq":    return Hz(E.LowShelfHz(value));
                case "Low Gain":    return Db(E.ShelfDb(value), "dB");
                case "High Freq":   return Hz(E.HighShelfHz(value));
                case "High Gain":   return Db(E.ShelfDb(value), "dB");
                case "Tilt":        return Db(E.TiltDb(value), "dB");
                case "Threshold":   return F(E.ThreshDb(value), "F1") + " dB";
                case "Ratio":       return Pick(Ratios, value);
                case "Attack":      return Pick(Attacks, value);
                case "Release":     return Pick(Rels, value);
                case "SC HPF":      return Pick(ScHpfs, value);
                case "Makeup":      return Db(E.MakeupDb(value), "dB");
                case "Comp Mix":    return value.ToString(CultureInfo.InvariantCulture) + "%";
                case "Low Mono":    return value <= 0 ? "Off" : Hz(E.LowMonoHz(value));
                case "Width":       return value == 0 ? "Mono" : value.ToString(CultureInfo.InvariantCulture) + "%";
                case "Lim Gain":    return Db(E.LimGainDb(value), "dB");
                case "Ceiling":     return F(E.CeilingDb(value), "F1") + " dBTP";
                case "Lim Release": return F(E.LimRelMs(value), "F0") + " ms";
            }
            return null;
        }

        // ── Right-click menu ─────────────────────────────────────────────────
        public IEnumerable<IMenuItem> Commands
        {
            get
            {
                yield return new MenuItemVM
                {
                    Text = "Meters...",
                    Command = new SimpleCommand
                    {
                        CanExecuteDelegate = p => true,
                        ExecuteDelegate = p => MeterWindow.ShowFor(host.Machine)
                    }
                };
                yield return new MenuItemVM
                {
                    Text = "About...",
                    Command = new SimpleCommand
                    {
                        CanExecuteDelegate = p => true,
                        ExecuteDelegate = p => MessageBox.Show(
                            "Pedal Master N   v" + Version + "\n\n" +
                            "Mastering machine: EQ with vintage low end, glue compressor,\n" +
                            "low mono and width, true-peak limiter, level-matched A/B,\n" +
                            "dither, and loudness metering (EBU R128, LRA).\n" +
                            "Put it last in the chain, just before Master.\n\n" +
                            "Latency " + GetLatency() + " samples, reported for delay compensation.\n\n" +
                            "By thepedal\n" +
                            "github.com/thepedal/pedal-master-n\n" +
                            "MIT License",
                            "About Pedal Master N")
                    }
                };
            }
        }
    }
}
