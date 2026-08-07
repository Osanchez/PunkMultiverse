using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace PunkMultiverse.Content
{
    /// <summary>
    /// Keeps the game's own loot tables and loadout pool restorable after a content mod has
    /// written into them.
    ///
    /// This mod's rule for BR loot is that vanilla data is never edited — no ScriptableObject is
    /// touched, so co-op and single-player keep their exact economy. Content mods do not share
    /// that rule. WeaponForge injects its weapons by appending straight into the live
    /// <c>DropTableWeightedGroup.itemDistribution</c> assets, and appends its loadouts into
    /// <c>LoadoutPool.loadouts</c>; neither is ever removed. Those are ScriptableObjects, so the
    /// additions outlive the run and every later run in the same process.
    ///
    /// That matters in two directions:
    ///   * A session where everyone runs the HOST's weapon pack would otherwise leave the host's
    ///     weapons in the player's crate tables for the rest of the process — visible in their
    ///     next SOLO run, until they restart PUNK.
    ///   * Going the other way, weapons a player injected during solo play are still sitting in
    ///     those tables when they join a session, where the host may not have them at all.
    ///
    /// So: take a pristine copy of anything a content mod is about to touch, BEFORE it touches
    /// it, and be able to put it back. Both capture hooks are <see cref="Priority.First"/>
    /// prefixes on the same methods the content mod patches, which is what guarantees "before".
    ///
    /// Nothing here knows what WeaponForge is. It defends the vanilla assets against any mod
    /// that appends to them, and it is inert on a machine where nothing does.
    /// </summary>
    internal static class VanillaContentGuard
    {
        // Pristine copies, keyed by the asset they came from. The entries themselves are never
        // cloned: a mod that only APPENDS leaves the originals untouched, so holding the same
        // references and restoring list membership is exact. (If one ever mutated an existing
        // entry in place this would not catch it — the digest barrier at go-live would.)
        private static readonly Dictionary<DropTableWeightedGroup,
            List<DropTableWeightedGroup.DroppabbleItemDistributionItem>> DropGroups =
            new Dictionary<DropTableWeightedGroup, List<DropTableWeightedGroup.DroppabbleItemDistributionItem>>();

        private static readonly Dictionary<LoadoutPool, List<LoadoutTemplate>> Pools =
            new Dictionary<LoadoutPool, List<LoadoutTemplate>>();

        // DropTableItem.group is a private field on a struct — the same one WeaponForge reflects
        // to find the pools it augments. Reading it off a boxed copy is fine: the group is a
        // reference to the shared ScriptableObject, which is the thing we need.
        private static readonly FieldInfo GroupField =
            AccessTools.Field(typeof(DropTableItem), "group");

        private static readonly FieldInfo SelectorPoolField =
            AccessTools.Field(typeof(LoadoutSelector), "loadoutPool");

        internal static int TrackedGroups => DropGroups.Count;
        internal static int TrackedPools => Pools.Count;

        /// <summary>
        /// Put every tracked asset back exactly as the game shipped it. Safe to call when nothing
        /// was ever modified — it simply finds no differences. Returns the number of appended
        /// entries removed, so callers can log a real number instead of asserting success.
        /// </summary>
        internal static int RestorePristine(string why)
        {
            int removed = 0;
            foreach (var pair in DropGroups)
            {
                var group = pair.Key;
                if (group == null || group.itemDistribution == null) continue;
                var live = group.itemDistribution.Items;
                if (live == null) continue;
                int delta = live.Count - pair.Value.Count;
                if (delta == 0) continue;
                live.Clear();
                live.AddRange(pair.Value);
                removed += delta;
            }

            int loadoutsRemoved = 0;
            foreach (var pair in Pools)
            {
                var pool = pair.Key;
                if (pool == null || pool.loadouts == null) continue;
                int delta = pool.loadouts.Count - pair.Value.Count;
                if (delta == 0) continue;
                pool.loadouts.Clear();
                pool.loadouts.AddRange(pair.Value);
                loadoutsRemoved += delta;
            }

            if (removed > 0 || loadoutsRemoved > 0)
                Plugin.Log.LogInfo($"[Content] restored vanilla data ({why}): removed {removed} " +
                    $"added drop entr{(removed == 1 ? "y" : "ies")} across {DropGroups.Count} group(s) " +
                    $"and {loadoutsRemoved} added loadout(s)");
            return removed + loadoutsRemoved;
        }

        private static void SnapshotGroup(DropTableWeightedGroup group)
        {
            if (group == null || DropGroups.ContainsKey(group)) return;
            var live = group.itemDistribution?.Items;
            if (live == null) return;
            DropGroups[group] = new List<DropTableWeightedGroup.DroppabbleItemDistributionItem>(live);
        }

        /// <summary>
        /// Runs ahead of any content mod's own prefix on the same method, so the copy it takes is
        /// the game's, not the game's-plus-theirs. Snapshotting from the passed table (rather than
        /// scanning every loaded group) means we only ever look at groups that are about to be
        /// used, and we see them at the first moment they can be touched.
        /// </summary>
        [HarmonyPatch(typeof(LootSelector), "SelectLoot")]
        [HarmonyPriority(Priority.First)]
        internal static class SnapshotDropGroups
        {
            private static void Prefix(DropTable dropTable)
            {
                try
                {
                    if (dropTable?.items == null || GroupField == null) return;
                    foreach (var item in dropTable.items)
                        SnapshotGroup(GroupField.GetValue(item) as DropTableWeightedGroup);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"[Content] drop-group snapshot skipped: {e.Message}");
                }
            }
        }

        [HarmonyPatch(typeof(LoadoutSelector), "Populate")]
        [HarmonyPriority(Priority.First)]
        internal static class SnapshotLoadoutPool
        {
            private static void Prefix(LoadoutSelector __instance)
            {
                try
                {
                    var pool = SelectorPoolField?.GetValue(__instance) as LoadoutPool;
                    if (pool?.loadouts == null || Pools.ContainsKey(pool)) return;
                    Pools[pool] = new List<LoadoutTemplate>(pool.loadouts);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"[Content] loadout-pool snapshot skipped: {e.Message}");
                }
            }
        }
    }
}
