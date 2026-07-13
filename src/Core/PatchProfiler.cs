using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace PunkMultiverse.Core
{
    internal enum PatchId
    {
        WorldCaptureCellChanges,
        WorldSuppressNetCellCascade,
        ProjectileCaptureFirePrefix,
        ProjectileCaptureFirePostfix,
        ProjectileMarkVisual,
        ProjectileMarkVisualPhysics,
        ProjectileSuppressDamage,
        ProjectileSuppressHitscanDamage,
        DamageRouteSingle,
        DamageRouteList,
        DamageDropWorldRemote,
        EnemyOnEntitySpawned,
        Count,
    }

    internal readonly struct PatchProfileToken
    {
        internal readonly long Started;
        internal readonly int PreviousPhase;

        internal PatchProfileToken(long started, int previousPhase)
        {
            Started = started;
            PreviousPhase = previousPhase;
        }
    }

    /// <summary>Allocation-free aggregate timing for Harmony bodies that execute outside the
    /// NetSession tick profiler.</summary>
    internal static class PatchProfiler
    {
        private static readonly long[] Calls = new long[(int)PatchId.Count];
        private static readonly long[] SumTicks = new long[(int)PatchId.Count];
        private static readonly long[] MaxTicks = new long[(int)PatchId.Count];
        private static readonly string[] Names =
        {
            "World.CaptureCell", "World.SuppressCascade", "Projectile.Fire.Pre",
            "Projectile.Fire.Post", "Projectile.MarkVisual", "Projectile.MarkPhysics",
            "Projectile.Damage", "Projectile.Hitscan", "Damage.Route", "Damage.RouteList",
            "Damage.DropWorld", "Enemy.Spawn",
        };

        private static PatchId _spikeId;
        private static long _spikeTicks;

        internal static bool Enabled => NetProfiler.Enabled;

        internal static PatchProfileToken Enter(PatchId id)
        {
            if (!Enabled) return default;
            var session = NetSession.Instance;
            if (session == null || (session.State != SessionState.InGame && session.State != SessionState.Loading))
                return default;
            int previous = RuntimeInstrumentation.EnterPatchPhase(id);
            return new PatchProfileToken(Stopwatch.GetTimestamp(), previous);
        }

        internal static void Exit(PatchId id, PatchProfileToken token)
        {
            if (token.Started == 0) return;
            long elapsed = Stopwatch.GetTimestamp() - token.Started;
            int i = (int)id;
            Calls[i]++;
            SumTicks[i] += elapsed;
            if (elapsed > MaxTicks[i]) MaxTicks[i] = elapsed;
            if (elapsed > _spikeTicks)
            {
                _spikeTicks = elapsed;
                _spikeId = id;
            }
            RuntimeInstrumentation.ExitPatchPhase(token.PreviousPhase);
        }

        internal static void Reset()
        {
            Array.Clear(Calls, 0, Calls.Length);
            Array.Clear(SumTicks, 0, SumTicks.Length);
            Array.Clear(MaxTicks, 0, MaxTicks.Length);
            _spikeTicks = 0;
        }

        internal static void EndFrame(double monoSeconds)
        {
            long spike = _spikeTicks;
            if (spike <= 0) return;
            _spikeTicks = 0;
            double ms = TicksToMs(spike);
            if (ms < 10.0) return;
            Plugin.Log.LogWarning(string.Format(CultureInfo.InvariantCulture,
                "[PatchProfile] SPIKE mono={0:0.000}s patch={1} call={2:0.0}ms",
                monoSeconds, Names[(int)_spikeId], ms));
        }

        internal static void Report(double monoSeconds, double elapsedSeconds)
        {
            if (elapsedSeconds <= 0) return;
            var order = new int[(int)PatchId.Count];
            int active = 0;
            for (int i = 0; i < order.Length; i++)
                if (Calls[i] > 0 || MaxTicks[i] > 0) order[active++] = i;
            Array.Sort(order, 0, active, Comparer.Instance);

            var sb = new StringBuilder(512);
            sb.AppendFormat(CultureInfo.InvariantCulture, "[PatchProfile] mono={0:0.000}s", monoSeconds);
            for (int n = 0; n < active; n++)
            {
                int i = order[n];
                sb.Append(n == 0 ? " " : "; ");
                sb.Append(Names[i]);
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    " calls={0:0.#}/s total={1:0.###}ms/s max={2:0.###}ms",
                    Calls[i] / elapsedSeconds,
                    TicksToMs(SumTicks[i]) / elapsedSeconds,
                    TicksToMs(MaxTicks[i]));
                if (sb.Length > 7600)
                {
                    sb.AppendFormat(CultureInfo.InvariantCulture, "; omitted={0}", active - n - 1);
                    break;
                }
            }
            if (active == 0) sb.Append(" idle");
            Plugin.Log.LogInfo(sb.ToString());

            Array.Clear(Calls, 0, Calls.Length);
            Array.Clear(SumTicks, 0, SumTicks.Length);
            Array.Clear(MaxTicks, 0, MaxTicks.Length);
        }

        private static double TicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        private sealed class Comparer : System.Collections.Generic.IComparer<int>
        {
            internal static readonly Comparer Instance = new Comparer();
            public int Compare(int x, int y) => SumTicks[y].CompareTo(SumTicks[x]);
        }
    }
}
