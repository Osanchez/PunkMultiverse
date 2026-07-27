using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using PunkMultiverse.Core;
using PunkMultiverse.Protocol;
using PunkMultiverse.Sync;
using UnityEngine;

namespace PunkMultiverse.Modes
{
    /// <summary>
    /// Host half of Battle Royale: match setup, the lava ring, care packages, and the win
    /// condition. Everything here runs ONLY on the host — clients receive the results as
    /// broadcasts and never recompute them (docs/BATTLE_ROYALE.md).
    /// </summary>
    internal static partial class BattleRoyale
    {
        // ---------------------------------------------------------------- setup

        /// <summary>Go-live in a BR run: seal the roster, pick the ring, and apply the match
        /// rules to the world. Host only — the world changes made here replicate through the
        /// systems that already replicate them (station unlocks, terrain diffs).</summary>
        public static void BeginMatch(NetSession session)
        {
            if (session == null || !session.IsHost) return;
            Reset();
            _active = true;
            _matchStart = Time.unscaledTime;
            _matchSeconds = Mathf.Max(60f, NetConfig.BrMatchMinutes.Value * 60f);
            _ringStartSeconds = Mathf.Clamp(NetConfig.BrRingStartMinutes.Value * 60f, 0f, _matchSeconds - 30f);
            _stages = Mathf.Max(1, NetConfig.BrRingStages.Value);

            MatchPlayers.Clear();
            foreach (var p in session.Players)
                if (p != null && p.Connected && !p.IsCoordinator) MatchPlayers.Add(p.Slot);
            if (MatchPlayers.Count < Mathf.Max(1, NetConfig.BrMinPlayers.Value))
                Plugin.Log.LogWarning($"[BR] starting with only {MatchPlayers.Count} player(s) — " +
                    "a match this small ends immediately (testing only)");

            ComputeSchedule();
            PickRingCenter();
            OpenAllStations();
            ClearHazardCells();
            _nextCarePackageAt = NetConfig.BrCarePackageMinutes.Value > 0
                ? NetConfig.BrCarePackageMinutes.Value * 60f
                : float.MaxValue;

            Plugin.Log.LogInfo($"[BR] MATCH START — {MatchPlayers.Count} players, {_matchSeconds / 60f:0} min, " +
                $"ring center ({_center.x:0},{_center.y:0}) r={_startRadius:0}, {_stages} stages");
            Announce(session, $"BATTLE ROYALE — {MatchPlayers.Count} PLAYERS. LAST ONE ALIVE WINS.", 8f);
            BroadcastRing(session);
        }

        /// <summary>Every station opens at match start: BR is about fighting over a map you can
        /// already shop on, not about unlock progression. Uses the game's own unlock primitive
        /// (installing an upgrade IS the unlock), which the existing progression sync captures and
        /// replicates to every client for free.</summary>
        private static void OpenAllStations()
        {
            int opened = 0;
            try
            {
                var em = ServiceLocator.Get<EntityManager>();
                if (em == null) return;
                foreach (var data in em.GetEntitiesWithComponent<Station.Data>().ToList())
                {
                    if (data == null || data.IsUnlocked) continue;
                    var upgrade = data.allUpgrades != null && data.allUpgrades.Count > 0 ? data.allUpgrades[0] : null;
                    if (upgrade == null) continue;
                    data.Install(upgrade);
                    opened++;
                }
            }
            catch (System.Exception e) { Plugin.Log.LogWarning($"[BR] open-all-stations failed: {e.Message}"); }
            Plugin.Log.LogInfo($"[BR] opened {opened} stations");
        }

        /// <summary>Maps ship with damaging terrain baked in; in BR the ring must be the only
        /// lethal ground, so anything already burning gets cleared. Writes go through the game's
        /// SetCell, so the existing terrain sync replicates and verifies them.</summary>
        private static void ClearHazardCells()
        {
            try
            {
                var level = LevelRef;
                if (level == null) return;
                byte hazard = RingCellId;
                if (hazard == 0) return; // nothing damaging exists on this map
                var cells = Traverse.Create(level).Field("cellTypes").GetValue();
                if (!(cells is Unity.Collections.NativeArray<byte> native) || !native.IsCreated) return;
                int cleared = 0;
                for (int i = 0; i < native.Length; i++)
                {
                    if (native[i] != hazard) continue;
                    level.SetCell(i, 0);
                    cleared++;
                }
                if (cleared > 0) Plugin.Log.LogInfo($"[BR] cleared {cleared} pre-existing hazard cells");
            }
            catch (System.Exception e) { Plugin.Log.LogWarning($"[BR] hazard clear failed: {e.Message}"); }
        }

        // ---------------------------------------------------------------- ring geometry

        private static Level LevelRef
        {
            get { try { return ServiceLocator.Get<Level>(); } catch { return null; } }
        }

        private static byte _ringCellId = 255; // 255 = unresolved, 0 = none available

        /// <summary>The most damaging terrain the game has on hand — that is what the ring is made
        /// of. Resolved once at runtime because cell type ids are registered per run, not fixed.
        /// Contact damage is applied by the victim's own machine, so no damage sync is involved.</summary>
        private static byte RingCellId
        {
            get
            {
                if (_ringCellId != 255) return _ringCellId;
                _ringCellId = 0;
                try
                {
                    CellType best = null;
                    float bestDamage = 0f;
                    foreach (var ct in Resources.FindObjectsOfTypeAll<CellType>())
                    {
                        if (ct == null || ct.id == 0) continue;
                        if (ct.name != null && ct.name.IndexOf("fog", System.StringComparison.OrdinalIgnoreCase) >= 0)
                            continue; // fog is a simulated gas, not a wall
                        // contactDamage is a Damage STRUCT, not a float — read its amount.
                        float dmg = 0f;
                        try
                        {
                            var contact = Traverse.Create(ct).Field("contactDamage").GetValue();
                            if (contact != null) dmg = Traverse.Create(contact).Field("amount").GetValue<float>();
                        }
                        catch { }
                        if (dmg <= bestDamage) continue;
                        bestDamage = dmg;
                        best = ct;
                    }
                    if (best != null)
                    {
                        _ringCellId = best.id;
                        Plugin.Log.LogInfo($"[BR] ring material = '{best.name}' id={best.id} contactDamage={bestDamage}");
                    }
                    else Plugin.Log.LogWarning("[BR] no damaging cell type found — the ring will rely on the kill zone only");
                }
                catch (System.Exception e) { Plugin.Log.LogWarning($"[BR] ring material lookup failed: {e.Message}"); }
                return _ringCellId;
            }
        }

        /// <summary>The final zone should be somewhere players can actually fight, so candidate
        /// centers are scored by how much open space surrounds them and the most open wins.</summary>
        private static void PickRingCenter()
        {
            var level = LevelRef;
            int w = level != null ? level.Width : 2000;
            int h = level != null ? level.Height : 2000;
            _center = new Vector2(w * 0.5f, h * 0.5f);

            try
            {
                var cells = Traverse.Create(level).Field("cellTypes").GetValue();
                if (cells is Unity.Collections.NativeArray<byte> native && native.IsCreated)
                {
                    var rnd = new System.Random(NetSession.Instance?.CurrentRunSeed ?? 12345);
                    float bestScore = -1f;
                    const int candidates = 64, samples = 200, probeRadius = 60;
                    for (int c = 0; c < candidates; c++)
                    {
                        // Middle half of the map: a corner "center" would make half the ring
                        // stages pointless.
                        float cx = w * 0.25f + (float)rnd.NextDouble() * w * 0.5f;
                        float cy = h * 0.25f + (float)rnd.NextDouble() * h * 0.5f;
                        int open = 0;
                        for (int s = 0; s < samples; s++)
                        {
                            double a = rnd.NextDouble() * System.Math.PI * 2.0;
                            double r = System.Math.Sqrt(rnd.NextDouble()) * probeRadius;
                            int x = (int)(cx + r * System.Math.Cos(a));
                            int y = (int)(cy + r * System.Math.Sin(a));
                            if (x < 0 || y < 0 || x >= w || y >= h) continue;
                            if (native[y * w + x] == 0) open++;
                        }
                        float score = open / (float)samples;
                        if (score <= bestScore) continue;
                        bestScore = score;
                        _center = new Vector2(cx, cy);
                    }
                    Plugin.Log.LogInfo($"[BR] ring center ({_center.x:0},{_center.y:0}) openness={bestScore:P0}");
                }
            }
            catch (System.Exception e) { Plugin.Log.LogWarning($"[BR] ring center pick failed: {e.Message}"); }

            // Cover the whole map from wherever the center landed.
            float far = 0f;
            far = Mathf.Max(far, Vector2.Distance(_center, new Vector2(0, 0)));
            far = Mathf.Max(far, Vector2.Distance(_center, new Vector2(w, 0)));
            far = Mathf.Max(far, Vector2.Distance(_center, new Vector2(0, h)));
            far = Mathf.Max(far, Vector2.Distance(_center, new Vector2(w, h)));
            _startRadius = far;
            _paintedRadius = far;
        }

        // The ring alternates HOLD and CLOSE, the way a battle royale is supposed to feel: the zone
        // sits still long enough to fight over, then draws in over a couple of minutes to the next
        // ring. Radius steps are equal, so each closure travels the same distance and the pace
        // never surprises you. A slow, even creep is also what keeps terrain painting cheap — the
        // front only advances a fraction of a cell per second.
        private static float _stageSpan;    // hold + close for one stage
        private static float _closeSeconds; // how long a single closure takes

        private static void ComputeSchedule()
        {
            float span = Mathf.Max(1f, _matchSeconds - _ringStartSeconds);
            _stageSpan = span / Mathf.Max(1, _stages);
            // Honour the configured closing time, but never let it swallow the whole stage — a
            // compressed test match has short stages and still needs a visible hold phase.
            _closeSeconds = Mathf.Min(Mathf.Max(5f, NetConfig.BrRingCloseSeconds.Value), _stageSpan * 0.6f);
        }

        /// <summary>Radius of the ring that a completed stage leaves behind. Even steps: stage k of
        /// N lands on startRadius * (1 - k/N), so the last one lands exactly on zero.</summary>
        private static float RadiusAfterStage(int stage) =>
            Mathf.Max(0f, _startRadius * (1f - Mathf.Clamp01(stage / (float)Mathf.Max(1, _stages))));

        /// <summary>Where the ring is right now, and where it is heading.</summary>
        private static void RingAt(float elapsed, out float radius, out int stage,
            out bool closing, out float nextTarget, out float phaseRemaining)
        {
            if (elapsed <= _ringStartSeconds)
            {
                radius = _startRadius;
                stage = 0;
                closing = false;
                nextTarget = RadiusAfterStage(1);
                phaseRemaining = _ringStartSeconds - elapsed;
                return;
            }
            float t = elapsed - _ringStartSeconds;
            int done = Mathf.FloorToInt(t / _stageSpan);     // fully completed stages
            if (done >= _stages)
            {
                radius = 0f;
                stage = _stages;
                closing = false;
                nextTarget = 0f;
                phaseRemaining = 0f;
                return;
            }
            float within = t - done * _stageSpan;
            float from = RadiusAfterStage(done);
            float to = RadiusAfterStage(done + 1);
            float hold = Mathf.Max(0f, _stageSpan - _closeSeconds);
            if (within < hold)
            {
                radius = from;
                stage = done;
                closing = false;
                nextTarget = to;
                phaseRemaining = hold - within;
            }
            else
            {
                float f = Mathf.Clamp01((within - hold) / Mathf.Max(0.001f, _closeSeconds));
                radius = Mathf.Lerp(from, to, f);
                stage = done + 1;
                closing = true;
                nextTarget = to;
                phaseRemaining = _closeSeconds * (1f - f);
            }
        }

        private static float RadiusAt(float elapsed)
        {
            RingAt(elapsed, out float r, out _, out _, out _, out _);
            return r;
        }

        private static int StageAt(float elapsed)
        {
            RingAt(elapsed, out _, out int stage, out _, out _, out _);
            return stage;
        }

        // ---------------------------------------------------------------- host tick

        private static float _nextCarePackageAt;
        private const float WallThickness = 32f;   // cells; thick enough to be a wall, thin enough
                                                   // that the terrain ledger stays bounded
        private const float PaintStep = 4f;        // repaint once the front has moved this far

        /// <summary>Host: advance the match. Called every frame while InGame.</summary>
        public static void HostTick(NetSession session)
        {
            if (!_active || session == null || !session.IsHost) return;
            if (session.State != SessionState.InGame) return;

            float elapsed = Time.unscaledTime - _matchStart;
            RingAt(elapsed, out float radius, out int stage, out bool closing,
                out float nextTarget, out float phaseRemaining);

            // Announce when a closure STARTS, so the warning means "move now" rather than marking
            // an invisible boundary. The zone it is closing to is on the map from this moment.
            if (closing && stage != _lastAnnouncedStage)
            {
                _lastAnnouncedStage = stage;
                if (stage >= _stages) Announce(session, "FINAL RING — NOWHERE LEFT TO RUN", 8f);
                else Announce(session, $"THE LAVA RING IS CLOSING ({stage}/{_stages}) — CHECK YOUR MAP", 7f);
                BroadcastRing(session);
            }

            if (radius < _paintedRadius - PaintStep)
            {
                PaintRing(radius);
                _paintedRadius = radius;
            }

            if (Time.unscaledTime >= _nextRingBroadcastAt)
            {
                _nextRingBroadcastAt = Time.unscaledTime + 5f;
                BroadcastRing(session);
            }

            if (elapsed >= _nextCarePackageAt)
            {
                _nextCarePackageAt = elapsed + Mathf.Max(60f, NetConfig.BrCarePackageMinutes.Value * 60f);
                // Drop into ground that stays safe through the NEXT closure, so a package is never
                // swallowed by lava before anyone can reach it.
                DropCarePackage(session, Mathf.Max(20f, nextTarget));
            }

            CheckLastAlive(session);
        }

        private static void BroadcastRing(NetSession session)
        {
            float elapsed = Time.unscaledTime - _matchStart;
            RingAt(elapsed, out float radius, out int stage, out bool closing,
                out float nextTarget, out float phaseRemaining);
            var msg = new RingStateMsg
            {
                CenterX = _center.x,
                CenterY = _center.y,
                SafeRadius = radius,
                TargetRadius = nextTarget,   // what the map draws players a path toward
                Closing = closing,
                Stage = (byte)Mathf.Clamp(stage, 0, 255),
                TotalStages = (byte)Mathf.Clamp(_stages, 0, 255),
                NextShrinkIn = Mathf.Max(0f, phaseRemaining),
                MatchRemaining = Mathf.Max(0f, _matchSeconds - elapsed),
            };
            ApplyRingState(msg); // the host runs the same HUD/damage path as everyone else
            var w = new NetWriter(64);
            msg.Write(w);
            session.SendToAll(Transport.NetChannel.Control, w.ToSegment(), reliable: true);
        }

        /// <summary>Paint the wall at the current boundary. Only the newly-crossed annulus is
        /// written, so the cost is proportional to how far the front moved, not to the burned
        /// area — which also keeps the terrain ledger bounded.</summary>
        private static void PaintRing(float radius)
        {
            byte id = RingCellId;
            if (id == 0) return; // kill zone still applies
            var level = LevelRef;
            if (level == null) return;
            int w = level.Width, h = level.Height;
            float outer = Mathf.Min(_paintedRadius, radius + WallThickness);
            float inner = radius;
            if (outer <= inner) return;

            int minX = Mathf.Max(0, Mathf.FloorToInt(_center.x - outer));
            int maxX = Mathf.Min(w - 1, Mathf.CeilToInt(_center.x + outer));
            int minY = Mathf.Max(0, Mathf.FloorToInt(_center.y - outer));
            int maxY = Mathf.Min(h - 1, Mathf.CeilToInt(_center.y + outer));
            float innerSq = inner * inner, outerSq = outer * outer;
            int painted = 0;
            for (int y = minY; y <= maxY; y++)
            {
                float dy = y - _center.y;
                for (int x = minX; x <= maxX; x++)
                {
                    float dx = x - _center.x;
                    float d2 = dx * dx + dy * dy;
                    if (d2 < innerSq || d2 > outerSq) continue;
                    level.SetCell(y * w + x, id); // changeSource 0 => captured + replicated
                    painted++;
                }
            }
            if (painted > 0) InstrumentationCounters.BrRingCellsPainted(painted);
        }

        // ---------------------------------------------------------------- care packages

        private static void DropCarePackage(NetSession session, float radius)
        {
            try
            {
                var level = LevelRef;
                var egm = ServiceLocator.Get<EntityGameObjectManager>();
                if (level == null || egm == null || egm.savablesCollection == null) return;
                var cells = Traverse.Create(level).Field("cellTypes").GetValue();
                if (!(cells is Unity.Collections.NativeArray<byte> native) || !native.IsCreated) return;

                // Somewhere open, inside the safe zone, so it is contestable rather than lethal.
                var rnd = new System.Random((int)(Time.unscaledTime * 1000f));
                int w = level.Width, h = level.Height;
                Vector2 spot = _center;
                for (int attempt = 0; attempt < 200; attempt++)
                {
                    double a = rnd.NextDouble() * System.Math.PI * 2.0;
                    double r = System.Math.Sqrt(rnd.NextDouble()) * Mathf.Max(20f, radius * 0.8f);
                    int x = (int)(_center.x + r * System.Math.Cos(a));
                    int y = (int)(_center.y + r * System.Math.Sin(a));
                    if (x < 1 || y < 1 || x >= w - 1 || y >= h - 1) continue;
                    if (native[y * w + x] != 0) continue;
                    spot = new Vector2(x, y);
                    break;
                }

                if (!TryPickCarePackagePrefab(egm, out var prefab, out string prefabId))
                { Plugin.Log.LogWarning("[BR] no care-package prefab available"); return; }
                var spawned = egm.CreateEntity(prefab, spot); // replicates via runtime-spawn capture
                if (spawned == null) return;
                int netId = 0;
                var se = spawned.GetComponent<SavableEntity>();
                if (se != null && se.EntityData != null) NetIds.TryGetNetId(se.EntityData.instanceId, out netId);
                CarePackages[netId] = spot;
                Plugin.Log.LogInfo($"[BR] care package '{prefabId}' #{netId} at ({spot.x:0},{spot.y:0})");

                var w2 = new NetWriter(32);
                new CarePackageMsg { NetId = netId, X = spot.x, Y = spot.y }.Write(w2);
                session.SendToAll(Transport.NetChannel.Control, w2.ToSegment(), reliable: true);
                Announce(session, "SUPPLY DROP INBOUND — CHECK YOUR MAP. DESTROY IT TO CLAIM IT", 7f);
            }
            catch (System.Exception e) { Plugin.Log.LogWarning($"[BR] care package drop failed: {e.Message}"); }
        }

        /// <summary>A destructible prop to use as the supply drop. Preference order is by entity
        /// id so the choice is stable across machines; anything destructible works because the
        /// reward rides the normal kill-credit path (whoever destroys it gets the drop, on their
        /// machine only).</summary>
        private static bool TryPickCarePackagePrefab(EntityGameObjectManager egm,
            out SavableEntity prefab, out string entityId)
        {
            prefab = null;
            entityId = null;
            var infos = egm.savablesCollection.savableObjectInfos;
            if (infos == null) return false;
            foreach (var want in new[] { "Crate", "Chest", "Container", "Barrel", "Box", "Rock" })
            {
                foreach (var info in infos)
                {
                    string id = info.entityId;
                    if (string.IsNullOrEmpty(id)) continue;
                    if (id.IndexOf(want, System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                    prefab = info.prefab;
                    entityId = id;
                    if (prefab != null) return true;
                }
            }
            return false;
        }

        // ---------------------------------------------------------------- win condition

        /// <summary>BR ends when one player is left standing — the co-op wipe check counts
        /// "is anyone alive", this counts "how many". Disconnects are eliminations too, so a
        /// player leaving can end the match.</summary>
        private static void CheckLastAlive(NetSession session)
        {
            int alive = 0;
            byte lastAliveSlot = 0;
            foreach (byte slot in MatchPlayers)
            {
                if (Eliminated.Contains(slot)) continue;
                var p = slot < session.Players.Count ? session.Players[slot] : null;
                if (p == null || !p.Connected) { Eliminate(session, slot, "left the match"); continue; }
                bool dead = p.IsLocal
                    ? (ShipSync.LocalShip != null && ShipSync.LocalShip.IsDead)
                    : ShipSync.IsSlotDead(slot);
                if (dead) { Eliminate(session, slot, "eliminated"); continue; }
                alive++;
                lastAliveSlot = slot;
            }

            if (alive > 1) { _lastAliveSince = -1f; return; }
            if (_lastAliveSince < 0f) { _lastAliveSince = Time.unscaledTime; return; }
            if (Time.unscaledTime - _lastAliveSince < 2f) return; // same debounce the wipe check uses

            if (alive == 1) Win(session, lastAliveSlot);
            else
            {
                // Everyone died together (or all left): the last one out is the winner, per spec.
                byte best = 0; int bestPlacement = int.MaxValue;
                foreach (var kv in Placements)
                    if (kv.Value < bestPlacement) { bestPlacement = kv.Value; best = kv.Key; }
                if (bestPlacement != int.MaxValue) Win(session, best, alreadyEliminated: true);
                else EndMatch(session);
            }
        }

        private static void Eliminate(NetSession session, byte slot, string why)
        {
            if (!Eliminated.Add(slot)) return;
            int placement = MatchPlayers.Count - Eliminated.Count + 1;
            Placements[slot] = (byte)placement;
            int remaining = MatchPlayers.Count - Eliminated.Count;
            var p = slot < session.Players.Count ? session.Players[slot] : null;
            Plugin.Log.LogInfo($"[BR] P{slot + 1} {why} — placed #{placement}, {remaining} remain");
            Broadcast(session, new PlacementMsg
            {
                Slot = slot,
                Placement = (byte)placement,
                AliveRemaining = (byte)Mathf.Max(0, remaining),
                TotalPlayers = (byte)MatchPlayers.Count,
                IsWinner = false,
            });
        }

        private static void Win(NetSession session, byte slot, bool alreadyEliminated = false)
        {
            if (!_active) return;
            _active = false;
            if (!alreadyEliminated) Placements[slot] = 1;
            var p = slot < session.Players.Count ? session.Players[slot] : null;
            Plugin.Log.LogInfo($"[BR] WINNER: P{slot + 1} '{p?.Name ?? "?"}'");
            Broadcast(session, new PlacementMsg
            {
                Slot = slot,
                Placement = 1,
                AliveRemaining = 1,
                TotalPlayers = (byte)MatchPlayers.Count,
                IsWinner = true,
            });
            // Hold the run open long enough for the victory callout AND the winner's self-destruct
            // (armed on their machine the moment this broadcast lands) — otherwise the lobby kick
            // would beat the countdown and the winner would never actually scuttle.
            EndMatch(session, delaySeconds: Mathf.Max(8f,
                NetConfig.BrWinnerSelfDestructSeconds.Value + 4f));
        }

        private static float _endMatchAt = -1f;

        private static void EndMatch(NetSession session, float delaySeconds = 3f)
        {
            _active = false;
            _endMatchAt = Time.unscaledTime + delaySeconds;
        }

        /// <summary>Host: after the victory screen has had a moment, return everyone to the lobby
        /// through the normal run-end path (which on a dedicated server also pre-builds the next
        /// world).</summary>
        public static void TickMatchEnd(NetSession session)
        {
            if (_endMatchAt < 0f || Time.unscaledTime < _endMatchAt) return;
            _endMatchAt = -1f;
            session.EndRunForMode("battle royale finished");
        }

        private static void Broadcast(NetSession session, PlacementMsg msg)
        {
            ApplyPlacement(msg, session); // host sees it too
            var w = new NetWriter(32);
            msg.Write(w);
            session.SendToAll(Transport.NetChannel.Control, w.ToSegment(), reliable: true);
        }
    }
}
