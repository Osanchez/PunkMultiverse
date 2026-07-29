using HarmonyLib;
using PunkMultiverse.Core;
using UnityEngine;

namespace PunkMultiverse.Patches
{
    /// <summary>
    /// Answers ONE question, because four rounds of "PvP still does not work" have been argued from
    /// inference instead of measurement: WHERE does a shot aimed at another player stop?
    ///
    /// A player's bullet has to survive four gates before the victim loses health, and each one
    /// fails silently:
    ///   1. the CircleCast in Projectile.FixedUpdate has to actually return the other ship's
    ///      collider (layer mask, colliders present and enabled)
    ///   2. Owner.IsFriendsWith(victim) has to be FALSE (Patches/BattleRoyalePvP.cs flips it)
    ///   3. ProjectileSync's damage prefix has to let the hit through to the victim's health
    ///   4. DamageSync has to route it to the owner, who applies it
    ///
    /// A miss at 1 and a miss at 4 look identical from the outside: nothing happens. So this counts
    /// each gate separately and prints one line, and the answer is then a fact rather than a theory.
    /// Always on in a BR match (a handful of counter increments), and it reports only when a local
    /// ship has actually been shooting.
    /// </summary>
    internal static class PvPDiag
    {
        private static int _castsAtShip;    // gate 1: a cast returned a ship collider
        private static int _friendFlips;    // gate 2: we made two ships hostile
        private static int _damagePrefix;   // gate 3: reached the projectile-damage prefix on a ship
        private static int _routed;         // gate 4: a DamageRequest was actually sent at a player
        private static float _nextReportAt;
        private static bool _dirty;

        private static int _playerProjTicks;  // gate 0: player-owned PROJECTILES exist and are ticking
        private static int _playerHitscans;   // gate 0b: player-owned HITSCAN shots were fired

        internal static void NotePlayerProjectile() { _playerProjTicks++; _dirty = true; }
        internal static void NotePlayerHitscan() { _playerHitscans++; _dirty = true; }
        internal static void NoteCastAtShip() { _castsAtShip++; _dirty = true; }
        internal static void NoteFriendFlip() { _friendFlips++; _dirty = true; }
        internal static void NoteDamagePrefix() { _damagePrefix++; _dirty = true; }
        internal static void NoteRouted() { _routed++; _dirty = true; }

        private static bool _dumped;

        internal static void Reset()
        {
            _castsAtShip = _friendFlips = _damagePrefix = _routed = 0;
            _playerProjTicks = _playerHitscans = 0;
            _dirty = false;
            _dumped = false;
            WatchProjectileHits.ResetLog();
            _nextReportAt = 0f;
        }

        /// <summary>What a player's bullets ACTUALLY hit. Proximity counting cannot answer this —
        /// a fast projectile steps past a ship between two FixedUpdates without ever sampling near
        /// it, which is precisely why the real code sweeps with a CircleCast instead of sampling.
        /// This sits on the branch that consumes a hit, so it reports the truth: terrain, an enemy,
        /// the shooter's own hull, or the other player.</summary>
        [HarmonyPatch(typeof(Projectile), "OnObjectHit")]
        internal static class WatchProjectileHits
        {
            private static int _logged;
            internal static void ResetLog() { _logged = 0; }

            private static void Prefix(Projectile __instance, RaycastHit2D __0)
            {
                if (_logged >= 6 || !Modes.BattleRoyale.Active || __instance == null) return;
                try
                {
                    var owner = __instance.Owner;
                    if (owner == null) return;
                    if (owner.GetComponentInParent<Ship>() == null
                        && owner.GetComponentInChildren<Ship>() == null) return;
                    var col = __0.collider;
                    if (col == null) return;
                    _logged++;
                    var hitShip = col.GetComponentInParent<Ship>();
                    var hitUnit = __0.rigidbody != null ? __0.rigidbody.GetComponent<Unit>() : null;
                    Plugin.Log.LogInfo($"[PvPDiag] player bullet HIT '{col.gameObject.name}' " +
                        $"layer={col.gameObject.layer}({LayerMask.LayerToName(col.gameObject.layer)}) " +
                        $"ship={(hitShip == null ? "no" : hitShip.name)} " +
                        $"rbUnit={(hitUnit == null ? "none" : "yes")} at " +
                        $"({__0.point.x:0.#},{__0.point.y:0.#})");
                }
                catch { }
            }
        }

        /// <summary>The one measurement that distinguishes "the mask excludes them", "they have no
        /// colliders" and "they are somewhere else entirely".</summary>
        private static void DumpPhysics(Projectile proj, Ship target, Vector2 projPos)
        {
            _dumped = true;
            try
            {
                int mask = 0;
                try { mask = Traverse.Create(proj).Field("collisionLayerMask").GetValue<LayerMask>().value; }
                catch { }
                float radius = 0f;
                try { radius = proj.Radius; } catch { }

                var cols = target.GetComponentsInChildren<Collider2D>(true);
                var sb = new System.Text.StringBuilder();
                foreach (var c in cols)
                {
                    if (c == null) continue;
                    int layer = c.gameObject.layer;
                    bool inMask = (mask & (1 << layer)) != 0;
                    sb.Append($"[{c.GetType().Name} on '{c.gameObject.name}' layer={layer}" +
                              $"({LayerMask.LayerToName(layer)}) enabled={c.enabled} " +
                              $"trigger={c.isTrigger} inMask={inMask}] ");
                }
                var rb = target.GetComponent<Rigidbody2D>();
                Plugin.Log.LogInfo($"[PvPDiag] PHYSICS PICTURE — projectile at ({projPos.x:0.#},{projPos.y:0.#}) " +
                    $"radius={radius:0.##} mask=0x{mask:X} | target ship '{target.name}' at " +
                    $"({target.transform.position.x:0.#},{target.transform.position.y:0.#}) " +
                    $"rootLayer={target.gameObject.layer}({LayerMask.LayerToName(target.gameObject.layer)}) " +
                    $"rb={(rb == null ? "NONE" : rb.bodyType + " simulated=" + rb.simulated)} " +
                    $"colliders={cols.Length} {sb}");
            }
            catch (System.Exception e) { Plugin.Log.LogWarning($"[PvPDiag] physics dump failed: {e.Message}"); }
        }

        /// <summary>Called from the session tick. Prints the ladder while anything is happening.</summary>
        internal static void Tick()
        {
            if (!_dirty || Time.unscaledTime < _nextReportAt) return;
            _nextReportAt = Time.unscaledTime + 2f;
            _dirty = false;
            Plugin.Log.LogInfo($"[PvPDiag] playerProjTicks={_playerProjTicks} playerHitscans={_playerHitscans} " +
                $"castHitShip={_castsAtShip} friendFlip={_friendFlips} " +
                $"damagePrefix={_damagePrefix} routedAtPlayer={_routed} " +
                "(the first ZERO from the left is where player-vs-player shots die)");
        }

        /// <summary>Gate 1. The same CircleCast Projectile.FixedUpdate runs, observed: did the
        /// projectile's sweep return a collider belonging to a SHIP that is not its owner? If this
        /// stays 0 while a player is firing at another player, nothing downstream can ever run and
        /// the fault is the cast itself — layer mask or colliders — not the friend check.</summary>
        [HarmonyPatch(typeof(Projectile), "FixedUpdate")]
        internal static class WatchProjectileSweep
        {
            private static void Postfix(Projectile __instance)
            {
                if (!Modes.BattleRoyale.Active || __instance == null) return;
                try
                {
                    var owner = __instance.Owner;
                    if (owner == null) return;
                    if (owner.GetComponentInParent<Ship>() == null
                        && owner.GetComponentInChildren<Ship>() == null) return; // not a player's shot
                    NotePlayerProjectile(); // gate 0: this IS a player's projectile, and it is alive
                    // Cheap proximity test rather than re-running the cast: is any OTHER ship within
                    // a projectile radius of where this bullet now is? If yes, the bullet is passing
                    // through a ship it should have hit.
                    var sm = ServiceLocator.Get<ShipManager>();
                    if (sm == null) return;
                    Vector2 p = __instance.transform.position;
                    foreach (var ship in sm.Ships)
                    {
                        if (ship == null || ship.Unit == owner) continue;
                        float d2 = ((Vector2)ship.transform.position - p).sqrMagnitude;
                        if (d2 <= 4f) NoteCastAtShip();
                        // ONE full physical picture per match, taken while a player's bullet is
                        // genuinely near another player's ship. Everything above only counts
                        // events; this says WHY the count is zero — whether the target has
                        // colliders at all, what layer they are on, and whether the projectile's
                        // own collision mask even includes that layer.
                        if (!_dumped && d2 <= 400f) DumpPhysics(__instance, ship, p);
                    }
                }
                catch { }
            }
        }
    }
}
