using HarmonyLib;
using PunkMultiverse.Core;

namespace PunkMultiverse.Patches
{
    /// <summary>
    /// EXPERIMENT (`nostream on|off`, default OFF): stop the dedicated coordinator instantiating
    /// entity GameObjects for streamed-in segments.
    ///
    /// The causal chain this tests, measured end to end today:
    ///     live entity objects on the server -> per-frame cost + allocation -> GC pauses of
    ///     300-550ms -> ship state delivered in clumps with ~400ms silences -> the playout buffer
    ///     demanding ~600ms and saturating its cap -> the delay and skipping felt in PvP.
    /// Every link is measured EXCEPT the last inference: that REDUCING the object count actually
    /// reduces the churn. That is correlation, not causation, which is why this is an experiment
    /// with a switch rather than a change with a rationale.
    ///
    /// What is blocked: only <c>InstantiateGameObjects</c>, the bulk segment-streaming path — the
    /// game builds every segment within 3 of a ship and instantiates everything inside it, which is
    /// how a shipless-but-puppet-carrying coordinator ended up with 170 live objects while holding
    /// authority over 2. Runtime spawns (<c>CreateEntity</c> -> <c>SpawnObjectForEntity</c>: care
    /// packages, minions, boss adds) are deliberately NOT blocked; those are entities the server or
    /// a client is creating on purpose and they must still appear.
    ///
    /// KNOWN RISK, to be watched rather than assumed away: when a lease makes the coordinator the
    /// simulator for a segment it needs real objects to simulate. Blanket-blocking measures the
    /// CEILING of the win and will show that breakage if it exists — which is the point of running
    /// it as an instrumented experiment. Correctness to watch in the same session: kills
    /// registering, lease handoff, terrain damage, the BR ring, death replication.
    /// </summary>
    internal static class NoStreamOnServer
    {
        internal static bool Enabled { get; private set; }

        internal static string Toggle(string arg)
        {
            Enabled = string.Equals(arg, "on", System.StringComparison.OrdinalIgnoreCase);
            Blocked = 0;
            return Enabled
                ? "ON — coordinator will not instantiate streamed segment entities"
                : "off — vanilla streaming (default)";
        }

        /// <summary>Segments whose instantiation was skipped, so the log can say how much was
        /// actually avoided rather than leaving it to inference.</summary>
        internal static long Blocked;

        [HarmonyPatch(typeof(EntityGameObjectManager), "InstantiateGameObjects")]
        internal static class SkipSegmentInstantiation
        {
            private static bool Prefix()
            {
                if (!Enabled || !NetConfig.IsCoordinator) return true;
                Blocked++;
                return false;
            }
        }
    }
}
