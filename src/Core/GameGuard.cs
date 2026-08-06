using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace PunkMultiverse.Core
{
    /// <summary>
    /// Answers one question at boot: is this the game build the mod was made for?
    ///
    /// Steam updates the base game silently and underneath us. When it does, the mod keeps
    /// loading and the failure shows up later as behaviour nobody can explain — a patch that no
    /// longer fires, a value that reads wrong. The point of this check is to put a name on that
    /// before it costs a debugging session.
    ///
    /// Scope, stated honestly because the log says the same thing: this verifies that the game
    /// members the mod depends on still EXIST with the same arity. It cannot see a method whose
    /// body changed while keeping its signature — that needs the IL comparison in
    /// tools/gamescan.ps1, which has the previous build's hashes to compare against and no
    /// startup-time budget to respect.
    ///
    /// Log-only, always. A base-game update is not a reason to refuse to load.
    /// </summary>
    internal static class GameGuard
    {
        internal static bool BaselineMatches { get; private set; } = true;

        public static void Run()
        {
            try { Check(); }
            catch (Exception e)
            {
                // This is a diagnostic. It must never be the reason the mod failed to start.
                Plugin.Log.LogWarning($"[GameScan] baseline check skipped: {e.Message}");
            }
        }

        private static void Check()
        {
            // Ship is an ordinary game type, so this resolves the game assembly without a
            // by-name lookup that could quietly find the wrong one.
            var game = typeof(Ship).Assembly;
            var mvid = game.ManifestModule.ModuleVersionId.ToString();

            if (string.Equals(mvid, GameBaseline.Mvid, StringComparison.OrdinalIgnoreCase))
            {
                Plugin.Log.LogInfo($"[GameScan] game matches baseline ({GameBaseline.GameVersion}).");
                return;
            }

            BaselineMatches = false;
            Plugin.Log.LogWarning(
                $"[GameScan] the base game has CHANGED since this mod was built " +
                $"(baseline {GameBaseline.GameVersion}, module {Short(GameBaseline.Mvid)} -> {Short(mvid)}).");

            var missingTypes = new List<string>();
            foreach (var name in GameBaseline.Types)
                if (Resolve(game, name) == null)
                    missingTypes.Add(name);

            var missingMembers = new List<string>();
            foreach (var entry in GameBaseline.Members)
            {
                var parts = entry.Split('|');
                if (parts.Length != 3) continue;

                var type = Resolve(game, parts[0]);
                // A member of a type that is already reported missing is not a separate finding.
                if (type == null) continue;

                if (!HasMember(type, parts[1], int.Parse(parts[2])))
                    missingMembers.Add($"{parts[0]}.{parts[1]}" + (parts[2] == "-1" ? "" : $"/{parts[2]}"));
            }

            if (missingTypes.Count == 0 && missingMembers.Count == 0)
            {
                Plugin.Log.LogWarning(
                    $"[GameScan] all {GameBaseline.Types.Length} types and {GameBaseline.Members.Length} members " +
                    "the mod uses are still present. Signatures are intact, but a changed method BODY " +
                    "would not show up here — run tools/gamescan.ps1 if something behaves oddly.");
                return;
            }

            Plugin.Log.LogError(
                $"[GameScan] {missingTypes.Count} type(s) and {missingMembers.Count} member(s) the mod " +
                "depends on are GONE. Expect patches to fail. Run tools/gamescan.ps1 for the full report.");

            foreach (var t in missingTypes.Take(20))
                Plugin.Log.LogError($"[GameScan]   missing type   {t}");
            foreach (var m in missingMembers.Take(40))
                Plugin.Log.LogError($"[GameScan]   missing member {m}");

            var hidden = Math.Max(0, missingTypes.Count - 20) + Math.Max(0, missingMembers.Count - 40);
            if (hidden > 0) Plugin.Log.LogError($"[GameScan]   ...and {hidden} more");
        }

        /// <summary>The baseline spells nested types the way Cecil does ('/'); reflection wants '+'.</summary>
        private static Type Resolve(Assembly game, string name) =>
            game.GetType(name.Replace('/', '+'), throwOnError: false);

        private static bool HasMember(Type type, string name, int arity)
        {
            const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic |
                                       BindingFlags.Instance | BindingFlags.Static |
                                       BindingFlags.DeclaredOnly;

            // Walk the base chain: a patch declared against a subclass often targets a member the
            // subclass inherits (ModulePickup.Update actually lives on InteractiblePickup<T>).
            for (var t = type; t != null; t = t.BaseType)
            {
                var members = t.GetMember(name, Flags);
                if (members.Length == 0) continue;
                if (arity < 0) return true;

                foreach (var m in members)
                    if (m is MethodBase method && method.GetParameters().Length == arity)
                        return true;

                // A member of that name exists but with no matching arity. Keep walking — an
                // overload on a base type may still satisfy it.
            }
            return false;
        }

        private static string Short(string mvid) =>
            string.IsNullOrEmpty(mvid) ? "?" : mvid.Substring(0, Math.Min(8, mvid.Length));
    }
}
