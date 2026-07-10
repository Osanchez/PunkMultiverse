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
        private static readonly Dictionary<Component, (StateMachine sm, State[] states)> StateCache
            = new Dictionary<Component, (StateMachine, State[])>();
        private static readonly Dictionary<Component, Shooter> ShooterCache = new Dictionary<Component, Shooter>();
        private static bool _warnedShield;
        private static bool _warnedBurn;

        public static void Reset()
        {
            InitialShieldCharge.Clear();
            BarrelCache.Clear();
            StateCache.Clear();
            ShooterCache.Clear();
            _warnedShield = false;
            _warnedBurn = false;
        }

        // ---------------------------------------------------------------- weapon audio state

        // Warmup/continuous weapon sounds (charge-up telegraphs, beam loops) are driven by
        // Shooter.Update — muted on puppets — so the audio state replicates instead. The
        // Play*/Stop* methods are idempotent (handle-guarded) and update loop positions when
        // called repeatedly, so applying per snapshot is exactly right.

        private static Shooter ShooterOf(Component root)
        {
            var unit = root != null ? root.GetComponentInParent<Unit>() : null;
            if (unit == null) return null;
            if (!ShooterCache.TryGetValue(unit, out var shooter))
                ShooterCache[unit] = shooter = unit.GetComponentInChildren<Shooter>(true);
            return shooter;
        }

        /// <summary>0 = idle, 1 = warming up, 2 = warmed/firing (continuous loop).</summary>
        public static byte ReadFireState(Component root)
        {
            try
            {
                var shooter = ShooterOf(root);
                var weapon = shooter != null ? shooter.Weapon : null;
                if (weapon == null || !weapon.IsTriggerPulled) return 0;
                return weapon.IsWarmedUp ? (byte)2 : (byte)1;
            }
            catch { return 0; }
        }

        public static void WriteFireState(Component root, byte state)
        {
            try
            {
                var shooter = ShooterOf(root);
                if (shooter == null) return;
                switch (state)
                {
                    case 1:
                        shooter.StopContinousSound();
                        shooter.PlayWarmupSound();
                        break;
                    case 2:
                        shooter.StopWarmupSound();
                        shooter.PlayContinousSound();
                        break;
                    default:
                        shooter.StopWarmupSound();
                        shooter.StopContinousSound();
                        break;
                }
            }
            catch { }
        }

        // ---------------------------------------------------------------- AI state (visual)

        // Enemy behavior states are child GameObjects toggled by StateMachine — muted on
        // puppets, so without replication a puppet stays frozen in its spawn-time state
        // (no attack poses, telegraph VFX, or animator changes).

        private static (StateMachine sm, State[] states) StatesOf(Component root)
        {
            var unit = root != null ? root.GetComponentInParent<Unit>() : null;
            if (unit == null) return (null, null);
            if (!StateCache.TryGetValue(unit, out var entry))
            {
                var sm = unit.GetComponentInChildren<StateMachine>(true);
                entry = (sm, sm != null ? sm.GetComponentsInChildren<State>(true) : null);
                StateCache[unit] = entry;
            }
            return entry;
        }

        /// <summary>Index of the unit's current AI state (prefab child order — identical on
        /// every client); 255 = no state machine / unknown.</summary>
        public static byte ReadState(Component root)
        {
            try
            {
                var (sm, states) = StatesOf(root);
                if (sm == null || states == null) return 255;
                var current = Traverse.Create(sm).Field("currentState").GetValue() as State;
                if (current == null) return 255;
                for (int i = 0; i < states.Length && i < 255; i++)
                    if (states[i] == current)
                        return (byte)i;
                return 255;
            }
            catch { return 255; }
        }

        /// <summary>Drive the puppet's StateMachine to the authority's state. ChangeState works
        /// on the disabled component and toggles the state child GameObjects (visuals); muted
        /// action Behaviours inside them stay disabled.</summary>
        public static void WriteState(Component root, byte index)
        {
            if (index == 255) return;
            try
            {
                var (sm, states) = StatesOf(root);
                if (sm == null || states == null || index >= states.Length) return;
                var current = Traverse.Create(sm).Field("currentState").GetValue() as State;
                if (current == states[index]) return;
                sm.ChangeState(states[index], false);
            }
            catch { }
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
