using HarmonyLib;
using PunkMultiverse.Core;

namespace PunkMultiverse.Patches
{
    /// <summary>
    /// In a net run, entity instanceIds are a deterministic INCREMENTING counter instead of the
    /// game's <c>rnd.Next()</c>. The game's version is seeded off the run seed (so it's already
    /// deterministic across machines), but it's random-VALUED — a ~1%/run chance of two entities
    /// drawing the same id. A counter is collision-free and, because generation creates entities in
    /// the same order on every machine, produces identical ids everywhere — which is exactly what
    /// makes it a clean cross-machine identity (see <see cref="Core.NetIds"/>).
    ///
    /// Safe as a drop-in: the game only uses instanceId as a unique lookup handle. The counter is
    /// reset at the START of level generation (LevelGenerator.GenerateLevel) — which runs AFTER
    /// the ships/puppets are spawned but BEFORE any world entity is created. That decoupling is
    /// what makes it robust: generation-time entities always start from 1 regardless of how many
    /// ships preceded them, so a rejoiner or a different player count can't shift every id (the
    /// thing the old position fingerprint was immune to). Single-player is left entirely alone.
    /// </summary>
    internal static class DeterministicIds
    {
        private static int _counter;

        [HarmonyPatch(typeof(LevelGenerator), "GenerateLevel", typeof(LevelGenerationContext), typeof(int))]
        internal static class ResetCounter
        {
            private static void Prefix() => _counter = 0; // after ships, before any world entity
        }

        [HarmonyPatch(typeof(EntityManager), "CreateInstanceId")]
        internal static class CounterInstanceId
        {
            private static bool Prefix(ref int __result)
            {
                if (!NetSession.Active) return true; // single-player: keep the game's rnd
                __result = ++_counter;
                return false;
            }
        }
    }
}
