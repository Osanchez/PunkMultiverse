using HarmonyLib;
using PunkMultiverse.Core;
using PunkMultiverse.Sync;

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

                // Per-player instanced loot: every machine drops its own copy (each player collects
                // only their own). De-dup so a re-applied / zombie death can't drop twice HERE.
                if (!EnemySync.TryMarkLootDropped(netId))
                {
                    if (NetDiag.Enabled)
                        NetDiag.Log("Loot", $"{NetDiag.Describe(netId)} drop SUPPRESSED — already dropped here (duplicate death)");
                    return false;
                }

                if (NetDiag.Enabled)
                    NetDiag.Log("Loot", $"{NetDiag.Describe(netId)} dropped loot (instanced, one per player)");
                return true;
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
