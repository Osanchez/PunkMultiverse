using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using PunkMultiverse.Core;
using PunkMultiverse.Sync;
using UnityEngine;

namespace PunkMultiverse.Modes
{
    /// <summary>
    /// The half of Battle Royale that runs on EVERY machine: staying inside the ring, the match
    /// HUD, and picking this player's spawn station. A ship's health belongs to the machine that
    /// owns it, so out-of-zone damage is self-applied rather than commanded by the host — the host
    /// only publishes where the ring is.
    /// </summary>
    internal static partial class BattleRoyale
    {
        // ---------------------------------------------------------------- kill zone

        private static float _nextBurnAt;
        private const float BurnInterval = 0.5f;
        private const float BurnPercentPerTick = 0.04f; // of max health — ~12s from full outside

        /// <summary>Every machine: burn the local ship while it is outside the safe zone. The wall
        /// itself is real terrain and hurts on contact through the game's own contact damage; this
        /// covers everything beyond the wall, so running past it is not an escape.</summary>
        public static void LocalTick(NetSession session)
        {
            if (!Active) return;
            TryRevealWholeMap();
            if (!RingKnown) return;
            var ship = ShipSync.LocalShip;
            if (ship == null || ship.IsDead) return;
            if (Time.unscaledTime < _nextBurnAt) return;
            _nextBurnAt = Time.unscaledTime + BurnInterval;

            Vector2 pos = ship.transform.position;
            float dist = Vector2.Distance(pos, new Vector2(Ring.CenterX, Ring.CenterY));
            if (dist <= Ring.SafeRadius) return;

            try
            {
                var unit = ship.GetComponentInParent<Unit>() ?? ship.GetComponent<Unit>();
                var dr = unit != null ? unit.GetComponent<DamagableResource>() : ship.GetComponent<DamagableResource>();
                var tank = dr != null ? dr.Tank : null;
                if (tank == null || tank.isInfinite || tank.Capacity <= 0f) return;
                float amount = Mathf.Max(1f, tank.Capacity * BurnPercentPerTick);
                dr.Damage(amount); // untyped chokepoint — our own ship, applied locally
                if (Time.frameCount % 120 == 0)
                    Plugin.Log.LogInfo($"[BR] outside the ring ({dist:0} > {Ring.SafeRadius:0}) — burning");
            }
            catch { }
        }

        // ---------------------------------------------------------------- spawn scatter

        /// <summary>slot -> station netId, computed identically on every machine (station layout is
        /// seed-deterministic, so no assignment needs to be sent). Farthest-point sampling: each
        /// player takes the station furthest from everyone already placed, and a station leaves the
        /// pool once taken — so no two players can ever share a shop.</summary>
        public static Dictionary<byte, int> AssignSpawnStations(NetSession session)
        {
            var result = new Dictionary<byte, int>();
            try
            {
                var em = ServiceLocator.Get<EntityManager>();
                if (em == null) return result;
                var stations = em.GetEntitiesWithComponent<Station.Data>()
                    .Where(d => d?.entity != null)
                    .OrderBy(d => d.entity.instanceId) // deterministic base order on every machine
                    .ToList();
                if (stations.Count == 0) return result;

                var slots = session.Players
                    .Where(p => p != null && p.Connected && !p.IsCoordinator)
                    .Select(p => p.Slot)
                    .OrderBy(s => s)
                    .ToList();
                if (slots.Count == 0) return result;

                var taken = new List<Vector2>();
                var pool = new List<Station.Data>(stations);
                foreach (byte slot in slots)
                {
                    if (pool.Count == 0)
                    {
                        Plugin.Log.LogWarning($"[BR] only {stations.Count} stations for {slots.Count} players — " +
                            $"slot {slot} has no distinct station");
                        break;
                    }
                    int pick = 0;
                    if (taken.Count == 0)
                    {
                        // Deterministic first pick from the run seed, so matches don't always open
                        // at the same corner of the map.
                        pick = Mathf.Abs(session.CurrentRunSeed % pool.Count);
                    }
                    else
                    {
                        float best = -1f;
                        for (int i = 0; i < pool.Count; i++)
                        {
                            Vector2 p = pool[i].entity.position;
                            float nearest = taken.Min(t => Vector2.Distance(t, p));
                            if (nearest <= best) continue;
                            best = nearest;
                            pick = i;
                        }
                    }
                    var chosen = pool[pick];
                    pool.RemoveAt(pick); // never reused — distinct stations by construction
                    Vector2 cp = chosen.entity.position;
                    taken.Add(cp);
                    if (NetIds.TryGetNetId(chosen.entity.instanceId, out int netId))
                    {
                        result[slot] = netId;
                        float nearestPeer = taken.Count > 1
                            ? taken.Take(taken.Count - 1).Min(t => Vector2.Distance(t, cp))
                            : 0f;
                        Plugin.Log.LogInfo($"[BR] spawn slot {slot} -> station #{netId} at " +
                            $"({cp.x:0},{cp.y:0}), nearest peer {nearestPeer:0} units");
                    }
                }
            }
            catch (System.Exception e) { Plugin.Log.LogWarning($"[BR] spawn assignment failed: {e.Message}"); }
            return result;
        }

        private static bool _scattered;
        private static float _scatterAt;

        /// <summary>Arm the scatter: the vanilla start cinematic freezes ships and pans the camera,
        /// so the teleport waits until control is back rather than fighting it.</summary>
        public static void ArmScatter() { _scattered = false; _scatterAt = Time.unscaledTime + 3f; }

        public static void TickScatter(NetSession session)
        {
            if (_scattered || !Active || Time.unscaledTime < _scatterAt) return;
            var ship = ShipSync.LocalShip;
            if (ship == null) return;
            _scattered = true;
            var assignment = AssignSpawnStations(session);
            if (!assignment.TryGetValue((byte)session.LocalSlot, out int stationNetId)) return;
            ShipSync.TeleportLocalShip(stationNetId);
            Plugin.Log.LogInfo($"[BR] scattered to station #{stationNetId}");
        }

        // ---------------------------------------------------------------- match rules (local)

        /// <summary>Apply the per-machine parts of the ruleset once the run is live: a fully
        /// stocked shop, no starting money, and (defensively) the standard loadout's economy
        /// baseline. Everything here is local state on each machine, so each applies its own.</summary>
        public static void ApplyLocalMatchRules()
        {
            RevealWholeMap();

            try
            {
                var runData = ServiceLocator.Get<RunData>();
                if (runData == null) return;
                // Every upgrade purchasable from minute one — BR has no unlock progression, but
                // prices stay normal so gold still matters.
                Traverse.Create(runData).Method("AddAllItemsToShop").GetValue();
                Plugin.Log.LogInfo("[BR] shop fully stocked");
            }
            catch (System.Exception e) { Plugin.Log.LogWarning($"[BR] shop stock failed: {e.Message}"); }

            try
            {
                // Vanilla already starts a run at zero currency; make it explicit so a future
                // change to starting money can't quietly hand BR players a head start.
                var runData = ServiceLocator.Get<RunData>();
                var tanks = runData != null
                    ? Traverse.Create(runData).Property("SharedResourceTanks").GetValue() as IEnumerable<ResourceTank>
                    : null;
                if (tanks == null) return;
                foreach (var tank in tanks) if (tank != null) tank.Value = 0f;
                Plugin.Log.LogInfo("[BR] starting currency zeroed");
            }
            catch { /* vanilla default is already zero */ }
        }

        /// <summary>No hidden ground in Battle Royale: the whole map is readable from the first
        /// second so players can plan a route to the next ring instead of exploring blind. The
        /// game already has this exact operation for its own scanners — DiscoverWholeMap — so BR
        /// just calls it once per machine at match start rather than faking a reveal.</summary>
        private static bool _mapRevealed;
        private static float _nextRevealTryAt;

        private static void RevealWholeMap()
        {
            _mapRevealed = false;
            _nextRevealTryAt = 0f;
            TryRevealWholeMap();
        }

        /// <summary>Retried until it lands: at go-live the map objects may not exist yet, and a
        /// one-shot attempt that quietly missed would leave players exploring blind for the whole
        /// match with nothing in the log to explain it.</summary>
        private static void TryRevealWholeMap()
        {
            if (_mapRevealed || Time.unscaledTime < _nextRevealTryAt) return;
            _nextRevealTryAt = Time.unscaledTime + 1f;
            try
            {
                var drawer = Object.FindFirstObjectByType<MapDrawer>();
                if (drawer == null) return; // not loaded yet — try again next second
                drawer.DiscoverWholeMap();
                _mapRevealed = true;
                Plugin.Log.LogInfo("[BR] whole map revealed (no fog of war)");
            }
            catch (System.Exception e)
            {
                _mapRevealed = true; // don't spin on a real failure
                Plugin.Log.LogWarning($"[BR] map reveal failed: {e.Message}");
            }
        }

        // ---------------------------------------------------------------- HUD

        private static GUIStyle _hudStyle;

        /// <summary>Match clock, ring state, and how many players are left. Drawn on every machine
        /// from the host's last ring broadcast.</summary>
        public static void DrawHud()
        {
            if (!Active || !RingKnown) return;
            if (_hudStyle == null)
                _hudStyle = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.UpperCenter,
                    fontSize = 15,
                    fontStyle = FontStyle.Bold,
                };

            int remain = Mathf.CeilToInt(Ring.MatchRemaining);
            string clock = $"{remain / 60:00}:{remain % 60:00}";
            string ring = Ring.Stage == 0
                ? $"RING HOLDS — CLOSES IN {Mathf.CeilToInt(Ring.NextShrinkIn)}s"
                : $"RING CLOSING {Ring.Stage}/{Ring.TotalStages} — SAFE RADIUS {Ring.SafeRadius:0}";

            var ship = ShipSync.LocalShip;
            bool outside = ship != null && !ship.IsDead
                && Vector2.Distance(ship.transform.position, new Vector2(Ring.CenterX, Ring.CenterY)) > Ring.SafeRadius;

            _hudStyle.normal.textColor = outside ? new Color(1f, 0.35f, 0.25f) : Color.white;
            var rect = new Rect(Screen.width * 0.5f - 300f, 8f, 600f, 44f);
            GUI.Label(rect, $"{clock}   {ring}", _hudStyle);
            if (outside)
                GUI.Label(new Rect(rect.x, rect.y + 20f, rect.width, 24f),
                    "OUTSIDE THE RING — GET BACK IN", _hudStyle);
        }
    }
}
