using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;

namespace PunkMultiverse.Content
{
    /// <summary>
    /// Points WeaponForge at the host's downloaded content for the duration of a session, and
    /// puts the player's own content back afterwards.
    ///
    /// This was believed to need a change in WeaponForge, and it does not. Its three content
    /// roots are each a <c>public static string</c> method — <c>ForgeRegistry.WeaponsFolder()</c>,
    /// <c>ForgeSpriteLibrary.SpritesFolder()</c>, <c>ForgeSoundLibrary.SoundsFolder()</c> — and
    /// each has exactly ONE call site, inside its own loader. So three Harmony postfixes redirect
    /// every read. No transpiler into a third party's method body, which was the construct the
    /// plan was trying to avoid.
    ///
    /// The upstream <c>ForgeInterop</c> hook is still worth asking for: postfixing another mod's
    /// methods and writing its private statics works, but it is a contract nobody promised us and
    /// their next release can silently end it. That is a reason to ALSO track WeaponForge in
    /// gamescan, not a reason to wait.
    ///
    /// Nothing here runs unless a session actually has host content to apply. WeaponForge absent,
    /// host serving nothing, or any lookup missing — all of them leave the swap a no-op and the
    /// player's own weapons exactly where they were.
    /// </summary>
    internal static class ForgeContentSwap
    {
        // ---- what the roots resolve to ------------------------------------------------------
        // null = WeaponForge's own folders beside its DLL, i.e. today's behaviour. Every path in
        // this file that can fail leaves this null, so failure means "the player's own content",
        // never "no content" or "half of each".
        private static string _rootOverride;

        private static bool _patched;
        private static bool _resolved;

        private static Type _registry;      // WeaponForge.ForgeRegistry
        private static Type _sprites;       // WeaponForge.ForgeSpriteLibrary
        private static Type _sounds;        // WeaponForge.ForgeSoundLibrary
        private static MethodInfo _weaponsFolder, _spritesFolder, _soundsFolder;
        private static MethodInfo _buildAll, _registerInto;

        /// <summary>The content set currently applied, or null when the player's own is in place.</summary>
        internal static string ActiveRoot => _rootOverride;

        /// <summary>True when every member this needs was found and the swap is actually available.</summary>
        internal static bool Available { get; private set; }

        // -----------------------------------------------------------------------------------
        // Resolution
        // -----------------------------------------------------------------------------------

        /// <summary>Resolve the members the swap needs. Re-entrant with
        /// <see cref="ForgeBridge.Probe"/> — each sets its own guard before doing any work, and
        /// ForgeBridge sets Present before it asks about the tier, so the cycle terminates with
        /// both sides seeing the truth.</summary>
        internal static void Probe()
        {
            if (_resolved) return;
            _resolved = true;

            ForgeBridge.Probe();
            if (!ForgeBridge.Present) return;

            try
            {
                _registry = AccessTools.TypeByName("WeaponForge.ForgeRegistry");
                _sprites = AccessTools.TypeByName("WeaponForge.ForgeSpriteLibrary");
                _sounds = AccessTools.TypeByName("WeaponForge.ForgeSoundLibrary");

                _weaponsFolder = _registry != null ? AccessTools.Method(_registry, "WeaponsFolder") : null;
                _spritesFolder = _sprites != null ? AccessTools.Method(_sprites, "SpritesFolder") : null;
                _soundsFolder = _sounds != null ? AccessTools.Method(_sounds, "SoundsFolder") : null;
                _buildAll = _registry != null ? AccessTools.Method(_registry, "BuildAll") : null;
                _registerInto = _registry != null ? AccessTools.Method(_registry, "RegisterInto") : null;

                Available = _weaponsFolder != null && _spritesFolder != null && _soundsFolder != null
                            && _buildAll != null && _registerInto != null;

                if (!Available)
                {
                    Plugin.Log.LogWarning("[Forge] WeaponForge is installed but its content roots could not " +
                        "be reached — host content will not be applied. Players whose weapon sets differ " +
                        "are still refused at go-live by the module digest, so nothing desyncs; they just " +
                        "have to install the same weapons by hand. " +
                        $"weapons={Found(_weaponsFolder)} sprites={Found(_spritesFolder)} " +
                        $"sounds={Found(_soundsFolder)} build={Found(_buildAll)} register={Found(_registerInto)}");
                }
            }
            catch (Exception e)
            {
                Available = false;
                Plugin.Log.LogWarning($"[Forge] content-root resolution failed: {e.Message}");
            }
        }

        private static string Found(MemberInfo m) => m != null ? "ok" : "MISSING";

        // -----------------------------------------------------------------------------------
        // The three redirects
        // -----------------------------------------------------------------------------------

        /// <summary>
        /// Applied once at load, by hand rather than through PatchAll — the targets belong to
        /// another mod, so a PatchAll over this assembly would throw at chainload on the
        /// overwhelmingly common machine where WeaponForge is absent and take this mod with it.
        /// Same reason and same shape as <see cref="ForgeBridge.ApplySuppressionPatch"/>.
        /// </summary>
        internal static void ApplyRootPatches(Harmony harmony)
        {
            if (_patched || harmony == null) return;
            Probe();
            if (!Available) return;
            _patched = true;

            var post = new HarmonyMethod(typeof(ForgeContentSwap)
                .GetMethod(nameof(RedirectRoot), BindingFlags.Static | BindingFlags.NonPublic));
            try
            {
                harmony.Patch(_weaponsFolder, postfix: post);
                harmony.Patch(_spritesFolder, postfix: post);
                harmony.Patch(_soundsFolder, postfix: post);
                Plugin.Log.LogInfo("[Forge] content roots are redirectable — host weapons can be applied");
            }
            catch (Exception e)
            {
                Available = false;
                Plugin.Log.LogWarning($"[Forge] could not redirect the content roots ({e.Message}) — " +
                    "host content will not be applied");
            }
        }

        /// <summary>
        /// One postfix for all three roots. The folder NAME is taken from what WeaponForge itself
        /// returned rather than hardcoded here, so if it ever renames "sprites" this follows
        /// along instead of silently pointing at a folder that does not exist.
        /// </summary>
        private static void RedirectRoot(ref string __result)
        {
            var root = _rootOverride;
            if (root == null || string.IsNullOrEmpty(__result)) return;
            try
            {
                var leaf = Path.GetFileName(__result.TrimEnd(Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar));
                if (!string.IsNullOrEmpty(leaf)) __result = Path.Combine(root, leaf);
            }
            catch { /* leave the original root: the player's own content is the safe answer */ }
        }

        // -----------------------------------------------------------------------------------
        // Swap / restore
        // -----------------------------------------------------------------------------------

        /// <summary>
        /// Apply the host's materialised content set. Lobby only — see <see cref="Reload"/>.
        /// Returns false when nothing was applied, which is never fatal: the go-live digest still
        /// refuses a run whose content differs, so a failed swap costs the session its custom
        /// weapons, not its integrity.
        /// </summary>
        internal static bool SwapTo(string activeRoot)
        {
            Probe();
            if (!Available || !_patched) return false;
            if (string.IsNullOrEmpty(activeRoot) || !Directory.Exists(activeRoot)) return false;
            if (string.Equals(_rootOverride, activeRoot, StringComparison.OrdinalIgnoreCase)) return true;

            var previous = _rootOverride;
            _rootOverride = activeRoot;
            if (Reload("host content"))
            {
                Plugin.Log.LogInfo($"[Forge] now using the host's content set at {activeRoot}");
                return true;
            }
            _rootOverride = previous;
            Reload("rollback");
            return false;
        }

        /// <summary>Put the player's own content back. Safe to call when nothing was swapped.</summary>
        internal static bool Restore()
        {
            if (!Available || !_patched || _rootOverride == null) return true;
            _rootOverride = null;
            bool ok = Reload("the player's own content");
            if (ok) Plugin.Log.LogInfo("[Forge] the player's own weapons are back");
            return ok;
        }

        /// <summary>
        /// Tear WeaponForge's content down and build it again from whatever the roots now resolve
        /// to.
        ///
        /// LOBBY ONLY. A rebuild replaces the ModuleData and Sprite objects that installed
        /// <c>Module</c>s hold references to, so doing this mid-run would leave live ship parts
        /// pointing at content that is no longer registered. Callers gate on session state; this
        /// is not the place to discover it.
        ///
        /// The order matters and each step exists for a reason:
        ///
        ///   1. Drop the previously-registered forge modules from the game's registry. Their
        ///      RegisterInto SKIPS ids already present, so without this the old and new sets
        ///      coexist and the module digest can never match the host's.
        ///   2. Clear their own caches, including the sprite/sound `_loaded` latches — both
        ///      loaders return immediately once loaded, so a swap without this reads the new
        ///      weapons against the OLD sprites and sounds. That is the failure that looks like
        ///      it worked.
        ///   3. Rebuild and re-register, then Initialize() so the registry's id lookup is rebuilt.
        ///
        /// Unity objects from the outgoing set are deliberately NOT destroyed. Destroying them is
        /// what turns a stale reference into a crash, and a session performs at most two swaps —
        /// a few MB held until the process exits is the cheaper mistake.
        /// </summary>
        private static bool Reload(string what)
        {
            try
            {
                var registry = ResolveModuleRegistry();
                if (registry == null)
                {
                    Plugin.Log.LogWarning("[Forge] no ModuleRegistry available — content not reloaded");
                    return false;
                }

                int removed = DropForgeModules(registry);
                ClearCollection(_registry, "_entries");
                ClearCollection(_registry, "_builtNames");
                ResetLoader(_sprites, "_loaded", "_sprites", "_anims", "_sheets");
                ResetLoader(_sounds, "_loaded", "_entries");

                _buildAll.Invoke(null, null);
                _registerInto.Invoke(null, new object[] { registry });
                registry.Initialize();

                int now = CountForgeModules();
                Plugin.Log.LogInfo($"[Forge] reloaded for {what}: dropped {removed}, registered {now}");
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Forge] content reload failed ({e.Message}) — " +
                    "the module digest will refuse the run rather than let content diverge");
                return false;
            }
        }

        private static ModuleRegistry ResolveModuleRegistry()
        {
            try { return ServiceLocator.TryGet<ModuleRegistry>(out var registry) ? registry : null; }
            catch { return null; }
        }

        /// <summary>
        /// Remove every module WeaponForge put into the game's registry, straight out of the
        /// private itemList that <c>ScriptableObjectRegistry</c> keeps. Identified by asking
        /// WeaponForge which ids are its own rather than by a name prefix, so a pack that names
        /// its weapons anything at all is still fully removed.
        /// </summary>
        private static int DropForgeModules(ModuleRegistry registry)
        {
            var ours = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var weaponIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ForgeBridge.CollectForgeIds(ours, weaponIds);
            if (ours.Count == 0) return 0;

            var field = AccessTools.Field(typeof(ScriptableObjectRegistry<ModuleData, string>), "itemList");
            if (field == null || !(field.GetValue(registry) is IList list)) return 0;

            int removed = 0;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var module = list[i] as ModuleData;
                if (module == null) continue;
                string id = null;
                try { id = module.Id; } catch { }
                if (id != null && ours.Contains(id)) { list.RemoveAt(i); removed++; }
            }
            return removed;
        }

        private static int CountForgeModules()
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var weapons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ForgeBridge.CollectForgeIds(ids, weapons);
            return ids.Count;
        }

        private static void ClearCollection(Type owner, string fieldName)
        {
            if (owner == null) return;
            try
            {
                var value = AccessTools.Field(owner, fieldName)?.GetValue(null);
                // List<T>, HashSet<T> and Dictionary<K,V> share no interface with Clear() in
                // common, so reach the method on the concrete type.
                value?.GetType().GetMethod("Clear", Type.EmptyTypes)?.Invoke(value, null);
            }
            catch { }
        }

        /// <summary>Clear a loader's caches and drop its one-shot `_loaded` latch, so the next
        /// LoadAll actually reads the folder instead of returning immediately.</summary>
        private static void ResetLoader(Type owner, string latch, params string[] caches)
        {
            if (owner == null) return;
            foreach (var c in caches) ClearCollection(owner, c);
            try { AccessTools.Field(owner, latch)?.SetValue(null, false); } catch { }
        }
    }
}
