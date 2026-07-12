using HarmonyLib;
using PunkMultiverse.Core;
using PunkMultiverse.Sync;
using UnityEngine;

namespace PunkMultiverse.Patches
{
    /// <summary>
    /// Loot / resource-drop routing + diagnostics. The game re-runs the whole death (including
    /// <see cref="LootDropper.DropLoot"/>) every time a kill is applied — and a kill is applied on
    /// EVERY machine that sees the death (the local kill, a broadcast kill, and zombie re-kills on
    /// stream-in all funnel through Die()). Left alone, that re-drops resources several times over
    /// on a single machine — the "way more gold than vanilla" inflation.
    ///
    /// MODEL: loot is INSTANCED per player. Every machine drops its OWN copy so each player gets
    /// exactly one — the wallet (Vault) is per-player and never synced, so one player collecting
    /// can't inflate another's, and each player sees only their own local drop. We do NOT route to
    /// the killer (that starved every non-killer: a container the host broke dropped nothing for
    /// the client, and vice-versa). The only thing we suppress is the DUPLICATE: per-machine
    /// de-dup (<see cref="EnemySync.TryMarkLootDropped"/>) so a re-applied / zombie death drops at
    /// most once on the machine it happens on.
    ///
    /// Diagnostics (gated behind [Diag] SyncDiagnostics — F10): drops, duplicate suppressions, and
    /// every <see cref="Vault.Add"/> with the running total.
    /// </summary>
    internal static class LootDiag
    {
        [HarmonyPatch(typeof(LootDropper), "DropLoot")]
        internal static class DropLootGuard
        {
            private static bool Prefix(LootDropper __instance)
            {
                var session = NetSession.Instance;
                if (session == null || !NetSession.Active) return true; // single-player: unchanged

                // No netId (unsynced orphan): each machine handles its own copy locally — allow.
                if (!EnemySync.TryGetNetId(__instance, out int netId)) return true;

                // Instanced, but only where a player can actually reach it. The killer always gets
                // their copy; so does any player standing near the death. A teammate across the
                // (loaded) map does NOT spawn a copy they'll never collect — those uncollected
                // pickups piled up and tanked the frame rate on distant clients. Checked BEFORE the
                // de-dup so a far death doesn't burn the one-drop-per-machine slot.
                byte local = (byte)session.LocalSlot;
                byte killer = EnemySync.SuppressLocalDeathEffects
                    ? EnemySync.RemoteKillerSlot
                    : DamageSync.LastKiller(netId);
                if (killer != local && !NearLocalShip(__instance))
                {
                    // Throttled per netId: a corpse in a damage field re-runs Die() (and this
                    // suppression) every frame — unthrottled, that's 60 log lines/sec of one entity.
                    NetDiag.Throttled($"loot-far-{netId}", 10f, "Loot",
                        () => $"{NetDiag.Describe(netId)} drop SUPPRESSED — too far to collect (not my kill)");
                    NoteRepeat(netId, __instance);
                    return false;
                }

                // De-dup so a re-applied / zombie death can't drop twice HERE.
                if (!EnemySync.TryMarkLootDropped(netId))
                {
                    NetDiag.Throttled($"loot-dup-{netId}", 10f, "Loot",
                        () => $"{NetDiag.Describe(netId)} drop SUPPRESSED — already dropped here (duplicate death)");
                    NoteRepeat(netId, __instance);
                    return false;
                }

                if (NetDiag.Enabled)
                    NetDiag.Log("Loot", $"{NetDiag.Describe(netId)} dropped loot (instanced, one per nearby player)");
                return true;
            }

            // A drop is worth spawning here only if the local player is close enough to collect it.
            private const float LootReachRadius = 55f;

            private static bool NearLocalShip(Component c)
            {
                var ship = ShipSync.LocalShip;
                if (ship == null || c == null) return false;
                return Vector2.Distance(ship.transform.position, c.transform.position) <= LootReachRadius;
            }

            // A single entity re-entering DropLoot dozens of times = something re-runs Die() on a
            // corpse every frame (observed live: #3535 Unit_Grunt, 60/s, severe client frame drops
            // — Die() re-fires the whole onDeath chain each call; vanilla has no already-dead
            // guard). Log ONE stack trace for the first offender so the caller is identified from
            // a normal playtest log without a debugger.
            private static readonly System.Collections.Generic.Dictionary<int, int> RepeatCounts
                = new System.Collections.Generic.Dictionary<int, int>();
            private static bool _dumpedRepeatStack;

            private static void NoteRepeat(int netId, Component c)
            {
                RepeatCounts.TryGetValue(netId, out int n);
                RepeatCounts[netId] = ++n;
                if (n != 30 || _dumpedRepeatStack) return;
                _dumpedRepeatStack = true;
                Plugin.Log.LogWarning($"[Loot] {NetDiag.Describe(netId)} hit DropLoot 30x — death loop. Caller:\n{new System.Diagnostics.StackTrace(2, false)}");
            }
        }

        [HarmonyPatch(typeof(Vault), "Add", typeof(Ingredient), typeof(int))]
        internal static class VaultAddDiag
        {
            private static void Postfix(Vault __instance, Ingredient __0, int __1)
            {
                if (!NetDiag.Enabled) return;
                int total = 0;
                try { total = __instance.AmountOf(__0); } catch { }
                string id = __0 != null ? __0.id : "?";
                NetDiag.Log("Loot", $"vault +{__1} {id} (now {total})");
            }
        }
    }
}
