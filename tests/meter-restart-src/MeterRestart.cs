using System; using PedalMasterN;
// Metering switches off when nobody reads it and back on when a display opens.
// Check that the first readings after it comes back are sane: level, true peak,
// and loudness blocks of a -23 LUFS-ish 1 kHz sine, and that Match still works.
static class C {
  static void Main() {
    int sr = 48000, n = 256; var e = new MasterEngine(); var f = new MeterFrame(); var io = new float[2 * n];
    double ph = 0; float amp = (float)(32768 * Math.Pow(10, -20.0 / 20));          // -20 dBFS sine
    void Block() { for (int i = 0; i < n; i++) { float v = amp * (float)Math.Sin(ph); ph += 2 * Math.PI * 1000 / sr; io[2*i] = v; io[2*i+1] = v; } e.Work(io, n, sr, true); }
    for (int i = 0; i < 4 * sr / n; i++) Block();                    // 4 s, nobody reading: metering off
    Console.WriteLine($"after 4 s unread, first read (GUI discards it): ok={e.ReadMeters(0, f)}");
    double lufsSum = 0; int blocks = 0; float tp = 0, pk = 0;
    for (int i = 0; i < 3 * sr / n; i++) {                           // 3 s with a display reading every ~32 ms
      Block();
      if (i % 6 == 5) { e.ReadMeters(0, f); tp = Math.Max(tp, f.OutTpL); pk = Math.Max(pk, f.InPkL);
                        for (int b = 0; b < f.BlockCount; b++) { lufsSum += f.Blocks[b]; blocks++; } }
    }
    double lufs = -0.691 + 10 * Math.Log10(lufsSum / blocks);
    Console.WriteLine($"input peak {20*Math.Log10(pk):F2} dBFS (expect -20.00), output true peak {20*Math.Log10(tp):F2} dBTP (expect ~-20), loudness {lufs:F2} LUFS over {blocks} blocks (expect -20.0: a -20 dBFS sine in both channels)");
    // Match on after a stretch without metering: the estimate starts fresh.
    var e2 = new MasterEngine(); e2.V[MasterEngine.P_LIMGAIN] = 120; e = e2;
    for (int i = 0; i < 2 * sr / n; i++) Block();
    e2.V[MasterEngine.P_MATCH] = 1;
    for (int i = 0; i < 3 * sr / n; i++) Block();
    Console.WriteLine($"Match switched on without any display: match estimate {e2.MatchDb:F2} LU (expect +12: Lim Gain +12 dB into a -20 dBFS sine, below the ceiling)");
  }
}
