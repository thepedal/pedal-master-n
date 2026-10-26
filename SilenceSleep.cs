// Pedal Master N — sleep on silent input (pure C#, no host types).
//
// ReBuzz keeps calling an effect's Work() with WM_READWRITE even when nothing
// upstream is playing, so silence has to be detected from the input itself
// (Core §33). Once the input has been digital silence for longer than one
// second plus the latency, every tail (delay lines, filters, envelopes) has
// died away and the output is silent too: the engine then stops running and
// only keeps the meters moving (MasterEngine.Idle). The first non-silent
// input wakes it; glides land straight on their targets (MasterEngine.Wake).
//
// Return value, for the host: true = take the output buffer. In ReBuzz a
// managed effect that returns false leaves its *input* in the host buffer
// and that is passed on, so false is returned only when the input is silent
// as well (at most 0.5, about -96 dBFS) — never while the look-ahead is still
// holding back a sound that has just started.
//
// MIT License — Copyright (c) 2026 thepedal. See LICENSE.

using System;

namespace PedalMasterN
{
    public sealed class SilenceSleep
    {
        const float SILENT_IN = 1e-3f;     // about -150 dBFS: inaudible even after +48 dB of gain
        const float AUDIBLE = 0.5f;        // the engine's own audibility threshold

        long silentRun;
        bool lastAudible;
        public bool Sleeping { get; private set; }

        public bool Work(MasterEngine e, Span<float> io, int n, int rate, bool read)
        {
            float inMax = 0;
            if (read)
            {
                var s = io.Slice(0, 2 * n);
                for (int i = 0; i < s.Length; i++)
                {
                    float a = MathF.Abs(s[i]);
                    if (a > inMax) inMax = a;
                }
            }
            bool silentIn = !read || inMax < SILENT_IN;
            silentRun = silentIn ? Math.Min(silentRun + n, long.MaxValue / 2) : 0;

            int sr = e.SampleRate;
            if (silentIn && !lastAudible && sr > 0 && (rate <= 0 || rate == sr) && silentRun > sr + e.Latency)
            {
                Sleeping = true;
                e.Idle(n);
                io.Slice(0, 2 * n).Clear();
                return false;
            }
            if (Sleeping) { Sleeping = false; e.Wake(); }

            lastAudible = e.Work(io, n, rate, read);
            return lastAudible || inMax > AUDIBLE;
        }
    }
}
