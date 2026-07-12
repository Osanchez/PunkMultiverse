using HarmonyLib;
using PunkMultiverse.Core;
using PunkMultiverse.Sync;

namespace PunkMultiverse.Patches
{
    /// <summary>
    /// Loot / resource-drop fix + diagnostics. The game re-runs the whole death (including
    /// <see cref="LootDropper.DropLoot"/>) every time a kill is applied, and a single entity's
    /// kill can be applied more than once on a machine — the owner's own death, the broadcast
    /// kill, and zombie re-kills on stream-in all funnel through Die(). That re-dropped resources
    /// every time: the "way more gold than vanilla" inflation.
    ///
    /// FIX: an entity drops loot at most ONCE per machine (<see cref="EnemySync.TryMarkLootDropped"/>).
    /// This keeps the intended per-player economy — each nearby player still gets their own single
    /// copy — while dropping the repeats. It deliberately does NOT suppress drops on received
    /// kills: under the host-authoritative model the host owns/kills most enemies, so suppressing
    /// received-kill drops would starve clients of loot for enemies killed right next to them.
    ///
    /// Diagnostics (gated behind [Diag] SyncDiagnostics — F10): each drop is tagged this-client's
    /// kill vs received-from-another-client, suppressed duplicates are logged, and every
    /// <see cref="Vault.Add"/> is logged with the running total so the gold rate is visible.
    /// </summary>
    internal static class LootDiag
    {
        [HarmonyPatch(typeof(LootDropper), "DropLoot")]
        internal static class DropLootGuard
        {
            private static bool Prefix(LootDropper __instance)
            {
                if (!NetSession.Active) return true; // single-player: unchanged
                bool hasId = EnemySync.TryGetNetId(__instance, out int netId);
                string who = hasId ? NetDiag.Describe(netId)
                    : (__instance != null ? __instance.gameObject.name : "?");

                // De-dup: only the first drop for this entity on this machine is real.
                if (hasId && !EnemySync.TryMarkLootDropped(netId))
                {
                    if (NetDiag.Enabled)
                        NetDiag.Log("Loot", $"{who} drop SUPPRESSED — already dropped here once (duplicate death)");
                    return false;
                }

                if (NetDiag.Enabled)
                {
                    bool remote = EnemySync.SuppressLocalDeathEffects || DamageSync.IsApplyingRemote;
                    NetDiag.Log("Loot", remote
                        ? $"{who} dropped loot — received from another client's kill"
                        : $"{who} dropped loot — this client's own kill");
                }
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
