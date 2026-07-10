using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace PunkMultiverse.Sync
{
    /// <summary>
    /// Reflection helpers for cosmetic unit status: shield charge and burn level. All fail-open —
    /// if the game's internals don't match, reads return defaults and writes no-op (logged once).
    /// </summary>
    internal static class UnitStatus
    {
        private static readonly Dictionary<Component, float> InitialShieldCharge = new Dictionary<Component, float>();
        private static readonly Dictionary<Component, BarrelTransform> BarrelCache = new Dictionary<Component, BarrelTransform>();
        private static bool _warnedShield;
        private static bool _warnedBurn;

        public static void Reset()
        {
            InitialShieldCharge.Clear();
            BarrelCache.Clear();
            _warnedShield = false;
            _warnedBurn = false;
        }

        // ---------------------------------------------------------------- aim / facing

        /// <summary>The direction this unit visibly aims — BarrelTransform.Direction is the
        /// game's single source of truth for aim (body facing is rb.rotation, synced separately;
        /// the game has no sprite flips). Zero = no barrel; receivers skip zero aims.</summary>
        public static Vector2 ReadAim(Component root)
        {
            try
            {
                var unit = root != null ? root.GetComponentInParent<Unit>() : null;
                if (unit == null) return Vector2.zero;
                if (!BarrelCache.TryGetValue(unit, out var barrel))
                    BarrelCache[unit] = barrel = unit.GetComponentInChildren<BarrelTransform>(true);
                return barrel != null ? barrel.Direction : Vector2.zero;
            }
            catch { return Vector2.zero; }
        }

        // ---------------------------------------------------------------- damage flash

        // DamageHighlight.OnDamage just stamps lastDamageTime; its Update applies/clears the
        // material swap. Invoking it directly gives the cosmetic flash WITHOUT the other
        // onDamage subscribers (camera shake, rumble, HUD) and without touching health.
        private static readonly System.Reflection.MethodInfo FlashMethod =
            AccessTools.Method(typeof(DamageHighlight), "OnDamage");

        /// <summary>Play the vanilla hit flash on an entity without applying damage. Safe to
        /// call once per replicated hit — repeats just refresh the flash timer.</summary>
        public static void PlayDamageFlash(Component root)
        {
            if (root == null || FlashMethod == null) return;
            try
            {
                var dh = root.GetComponentInChildren<DamageHighlight>(true);
                if (dh == null) dh = root.GetComponentInParent<DamageHighlight>();
                if (dh != null && dh.isActiveAndEnabled) FlashMethod.Invoke(dh, null);
            }
            catch { }
        }

        // ---------------------------------------------------------------- shields

        private static IEnumerable ShieldsOf(Component root)
        {
            var unit = root != null ? root.GetComponentInParent<Unit>() : null;
            if (unit == null) yield break;
            var shields = Traverse.Create(unit).Field("shields").GetValue() as IEnumerable;
            if (shields == null) yield break;
            foreach (var s in shields) yield return s;
        }

        private static float ChargeOf(object shield)
        {
            var t = Traverse.Create(shield);
            var prop = t.Property("Charge");
            if (prop != null && prop.PropertyExists()) return prop.GetValue<float>();
            return 0f;
        }

        public static float ReadShieldFraction(Component root)
        {
            try
            {
                float charge = 0f, initial = 0f;
                foreach (var shield in ShieldsOf(root))
                {
                    if (!(shield is Component sc)) continue;
                    float c = ChargeOf(shield);
                    if (!InitialShieldCharge.TryGetValue(sc, out float init) || c > init)
                        InitialShieldCharge[sc] = init = Mathf.Max(c, 0.001f);
                    charge += c;
                    initial += init;
                }
                return initial > 0f ? Mathf.Clamp01(charge / initial) : 0f;
            }
            catch { return 0f; }
        }

        public static void WriteShieldFraction(Component root, float fraction)
        {
            try
            {
                foreach (var shield in ShieldsOf(root))
                {
                    if (!(shield is Component sc)) continue;
                    float c = ChargeOf(shield);
                    if (!InitialShieldCharge.TryGetValue(sc, out float init) || c > init)
                        InitialShieldCharge[sc] = init = Mathf.Max(c, 0.001f);
                    var tank = Traverse.Create(shield).Field("tank");
                    var value = tank != null && tank.FieldExists() ? Traverse.Create(tank.GetValue()).Property("Value") : null;
                    if (value != null && value.PropertyExists())
                        value.SetValue(fraction * init);
                }
            }
            catch (System.Exception e)
            {
                if (!_warnedShield)
                {
                    _warnedShield = true;
                    Plugin.Log.LogWarning($"[Status] shield sync unavailable: {e.Message}");
                }
            }
        }

        // ---------------------------------------------------------------- burn

        public static float ReadBurnLevel(Component root)
        {
            try
            {
                var unit = root != null ? root.GetComponentInParent<Unit>() : null;
                if (unit == null) return 0f;
                var burn = Traverse.Create(unit).Property("Data")?.Property("BurnLevel");
                return burn != null && burn.PropertyExists() ? burn.GetValue<float>() : 0f;
            }
            catch { return 0f; }
        }

        public static void WriteBurnLevel(Component root, float value)
        {
            try
            {
                var unit = root != null ? root.GetComponentInParent<Unit>() : null;
                if (unit == null) return;
                var burn = Traverse.Create(unit).Property("Data")?.Property("BurnLevel");
                if (burn != null && burn.PropertyExists()) burn.SetValue(value);
            }
            catch (System.Exception e)
            {
                if (!_warnedBurn)
                {
                    _warnedBurn = true;
                    Plugin.Log.LogWarning($"[Status] burn sync unavailable: {e.Message}");
                }
            }
        }
    }
}
