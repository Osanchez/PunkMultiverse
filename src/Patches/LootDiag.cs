using HarmonyLib;
using PunkMultiverse.Core;
using PunkMultiverse.Sync;

namespace PunkMultiverse.Patches
{
    /// <summary>
    /// Loot / resource-drop routing + diagnostics. The game re-runs the whole death (including
    /// <see cref="LootDropper.DropLoot"/>) every time a kill is applied, and it's applied on every
    /// machine that sees the death, so loot dropped everywhere — anyone standing nearby collected
    /// a free copy, and re-kills dropped it several times over. That was the "way more gold than
    /// vanilla" inflation (and the "my wallet grew when my teammate collected nearby" report).
    ///
    /// FIX: loot drops only on the KILLER's machine, at most once. The killer is the last player
    /// to damage the entity (DamageSync tracks it on the simulating machine; it travels in the
    /// kill's <c>KillerSlot</c>). So resources go to whoever earned the kill, wherever they are —
    /// not to bystanders, and not to the host just because it owns the sim. Per-machine de-dup
    /// (<see cref="EnemySync.TryMarkLootDropped"/>) additionally kills re-drop from zombie re-kills.
    ///
    /// Diagnostics (gated behind [Diag] SyncDiagnostics — F10): drops, suppressions (not-my-kill /
    /// duplicate), and every <see cref="Vault.Add"/> with the running total.
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
                bool hasId = EnemySync.TryGetNetId(__instance, out int netId);
                string who = hasId ? NetDiag.Describe(netId)
                    : (__instance != null ? __instance.gameObject.name : "?");

                // No netId (unsynced orphan): each machine handles its own copy locally — allow.
                if (!hasId) return true;

                // Loot belongs to whoever landed the kill. During a received kill that's the
                // broadcast KillerSlot; during our own local kill it's the last damager we tracked.
                byte local = (byte)session.LocalSlot;
                byte killer = EnemySync.SuppressLocalDeathEffects
                    ? EnemySync.RemoteKillerSlot
                    : DamageSync.LastKiller(netId);
                if (killer != 255 && killer != local)
                {
                    if (NetDiag.Enabled)
                        NetDiag.Log("Loot", $"{who} drop SUPPRESSED — {NetDiag.Owner(killer)} landed the kill, not me");
                    return false;
                }

                // De-dup: only the first drop for this entity on this machine is real.
                if (!EnemySync.TryMarkLootDropped(netId))
                {
                    if (NetDiag.Enabled)
                        NetDiag.Log("Loot", $"{who} drop SUPPRESSED — already dropped here once (duplicate death)");
                    return false;
                }

                if (NetDiag.Enabled)
                    NetDiag.Log("Loot", $"{who} dropped loot — I landed the kill");
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
