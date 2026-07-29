using System.Linq;
using HarmonyLib;
using PunkMultiverse.Core;
using UnityEngine;

namespace PunkMultiverse.Patches
{
    /// <summary>
    /// Puts each player ON their Battle Royale spawn station from the first frame, instead of
    /// spawning everyone on the shared start pad and teleporting them apart three seconds later.
    ///
    /// Omar, 2026-07-28: "the players seem to spawn in the same spawn point, then get teleported —
    /// is there any way to just have them start off their spawn position from the start?"
    ///
    /// Moving the ship is the easy half. The hard half is that the vanilla opening cinematic is
    /// built around ONE start station: it finds "the station with an installed upgrade", moves the
    /// camera there, opens THAT platform and spawns THAT light. Put the ship somewhere else and the
    /// cinematic still performs at the old pad — so the player watches a station they are not on,
    /// and then `UnlockCamera(2f)` sweeps the map to find them. Moving the ship without moving the
    /// cinematic just trades a teleport for a longer pan.
    ///
    /// So this does both, and does them locally: the assigned station is handed the installed
    /// upgrade and the original start station gives its up. The cinematic's own lookup then finds
    /// the player's station and performs there — right camera, right platform, right light, no
    /// teleport and no sweep. Every machine computes the same assignment independently (the layout
    /// is seed-deterministic and the sampling is farthest-point), so this needs no wire traffic.
    ///
    /// Safe by construction: it runs BEFORE go-live, and <c>ProgressionSync</c> only captures
    /// station unlocks while <c>InGame</c>, so none of this replicates — every machine is simply
    /// arranging its own opening. The end state is identical either way, because BR unlocks every
    /// station a few seconds into the match regardless.
    ///
    /// If anything here fails the mode falls back to the old behaviour automatically: the scatter
    /// teleport in <c>BattleRoyale.TickScatter</c> still runs unless this reports success.
    /// </summary>
    internal static class BattleRoyaleSpawn
    {
        /// <summary>Set once this machine has placed its ship itself, so the scatter teleport
        /// stands down. False means "something went wrong, let the teleport do it".</summary>
        internal static bool PlacedAtStation;

        internal static void Reset() => PlacedAtStation = false;

        // ShipManager.SpawnShipGameObjects is the last SYNCHRONOUS step before the cinematic starts
        // (it waits on Ships.Count >= 1, which this satisfies). Its async sibling
        // PlaceShipEntitiesToStartPosition is a UniTask whose Harmony stub returns before the body
        // has run, so a postfix there would fire too early to be useful.
        [HarmonyPatch(typeof(ShipManager), nameof(ShipManager.SpawnShipGameObjects))]
        internal static class PlaceOnOwnStation
        {
            private static void Postfix(ShipManager __instance)
            {
                PlacedAtStation = false;
                var session = NetSession.Instance;
                if (session == null || !NetSession.Active) return;
                if (session.LobbyMode != Protocol.GameMode.BattleRoyale
                    && session.CurrentMode != Protocol.GameMode.BattleRoyale) return;
                if (!NetConfig.BrSpawnAtStationDirectly.Value) return;
                if (NetConfig.IsCoordinator) return; // shipless; nothing to place

                try { Place(session, __instance); }
                catch (System.Exception e)
                {
                    PlacedAtStation = false;
                    Plugin.Log.LogWarning($"[BR] direct station spawn failed ({e.Message}) — " +
                        "falling back to the scatter teleport");
                }
            }
        }

        private static void Place(NetSession session, ShipManager shipManager)
        {
            // With the drop screen on, the player has not chosen yet and this must NOT pre-place
            // them: they deploy themselves the moment they pick a region
            // (Modes/BattleRoyaleSpawnSelect.Deploy). Placing here would put the ship on a station
            // it is about to leave, and the redirected cinematic would open the wrong platform.
            //
            // But it must not stay on the START PAD either. This postfix is the moment the ship
            // first exists, and it runs BEFORE the first InGame net tick — the tick that holds the
            // pen. Returning without parking left the ship standing on the shared pad through that
            // gap: in the world, visible, attackable, ahead of any choice (Omar, 2026-07-29: "we
            // still seem to be spawning before spawn selection"). Park it here, in the same frame
            // it spawns, so an undeployed ship never stands anywhere real.
            if (NetConfig.BrChooseSpawn.Value)
            {
                if (!Modes.BattleRoyaleSpawnSelect.Deployed)
                    Modes.BattleRoyaleSpawnSelect.HoldInTheVoid();
                return;
            }

            var assignment = Modes.BattleRoyale.AssignSpawnStations(session);
            if (!assignment.TryGetValue((byte)session.LocalSlot, out int stationNetId))
            {
                Plugin.Log.LogWarning($"[BR] no spawn station assigned for slot {session.LocalSlot} " +
                    "— using the scatter teleport");
                return;
            }
            if (!NetIds.TryGetInstanceId(stationNetId, out int stationInstance)) return;
            var em = ServiceLocator.Get<EntityManager>();
            var stationData = em != null ? em.GetEntity(stationInstance) : null;
            if (stationData == null) return;

            var ship = shipManager.Ships.FirstOrDefault();
            if (ship == null) return;

            Vector2 pos = (Vector2)stationData.position + Vector2.up * 2f; // hover above the platform
            var entity = ship.SavableEntity != null ? ship.SavableEntity.EntityData : null;
            if (entity != null) entity.MoveTo(new Vector3(pos.x, pos.y, entity.position.z));
            var rb = ship.GetComponent<Rigidbody2D>();
            if (rb != null)
            {
                Sync.RemoteEntityPuppet.TeleportWithChildren(rb, pos);
                rb.linearVelocity = Vector2.zero;
            }
            ship.transform.position = pos;

            RedirectStartCinematic(em, stationInstance);

            // The cinematic moves the camera itself, but only once it reaches its first line; put
            // it here now so there is no frame showing the old pad.
            try
            {
                var cam = Com.LuisPedroFonseca.ProCamera2D.ProCamera2D.Instance;
                if (cam != null) cam.MoveCameraInstantlyToPosition(pos);
            }
            catch { }

            PlacedAtStation = true;
            Modes.BattleRoyaleSpawnSelect.NoteSpawn("direct station spawn");
            Plugin.Log.LogInfo($"[BR] spawned directly on station #{stationNetId} at " +
                $"({pos.x:0},{pos.y:0}) — no scatter teleport needed");
        }

        /// <summary>Make the cinematic's "which station do I open" lookup resolve to OURS.
        ///
        /// It is <c>FirstOrDefault(s =&gt; s.installedUpgrades.Count &gt; 0)</c>, and vanilla
        /// guarantees exactly one match. Rather than fight that, satisfy it: give our station the
        /// installed upgrade and take it off whatever had it. Local and pre-go-live, so nothing
        /// replicates, and BR unlocks every station moments later anyway — the only lasting effect
        /// is which platform the opening animation plays on.</summary>
        private static void RedirectStartCinematic(EntityManager em, int ourStationInstance)
        {
            if (em == null) return;
            var stations = em.GetEntitiesWithComponent<Station.Data>().Where(s => s != null).ToList();
            var ours = stations.FirstOrDefault(s => s.entity != null && s.entity.instanceId == ourStationInstance);
            if (ours == null) return;

            int cleared = 0;
            foreach (var s in stations)
            {
                if (ReferenceEquals(s, ours)) continue;
                if (s.installedUpgrades == null || s.installedUpgrades.Count == 0) continue;
                s.installedUpgrades.Clear();
                cleared++;
            }

            if (ours.installedUpgrades == null || ours.installedUpgrades.Count == 0)
            {
                var upgrade = ours.allUpgrades != null && ours.allUpgrades.Count > 0 ? ours.allUpgrades[0] : null;
                if (upgrade != null) ours.Install(upgrade);
            }
            Plugin.Log.LogInfo($"[BR] start cinematic redirected to our own station " +
                $"(unlocked ours, cleared {cleared} other pre-unlocked station(s))");
        }
    }
}
