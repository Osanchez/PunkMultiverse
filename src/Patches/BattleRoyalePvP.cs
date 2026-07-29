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
                if (__instance == null || !Modes.BattleRoyale.Active) return;
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

        /// <summary>The same widening for HITSCAN weapons. These carry their own
        /// <c>LayerMask</c> from the weapon's data asset rather than the physics matrix, so they are
        /// a second, independent way for a shot to miss a player — and the GUNNER loadout every
        /// Battle Royale player flies is exactly the kind of weapon that uses one.</summary>
        [HarmonyPatch(typeof(HitscanWeapon), "FireSingle")]
        internal static class HitscanCanReachOtherShips
        {
            private static void Prefix(HitscanWeapon __instance)
            {
                if (__instance == null || !Modes.BattleRoyale.Active) return;
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

        private static bool _loggedOnce;
        private static bool _loggedHitscan;
        private static int _shipLayers;
        private static float _shipLayersAt = -999f;

        /// <summary>Every layer a player ship actually presents a collider on, recomputed
        /// occasionally (a ship's parts can stream in after it spawns).</summary>
        private static int ShipLayers()
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

        internal static void Reset() { _loggedOnce = false; _loggedHitscan = false; _shipLayers = 0; _shipLayersAt = -999f; }

        [HarmonyPatch(typeof(Unit), nameof(Unit.IsFriendsWith))]
        internal static class ShipsAreNotFriendsInBattleRoyale
        {
            private static void Postfix(Unit __instance, Unit __0, ref bool __result)
            {
                if (!__result) return;                       // already hostile — nothing to do
                if (!Modes.BattleRoyale.Active) return;
                if (__instance == null || __0 == null) return;
                if (ReferenceEquals(__instance, __0)) return; // never make a ship hostile to itself
                // GetComponentInParent, not GetComponent: it searches this object AND its ancestors,
                // so it is correct whether Unit sits on the ship root or on a child of it. The
                // codebase reaches for Ship from a Unit both ways elsewhere, which means the
                // hierarchy is not something this patch should be asserting.
                var a = __instance.GetComponentInParent<Ship>() ?? __instance.GetComponentInChildren<Ship>();
                var b = __0.GetComponentInParent<Ship>() ?? __0.GetComponentInChildren<Ship>();
                if (a == null || b == null) return;
                __result = false;
                PvPDiag.NoteFriendFlip();
            }
        }
    }
}
