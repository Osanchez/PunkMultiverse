using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace PunkMultiverse.Core
{
    /// <summary>
    /// The local BepInEx plugin set as a canonical string ("guid@version;guid@version;…",
    /// sorted). Sent in the HELLO so the host can compare mod sets — other mods can change
    /// gameplay rules or conflict with the netcode's patches, so the host decides via
    /// NetConfig.ModManifestPolicy whether a differing set is rejected, flagged, or ignored.
    /// Collected from BepInPlugin attributes across loaded assemblies (chainloader-agnostic).
    /// </summary>
    internal static class ModManifest
    {
        private static string _local;

        private static readonly string[] SkipPrefixes =
        {
            "mscorlib", "System", "netstandard", "Unity", "Mono.", "0Harmony", "BepInEx",
            "Newtonsoft", "Punk.", "Sirenix", "UniTask", "com.rlabrecque", "ProCamera2D",
            "ServiceLocator", "Assembly-CSharp",
        };

        public static string Local => _local ?? (_local = Build());

        private static string Build()
        {
            var entries = new List<string>();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name;
                try { name = asm.GetName().Name; }
                catch { continue; }
                if (string.IsNullOrEmpty(name)) continue;
                bool skip = false;
                foreach (var prefix in SkipPrefixes)
                    if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { skip = true; break; }
                if (skip) continue;

                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types; }
                catch { continue; }
                foreach (var type in types)
                {
                    if (type == null) continue;
                    object[] attrs;
                    try { attrs = type.GetCustomAttributes(typeof(BepInEx.BepInPlugin), false); }
                    catch { continue; }
                    foreach (BepInEx.BepInPlugin bp in attrs)
                    {
                        // bp.Version is a SemanticVersioning.Version — read reflectively to
                        // avoid referencing that assembly (keeps the CI refs bundle unchanged).
                        string version = "?";
                        try { version = bp.GetType().GetProperty("Version")?.GetValue(bp, null)?.ToString() ?? "?"; }
                        catch { }
                        entries.Add($"{bp.GUID}@{version}");
                    }
                }
            }
            var manifest = string.Join(";", entries.Distinct().OrderBy(e => e, StringComparer.Ordinal));
            Plugin.Log.LogInfo($"[Mods] local manifest: {manifest}");
            return manifest;
        }

        /// <summary>
        /// Comma-separated mod ids for the `mods` column in the public server browser.
        ///
        /// These are CATALOG ids read from each plugin folder's mod.json, not BepInPlugin GUIDs,
        /// because the consumer is PUNK Nexus and a catalog id is what it can actually act on —
        /// look the mod up, show its name, install it for someone joining. A GUID would leave the
        /// client guessing which listing it belongs to. Folders with no mod.json fall back to their
        /// plugin GUID so a hand-built mod still shows up as something.
        ///
        /// Versions are dropped deliberately: the browser filters on identity, the HELLO handshake
        /// is what actually enforces version agreement, and Steam caps lobby metadata size.
        /// </summary>
        public static string BrowserList(int max = 12)
        {
            var ids = new List<string>();
            var withCatalogId = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                var pluginRoot = BepInEx.Paths.PluginPath;
                if (Directory.Exists(pluginRoot))
                    foreach (var dir in Directory.GetDirectories(pluginRoot).OrderBy(d => d, StringComparer.Ordinal))
                    {
                        var id = ReadCatalogId(Path.Combine(dir, "mod.json"));
                        if (string.IsNullOrEmpty(id)) continue;
                        if (!ids.Contains(id)) ids.Add(id);
                        withCatalogId.Add(Path.GetFullPath(dir));
                    }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Mods] could not read catalog ids: {e.Message}");
            }

            // Anything without a catalog id still deserves to be visible, so top up from the GUIDs.
            //
            // But skip the GUID of a plugin whose folder ALREADY supplied a catalog id, or the same
            // mod is advertised twice. Deduping by string cannot catch that: the two spellings of
            // this mod are "PunkMultiverse" and "com.osanchez.punkmultiverse", which share no
            // characters to compare. The live listing read back from Steam was
            //
            //     mods = PunkMultiverse,com.andy.weaponforge,com.osanchez.punkmultiverse
            //
            // -- this mod under both names. A browser resolving that list sees a mod it must
            // install twice, and the whole point of advertising catalog ids was that the list is
            // resolvable. Identity here is the plugin FOLDER, which is what mod.json describes and
            // what the GUID's assembly sits in.
            var claimed = ClaimedGuids(withCatalogId);
            foreach (var guid in (Local ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                         .Select(e => e.Split('@')[0])
                         .Where(g => !string.IsNullOrEmpty(g)))
                if (ids.Count < max && !ids.Contains(guid) && !claimed.Contains(guid)) ids.Add(guid);

            return string.Join(",", ids.Take(max));
        }

        /// <summary>
        /// GUIDs of plugins living in a folder that already advertised a catalog id, so the GUID
        /// top-up can leave them out. Best effort: an assembly with no readable Location simply is
        /// not claimed, which lands on the old behaviour (listed twice) rather than on dropping a
        /// mod from the listing entirely. Over-listing is a cosmetic bug; under-listing would make
        /// a joinable server look incompatible.
        /// </summary>
        private static HashSet<string> ClaimedGuids(HashSet<string> folders)
        {
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (folders == null || folders.Count == 0) return claimed;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string dir;
                try
                {
                    if (asm.IsDynamic) continue;
                    var loc = asm.Location;
                    if (string.IsNullOrEmpty(loc)) continue;
                    dir = Path.GetFullPath(Path.GetDirectoryName(loc));
                }
                catch { continue; }
                if (!folders.Contains(dir)) continue;

                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types; }
                catch { continue; }
                foreach (var type in types)
                {
                    if (type == null) continue;
                    object[] attrs;
                    try { attrs = type.GetCustomAttributes(typeof(BepInEx.BepInPlugin), false); }
                    catch { continue; }
                    foreach (BepInEx.BepInPlugin bp in attrs)
                        if (!string.IsNullOrEmpty(bp.GUID)) claimed.Add(bp.GUID);
                }
            }
            return claimed;
        }

        /// <summary>
        /// Pulls the "id" out of a mod.json without a JSON parser — this assembly has no reference
        /// to one, and adding a dependency to read a single string would have to be carried by the
        /// CI reference bundle for no benefit.
        /// </summary>
        private static string ReadCatalogId(string manifestPath)
        {
            try
            {
                if (!File.Exists(manifestPath)) return null;
                var text = File.ReadAllText(manifestPath);
                var match = System.Text.RegularExpressions.Regex.Match(
                    text, "\"id\"\\s*:\\s*\"([^\"]+)\"");
                return match.Success ? match.Groups[1].Value : null;
            }
            catch
            {
                return null;
            }
        }

        public static bool Matches(string theirs) =>
            string.Equals(Local, theirs ?? "", StringComparison.Ordinal);

        /// <summary>Human-readable difference, both directions, truncated for messages.</summary>
        public static string Describe(string theirs)
        {
            var mine = new HashSet<string>((Local ?? "").Split(';'));
            var other = new HashSet<string>((theirs ?? "").Split(';'));
            var onlyThem = other.Except(mine).OrderBy(s => s).ToList();
            var onlyUs = mine.Except(other).OrderBy(s => s).ToList();
            var parts = new List<string>();
            if (onlyThem.Count > 0)
                parts.Add("joiner has: " + string.Join(", ", onlyThem.Take(4)) + (onlyThem.Count > 4 ? ", …" : ""));
            if (onlyUs.Count > 0)
                parts.Add("host has: " + string.Join(", ", onlyUs.Take(4)) + (onlyUs.Count > 4 ? ", …" : ""));
            return parts.Count > 0 ? string.Join("; ", parts) : "same plugins, different versions";
        }
    }
}
