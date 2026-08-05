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
            // Filled in by ComputeSchedule, which distributes BrMatchMinutes across the stages on a
            // curve. There is no separate grace field any more: the opening window is stage 1's own
            // wait, which is what keeps the first on-screen countdown honest.
            _matchSeconds = 0f;
            _stages = Mathf.Max(1, NetConfig.BrRingStages.Value);

            MatchPlayers.Clear();
            foreach (var p in session.Players)
                if (p != null && p.Connected && !p.IsCoordinator) MatchPlayers.Add(p.Slot);
            if (MatchPlayers.Count < Mathf.Max(1, NetConfig.BrMinPlayers.Value))
                Plugin.Log.LogWarning($"[BR] starting with only {MatchPlayers.Count} player(s) — " +
                    "a match this small ends immediately (testing only)");

            ComputeSchedule();
            PickRingCenter();
            // Stations are opened LATER — see TickStationUnlock. Opening them here breaks the
            // vanilla start cinematic on any machine that has not picked its start station yet.
            _openStationsAt = Mathf.Max(0f, NetConfig.BrStationUnlockDelaySeconds.Value);
            _stationsOpened = false;
            // The map's own hazards are left alone. They only ever needed clearing so the PAINTED
            // ring could be the one lethal ground; the ring is now a rendered zone, visually
            // unmistakable, and wiping ~100k cells at go-live was itself a terrain-diff burst
            // replicated to every client at the worst possible moment.
            // First wave at HALF the interval: a short match should still see a supply drop.
            _nextCarePackageAt = NetConfig.BrCarePackageMinutes.Value > 0
                ? NetConfig.BrCarePackageMinutes.Value * 30f
                : float.MaxValue;

            // Print the whole ladder, stage by stage. Every stage now has its OWN wait and closure,
            // so no single averaged number describes the pacing — and the per-stage line is the
            // only way to check by eye that the match actually tightens the way it is meant to.
            Plugin.Log.LogInfo($"[BR] MATCH START — {MatchPlayers.Count} players, " +
                $"ring center ({_center.x:0},{_center.y:0}) r={_startRadius:0}, {_stages} closures, " +
                $"{_matchSeconds / 60f:0.#} min total");
            for (int k = 0; k < _stages; k++)
            {
                // `drift` is how far the whole circle walks during this closure — the distance a
                // player sitting on the old centre has to cover just to stay in the middle. That is
                // the number that says whether a zone is a rotation or a nudge, so it is logged
                // next to the radius it is paired with.
                float drift = Vector2.Distance(CenterAfterStage(k), CenterAfterStage(k + 1));
                Plugin.Log.LogInfo($"[BR]   zone {k + 1}/{_stages}: wait {_stageWait[k]:0}s, " +
                    $"close {_stageClose[k]:0}s, r {RadiusAfterStage(k):0} -> {RadiusAfterStage(k + 1):0}, " +
                    $"drift {drift:0} (closed by {(_stageBegin[k] + _stageWait[k] + _stageClose[k]) / 60f:0.0} min)");
            }
            VerifyContainment();
            Announce(session, $"BATTLE ROYALE — {MatchPlayers.Count} PLAYERS. LAST ONE ALIVE WINS.", 8f);
            BroadcastRing(session);
        }

        private static float _openStationsAt;
        private static bool _stationsOpened;

        /// <summary>Open every station, but NOT at go-live.
        ///
        /// The vanilla opening cinematic finds the station to pan to with
        /// <c>FirstOrDefault(s =&gt; s.installedUpgrades.Count &gt; 0)</c> — "the one station that
        /// starts unlocked". Unlocking all 49 at go-live makes that lookup return an ARBITRARY
        /// station, and both halves of the cinematic then go wrong (field-reported 2026-07-28):
        ///
        ///   - it calls <c>MoveCameraInstantlyToPosition</c> on that station, so the client snaps
        ///     to a random corner of the map — sometimes another player's spawn — and then races
        ///     back when the scatter teleport lands. That is the "pans to another player's spawn,
        ///     then pans fast to its real spawn".
        ///   - it then spins on <c>while (startStation == null) await NextFrame()</c> waiting for
        ///     that station's GameObject, which on a client is very likely NOT streamed in. The
        ///     sequence never reaches its final line, so <c>shipInput.enabled</c> is never restored:
        ///     dead controls, until StartSequenceWatchdog rescues it 25 seconds later.
        ///
        /// Both disappear if the unlock simply waits until every machine's cinematic has already
        /// chosen its station. The delay costs nothing — BR players are still flying to their
        /// scattered spawns for the first few seconds — and the watchdog remains as the backstop.</summary>
        private static void TickStationUnlock(NetSession session, float elapsed)
        {
            if (_stationsOpened || elapsed < _openStationsAt) return;
            _stationsOpened = true;
            OpenAllStations();
            ClearHazardsAroundStations();
        }

        /// <summary>BR is about fighting over a map you can already shop on, not about unlock
        /// progression. Uses the game's own unlock primitive (installing an upgrade IS the unlock),
        /// which the existing progression sync captures and replicates to every client for free.</summary>
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

        /// <summary>Scrub damaging TERRAIN off the ground around every shop.
        ///
        /// World generation is free to put lava, gas or anything else with contact damage right up
        /// against a station — reasonable in the co-op game, where you arrive on your own terms and
        /// can see it coming. In Battle Royale a station is a SPAWN, and Omar's report stands on
        /// its own: "one of the spawn locations had hazards around it... it seems kind of crazy that
        /// world gen allows hazards close to the shops." Dropping into damage you did not choose and
        /// could not see is not a fight, it is a coin toss.
        ///
        /// Host only, because these are real terrain writes that replicate through the normal cell
        /// pipeline — the enemy sweep next door is the opposite (derived identically everywhere and
        /// sent nowhere), and confusing the two would either double-apply or diverge.
        ///
        /// Radius is deliberately small. This is a landing pad, not a cleared arena: far enough that
        /// you are not standing in fire on arrival, close enough that the hazard still shapes the
        /// ground you fight over. And the cost scales with it — every cleared cell is a replicated
        /// diff, which is the bill the ring taught us to read before writing terrain in bulk.</summary>
        private static void ClearHazardsAroundStations()
        {
            float radius = Mathf.Max(0f, NetConfig.BrStationHazardClearRadius.Value);
            if (radius <= 0f) return;
            try
            {
                var level = LevelRef;
                var em = ServiceLocator.Get<EntityManager>();
                if (level == null || em == null) return;

                var damaging = DamagingCellIds();
                if (damaging.Count == 0) return;

                var cells = Traverse.Create(level).Field("cellTypes").GetValue();
                if (!(cells is Unity.Collections.NativeArray<byte> native) || !native.IsCreated) return;

                int w = level.Width, h = level.Height, cleared = 0, pads = 0;
                float r2 = radius * radius;
                foreach (var station in em.GetEntitiesWithComponent<Station.Data>().ToList())
                {
                    if (station?.entity == null) continue;
                    pads++;
                    var c = (Vector2)station.entity.position;
                    int minX = Mathf.Max(0, Mathf.FloorToInt(c.x - radius));
                    int maxX = Mathf.Min(w - 1, Mathf.CeilToInt(c.x + radius));
                    int minY = Mathf.Max(0, Mathf.FloorToInt(c.y - radius));
                    int maxY = Mathf.Min(h - 1, Mathf.CeilToInt(c.y + radius));
                    for (int y = minY; y <= maxY; y++)
                    {
                        float dy = y - c.y;
                        int row = y * w;
                        for (int x = minX; x <= maxX; x++)
                        {
                            float dx = x - c.x;
                            if (dx * dx + dy * dy > r2) continue;
                            if (!damaging.Contains(native[row + x])) continue;
                            level.SetCell(row + x, 0); // to empty; replicates like any terrain change
                            cleared++;
                        }
                    }
                }
                Plugin.Log.LogInfo($"[BR] cleared {cleared} hazard cells within {radius:0} units of {pads} shops");
            }
            catch (System.Exception e) { Plugin.Log.LogWarning($"[BR] shop hazard clear failed: {e.Message}"); }
        }

        /// <summary>Cell types that hurt on contact. Resolved at runtime because ids are registered
        /// per run, and read from the same <c>contactDamage</c> struct the ring material lookup used
        /// to — a float read of it silently scores every cell zero.</summary>
        private static HashSet<byte> DamagingCellIds()
        {
            var ids = new HashSet<byte>();
            try
            {
                foreach (var ct in Resources.FindObjectsOfTypeAll<CellType>())
                {
                    if (ct == null || ct.id == 0) continue;
                    float dmg = 0f;
                    try
                    {
                        var contact = Traverse.Create(ct).Field("contactDamage").GetValue();
                        if (contact != null) dmg = Traverse.Create(contact).Field("amount").GetValue<float>();
                    }
                    catch { }
                    if (dmg > 0f) ids.Add(ct.id);
                }
            }
            catch { }
            return ids;
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
                    var rnd = new System.Random(NetSession.Instance?.CurrentRunSeed ?? 12345);
                    float bestScore = -1f;
                    const int candidates = 64;
                    float drift = _mapRadius * CenterDriftFraction;
                    for (int c = 0; c < candidates; c++)
                    {
                        double ca = rnd.NextDouble() * System.Math.PI * 2.0;
                        double cr = System.Math.Sqrt(rnd.NextDouble()) * drift;
                        var cand = new Vector2(
                            _mapCenter.x + (float)(cr * System.Math.Cos(ca)),
                            _mapCenter.y + (float)(cr * System.Math.Sin(ca)));
                        float score = Openness(level, native, cand, rnd);
                        if (score <= bestScore) continue;
                        bestScore = score;
                        _center = cand;
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

            BuildRingPath(level);
        }

        /// <summary>Fraction of a probe disc around <paramref name="at"/> that is empty, real ground.
        /// Void cells score zero — they are not "open", they are off the map, and treating them as
        /// open once rated the border as the most fightable place in the world.</summary>
        private static float Openness(Level level, Unity.Collections.NativeArray<byte> cells,
            Vector2 at, System.Random rnd)
        {
            const int samples = 200, probeRadius = 60;
            if (level == null) return 0f;
            int w = level.Width, h = level.Height, open = 0;
            for (int s = 0; s < samples; s++)
            {
                double a = rnd.NextDouble() * System.Math.PI * 2.0;
                double r = System.Math.Sqrt(rnd.NextDouble()) * probeRadius;
                int x = (int)(at.x + r * System.Math.Cos(a));
                int y = (int)(at.y + r * System.Math.Sin(a));
                if (x < 0 || y < 0 || x >= w || y >= h) continue;
                if (IsVoidCell(level, x, y)) continue;
                if (cells[y * w + x] == 0) open++;
            }
            return open / (float)samples;
        }

        // ---------------------------------------------------------------- the drifting zone
        //
        // The zone no longer shrinks around one fixed point. It walks, closing on a SHOP (Omar,
        // 2026-08-05: "lets also include the off-centre drift. this makes the game more exciting.
        // just make sure its an area thats accessible, perhaps closing at one of the many shops").
        //
        // A fixed centre means the safest play is to fly to the middle early and never move again.
        // Every zone after the first is then free information you acted on twenty minutes ago, and
        // the ring stops being a decision. Drift is what turns each closure into a question — is
        // this ground still mine, and if not, which way do I cross — which is the actual engine of
        // a battle royale's mid-game.
        //
        // A shop as the final anchor is the endgame arena chosen rather than stumbled into: shops
        // are open ground by construction, every one is already unlocked and stocked (OpenAllStations)
        // and had its surrounding hazards scrubbed (ClearHazardsAroundStations), and being landmarks
        // players can name, everyone can see where the match is going to end.

        private static Vector2[] _stageCenter;   // centre the ring holds at after k closures
        private static Vector2 _finalAnchor;

        /// <summary>How far from the map's centre the final zone may be placed, as a fraction of the
        /// map radius. A shop out near the border would be a legal anchor and a miserable arena —
        /// half the approach angles are void, and the drift budget needed to reach it eats the
        /// headroom that keeps the walk from being a straight line. Inside 55% the endgame is on
        /// ground that can be reached from any direction.</summary>
        private const float AnchorMaxOffsetFraction = 0.55f;

        /// <summary>Sideways wander, as a fraction of the CURRENT radius. It shrinks with the zone
        /// on purpose: the path is unpredictable while there is room to rotate and settles into a
        /// straight run once there is not.</summary>
        private const float DriftJitter = 0.18f;

        /// <summary>Fraction of the legal move a single closure is allowed to use. Below 1 so that
        /// float error can never turn "just touching" into a new zone poking outside the old one.</summary>
        private const float ContainmentMargin = 0.97f;

        /// <summary>Plot every zone's centre, from the opening circle to the shop it closes on.
        ///
        /// The invariant this must never break: <b>each zone is entirely inside the one before it</b>.
        /// If a new circle pokes outside its predecessor then ground a player is standing on, well
        /// within the safe zone and with no warning showing, becomes lethal — the one thing a ring
        /// is not allowed to do. It holds iff the centre moves by no more than the radius given up,
        /// |C(k+1) - C(k)| &lt;= R(k) - R(k+1), which is asserted per step below.
        ///
        /// The base path is what makes that free rather than fiddly: put each centre a fraction of
        /// the way from the start to the anchor equal to the fraction of RADIUS already surrendered.
        /// Then each step moves |anchor - start| * (R(k) - R(k+1)) / R(0), which is within budget
        /// whenever the anchor is inside the opening circle — true by construction — and lands
        /// exactly on the anchor at the final closure, where the radius reaches zero. Jitter rides
        /// on top and is clamped against the same budget, so no amount of wander can break it.</summary>
        private static void BuildRingPath(Level level)
        {
            int n = Mathf.Max(1, _stages);
            _stageCenter = new Vector2[n + 1];
            _stageCenter[0] = _center;
            _finalAnchor = PickFinalAnchor(level);

            var rnd = new System.Random((NetSession.Instance?.CurrentRunSeed ?? 12345) ^ 0x5EED);
            // A slowly turning direction rather than an independent draw per stage: independent
            // draws average out into the straight base path, which is the thing the jitter exists
            // to avoid. A random walk in ANGLE gives a route that commits to a side for a while.
            double angle = rnd.NextDouble() * System.Math.PI * 2.0;
            float r0 = Mathf.Max(0.001f, _startRadius);

            for (int k = 0; k < n; k++)
            {
                float rk = RadiusAfterStage(k);
                float rNext = RadiusAfterStage(k + 1);
                // Base: as far along start -> anchor as we are along R0 -> 0.
                Vector2 baseNext = Vector2.Lerp(_stageCenter[0], _finalAnchor, 1f - rNext / r0);

                angle += (rnd.NextDouble() - 0.5) * System.Math.PI * 0.7;  // up to +-63 degrees
                var wander = new Vector2((float)System.Math.Cos(angle), (float)System.Math.Sin(angle))
                             * (rNext * DriftJitter);

                Vector2 move = baseNext + wander - _stageCenter[k];
                float budget = Mathf.Max(0f, (rk - rNext) * ContainmentMargin);
                if (move.magnitude > budget) move = move.normalized * budget;
                _stageCenter[k + 1] = _stageCenter[k] + move;
            }

            // The final zone closes to a point, so pin it to the anchor exactly rather than to
            // wherever the clamp left it — a shop the ring lands ON is the promise being made.
            _stageCenter[n] = _finalAnchor;

            float pull = Vector2.Distance(_stageCenter[0], _finalAnchor);
            Plugin.Log.LogInfo($"[BR] ring drifts to ({_finalAnchor.x:0},{_finalAnchor.y:0}) " +
                $"— {pull:0} units from the opening centre over {n} closures");
        }

        /// <summary>Where the match ends: the most open shop that is not out on the rim, or the
        /// opening centre if the world has no usable one.</summary>
        private static Vector2 PickFinalAnchor(Level level)
        {
            try
            {
                var em = ServiceLocator.Get<EntityManager>();
                var cellsRaw = Traverse.Create(level).Field("cellTypes").GetValue();
                if (em == null || level == null
                    || !(cellsRaw is Unity.Collections.NativeArray<byte> cells) || !cells.IsCreated)
                    return _center;

                float maxOffset = _mapRadius * AnchorMaxOffsetFraction;
                var rnd = new System.Random((NetSession.Instance?.CurrentRunSeed ?? 12345) ^ 0xA9C0);
                Vector2 best = _center;
                float bestScore = -1f;
                int considered = 0, rejected = 0;

                foreach (var station in em.GetEntitiesWithComponent<Station.Data>().ToList())
                {
                    if (station?.entity == null) continue;
                    var pos = (Vector2)station.entity.position;
                    if (Vector2.Distance(pos, _mapCenter) > maxOffset) { rejected++; continue; }
                    considered++;
                    float score = Openness(level, cells, pos, rnd);
                    if (score <= bestScore) continue;
                    bestScore = score;
                    best = pos;
                }

                if (considered == 0)
                {
                    Plugin.Log.LogInfo($"[BR] no shop within {maxOffset:0} units of the map centre " +
                        $"({rejected} too far out) — the ring closes on its opening centre instead");
                    return _center;
                }
                Plugin.Log.LogInfo($"[BR] final zone anchored on a shop at ({best.x:0},{best.y:0}) " +
                    $"openness={bestScore:P0} (best of {considered} central shops, {rejected} rejected as too far out)");
                return best;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"[BR] shop anchor pick failed: {e.Message} — " +
                    "the ring closes on its opening centre instead");
                return _center;
            }
        }

        /// <summary>Assert, out loud and in the match log, that every zone is contained in the one
        /// before it.
        ///
        /// The construction in <see cref="BuildRingPath"/> makes this true by algebra, and that is
        /// exactly why it is worth checking: the failure mode is silent and cruel — a player parked
        /// well inside the safe zone, no warning circle anywhere near them, suddenly burning because
        /// the new circle poked out of the old one. It would be blamed on the damage code for a week.
        /// One log line per match beats that. It should never fire; if it ever does, the drift
        /// constants moved and the clamp in BuildRingPath is the thing to look at.</summary>
        private static void VerifyContainment()
        {
            if (_stageCenter == null) return;
            for (int k = 0; k + 1 < _stageCenter.Length; k++)
            {
                float moved = Vector2.Distance(_stageCenter[k], _stageCenter[k + 1]);
                float allowed = RadiusAfterStage(k) - RadiusAfterStage(k + 1);
                if (moved <= allowed + 0.01f) continue;
                Plugin.Log.LogError($"[BR] RING PATH BUG: zone {k + 1} moves {moved:0.0} units but " +
                    $"only gives up {allowed:0.0} of radius — it is NOT contained in zone {k}. " +
                    "Ground inside the current safe zone will turn lethal with no warning.");
            }
        }

        /// <summary>Centre the ring holds at once <paramref name="stage"/> closures are done.</summary>
        private static Vector2 CenterAfterStage(int stage)
        {
            if (_stageCenter == null || _stageCenter.Length == 0) return _center;
            return _stageCenter[Mathf.Clamp(stage, 0, _stageCenter.Length - 1)];
        }

        /// <summary>How far the ring's centre may sit from the map's, as a fraction of the map
        /// radius. Every unit of offset adds a unit of radius the ring must close through before it
        /// touches the world, so this buys variety at a directly measurable cost in pacing — and in
        /// LEGIBILITY: at 0.15 the starting circle sat up to 150 units past the world border, which
        /// on the map screen read as a ring that "is closing off map" and in the world as no lava
        /// anywhere (Omar, 2026-07-29: "I don't see the ring closing at all"). At 0.05 the wall
        /// begins within ~50 units of the disc edge — hugging the border a player can fly to and
        /// SEE — while the openness scoring still nudges the endgame toward fightable ground.</summary>
        private const float CenterDriftFraction = 0.05f;

        // The ring alternates WAIT and CLOSE, the way a battle royale is supposed to feel: the zone
        // sits still long enough to fight over, then draws in to the next ring. What changed
        // (Omar, 2026-08-05: "the closing ring is too slow... I want to feel like I'm rushing to
        // the centre") is that the wait and the closure are no longer the SAME every stage.
        //
        // A constant hold makes the last zone feel exactly like the first, which is the one thing a
        // battle royale must not do — the whole shape of the genre is a match that tightens. The
        // schedule now follows the Fortnite blueprint:
        //
        //   early zones  long safety window, long unhurried closure  (loot, rotate, set up)
        //   mid zones    the window collapses; closures stay ~a minute (pick a lane and commit)
        //   late zones   almost no window at all — the wall is nearly always moving
        //
        // Both curves are evaluated per stage rather than typed out as a table, so the shape holds
        // for ANY stage count (short test matches included) instead of being a magic 12-row list
        // that silently stops meaning anything the moment BrRingStages changes.
        private static float[] _stageWait;   // seconds the zone holds still before closure i
        private static float[] _stageClose;  // seconds closure i takes
        private static float[] _stageBegin;  // elapsed time at which stage i's WAIT starts

        // Unnormalised shape, in "reference seconds". Only the RATIOS between these matter — the
        // whole schedule is then scaled to fit BrMatchMinutes exactly, so changing the total match
        // length stretches or compresses the pacing without flattening it.
        private const float WaitAtStart = 130f;   // reference safety window for zone 1
        private const float WaitFalloff = 2.5f;   // >1 collapses the window fast; this reaches ~0 by zone 9 of 12
        private const float CloseAtStart = 90f;   // reference closure time for zone 1
        private const float CloseAtEnd = 35f;     // ...and for the final zone

        /// <summary>Build the per-stage wait/close ladder and scale it to the match length.
        ///
        /// Total match length is the INPUT again (BrMatchMinutes), because that is the thing a host
        /// actually wants to set — "a 20 minute match" — and under a variable schedule there is no
        /// single "hold" left to configure in its place. The per-stage seconds are derived and
        /// logged, so the pacing is still readable, it is just no longer typed in by hand.</summary>
        private static void ComputeSchedule()
        {
            int n = Mathf.Max(1, _stages);
            _stageWait = new float[n];
            _stageClose = new float[n];
            _stageBegin = new float[n];

            float raw = 0f;
            for (int i = 0; i < n; i++)
            {
                // p walks 0 -> 1 across the match. One stage is a degenerate curve, so pin it to
                // the start of the shape rather than dividing by zero.
                float p = n > 1 ? i / (float)(n - 1) : 0f;
                _stageWait[i] = WaitAtStart * Mathf.Pow(1f - p, WaitFalloff);
                _stageClose[i] = Mathf.Lerp(CloseAtStart, CloseAtEnd, p);
                raw += _stageWait[i] + _stageClose[i];
            }

            // Scale the whole shape so the final closure lands exactly on the configured length.
            float target = Mathf.Max(60f, NetConfig.BrMatchMinutes.Value * 60f);
            float scale = raw > 0.001f ? target / raw : 1f;
            float at = 0f;
            for (int i = 0; i < n; i++)
            {
                _stageWait[i] = Mathf.Max(0f, _stageWait[i] * scale);
                // A closure still has to be long enough to read as the ground MOVING rather than
                // teleporting, however short the match is configured to be.
                _stageClose[i] = Mathf.Max(3f, _stageClose[i] * scale);
                _stageBegin[i] = at;
                at += _stageWait[i] + _stageClose[i];
            }
            // Derived, and exact: the last closure lands on the configured length.
            _matchSeconds = at;
        }

        // The ring shrinks on a curve, not in equal steps and no longer by halving.
        //
        // Halving was right for six closures and wrong for twelve — it reaches a pinpoint zone by
        // stage 5 and leaves the rest of the match with nothing left to take. This exponent form
        // trims gently while the zone still encloses most of the world (nobody has flown anywhere
        // yet) and then accelerates: the last few closures take a large FRACTION of what remains,
        // which is what makes the endgame feel like a collapse rather than a creep.
        private const float ShrinkCurve = 1.6f;

        /// <summary>Radius the ring holds at once <paramref name="stage"/> closures are done.</summary>
        private static float RadiusAfterStage(int stage)
        {
            int stages = Mathf.Max(1, _stages);
            if (stage <= 0) return _startRadius;
            if (stage >= stages) return 0f;               // the last one always closes fully
            return Mathf.Max(0f, _startRadius * Mathf.Pow(1f - stage / (float)stages, ShrinkCurve));
        }

        /// <summary>Where the ring is right now, and where it is heading. The centre travels on the
        /// same clock as the radius — a closure moves the whole circle, so the two interpolate
        /// together or the boundary would sweep over ground it never actually crossed.</summary>
        private static void RingAt(float elapsed, out float radius, out Vector2 center, out int stage,
            out bool closing, out float nextTarget, out Vector2 nextCenter, out float phaseRemaining)
        {
            int n = _stageWait != null ? _stageWait.Length : 0;
            if (n == 0)   // asked before ComputeSchedule ran; the ring has not moved yet
            {
                radius = _startRadius;
                center = _center;
                stage = 0;
                closing = false;
                nextTarget = _startRadius;
                nextCenter = _center;
                phaseRemaining = 0f;
                return;
            }

            // A dozen stages at most, so a scan is cheaper than the arithmetic that would replace
            // it — and unlike the old uniform division it stays correct when the spans differ.
            for (int i = 0; i < n; i++)
            {
                float waitEnd = _stageBegin[i] + _stageWait[i];
                float closeEnd = waitEnd + _stageClose[i];
                if (elapsed >= closeEnd) continue;

                float from = RadiusAfterStage(i);
                float to = RadiusAfterStage(i + 1);
                Vector2 fromC = CenterAfterStage(i);
                nextTarget = to;
                nextCenter = CenterAfterStage(i + 1);
                if (elapsed < waitEnd)
                {
                    radius = from;
                    center = fromC;
                    stage = i;
                    closing = false;
                    phaseRemaining = waitEnd - elapsed;
                }
                else
                {
                    float f = Mathf.Clamp01((elapsed - waitEnd) / Mathf.Max(0.001f, _stageClose[i]));
                    radius = Mathf.Lerp(from, to, f);
                    center = Vector2.Lerp(fromC, nextCenter, f);
                    stage = i + 1;
                    closing = true;
                    phaseRemaining = _stageClose[i] * (1f - f);
                }
                return;
            }

            radius = 0f;
            center = CenterAfterStage(_stages);
            stage = _stages;
            closing = false;
            nextTarget = 0f;
            nextCenter = center;
            phaseRemaining = 0f;
        }

        private static float RadiusAt(float elapsed)
        {
            RingAt(elapsed, out float r, out _, out _, out _, out _, out _, out _);
            return r;
        }

        private static int StageAt(float elapsed)
        {
            RingAt(elapsed, out _, out _, out int stage, out _, out _, out _, out _);
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
            RingAt(elapsed, out float radius, out Vector2 center, out int stage, out bool closing,
                out float nextTarget, out Vector2 nextCenter, out float phaseRemaining);
            _center = center;   // the live centre; the zone walks as well as shrinks

            // Announce when a closure STARTS, so the warning means "move now" rather than marking
            // an invisible boundary. The zone it is closing to is on the map from this moment.
            if (closing && stage != _lastAnnouncedStage)
            {
                _lastAnnouncedStage = stage;
                if (stage >= _stages) Announce(session, "FINAL RING — NOWHERE LEFT TO RUN", 8f);
                else Announce(session, $"THE LAVA RING IS CLOSING ({stage}/{_stages}) — CHECK YOUR MAP", 7f);
                BroadcastRing(session);
            }

            TickStationUnlock(session, elapsed);

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
                // swallowed by lava before anyone can reach it. A WAVE is several packages
                // scattered independently (Omar, 2026-07-29: "more air drops throughout the
                // world") with one announcement — five toasts for five crates is noise.
                // HALF the players, minimum one, unless the config names a fixed count. Scarcity is
                // the mechanism: a crate per player is a distribution, half a crate per player is a
                // reason to fight over one.
                int configured = NetConfig.BrCarePackageCount.Value;
                int wave = configured > 0
                    ? Mathf.Clamp(configured, 1, 8)
                    : Mathf.Clamp(Mathf.Max(1, MatchPlayers.Count / 2), 1, 8);
                int dropped = 0;
                for (int i = 0; i < wave; i++)
                    // Scattered around the NEXT zone's centre, not this one's. With a drifting ring
                    // those are different places, and a crate placed on the old centre can be
                    // outside the very closure it was meant to survive.
                    if (DropCarePackage(session, nextCenter, Mathf.Max(20f, nextTarget), i)) dropped++;
                if (dropped > 0)
                    Announce(session, dropped == 1
                        ? "SUPPLY DROP INBOUND — CHECK YOUR MAP. DESTROY IT TO CLAIM IT"
                        : $"{dropped} SUPPLY DROPS INBOUND — CHECK YOUR MAP. DESTROY THEM TO CLAIM THEM", 7f);
            }

            CheckLastAlive(session);
        }

        private static void BroadcastRing(NetSession session)
        {
            float elapsed = Time.unscaledTime - _matchStart;
            RingAt(elapsed, out float radius, out Vector2 center, out int stage, out bool closing,
                out float nextTarget, out Vector2 nextCenter, out float phaseRemaining);
            var msg = new RingStateMsg
            {
                CenterX = center.x,
                CenterY = center.y,
                SafeRadius = radius,
                TargetRadius = nextTarget,   // what the map draws players a path toward
                TargetCenterX = nextCenter.x,
                TargetCenterY = nextCenter.y,
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

        /// <summary>One package. Returns whether it actually spawned; the caller owns the wave
        /// announcement. <paramref name="waveIndex"/> salts the seed — two drops in the same wave
        /// used to derive the same Random from the same millisecond and land on the same cell.</summary>
        private static bool DropCarePackage(NetSession session, Vector2 around, float radius,
            int waveIndex = 0)
        {
            try
            {
                var level = LevelRef;
                var egm = ServiceLocator.Get<EntityGameObjectManager>();
                if (level == null || egm == null || egm.savablesCollection == null) return false;
                var cells = Traverse.Create(level).Field("cellTypes").GetValue();
                if (!(cells is Unity.Collections.NativeArray<byte> native) || !native.IsCreated) return false;

                // Somewhere open, inside the safe zone, so it is contestable rather than lethal.
                //
                // Placed in a BAND near the edge of the NEXT ring rather than spread evenly across
                // the zone (Omar, 2026-07-29: "the crates should be spawned closer to the next
                // ring"). Uniform-over-the-disc sampling put most crates near the middle, which is
                // where players end up anyway — so the crate added no reason to move and no reason
                // to meet anyone. Out at the next ring's edge it is a decision: go now, while the
                // ground there is still safe, or leave it. `radius` is already the NEXT target
                // radius, so 55-92% of it is comfortably inside the ground players will still have —
                // and `around` is the next zone's CENTRE, which the drift has moved away from the
                // current one.
                var rnd = new System.Random((int)(Time.unscaledTime * 1000f) + waveIndex * 7919);
                int w = level.Width, h = level.Height;
                Vector2 spot = around;
                for (int attempt = 0; attempt < 200; attempt++)
                {
                    double a = rnd.NextDouble() * System.Math.PI * 2.0;
                    double r = Mathf.Max(18f, (float)(radius * (0.55 + rnd.NextDouble() * 0.37)));
                    int x = (int)(around.x + r * System.Math.Cos(a));
                    int y = (int)(around.y + r * System.Math.Sin(a));
                    if (x < 1 || y < 1 || x >= w - 1 || y >= h - 1) continue;
                    if (native[y * w + x] != 0) continue;
                    spot = new Vector2(x, y);
                    break;
                }

                if (!TryPickCarePackagePrefab(egm, out var prefab, out string prefabId))
                { Plugin.Log.LogWarning("[BR] no care-package prefab available"); return false; }
                var spawned = egm.CreateEntity(prefab, spot); // replicates via runtime-spawn capture
                if (spawned == null) return false;
                int netId = 0;
                var se = spawned.GetComponent<SavableEntity>();
                if (se != null && se.EntityData != null) NetIds.TryGetNetId(se.EntityData.instanceId, out netId);
                CarePackages[netId] = spot;
                Plugin.Log.LogInfo($"[BR] care package '{prefabId}' #{netId} at ({spot.x:0},{spot.y:0})");

                var w2 = new NetWriter(32);
                new CarePackageMsg { NetId = netId, X = spot.x, Y = spot.y }.Write(w2);
                session.SendToAll(Transport.NetChannel.Control, w2.ToSegment(), reliable: true);
                return true;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"[BR] care package drop failed: {e.Message}");
                return false;
            }
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
