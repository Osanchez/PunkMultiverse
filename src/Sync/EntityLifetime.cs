using System;
using System.Collections.Generic;
using HarmonyLib;
using PunkMultiverse.Core;
using UnityEngine;

namespace PunkMultiverse.Sync
{
    /// <summary>
    /// Destroying an entity's GameObject is not the same as ending its life, and the difference is
    /// what put a black screen in front of both players (2026-08-08).
    ///
    /// <c>SavableEntity.Bind</c> subscribes to <c>EntityData.Moved</c>; only <c>Unbind</c> removes
    /// that subscription — <c>OnDestroy</c> does not. Vanilla never notices because it destroys
    /// these objects through exactly one door, <c>EntityGameObjectManager.UnloadEntity</c>, which
    /// unbinds first. This mod has its own reasons to destroy them (kills, ghost removal, duplicate
    /// retirement, disconnect despawn) and went straight to <c>Object.Destroy</c>. The object dies,
    /// the EntityData lives on in the EntityManager, and the dead subscriber stays on its Moved
    /// list — so the next time ANY live entity moves:
    ///
    /// <code>
    /// NullReferenceException at UnityEngine.Component.get_transform ()
    ///   SavableEntity.OnEntityMoved (...)
    ///   EntityData.MoveTo (...)
    ///   SavableEntity.Update ()        ← the LIVE entity's update, aborted every frame
    /// </code>
    ///
    /// That is a per-frame throw inside a live object's Update, forever: entities stop updating,
    /// the world never finishes coming up, and the player sits on a black screen while the
    /// simulation keeps running around them (they get killed by something they cannot see).
    ///
    /// Most of the mod's destroy sites also destroy the EntityData right after, which hides the
    /// leak — the data is gone, so nothing raises Moved. The two that do NOT are exactly the ones
    /// that keep the data alive on purpose: duplicate-lifetime retirement (the canonical object
    /// shares that data) and divergence heal (it respawns from the same data immediately after).
    ///
    /// So: unsubscribe first, then destroy. Never <c>Unbind</c> — it would also run
    /// <c>EntityData.Destroy()</c> for anything flagged <c>destroyWhenUnloaded</c>, and on a
    /// duplicate that would take the canonical entity's data with it.
    /// </summary>
    internal static class EntityLifetime
    {
        private static int _unsubscribed;
        private static MethodBaseCache _cache;

        private sealed class MethodBaseCache
        {
            internal System.Reflection.MethodInfo OnEntityMoved;
        }

        internal static void Reset() => _unsubscribed = 0;

        internal static int UnsubscribedCount => _unsubscribed;

        /// <summary>Remove this object's Moved handler from its data, leaving the data itself and
        /// every other subscriber untouched. Safe to call on anything, including a duplicate that
        /// shares its EntityData with the canonical object.</summary>
        internal static void Unsubscribe(SavableEntity se)
        {
            if (se == null) return;
            try
            {
                var data = se.EntityData;
                if (data == null) return;   // already unbound: nothing to leak
                if (_cache == null)
                    _cache = new MethodBaseCache { OnEntityMoved = AccessTools.Method(typeof(SavableEntity), "OnEntityMoved") };
                if (_cache.OnEntityMoved == null) return;
                // A fresh delegate over the same target+method compares equal, so -= finds the
                // original registration.
                var handler = (Action<EntityData, Vector3, Vector3>)_cache.OnEntityMoved
                    .CreateDelegate(typeof(Action<EntityData, Vector3, Vector3>), se);
                data.Moved -= handler;
                _unsubscribed++;
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Lifetime] unsubscribe failed: {e.Message}"); }
        }

        /// <summary>The mod's door for destroying an entity's GameObject: unsubscribe, then destroy.</summary>
        internal static void Destroy(SavableEntity se)
        {
            if (se == null) return;
            // Note before unsubscribing — the data (and with it the netId) is still reachable, and
            // "the mod destroyed this one" is the single most useful line in a forensics dump.
            try
            {
                var data = se.EntityData;
                if (data != null) EntityForensics.NoteInstance(data.instanceId, EntityForensics.Kind.Destroy, "mod destroy");
            }
            catch { }
            Unsubscribe(se);
            UnityEngine.Object.Destroy(se.gameObject);
        }

        // ------------------------------------------------------------------ vanilla containment

        /// <summary>
        /// <c>SpawnObjectForEntity</c> ends with <c>entityGameObjects[instanceId] = savableEntity</c>
        /// — a plain overwrite. Spawning an entity that already has a live object therefore ORPHANS
        /// the first one: still bound to the data, no longer reachable from the manager, so nothing
        /// will ever unbind it. Hand back the object that already exists instead.
        /// </summary>
        [HarmonyPatch(typeof(EntityGameObjectManager), "SpawnObjectForEntity")]
        internal static class NoDoubleSpawn
        {
            private static bool Prefix(EntityGameObjectManager __instance, EntityData __0, ref SavableEntity __result)
            {
                if (!NetSession.Active || __0 == null) return true;
                try
                {
                    if (!__instance.TryGetSavableEntity(__0.instanceId, out var existing) || existing == null)
                        return true;
                    __result = existing;
                    return false;
                }
                catch { return true; }
            }
        }

        /// <summary>
        /// <c>InstantiateGameObjects</c> enumerates a segment's entity list WHILE spawning into it:
        ///
        /// <code>
        /// foreach (EntityData item in level.entityManager.GetEntitiesInSegment(segmentPosition))
        ///     if (item.isUnloadable) SpawnObjectForEntity(item);
        /// activeSegments.Add(segmentPosition);
        /// </code>
        ///
        /// Anything that adds or moves an entity into that segment mid-loop throws
        /// <c>InvalidOperationException: Collection was modified</c> — observed 28-29 times per
        /// session. The loop dies partway, and the last line never runs, so the segment is never
        /// marked active: later unloads then miss the dictionary (388 x "Trying to unload
        /// savableEntity not found in the dictionary" in one session).
        ///
        /// Same five lines over a snapshot. Same order, same calls, same result — it just cannot
        /// be interrupted by its own side effects.
        /// </summary>
        [HarmonyPatch(typeof(EntityGameObjectManager), "InstantiateGameObjects")]
        internal static class BuildSegmentFromSnapshot
        {
            private static bool Prefix(EntityGameObjectManager __instance, Vector2Int __0)
            {
                if (!NetSession.Active) return true;
                try
                {
                    var view = Traverse.Create(__instance);
                    var level = view.Field("level").GetValue<Level>();
                    var active = view.Field("activeSegments").GetValue<HashSet<Vector2Int>>();
                    if (level == null || level.entityManager == null || active == null)
                        return true;   // shape changed under us: vanilla knows better than a guess

                    var snapshot = new List<EntityData>(level.entityManager.GetEntitiesInSegment(__0));
                    foreach (var item in snapshot)
                        if (item != null && item.isUnloadable)
                            __instance.SpawnObjectForEntity(item);
                    active.Add(__0);
                    return false;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"[Lifetime] segment build guard failed, letting vanilla run: {e.Message}");
                    return true;
                }
            }
        }
    }
}
