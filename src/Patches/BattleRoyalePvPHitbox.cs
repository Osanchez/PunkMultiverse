using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace PunkMultiverse.Patches
{
    /// <summary>
    /// A SQUARE aim-assist hitbox around every other player's ship, for player-vs-player fire only
    /// (Omar, 2026-07-30: "make the hitbox for the ships being attacked by other ships bigger... a
    /// square hitbox around the players, slightly larger than their ship, not enough to make it
    /// seem like shots are easy").
    ///
    /// WHY SQUARE. Vanilla hit detection is true to the silhouette, and a PUNK ship is not
    /// symmetrical — it is wider than it is tall. So the same shot that lands comfortably from the
    /// side sails over the top from head-on, and the player cannot tell which case they were in.
    /// An axis-aligned square is the same size from every direction: the margin you learn on one
    /// approach is the margin you get on all of them.
    ///
    /// WHAT IT IS NOT. It does not replace or resize any collider. The ship's real hull is
    /// untouched and still hit-tests exactly as before, so this can only ever ADD a hit that was a
    /// near-miss — it can never take one away, and it cannot desync anything, because nothing about
    /// the ship's physical presence changed. It is narrow on purpose:
    ///
    ///   * only in a live Battle Royale match (a friendly-fire co-op lobby keeps true-to-shape —
    ///     making it EASIER to hit a teammate by accident is not an assist),
    ///   * only for a projectile or beam that came out of a player's ship,
    ///   * only against OTHER player ships — never the shooter, never an enemy, never terrain,
    ///   * and never through cover: if anything real would be struck at or before the square, the
    ///     shot is handed straight back to vanilla and stops there.
    ///
    /// HOW THE HIT IS MADE REAL. The square only decides WHETHER the shot connects. To actually
    /// deliver it, a short ray is fired from the square's entry point at the ship's centre to pick
    /// up a genuine <c>RaycastHit2D</c> on the real hull, and vanilla's own impact handler is then
    /// called with it. Damage, knockback, piercing, repeat delays, impact effects, explosions and
    /// the whole of the mod's damage routing (trace, attribution, the BR PvP scale) therefore run
    /// exactly as they do for an ordinary hit — none of it is re-implemented here, which is the
    /// point. If no hull can be found the assist stands down rather than inventing a hit.
    /// </summary>
    internal static class BattleRoyalePvPHitbox
    {
        /// <summary>Battle Royale only — see the class remark on friendly-fire co-op.</summary>
        private static bool Enabled => Modes.BattleRoyale.Active && ScaleValue > 1f;

        private static float ScaleValue =>
            NetConfig.PvpHitboxScale != null ? NetConfig.PvpHitboxScale.Value : 0f;

        private static float MaxMargin =>
            NetConfig.PvpHitboxMaxUnits != null ? Mathf.Max(0f, NetConfig.PvpHitboxMaxUnits.Value) : 0f;

        // ---------------------------------------------------------------- the square

        private sealed class Square
        {
            internal float Half;          // half-extent of the assist square, world units
            internal float HullHalf;      // the ship's own largest half-extent, for the log
            internal Vector2 LocalCentre; // hull centre in the ship's local space (ships rotate)
            internal float BuiltAt;
        }

        // Keyed by ship instance id. A ship's parts stream in after it spawns, so the measurement
        // is refreshed rather than taken once — but not per shot: this is on the projectile path.
        private static readonly Dictionary<int, Square> Squares = new Dictionary<int, Square>();
        private const float RebuildSeconds = 2f;
        private static bool _loggedSize;

        internal static void Reset() { Squares.Clear(); _loggedSize = false; }

        private static bool TryGetSquare(Ship ship, out Vector2 centre, out float half)
        {
            centre = default; half = 0f;
            if (ship == null) return false;
            int id = ship.GetInstanceID();
            if (!Squares.TryGetValue(id, out var sq) || Time.unscaledTime - sq.BuiltAt > RebuildSeconds)
            {
                sq = Measure(ship);
                Squares[id] = sq;
            }
            if (sq.Half <= 0f) return false;
            centre = ship.transform.TransformPoint(sq.LocalCentre);
            half = sq.Half;
            return true;
        }

        private static Square Measure(Ship ship)
        {
            var sq = new Square { BuiltAt = Time.unscaledTime };
            try
            {
                int counted = 0;
                Bounds b = default;
                foreach (var col in ship.GetComponentsInChildren<Collider2D>(true))
                {
                    if (col == null || col.isTrigger || !col.enabled) continue;
                    if (counted++ == 0) b = col.bounds;
                    else b.Encapsulate(col.bounds);
                }
                if (counted == 0) return sq;

                float hull = Mathf.Max(b.extents.x, b.extents.y);
                if (hull <= 0f) return sq;
                // Two limits, whichever binds first: a proportional one so the assist scales with
                // the ship, and an absolute one in world units so it stays a margin rather than a
                // barn door on a large ship.
                float half = Mathf.Min(hull * ScaleValue, hull + MaxMargin);
                sq.HullHalf = hull;
                sq.Half = Mathf.Max(half, hull);   // never smaller than the ship it wraps
                sq.LocalCentre = ship.transform.InverseTransformPoint(b.center);

                if (!_loggedSize)
                {
                    _loggedSize = true;
                    // The measurement, not an assumption: a ship's hull turned out to be far
                    // smaller than it looks (0.70x0.70u), which is why the first scale that seemed
                    // sensible added a margin of 0.05u and changed nothing anyone could feel.
                    // Tune PvpHitboxScale against THIS line, not against the sprite.
                    Plugin.Log.LogInfo($"[PvP] square hitbox: '{ship.name}' hull {b.size.x:0.00}x{b.size.y:0.00}u " +
                        $"from {counted} collider(s) -> {sq.Half * 2f:0.00}u square (scale {ScaleValue:0.00}, " +
                        $"cap +{MaxMargin:0.00}u, margin +{sq.Half - hull:0.00}u per side). PvP only; the real " +
                        "hull still hit-tests as before, so this can only add near-misses.");
                }
            }
            catch { }
            return sq;
        }

        /// <summary>Slab test: does the swept segment enter another player's square, and where?
        /// Nearest square wins, so a shot lined up on two ships hits the closer one.</summary>
        private static bool TryFindTarget(Ship shooter, Vector2 from, Vector2 dir, float length,
            out Ship target, out float entry, out Vector2 edge)
        {
            target = null; entry = 0f; edge = from;
            ShipManager sm = null;
            try { sm = ServiceLocator.Get<ShipManager>(); } catch { }
            if (sm == null || sm.Ships == null) return false;

            float best = float.MaxValue;
            for (int i = 0; i < sm.Ships.Count; i++)
            {
                var ship = sm.Ships[i];
                if (ship == null || ship == shooter) continue;
                if (!TryGetSquare(ship, out var centre, out float half)) continue;
                if (!SegmentSquare(from, dir, length, centre, half, out float t, out var point)) continue;
                if (t >= best) continue;
                best = t; target = ship; entry = t; edge = point;
            }
            return target != null;
        }

        private static bool SegmentSquare(Vector2 origin, Vector2 dir, float length,
            Vector2 centre, float half, out float entry, out Vector2 point)
        {
            entry = 0f; point = origin;
            float tMin = 0f, tMax = length;
            for (int axis = 0; axis < 2; axis++)
            {
                float o = axis == 0 ? origin.x : origin.y;
                float d = axis == 0 ? dir.x : dir.y;
                float lo = (axis == 0 ? centre.x : centre.y) - half;
                float hi = (axis == 0 ? centre.x : centre.y) + half;
                if (Mathf.Abs(d) < 1e-6f)
                {
                    if (o < lo || o > hi) return false;   // parallel and outside this slab
                    continue;
                }
                float t1 = (lo - o) / d, t2 = (hi - o) / d;
                if (t1 > t2) { float swap = t1; t1 = t2; t2 = swap; }
                if (t1 > tMin) tMin = t1;
                if (t2 < tMax) tMax = t2;
                if (tMin > tMax) return false;
            }
            entry = tMin;
            point = origin + dir * tMin;
            return true;
        }

        /// <summary>Should the assist keep out of this shot? Two reasons it must, and they are the
        /// whole safety story:
        ///
        ///   COVER — something real is struck at or before the square. The shot stops there, and
        ///   vanilla is the one that says so. Nothing is ever hit through a wall.
        ///
        ///   IT ALREADY HITS — the sweep reaches the target's own hull. This is the common case in a
        ///   well-aimed burst, and letting the assist take those over would be wrong twice: the
        ///   impact would be repositioned off the true line of fire, and the diagnostic count of
        ///   assisted shots would report every hit instead of the near-misses it exists to measure.
        ///
        /// So the assist fires on exactly one case: the swept shot clips the square, and vanilla's
        /// own sweep finds nothing in front of it — a miss, by a margin smaller than the box.</summary>
        private static bool StandDown(Vector2 from, Vector2 dir, float length, float radius,
            LayerMask mask, Ship shooter, Ship target, float entry)
        {
            var hits = Physics2D.CircleCastAll(from, radius, dir, length, mask);
            for (int i = 0; i < hits.Length; i++)
            {
                var col = hits[i].collider;
                if (col == null) continue;
                // The muzzle sits inside the shooter's own silhouette, so its hull is on every
                // sweep at distance ~0 — see BattleRoyalePvP.ShotsDoNotDetonateOnTheirOwnHull.
                var ship = col.GetComponentInParent<Ship>();
                if (shooter != null && ship == shooter) continue;
                if (hits[i].distance <= entry) return true;   // cover
                if (ship == target) return true;              // vanilla lands it on the real hull
            }
            return false;
        }

        /// <summary>Turn "the square was entered" into a genuine hit on the real hull. The hull is
        /// inside the square by construction, so a ray from the entry point at the ship's centre
        /// crosses it.</summary>
        private static bool TryHullHit(Ship target, Vector2 from, out RaycastHit2D hit)
        {
            hit = default;
            int mask = BattleRoyalePvP.ShipLayers();
            if (mask == 0) return false;
            Vector2 centre = target.transform.position;
            Vector2 d = centre - from;
            float len = d.magnitude;
            if (len < 1e-4f) { d = Vector2.up; len = 1f; } else d /= len;
            var hits = Physics2D.RaycastAll(from, d, len + 1f, mask);
            for (int i = 0; i < hits.Length; i++)
            {
                var col = hits[i].collider;
                if (col == null || col.isTrigger) continue;
                if (col.GetComponentInParent<Ship>() != target) continue;
                hit = hits[i];
                return hit.collider != null;
            }
            return false;
        }

        private static float _nextMissLogAt;

        private static void NoteNoHull(Ship target)
        {
            if (Time.unscaledTime < _nextMissLogAt) return;
            _nextMissLogAt = Time.unscaledTime + 10f;
            Plugin.Log.LogWarning($"[PvP] square hitbox entered on '{target.name}' but no hull collider " +
                "could be found to hit — the assist stood down and vanilla decided this shot. If this " +
                "repeats, the ship's colliders are not where its bounds say they are.");
        }

        internal static Ship ShipOf(Unit unit) => unit == null
            ? null
            : (unit.GetComponentInParent<Ship>() ?? unit.GetComponentInChildren<Ship>());

        // ---------------------------------------------------------------- direct fire

        /// <summary>Runs before vanilla's own sweep. If the tick's travel would clip another
        /// player's square with nothing in the way, vanilla's <c>OnObjectHit</c> is called with a
        /// real hull hit and this tick is consumed; in every other case vanilla runs untouched.</summary>
        [HarmonyPatch(typeof(Projectile), "FixedUpdate")]
        internal static class SquareHitboxForPlayerShots
        {
            private static System.Reflection.MethodInfo _onObjectHit;

            private static bool Prefix(Projectile __instance)
            {
                if (!Enabled || __instance == null) return true;
                try
                {
                    var shooter = ShipOf(__instance.Owner);
                    if (shooter == null) return true;            // enemy fire keeps vanilla exactly

                    Vector2 v = __instance.Velocity;
                    float speed = v.magnitude;
                    if (speed <= 1e-4f) return true;
                    float length = speed * Time.deltaTime;
                    if (length <= 0f) return true;
                    Vector2 dir = v / speed;
                    Vector2 from = __instance.transform.position;

                    if (!TryFindTarget(shooter, from, dir, length, out var target, out float entry, out var edge))
                        return true;

                    LayerMask mask = Traverse.Create(__instance).Field("collisionLayerMask").GetValue<LayerMask>();
                    if (StandDown(from, dir, length, __instance.Radius, mask, shooter, target, entry)) return true;

                    if (!TryHullHit(target, edge, out var hit)) { NoteNoHull(target); return true; }

                    // Vanilla's own impact path — damage, piercing, knockback, effects, explosion,
                    // destruction, and every mod patch layered on it. Nothing is duplicated here.
                    if (_onObjectHit == null)
                        _onObjectHit = AccessTools.Method(typeof(Projectile), "OnObjectHit");
                    if (_onObjectHit == null) return true;
                    PvPDiag.NoteSquareAssist();
                    _onObjectHit.Invoke(__instance, new object[] { hit });
                    return false;
                }
                catch { return true; }
            }
        }

        // ---------------------------------------------------------------- hitscan beams

        /// <summary>Beams get the same square. <c>HitscanWeapon</c> carries no owner, so the
        /// shooter is identified physically: a player's barrel sits INSIDE that player's own hull
        /// (the same fact that makes a bullet's first sweep return the shooter — see
        /// BattleRoyalePvP). A beam whose origin is not inside a player ship is an enemy's and is
        /// left alone.</summary>
        [HarmonyPatch(typeof(HitscanWeapon), "FireSingle")]
        internal static class SquareHitboxForPlayerBeams
        {
            private static bool Prefix(HitscanWeapon __instance, Vector2 __0, Vector2 __1)
            {
                if (!Enabled || __instance == null) return true;
                try
                {
                    var shooter = ShooterAt(__0);
                    if (shooter == null) return true;            // not a player's beam

                    Vector2 dir = __1.normalized;
                    if (dir.sqrMagnitude < 1e-6f) return true;
                    float range = __instance.Range;
                    if (range <= 0f) return true;

                    if (!TryFindTarget(shooter, __0, dir, range, out var target, out float entry, out var edge))
                        return true;
                    if (StandDown(__0, dir, range, __instance.RayWidth, __instance.LayerMask,
                            shooter, target, entry))
                        return true;
                    if (!TryHullHit(target, edge, out var hit)) { NoteNoHull(target); return true; }

                    DeliverBeam(__instance, hit, dir);
                    PvPDiag.NoteSquareAssist();
                    return false;   // the beam stopped on this ship; vanilla would go no further
                }
                catch { return true; }
            }

            /// <summary>Vanilla's FireSingle tail, against a hit we already have. Kept faithful
            /// line for line — including the private repeat-delay ledger, so a beam's damage
            /// cadence through the assist is the cadence it has through the hull.</summary>
            private static void DeliverBeam(HitscanWeapon weapon, RaycastHit2D hit, Vector2 dir)
            {
                var ledger = Traverse.Create(weapon).Field("lastDamageTimes")
                    .GetValue() as Dictionary<IHitscanWeaponListener, float>;

                var listeners = hit.collider.GetComponents<IHitscanWeaponListener>();
                if (listeners != null && listeners.Length > 0)
                {
                    foreach (var listener in listeners) Hit(weapon, listener, hit, ledger);
                }
                else
                {
                    Hit(weapon, hit.collider.GetComponentInParent<IHitscanWeaponListener>(), hit, ledger);
                }

                if (hit.collider.attachedRigidbody != null)
                    hit.collider.attachedRigidbody.AddForceAtPosition(
                        dir * weapon.PushForce, hit.point, ForceMode2D.Impulse);
                if (weapon.CellConvertData.enabled)
                    ServiceLocator.Get<Level>().ConvertCells(weapon.CellConvertData, hit.point);
            }

            private static void Hit(HitscanWeapon weapon, IHitscanWeaponListener listener,
                RaycastHit2D hit, Dictionary<IHitscanWeaponListener, float> ledger)
            {
                if (listener == null) return;
                if (ledger != null && ledger.TryGetValue(listener, out float last)
                    && Time.time - last <= weapon.DamageRepeatDelay) return;
                listener.OnHitByHitscanWeapon(weapon, hit);
                if (ledger != null) ledger[listener] = Time.time;
            }

            /// <summary>The player ship this shot came out of, or null. Physical, not inferred from
            /// distance: the origin has to be inside a ship's own hull collider.</summary>
            private static Ship ShooterAt(Vector2 origin)
            {
                int mask = BattleRoyalePvP.ShipLayers();
                if (mask == 0) return null;
                var cols = Physics2D.OverlapPointAll(origin, mask);
                for (int i = 0; i < cols.Length; i++)
                {
                    var ship = cols[i] != null ? cols[i].GetComponentInParent<Ship>() : null;
                    if (ship != null) return ship;
                }
                return null;
            }
        }
    }
}
