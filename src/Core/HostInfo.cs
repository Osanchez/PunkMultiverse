using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

namespace PunkMultiverse.Core
{
    /// <summary>
    /// Read the HOST's vital signs from inside the Wine container (`hostinfo [secs]` devcmd).
    ///
    /// Why this exists: the ~480ms whole-process freezes on the dedicated server were proven
    /// host-side by elimination — identical image + identical load in a local WSL container runs
    /// 88-93fps with zero stalls while the remote box stalls 3x/3s, and the freeze stops even the
    /// Highest-priority relay thread, so it is not our process's doing. What remains is to prove
    /// WHAT the host is doing. Three suspects, each with a distinct fingerprint:
    ///
    ///   * CPU steal  — the host is itself a VM being descheduled by its hypervisor. Fingerprint:
    ///     the `steal` field of /proc/stat's cpu line jumps during the freeze.
    ///   * cgroup throttling — the container has a CPU quota despite the panel saying cpu=0.
    ///     Fingerprint: nr_throttled/throttled_usec climb in /sys/fs/cgroup/cpu.stat.
    ///   * contention — co-tenant containers saturate the box. Fingerprint: /proc/pressure/cpu
    ///     "some total" climbs while steal and throttle stay flat.
    ///
    /// The trick that makes this possible with no image rebuild and no extra ports: Wine maps the
    /// Linux root at Z:\, so the Windows game process can read the container's /proc and cgroup
    /// files directly. (A native-Linux future server reads the same files at /proc.)
    ///
    /// The sampler is itself an instrument: it runs on its own thread sampling every ~250ms, so a
    /// sample interval that STRETCHES past ~400ms means the freeze caught the sampler too — and
    /// the /proc deltas across exactly that stretched interval say what the host was doing during
    /// the freeze. Stretched intervals are logged individually for correlation with [Hitch].
    /// </summary>
    internal static class HostInfo
    {
        private static Thread _thread;
        private static volatile bool _running;

        private static string _statPath, _psiCpuPath, _cgroupCpuPath, _loadPath;

        /// <summary>Locate the proc/cgroup files through whichever root works here.</summary>
        private static bool ResolvePaths(out string error)
        {
            error = null;
            foreach (string root in new[] { @"Z:\", "/" })
            {
                string stat = root == "/" ? "/proc/stat" : root + @"proc\stat";
                try
                {
                    if (!File.Exists(stat)) continue;
                    _statPath = stat;
                    string sep = root == "/" ? "/" : @"\";
                    string p = root == "/" ? "/proc" : root + "proc";
                    string sys = root == "/" ? "/sys" : root + "sys";
                    _psiCpuPath = p + sep + "pressure" + sep + "cpu";
                    _cgroupCpuPath = sys + sep + "fs" + sep + "cgroup" + sep + "cpu.stat";
                    _loadPath = p + sep + "loadavg";
                    return true;
                }
                catch { }
            }
            error = "no readable /proc (checked Z:\\proc\\stat and /proc/stat) — Wine Z: mapping absent?";
            return false;
        }

        internal static string Start(float seconds)
        {
            if (_running) return "already sampling";
            if (!ResolvePaths(out string err)) return err;
            _running = true;
            float secs = Math.Max(5f, Math.Min(120f, seconds));
            _thread = new Thread(() => Sample(secs)) { IsBackground = true, Name = "PunkMV-HostInfo" };
            _thread.Start();
            return $"sampling host vitals for {secs:0}s via {_statPath} (results -> [HostInfo])";
        }

        // /proc/stat cpu line: user nice system idle iowait irq softirq steal guest guest_nice
        private static bool ReadCpu(out long total, out long steal, out long idle, out long iowait)
        {
            total = steal = idle = iowait = 0;
            try
            {
                using (var r = new StreamReader(_statPath))
                {
                    string line = r.ReadLine();
                    if (line == null || !line.StartsWith("cpu ", StringComparison.Ordinal)) return false;
                    var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    for (int i = 1; i < parts.Length && i <= 10; i++)
                    {
                        if (!long.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out long v)) return false;
                        total += v;
                        if (i == 4) idle = v;
                        if (i == 5) iowait = v;
                        if (i == 8) steal = v;
                    }
                    return true;
                }
            }
            catch { return false; }
        }

        /// <summary>"some avg10=.. avg60=.. avg300=.. total=N" (µs stalled since boot).</summary>
        private static long ReadPsiTotal(string path, string prefix)
        {
            try
            {
                foreach (string line in File.ReadAllLines(path))
                {
                    if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    int at = line.LastIndexOf("total=", StringComparison.Ordinal);
                    if (at >= 0 && long.TryParse(line.Substring(at + 6).Trim(), out long v)) return v;
                }
            }
            catch { }
            return -1;
        }

        private static void ReadCgroup(out long nrThrottled, out long throttledUsec)
        {
            nrThrottled = throttledUsec = -1;
            try
            {
                foreach (string line in File.ReadAllLines(_cgroupCpuPath))
                {
                    if (line.StartsWith("nr_throttled ", StringComparison.Ordinal))
                        long.TryParse(line.Substring(13).Trim(), out nrThrottled);
                    else if (line.StartsWith("throttled_usec ", StringComparison.Ordinal))
                        long.TryParse(line.Substring(15).Trim(), out throttledUsec);
                }
            }
            catch { }
        }

        private static void Sample(float seconds)
        {
            try
            {
                double toMs = 1000.0 / Stopwatch.Frequency;
                bool haveCpu = ReadCpu(out long pTotal, out long pSteal, out long pIdle, out long pIowait);
                long pPsiSome = ReadPsiTotal(_psiCpuPath, "some");
                long pPsiFull = ReadPsiTotal(_psiCpuPath, "full");
                ReadCgroup(out long pThr, out long pThrUs);
                long t0 = Stopwatch.GetTimestamp(), prevT = t0;

                long sTotal0 = pTotal, sSteal0 = pSteal, sIdle0 = pIdle, sIowait0 = pIowait;
                long sPsiSome0 = pPsiSome, sPsiFull0 = pPsiFull, sThr0 = pThr, sThrUs0 = pThrUs;
                int stretched = 0, samples = 0;
                double worstMs = 0;

                while (_running && (Stopwatch.GetTimestamp() - t0) * toMs < seconds * 1000.0)
                {
                    Thread.Sleep(250);
                    long now = Stopwatch.GetTimestamp();
                    double wallMs = (now - prevT) * toMs;
                    prevT = now;
                    samples++;
                    if (wallMs > worstMs) worstMs = wallMs;

                    bool ok = ReadCpu(out long total, out long steal, out long idle, out long iowait);
                    long psiSome = ReadPsiTotal(_psiCpuPath, "some");
                    ReadCgroup(out long thr, out long thrUs);

                    // A stretched interval means the freeze caught THIS thread. Log what the host
                    // was doing across exactly that window.
                    if (wallMs > 400.0)
                    {
                        stretched++;
                        long dTotal = ok && haveCpu ? total - pTotal : 0;
                        long dSteal = ok && haveCpu ? steal - pSteal : 0;
                        long dIdle = ok && haveCpu ? idle - pIdle : 0;
                        long dIowait = ok && haveCpu ? iowait - pIowait : 0;
                        long dPsi = psiSome >= 0 && pPsiSome >= 0 ? psiSome - pPsiSome : -1;
                        long dThr = thr >= 0 && pThr >= 0 ? thr - pThr : -1;
                        long dThrUs = thrUs >= 0 && pThrUs >= 0 ? thrUs - pThrUs : -1;
                        Plugin.Log.LogWarning(string.Format(CultureInfo.InvariantCulture,
                            "[HostInfo] FREEZE CAUGHT wall={0:0}ms | host cpu jiffies: total={1} steal={2} " +
                            "idle={3} iowait={4} | psiSomeDelta={5}us | throttleEvents={6} throttledDelta={7}us",
                            wallMs, dTotal, dSteal, dIdle, dIowait, dPsi, dThr, dThrUs));
                    }
                    if (ok) { pTotal = total; pSteal = steal; pIdle = idle; pIowait = iowait; haveCpu = true; }
                    if (psiSome >= 0) pPsiSome = psiSome;
                    if (thr >= 0) { pThr = thr; pThrUs = thrUs; }
                }

                // Window summary.
                ReadCpu(out long eTotal, out long eSteal, out long eIdle, out long eIowait);
                long ePsiSome = ReadPsiTotal(_psiCpuPath, "some");
                long ePsiFull = ReadPsiTotal(_psiCpuPath, "full");
                ReadCgroup(out long eThr, out long eThrUs);
                string load = "?";
                try { load = File.ReadAllText(_loadPath).Trim(); } catch { }
                double wall = (Stopwatch.GetTimestamp() - t0) * toMs;
                long dT = eTotal - sTotal0;
                Plugin.Log.LogInfo(string.Format(CultureInfo.InvariantCulture,
                    "[HostInfo] === {0:0.0}s, {1} samples, {2} stretched (worst {3:0}ms) === " +
                    "steal={4:0.00}% idle={5:0.0}% iowait={6:0.00}% | psiCpuSome={7}us psiCpuFull={8}us | " +
                    "throttleEvents={9} throttledUs={10} | loadavg: {11}",
                    wall / 1000.0, samples, stretched, worstMs,
                    dT > 0 ? 100.0 * (eSteal - sSteal0) / dT : -1,
                    dT > 0 ? 100.0 * (eIdle - sIdle0) / dT : -1,
                    dT > 0 ? 100.0 * (eIowait - sIowait0) / dT : -1,
                    ePsiSome >= 0 && sPsiSome0 >= 0 ? ePsiSome - sPsiSome0 : -1,
                    ePsiFull >= 0 && sPsiFull0 >= 0 ? ePsiFull - sPsiFull0 : -1,
                    eThr >= 0 && sThr0 >= 0 ? eThr - sThr0 : -1,
                    eThrUs >= 0 && sThrUs0 >= 0 ? eThrUs - sThrUs0 : -1, load));
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[HostInfo] sampler failed: {e.Message}"); }
            finally { _running = false; }
        }
    }
}
