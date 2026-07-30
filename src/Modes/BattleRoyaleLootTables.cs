using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using PunkMultiverse.Core;
using UnityEngine;

namespace PunkMultiverse.Modes
{
    /// <summary>
    /// Battle Royale loot economy: more crates in the world, weapons inside them, enemies that drop
    /// more than gold, and boss kills that pay in the weapons nothing else drops (Omar, 2026-07-29).
    ///
    /// THE RULE THAT MAKES ALL OF IT SAFE: every machine must roll the same thing. World crates are
    /// added inside the generator's own pass, consuming the generator's own seeded Rnd — so the
    /// go-live world-hash barrier itself proves the pass was identical everywhere (a machine that
    /// rolled differently cannot join). Death drops are rolled from a seed derived from the run seed
    /// and the dying entity's netId, so each machine independently produces the same items in the
    /// same order; the existing contested-loot machinery (BattleRoyaleLoot) then keys them by
    /// (group, ordinal) exactly like vanilla drops, and only one player may claim each.
    ///
    /// THE WEAPON SPLIT, as specified: WHITE weapons (WeaponModuleData whose weapon's resourceUsed
    /// is the White resource) circulate — crates carry them and ordinary enemies rarely carry them.
    /// COLOURED weapons (every other resource type) come from boss-tier kills ONLY; nothing here
    /// adds a coloured weapon to any lesser source. Boss tier means: a prefab carrying the game's
    /// own BossStateActivator (the healthbar/music boss), plus the elite ids named in
    /// BrMiniBossIds (docs/bosses.md's observed miniboss roster by default).
    ///
    /// Vanilla data is never edited — no ScriptableObject is touched, so co-op and single-player
    /// keep their exact economy. Everything here is additive rolls at generation or death time,
    /// gated on the Battle Royale mode of the run being generated/played.
    /// </summary>
    internal static class BattleRoyaleLootTables
    {
        /// <summary>BR-gate that is also correct at WORLD GENERATION time. CurrentMode is only set
        /// at run start, but a dedicated server PRE-generates its world in the lobby — there the
        /// configured LobbyMode is the truth. Same disjunction Patches/BattleRoyaleSpawn uses.</summary>
        private static bool BattleRoyaleWorld
        {
            get
            {
                var s = NetSession.Instance;
                if (s == null || !NetSession.Active) return false;
                return s.CurrentMode == Protocol.GameMode.BattleRoyale
                       || s.LobbyMode == Protocol.GameMode.BattleRoyale;
            }
        }

        // ---------------------------------------------------------------- more crates in the world
        //
        // MEASURED FIRST, then written: ordinary rooms' spawn lists (RoomSetup.placedEntities)
        // contain NO containers at all — a full match logged `+0 extra` when this pass multiplied
        // room entries, because every world crate actually comes from PoI prefabs
        // (EntityGenerator.SelectPrefabForPoi places whole arrangements). Multiplying PoI prefabs
        // would duplicate entire landmark layouts, which is not "more crates", it is "two of the
        // same ruin". So crates are placed directly: each ordinary room rolls a chance of one
        // standalone container, drawn from the game's own savables collection (the same prefabs the
        // PoIs use), scattered off the room centre. Runs as a postfix INSIDE the generation pass
        // consuming the generator's own seeded Rnd — deterministic across machines by the same
        // argument as vanilla itself, and the go-live hash barrier enforces it.
        [HarmonyPatch(typeof(EntityGenerator), "PlaceObjects")]
        internal static class MoreCratesInBattleRoyale
        {
            private static void Postfix(Level level, Rnd rnd)
            {
                if (!BattleRoyaleWorld) return;
                int percent = Mathf.Clamp(NetConfig.BrRoomCratePercent.Value, 0, 100);
                if (percent <= 0) return;
                try
                {
                    var pois = ServiceLocator.Get<PoIRegistry>();
                    var egm = ServiceLocator.Get<EntityGameObjectManager>();
                    if (egm == null || egm.savablesCollection == null
                        || level == null || !level.graph.nodes.IsCreated) return;

                    // Container prefabs from the savables collection, ordered by id so every
                    // machine's Rnd picks land on the same prefab.
                    var containers = new List<SavablesCollection.EntityPrefab>();
                    foreach (var info in egm.savablesCollection.savableObjectInfos)
                        if (info.prefab != null && IsContainerId(info.entityId))
                            containers.Add(info);
                    containers.Sort((a, b) => string.CompareOrdinal(a.entityId, b.entityId));
                    if (containers.Count == 0)
                    {
                        Plugin.Log.LogWarning("[BRLoot] no container prefabs in the savables collection");
                        return;
                    }

                    int added = 0;
                    for (int i = 0; i < level.graph.nodes.Length; i++)
                    {
                        var node = level.graph.nodes[i];
                        if (pois != null && pois.Get(node.poiId) != null) continue; // PoIs own their layout
                        if (!rnd.Probability(percent / 100f)) continue;
                        var pick = containers[rnd.Range(0, containers.Count)];
                        var data = pick.prefab.CreateData();
                        data.instanceId = level.entityManager.CreateInstanceId();
                        var jitter = new Vector2(rnd.Range(-6f, 6f), rnd.Range(-6f, 6f));
                        level.entityManager.Add(data, (Vector2)node.center + jitter);
                        added++;
                    }
                    Plugin.Log.LogInfo($"[BRLoot] generation: +{added} container(s) across " +
                        $"{level.graph.nodes.Length} rooms ({percent}% per ordinary room, " +
                        $"{containers.Count} container types)");
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogWarning($"[BRLoot] extra-crate generation failed: {e.Message}");
                }
            }
        }

        internal static bool IsContainerId(string id) =>
            !string.IsNullOrEmpty(id)
            && (id.StartsWith("Crate", System.StringComparison.OrdinalIgnoreCase)
                || id.StartsWith("Box", System.StringComparison.OrdinalIgnoreCase)
                || id.StartsWith("Chest", System.StringComparison.OrdinalIgnoreCase)
                || id.StartsWith("Barrel", System.StringComparison.OrdinalIgnoreCase));

        // ---------------------------------------------------------------- death-time drop augments

        /// <summary>Runs INSIDE LootDiag.DropLootGuard's deterministic window (Priority.High puts
        /// this postfix ahead of the guard's scope-disposing one), and spawns through the same
        /// LootFactory.Create funnel — so the extra items get contested-loot keys with ordinals
        /// AFTER vanilla's, identically on every machine.</summary>
        [HarmonyPatch(typeof(LootDropper), "DropLoot")]
        internal static class AugmentBattleRoyaleDrops
        {
            [HarmonyPriority(HarmonyLib.Priority.High)]
            private static void Postfix(LootDropper __instance)
            {
                if (!BattleRoyale.Active || __instance == null) return;
                try
                {
                    var se = __instance.GetComponentInParent<SavableEntity>();
                    string id = se != null && se.EntityData != null ? se.EntityData.entityId : null;
                    if (string.IsNullOrEmpty(id)) return;

                    var session = NetSession.Instance;
                    if (session == null) return;
                    // The same-on-every-machine seed: run seed x the entity's shared identity.
                    int netId = 0;
                    if (se.EntityData != null) NetIds.TryGetNetId(se.EntityData.instanceId, out netId);
                    var rnd = new System.Random(unchecked(session.CurrentRunSeed * 486187739 + netId));
                    Vector2 pos = __instance.transform.position;

                    bool bossTier = IsBossTier(se, id, out bool fullBoss);
                    if (bossTier)
                    {
                        // Coloured weapons live HERE and nowhere else. A full boss pays a fixed
                        // number; a miniboss rolls a chance for one.
                        if (fullBoss)
                        {
                            int count = Mathf.Clamp(NetConfig.BrBossWeaponDrops.Value, 0, 6);
                            for (int i = 0; i < count; i++)
                                DropWeapon(rnd, pos, white: false, $"boss '{id}'");
                        }
                        else if (rnd.Next(100) < Mathf.Clamp(NetConfig.BrMiniBossWeaponPercent.Value, 0, 100))
                        {
                            DropWeapon(rnd, pos, white: false, $"miniboss '{id}'");
                        }
                        return;
                    }

                    if (IsContainerId(id))
                    {
                        if (rnd.Next(100) < Mathf.Clamp(NetConfig.BrCrateWeaponPercent.Value, 0, 100))
                            DropWeapon(rnd, pos, white: true, $"container '{id}'");
                        return;
                    }

                    if (id.StartsWith("Enemy", System.StringComparison.Ordinal)
                        || id.StartsWith("Unit_", System.StringComparison.Ordinal))
                    {
                        // Ordinary enemies: mostly consumables, rarely a white weapon. Never coloured.
                        if (rnd.Next(100) < Mathf.Clamp(NetConfig.BrEnemyWeaponPercent.Value, 0, 100))
                            DropWeapon(rnd, pos, white: true, $"enemy '{id}'");
                        else if (rnd.Next(100) < Mathf.Clamp(NetConfig.BrEnemyConsumablePercent.Value, 0, 100))
                            DropConsumable(rnd, pos, id);
                    }
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogWarning($"[BRLoot] drop augment failed: {e.Message}");
                }
            }
        }

        /// <summary>Boss tier: the game's own boss marker (BossStateActivator drives the healthbar
        /// and music) counts as a FULL boss; the configured elite id list counts as miniboss.</summary>
        private static bool IsBossTier(SavableEntity se, string id, out bool fullBoss)
        {
            fullBoss = false;
            try { fullBoss = se.GetComponentInChildren<BossStateActivator>(true) != null; }
            catch { }
            if (fullBoss) return true;
            var minis = NetConfig.BrMiniBossIds.Value;
            if (string.IsNullOrEmpty(minis)) return false;
            foreach (var raw in minis.Split(','))
            {
                var mini = raw.Trim();
                if (mini.Length > 0 && id.StartsWith(mini, System.StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        // Weapon module pools, split by the weapon's ammo resource. Cached per session and ORDERED
        // BY ID — the registry's enumeration order is not a cross-machine guarantee, and the pick
        // index must land on the same module everywhere.
        private static List<ModuleData> _whiteWeapons;
        private static List<ModuleData> _colouredWeapons;

        internal static void Reset() { _whiteWeapons = null; _colouredWeapons = null; }

        private static void BuildWeaponPools()
        {
            _whiteWeapons = new List<ModuleData>();
            _colouredWeapons = new List<ModuleData>();
            try
            {
                var registry = ServiceLocator.Get<ModuleRegistry>();
                if (registry == null) return;
                foreach (var module in registry.AllItems.OrderBy(m => m != null ? m.Id : "", System.StringComparer.Ordinal))
                {
                    if (!(module is WeaponModuleData wm) || wm.weapon == null || !module.Equippable) continue;
                    var resource = wm.weapon.resourceUsed;
                    bool white = resource != null
                        && resource.name.IndexOf("White", System.StringComparison.OrdinalIgnoreCase) >= 0;
                    (white ? _whiteWeapons : _colouredWeapons).Add(module);
                }
                Plugin.Log.LogInfo($"[BRLoot] weapon pools: {_whiteWeapons.Count} white, " +
                    $"{_colouredWeapons.Count} coloured (split by weapon resourceUsed)");
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"[BRLoot] weapon pool build failed: {e.Message}");
            }
        }

        private static void DropWeapon(System.Random rnd, Vector2 pos, bool white, string source)
        {
            if (_whiteWeapons == null) BuildWeaponPools();
            var pool = white ? _whiteWeapons : _colouredWeapons;
            if (pool == null || pool.Count == 0) return;
            var module = pool[rnd.Next(pool.Count)];
            var factory = ServiceLocator.Get<LootFactory>();
            if (factory == null || module == null) return;
            factory.Create(new DroppabbleItem { droppableType = DroppabbleType.Module, module = module }, pos);
            string label = !string.IsNullOrEmpty(module.displayName) ? module.displayName : module.Id;
            Plugin.Log.LogInfo($"[BRLoot] {source} dropped {(white ? "WHITE" : "COLOURED")} weapon '{label}'");
        }

        private static void DropConsumable(System.Random rnd, Vector2 pos, string source)
        {
            try
            {
                var registry = ServiceLocator.Get<ConsumableRegistry>();
                if (registry == null) return;
                var all = registry.AllItems
                    .Where(c => c != null)
                    .OrderBy(c => c.Id, System.StringComparer.Ordinal)
                    .ToList();
                if (all.Count == 0) return;
                var pick = all[rnd.Next(all.Count)];
                var factory = ServiceLocator.Get<LootFactory>();
                if (factory == null) return;
                factory.Create(new DroppabbleItem { droppableType = DroppabbleType.Consumable, consumable = pick }, pos);
                Plugin.Log.LogInfo($"[BRLoot] enemy '{source}' dropped consumable '{pick.Id}'");
            }
            catch { }
        }
    }
}
