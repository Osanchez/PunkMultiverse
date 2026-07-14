using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using HarmonyLib;
using PunkMultiverse.Sync;
using UnityEngine;
using UnityEngine.InputSystem;

namespace PunkMultiverse.Core
{
    /// <summary>
    /// Dev/test harness hooks, all gated behind [Dev] config flags (off by default):
    ///
    /// 1. DebugMenuKey — F1 opens the game's built-in developer debug menu (spawn lists,
    ///    noclip, loadouts, free camera). Ported from the standalone PunkDebugKey mod's
    ///    crash-safe design: a postfix on DebugMenu.Update replays the menu's own open
    ///    branch, so the game's Close() path reverses it cleanly. Menu spawns replicate
    ///    to every peer automatically via MinionSync's generic CreateEntity capture.
    ///
    /// 2. CommandFile — scripted scenario driving for automated repro tests. When set, the
    ///    mod polls the file (in the plugin folder) twice a second, executes each line, and
    ///    truncates it. An external harness (or Claude) writes commands and reads the log:
    ///        spawn &lt;EntityId&gt; [x y]        spawn at world pos (default: ship + (3,0))
    ///        spawn &lt;EntityId&gt; rel dx dy    spawn relative to the local ship
    ///        tp &lt;x&gt; &lt;y&gt; | tp rel dx dy    teleport the local ship
    ///        autofly &lt;seconds&gt;            re-arm the AutoFly scripted flight
    ///        say &lt;text&gt;                   echo a marker line into the log
    ///    Every execution logs "[Dev] ..." so scenarios are assertable from LogOutput.log.
    /// </summary>
    internal static class DevTools
    {
        private static float _nextPollAt;
        private static bool _warnedPath;

        public static void Tick(NetSession session)
        {
            string file = NetConfig.CommandFile != null ? NetConfig.CommandFile.Value : "";
            if (string.IsNullOrEmpty(file)) return;
            float now = Time.unscaledTime;
            if (now < _nextPollAt) return;
            _nextPollAt = now + 0.5f;

            string path;
            try { path = Path.IsPathRooted(file) ? file : Path.Combine(ModFolder.Dir, file); }
            catch { return; }
            string[] lines;
            try
            {
                if (!File.Exists(path)) return;
                lines = File.ReadAllLines(path);
                if (lines.Length == 0) return;
                File.WriteAllText(path, ""); // consumed — the harness appends fresh commands
            }
            catch (IOException) { return; } // writer holds the file — retry next poll
            catch (Exception e)
            {
                if (!_warnedPath) { _warnedPath = true; Plugin.Log.LogWarning($"[Dev] command file unreadable: {e.Message}"); }
                return;
            }

            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                try { Execute(session, line); }
                catch (Exception e) { Plugin.Log.LogWarning($"[Dev] command '{line}' failed: {e.Message}"); }
            }
        }

        private static void Execute(NetSession session, string line)
        {
            var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            switch (parts[0].ToLowerInvariant())
            {
                case "say":
                    Plugin.Log.LogInfo($"[Dev] say: {line.Substring(3).Trim()}");
                    return;
                case "autofly":
                    if (parts.Length >= 2 && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float secs))
                    {
                        NetConfig.AutoFly.Value = secs;
                        session.RearmAutoFly(secs);
                        Plugin.Log.LogInfo($"[Dev] autofly {secs:0.0}s");
                    }
                    return;
                case "tp":
                {
                    var ship = ShipSync.LocalShip;
                    if (ship == null) { Plugin.Log.LogWarning("[Dev] tp: no local ship"); return; }
                    if (!TryParsePos(parts, 1, (Vector2)ship.transform.position, out var pos))
                    { Plugin.Log.LogWarning($"[Dev] tp: bad args in '{line}'"); return; }
                    ship.Unit.ComponentData.entity.MoveTo(pos);
                    var rb = ship.GetComponent<Rigidbody2D>();
                    if (rb != null)
                    {
                        RemoteEntityPuppet.TeleportWithChildren(rb, pos);
                        rb.linearVelocity = Vector2.zero;
                    }
                    ship.transform.position = pos;
                    Plugin.Log.LogInfo($"[Dev] tp -> {pos.x:0.0},{pos.y:0.0}");
                    return;
                }
                case "spawn":
                {
                    if (parts.Length < 2) { Plugin.Log.LogWarning("[Dev] spawn: missing EntityId"); return; }
                    if (session.State != SessionState.InGame) { Plugin.Log.LogWarning("[Dev] spawn: not in game"); return; }
                    var ship = ShipSync.LocalShip;
                    Vector2 basePos = ship != null ? (Vector2)ship.transform.position + new Vector2(3f, 0f) : Vector2.zero;
                    if (!TryParsePos(parts, 2, ship != null ? (Vector2)ship.transform.position : Vector2.zero, out var pos))
                        pos = basePos;

                    var egm = ServiceLocator.Get<EntityGameObjectManager>();
                    if (egm == null || egm.savablesCollection == null) { Plugin.Log.LogWarning("[Dev] spawn: no EGM"); return; }
                    foreach (var info in egm.savablesCollection.savableObjectInfos)
                    {
                        if (!string.Equals(info.entityId, parts[1], StringComparison.OrdinalIgnoreCase)) continue;
                        // CreateEntity rides MinionSync's generic runtime-spawn capture, so this
                        // spawn replicates to every peer with a proper runtime netId + authority.
                        egm.CreateEntity(info.prefab, pos);
                        Plugin.Log.LogInfo($"[Dev] spawned {info.entityId} at {pos.x:0.0},{pos.y:0.0}");
                        return;
                    }
                    Plugin.Log.LogWarning($"[Dev] spawn: unknown EntityId '{parts[1]}' (names are the prefab entityIds, e.g. Unit_Fly)");
                    return;
                }
                default:
                    Plugin.Log.LogWarning($"[Dev] unknown command '{parts[0]}' (spawn/tp/autofly/say)");
                    return;
            }
        }

        /// <summary>Parse "[rel] x y" starting at <paramref name="start"/>; rel is offset from
        /// <paramref name="origin"/>. False when args are present but malformed; when absent,
        /// false with pos=origin (callers pick their own default).</summary>
        private static bool TryParsePos(string[] parts, int start, Vector2 origin, out Vector2 pos)
        {
            pos = origin;
            if (parts.Length <= start) return false;
            bool rel = parts[start].Equals("rel", StringComparison.OrdinalIgnoreCase);
            int i = rel ? start + 1 : start;
            if (parts.Length < i + 2
                || !float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                || !float.TryParse(parts[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y))
                return false;
            pos = rel ? origin + new Vector2(x, y) : new Vector2(x, y);
            return true;
        }

        // ---------------------------------------------------------------- debug menu key
        // Crash-safe port of the standalone PunkDebugKey mod: fires only while the menu's own
        // Update runs, replays its open branch via reflection, never touches its InputActions.
        [HarmonyPatch(typeof(DebugMenu), "Update")]
        internal static class OpenDebugMenuKey
        {
            private static readonly FieldInfo IsOpenedF = AccessTools.Field(typeof(DebugMenu), "isOpened");
            private static readonly FieldInfo ScreenF = AccessTools.Field(typeof(DebugMenu), "screen");
            private static readonly FieldInfo TimeMgrF = AccessTools.Field(typeof(DebugMenu), "timeManager");
            private static readonly FieldInfo WeaponDropF = AccessTools.Field(typeof(DebugMenu), "weaponDropdown");
            private static readonly MethodInfo SetHoverM = AccessTools.Method(typeof(DebugMenu), "SetShipsHovering");
            private static bool _warned;

            private static void Postfix(DebugMenu __instance)
            {
                if (NetConfig.DebugMenuKey == null || !NetConfig.DebugMenuKey.Value) return;
                var kb = Keyboard.current;
                if (kb == null || !kb.f1Key.wasPressedThisFrame) return;
                try
                {
                    if (IsOpenedF == null || (bool)IsOpenedF.GetValue(__instance)) return;
                    IsOpenedF.SetValue(__instance, true);
                    ServiceLocator.Get<ShipManager>()?.DisableShipControl();
                    SetHoverM?.Invoke(__instance, new object[] { true });
                    (TimeMgrF?.GetValue(__instance) as TimeManager)?.SetTimeScale(0.1f, __instance);
                    (ScreenF?.GetValue(__instance) as UIScreen)?.Open();
                    (WeaponDropF?.GetValue(__instance) as WeaponDropdown)?.Refresh();
                    Plugin.Log.LogInfo("[Dev] debug menu opened (F1)");
                }
                catch (Exception e)
                {
                    if (!_warned) { _warned = true; Plugin.Log.LogWarning($"[Dev] F1 debug menu open failed: {e.Message}"); }
                }
            }
        }
    }
}
