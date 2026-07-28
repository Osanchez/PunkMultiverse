using HarmonyLib;

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
        [HarmonyPatch(typeof(Unit), nameof(Unit.IsFriendsWith))]
        internal static class ShipsAreNotFriendsInBattleRoyale
        {
            private static void Postfix(Unit __instance, Unit __0, ref bool __result)
            {
                if (!__result) return;                       // already hostile — nothing to do
                if (!Modes.BattleRoyale.Active) return;
                if (__instance == null || __0 == null) return;
                if (ReferenceEquals(__instance, __0)) return; // never make a ship hostile to itself
                if (__instance.GetComponent<Ship>() == null || __0.GetComponent<Ship>() == null) return;
                __result = false;
            }
        }
    }
}
