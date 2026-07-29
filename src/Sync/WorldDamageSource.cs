using HarmonyLib;
using PunkMultiverse.Core;
using UnityEngine;

namespace PunkMultiverse.Sync
{
    /// <summary>
    /// Names the things that kill you WITHOUT firing a shot.
    ///
    /// Killer attribution used to come only from a projectile's <c>DamageTrace</c>
    /// (<c>DamageSync.DescribeSource</c>), so every non-projectile death was anonymous: the callout
    /// degraded to "OSANCHEZ DIED" and the audit line to <c>source=unknown shot=unknown</c>. That is
    /// exactly the case a player most wants explained — Omar, 2026-07-28, after a Battle Royale drop
    /// ended in four ticks of `type=Resource Electron` from nothing the log could name: "look at what
    /// eliminated my player 1".
    ///
    /// Everything that damages by CONTACT rather than by projectile funnels through three
    /// <c>HealthBase</c> entry points, and the ship's <c>DamagableResource</c> inherits all three
    /// without overriding them:
    ///
    ///   OnHazardTouched   — a <c>Hazard</c> collider: enemy melee arms, spikes, damaging props
    ///   OnHitByElectricity— an <c>ElectricityConductor</c> / arc beam
    ///   OnCellCollision   — terrain whose <c>CellType.contactDamage</c> is non-zero (lava, ...)
    ///
    /// Only <c>OnHitByElectricity</c> and <c>OnCellCollision</c> carry their source in the arguments;
    /// <c>HazardTouchArgs</c> deliberately does not, so the hazard is captured one level up, in
    /// <c>Hazard</c>'s own collision handlers — which call the sensor synchronously, in the same
    /// frame, so a one-frame window is enough to pair them without any bookkeeping.
    ///
    /// This is a RECORDER, not a gate: every patch here is a prefix that returns void, so it cannot
    /// change what damage lands. <c>DamageSync.CaptureAuditBase</c> reads the note and only uses it
    /// when there is no projectile trace — a real shot always wins, because "who shot me" beats "what
    /// was I touching".
    /// </summary>
    internal static class WorldDamageSource
    {
        private static string _name;
        private static int _frame = -1;

        internal static void Reset() { _name = null; _frame = -1; }

        /// <summary>Record what is about to deal contact damage. Cheap and unconditional: the read
        /// side is what decides whether anyone cares.</summary>
        internal static void Note(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            _name = name;
            _frame = Time.frameCount;
        }

        internal static void Note(GameObject source)
        {
            if (source == null) return;
            Note(Describe(source));
        }

        /// <summary>The last contact source, if one was recorded THIS frame — and CONSUMED, so one
        /// touch can only ever explain one hit. Both halves matter: a stale name in a death callout
        /// is worse than no name, and a note reused across several hits would keep blaming the last
        /// thing we brushed against long after we stopped touching it.</summary>
        internal static bool TryTake(out string name)
        {
            name = _name;
            if (string.IsNullOrEmpty(name) || Time.frameCount - _frame > 1) return false;
            Reset();
            return true;
        }

        /// <summary>Contact damage is recorded ONLY for our own ship. Every other victim's touches
        /// would just be noise competing for the same one-frame slot.</summary>
        private static bool IsLocalShip(Component victim)
        {
            try
            {
                var local = ShipSync.LocalShip;
                return local != null && victim != null
                       && victim.GetComponentInParent<Ship>() == local;
            }
            catch { return false; }
        }

        /// <summary>Turn the damaging GameObject into something a player would recognise.
        ///
        /// Prefer the entity id of the thing it belongs to: an enemy's melee hazard is a child
        /// collider called something like "DamageArea", while the entity it hangs off is
        /// "Unit_Cross_RedElectron" — the name worth putting in a callout. Falls back to the object's
        /// own name with Unity's "(Clone)" noise stripped.</summary>
        private static string Describe(GameObject go)
        {
            try
            {
                var savable = go.GetComponentInParent<SavableEntity>();
                if (savable != null && !string.IsNullOrEmpty(savable.entityId))
                    return DamageSync.PrettyEntityName(savable.entityId);
            }
            catch { }
            string n = go.name ?? string.Empty;
            int clone = n.IndexOf("(Clone)", System.StringComparison.Ordinal);
            if (clone >= 0) n = n.Substring(0, clone);
            return DamageSync.PrettyEntityName(n.Trim());
        }

        // ------------------------------------------------------------------ hazards (melee, spikes)

        // Hazard hands the victim a damage value and NOT itself, so the sensor cannot name what hit
        // it. Both of Hazard's collision entry points call the sensor synchronously further down the
        // same stack, so noting the hazard here reaches the audit intact.
        [HarmonyPatch(typeof(Hazard), "OnCollisionEnter2D")]
        internal static class NoteHazardCollision
        {
            private static void Prefix(Hazard __instance, Collision2D __0)
            {
                if (!NetSession.Active || __instance == null || __0 == null) return;
                if (!IsLocalShip(__0.collider)) return;
                Note(__instance.gameObject);
            }
        }

        [HarmonyPatch(typeof(Hazard), "OnTriggerEnter2D")]
        internal static class NoteHazardTrigger
        {
            private static void Prefix(Hazard __instance, Collider2D __0)
            {
                if (!NetSession.Active || __instance == null || __0 == null) return;
                if (!IsLocalShip(__0)) return;
                Note(__instance.gameObject);
            }
        }

        // ------------------------------------------------------------------ electricity

        [HarmonyPatch(typeof(HealthBase), nameof(HealthBase.OnHitByElectricity))]
        internal static class NoteElectricity
        {
            private static void Prefix(HealthBase __instance, ElectricityConductor __0)
            {
                if (!NetSession.Active || __0 == null || !IsLocalShip(__instance)) return;
                Note(__0.gameObject);
            }
        }

        // ------------------------------------------------------------------ damaging terrain

        [HarmonyPatch(typeof(HealthBase), nameof(HealthBase.OnCellCollision))]
        internal static class NoteCellContact
        {
            private static void Prefix(HealthBase __instance, CellCollision __0)
            {
                if (!NetSession.Active || !IsLocalShip(__instance)) return;
                var cell = __0.cellType;
                if (cell == null) return;
                // Match the game's own gate: a cell with no contact damage is scenery, and naming it
                // would credit the floor for a death it had nothing to do with.
                if (DamageSync.AmountOf(cell.contactDamage) <= 0f) return;
                Note(DamageSync.PrettyEntityName(cell.name));
            }
        }
    }
}
