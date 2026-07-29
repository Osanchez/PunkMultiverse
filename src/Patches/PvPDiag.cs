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
        private static int _selfHits;       // gate 0c: shots that struck the hull that fired them
        private static int _friendFlips;    // gate 2: we made two ships hostile
        private static int _damagePrefix;   // gate 3: reached the projectile-damage prefix on a ship
        private static int _routed;         // gate 4: a DamageRequest was actually sent at a player
        private static float _nextReportAt;
        private static bool _dirty;

        private static int _playerProjTicks;  // gate 0: player-owned PROJECTILES exist and are ticking
        private static int _playerHitscans;   // gate 0b: player-owned HITSCAN shots were fired

        // Physical facts about the most recent player-owned projectile, for `pvpprobe`. Read from a
        // live bullet rather than guessed from layer names: the projectile layer is a prefab detail
        // and the mask is whatever Projectile.Shoot computed AFTER the widening postfix ran, which
        // is the only number that decides whether a sweep can return a ship.
        internal static int LastProjectileLayer = -1;
        internal static int LastProjectileMask;
        internal static float LastProjectileRadius;

        internal static void NotePlayerProjectile() { _playerProjTicks++; _dirty = true; }
        internal static void NotePlayerHitscan() { _playerHitscans++; _dirty = true; }
        internal static void NoteCastAtShip() { _castsAtShip++; _dirty = true; }
        internal static void NoteSelfHit() { _selfHits++; _dirty = true; }
        internal static void NoteFriendFlip() { _friendFlips++; _dirty = true; }
        internal static void NoteDamagePrefix() { _damagePrefix++; _dirty = true; }
        internal static void NoteRouted() { _routed++; _dirty = true; }

        private static bool _dumped;

        internal static void Reset()
        {
            _castsAtShip = _friendFlips = _damagePrefix = _routed = _selfHits = 0;
            _playerProjTicks = _playerHitscans = 0;
            LastProjectileLayer = -1;
            LastProjectileMask = 0;
            LastProjectileRadius = 0f;
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
            private static float _nextHitLogAt;
            internal static void ResetLog() { _logged = 0; _nextHitLogAt = 0f; }

            private static void Prefix(Projectile __instance, RaycastHit2D __0)
            {
                // NOT gated on _logged: the log line is capped at 6, the COUNTER must not be.
                if (!BattleRoyalePvP.PlayersCanHitPlayers || __instance == null) return;
                try
                {
                    var owner = __instance.Owner;
                    if (owner == null) return;
                    if (owner.GetComponentInParent<Ship>() == null
                        && owner.GetComponentInChildren<Ship>() == null) return;
                    var col = __0.collider;
                    if (col == null) return;
                    var hitShip = col.GetComponentInParent<Ship>();
                    // GATE 1, measured properly. The old counter was a proximity test — "is any other
                    // ship within 2 units of this bullet" — which a genuine hit FAILS: a bullet
                    // detonates on the hull, several units out from the ship's transform origin, and
                    // the projectile is destroyed before it ever gets that close to the centre. It
                    // could therefore read 0 through a match in which every shot connected. This
                    // counts the only thing that means "the sweep returned another player's ship":
                    // a consumed hit whose collider actually belongs to one.
                    if (hitShip != null && owner.GetComponentInParent<Ship>() != hitShip
                        && owner.GetComponentInChildren<Ship>() != hitShip) NoteCastAtShip();
                    // Throttled, not capped at six for the whole match: the six were always spent in
                    // the first seconds on scenery, so by the time a staged PvP burst ran there was
                    // nothing left to log and every run reported the same silent zero.
                    if (Time.unscaledTime < _nextHitLogAt) return;
                    _nextHitLogAt = Time.unscaledTime + 0.35f;
                    _logged++;
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
                $"selfHitsSkipped={_selfHits} " +
                $"hitAnotherShip={_castsAtShip} friendFlip={_friendFlips} " +
                $"damagePrefix={_damagePrefix} routedAtPlayer={_routed} " +
                "(the first ZERO from the left is where player-vs-player shots die; " +
                "run `pvpprobe` for the physical reason)");
        }

        /// <summary>
        /// The decisive measurement, on demand: can a player's bullet physically reach another
        /// player's ship from where I am standing, right now?
        ///
        /// Every previous round of this investigation inferred the answer from counters that could
        /// read zero for innocent reasons, and from a bot rig that buried its ships in terrain and
        /// detonated every shot at the muzzle. This asks the physics engine directly, using the mask
        /// and radius taken off a real bullet, and reports what the sweep returns IN ORDER — so
        /// "terrain is in the way" and "the ship is not castable" stop being the same observation.
        /// </summary>
        internal static string Probe()
        {
            var local = Sync.ShipSync.LocalShip;
            if (local == null) return "pvpprobe: no local ship";
            var sm = ServiceLocator.Get<ShipManager>();
            if (sm == null) return "pvpprobe: no ShipManager";

            var sb = new System.Text.StringBuilder();
            int shipLayers = BattleRoyalePvP.ShipLayers();
            int mask = LastProjectileMask;
            float radius = LastProjectileRadius > 0f ? LastProjectileRadius : 0.1f;
            string maskSource = "live bullet";
            if (mask == 0)
            {
                // Nobody has fired yet. Fall back to the matrix value the projectile WOULD get,
                // using the layer a player bullet was last seen on.
                int layer = LastProjectileLayer >= 0 ? LastProjectileLayer : local.gameObject.layer;
                mask = Physics2D.GetLayerCollisionMask(layer);
                maskSource = $"matrix for layer {layer} (no bullet seen yet — FIRE ONCE then re-run)";
            }
            sb.AppendLine($"pvpprobe: mode={(Modes.BattleRoyale.Active ? "BattleRoyale" : "Standard")} " +
                $"playersCanHitPlayers={BattleRoyalePvP.PlayersCanHitPlayers} " +
                $"friendlyFire={(Core.NetSession.Instance != null && Core.NetSession.Instance.FriendlyFire)}");
            sb.AppendLine($"  projectile: layer={LastProjectileLayer} radius={radius:0.###} " +
                $"mask=0x{mask:X} ({maskSource})");
            sb.AppendLine($"  ship layers=0x{shipLayers:X} -> " +
                (shipLayers != 0 && (mask & shipLayers) == shipLayers
                    ? "IN MASK (a sweep can return a ship)"
                    : "*** NOT IN MASK — bullets pass straight through players ***"));

            // What the shooter is actually holding. A hitscan beam has a hard RANGE from its data
            // asset, and a target beyond it is simply never reached — indistinguishable, from the
            // counters alone, from a mask or faction failure. Reported so "the shot did not land"
            // can be read as "the shot could not have landed".
            float longestReach = 0f;
            foreach (var field in new[] { "primaryWeaponHolder", "secondaryWeaponHolder" })
            {
                try
                {
                    var holder = Traverse.Create(local).Field(field).GetValue() as WeaponHolder;
                    var weapon = holder != null ? holder.Weapon : null;
                    if (weapon == null) { sb.AppendLine($"  {field}: none"); continue; }
                    if (weapon is HitscanWeapon hs)
                    {
                        int hsMask = hs.LayerMask.value;
                        if (hs.Range > longestReach) longestReach = hs.Range;
                        sb.AppendLine($"  {field}: HITSCAN range={hs.Range:0.#} rayWidth={hs.RayWidth:0.##} " +
                            $"mask=0x{hsMask:X} shipsInMask={(shipLayers != 0 && (hsMask & shipLayers) == shipLayers)}");
                    }
                    else sb.AppendLine($"  {field}: {weapon.GetType().Name} (projectile-based)");
                }
                catch (System.Exception e) { sb.AppendLine($"  {field}: read failed ({e.Message})"); }
            }

            int others = 0;
            foreach (var ship in sm.Ships)
            {
                if (ship == null || ship == local) continue;
                others++;
                Vector2 from = local.transform.position;
                Vector2 to = ship.transform.position;
                Vector2 dir = to - from;
                float dist = dir.magnitude;
                sb.AppendLine($"  --- target '{ship.name}' puppet={(ship.GetComponent<Sync.RemotePuppet>() != null)} " +
                    $"at ({to.x:0.#},{to.y:0.#}) distance={dist:0.#}" +
                    (longestReach > 0f && dist > longestReach
                        ? $"  *** OUT OF WEAPON RANGE ({longestReach:0.#}) — no shot can reach ***"
                        : ""));

                // Is the ship castable at all? Colliders, their layers, and — the thing that
                // silently removes a body from every query — whether its rigidbody is simulated.
                int castable = 0;
                foreach (var col in ship.GetComponentsInChildren<Collider2D>(true))
                {
                    if (col == null) continue;
                    var rb = col.attachedRigidbody;
                    bool inMask = (mask & (1 << col.gameObject.layer)) != 0;
                    bool live = col.enabled && col.gameObject.activeInHierarchy
                                && (rb == null || rb.simulated) && !col.isTrigger;
                    if (inMask && live) castable++;
                    sb.AppendLine($"      collider '{col.gameObject.name}' layer={col.gameObject.layer}" +
                        $"({LayerMask.LayerToName(col.gameObject.layer)}) enabled={col.enabled} " +
                        $"trigger={col.isTrigger} active={col.gameObject.activeInHierarchy} " +
                        $"rb={(rb == null ? "none" : (rb.simulated ? "simulated" : "NOT SIMULATED"))} " +
                        $"inMask={inMask}");
                }
                sb.AppendLine($"      castable colliders in mask: {castable}" +
                    (castable == 0 ? "  *** the sweep can never return this ship ***" : ""));

                // The real sweep, all hits in order: this separates "terrain blocks the shot"
                // from "the ship is invisible to physics".
                if (dist > 0.01f)
                {
                    var hits = Physics2D.CircleCastAll(from, radius, dir.normalized, dist, mask);
                    sb.AppendLine($"      CircleCastAll returned {hits.Length} hit(s):");
                    bool reached = false;
                    for (int i = 0; i < hits.Length && i < 8; i++)
                    {
                        var h = hits[i];
                        if (h.collider == null) continue;
                        var owningShip = h.collider.GetComponentInParent<Ship>();
                        bool isTarget = owningShip == ship;
                        bool isSelf = owningShip == local;
                        if (isTarget) reached = true;
                        sb.AppendLine($"        [{i}] '{h.collider.gameObject.name}' " +
                            $"layer={h.collider.gameObject.layer}({LayerMask.LayerToName(h.collider.gameObject.layer)}) " +
                            $"dist={h.distance:0.##}" +
                            (isTarget ? "  <== THE TARGET" : isSelf ? "  (my own hull)" : ""));
                    }
                    sb.AppendLine(reached
                        ? "      VERDICT: the target IS reachable by a bullet along this line."
                        : "      VERDICT: nothing on this line belongs to the target" +
                          (castable == 0 ? " (it is not castable at all)" : " (something is in the way)"));
                }

                // Gate 2, asked directly rather than waited for.
                try
                {
                    bool friends = local.Unit != null && ship.Unit != null && local.Unit.IsFriendsWith(ship.Unit);
                    sb.AppendLine($"      IsFriendsWith={friends} " +
                        (friends ? "*** still friendly — the bullet will pass through ***" : "(hostile: a hit will register)"));
                }
                catch (System.Exception e) { sb.AppendLine($"      IsFriendsWith threw: {e.Message}"); }
            }
            if (others == 0) sb.AppendLine("  no other ships in ShipManager — nothing to shoot at");
            return sb.ToString().TrimEnd();
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
                if (!BattleRoyalePvP.PlayersCanHitPlayers || __instance == null) return;
                try
                {
                    var owner = __instance.Owner;
                    if (owner == null) return;
                    if (owner.GetComponentInParent<Ship>() == null
                        && owner.GetComponentInChildren<Ship>() == null) return; // not a player's shot
                    NotePlayerProjectile(); // gate 0: this IS a player's projectile, and it is alive
                    // Snapshot the real numbers a live player bullet is flying with, so `pvpprobe`
                    // can re-run the game's own sweep with the game's own mask instead of a guess.
                    LastProjectileLayer = __instance.gameObject.layer;
                    try
                    {
                        LastProjectileMask = Traverse.Create(__instance)
                            .Field("collisionLayerMask").GetValue<LayerMask>().value;
                        LastProjectileRadius = __instance.Radius;
                    }
                    catch { }
                    var sm = ServiceLocator.Get<ShipManager>();
                    if (sm == null) return;
                    Vector2 p = __instance.transform.position;
                    foreach (var ship in sm.Ships)
                    {
                        if (ship == null || ship.Unit == owner) continue;
                        float d2 = ((Vector2)ship.transform.position - p).sqrMagnitude;
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
