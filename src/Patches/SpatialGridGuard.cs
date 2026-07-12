using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace PunkMultiverse.Patches
{
    /// <summary>
    /// Null-safety guard for the vanilla <c>SpatialGrid.Remove</c>, which NREs when an entity's
    /// current position maps to a grid bucket that doesn't contain it:
    /// <code>
    ///   if (!entityDictionary.TryGetValue(gridIndex, out var value))
    ///       Debug.LogWarning(...);   // logs but does NOT return
    ///   value.Remove(entity);        // value is null here -> NullReferenceException
    /// </code>
    /// Our kill sync destroys an entity's data (EntityData.Destroy → Destroyed event →
    /// SpatialGrid.Remove) on every machine; if that throws, the destroy aborts and the entity
    /// is never removed — it stays standing/alive on the machine that failed (seen as "an entity
    /// one client killed still visible" and the "[Enemies] kill apply failed … SpatialGrid.Remove"
    /// warning). We intervene ONLY in the crash case: when the position's bucket is missing we
    /// scan-remove the entity from wherever it actually sits and skip the throwing original;
    /// otherwise vanilla runs untouched.
    /// </summary>
    internal static class SpatialGridGuard
    {
        [HarmonyPatch(typeof(SpatialGrid), "Remove")]
        internal static class SafeRemove
        {
            private static FieldInfo _dict;

            private static bool Prefix(SpatialGrid __instance, EntityData entity)
            {
                if (entity == null) return false; // nothing to remove; vanilla would NRE on GetGridIndex
                try
                {
                    if (_dict == null) _dict = AccessTools.Field(typeof(SpatialGrid), "entityDictionary");
                    if (!(_dict?.GetValue(__instance) is Dictionary<Vector2Int, List<EntityData>> dict))
                        return true; // can't inspect — let vanilla handle it

                    int seg = Level.SegmentSize > 0 ? Level.SegmentSize : 25;
                    var idx = Vector2Int.FloorToInt(entity.position / seg); // mirrors GetGridIndex
                    if (dict.TryGetValue(idx, out var bucket) && bucket != null)
                        return true; // normal case: bucket exists, vanilla removes it safely

                    // The entity isn't in the bucket its position maps to (its position drifted from
                    // where it was bucketed). Vanilla would NRE — remove it wherever it actually is.
                    foreach (var kv in dict)
                        if (kv.Value != null && kv.Value.Remove(entity)) break;
                    return false;
                }
                catch
                {
                    return true; // on any reflection hiccup, fall back to vanilla
                }
            }
        }
    }
}
