using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using PunkMultiverse.Core;
using PunkMultiverse.Protocol;
using PunkMultiverse.Transport;
using UnityEngine;
using UnityEngine.InputSystem;

namespace PunkMultiverse.Sync
{
    /// <summary>
    /// Player-ship replication. Spawns puppet Ships for remote players (PunkFourPlayer's proven
    /// reflection recipe into ShipManager), applies lobby colors as runtime ShipThemes, streams
    /// the local ship at ShipStateHz, and routes received snapshots into RemotePuppets.
    /// </summary>
    internal static class ShipSync
    {
        /// <summary>slot -> ship (index 0..3; local slot points at the vanilla-spawned local ship).</summary>
        public static readonly Dictionary<int, Ship> ShipsBySlot = new Dictionary<int, Ship>();
        public static Ship LocalShip;

        private static float _nextSendAt;
        private static float _nextCameraSweepAt;
        private static readonly NetWriter Writer = new NetWriter(256);

        public static void Reset()
        {
            ShipsBySlot.Clear();
            LocalShip = null;
        }

        // ---------------------------------------------------------------- puppet spawning

        // After vanilla spawns the local ship's GameObject, place + spawn one puppet Ship per
        // remote player. Runs on every client; each client has its own local ship + N puppets.
        [HarmonyPatch(typeof(ShipManager), "SpawnShipGameObjects")]
        internal static class SpawnPuppets
        {
            private static void Postfix(ShipManager __instance, Level level, RunArguments runArguments)
            {
                var session = NetSession.Instance;
                if (session == null || !NetSession.Active || session.State != SessionState.Loading) return;
                try
                {
                    Spawn(__instance, level, session);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"[Ships] puppet spawn failed: {e}");
                }
            }

            private static void Spawn(ShipManager sm, Level level, NetSession session)
            {
                Reset();
                if (sm.Ships.Count == 0)
                {
                    Plugin.Log.LogError("[Ships] no local ship spawned?");
                    return;
                }
                LocalShip = sm.Ships[0];
                ShipsBySlot[session.LocalSlot] = LocalShip;

                var smT = typeof(ShipManager);
                var entityManager = AccessTools.Field(smT, "entityManager").GetValue(sm);
                var getShipsM = AccessTools.Method(entityManager.GetType(), "GetShips");
                var placeM = AccessTools.Method(smT, "PlaceShipEntity");
                var spawnM = AccessTools.Method(smT, "Spawn", new[] { typeof(EntityData), typeof(Ship), typeof(InputDevice), typeof(bool) });
                var shipsConfig = AccessTools.Field(smT, "shipsConfig").GetValue(sm) as ShipsConfig;
                var prefab = shipsConfig != null ? shipsConfig.AutoSwichShipPrefab : null;
                var loadout = RunStarter.CurrentLoadout;
                if (prefab == null || loadout == null)
                {
                    Plugin.Log.LogError("[Ships] missing prefab/loadout for puppet spawn");
                    return;
                }

                var remoteSlots = session.Players.Where(p => p != null && !p.IsLocal).Select(p => (int)p.Slot).OrderBy(s => s).ToList();
                Vector2 c = level.graph.StartNode.center;
                Vector2[] off = { Vector2.up * 2f, Vector2.down * 2f, Vector2.left * 2f, Vector2.right * 2f };

                var known = new HashSet<EntityData>(sm.Ships.Select(s => s.SavableEntity.EntityData));
                foreach (var slot in remoteSlots)
                {
                    var p = c + off[slot % off.Length];
                    placeM.Invoke(sm, new object[] { new Vector3(p.x, p.y, 0f), loadout });
                    var entity = (getShipsM.Invoke(entityManager, null) as IEnumerable<EntityData>)
                        .FirstOrDefault(e => !known.Contains(e));
                    if (entity == null)
                    {
                        Plugin.Log.LogError($"[Ships] placed entity for slot {slot} not found");
                        continue;
                    }
                    known.Add(entity);

                    spawnM.Invoke(sm, new object[] { entity, prefab, null, false });
                    var ship = sm.Ships[sm.Ships.Count - 1];
                    var puppet = ship.gameObject.AddComponent<RemotePuppet>();
                    puppet.Slot = (byte)slot;
                    ShipsBySlot[slot] = ship;
                    Plugin.Log.LogInfo($"[Ships] puppet spawned for slot {slot}");
                }

                ApplyThemes(sm, session);
            }
        }

        private static readonly Dictionary<int, ShipTheme> ThemeCache = new Dictionary<int, ShipTheme>();

        private static void ApplyThemes(ShipManager sm, NetSession session)
        {
            var assignThemeM = AccessTools.Method(typeof(ShipManager), "AssignTheme");
            foreach (var p in session.Players)
            {
                if (p == null || !ShipsBySlot.TryGetValue(p.Slot, out var ship) || ship == null) continue;
                if (!ThemeCache.TryGetValue(p.ColorIndex, out var theme))
                {
                    var color = PlayerColors.Get(p.ColorIndex);
                    theme = ScriptableObject.CreateInstance<ShipTheme>();
                    theme.spriteColor = color;
                    theme.boostParticleColor1 = color;
                    theme.boostParticleColor2 = Color.Lerp(color, Color.white, 0.4f);
                    ThemeCache[p.ColorIndex] = theme;
                }
                assignThemeM.Invoke(sm, new object[] { ship, theme });
                p.Ship = ship;
            }
        }

        // ---------------------------------------------------------------- game flow gates

        // Hold "press any button" until every peer generated an identical level (GO_LIVE),
        // and swallow re-entrant StartGame calls after we started (a late keypress on a
        // still-visible loading screen must not fire GameStarted twice).
        [HarmonyPatch(typeof(GameController), "StartGame")]
        internal static class GateStartGame
        {
            internal static GameController Controller;
            internal static bool Unlocked;
            internal static bool Started;

            private static bool Prefix(GameController __instance)
            {
                if (!NetSession.Active) return true;
                Controller = __instance;
                if (Started) return false;
                if (NetSession.Instance.State == SessionState.Loading && !Unlocked)
                {
                    Plugin.Log.LogInfo("[Run] StartGame held — waiting for all players (GO_LIVE)");
                    return false;
                }
                Started = true;
                return true;
            }
        }

        /// <summary>Called on GO_LIVE: unblock, start gameplay, and dismiss the loading screen
        /// (it only hides itself on its own input path, not on a programmatic StartGame).</summary>
        public static void ReleaseStartGate()
        {
            GateStartGame.Unlocked = true;
            var gc = GateStartGame.Controller ?? UnityEngine.Object.FindFirstObjectByType<GameController>();
            if (gc != null)
            {
                try { AccessTools.Method(typeof(GameController), "StartGame").Invoke(gc, null); }
                catch (Exception e) { Plugin.Log.LogError($"[Run] StartGame invoke failed: {e.Message}"); }
            }
            HideLoadingScreen();
        }

        private static void HideLoadingScreen()
        {
            try
            {
                var ls = UnityEngine.Object.FindFirstObjectByType<LoadingScreen>();
                if (ls == null) return;
                foreach (var name in new[] { "Close", "Hide" })
                {
                    var m = AccessTools.Method(typeof(LoadingScreen), name);
                    if (m != null && m.GetParameters().Length == 0)
                    {
                        m.Invoke(ls, null);
                        Plugin.Log.LogInfo($"[Run] loading screen dismissed via {name}()");
                        return;
                    }
                }
                var screen = Traverse.Create(ls).Field("screen").GetValue() as UIScreen;
                if (screen != null)
                {
                    screen.Close();
                    Plugin.Log.LogInfo("[Run] loading screen dismissed via UIScreen.Close()");
                    return;
                }
                ls.gameObject.SetActive(false);
                Plugin.Log.LogInfo("[Run] loading screen deactivated (fallback)");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Run] could not hide loading screen: {e.Message}");
            }
        }

        public static void ResetStartGate()
        {
            GateStartGame.Unlocked = false;
            GateStartGame.Started = false;
            GateStartGame.Controller = null;
        }

        // Puppets must not receive player HUDs (vanilla assigns huds[i] to Ships[i]).
        [HarmonyPatch(typeof(GameController), "AssignHuds")]
        internal static class NoPuppetHuds
        {
            private static void Postfix(GameController __instance)
            {
                if (!NetSession.Active) return;
                try
                {
                    var huds = AccessTools.Field(typeof(GameController), "huds").GetValue(__instance) as Array;
                    if (huds == null) return;
                    var sm = ServiceLocator.Get<ShipManager>();
                    for (int i = 0; i < huds.Length && i < sm.Ships.Count; i++)
                    {
                        if (sm.Ships[i] != null && sm.Ships[i].GetComponent<RemotePuppet>() != null
                            && huds.GetValue(i) is Component hud)
                        {
                            hud.gameObject.SetActive(false);
                            Plugin.Log.LogInfo($"[Ships] disabled HUD {i} (puppet ship)");
                        }
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"[Ships] HUD gating failed: {e.Message}");
                }
            }
        }

        // ---------------------------------------------------------------- snapshot TX/RX

        /// <summary>Called from NetSession.Update while InGame.</summary>
        public static void Tick(NetSession session)
        {
            SweepDistantCameraTargets();
            if (LocalShip == null || Time.unscaledTime < _nextSendAt) return;
            _nextSendAt = Time.unscaledTime + 1f / Mathf.Max(1f, NetConfig.ShipStateHz.Value);

            var rb = LocalShip.GetComponent<Rigidbody2D>();
            if (rb == null) return;
            var dr = LocalShip.GetComponent<DamagableResource>();
            var movement = LocalShip.GetComponent<ShipMovement>();
            var input = LocalShip.GetComponent<ShipInput>();

            var flags = ShipFlags.None;
            if (LocalShip.IsDead) flags |= ShipFlags.Dead;
            try { if (movement != null && movement.IsBoosted) flags |= ShipFlags.Boost; } catch { }

            float hp = 1f;
            try { if (dr != null && dr.MaxHealth > 0) hp = dr.CurrentHealth / dr.MaxHealth; } catch { }

            var msg = new ShipStateMsg
            {
                Slot = (byte)session.LocalSlot,
                Pos = rb.position,
                Vel = rb.linearVelocity,
                RotDeg = rb.rotation,
                Aim = input != null ? (Vector2)input.AimDirection : Vector2.right,
                Flags = flags,
                HpFraction = hp,
            };
            Writer.Reset();
            msg.Write(Writer);
            session.SendToAll(NetChannel.State, Writer.ToSegment(), reliable: false);
        }

        // POI camera targets activate off the AVERAGE alive-ship position — with the party split
        // across the map that midpoint can pull the local camera toward a POI nobody is at.
        // Anything farther than ~45u from the local ship has no business steering our camera.
        private static void SweepDistantCameraTargets()
        {
            if (LocalShip == null || Time.unscaledTime < _nextCameraSweepAt) return;
            _nextCameraSweepAt = Time.unscaledTime + 1f;
            try
            {
                var cam = Com.LuisPedroFonseca.ProCamera2D.ProCamera2D.Instance;
                if (cam == null) return;
                Vector2 local = LocalShip.transform.position;
                for (int i = cam.CameraTargets.Count - 1; i >= 0; i--)
                {
                    var t = cam.CameraTargets[i].TargetTransform;
                    if (t != null && Vector2.Distance(local, t.position) > 45f)
                        cam.RemoveCameraTarget(t);
                }
            }
            catch { }
        }

        /// <summary>Apply a snapshot from the wire (already relayed by the host if needed).</summary>
        public static void ApplyShipState(ShipStateMsg msg)
        {
            if (!ShipsBySlot.TryGetValue(msg.Slot, out var ship) || ship == null) return;
            var puppet = ship.GetComponent<RemotePuppet>();
            if (puppet == null) return; // our own echo — ignore

            puppet.PushSnapshot(Time.unscaledTime, msg.Pos, msg.Vel, msg.RotDeg, msg.Aim);
            puppet.SetBoosting((msg.Flags & ShipFlags.Boost) != 0);

            try
            {
                var dr = ship.GetComponent<DamagableResource>();
                if (dr != null && dr.MaxHealth > 0)
                {
                    float target = msg.HpFraction * dr.MaxHealth;
                    if (Mathf.Abs(dr.CurrentHealth - target) > 0.5f) dr.CurrentHealth = target;
                }
            }
            catch { }
        }
    }
}
