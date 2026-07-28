using System.Collections.Generic;
using HarmonyLib;
using PunkMultiverse.Core;
using UnityEngine;

namespace PunkMultiverse.Patches
{
    /// <summary>
    /// Battle Royale takes the enemies and the other players back off the map.
    ///
    /// The mode reveals the whole world at match start (<c>MapDrawer.DiscoverWholeMap</c>) so
    /// players can plan a route to the next ring. That call does two things, and only one of them
    /// was wanted: it fills the map texture AND sets every cell of <c>FoWMaskForIcons</c> to true.
    /// <c>MapIconManager.RefreshIconsPositionAndVisibility</c> shows an icon whenever its entity is
    /// not in fog — so revealing the map also handed every player a live, map-wide readout of every
    /// enemy AND every other player's ship (<c>ShipManager.AssignTheme</c> confirms ships carry map
    /// icons). Field-reported 2026-07-27: "all enemies in loaded/discovered regions are visible from
    /// the map, and I can still see the other players' location".
    ///
    /// Fixed here rather than by dropping the reveal, because the reveal is the feature — the fog
    /// was never what was supposed to be hiding people. This is the same rule the screen-edge
    /// trackers (UI/PlayerTracker.cs) and the HUD minimap (UI/RingMapOverlay.cs) already follow, now
    /// applied to the third and last surface that leaks position: finding people is the game.
    ///
    /// A postfix rather than a prefix: vanilla still does its own fog/always-visible bookkeeping,
    /// and we only overrule the final visible/hidden decision. Icons stay INSTANTIATED (cheap, and
    /// they must come straight back if a match ends), they are just not shown.
    /// </summary>
    internal static class BattleRoyaleMapIcons
    {
        [HarmonyPatch(typeof(MapIconManager), "RefreshIconsPositionAndVisibility")]
        internal static class HideCombatantIcons
        {
            private static void Postfix(MapIconManager __instance)
            {
                if (!Modes.BattleRoyale.Active) return;
                try { Hide(__instance); }
                catch { /* the map must never be the thing that breaks a match */ }
            }
        }

        private static System.Reflection.FieldInfo _iconsField;
        private static System.Reflection.FieldInfo _visibleField;
        private static readonly HashSet<int> LocalShipInstanceIds = new HashSet<int>();
        private static float _nextLocalShipScanAt;

        private static void Hide(MapIconManager manager)
        {
            if (_iconsField == null)
                _iconsField = AccessTools.Field(typeof(MapIconManager), "icons");
            if (_visibleField == null)
                _visibleField = AccessTools.Field(typeof(MapIconManager), "entitiesWithVisibleIcons");
            if (_iconsField == null) return;

            if (!(_iconsField.GetValue(manager) is Dictionary<EntityData, MapIcon> icons)) return;
            var visible = _visibleField?.GetValue(manager) as HashSet<int>;

            RefreshLocalShipIds();

            foreach (var kv in icons)
            {
                var entity = kv.Key;
                var icon = kv.Value;
                if (entity == null || icon == null) continue;
                if (!ShouldHide(entity)) continue;
                if (icon.gameObject.activeSelf) icon.gameObject.SetActive(false);
                // Keep the manager's own "what is visible" set honest — IsIconVisible feeds the
                // instrument/discovery UI, which would otherwise offer to track a hidden icon.
                visible?.Remove(entity.instanceId);
            }
        }

        /// <summary>EVERYTHING is hidden except your own ship.
        ///
        /// This started as "hide the enemies and the other players" and that was the wrong shape —
        /// it was a blocklist, so every entity type nobody had thought about stayed on the map.
        /// Crates and beacons were still being handed out for free (Omar, 2026-07-28: "really the
        /// only thing that should be shown on the map in battle royale is the supply drop and the
        /// ring zones"). An allowlist cannot rot the same way: a new entity type added to the game
        /// is hidden by default rather than quietly leaking whatever it is.
        ///
        /// Your own ship is the one exception, and it is not a compromise of the rule — a map you
        /// cannot locate yourself on cannot be used to run from a closing ring, and it tells you
        /// nothing you do not already know.
        ///
        /// The supply drop is NOT excepted here because it is not drawn by this system at all: it
        /// gets a purpose-built marker in UI/RingWorldMapOverlay.cs, which is why it can look like
        /// a supply drop instead of whatever icon its crate prefab happens to carry.</summary>
        private static bool ShouldHide(EntityData entity)
        {
            if (entity.entityId == "Ship") return !LocalShipInstanceIds.Contains(entity.instanceId);
            return true;
        }

        /// <summary>Which entity instance is OUR ship. Rescanned on a timer rather than cached
        /// once: the local ship is replaced on resurrection, and a stale id here would hide the
        /// player from their own map.</summary>
        private static void RefreshLocalShipIds()
        {
            if (Time.unscaledTime < _nextLocalShipScanAt) return;
            _nextLocalShipScanAt = Time.unscaledTime + 1f;
            LocalShipInstanceIds.Clear();
            try
            {
                var local = Sync.ShipSync.LocalShip;
                if (local == null) return;
                var se = local.GetComponentInChildren<SavableEntity>();
                if (se != null && se.EntityData != null) LocalShipInstanceIds.Add(se.EntityData.instanceId);
            }
            catch { }
        }
    }
}
