using System.Collections.Generic;
using HarmonyLib;
using PunkMultiverse.Core;
using PunkMultiverse.Protocol;
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
        // ---- distant-teammate loot payload ------------------------------------------------
        // On the killer/authority machine the vanilla death chain runs LootDropper.DropLoot and
        // spawns per-player pickups locally. A teammate for whom the entity isn't resident never
        // runs that chain and would get NOTHING. We record exactly what the killer's DropLoot rolled
        // (keyed by the entity's netId) so BroadcastEntityDeath can ship it on EntityKilledMsg;
        // receivers that didn't drop locally grant each player their own copy straight into the
        // (per-player, never-synced) Vault. Ingredient, shared-currency, and Consumable drops travel
        // this path (all have a clean per-player inventory grant); Module drops stay resident-only —
        // they are physical ModulePickup GameObjects with no inventory-add API, so there is nothing to
        // grant remotely without spawning a position-based pickup (a different mechanism, WS5.1).
        private static readonly Dictionary<int, Dictionary<string, int>> CapturedLoot
            = new Dictionary<int, Dictionary<string, int>>();

        private static void CaptureIngredientDrop(int netId, string id)
        {
            if (!CapturedLoot.TryGetValue(netId, out var byId))
                CapturedLoot[netId] = byId = new Dictionary<string, int>();
            byId.TryGetValue(id, out int n);
            byId[id] = n + 1; // each Ingredient pickup is worth 1 (IngredientPickup.OnPickedUp adds 1)
        }

        // Reserved LootEntry.Id prefix marking a SHARED-currency (money/gold) amount rather than an
        // Ingredient id. Suffix is the Resource.Id; Count carries the currency VALUE (not a count).
        // Kept in the same CapturedLoot map so it ships on the existing EntityKilledMsg.Loot payload
        // with no wire-format change.
        internal const string CurrencyIdPrefix = "$res:";

        private static void CaptureResourceDrop(int netId, string resourceId, int amount)
        {
            if (!CapturedLoot.TryGetValue(netId, out var byId))
                CapturedLoot[netId] = byId = new Dictionary<string, int>();
            string key = CurrencyIdPrefix + resourceId;
            byId.TryGetValue(key, out int n);
            byId[key] = n + amount; // accumulate the coin's currency VALUE
        }

        // Reserved prefix marking a Consumable id (vs an Ingredient id). Suffix is the Consumable.id;
        // Count is the number of that consumable, granted per-player via Vault.Add(Consumable,int).
        internal const string ConsumableIdPrefix = "$con:";

        private static void CaptureConsumableDrop(int netId, string id)
        {
            if (!CapturedLoot.TryGetValue(netId, out var byId))
                CapturedLoot[netId] = byId = new Dictionary<string, int>();
            string key = ConsumableIdPrefix + id;
            byId.TryGetValue(key, out int n);
            byId[key] = n + 1; // each Consumable pickup is worth 1
        }

        // Reserved prefix marking a Module (equipment) id. Modules are the ONLY loot with no
        // inventory-add API — they live purely as physical ModulePickup entities. So unlike currency
        // and ingredients (granted directly to a distant player), a module MUST be re-spawned as a
        // real pickup at the death site on every machine, or a non-owner gets nothing at all (the
        // tester's permanently-lost tech shield). The pickup carries the vanilla EntityMapItem, so its
        // map marker + fog/discovery gating come for free.
        internal const string ModuleIdPrefix = "$mod:";

        private static void CaptureModuleDrop(int netId, string id)
        {
            if (!CapturedLoot.TryGetValue(netId, out var byId))
                CapturedLoot[netId] = byId = new Dictionary<string, int>();
            string key = ModuleIdPrefix + id;
            byId.TryGetValue(key, out int n);
            byId[key] = n + 1;
        }

        /// <summary>Read and clear the loot rolled for netId as a wire payload. Empty when nothing
        /// ingredient-typed was rolled (or this machine didn't simulate the death).</summary>
        internal static LootEntry[] ConsumeCapturedLoot(int netId)
        {
            if (!CapturedLoot.TryGetValue(netId, out var byId) || byId.Count == 0)
                return System.Array.Empty<LootEntry>();
            CapturedLoot.Remove(netId);
            var entries = new LootEntry[byId.Count];
            int i = 0;
            foreach (var kv in byId) entries[i++] = new LootEntry { Id = kv.Key, Count = kv.Value };
            return entries;
        }

        internal static void DiscardCapturedLoot(int netId) => CapturedLoot.Remove(netId);

        internal static void ResetCapturedLoot() => CapturedLoot.Clear();

        /// <summary>Grant a received loot payload to the LOCAL player when the entity wasn't resident
        /// here (its death chain never dropped locally). Per-player: goes straight to this player's
        /// Vault, which is never synced, so it can't inflate anyone else. Gated on the SAME
        /// one-drop-per-machine latch as the local drop so a machine that dropped locally never also
        /// grants.</summary>
        internal static void GrantRemoteLoot(int netId, LootEntry[] loot, UnityEngine.Vector2 deathPos, bool hasPos)
        {
            if (loot == null || loot.Length == 0) return;
            if (!EnemySync.TryMarkLootDropped(netId)) return; // already dropped/granted here

            IngredientRegistry registry = null;
            RunData runData = null;
            ResourceRegistry resources = null;
            try { registry = ServiceLocator.Get<IngredientRegistry>(); } catch { }
            try { runData = ServiceLocator.Get<RunData>(); } catch { }
            try { resources = ServiceLocator.Get<ResourceRegistry>(); } catch { }

            foreach (var entry in loot)
            {
                if (string.IsNullOrEmpty(entry.Id) || entry.Count <= 0) continue;

                // Module: spawn a REAL physical pickup at the death site (no inventory grant exists,
                // and "be present" is the model). ModulePickupFactory.Create routes through
                // EntityGameObjectManager.CreateEntity — and MinionSync.MarkLootSpawns already patches
                // that factory, so the spawn is auto-excluded from runtime-spawn replication (stays a
                // local per-player copy). The pickup's EntityMapItem gives it the vanilla map marker,
                // shown only once the area is discovered.
                if (entry.Id.StartsWith(ModuleIdPrefix, System.StringComparison.Ordinal))
                {
                    if (!hasPos) continue; // no site to place it — can't materialize a physical module
                    string modId = entry.Id.Substring(ModuleIdPrefix.Length);
                    ModuleData md = null;
                    try { md = ServiceLocator.Get<ModuleRegistry>()?.Get(modId); } catch { }
                    if (md == null)
                    {
                        if (NetDiag.Enabled)
                            NetDiag.Log("Loot", $"{NetDiag.Describe(netId)} remote module '{modId}' — unknown id, skipped");
                        continue;
                    }
                    try
                    {
                        var factory = ServiceLocator.Get<ModulePickupFactory>();
                        for (int i = 0; i < entry.Count; i++) factory?.Create(md, deathPos);
                        if (NetDiag.Enabled)
                            NetDiag.Log("Loot", $"{NetDiag.Describe(netId)} materialized module '{modId}' x{entry.Count} at {deathPos} (physical pickup, map-marked)");
                    }
                    catch (System.Exception e)
                    {
                        Plugin.Log.LogWarning($"[Loot] module materialize failed for '{modId}': {e.Message}");
                    }
                    continue;
                }

                // Shared currency (money/gold): charge the local player's shared resource tank
                // directly — no physical coin (avoids re-replicating a pickup). The tank is the same
                // object the ship reads, so the money HUD updates as a real pickup would. Per-machine,
                // never synced — can't inflate anyone else's wallet.
                if (entry.Id.StartsWith(CurrencyIdPrefix, System.StringComparison.Ordinal))
                {
                    if (runData == null || resources == null) continue;
                    string resId = entry.Id.Substring(CurrencyIdPrefix.Length);
                    var res = resources.Get(resId);
                    ResourceTank tank = null;
                    if (res != null)
                        foreach (var t in runData.SharedResourceTanks)
                            if (t != null && t.resource == res) { tank = t; break; }
                    if (tank == null)
                    {
                        if (NetDiag.Enabled)
                            NetDiag.Log("Loot", $"{NetDiag.Describe(netId)} remote currency '{resId}' — no shared tank, skipped");
                        continue;
                    }
                    tank.Charge(entry.Count);
                    if (NetDiag.Enabled)
                        NetDiag.Log("Loot", $"{NetDiag.Describe(netId)} granted remote currency +{entry.Count} {resId} (entity not resident here)");
                    continue;
                }

                // Consumable: spawn a physical pickup to COLLECT (Omar's "everyone picks up their
                // own"). This also side-steps a real crash — Vault.Add(Consumable) drove the vanilla
                // ConsumableWheel.OnConsumableAmountChanged into an out-of-range index when granting
                // to a distant player whose wheel UI wasn't expecting it. Factory routes through
                // CreateEntity, which MarkLootSpawns already excludes from replication.
                if (entry.Id.StartsWith(ConsumableIdPrefix, System.StringComparison.Ordinal))
                {
                    if (!hasPos) continue;
                    string conId = entry.Id.Substring(ConsumableIdPrefix.Length);
                    Consumable consumable = null;
                    try { consumable = ServiceLocator.Get<IRegistry<Consumable, string>>()?.Get(conId); } catch { }
                    if (consumable == null)
                    {
                        if (NetDiag.Enabled)
                            NetDiag.Log("Loot", $"{NetDiag.Describe(netId)} remote consumable '{conId}' — unknown id, skipped");
                        continue;
                    }
                    try
                    {
                        var factory = ServiceLocator.Get<ConsumablePickupFactory>();
                        for (int i = 0; i < entry.Count; i++) factory?.Create(consumable, deathPos);
                        if (NetDiag.Enabled)
                            NetDiag.Log("Loot", $"{NetDiag.Describe(netId)} materialized consumable '{conId}' x{entry.Count} at {deathPos}");
                    }
                    catch (System.Exception e) { Plugin.Log.LogWarning($"[Loot] consumable materialize failed '{conId}': {e.Message}"); }
                    continue;
                }

                // Ingredient: spawn a physical pickup to collect (same model).
                if (!hasPos) continue;
                var ingredient = registry?.Get(entry.Id);
                if (ingredient == null)
                {
                    if (NetDiag.Enabled)
                        NetDiag.Log("Loot", $"{NetDiag.Describe(netId)} remote loot '{entry.Id}' x{entry.Count} — unknown id, skipped");
                    continue;
                }
                try
                {
                    var factory = ServiceLocator.Get<IngredientPickupFactory>();
                    for (int i = 0; i < entry.Count; i++) factory?.Create(ingredient, deathPos);
                    if (NetDiag.Enabled)
                        NetDiag.Log("Loot", $"{NetDiag.Describe(netId)} materialized ingredient '{entry.Id}' x{entry.Count} at {deathPos}");
                }
                catch (System.Exception e) { Plugin.Log.LogWarning($"[Loot] ingredient materialize failed '{entry.Id}': {e.Message}"); }
            }
        }

        // Records each Ingredient the killer's DropLoot actually spawned, keyed by the entity's
        // netId. Runs inside DamagableResource.Die() (onDeath -> DropLoot -> Drop), synchronously
        // before the death is broadcast, so ConsumeCapturedLoot is ready when BroadcastEntityDeath
        // (a Die Postfix) reads it. Entries from a resident receiver's re-kill are cleared by
        // DiscardCapturedLoot in ApplyEntityKilled.
        [HarmonyPatch(typeof(LootDropper), "Drop")]
        internal static class DropCapture
        {
            private static void Postfix(LootDropper __instance, DroppabbleItem __0)
            {
                if (!NetSession.Active) return;
                if (!EnemySync.TryGetNetId(__instance, out int netId)) return;

                // Ingredient == item pickup granted per-player into the Vault.
                if (__0.droppableType == DroppabbleType.Ingedient)
                {
                    var ingredient = __0.ingredient;
                    if (ingredient == null || string.IsNullOrEmpty(ingredient.id)) return;
                    CaptureIngredientDrop(netId, ingredient.id);
                    return;
                }

                // Consumable == item pickup granted per-player into the Vault (WS5.1).
                if (__0.droppableType == DroppabbleType.Consumable)
                {
                    var consumable = __0.consumable;
                    if (consumable == null || string.IsNullOrEmpty(consumable.id)) return;
                    CaptureConsumableDrop(netId, consumable.id);
                    return;
                }

                // Module == physical equipment pickup. No inventory API — re-spawned as a real
                // ModulePickup on receivers so everyone gets one to collect (must be present).
                if (__0.droppableType == DroppabbleType.Module)
                {
                    var module = __0.module;
                    if (module == null || string.IsNullOrEmpty(module.Id)) return;
                    CaptureModuleDrop(netId, module.Id);
                    return;
                }

                // Prefab coin == shared currency (money/gold). The coin GameObject carries a
                // ResourcePickup whose serialized `amount` charges the collector's shared resource
                // tank. The value is known here at drop time. Only SHARED resources travel this path.
                if (__0.droppableType == DroppabbleType.Prefab)
                {
                    var prefab = __0.prefab;
                    if (prefab == null || !prefab.TryGetComponent<ResourcePickup>(out var coin)) return;
                    var res = coin.resource;
                    if (res == null || !res.isShared) return;
                    int amount = Mathf.RoundToInt(coin.amount);
                    if (amount <= 0) return;
                    CaptureResourceDrop(netId, res.Id, amount);
                }
            }
        }

        [HarmonyPatch(typeof(LootDropper), "DropLoot")]
        internal static class DropLootGuard
        {
            private static bool Prefix(LootDropper __instance, out DeterministicGeneration.Scope __state)
            {
                __state = null;
                var session = NetSession.Instance;
                if (session == null || !NetSession.Active) return true; // single-player: unchanged

                // No netId (unsynced orphan): each machine handles its own copy locally — allow.
                if (!EnemySync.TryGetNetId(__instance, out int netId)) return true;
                bool isPlantFruit = EnemySync.TryGetPlantFruitIdentity(__instance,
                    out int plantNetId, out int fruitId);

                // Instanced per player, but only where the local player can actually COLLECT it. The
                // killer and anyone near the death drop a real pickup at the death site. A player too
                // far to reach that pickup must NOT spawn an uncollectable pile there — instead they
                // get a collectable copy straight into their Vault via the EntityKilledMsg loot
                // payload (EnemySync.ApplyEntityKilled -> GrantRemoteLoot). Suppress the far local
                // drop here WITHOUT marking the de-dup latch, so the grant path is free to fire.
                byte local = (byte)session.LocalSlot;
                byte killer = EnemySync.SuppressLocalDeathEffects
                    ? EnemySync.RemoteKillerSlot
                    : DamageSync.LastKiller(netId);
                if (killer != local && !NearLocalShip(__instance))
                {
                    NetDiag.Throttled($"loot-far-{netId}", 10f, "Loot",
                        () => $"{NetDiag.Describe(netId)} local drop SUPPRESSED — too far; granted at ship via payload");
                    NoteRepeat(netId, __instance);
                    return false;
                }

                // De-dup so a re-applied / zombie death can't drop twice HERE.
                bool firstDrop = isPlantFruit
                    ? EnemySync.TryMarkPlantFruitLootDropped(plantNetId, fruitId)
                    : EnemySync.TryMarkLootDropped(netId);
                if (!firstDrop)
                {
                    NetDiag.Throttled($"loot-dup-{netId}", 10f, "Loot",
                        () => $"{NetDiag.Describe(netId)} drop SUPPRESSED — already dropped here (duplicate death)");
                    NoteRepeat(netId, __instance);
                    return false;
                }

                if (NetDiag.Enabled)
                    NetDiag.Log("Loot", $"{NetDiag.Describe(netId)} dropped loot (instanced, one per nearby player)");
                // Pickups are per-player copies, but their CONTENTS should agree across
                // machines: seed the roll from stable identity + this death's mutation
                // revision so every peer's local copy rolls the same result.
                uint revision = isPlantFruit
                    ? EnemySync.PlantFruitRevisionOf(plantNetId, fruitId)
                    : EnemySync.MutationRevisionOf(netId);
                __state = DeterministicGeneration.Begin(DeterministicGeneration.Mix(
                    session.CurrentRunSeed, isPlantFruit ? plantNetId : netId,
                    unchecked((int)revision), isPlantFruit ? fruitId ^ 0x4C4F4F54 : 0x4C4F4F54));
                return true;
            }

            private static void Postfix(DeterministicGeneration.Scope __state)
                => DeterministicGeneration.End(__state);

            // A drop is collectable here only if the local player is close enough to reach it.
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
