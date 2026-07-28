using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using PunkMultiverse.Core;
using PunkMultiverse.Sync;
using UnityEngine;

namespace PunkMultiverse.Modes
{
    /// <summary>
    /// The half of Battle Royale that runs on EVERY machine: staying inside the ring, the match
    /// HUD, and picking this player's spawn station. A ship's health belongs to the machine that
    /// owns it, so out-of-zone damage is self-applied rather than commanded by the host — the host
    /// only publishes where the ring is.
    /// </summary>
    internal static partial class BattleRoyale
    {
        // ---------------------------------------------------------------- kill zone

        private static float _nextBurnAt;
        private const float BurnInterval = 0.5f;

        /// <summary>Every machine: burn the local ship while it is outside the safe zone.
        ///
        /// THE ZONE IS NOT SOLID and never damages by contact — this radius check IS the entire
        /// enforcement (the lava is rendered, see UI/RingLavaVisual.cs). That is deliberate: a
        /// player must always be able to fly THROUGH the zone rather than be walled in by it, so
        /// getting caught outside is a cost you pay in health, not a death sentence. Omar,
        /// 2026-07-28: "players can still go through the area... it's how players can prevent being
        /// trapped."
        ///
        /// Applied by the victim's own machine because a ship's health belongs to whoever owns it —
        /// the same rule the winner's self-destruct follows.
        ///
        /// PACING. <see cref="NetConfig.BrZoneKillSeconds"/> is the honest knob: seconds to die
        /// from FULL health in the FIRST zone (default 60, i.e. a long crossing is survivable at
        /// full health and nothing else). Every completed shrink stage then multiplies the rate by
        /// <see cref="NetConfig.BrZoneDamageStageScale"/>, so the late zones close the escape hatch
        /// the early ones deliberately leave open — by the final ring, being outside is fatal in
        /// seconds. Damage scales with MAX health, so an upgraded hull buys proportionally more
        /// time rather than trivialising the zone.</summary>
        public static void LocalTick(NetSession session)
        {
            if (!Active) return;
            TryRevealWholeMap();
            TickSelfDestruct(session);
            if (!RingKnown) return;
            var ship = ShipSync.LocalShip;
            if (ship == null || ship.IsDead) return;
            if (Time.unscaledTime < _nextBurnAt) return;
            _nextBurnAt = Time.unscaledTime + BurnInterval;

            Vector2 pos = ship.transform.position;
            float dist = Vector2.Distance(pos, new Vector2(Ring.CenterX, Ring.CenterY));
            if (dist <= Ring.SafeRadius) { StopZoneFire(); return; }

            try
            {
                var unit = ship.GetComponentInParent<Unit>() ?? ship.GetComponent<Unit>();
                var dr = unit != null ? unit.GetComponent<DamagableResource>() : ship.GetComponent<DamagableResource>();
                var tank = dr != null ? dr.Tank : null;
                if (tank == null || tank.isInfinite || tank.Capacity <= 0f) return;

                SetShipOnFire(unit, tank);
                if (Time.frameCount % 120 == 0)
                {
                    float killSeconds = Mathf.Max(1f, NetConfig.BrZoneKillSeconds.Value);
                    Plugin.Log.LogInfo($"[BR] in the zone ({dist:0} > {Ring.SafeRadius:0}) — " +
                        $"burning: stage {Ring.Stage}, x{ZoneDamageMultiplier:0.0} damage, " +
                        $"~{killSeconds / ZoneDamageMultiplier:0}s from full");
                }
            }
            catch { }
        }

        // The ship's own burn settings, saved the first time we set it alight so they can be put
        // back when it leaves the zone. Fire is a normal part of this game — a rocket can set you
        // alight — and the zone must not permanently reprogram how that feels.
        private static Unit.Data.BurnProperties _originalBurn;
        private static bool _burnSaved;
        private static bool _burning;

        /// <summary>Set the ship ALIGHT rather than deducting health from nowhere.
        ///
        /// Omar, 2026-07-28: "we should do damage to them by lighting them on fire, not just doing
        /// damage blindly — that fire will do the damage we specified." So the zone stops calling
        /// <c>Damage()</c> itself and instead drives the game's OWN fire: raise
        /// <c>Unit.Data.BurnLevel</c> past <c>fireThreshold</c> and
        /// <c>DamagableResource.Update</c> ticks <c>fireDmgPerTick</c> every <c>fireTickRate</c>
        /// through the normal damage pipeline, while <c>StatusEffectForUnit</c> emits the flames —
        /// no bespoke visual needed, and being in the zone now LOOKS like what it is.
        ///
        /// The configured pacing is preserved by rewriting this ship's fire RATE rather than
        /// accepting the prefab's: fireDmgPerTick is set so one tick removes exactly the fraction
        /// of MAX health that BrZoneKillSeconds (scaled by the stage multiplier) calls for. Vanilla
        /// cools BurnLevel every frame by coolingSpeed, so it is topped back up each tick while the
        /// ship is outside — which also means a player who escapes keeps burning briefly as the
        /// fire dies down, instead of the damage stopping dead at an invisible line.</summary>
        private static void SetShipOnFire(Unit unit, ResourceTank tank)
        {
            var data = unit != null ? unit.ComponentData : null;
            if (data == null) return;
            if (!_burnSaved) { _originalBurn = data.burnProperties; _burnSaved = true; }

            float killSeconds = Mathf.Max(1f, NetConfig.BrZoneKillSeconds.Value);
            var burn = data.burnProperties;
            // Keep the prefab's tick cadence when it has one; it drives the flame animation's
            // rhythm as much as the damage.
            float tickRate = burn.fireTickRate > 0.01f ? burn.fireTickRate : BurnInterval;
            burn.fireTickRate = tickRate;
            burn.fireDmgPerTick = Mathf.Max(0.5f,
                tank.Capacity * (tickRate / killSeconds) * ZoneDamageMultiplier);
            // A threshold of 0 would mean "never actually on fire"; guarantee a usable band.
            if (burn.maxBurnLevel <= burn.fireThreshold) burn.maxBurnLevel = burn.fireThreshold + 1f;
            data.burnProperties = burn;

            // Top up above the threshold every tick — vanilla is simultaneously cooling it.
            if (data.BurnLevel <= burn.fireThreshold + 0.01f)
                data.BurnLevel = burn.maxBurnLevel;
            _burning = true;
        }

        /// <summary>Back inside: hand the ship's fire settings back to the game. The flames are NOT
        /// snuffed out — vanilla's cooling puts them out over the next moment or two, which is the
        /// right feel for having just run through a wall of fire.</summary>
        private static void StopZoneFire()
        {
            if (!_burning || !_burnSaved) return;
            _burning = false;
            try
            {
                var ship = ShipSync.LocalShip;
                var unit = ship != null
                    ? (ship.GetComponentInParent<Unit>() ?? ship.GetComponent<Unit>()) : null;
                if (unit?.ComponentData != null) unit.ComponentData.burnProperties = _originalBurn;
            }
            catch { }
        }

        internal static void ResetZoneFire() { _burning = false; _burnSaved = false; }

        /// <summary>How much harder the zone bites now than it did at the opening ring. Stage 0 (the
        /// grace period, before anything has closed) is always 1x.</summary>
        public static float ZoneDamageMultiplier
        {
            get
            {
                float perStage = Mathf.Max(0f, NetConfig.BrZoneDamageStageScale.Value);
                return 1f + Mathf.Max(0, Ring.Stage) * perStage;
            }
        }

        // ---------------------------------------------------------------- spawn scatter

        /// <summary>slot -> station netId, computed identically on every machine (station layout is
        /// seed-deterministic, so no assignment needs to be sent). Farthest-point sampling: each
        /// player takes the station furthest from everyone already placed, and a station leaves the
        /// pool once taken — so no two players can ever share a shop.</summary>
        public static Dictionary<byte, int> AssignSpawnStations(NetSession session)
        {
            var result = new Dictionary<byte, int>();
            try
            {
                var em = ServiceLocator.Get<EntityManager>();
                if (em == null) return result;
                var stations = em.GetEntitiesWithComponent<Station.Data>()
                    .Where(d => d?.entity != null)
                    .OrderBy(d => d.entity.instanceId) // deterministic base order on every machine
                    .ToList();
                if (stations.Count == 0) return result;

                var slots = session.Players
                    .Where(p => p != null && p.Connected && !p.IsCoordinator)
                    .Select(p => p.Slot)
                    .OrderBy(s => s)
                    .ToList();
                if (slots.Count == 0) return result;

                var taken = new List<Vector2>();
                var pool = new List<Station.Data>(stations);
                foreach (byte slot in slots)
                {
                    if (pool.Count == 0)
                    {
                        Plugin.Log.LogWarning($"[BR] only {stations.Count} stations for {slots.Count} players — " +
                            $"slot {slot} has no distinct station");
                        break;
                    }
                    int pick = 0;
                    if (taken.Count == 0)
                    {
                        // Deterministic first pick from the run seed, so matches don't always open
                        // at the same corner of the map.
                        pick = Mathf.Abs(session.CurrentRunSeed % pool.Count);
                    }
                    else
                    {
                        float best = -1f;
                        for (int i = 0; i < pool.Count; i++)
                        {
                            Vector2 p = pool[i].entity.position;
                            float nearest = taken.Min(t => Vector2.Distance(t, p));
                            if (nearest <= best) continue;
                            best = nearest;
                            pick = i;
                        }
                    }
                    var chosen = pool[pick];
                    pool.RemoveAt(pick); // never reused — distinct stations by construction
                    Vector2 cp = chosen.entity.position;
                    taken.Add(cp);
                    if (NetIds.TryGetNetId(chosen.entity.instanceId, out int netId))
                    {
                        result[slot] = netId;
                        float nearestPeer = taken.Count > 1
                            ? taken.Take(taken.Count - 1).Min(t => Vector2.Distance(t, cp))
                            : 0f;
                        Plugin.Log.LogInfo($"[BR] spawn slot {slot} -> station #{netId} at " +
                            $"({cp.x:0},{cp.y:0}), nearest peer {nearestPeer:0} units");
                    }
                }
            }
            catch (System.Exception e) { Plugin.Log.LogWarning($"[BR] spawn assignment failed: {e.Message}"); }
            return result;
        }

        private static bool _scattered;
        private static float _scatterAt;

        /// <summary>Arm the scatter. The teleport must land AFTER the vanilla start cinematic has
        /// finished, not on a fixed timer.
        ///
        /// The cinematic's last act is <c>UnlockCamera(2f)</c> — a TWO-SECOND camera transition to
        /// wherever the ship is. Teleporting before that meant the transition began from the shared
        /// start station and swept the whole map to the scattered spawn: the "camera starts
        /// somewhere else then pans over" (field-reported 2026-07-28, and still there after the
        /// station-unlock fix because this is a different mechanism). Waiting for the cinematic to
        /// hand back control means the camera is already settled on the ship, so
        /// <c>TeleportLocalShip</c>'s instant camera move is a cut rather than a pan.</summary>
        public static void ArmScatter()
        {
            _scattered = false;
            _scatterAt = Time.unscaledTime + 3f;   // earliest; the real gate is control being back
            _scatterDeadline = Time.unscaledTime + 30f; // never wait forever on a broken cinematic
        }

        private static float _scatterDeadline;

        public static void TickScatter(NetSession session)
        {
            if (_scattered || !Active) return;
            // The drop screen owns placement when it is on — the player puts themselves on a pad by
            // choosing. Scattering underneath that would move a ship that has already deployed.
            if (NetConfig.BrChooseSpawn.Value) { _scattered = true; return; }
            // Already standing on our own station — Patches/BattleRoyaleSpawn.cs put us there
            // before the cinematic even started, so there is nothing to teleport.
            if (Patches.BattleRoyaleSpawn.PlacedAtStation) { _scattered = true; return; }
            if (Time.unscaledTime < _scatterAt) return;
            var ship = ShipSync.LocalShip;
            if (ship == null) return;
            // Control restored == the cinematic reached its final line (the same signal
            // StartSequenceWatchdog waits on). Past the deadline, go anyway: a scattered spawn
            // matters more than a tidy camera, and the watchdog will restore input separately.
            bool cinematicDone = ship.shipInput != null && ship.shipInput.enabled;
            if (!cinematicDone && Time.unscaledTime < _scatterDeadline) return;
            if (!cinematicDone)
                Plugin.Log.LogWarning("[BR] scattering before the start cinematic finished — " +
                    "expect a camera sweep; the cinematic never gave control back in 30s");
            _scattered = true;
            var assignment = AssignSpawnStations(session);
            if (!assignment.TryGetValue((byte)session.LocalSlot, out int stationNetId)) return;
            ShipSync.TeleportLocalShip(stationNetId);
            Plugin.Log.LogInfo($"[BR] scattered to station #{stationNetId}");
        }

        // ---------------------------------------------------------------- spawn-area clear

        /// <summary>Hostile families in this game's entity ids: every enemy is either
        /// <c>Enemy_*</c> (Enemy_Raven, Enemy_Fish, Enemy_Turret_Laser, ...) or <c>Unit_*</c>
        /// (Unit_Grunt, Unit_Floater_Soldier, Unit_Swimmer_Maggot, ...). Everything else generated
        /// into the world is scenery, plants, crates or the stations themselves.</summary>
        internal static bool IsHostileEntityId(string entityId) =>
            !string.IsNullOrEmpty(entityId)
            && (entityId.StartsWith("Enemy", System.StringComparison.Ordinal)
                || entityId.StartsWith("Unit_", System.StringComparison.Ordinal));

        /// <summary>Give every player clear ground to open the match on: remove the enemies sitting
        /// on top of the spawn stations. Landing inside a fight you did not choose is not a battle
        /// royale, it is an ambush by the map.
        ///
        /// This runs on EVERY machine and sends nothing, which is the whole point: the station
        /// assignment is already computed identically everywhere (seed-deterministic layout,
        /// farthest-point sampling) and the world is identical by construction, so every machine
        /// derives the SAME set of entities and removes it locally. A removal every machine agrees
        /// on needs no wire traffic and cannot desync. Removal is silent — no loot, no death VFX,
        /// no kill credit — because these enemies are being ruled out of the match, not killed;
        /// dropping 40 enemies' worth of gold at spawn would also undo the zeroed starting economy.
        ///
        /// Why not during world PRE-GENERATION (the obvious place): the pre-built world is hashed
        /// and compared against every client's freshly generated one at the go-live barrier. An
        /// entity the server deleted pre-gen is an entity count + digest the clients don't have —
        /// an instant GENERATION MISMATCH. Go-live is the earliest moment the world can be changed
        /// at all, and it is still before the scatter teleport puts anyone on a station.</summary>
        private static void ClearSpawnAreas(NetSession session)
        {
            float radius = NetConfig.BrSpawnClearRadius.Value;
            if (radius <= 0f) return;
            try
            {
                var em = ServiceLocator.Get<EntityManager>();
                if (em == null) return;

                var centers = new List<Vector2>();
                if (NetConfig.BrChooseSpawn.Value)
                {
                    // With the drop screen on, ANY station can be a spawn — the player picks after
                    // the world is already running, so there is no "assigned" pad to clear ahead of
                    // time. Sweep them all. Field-reported 2026-07-28: "the enemies around the
                    // spawned selection were still there". Still derived identically on every
                    // machine and still sends nothing, which is the property that makes this safe.
                    foreach (var station in em.GetEntitiesWithComponent<Station.Data>())
                        if (station?.entity != null) centers.Add(station.entity.position);
                }
                else
                {
                    foreach (var kv in AssignSpawnStations(session))
                    {
                        if (!NetIds.TryGetInstanceId(kv.Value, out int stationInstance)) continue;
                        var stationData = em.GetEntity(stationInstance);
                        if (stationData != null) centers.Add(stationData.position);
                    }
                }
                if (centers.Count == 0) return;

                // Ships are savable entities too; never let a prefix match delete a player.
                var shipInstances = new HashSet<int>();
                try
                {
                    foreach (var ship in ServiceLocator.Get<ShipManager>().Ships)
                    {
                        var se = ship != null ? ship.GetComponentInChildren<SavableEntity>() : null;
                        if (se != null && se.EntityData != null) shipInstances.Add(se.EntityData.instanceId);
                    }
                }
                catch { }

                // Collect first, remove second: removal destroys entity data, and mutating the
                // manager's collection while enumerating it would throw halfway through.
                float radiusSq = radius * radius;
                var doomed = new List<int>();
                foreach (var data in em.GetAllEntities())
                {
                    if (data == null || shipInstances.Contains(data.instanceId)) continue;
                    if (!IsHostileEntityId(data.entityId)) continue;
                    Vector2 pos = data.position;
                    bool nearSpawn = false;
                    for (int i = 0; i < centers.Count; i++)
                        if ((pos - centers[i]).sqrMagnitude <= radiusSq) { nearSpawn = true; break; }
                    if (!nearSpawn) continue;
                    if (NetIds.TryGetNetId(data.instanceId, out int netId)) doomed.Add(netId);
                }

                foreach (int netId in doomed) Sync.EnemySync.RemoveSilently(netId);
                Plugin.Log.LogInfo($"[BR] spawn clear: removed {doomed.Count} enemies within " +
                    $"{radius:0} units of {centers.Count} spawn station(s)");
            }
            catch (System.Exception e) { Plugin.Log.LogWarning($"[BR] spawn clear failed: {e.Message}"); }
        }

        // ---------------------------------------------------------------- winner self-destruct

        private static float _selfDestructAt = -1f;
        private static int _lastSelfDestructCount = -1;
        private static bool _selfDestructFired;

        internal static void ResetSelfDestruct()
        {
            _selfDestructAt = -1f;
            _lastSelfDestructCount = -1;
            _selfDestructFired = false;
        }

        /// <summary>The winner does not get to keep the map. Once the victory callout has had its
        /// moment their ship scuttles itself, so a won match ends the same way every other player's
        /// did — dead — instead of leaving one person alone in a world the run is waiting on.
        /// Applied by the winner's OWN machine: a ship's health belongs to the machine that owns
        /// it, the same rule the out-of-zone burn follows.</summary>
        private static void TickSelfDestruct(NetSession session)
        {
            if (!LocalIsWinner || _selfDestructFired) return;
            float seconds = NetConfig.BrWinnerSelfDestructSeconds.Value;
            if (seconds <= 0f) return;

            if (_selfDestructAt < 0f)
            {
                _selfDestructAt = Time.unscaledTime + seconds;
                UI.Toast.Show("SELF-DESTRUCT SEQUENCE ENGAGED", 4f);
                Plugin.Log.LogInfo($"[BR] winner self-destruct armed — {seconds:0}s");
            }

            float remaining = _selfDestructAt - Time.unscaledTime;
            int count = Mathf.CeilToInt(remaining);
            if (count != _lastSelfDestructCount && count > 0 && count <= 5)
            {
                _lastSelfDestructCount = count;
                UI.Toast.Show($"SELF-DESTRUCT IN {count}", 1.1f);
            }
            if (remaining > 0f) return;

            var ship = ShipSync.LocalShip;
            // A winner who is ALREADY dead needs no scuttling — the ring or a last trade got there
            // first, and the goal (nobody is left alone in a world the run is waiting on) is met.
            // Setting the fired latch BEFORE this check used to consume it silently, so a winner
            // that died during their own countdown produced no self-destruct AND no explanation.
            if (ship == null || ship.IsDead)
            {
                _selfDestructFired = true;
                Plugin.Log.LogInfo("[BR] winner self-destruct skipped — the winner was already dead");
                return;
            }
            _selfDestructFired = true;
            try
            {
                var unit = ship.GetComponentInParent<Unit>() ?? ship.GetComponent<Unit>();
                var dr = unit != null ? unit.GetComponent<DamagableResource>() : ship.GetComponent<DamagableResource>();
                if (dr == null) { Plugin.Log.LogWarning("[BR] self-destruct: no DamagableResource"); return; }
                var tank = dr.Tank;

                // Damage is the WRONG tool here and it failed in the field (2026-07-28: "the
                // self-destruct failed because I probably had F1 unlimited resources selected").
                // A self-destruct is not an injury to be survived — it is the run ending — but
                // dr.Damage goes through every survival mechanism there is: the god-mode gate
                // (Sync/DamageSync.IsGodShieldedLocalShip drops it outright), shields, and an
                // INFINITE tank, which by definition can never be emptied. Any one of those turns
                // the winner into a player standing alone in a world the run is waiting on.
                //
                // So: empty the tank directly and tell the game to notice. Ship.CheckIfDead is the
                // game's own "re-evaluate whether this ship is dead" entry point (ShipManager
                // .CheckShipsAlive calls it), and the resulting death replicates through the normal
                // ShipDied path exactly as a real one does.
                if (tank != null)
                {
                    if (tank.isInfinite)
                    {
                        tank.isInfinite = false; // the run is over; nothing outlives this
                        Plugin.Log.LogInfo("[BR] self-destruct: cleared an infinite health tank " +
                            "(debug menu) — the winner does not get to keep the map");
                    }
                    tank.Value = 0f;
                }
                ship.CheckIfDead();

                if (!ship.IsDead)
                {
                    // Last resort: the tank route did not convince it. Damage at least still runs
                    // the vanilla pipeline, and a failure here is worth seeing rather than silence.
                    dr.Damage(tank != null && tank.Capacity > 0f ? tank.Capacity * 10f : 100000f);
                    ship.CheckIfDead();
                }
                Plugin.Log.LogInfo($"[BR] winner self-destructed (dead={ship.IsDead})");
                if (!ship.IsDead)
                    Plugin.Log.LogWarning("[BR] self-destruct did NOT kill the winner — they are " +
                        "still alive and the run will end around them");
            }
            catch (System.Exception e) { Plugin.Log.LogWarning($"[BR] self-destruct failed: {e.Message}"); }
        }

        // ---------------------------------------------------------------- match rules (local)

        /// <summary>Apply the per-machine parts of the ruleset once the run is live: a fully
        /// stocked shop, no starting money, clear ground around the spawns, and (defensively) the
        /// standard loadout's economy baseline. Everything here is local state on each machine, so
        /// each applies its own.</summary>
        public static void ApplyLocalMatchRules(NetSession session)
        {
            RevealWholeMap();
            ShowEveryStationOnMap();
            ClearSpawnAreas(session);

            try
            {
                var runData = ServiceLocator.Get<RunData>();
                if (runData == null) return;
                // Every upgrade purchasable from minute one — BR has no unlock progression, but
                // prices stay normal so gold still matters.
                Traverse.Create(runData).Method("AddAllItemsToShop").GetValue();
                Plugin.Log.LogInfo("[BR] shop fully stocked");
            }
            catch (System.Exception e) { Plugin.Log.LogWarning($"[BR] shop stock failed: {e.Message}"); }

            try
            {
                // Vanilla already starts a run at zero currency; make it explicit so a future
                // change to starting money can't quietly hand BR players a head start.
                var runData = ServiceLocator.Get<RunData>();
                var tanks = runData != null
                    ? Traverse.Create(runData).Property("SharedResourceTanks").GetValue() as IEnumerable<ResourceTank>
                    : null;
                if (tanks == null) return;
                foreach (var tank in tanks) if (tank != null) tank.Value = 0f;
                Plugin.Log.LogInfo("[BR] starting currency zeroed");
            }
            catch { /* vanilla default is already zero */ }
        }

        /// <summary>Put EVERY shop on the map, not just the ones nearby.
        ///
        /// Revealing the map (DiscoverWholeMap) reveals the TERRAIN. Icons are a separate system:
        /// vanilla creates one when an entity's GameObject spawns (EntityMapItem.Bind), so a station
        /// on the far side of the world — never streamed in — simply has no icon to show. In a
        /// normal run that is correct, because you have not found it yet. In Battle Royale, where
        /// the whole map is readable and every shop is unlocked from the start, it reads as a broken
        /// map: field-reported 2026-07-28, "the map is not displaying all of the shop locations".
        ///
        /// MapIconManager.SetIconToOverdrawn is the game's own answer — it creates the icon if
        /// missing AND marks it always-visible, which is exactly what the instrument/scanner does
        /// when it permanently reveals a point of interest. Local, and derived from world data every
        /// machine already has, so nothing is sent.</summary>
        private static void ShowEveryStationOnMap()
        {
            try
            {
                var em = ServiceLocator.Get<EntityManager>();
                var icons = ServiceLocator.Get<MapIconManager>();
                if (em == null || icons == null) return;
                int shown = 0;
                foreach (var station in em.GetEntitiesWithComponent<Station.Data>().ToList())
                {
                    if (station?.entity == null) continue;
                    icons.SetIconToOverdrawn(station.entity);
                    shown++;
                }
                Plugin.Log.LogInfo($"[BR] {shown} shops pinned to the map (all of them, always visible)");
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"[BR] could not pin shops to the map: {e.Message} — " +
                    "only shops you have streamed in will show");
            }
        }

        /// <summary>No hidden ground in Battle Royale: the whole map is readable from the first
        /// second so players can plan a route to the next ring instead of exploring blind. The
        /// game already has this exact operation for its own scanners — DiscoverWholeMap — so BR
        /// just calls it once per machine at match start rather than faking a reveal.</summary>
        private static bool _mapRevealed;
        private static float _nextRevealTryAt;

        private static void RevealWholeMap()
        {
            _mapRevealed = false;
            _nextRevealTryAt = 0f;
            TryRevealWholeMap();
        }

        /// <summary>Retried until it lands: at go-live the map objects may not exist yet, and a
        /// one-shot attempt that quietly missed would leave players exploring blind for the whole
        /// match with nothing in the log to explain it.</summary>
        private static void TryRevealWholeMap()
        {
            if (_mapRevealed || Time.unscaledTime < _nextRevealTryAt) return;
            _nextRevealTryAt = Time.unscaledTime + 1f;
            try
            {
                var drawer = Object.FindFirstObjectByType<MapDrawer>();
                if (drawer == null) return; // not loaded yet — try again next second
                drawer.DiscoverWholeMap();
                _mapRevealed = true;
                Plugin.Log.LogInfo("[BR] whole map revealed (no fog of war)");
            }
            catch (System.Exception e)
            {
                _mapRevealed = true; // don't spin on a real failure
                Plugin.Log.LogWarning($"[BR] map reveal failed: {e.Message}");
            }
        }

        // ---------------------------------------------------------------- HUD

        private static GUIStyle _hudStyle;

        /// <summary>Match clock, ring state, and how many players are left. Drawn on every machine
        /// from the host's last ring broadcast.</summary>
        public static void DrawHud()
        {
            if (!Active || !RingKnown) return;
            if (_hudStyle == null)
                _hudStyle = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.UpperCenter,
                    fontSize = 15,
                    fontStyle = FontStyle.Bold,
                };

            // Only the NEXT CLOSURE, never the match clock. A total-time-remaining readout tells a
            // player nothing they can act on and quietly reframes the match as a countdown to an
            // ending rather than a countdown to the next thing that will kill them (Omar,
            // 2026-07-28). What matters is always "how long until the ground moves".
            int shrinkIn = Mathf.CeilToInt(Mathf.Max(0f, Ring.NextShrinkIn));
            string clock = $"{shrinkIn / 60:00}:{shrinkIn % 60:00}";
            string ring = Ring.Closing
                ? $"RING CLOSING {Ring.Stage}/{Ring.TotalStages}"
                : Ring.Stage >= Ring.TotalStages
                    ? "FINAL RING"
                    : $"NEXT CLOSURE IN";

            var ship = ShipSync.LocalShip;
            bool outside = ship != null && !ship.IsDead
                && Vector2.Distance(ship.transform.position, new Vector2(Ring.CenterX, Ring.CenterY)) > Ring.SafeRadius;

            _hudStyle.normal.textColor = outside ? new Color(1f, 0.35f, 0.25f) : Color.white;
            var rect = new Rect(Screen.width * 0.5f - 300f, 8f, 600f, 44f);
            GUI.Label(rect, $"{ring}   {clock}", _hudStyle);
            if (outside)
                GUI.Label(new Rect(rect.x, rect.y + 20f, rect.width, 24f),
                    "IN THE ZONE — GET BACK INSIDE", _hudStyle);
        }
    }
}
