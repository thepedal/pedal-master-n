// usage: harness in.f32 out.f32 meters.txt sr block P=V ... [@frame:P=V ...] [--sleep]
// Interleaved float32 stereo in and out (±32768). Mirrors tests/harness.cpp of
// the native machine: parameters by index, slot-1 meter reads about every 33 ms.
// --sleep runs the machine's silent-input sleep logic (PedalMasterN.cs) as well.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using PedalMasterN;

static class Program
{
    static string G(float v) => v.ToString("G9", CultureInfo.InvariantCulture);

    static int Main(string[] argv)
    {
        var bytes = File.ReadAllBytes(argv[0]);
        float[] x = MemoryMarshal.Cast<byte, float>(bytes).ToArray();
        int sr = int.Parse(argv[3]), blk = int.Parse(argv[4]);
        var e = new MasterEngine();
        var later = new List<(int at, int p, int v)>();
        bool sleep = false;
        for (int a = 5; a < argv.Length; a++)
        {
            string s = argv[a]; int at = -1;
            if (s == "--sleep") { sleep = true; continue; }
            if (s[0] == '@') { int c = s.IndexOf(':'); at = int.Parse(s.Substring(1, c - 1)); s = s.Substring(c + 1); }
            int eq = s.IndexOf('=');
            int p = int.Parse(s.Substring(0, eq)), v = int.Parse(s.Substring(eq + 1));
            if (at < 0) e.V[p] = v; else later.Add((at, p, v));
        }
        var sleeper = new SilenceSleep();
        var f = new MeterFrame();
        var sb = new StringBuilder();
        int frames = x.Length / 2;
        int every = (int)(0.033 * sr / blk + 1);
        for (int i = 0; i < frames; i += blk)
        {
            foreach (var c in later) if (c.at >= i && c.at < i + blk) e.V[c.p] = c.v;
            int n = frames - i < blk ? frames - i : blk;
            var io = x.AsSpan(2 * i, 2 * n);
            if (sleep) sleeper.Work(e, io, n, sr, true);
            else e.Work(io, n, sr, true);
            if ((i / blk) % every == 0 || i + blk >= frames)
            {
                e.ReadMeters(1, f);
                sb.Append("R ").Append(i).Append(' ').Append(2).Append(' ').Append(f.SampleRate).Append(' ').Append(f.Latency);
                foreach (float v in new[] { f.InPkL, f.InPkR, f.InMsL, f.InMsR, f.OutTpL, f.OutTpR, f.OutMsL, f.OutMsR, f.CompGr, f.LimGr, f.MatchDb })
                    sb.Append(' ').Append(G(v));
                sb.Append(' ').Append(f.BlockCount).Append(' ').Append(f.Dropped);
                for (int k = 0; k < f.BlockCount; k++) sb.Append(" b").Append(G(f.Blocks[k]));
                sb.Append(" c").Append(G(f.LL)).Append(" c").Append(G(f.LR)).Append(" c").Append(G(f.RR));
                sb.Append('\n');
            }
        }
        File.WriteAllText(argv[2], sb.ToString());
        File.WriteAllBytes(argv[1], MemoryMarshal.Cast<float, byte>(x.AsSpan()).ToArray());
        Console.WriteLine("latency " + e.Latency);
        return 0;
    }
}
