using HarmonyLib;
using PunkMultiverse.Core;
using PunkMultiverse.Sync;

namespace PunkMultiverse.Patches
{
    /// <summary>
    /// Loot / resource-drop diagnostics (gated behind [Diag] SyncDiagnostics — F10). Two hooks
    /// aimed squarely at the "way more gold than vanilla" duplication:
    ///
    ///  - <see cref="LootDropper.DropLoot"/> — every time an entity drops loot, tagged with WHY:
    ///    a LOCAL kill on this machine, or a REMOTE kill we're replaying. The game re-runs the
    ///    whole death (including the loot drop) on every machine that receives the kill, so an
    ///    enemy/box killed near both players drops resources on BOTH. Any "REMOTE kill" drop line
    ///    is a duplicate this machine should not have produced.
    ///
    ///  - <see cref="Vault.Add"/> (the Ingredient overload) — every resource actually banked, with
    ///    the running total, so the gold rate is visible and correlates with the drop lines.
    /// </summary>
    internal static class LootDiag
    {
        [HarmonyPatch(typeof(LootDropper), "DropLoot")]
        internal static class DropLootDiag
        {
            private static void Prefix(LootDropper __instance)
            {
                if (!NetDiag.Enabled) return;
                // EnemySync flag covers entity kills; DamageSync flag covers ship-routed deaths.
                bool remote = EnemySync.SuppressLocalDeathEffects || DamageSync.IsApplyingRemote;
                string who = EnemySync.TryGetNetId(__instance, out int netId)
                    ? NetDiag.Describe(netId)
                    : (__instance != null ? __instance.gameObject.name : "?");
                NetDiag.Log("Loot", remote
                    ? $"{who} dropped loot — RECEIVED from another client (REMOTE kill → DUPLICATE drop here)"
                    : $"{who} dropped loot — this client's own kill");
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
