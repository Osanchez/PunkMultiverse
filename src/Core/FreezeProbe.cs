using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace PunkMultiverse.Core
{
    /// <summary>
    /// Fingerprint WHICH kinds of thread activity freeze together (`freezeprobe [secs]` devcmd).
    ///
    /// Why: the ~0.95s-beat stalls on the dedicated server hit the main thread (370-500ms frame
    /// spikes) and the state-relay/send path (matching send gaps) — but NOT the hostinfo sampler
    /// (1 stretched interval in 117 while 40+ stalls elapsed), and the host reports itself ~80%
    /// idle with steal=0 and throttle=0 during a caught stall. So this is not a whole-process
    /// pause, not the hypervisor, not a cgroup quota — it is selective by what the thread DOES.
    ///
    /// Four sentinels, one discriminating operation each, all measuring their own loop gaps with
    /// the monotonic Stopwatch clock:
    ///
    ///   spin  — pure userspace arithmetic, no syscalls at all. Stalls here mean the OS stopped
    ///           scheduling the process (VM pause / SIGSTOP / runqueue starvation) or a
    ///           stop-the-world suspended managed threads.
    ///   sleep — Thread.Sleep(5) loop: adds kernel timer delivery, still no Wine server traffic.
    ///   wsrv  — open/read/close Z:\proc\uptime each iteration: every open is a wineserver
    ///           round-trip, so this stalls if the single-threaded wineserver process is the
    ///           chokepoint.
    ///   sock  — UDP sendto loopback each iteration: the Wine socket path the game's transport
    ///           lives on.
    ///
    /// Each sentinel records its gaps >100ms with offsets from probe start; the report prints them
    /// alongside so they can be correlated with each other and with [Hitch] main-thread stamps
    /// (probe start is logged in Time.unscaledTime terms for that translation). The pattern of
    /// which columns stall together is the fingerprint that names the mechanism.
    /// </summary>
    internal static class FreezeProbe
    {
        private const int MaxGaps = 96;
        private const double GapThresholdMs = 100.0;

        private sealed class Sentinel
        {
            public string Name;
            public double[] OffsetMs = new double[MaxGaps];
            public double[] GapMs = new double[MaxGaps];
            public int Count;
            public long Iterations;

            public void Note(long startTicks, long prevTicks, long nowTicks, double toMs)
            {
                double gap = (nowTicks - prevTicks) * toMs;
                if (gap < GapThresholdMs) return;
                int i = Count;
                if (i >= MaxGaps) return;
                OffsetMs[i] = (prevTicks - startTicks) * toMs;
                GapMs[i] = gap;
                Count = i + 1;
            }
        }

        private static volatile bool _running;

        internal static string Start(float seconds)
        {
            if (_running) return "already running";
            _running = true;
            float secs = Math.Max(10f, Math.Min(120f, seconds));
            long start = Stopwatch.GetTimestamp();
            double toMs = 1000.0 / Stopwatch.Frequency;
            // Translation anchor: [Hitch]/[Frame] stamps are Time.unscaledTime ("mono="); the
            // sentinels can only use Stopwatch. Logged so offsets line up with the main log.
            Plugin.Log.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "[FreezeProbe] start monoAnchor={0:0.000}s window={1:0}s", UnityEngine.Time.unscaledTime, secs));

            var sentinels = new[]
            {
                new Sentinel { Name = "spin" },
                new Sentinel { Name = "sleep" },
                new Sentinel { Name = "wsrv" },
                new Sentinel { Name = "sock" },
            };

            StartThread(sentinels[0], start, toMs, secs, SpinBody);
            StartThread(sentinels[1], start, toMs, secs, s => Thread.Sleep(5));
            string uptime = File.Exists(@"Z:\proc\uptime") ? @"Z:\proc\uptime"
                : (File.Exists("/proc/uptime") ? "/proc/uptime" : null);
            StartThread(sentinels[2], start, toMs, secs, s =>
            {
                if (uptime != null) { try { using (var r = new StreamReader(uptime)) r.ReadLine(); } catch { } }
                Thread.Sleep(5);
            });
            var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            var sink = new IPEndPoint(IPAddress.Loopback, 9); // discard port; delivery irrelevant
            byte[] one = { 0x50 };
            StartThread(sentinels[3], start, toMs, secs, s =>
            {
                try { udp.SendTo(one, sink); } catch { }
                Thread.Sleep(5);
            });

            // Reporter: waits out the window off the main thread, then prints everything.
            new Thread(() =>
            {
                Thread.Sleep((int)(secs * 1000) + 500);
                _running = false;
                Thread.Sleep(300); // let sentinels wind down
                try { udp.Close(); } catch { }
                foreach (var s in sentinels)
                {
                    var sb = new System.Text.StringBuilder(256);
                    sb.AppendFormat(CultureInfo.InvariantCulture,
                        "[FreezeProbe] {0,-5} iters={1} gaps>{2:0}ms: {3}",
                        s.Name, s.Iterations, GapThresholdMs, s.Count);
                    int shown = Math.Min(s.Count, 24);
                    for (int i = 0; i < shown; i++)
                        sb.AppendFormat(CultureInfo.InvariantCulture, " +{0:0.00}s/{1:0}ms",
                            s.OffsetMs[i] / 1000.0, s.GapMs[i]);
                    if (s.Count > shown) sb.Append(" ...");
                    Plugin.Log.LogInfo(sb.ToString());
                }
                Plugin.Log.LogInfo("[FreezeProbe] === end ===");
            })
            { IsBackground = true, Name = "PunkMV-FreezeReport" }.Start();

            return $"4 sentinel threads running for {secs:0}s (spin/sleep/wsrv/sock) -> [FreezeProbe]";
        }

        private static void SpinBody(Sentinel s)
        {
            // Pure userspace: no syscalls, no allocation, nothing the kernel or wineserver can
            // block. Volatile read of _running is a plain memory load.
            long x = s.Iterations;
            for (int i = 0; i < 20000; i++) x = unchecked(x * 6364136223846793005L + 1442695040888963407L);
            if (x == long.MinValue) Plugin.Log.LogInfo("."); // defeat dead-code elimination
        }

        private static void StartThread(Sentinel s, long start, double toMs, float secs, Action<Sentinel> body)
        {
            new Thread(() =>
            {
                long prev = Stopwatch.GetTimestamp();
                double endMs = secs * 1000.0;
                while (_running)
                {
                    body(s);
                    long now = Stopwatch.GetTimestamp();
                    s.Note(start, prev, now, toMs);
                    prev = now;
                    s.Iterations++;
                    if ((now - start) * toMs > endMs) break;
                }
            })
            { IsBackground = true, Name = "PunkMV-Freeze-" + s.Name }.Start();
        }
    }
}
