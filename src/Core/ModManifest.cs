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

            try
            {
                var pluginRoot = BepInEx.Paths.PluginPath;
                if (Directory.Exists(pluginRoot))
                    foreach (var dir in Directory.GetDirectories(pluginRoot).OrderBy(d => d, StringComparer.Ordinal))
                    {
                        var id = ReadCatalogId(Path.Combine(dir, "mod.json"));
                        if (!string.IsNullOrEmpty(id) && !ids.Contains(id)) ids.Add(id);
                    }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Mods] could not read catalog ids: {e.Message}");
            }

            // Anything without a catalog id still deserves to be visible, so top up from the GUIDs.
            foreach (var guid in (Local ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                         .Select(e => e.Split('@')[0])
                         .Where(g => !string.IsNullOrEmpty(g)))
                if (ids.Count < max && !ids.Contains(guid)) ids.Add(guid);

            return string.Join(",", ids.Take(max));
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
