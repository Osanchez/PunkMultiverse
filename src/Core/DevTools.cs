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
    ///        poke &lt;netId&gt; [amount]         routed damage (wakes dormant, requests to owner)
    ///        entities [radius]            structured nearby-entity dump -> devout.txt
    ///        status                       session/ship state -> devout.txt
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

        // fire <seconds>: hold the local ship's trigger via the game's own Shooter API
        // (SetShooting — what every AI ShootAction uses); weapons without a Shooter get the
        // IsTriggerPulled+Warmup fallback. Driven every frame, independent of the poll gate.
        private static float _fireUntil;
        private static Shooter _fireShooter;
        private static WeaponBase _fireWeapon;

        private static void TickFire()
        {
            if (_fireUntil <= 0f) return;
            try
            {
                if (Time.unscaledTime >= _fireUntil)
                {
                    if (_fireShooter != null) _fireShooter.SetShooting(false);
                    if (_fireWeapon != null) _fireWeapon.IsTriggerPulled = false;
                    _fireShooter = null; _fireWeapon = null; _fireUntil = 0f;
                    Out("fire: stopped");
                    return;
                }
                if (_fireShooter != null) _fireShooter.SetShooting(true);
                else if (_fireWeapon != null)
                {
                    _fireWeapon.IsTriggerPulled = true;
                    _fireWeapon.Warmup(Time.deltaTime);
                }
            }
            catch { _fireShooter = null; _fireWeapon = null; _fireUntil = 0f; }
        }

        public static void Tick(NetSession session)
        {
            TickFire();
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
                catch (Exception e) { Out($"command '{line}' FAILED: {e.Message}"); }
            }
        }

        // ---------------------------------------------------------------- response channel
        // Structured results for the driving harness: every command's outcome is appended to
        // devout.txt next to the command file (the log stays the human-readable mirror). The
        // harness truncates the file after reading.
        private static void Out(string text)
        {
            Plugin.Log.LogInfo($"[Dev] {text}");
            try
            {
                File.AppendAllText(Path.Combine(ModFolder.Dir, "devout.txt"),
                    $"[{Time.unscaledTime:0.000}] {text}\n");
            }
            catch { }
        }

        private static void Execute(NetSession session, string line)
        {
            var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            switch (parts[0].ToLowerInvariant())
            {
                case "say":
                    Out($"say: {line.Substring(3).Trim()}");
                    return;
                case "status":
                {
                    var ship = ShipSync.LocalShip;
                    string pos = ship != null
                        ? $"{ship.transform.position.x:0.0},{ship.transform.position.y:0.0}" : "none";
                    string dead = ship != null && ship.IsDead ? " DEAD" : "";
                    Out($"status v{PluginVersionInfo.Version} state={session.State} slot={session.LocalSlot} " +
                        $"host={session.IsHost} ship={pos}{dead}");
                    return;
                }
                case "entities":
                {
                    var ship = ShipSync.LocalShip;
                    if (ship == null) { Out("entities: no local ship"); return; }
                    float radius = 60f;
                    if (parts.Length >= 2)
                        float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out radius);
                    Vector2 origin = ship.transform.position;
                    int reported = 0;
                    foreach (var unit in UnityEngine.Object.FindObjectsOfType<Unit>())
                    {
                        if (unit == null) continue;
                        Vector2 pos = unit.transform.position;
                        float dist = Vector2.Distance(origin, pos);
                        if (dist > radius) continue;
                        if (++reported > 30) { Out("entities: ...truncated at 30"); break; }
                        var shipComp = unit.GetComponent<Ship>();
                        if (shipComp != null)
                        {
                            // Ships live outside the entity manifest (ShipSync owns them).
                            var rp = shipComp.GetComponent<RemotePuppet>();
                            string who = shipComp == ship ? $"P{session.LocalSlot + 1}(local)"
                                : rp != null ? $"P{rp.Slot + 1}(puppet)" : "?";
                            var sdr = shipComp.GetComponent<DamagableResource>();
                            float shp = -1f;
                            try { if (sdr != null && sdr.MaxHealth > 0) shp = sdr.CurrentHealth / sdr.MaxHealth; } catch { }
                            Out($"ship {who} pos={pos.x:0.0},{pos.y:0.0} dist={dist:0.0} hp={shp:0.00}" +
                                (shipComp.IsDead ? " DEAD" : ""));
                            continue;
                        }
                        EnemySync.TryGetNetId(unit, out int netId);
                        var se = unit.GetComponentInParent<SavableEntity>();
                        string type = se != null && se.EntityData != null ? se.EntityData.entityId : unit.name;
                        byte owner = netId != 0 ? EnemySync.OwnerOf(netId) : (byte)255;
                        bool puppet = unit.GetComponent<RemoteEntityPuppet>() != null
                                      || unit.GetComponent<RemotePuppet>() != null;
                        var dr = unit.GetComponent<DamagableResource>();
                        float hp = -1f;
                        try { if (dr != null && dr.MaxHealth > 0) hp = dr.CurrentHealth / dr.MaxHealth; } catch { }
                        byte fire = UnitStatus.ReadFireState(unit);
                        Out($"entity #{netId} {type} pos={pos.x:0.0},{pos.y:0.0} dist={dist:0.0} " +
                            $"owner={(owner == 255 ? "dormant" : "P" + (owner + 1))}{(puppet ? " puppet" : "")} " +
                            $"hp={hp:0.00} fire={fire}");
                    }
                    if (reported == 0) Out($"entities: none within {radius:0}");
                    return;
                }
                case "fire":
                {
                    var ship = ShipSync.LocalShip;
                    if (ship == null) { Out("fire: no local ship"); return; }
                    float fireSecs = 2f;
                    if (parts.Length >= 2)
                        float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out fireSecs);
                    if (fireSecs <= 0f) { _fireUntil = Time.unscaledTime; TickFire(); return; } // fire 0 = stop
                    _fireShooter = ship.GetComponentInChildren<Shooter>(true);
                    _fireWeapon = _fireShooter == null ? ship.PrimaryWeapon : null;
                    if (_fireShooter == null && _fireWeapon == null) { Out("fire: no shooter/weapon on ship"); return; }
                    _fireUntil = Time.unscaledTime + fireSecs;
                    Out($"fire: {fireSecs:0.0}s via {(_fireShooter != null ? "Shooter" : "PrimaryWeapon")}");
                    return;
                }
                case "poke":
                {
                    if (parts.Length < 2 || !int.TryParse(parts[1], out int netId))
                    { Out("poke: usage poke <netId> [amount]"); return; }
                    float amount = 5f;
                    if (parts.Length >= 3)
                        float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out amount);
                    if (!NetIds.TryGetInstanceId(netId, out int instanceId))
                    { Out($"poke: netId {netId} unknown"); return; }
                    var egm = ServiceLocator.Get<EntityGameObjectManager>();
                    if (egm == null || !egm.TryGetSavableEntity(instanceId, out var se) || se == null)
                    { Out($"poke: #{netId} has no live object here"); return; }
                    var dr = se.GetComponent<DamagableResource>();
                    if (dr == null) { Out($"poke: #{netId} not damagable"); return; }
                    // Typeless Damage through TakeDamage — the ROUTED path: puppets forward a
                    // damage request to the owner, dormant targets queue a claim (wake-on-hit),
                    // owned targets apply locally. Exactly what a projectile hit exercises,
                    // minus the projectile.
                    dr.TakeDamage(new Damage(amount, null));
                    Out($"poke: #{netId} hit for {amount:0.#} (owner=" +
                        $"{(EnemySync.OwnerOf(netId) == 255 ? "dormant" : "P" + (EnemySync.OwnerOf(netId) + 1))})");
                    return;
                }
                case "autofly":
                    if (parts.Length >= 2 && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float secs))
                    {
                        NetConfig.AutoFly.Value = secs;
                        session.RearmAutoFly(secs);
                        Out($"autofly {secs:0.0}s");
                    }
                    return;
                case "tp":
                {
                    var ship = ShipSync.LocalShip;
                    if (ship == null) { Out("tp: no local ship"); return; }
                    if (!TryParsePos(parts, 1, (Vector2)ship.transform.position, out var pos))
                    { Out($"tp: bad args in '{line}'"); return; }
                    ship.Unit.ComponentData.entity.MoveTo(pos);
                    var rb = ship.GetComponent<Rigidbody2D>();
                    if (rb != null)
                    {
                        RemoteEntityPuppet.TeleportWithChildren(rb, pos);
                        rb.linearVelocity = Vector2.zero;
                    }
                    ship.transform.position = pos;
                    Out($"tp -> {pos.x:0.0},{pos.y:0.0}");
                    return;
                }
                case "spawn":
                {
                    if (parts.Length < 2) { Out("spawn: missing EntityId"); return; }
                    if (session.State != SessionState.InGame) { Out("spawn: not in game"); return; }
                    var ship = ShipSync.LocalShip;
                    Vector2 basePos = ship != null ? (Vector2)ship.transform.position + new Vector2(3f, 0f) : Vector2.zero;
                    if (!TryParsePos(parts, 2, ship != null ? (Vector2)ship.transform.position : Vector2.zero, out var pos))
                        pos = basePos;

                    var egm = ServiceLocator.Get<EntityGameObjectManager>();
                    if (egm == null || egm.savablesCollection == null) { Out("spawn: no EGM"); return; }
                    foreach (var info in egm.savablesCollection.savableObjectInfos)
                    {
                        if (!string.Equals(info.entityId, parts[1], StringComparison.OrdinalIgnoreCase)) continue;
                        // CreateEntity rides MinionSync's generic runtime-spawn capture, so this
                        // spawn replicates to every peer with a proper runtime netId + authority.
                        egm.CreateEntity(info.prefab, pos);
                        Out($"spawned {info.entityId} at {pos.x:0.0},{pos.y:0.0}");
                        return;
                    }
                    Out($"spawn: unknown EntityId '{parts[1]}' (names are the prefab entityIds, e.g. Unit_Fly)");
                    return;
                }
                default:
                    Out($"unknown command '{parts[0]}' (spawn/tp/poke/entities/status/autofly/say)");
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
