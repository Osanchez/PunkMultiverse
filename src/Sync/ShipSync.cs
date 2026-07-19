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
        private static readonly Dictionary<byte, byte> ViewSlotByPlayer = new Dictionary<byte, byte>();
        public static Ship LocalShip;

        private static float _nextSendAt;
        private static float _nextCameraSweepAt;
        private static readonly NetWriter Writer = new NetWriter(256);

        // Cached at the level-load spawn pass so mid-run (late-join) puppets use the same recipe.
        private static ShipManager _shipManager;
        private static Level _level;
        private static float _nextLateSpawnAt;

        public static void Reset()
        {
            ShipsBySlot.Clear();
            ViewSlotByPlayer.Clear();
            LastShipStateMs.Clear();
            LocalShip = null;
            _localAimer = null;
            _shipManager = null;
            _level = null;
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
                _shipManager = sm;
                _level = level;
                LocalShip = sm.Ships[0];
                ShipsBySlot[session.LocalSlot] = LocalShip;

                foreach (var p in session.Players.Where(p => p != null && p.Connected && !p.IsLocal).OrderBy(p => p.Slot))
                    SpawnPuppet(p.Slot);

                ApplyThemes(sm, session);
            }
        }

        /// <summary>Place + spawn one puppet Ship at the start station (reflection into ShipManager).</summary>
        private static bool SpawnPuppet(int slot)
        {
            var sm = _shipManager;
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
                return false;
            }

            Vector2 c = _level.graph.StartNode.center;
            Vector2[] off = { Vector2.up * 2f, Vector2.down * 2f, Vector2.left * 2f, Vector2.right * 2f };
            var known = new HashSet<EntityData>(sm.Ships.Where(s => s != null && s.SavableEntity != null)
                                                        .Select(s => s.SavableEntity.EntityData));
            var pos = c + off[slot % off.Length];
            placeM.Invoke(sm, new object[] { new Vector3(pos.x, pos.y, 0f), loadout });
            var entity = (getShipsM.Invoke(entityManager, null) as IEnumerable<EntityData>)
                .FirstOrDefault(e => !known.Contains(e));
            if (entity == null)
            {
                Plugin.Log.LogError($"[Ships] placed entity for slot {slot} not found");
                return false;
            }

            spawnM.Invoke(sm, new object[] { entity, prefab, null, false });
            var ship = sm.Ships[sm.Ships.Count - 1];
            var puppet = ship.gameObject.AddComponent<RemotePuppet>();
            puppet.Slot = (byte)slot;
            ShipsBySlot[slot] = ship;
            Plugin.Log.LogInfo($"[Ships] puppet spawned for slot {slot}");
            return true;
        }

        // A player who joined mid-run wasn't in the level-load spawn pass — give them a puppet
        // as soon as they show up in the roster.
        private static void EnsureLatePuppets(NetSession session)
        {
            if (_shipManager == null || _level == null || Time.unscaledTime < _nextLateSpawnAt) return;
            _nextLateSpawnAt = Time.unscaledTime + 1f;
            foreach (var p in session.Players)
            {
                // ContainsKey, not a null check: a destroyed-but-known ship is the
                // death/respawn flow, which owns that slot's lifecycle.
                if (p == null || !p.Connected || p.IsLocal || ShipsBySlot.ContainsKey(p.Slot)) continue;
                try
                {
                    if (SpawnPuppet(p.Slot)) ApplyThemes(_shipManager, session);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"[Ships] late puppet spawn failed for slot {p.Slot}: {e}");
                }
            }
        }

        /// <summary>Remove a disconnected player's puppet completely. Keeping it frozen leaves a
        /// targetable ship prop in the world and also prevents EnsureLatePuppets from creating a
        /// clean replacement when that slot reattaches.</summary>
        public static void RemoveRemoteShip(byte slot, string reason)
        {
            var session = NetSession.Instance;
            if (session != null && slot == session.LocalSlot) return;
            LastShipStateMs.Remove(slot);
            ShipsBySlot.TryGetValue(slot, out var ship);
            ShipsBySlot.Remove(slot);

            try
            {
                if (session != null && slot < session.Players.Count && session.Players[slot] != null)
                    session.Players[slot].Ship = null;
                // Recover from a lost dictionary entry too. Disconnect is rare, so an exhaustive
                // lookup is preferable to leaving a frozen, targetable ship prop in the scene.
                if (ship == null && _shipManager != null)
                    ship = _shipManager.Ships.FirstOrDefault(candidate => candidate != null
                        && candidate.GetComponent<RemotePuppet>()?.Slot == slot);
                if (ship == null)
                    ship = UnityEngine.Object.FindObjectsByType<RemotePuppet>(FindObjectsSortMode.None)
                        .FirstOrDefault(candidate => candidate != null && candidate.Slot == slot)
                        ?.GetComponent<Ship>();
                if (ship == null)
                {
                    Plugin.Log.LogInfo($"[Ships] disconnect cleanup P{slot + 1}: no live puppet remained ({reason})");
                    return;
                }
                RemotePuppet.ScrubInteractions(ship);
                try
                {
                    var ships = _shipManager != null
                        ? AccessTools.Field(typeof(ShipManager), "ships")?.GetValue(_shipManager) as IList<Ship>
                        : null;
                    ships?.Remove(ship);
                }
                catch { }

                var data = ship.SavableEntity != null ? ship.SavableEntity.EntityData : null;
                UnityEngine.Object.Destroy(ship.gameObject);
                if (data != null)
                {
                    var destroy = AccessTools.Method(data.GetType(), "Destroy");
                    if (destroy != null && destroy.GetParameters().Length == 0) destroy.Invoke(data, null);
                }
                InstrumentationCounters.DisconnectShipDespawned();
                Plugin.Log.LogInfo($"[Ships] removed disconnected puppet P{slot + 1} ({reason})");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Ships] disconnected puppet cleanup failed for P{slot + 1}: {e.Message}");
                try { if (ship != null) ship.gameObject.SetActive(false); } catch { }
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
            private static void Postfix(GameController __instance) => EnforcePuppetHudGate(__instance);
        }

        private static void EnforcePuppetHudGate(GameController gc)
        {
            if (!NetSession.Active || gc == null) return;
            try
            {
                var huds = AccessTools.Field(typeof(GameController), "huds").GetValue(gc) as Array;
                if (huds == null) return;
                for (int i = 0; i < huds.Length; i++)
                {
                    // Match by the hud's ASSIGNED ship, not list position — Ships[] can reorder
                    // across death/respawn, and an index match could black out a real player.
                    if (huds.GetValue(i) is ShipHud hud && hud.Ship != null
                        && hud.Ship.GetComponent<RemotePuppet>() != null && hud.gameObject.activeSelf)
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

        // AssignHuds runs once per run, but the HUD show/hide ANIMATORS run on every ship-menu
        // close (InGameHud.SetHudVisible triggers "Show" on ship2HudAnimator unconditionally) —
        // observed live: the remote player's HUD reappeared mid-run and its panel shoved the
        // minimap into the co-op corner. Counter the trigger immediately, and re-run the gate
        // at 1 Hz as a backstop against any other vanilla re-show path.
        [HarmonyPatch(typeof(InGameHud), "SetHudVisible")]
        internal static class NoPuppetHudReshow
        {
            private static void Postfix(InGameHud __instance, bool visible)
            {
                if (!visible || !NetSession.Active) return;
                try
                {
                    // ship2HudAnimator is hard-wired to huds[1]; check that hud's ASSIGNED ship.
                    var gc = ServiceLocator.Get<GameController>();
                    var huds = gc != null
                        ? AccessTools.Field(typeof(GameController), "huds").GetValue(gc) as Array
                        : null;
                    if (huds == null || huds.Length < 2 || huds.GetValue(1) is not ShipHud hud
                        || hud.Ship == null || hud.Ship.GetComponent<RemotePuppet>() == null) return;
                    var animator = AccessTools.Field(typeof(InGameHud), "ship2HudAnimator")
                        .GetValue(__instance) as Animator;
                    if (animator == null || !animator.isActiveAndEnabled) return;
                    animator.ResetTrigger("Show");
                    animator.ResetTrigger("SetToVisible");
                    animator.SetTrigger("SetToHidden");
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"[Ships] HUD re-show gating failed: {e.Message}");
                }
            }
        }

        private static float _nextHudSweepAt;

        private static void SweepPuppetHudGate()
        {
            if (Time.unscaledTime < _nextHudSweepAt) return;
            _nextHudSweepAt = Time.unscaledTime + 1f;
            try { EnforcePuppetHudGate(ServiceLocator.Get<GameController>()); } catch { }
        }

        // A field report of "the other player flew straight through an enemy" could not be
        // reproduced under controlled conditions and does not self-report anywhere. This 1 Hz
        // tripwire names the exact entity, its ownership, and its puppet state the moment a
        // remote ship's body overlaps an enemy in the wild — one log line turns the next
        // sighting into a diagnosis.
        private static float _nextOverlapSweepAt;
        private static readonly Collider2D[] OverlapScratch = new Collider2D[16];
        private static readonly Dictionary<(byte slot, int netId), float> NextOverlapLogAt
            = new Dictionary<(byte, int), float>();

        private static void SweepPuppetOverlaps()
        {
            if (Time.unscaledTime < _nextOverlapSweepAt) return;
            _nextOverlapSweepAt = Time.unscaledTime + 1f;
            foreach (var kv in ShipsBySlot)
            {
                var ship = kv.Value;
                if (ship == null || ship.IsDead || ship.GetComponent<RemotePuppet>() == null) continue;
                int hits = Physics2D.OverlapCircleNonAlloc(ship.transform.position, 1.25f, OverlapScratch);
                for (int i = 0; i < hits; i++)
                {
                    var unit = OverlapScratch[i] != null ? OverlapScratch[i].GetComponentInParent<Unit>() : null;
                    if (unit == null || unit.GetComponentInParent<Ship>() != null) continue;
                    if (!EnemySync.TryGetNetId(unit, out int netId)) continue;
                    var pairKey = ((byte)kv.Key, netId);
                    if (NextOverlapLogAt.TryGetValue(pairKey, out float next) && Time.unscaledTime < next) continue;
                    NextOverlapLogAt[pairKey] = Time.unscaledTime + 5f;
                    var se = unit.GetComponentInParent<SavableEntity>();
                    bool puppet = unit.GetComponentInParent<RemoteEntityPuppet>() != null;
                    Plugin.Log.LogWarning($"[Contact] remote ship P{kv.Key + 1} overlapping " +
                        $"#{netId} {se?.EntityData?.entityId ?? "?"} owner={Core.NetDiag.Owner(EnemySync.OwnerOf(netId))} " +
                        $"puppet={puppet} — if no collision was visible in-game, report this line");
                }
            }
        }

        // ---------------------------------------------------------------- snapshot TX/RX

        /// <summary>Called from NetSession.Update while InGame.</summary>
        public static void Tick(NetSession session)
        {
            EnsureLatePuppets(session);
            SweepDistantCameraTargets();
            SweepPuppetOverlaps();
            SweepPuppetHudGate();
            if (Time.unscaledTime < _nextSendAt) return;
            _nextSendAt = Time.unscaledTime + 1f / Mathf.Max(1f, NetConfig.ShipStateHz.Value);

            byte viewSlot = UI.SpectatorCam.Active
                ? (byte)Mathf.Clamp(UI.SpectatorCam.SpectatedSlot, 0, NetSession.MaxPlayers - 1)
                : (byte)session.LocalSlot;

            // A dead player has no local Ship, but the host still needs their selected spectator
            // target to route entity interest. This compact state carries no pose and therefore
            // cannot move or resurrect the destroyed remote ship.
            if (LocalShip == null || LocalShip.GetComponent<Rigidbody2D>() is not Rigidbody2D rb)
            {
                Writer.Reset();
                new ShipStateMsg
                {
                    Slot = (byte)session.LocalSlot,
                    ViewSlot = viewSlot,
                    HasBody = false,
                    TimeMs = (uint)(Time.unscaledTime * 1000f),
                }.Write(Writer);
                session.SendToAll(NetChannel.State, Writer.ToSegment(), reliable: false);
                return;
            }
            var dr = LocalShip.GetComponent<DamagableResource>();
            var movement = LocalShip.GetComponent<ShipMovement>();
            var input = LocalShip.GetComponent<ShipInput>();

            var flags = ShipFlags.None;
            if (LocalShip.IsDead) flags |= ShipFlags.Dead;
            Vector2 move = Vector2.zero;
            try
            {
                if (movement != null)
                {
                    if (movement.IsBoosted) flags |= ShipFlags.Boost;
                    if (movement.isHovering) flags |= ShipFlags.Hover;
                    move = movement.flyDirection;
                }
            }
            catch { }

            float hp = 1f;
            try { if (dr != null && dr.MaxHealth > 0) hp = dr.CurrentHealth / dr.MaxHealth; } catch { }

            var msg = new ShipStateMsg
            {
                Slot = (byte)session.LocalSlot,
                ViewSlot = viewSlot,
                HasBody = true,
                TimeMs = (uint)(Time.unscaledTime * 1000f),
                Pos = rb.position,
                Vel = rb.linearVelocity,
                RotDeg = rb.rotation,
                Aim = ReadLocalAim(input),
                Move = move,
                Flags = flags,
                HpFraction = hp,
                ShieldFraction = UnitStatus.ReadShieldFraction(LocalShip),
                BurnLevel = UnitStatus.ReadBurnLevel(LocalShip),
                FuelFraction = UnitStatus.ReadFuelFraction(LocalShip),
            };
            Writer.Reset();
            msg.Write(Writer);
            session.SendToAll(NetChannel.State, Writer.ToSegment(), reliable: false);
        }

        private static Aimer _localAimer;

        /// <summary>The local turret's actual rendered direction. ShipInput.AimDirection is only
        /// written on the GAMEPAD path — for mouse players it stays at its initial Vector2.right
        /// forever, so peers never saw KBM aim. The Aimer's barrel is the truth for both schemes
        /// (and already includes the turret's rotation-speed smoothing).</summary>
        private static Vector2 ReadLocalAim(ShipInput input)
        {
            if (_localAimer == null && LocalShip != null) // refetch after death/respawn (Unity null)
                _localAimer = LocalShip.GetComponentInChildren<Aimer>(true);
            try
            {
                if (_localAimer != null && _localAimer.barrel != null) return _localAimer.barrel.Direction;
            }
            catch { }
            return input != null ? (Vector2)input.AimDirection : Vector2.right;
        }

        // Dash is an instantaneous event, not state — DashParticle (and any dash audio) hang off
        // ShipMovement.DashStarted, so peers replay the event instead of inferring it from motion.
        [HarmonyPatch(typeof(ShipMovement), "Dash")]
        internal static class BroadcastDash
        {
            private static void Postfix(ShipMovement __instance)
            {
                var session = NetSession.Instance;
                if (session == null || session.State != SessionState.InGame) return;
                if (LocalShip == null || __instance.gameObject != LocalShip.gameObject) return;
                Writer.Reset();
                new ShipDashMsg { Slot = (byte)session.LocalSlot }.Write(Writer);
                session.SendToAll(NetChannel.State, Writer.ToSegment(), reliable: false);
            }
        }

        /// <summary>Move the local ship (and camera) to a station — used right after a rejoin
        /// goes live so the player resumes at the party's latest unlocked station instead of
        /// the run start. Works from EntityData alone; the station needn't be streamed in.</summary>
        /// <summary>World position of a netId'd entity (stations for spawn/stream targeting).
        /// Works from EntityData alone; the entity needn't be streamed in.</summary>
        public static bool TryGetEntityPosition(int netId, out Vector2 pos)
        {
            pos = default;
            try
            {
                if (netId == 0 || !NetIds.TryGetInstanceId(netId, out int instanceId)) return false;
                var data = ServiceLocator.Get<EntityManager>().GetEntity(instanceId);
                if (data == null) return false;
                pos = data.position;
                return true;
            }
            catch { return false; }
        }

        public static void TeleportLocalShip(int stationNetId)
        {
            try
            {
                if (LocalShip == null || !NetIds.TryGetInstanceId(stationNetId, out int instanceId)) return;
                var em = ServiceLocator.Get<EntityManager>();
                var data = em.GetEntity(instanceId);
                if (data == null) return;
                Vector2 pos = (Vector2)data.position + Vector2.up * 2f; // hover above the platform
                var rb = LocalShip.GetComponent<Rigidbody2D>();
                if (rb != null)
                {
                    RemoteEntityPuppet.TeleportWithChildren(rb, pos);
                    rb.linearVelocity = Vector2.zero;
                }
                LocalShip.transform.position = pos;
                var shipData = LocalShip.SavableEntity != null ? LocalShip.SavableEntity.EntityData : null;
                if (shipData != null) shipData.MoveTo(new Vector3(pos.x, pos.y, shipData.position.z));
                try
                {
                    var cam = Com.LuisPedroFonseca.ProCamera2D.ProCamera2D.Instance;
                    if (cam != null) cam.MoveCameraInstantlyToPosition(pos);
                }
                catch { }
                Plugin.Log.LogInfo($"[Ships] rejoin spawn at station #{stationNetId} ({pos.x:F0},{pos.y:F0})");
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"[Ships] station spawn failed: {e.Message}");
            }
        }

        /// <summary>Fire the puppet's DashStarted subscribers (VFX) and the dash sfx — never
        /// ShipMovement.Dash itself; the velocity impulse belongs to the owner's simulation,
        /// ours comes by snapshot. The sfx is played manually because vanilla plays it inside
        /// Dash(), not from the event.</summary>
        public static void ApplyDash(ShipDashMsg msg)
        {
            if (!ShipsBySlot.TryGetValue(msg.Slot, out var ship) || ship == null) return;
            if (ship.GetComponent<RemotePuppet>() == null) return; // our own echo
            try
            {
                var movement = ship.GetComponent<ShipMovement>();
                if (movement == null) return;
                movement.DashStarted?.Invoke();
                if (!string.IsNullOrEmpty(movement.dashSfx))
                    AudioManager.PlaySfx(movement.dashSfx, (Vector2)ship.transform.position);
            }
            catch { }
        }

        // POI camera targets activate off the AVERAGE alive-ship position — with the party split
        // across the map that midpoint can pull the local camera toward a POI nobody is at.
        // Anything farther than ~45u from the local ship has no business steering our camera.
        private static void SweepDistantCameraTargets()
        {
            if (UI.SpectatorCam.Active) return; // spectating a (distant) teammate is the point
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
        private static readonly Dictionary<byte, uint> LastShipStateMs = new Dictionary<byte, uint>();

        public static void ApplyShipState(ShipStateMsg msg)
        {
            ViewSlotByPlayer[msg.Slot] = msg.ViewSlot < NetSession.MaxPlayers ? msg.ViewSlot : msg.Slot;
            if (!msg.HasBody) return;
            if (!ShipsBySlot.TryGetValue(msg.Slot, out var ship) || ship == null) return;
            var puppet = ship.GetComponent<RemotePuppet>();
            if (puppet == null) return; // our own echo — ignore

            // The state channel is unreliable AND unordered: a late packet applied after a
            // newer one yanks the puppet backwards. Wrap-safe sender-clock compare.
            if (LastShipStateMs.TryGetValue(msg.Slot, out uint last) && (int)(msg.TimeMs - last) <= 0) return;
            LastShipStateMs[msg.Slot] = msg.TimeMs;

            puppet.PushSnapshot(ClockSync.ToLocalTime(msg.Slot, msg.TimeMs), msg.Pos, msg.Vel, msg.RotDeg, msg.Aim);
            puppet.SetMoveInput(msg.Move);
            puppet.SetBoosting((msg.Flags & ShipFlags.Boost) != 0);
            puppet.SetHovering((msg.Flags & ShipFlags.Hover) != 0);
            UnitStatus.WriteShieldFraction(ship, msg.ShieldFraction);
            UnitStatus.WriteBurnLevel(ship, msg.BurnLevel);
            UnitStatus.WriteFuelFraction(ship, msg.FuelFraction);

            try
            {
                var dr = ship.GetComponent<DamagableResource>();
                if (dr != null && dr.MaxHealth > 0)
                {
                    float target = msg.HpFraction * dr.MaxHealth;
                    if (Mathf.Abs(dr.CurrentHealth - target) > 0.5f)
                    {
                        // Snapshot writes bypass the damage pipeline — surface the hit flash
                        // observers would see in vanilla.
                        if (target < dr.CurrentHealth) UnitStatus.PlayDamageFlash(ship);
                        dr.CurrentHealth = target;
                    }
                }
            }
            catch { }
        }

        /// <summary>Interest follows what a peer can currently see. During death spectating this
        /// is the selected teammate, not the corpse left behind across the map.</summary>
        internal static bool TryGetViewPosition(byte playerSlot, out Vector2 position)
        {
            position = default;
            byte viewSlot = ViewSlotByPlayer.TryGetValue(playerSlot, out byte viewed)
                ? viewed : playerSlot;
            if (!ShipsBySlot.TryGetValue(viewSlot, out var ship) || ship == null)
            {
                if (!ShipsBySlot.TryGetValue(playerSlot, out ship) || ship == null) return false;
            }
            position = ship.transform.position;
            return true;
        }
    }
}
