using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace PunkMultiverse.Patches
{
    /// <summary>
    /// Makes player ships shootable by other players.
    ///
    /// PUNK is a co-op game, so every player ship carries the SAME faction, and
    /// <c>Projectile.FixedUpdate</c> asks one question before it registers a hit:
    /// <c>Owner.IsFriendsWith(hitUnit)</c> — if true it calls <c>MoveForward()</c> instead of
    /// <c>OnObjectHit()</c>. Direct-fire projectiles therefore fly straight THROUGH another
    /// player's ship without ever reaching a collision, let alone the mod's damage routing. This
    /// was the "none of my attacks are hitting the other player" report (2026-07-27): the friendly
    /// -fire toggle, the BR PvP damage scale, and <c>DamageSync</c>'s ship-vs-ship chokepoint were
    /// all correct and all downstream of a hit that never happened.
    ///
    /// Hitscan beams and explosions have no such filter, which is exactly why they DID land — the
    /// symptom was weapon-dependent, not networking-dependent.
    ///
    /// Narrow by construction: only in a live Battle Royale match, and only when BOTH units are
    /// player ships that are not the same ship. Enemy AI is untouched (an <c>AIAgent</c>'s unit is
    /// never a <c>Ship</c>), self-hits stay friendly (same object), and Standard co-op keeps
    /// vanilla behaviour — friendly fire there is the lobby's <c>FriendlyFire</c> option, enforced
    /// in <c>ProjectileSync.FriendlyFireBlocked</c> on the routed damage, not on the collision.
    ///
    /// KNOWN GAP: a player's MINIONS still pass through other players — their projectiles' Owner is
    /// the minion Unit, not a Ship, so "which player does this unit belong to" would have to be
    /// resolved before their fire could be made hostile without also making it hit their own owner.
    /// </summary>
    internal static class BattleRoyalePvP
    {
        /// <summary>THE reason player bullets pass through players, and it is not the faction.
        ///
        /// <c>Projectile.Shoot</c> sets its own sweep mask from the PROJECT-WIDE physics matrix:
        /// <c>collisionLayerMask = Physics2D.GetLayerCollisionMask(gameObject.layer)</c>. PUNK is a
        /// co-op game, so that matrix has player projectiles NOT colliding with player ships — your
        /// bullets are meant to fly through your teammates. The consequence is that
        /// <c>Physics2D.CircleCast</c> never returns the other ship's collider at all, so
        /// <c>Owner.IsFriendsWith</c> is never even called and every downstream fix — the faction
        /// flip, the friendly-fire gate, the PvP scale, the damage routing — sits behind a
        /// collision that cannot happen. Measured 2026-07-29 with Patches/PvPDiag.cs: a bot fired
        /// at another bot for 12 seconds from 8 units away and produced
        /// <c>castHitShip=0 friendFlip=0</c> — not one sweep ever saw the target.
        ///
        /// So the mask is widened per shot, for player-owned projectiles in a live Battle Royale
        /// match only: the ship layers are OR-ed in, and the friend check (which now runs) keeps a
        /// shooter's own hull exempt because a Unit is always friends with itself. Co-op is
        /// untouched — its bullets keep passing through teammates exactly as the matrix says.
        /// Per-shot rather than a global matrix edit: Physics2D.IgnoreLayerCollision is process-wide
        /// state that would outlive the match and leak into the next single-player run.</summary>
        [HarmonyPatch(typeof(Projectile), nameof(Projectile.Shoot))]
        internal static class ProjectilesCanReachOtherShips
        {
            private static void Postfix(Projectile __instance)
            {
                if (__instance == null || !PlayersCanHitPlayers) return;
                try
                {
                    var owner = __instance.Owner;
                    if (owner == null) return;
                    // Only a PLAYER's shot. An enemy's projectile already collides with ships.
                    if (owner.GetComponentInParent<Ship>() == null
                        && owner.GetComponentInChildren<Ship>() == null) return;

                    int shipMask = ShipLayers();
                    if (shipMask == 0) return;
                    var field = Traverse.Create(__instance).Field("collisionLayerMask");
                    LayerMask current = field.GetValue<LayerMask>();
                    if ((current.value & shipMask) == shipMask) return; // already reachable
                    LayerMask widened = current.value | shipMask;
                    field.SetValue(widened);
                    if (!_loggedOnce)
                    {
                        _loggedOnce = true;
                        Plugin.Log.LogInfo($"[BR] player projectiles widened to reach ships: " +
                            $"mask 0x{current.value:X} -> 0x{widened.value:X} (ship layers 0x{shipMask:X}). " +
                            "The physics matrix excludes ship-vs-player-bullet for co-op; " +
                            "battle royale needs it back.");
                    }
                }
                catch { }
            }
        }

        /// <summary>The other half of the widening, and the reason the widening alone changed
        /// nothing.
        ///
        /// Once the ship layer is in the sweep mask, a bullet's FIRST <c>CircleCast</c> — taken at
        /// the muzzle, which sits inside the firing ship's own silhouette — returns the SHOOTER'S
        /// hull. Vanilla has a guard for that: <c>Owner.IsFriendsWith(hitUnit)</c> calls
        /// <c>MoveForward()</c> instead of registering a hit. But it only reaches that guard when
        /// <c>hit.rigidbody</c> itself carries the <c>Unit</c>, and a ship's hull is a set of CHILD
        /// colliders whose bodies carry no <c>Unit</c> at all. So the check is skipped, the bullet
        /// takes the impact branch, and every shot detonates on the ship that fired it.
        ///
        /// Measured 2026-07-29, with the target proven reachable by <c>pvpprobe</c> (in mask, three
        /// castable colliders, <c>IsFriendsWith=False</c>, clear line of fire, VERDICT reachable):
        /// 1148 player projectile ticks, <c>hitAnotherShip=0</c>. The bullets were never travelling.
        /// The shooter's own hull is the first thing on the ray — the probe's own cast shows it at
        /// distance 0 and 0.1, ahead of the target at 37.97.
        ///
        /// This restores vanilla's intent for the case its own check cannot see: a shot passes
        /// through the ship that fired it, exactly as <c>MoveForward()</c> would have made it.
        /// </summary>
        [HarmonyPatch(typeof(Projectile), "OnObjectHit")]
        internal static class ShotsDoNotDetonateOnTheirOwnHull
        {
            private static bool Prefix(Projectile __instance, RaycastHit2D __0)
            {
                if (__instance == null || !PlayersCanHitPlayers) return true;
                try
                {
                    var owner = __instance.Owner;
                    if (owner == null) return true;
                    var ownerShip = owner.GetComponentInParent<Ship>() ?? owner.GetComponentInChildren<Ship>();
                    if (ownerShip == null) return true;          // not a player's shot
                    var col = __0.collider;
                    if (col == null) return true;
                    if (col.GetComponentInParent<Ship>() != ownerShip) return true; // someone else — real hit
                    // Our own hull. This is vanilla's MoveForward, inlined: keep flying.
                    PvPDiag.NoteSelfHit();
                    var v = __instance.Velocity;
                    __instance.transform.position += new Vector3(v.x, v.y, 0f) * Time.deltaTime;
                    return false;
                }
                catch { return true; }
            }
        }

        /// <summary>The same widening for HITSCAN weapons. These carry their own
        /// <c>LayerMask</c> from the weapon's data asset rather than the physics matrix, so they are
        /// a second, independent way for a shot to miss a player — and the GUNNER loadout every
        /// Battle Royale player flies is exactly the kind of weapon that uses one.</summary>
        /// <summary>
        /// Start the shooter's own hitscan cast OUTSIDE its own hull.
        ///
        /// FireSingle is a single Physics2D.CircleCast that acts on the FIRST collider and returns.
        /// Once the mask above includes ship layers, that first collider is the hull the muzzle
        /// sits inside, so the beam stopped dead on its owner: it damaged the shooter and drew no
        /// line, because a beam's visual length IS its hit distance.
        ///
        /// This moves the cast's ORIGIN, and nothing else. Two earlier attempts changed physics
        /// state instead and both were worse than the bug:
        ///
        ///   relayering the hull  moved it to layer 2, which ShipLayers() had already put INSIDE
        ///                        the widened mask on that machine. The hiding place was inside
        ///                        the search, so nothing was hidden.
        ///   disabling the hull   worked once and then destroyed the ship. A held beam re-enters
        ///                        FireSingle, the second prefix overwrote _hidden with an EMPTY
        ///                        list (already-disabled colliders are skipped), the inner
        ///                        finalizer restored nothing and the outer's list was gone. The
        ///                        ship was left permanently without colliders, fell out of the
        ///                        world and died -- while the beam, now somewhere else entirely,
        ///                        reached nothing at all.
        ///
        /// An origin offset has no such failure mode: it is a local edit to one argument, it
        /// cannot leak, and re-entrancy is harmless because there is no state to restore. The push
        /// is only ever as far as the hull actually extends, and only when the muzzle is not
        /// already clear of it, so a target at point-blank range is not skipped.
        /// </summary>
        [HarmonyPatch(typeof(HitscanWeapon), "FireSingle")]
        internal static class HitscanStartsOutsideOwnHull
        {
            private static bool _logged;

            private static void Prefix(HitscanWeapon __instance, ref Vector2 __0, Vector2 __1)
            {
                if (__instance == null || !PlayersCanHitPlayers) return;
                try
                {
                    var ownerShip = OwnerShipOf(__instance);
                    if (ownerShip == null) return;

                    var dir = __1.sqrMagnitude > 1e-6f ? __1.normalized : Vector2.right;
                    Vector2 centre = ownerShip.transform.position;

                    // How far the hull actually reaches, measured rather than assumed -- a ship's
                    // hull is about 0.70u but modules extend it and an upgrade changes it.
                    float reach = 0f;
                    foreach (var col in ownerShip.GetComponentsInChildren<Collider2D>(true))
                    {
                        if (col == null || !col.enabled) continue;
                        // SOLID colliders only. A ship also carries large TRIGGER volumes -- aim
                        // assist, sensors -- and including them measured a "hull" of 20.39u on a
                        // ship whose hull is about 0.7u. The cast then started 20 units downrange,
                        // past a target 5 units away, and the beam hit nothing at all: the
                        // self-damage was gone because the weapon had been blinded.
                        if (col.isTrigger) continue;
                        var b = col.bounds;
                        Vector3 far = new Vector3(centre.x + dir.x * 1000f, centre.y + dir.y * 1000f, 0f);
                        Vector3 near = b.ClosestPoint(far);
                        float d = Vector2.Distance(centre, new Vector2(near.x, near.y));
                        if (d > reach) reach = d;
                    }
                    if (reach <= 0f) return;
                    // Hard ceiling. The push exists to clear a hull, and no hull is metres across;
                    // if the measurement ever goes wrong again it must fail SAFE (a weapon that
                    // can still hurt its owner) rather than silently blind the weapon entirely.
                    float need = Mathf.Min(reach, 3f) + 0.15f;

                    float along = Vector2.Dot(__0 - centre, dir);
                    if (along >= need) return;              // muzzle is already clear of the hull
                    __0 += dir * (need - along);

                    if (!_logged)
                    {
                        _logged = true;
                        Plugin.Log.LogInfo($"[BR] hitscan origin pushed clear of its own hull " +
                            $"(reach {reach:0.##}u, muzzle was {along:0.##}u along the ray)");
                    }
                }
                catch { }
            }
        }

        /// <summary>The ship holding this weapon. WeaponBase is a plain object, not a Component --
        /// it reaches the world through its Owner Unit, the same handle the projectile path uses.</summary>
        private static Ship OwnerShipOf(HitscanWeapon w)
        {
            try
            {
                var owner = w.Owner;
                if (owner == null) return null;
                return owner.GetComponentInParent<Ship>() ?? owner.GetComponentInChildren<Ship>();
            }
            catch { return null; }
        }

        [HarmonyPatch(typeof(HitscanWeapon), "FireSingle")]
        internal static class HitscanCanReachOtherShips
        {
            private static void Prefix(HitscanWeapon __instance)
            {
                if (__instance == null || !PlayersCanHitPlayers) return;
                try
                {
                    Patches.PvPDiag.NotePlayerHitscan();
                    int shipMask = ShipLayers();
                    if (shipMask == 0) return;
                    LayerMask current = __instance.LayerMask;
                    if ((current.value & shipMask) == shipMask) return;
                    LayerMask widened = current.value | shipMask;
                    __instance.LayerMask = widened;
                    if (!_loggedHitscan)
                    {
                        _loggedHitscan = true;
                        Plugin.Log.LogInfo($"[BR] hitscan weapon widened to reach ships: " +
                            $"mask 0x{current.value:X} -> 0x{widened.value:X}");
                    }
                }
                catch { }
            }
        }


        /// <summary>
        /// PHYSICS projectiles are a third path, and they were never covered.
        ///
        /// Projectile sweeps a cast it owns, and HitscanWeapon carries a LayerMask from its data
        /// asset -- both are widened above. PhysicsProjectile has NEITHER. It is a rigidbody that
        /// learns about hits only through OnTriggerEnter2D / OnCollisionEnter2D, so whether it can
        /// touch a player ship at all is decided by the PROJECT-WIDE Physics2D layer matrix, which
        /// says a player's bullet passes through a player's ship. A lobbed weapon therefore flew
        /// straight through the other player as if they had no hitbox (Omar, 2026-08-07) and
        /// nothing in the mask work could ever have changed that.
        ///
        /// So open the pair in the matrix instead, lazily: only for layers a player's physics
        /// projectile has actually been fired on, and only while PvP is allowed. Every pair opened
        /// is recorded so RestoreLayerMatrix can put the matrix back exactly as it was -- this is
        /// global state shared with single-player, and leaving it open would change how the game
        /// behaves after the session ends.
        ///
        /// Opening the matrix is necessary but NOT sufficient, and assuming otherwise cost a
        /// round: the matrix is layer-based, so the same pair that lets a shard reach another
        /// player also lets it reach its OWN. I claimed ImpactBehaviour.safetyDistance would cover
        /// that. It does not — a lobbed shot arcs back down onto the ship that fired it, well past
        /// any muzzle-proximity guard, and Omar killed himself with his own shards.
        ///
        /// So each projectile is additionally made to ignore its OWN ship's colliders, pair by
        /// pair, via Physics2D.IgnoreCollision. That is per-collider rather than per-layer, so it
        /// removes the self-hit without touching anyone else's — and unlike a damage-side guard it
        /// also stops the shard bouncing off its owner's hull, which would still have been wrong.
        /// </summary>
        [HarmonyPatch(typeof(PhysicsProjectile), nameof(PhysicsProjectile.Shoot))]
        internal static class PhysicsProjectilesCanReachOtherShips
        {
            private static void Postfix(PhysicsProjectile __instance)
            {
                if (__instance == null || !PlayersCanHitPlayers) return;
                try
                {
                    var owner = __instance.Owner;
                    if (owner == null) return;
                    if (owner.GetComponentInParent<Ship>() == null
                        && owner.GetComponentInChildren<Ship>() == null) return;   // not a player's shot

                    // The shot must never touch the ship that fired it. Done first: if the
                    // matrix opens below and this has not run, the projectile is briefly able to
                    // hit its owner.
                    var ownerShip = owner.GetComponentInParent<Ship>() ?? owner.GetComponentInChildren<Ship>();
                    var mine = __instance.GetComponentsInChildren<Collider2D>(true);
                    foreach (var ownCol in ownerShip.GetComponentsInChildren<Collider2D>(true))
                    {
                        if (ownCol == null) continue;
                        foreach (var projCol in mine)
                        {
                            if (projCol == null) continue;
                            try { Physics2D.IgnoreCollision(projCol, ownCol, true); } catch { }
                        }
                    }

                    int projLayer = __instance.gameObject.layer;
                    int shipMask = ShipLayers();
                    if (shipMask == 0) return;

                    for (int layer = 0; layer < 32; layer++)
                    {
                        if ((shipMask & (1 << layer)) == 0) continue;
                        if (!Physics2D.GetIgnoreLayerCollision(projLayer, layer)) continue;
                        Physics2D.IgnoreLayerCollision(projLayer, layer, false);
                        _openedPairs.Add((projLayer, layer));
                        Plugin.Log.LogInfo($"[BR] physics projectile layer {projLayer} can now hit " +
                            $"ship layer {layer} (was blocked by the collision matrix)");
                    }
                }
                catch { }
            }
        }


        /// <summary>
        /// A physics projectile must not detonate on the ship that fired it.
        ///
        /// Opening the layer matrix (above) is what lets a lobbed shot reach another player at
        /// all, and it is layer-based, so it equally lets the shot reach its OWN ship. The first
        /// attempt at this called Physics2D.IgnoreCollision between the projectile's colliders and
        /// the owner's at Shoot time; measurement says that is not holding -- the shard still
        /// killed its own shooter for full health.
        ///
        /// So refuse it where the decision is actually made. Both of PhysicsProjectile's entry
        /// points are guarded, because a shard arcs: it can arrive as a trigger overlap or as a
        /// solid collision depending on what it grazes on the way back down.
        ///
        /// Vanilla's ImpactBehaviour.safetyDistance does NOT cover this, whatever its name
        /// suggests -- it is a muzzle-proximity guard, and a lobbed shot comes back to its owner
        /// from a long way away. Assuming otherwise is what let this ship.
        /// </summary>
        [HarmonyPatch(typeof(PhysicsProjectile))]
        internal static class PhysicsProjectilesSpareTheirOwner
        {
            private static bool _logged;

            [HarmonyPrefix, HarmonyPatch("OnCollisionEnter2D")]
            private static bool OnCollision(PhysicsProjectile __instance, Collision2D __0)
                => !IsOwnShip(__instance, __0 != null ? __0.collider : null);

            [HarmonyPrefix, HarmonyPatch("OnTriggerEnter2D")]
            private static bool OnTrigger(PhysicsProjectile __instance, Collider2D __0)
                => !IsOwnShip(__instance, __0);

            private static bool IsOwnShip(PhysicsProjectile proj, Collider2D other)
            {
                if (proj == null || other == null || !PlayersCanHitPlayers) return false;
                try
                {
                    var owner = proj.Owner;
                    if (owner == null) return false;
                    var ownerShip = owner.GetComponentInParent<Ship>() ?? owner.GetComponentInChildren<Ship>();
                    if (ownerShip == null) return false;
                    if (other.GetComponentInParent<Ship>() != ownerShip) return false;
                    if (!_logged)
                    {
                        _logged = true;
                        Plugin.Log.LogInfo("[BR] a physics projectile was refused a hit on the ship that fired it");
                    }
                    return true;
                }
                catch { return false; }
            }
        }

        private static readonly List<(int a, int b)> _openedPairs = new List<(int, int)>();

        /// <summary>Put every layer pair this mod opened back. Global physics state outlives the
        /// session, so a pair left open would quietly change single-player.</summary>
        internal static void RestoreLayerMatrix()
        {
            if (_openedPairs.Count == 0) return;
            foreach (var (a, b) in _openedPairs)
            {
                try { Physics2D.IgnoreLayerCollision(a, b, true); } catch { }
            }
            Plugin.Log.LogInfo($"[BR] restored {_openedPairs.Count} collision-matrix pair(s)");
            _openedPairs.Clear();
        }

        private static bool _loggedOnce;
        private static bool _loggedHitscan;
        private static int _shipLayers;
        private static float _shipLayersAt = -999f;

        /// <summary>Player ships are shootable when Battle Royale is running, and — for the same
        /// physical reason — when a co-op lobby has friendly fire switched ON. The layer matrix
        /// excludes player-bullet-vs-player-ship in BOTH cases, so a co-op host who enabled
        /// friendly fire got a setting that silently did nothing to direct-fire weapons: the
        /// routed-damage gate in <c>ProjectileSync.FriendlyFireBlocked</c> was ready to let those
        /// hits through, but the collision they depend on never happened. Everything else — the
        /// faction flip, the PvP damage scale — stays Battle-Royale-only.</summary>
        internal static bool PlayersCanHitPlayers
        {
            get
            {
                if (Modes.BattleRoyale.Active) return true;
                var session = Core.NetSession.Instance;
                return session != null && session.FriendlyFire;
            }
        }

        /// <summary>Every layer a player ship actually presents a collider on, recomputed
        /// occasionally (a ship's parts can stream in after it spawns).</summary>
        internal static int ShipLayers()
        {
            if (_shipLayers != 0 && Time.unscaledTime - _shipLayersAt < 5f) return _shipLayers;
            _shipLayersAt = Time.unscaledTime;
            int mask = 0;
            try
            {
                var sm = ServiceLocator.Get<ShipManager>();
                if (sm == null) return _shipLayers;
                foreach (var ship in sm.Ships)
                {
                    if (ship == null) continue;
                    mask |= 1 << ship.gameObject.layer;
                    foreach (var col in ship.GetComponentsInChildren<Collider2D>(true))
                        if (col != null && !col.isTrigger) mask |= 1 << col.gameObject.layer;
                }
            }
            catch { }
            if (mask != 0) _shipLayers = mask;
            return _shipLayers;
        }

        internal static void Reset()
        {
            _loggedOnce = false; _loggedHitscan = false; _shipLayers = 0; _shipLayersAt = -999f;
            RestoreLayerMatrix();
        }

        [HarmonyPatch(typeof(Unit), nameof(Unit.IsFriendsWith))]
        internal static class ShipsAreNotFriendsInBattleRoyale
        {
            private static void Postfix(Unit __instance, Unit __0, ref bool __result)
            {
                if (!__result) return;                       // already hostile — nothing to do
                // Same gate as the mask widening: without the faction flip a direct-fire
                // projectile calls MoveForward() and passes through, so a friendly-fire co-op
                // lobby needs this exactly as much as Battle Royale does. The PvP damage SCALE
                // stays Battle-Royale-only — that is a mode rule, not a collision rule.
                if (!PlayersCanHitPlayers) return;
                if (__instance == null || __0 == null) return;
                if (ReferenceEquals(__instance, __0)) return; // never make a ship hostile to itself
                // GetComponentInParent, not GetComponent: it searches this object AND its ancestors,
                // so it is correct whether Unit sits on the ship root or on a child of it. The
                // codebase reaches for Ship from a Unit both ways elsewhere, which means the
                // hierarchy is not something this patch should be asserting.
                var a = __instance.GetComponentInParent<Ship>() ?? __instance.GetComponentInChildren<Ship>();
                var b = __0.GetComponentInParent<Ship>() ?? __0.GetComponentInChildren<Ship>();
                if (a == null || b == null) return;
                // SAME SHIP stays friendly. The ReferenceEquals check above only catches one Unit
                // compared with itself, and a ship carries SEVERAL Units -- the hull root and its
                // modules. So a weapon's owner Unit and the hull Unit it is standing inside are
                // different objects on the same ship, and this happily declared them enemies.
                // Direct-fire projectiles survived that because RejectSelfHullAtMuzzle throws the
                // muzzle hit away; hitscan had no such guard and shot its own owner (Omar,
                // 2026-08-07: "the beam is self-inflicting damage to the shooter").
                if (a == b) return;
                __result = false;
                PvPDiag.NoteFriendFlip();
            }
        }
    }
}
