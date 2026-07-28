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
            // The map's own hazards are left alone. They only ever needed clearing so the PAINTED
            // ring could be the one lethal ground; the ring is now a rendered zone, visually
            // unmistakable, and wiping ~100k cells at go-live was itself a terrain-diff burst
            // replicated to every client at the worst possible moment.
            _nextCarePackageAt = NetConfig.BrCarePackageMinutes.Value > 0
                ? NetConfig.BrCarePackageMinutes.Value * 60f
                : float.MaxValue;

            // The closure RATE is the number that decides whether the ring reads as pressure or as
            // scenery, so state it outright instead of leaving it to be derived from four configs.
            float stepPerStage = _startRadius / Mathf.Max(1, _stages);
            Plugin.Log.LogInfo($"[BR] MATCH START — {MatchPlayers.Count} players, {_matchSeconds / 60f:0} min, " +
                $"ring center ({_center.x:0},{_center.y:0}) r={_startRadius:0}, {_stages} stages, " +
                $"each closing {stepPerStage:0}u over {_closeSeconds:0}s = {stepPerStage / Mathf.Max(1f, _closeSeconds):0.0} u/s " +
                $"(hold {Mathf.Max(0f, _stageSpan - _closeSeconds):0}s between)");
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

        // ---------------------------------------------------------------- ring geometry

        private static Level LevelRef
        {
            get { try { return ServiceLocator.Get<Level>(); } catch { return null; } }
        }

        // The PLAYABLE map, which is not the cell grid. BorderGenerator stamps everything outside
        // distance Width/2 of the grid centre as the VOID biome, so the world a player can reach is
        // a DISC inscribed in a square array — measured here rather than assumed, because the
        // generator's radius is config-driven and the array need not be square.
        private static Vector2 _mapCenter;
        private static float _mapRadius;

        /// <summary>Measure the playable disc from the biome map: the bounding box of every cell
        /// that is not VOID. Used for both the ring's size and its centre, because sizing the ring
        /// off the cell ARRAY instead put its first ~22% of travel entirely outside the world —
        /// field-reported as "the ring starts way out in world space" (2026-07-27; the match log
        /// showed centre (1282,955) r=1654 on a disc of radius ~1000).</summary>
        private static void MeasurePlayableArea(Level level)
        {
            int w = level != null ? level.Width : 2000;
            int h = level != null ? level.Height : 2000;
            _mapCenter = new Vector2(w * 0.5f, h * 0.5f);
            _mapRadius = Mathf.Min(w, h) * 0.5f;
            _voidBiomeId = 255;

            try
            {
                var cfg = ServiceLocator.Get<LevelGeneratorConfig>();
                var voidBiom = cfg != null ? cfg.voidBiom : null;
                if (voidBiom == null || level == null) return;
                _voidBiomeId = voidBiom.id;

                var bioms = level.bioms;
                if (!bioms.IsCreated || bioms.Length < w * h) return;

                int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
                for (int y = 0; y < h; y++)
                {
                    int row = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        if (bioms[row + x] == _voidBiomeId) continue;
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
                if (minX > maxX || minY > maxY) return; // all void — keep the array-based fallback

                _mapCenter = new Vector2((minX + maxX) * 0.5f, (minY + maxY) * 0.5f);
                // The playable region is a disc, so its bounding box is a square whose half-width
                // IS the radius. Take the smaller half-extent: never claim ground that isn't there.
                _mapRadius = Mathf.Min(maxX - minX + 1, maxY - minY + 1) * 0.5f;
                Plugin.Log.LogInfo($"[BR] playable map = disc centre ({_mapCenter.x:0},{_mapCenter.y:0}) " +
                    $"r={_mapRadius:0} (grid {w}x{h}, void biome id {_voidBiomeId})");
            }
            catch (System.Exception e) { Plugin.Log.LogWarning($"[BR] playable-area measure failed: {e.Message}"); }
        }

        private static byte _voidBiomeId = 255; // 255 = unknown; nothing is treated as void

        private static bool IsVoidCell(Level level, int x, int y)
        {
            if (_voidBiomeId == 255 || level == null) return false;
            try
            {
                var bioms = level.bioms;
                int idx = y * level.Width + x;
                return bioms.IsCreated && idx >= 0 && idx < bioms.Length && bioms[idx] == _voidBiomeId;
            }
            catch { return false; }
        }

        /// <summary>The final zone should be somewhere players can actually fight, so candidate
        /// centers are scored by how much open space surrounds them and the most open wins.
        ///
        /// Candidates are drawn from a disc around the MAP's centre, not from the middle half of
        /// the cell array: the further the ring's centre sits from the map's, the more of its
        /// travel is spent shrinking through ground nobody can stand on (the ring has to start big
        /// enough to contain the whole disc, so every unit of offset costs two units of dead
        /// radius). "Open" also now means open AND real — void cells are empty, so the old scoring
        /// rated the border as the most fightable ground on the map.</summary>
        private static void PickRingCenter()
        {
            var level = LevelRef;
            MeasurePlayableArea(level);
            _center = _mapCenter;

            try
            {
                var cells = Traverse.Create(level).Field("cellTypes").GetValue();
                if (cells is Unity.Collections.NativeArray<byte> native && native.IsCreated && level != null)
                {
                    int w = level.Width, h = level.Height;
                    var rnd = new System.Random(NetSession.Instance?.CurrentRunSeed ?? 12345);
                    float bestScore = -1f;
                    const int candidates = 64, samples = 200, probeRadius = 60;
                    float drift = _mapRadius * CenterDriftFraction;
                    for (int c = 0; c < candidates; c++)
                    {
                        double ca = rnd.NextDouble() * System.Math.PI * 2.0;
                        double cr = System.Math.Sqrt(rnd.NextDouble()) * drift;
                        float cx = _mapCenter.x + (float)(cr * System.Math.Cos(ca));
                        float cy = _mapCenter.y + (float)(cr * System.Math.Sin(ca));
                        int open = 0;
                        for (int s = 0; s < samples; s++)
                        {
                            double a = rnd.NextDouble() * System.Math.PI * 2.0;
                            double r = System.Math.Sqrt(rnd.NextDouble()) * probeRadius;
                            int x = (int)(cx + r * System.Math.Cos(a));
                            int y = (int)(cy + r * System.Math.Sin(a));
                            if (x < 0 || y < 0 || x >= w || y >= h) continue;
                            if (IsVoidCell(level, x, y)) continue; // outside the world, not "open"
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

            // The SMALLEST circle around the chosen centre that still contains the whole playable
            // disc. Everyone starts inside the ring (nobody burns at t=0) and not one metre of the
            // schedule is spent closing through the void.
            _startRadius = _mapRadius + Vector2.Distance(_center, _mapCenter);
            Plugin.Log.LogInfo($"[BR] ring start radius {_startRadius:0} " +
                $"(map r={_mapRadius:0} + centre offset {Vector2.Distance(_center, _mapCenter):0})");
        }

        /// <summary>How far the ring's centre may sit from the map's, as a fraction of the map
        /// radius. Every unit of offset adds a unit of radius the ring must close through before it
        /// touches the world, so this buys variety at a directly measurable cost in pacing.</summary>
        private const float CenterDriftFraction = 0.15f;

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

            // NOTHING IS PAINTED. The zone is rendered (UI/RingLavaVisual.cs) and enforced by a
            // radius check each client applies to its own ship — the model every large battle
            // royale uses, and the reason theirs cost nothing. See the ring section of
            // docs/BATTLE_ROYALE.md for the measurements that ended the terrain version.

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
