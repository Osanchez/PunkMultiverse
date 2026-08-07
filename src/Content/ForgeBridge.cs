using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace PunkMultiverse.Content
{
    /// <summary>
    /// Detects WeaponForge (`com.andy.weaponforge`) and reports what this build can do with it.
    ///
    /// WeaponForge lets players author custom weapons from JSON and registers them into the
    /// game's own ModuleRegistry, so every one of them widens the module set that
    /// <see cref="Core.DeterminismAudit.CaptureModules"/> fingerprints. Two things follow, and
    /// both are handled elsewhere — this type only finds the mod and describes it:
    ///
    ///   * Players whose weapon sets differ are refused at go-live by the module digest, with a
    ///     reason naming content rather than world generation.
    ///   * Its loot injection writes into shared vanilla ScriptableObjects, so it is held off
    ///     for the duration of a net run (below) and the assets are restored by
    ///     <see cref="VanillaContentGuard"/>.
    ///
    /// Everything is resolved by reflection and every member is optional. This mod must load
    /// normally on the overwhelmingly common machine where WeaponForge is not installed, and it
    /// must not fall over when WeaponForge changes shape — its plugin version is pinned at
    /// "1.0.0" across releases, so there is no version to gate on and every lookup is treated as
    /// possibly-absent.
    /// </summary>
    internal static class ForgeBridge
    {
        internal const string PluginGuid = "com.andy.weaponforge";

        private static bool _probed;
        private static Assembly _assembly;
        private static Type _registryType;
        private static Type _lootPatchType;
        private static MethodInfo _lootPrefix;
        private static FieldInfo _lootDone;

        /// <summary>WeaponForge is loaded in this process.</summary>
        internal static bool Present { get; private set; }

        /// <summary>What this build can drive: "none" | "observe" | "reflection" | "api-1".
        ///
        /// "observe" is detect, report, and hold its loot injection off during a run.
        /// "reflection" adds redirecting its content roots — which turned out NOT to need the
        /// upstream hook or a transpiler, because all three roots are public static methods with
        /// a single call site each. See <see cref="ForgeContentSwap"/>; it decides which of the
        /// two applies by whether those members are actually there.</summary>
        internal static string Tier { get; private set; } = "none";

        /// <summary>True while a net run should keep WeaponForge out of the vanilla loot tables.
        /// Set by the session; read by the suppression patch.</summary>
        internal static bool SuppressLootInjection { get; set; }

        internal static void Probe()
        {
            if (_probed) return;
            _probed = true;

            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string name;
                    try { name = asm.GetName().Name; } catch { continue; }
                    if (!string.Equals(name, "WeaponForge", StringComparison.OrdinalIgnoreCase)) continue;
                    _assembly = asm;
                    break;
                }
                if (_assembly == null) return;

                Present = true;
                _registryType = AccessTools.TypeByName("WeaponForge.ForgeRegistry");
                _lootPatchType = AccessTools.TypeByName("WeaponForge.ForgeLootPatch");
                _lootPrefix = _lootPatchType != null ? AccessTools.Method(_lootPatchType, "Prefix") : null;
                _lootDone = _lootPatchType != null ? AccessTools.Field(_lootPatchType, "_done") : null;

                ForgeContentSwap.Probe();
                Tier = _registryType == null ? "none"
                     : ForgeContentSwap.Available ? "reflection" : "observe";
                Plugin.Log.LogInfo($"[Forge] WeaponForge detected (tier {Tier}) — " +
                    $"registry={(_registryType != null ? "ok" : "MISSING")} " +
                    $"lootPrefix={(_lootPrefix != null ? "ok" : "MISSING")} " +
                    $"done={(_lootDone != null ? "ok" : "MISSING")}");
                if (_registryType == null)
                    Plugin.Log.LogWarning("[Forge] WeaponForge is installed but its ForgeRegistry could " +
                        "not be found — it may have changed shape. Custom weapons still work; this mod " +
                        "just cannot reason about them beyond the module digest.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Forge] detection failed: {e.Message}");
            }
        }

        /// <summary>
        /// Hold WeaponForge's loot injection off for the duration of a net run.
        ///
        /// Its patch appends weapons straight into the shared vanilla
        /// <c>DropTableWeightedGroup</c> assets, caching which groups it has done in a private
        /// set. That is fine single-player and wrong in a session: BR rolls loot independently on
        /// every machine and matches results by ordinal, so a table one machine augmented and
        /// another did not does not merely differ by an item — every ordinal after the insertion
        /// point names a different thing. Its own cache also means a group augmented before the
        /// session keeps those entries afterwards.
        ///
        /// So custom weapons reach BR loot through this mod's own drop code instead
        /// (Modes/BattleRoyaleLootTables), which is seeded, id-ordered and covered by the match
        /// harness. Suppression is a no-op outside a run, so solo play is untouched.
        /// </summary>
        internal static void ApplySuppressionPatch(Harmony harmony)
        {
            Probe();
            if (!Present || _lootPrefix == null || harmony == null) return;
            try
            {
                harmony.Patch(_lootPrefix, prefix: new HarmonyMethod(
                    typeof(ForgeBridge).GetMethod(nameof(SkipWhileSuppressed),
                        BindingFlags.Static | BindingFlags.NonPublic)));
                Plugin.Log.LogInfo("[Forge] loot injection will be held off during net runs");
            }
            catch (Exception e)
            {
                // Not fatal: without this, a session merely inherits WeaponForge's own injection,
                // which the go-live module digest will still refuse if it differs between peers.
                Plugin.Log.LogWarning($"[Forge] could not hook loot injection ({e.Message}) — " +
                    "custom weapons may appear in vanilla crate tables during a session");
            }
        }

        private static bool SkipWhileSuppressed() => !SuppressLootInjection;

        /// <summary>
        /// The module and weapon ids that came from WeaponForge, so diagnostics can tell a custom
        /// weapon from a stock one. Rebuilt on each call — WeaponForge keeps registering as its
        /// loadout screen re-runs, and a cached set would go stale exactly when a new weapon
        /// appeared. Empty when WeaponForge is absent, which makes every caller a no-op.
        /// </summary>
        internal static void CollectForgeIds(HashSet<string> moduleIds, HashSet<string> weaponIds)
        {
            if (!Present || _registryType == null) return;
            try
            {
                var entries = AccessTools.Property(_registryType, "Entries")?.GetValue(null) as System.Collections.IEnumerable;
                if (entries == null) return;
                foreach (var entry in entries)
                {
                    if (entry == null) continue;
                    var module = AccessTools.Field(entry.GetType(), "module")?.GetValue(entry) as ModuleData;
                    if (module == null) continue;
                    try { if (!string.IsNullOrEmpty(module.Id)) moduleIds.Add(module.Id); } catch { }
                    if (module is WeaponModuleData wm && wm.weapon != null)
                    {
                        try { if (!string.IsNullOrEmpty(wm.weapon.Id)) weaponIds.Add(wm.weapon.Id); } catch { }
                        if (!string.IsNullOrEmpty(wm.weapon.name)) weaponIds.Add(wm.weapon.name);
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Forge] could not enumerate custom weapons: {e.Message}");
            }
        }

        /// <summary>Let WeaponForge re-augment the vanilla tables for solo play after a session
        /// has restored them, by clearing the "already done these groups" cache it keeps.</summary>
        internal static void ClearLootCache()
        {
            if (!Present || _lootDone == null) return;
            try
            {
                // A HashSet<T>, so not IList — reach Clear() on whatever collection it actually is.
                var done = _lootDone.GetValue(null);
                done?.GetType().GetMethod("Clear", Type.EmptyTypes)?.Invoke(done, null);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Forge] could not clear the loot-injection cache ({e.Message}) — " +
                    "custom weapons may be missing from crates in solo play until the game restarts");
            }
        }
    }
}
