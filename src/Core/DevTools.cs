using System;
using System.Collections.Generic;
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

        /// <summary>Dev shield for sweep tests: the local ship's damage is BLOCKED at the
        /// routing chokepoints (DamageSync), so every incoming hit still logs its
        /// [CombatHit] audit line with source attribution (applied=False) — the test proves
        /// enemy damage reaches the player pipeline without ever losing the test ship.</summary>
        internal static bool GodMode { get; private set; }

        internal static void Reset()
        {
            GodMode = false;
        }

        /// <summary>Runs at the poll cadence while god is armed: re-assert infinite weapon
        /// resource (respawns rebuild the ship) and refill every non-shared tank — fire tests
        /// never run dry mid-burst, fuel-type drains included.</summary>
        private static void TickGod()
        {
            if (!GodMode) return;
            try
            {
                var ship = ShipSync.LocalShip;
                var unit = ship != null ? ship.GetComponent<Unit>() : null;
                if (unit == null) return;
                if (!unit.HasInfiniteResource) unit.HasInfiniteResource = true;
                unit.ComponentData?.RefillResources();
            }
            catch { }
        }

        // fire <seconds>: hold the local ship's trigger via the game's own Shooter API
        // (SetShooting — what every AI ShootAction uses); weapons without a Shooter get the
        // IsTriggerPulled+Warmup fallback. Driven every frame, independent of the poll gate.
        private static float _fireUntil;
        private static Shooter _fireShooter;
        private static WeaponBase _fireWeapon;
        private static int _fireTargetNetId;   // aim: track this entity while firing
        private static Vector2 _fireDir;       // aim: fixed direction (zero = don't steer)
        private static BarrelTransform[] _fireBarrels;

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
                    _fireTargetNetId = 0; _fireDir = Vector2.zero; _fireBarrels = null;
                    Out("fire: stopped");
                    return;
                }
                if (_fireShooter != null) _fireShooter.SetShooting(true);
                else if (_fireWeapon != null)
                {
                    _fireWeapon.IsTriggerPulled = true;
                    _fireWeapon.Warmup(Time.deltaTime);
                }
                // Steer the barrels the same way puppet aim mirroring does: BarrelTransform.
                // Direction is the game's single source of truth for aim, and in a harness run
                // the window is unfocused so the crosshair isn't fighting us for it.
                Vector2 dir = Vector2.zero;
                var ship = ShipSync.LocalShip;
                if (_fireTargetNetId != 0 && ship != null
                    && NetIds.TryGetInstanceId(_fireTargetNetId, out int inst))
                {
                    var egm = ServiceLocator.Get<EntityGameObjectManager>();
                    if (egm != null && egm.TryGetSavableEntity(inst, out var target) && target != null)
                        dir = ((Vector2)target.transform.position - (Vector2)ship.transform.position).normalized;
                }
                else if (_fireDir != Vector2.zero) dir = _fireDir;
                if (dir != Vector2.zero && _fireBarrels != null)
                    foreach (var b in _fireBarrels)
                        if (b != null) b.Direction = dir;
            }
            catch { _fireShooter = null; _fireWeapon = null; _fireUntil = 0f; _fireBarrels = null; }
        }

        public static void Tick(NetSession session)
        {
            TickFire();
            string file = NetConfig.CommandFile != null ? NetConfig.CommandFile.Value : "";
            if (string.IsNullOrEmpty(file)) return;
            float now = Time.unscaledTime;
            if (now < _nextPollAt) return;
            _nextPollAt = now + 0.5f;
            TickGod();

            string path;
            try { path = Path.IsPathRooted(file) ? file : Path.Combine(ModFolder.Dir, file); }
            catch { return; }
            string[] lines;
            try
            {
                // Consume by RENAME, not read-then-truncate: a command written between the read
                // and the truncate was silently erased (observed live — dropped tp commands mid-
                // scenario). Move is atomic: we either take the whole file or fail and retry;
                // anything written after the move lands in a fresh file for the next poll.
                string consuming = path + ".consuming";
                if (!File.Exists(consuming)) // crash leftover from a previous poll gets drained first
                {
                    if (!File.Exists(path)) return;
                    File.Move(path, consuming);
                }
                lines = File.ReadAllLines(consuming);
                File.Delete(consuming);
                if (lines.Length == 0) return;
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

        /// <summary>Owner-side motion spectrum of one entity (see the motionprofile devcmd): per
        /// physics step, count velocity direction reversals (dot &lt; 0 with both steps moving) and
        /// track speed + the in-place oscillation amplitude around a rolling mean position.</summary>
        private static System.Collections.IEnumerator MotionProfile(int netId, Rigidbody2D rb, float secs)
        {
            int steps = 0, reversals = 0;
            float speedSum = 0f, speedMax = 0f, amp = 0f;
            Vector2 prevVel = Vector2.zero, meanPos = rb.position;
            // Wasted speed with the SAME 0.5s windowing the puppet uses (path - net displacement),
            // so owner and puppet numbers are directly comparable: puppet ≈ owner means the wobble
            // is REAL motion faithfully reproduced; puppet >> owner means the sync manufactures it.
            float winStart = Time.unscaledTime, winPath = 0f, wastedSum = 0f, wastedMax = 0f;
            int wins = 0;
            Vector2 winStartPos = rb.position, lastPos = rb.position;
            float t0 = Time.unscaledTime;
            while (Time.unscaledTime - t0 < secs)
            {
                yield return new WaitForFixedUpdate();
                if (rb == null) { Out($"motionprofile #{netId}: entity died mid-sample"); yield break; }
                var vel = rb.linearVelocity;
                steps++;
                float speed = vel.magnitude;
                speedSum += speed; speedMax = Mathf.Max(speedMax, speed);
                if (speed > 0.5f && prevVel.magnitude > 0.5f && Vector2.Dot(vel, prevVel) < 0f) reversals++;
                prevVel = vel;
                meanPos = Vector2.Lerp(meanPos, rb.position, 0.05f);       // ~0.4s rolling mean
                amp = Mathf.Max(amp, Vector2.Distance(rb.position, meanPos));
                winPath += Vector2.Distance(rb.position, lastPos);
                lastPos = rb.position;
                float now = Time.unscaledTime;
                if (now - winStart >= 0.5f)
                {
                    float wasted = (winPath - Vector2.Distance(rb.position, winStartPos)) / (now - winStart);
                    wastedSum += wasted; wastedMax = Mathf.Max(wastedMax, wasted); wins++;
                    winStart = now; winStartPos = rb.position; winPath = 0f;
                }
            }
            float dur = Time.unscaledTime - t0;
            float revHz = reversals / Mathf.Max(0.001f, dur);
            float snapHz = Mathf.Max(NetConfig.StateHz.Value, NetConfig.CombatStateHz.Value);
            Out(string.Format(CultureInfo.InvariantCulture,
                "motionprofile #{0}: {1:0.0}s {2} steps | speed avg={3:0.0} max={4:0.0} u/s | " +
                "direction reversals={5} ({6:0.0}/s) | oscillation amp={7:0.00}u | " +
                "OWNER wasted avg={8:0.00} max={9:0.0} u/s | snapshot={10:0}Hz (Nyquist {11:0}Hz) -> {12}",
                netId, dur, steps, speedSum / Mathf.Max(1, steps), speedMax, reversals, revHz, amp,
                wins > 0 ? wastedSum / wins : 0f, wastedMax,
                snapHz, snapHz / 2f,
                revHz > snapHz / 2f ? "UNDER-SAMPLED: motion exceeds what snapshots can carry"
                                    : "sampling adequate; compare OWNER wasted vs puppet jitterstats"));
        }

        private static void Execute(NetSession session, string line)
        {
            var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            switch (parts[0].ToLowerInvariant())
            {
                case "say":
                    Out($"say: {line.Substring(3).Trim()}");
                    return;
                case "uploadlogs":
                    // Tester diagnostics pipeline: gzip + PUT this machine's BepInEx log to the
                    // write-only S3 prefix, grouped under the shared run id (see LogUpload).
                    Out($"uploadlogs: run id {LogUpload.RunId} — starting");
                    LogUpload.Upload(session, Out);
                    return;
                case "runid":
                    Out($"runid: {LogUpload.RunId}");
                    return;
                case "wallet":
                {
                    // Loot-sync assertion surface: shared-currency tank values (gold etc.) + this
                    // player's per-player Vault totals. Lets a two-instance test measure WHO
                    // actually receives loot from a kill (the "non-host pickups don't sync" claim).
                    var rd = ServiceLocator.Get<RunData>();
                    if (rd == null) { Out("wallet: no RunData"); return; }
                    var sb = new System.Text.StringBuilder("wallet:");
                    try
                    {
                        foreach (var tank in rd.SharedResourceTanks)
                        {
                            if (tank == null || tank.resource == null) continue;
                            string id = null; try { id = tank.resource.Id; } catch { }
                            sb.Append($" {id ?? tank.resource.name}={tank.Value:0}");
                        }
                    }
                    catch (Exception e) { sb.Append($" (tanks err {e.Message})"); }
                    try
                    {
                        var vault = ServiceLocator.Get<Vault>();
                        int ing = 0;
                        if (vault != null) foreach (var kv in vault.Ingredients) ing += kv.Value;
                        sb.Append($" | vaultIngredients={ing} vaultModules={vault?.ModuleCount ?? 0}");
                    }
                    catch (Exception e) { sb.Append($" (vault err {e.Message})"); }
                    Out(sb.ToString());
                    return;
                }
                case "shopstate":
                {
                    // Assertion surface for shop-upgrade parity: UnlockedStationCount is the
                    // per-player shop LEVEL (RunData.unlockedShopCount, bumped by
                    // RegisterShopUnlock) and the item count is the stock it has rolled.
                    // ("shop" is taken — that one fakes the shop-open damage shield.)
                    var rd = ServiceLocator.Get<RunData>();
                    if (rd == null) { Out("shopstate: no RunData"); return; }
                    int items = -1;
                    try { items = rd.GeneralShopItemList?.Items?.Count ?? -1; } catch { }
                    Out($"shopstate: unlockedShopCount={rd.UnlockedStationCount} items={items}");
                    return;
                }
                case "jitterstats":
                    // Per-enemy-type sync-smoothness table (wastedAvg/peak/jitter%), accumulated by
                    // every puppet since the last dump. `jitterstats keep` dumps without resetting.
                    DiagWatch.DumpTypeStats(Out, reset: parts.Length < 2 || !parts[1].Equals("keep", StringComparison.OrdinalIgnoreCase));
                    return;
                case "motionprofile":
                {
                    // OWNER-side ground truth for the jitter hypothesis: sample an entity's rigidbody
                    // every FixedUpdate for N seconds and report its direction-reversal rate vs the
                    // snapshot rate. Reversals/sec above ~half the snapshot Hz cannot be represented
                    // by sampling (Nyquist) — proof that the type moves too fast for snapshots.
                    if (parts.Length < 2 || !int.TryParse(parts[1], out int mpId)) { Out("motionprofile <netId> [secs]"); return; }
                    float mpSecs = 5f;
                    if (parts.Length >= 3) float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out mpSecs);
                    if (!NetIds.TryGetInstanceId(mpId, out int mpInst)) { Out($"motionprofile: netId {mpId} unknown here"); return; }
                    var mpEgm = ServiceLocator.Get<EntityGameObjectManager>();
                    if (mpEgm == null || !mpEgm.TryGetSavableEntity(mpInst, out var mpSe) || mpSe == null)
                    { Out($"motionprofile: #{mpId} not instantiated here"); return; }
                    var mpRb = mpSe.GetComponent<Rigidbody2D>();
                    if (mpRb == null) { Out($"motionprofile: #{mpId} has no rigidbody"); return; }
                    Out($"motionprofile: sampling #{mpId} for {mpSecs:0.0}s...");
                    session.StartCoroutine(MotionProfile(mpId, mpRb, Mathf.Clamp(mpSecs, 1f, 20f)));
                    return;
                }
                case "unlockstation":
                {
                    // Harness aid for the FULL-rejoin path (which needs a station checkpoint):
                    // unlock a station through the REAL purchase path (Station.Data.Install), so
                    // ProgressionSync captures + broadcasts it, LatestStationNetId becomes the
                    // party's respawn checkpoint, and the vanilla unlock cascade (respawn, lights,
                    // map icon) runs everywhere — identical to a player buying the first upgrade.
                    // `unlockstation` = nearest station to the local ship; `unlockstation <netId>`.
                    var em = ServiceLocator.Get<EntityManager>();
                    var ship = ShipSync.LocalShip;
                    if (em == null || ship == null) { Out("unlockstation: no entity manager / local ship"); return; }
                    int wantNetId = 0;
                    if (parts.Length >= 2) int.TryParse(parts[1], out wantNetId);
                    Vector2 origin = ship.transform.position;
                    // Nearest-first, but skip stations with nothing left to install (the spawn
                    // station ships fully unlocked) — the point is to CREATE a new checkpoint.
                    var candidates = new List<(float dist, int netId, Station.Data data)>();
                    foreach (var data in em.GetEntitiesWithComponent<Station.Data>())
                    {
                        var entity = data?.entity;
                        if (entity == null || !NetIds.TryGetNetId(entity.instanceId, out int netId)) continue;
                        if (wantNetId != 0 && netId != wantNetId) continue;
                        candidates.Add((Vector2.Distance(origin, entity.position), netId, data));
                    }
                    candidates.Sort((a, b) => a.dist.CompareTo(b.dist));
                    foreach (var (dist, netId, data) in candidates)
                    {
                        StationUpgrade install = null;
                        foreach (var u in data.allUpgrades)
                        {
                            if (u == null || string.IsNullOrEmpty(u.id) || data.installedUpgrades.Contains(u)) continue;
                            install = u;
                            break;
                        }
                        if (install == null) continue;
                        // Mirror vanilla Station.OnUseActivated, which does TWO things on an unlock:
                        // Install (replicates via ProgressionSync) AND RegisterShopUnlock (grows the
                        // LOCAL shop). Calling only Install made this devcmd unfaithful — the
                        // unlocker's own shop never grew, which looked like a sync bug in reverse.
                        bool wasLocked = data.installedUpgrades.Count == 0;
                        data.Install(install); // -> ProgressionSync.CaptureUpgrade -> broadcast + checkpoint
                        if (wasLocked)
                            try { ServiceLocator.Get<RunData>()?.RegisterShopUnlock(); } catch { }
                        Out($"unlockstation: installed '{install.id}' on station netId {netId} " +
                            $"dist={dist:0.0} checkpoint={Sync.ProgressionSync.LatestStationNetId}" +
                            (wasLocked ? " (unlock: local shop grown)" : " (already unlocked)"));
                        return;
                    }
                    Out($"unlockstation: no station with uninstalled upgrades ({candidates.Count} stations seen)");
                    return;
                }
                case "god":
                {
                    GodMode = parts.Length < 2 || !parts[1].Equals("off", StringComparison.OrdinalIgnoreCase);
                    try
                    {
                        // Infinite weapon resource rides the game's own flag (Shooter checks it
                        // before every cost gate); TickGod keeps tanks topped for fuel-type
                        // drains and re-arms across respawns.
                        var unit = ShipSync.LocalShip != null ? ShipSync.LocalShip.GetComponent<Unit>() : null;
                        if (unit != null) unit.HasInfiniteResource = GodMode;
                    }
                    catch { }
                    Out($"god {(GodMode ? "ON" : "OFF")} — local ship damage " +
                        (GodMode ? "blocked at the routing chokepoints (hits still audit as [CombatHit] applied=False), weapon resource infinite"
                                 : "back to normal"));
                    return;
                }
                case "roster":
                {
                    // Every spawnable entity with the classification the sweep scenario needs:
                    // what to spawn, and which assertions apply (fire audit only for shooters,
                    // loot lines only for droppers, kill sync for anything damageable).
                    var egm = ServiceLocator.Get<EntityGameObjectManager>();
                    var dict = Traverse.Create(egm).Field("entityPrefabDictionary").GetValue()
                        as System.Collections.Generic.Dictionary<string, SavableEntity>;
                    if (dict == null) { Out("roster: prefab dictionary unavailable"); return; }
                    string filter = parts.Length >= 2 ? parts[1].ToLowerInvariant() : null;
                    int listed = 0;
                    foreach (var kv in System.Linq.Enumerable.OrderBy(dict, item => item.Key))
                    {
                        var prefab = kv.Value;
                        if (prefab == null) continue;
                        bool unit = prefab.GetComponent<Unit>() != null;
                        bool body = prefab.GetComponent<Rigidbody2D>() != null;
                        bool damageable = prefab.GetComponentInChildren<DamagableResource>(true) != null
                                          || prefab.GetComponentInChildren<Health>(true) != null;
                        bool shooter = prefab.GetComponentInChildren<Shooter>(true) != null;
                        bool loot = prefab.GetComponentInChildren<LootDropper>(true) != null;
                        if (filter == "unit" && !unit) continue;
                        if (filter == "damageable" && !damageable) continue;
                        listed++;
                        Out($"roster {kv.Key} unit={unit} body={body} damageable={damageable} " +
                            $"shooter={shooter} loot={loot}");
                    }
                    Out($"roster: {listed} entries");
                    return;
                }
                case "status":
                {
                    var ship = ShipSync.LocalShip;
                    string pos = ship != null
                        ? $"{ship.transform.position.x:0.0},{ship.transform.position.y:0.0}" : "none";
                    string dead = ship != null && ship.IsDead ? " DEAD" : "";
                    Out($"status v{PluginVersionInfo.Version} state={session.State} slot={session.LocalSlot} " +
                        $"host={session.IsHost} ship={pos}{dead} " +
                        $"shipFireReplays={ProjectileSync.ShipFireQueued + ProjectileSync.ShipFireLate} " +
                        $"phantomHits={ProjectileSync.PhantomHitCount}");
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
                        // Root first, then children — Unit_Hiver-class entities keep health on
                        // a sub-part and read hp=-1.00 with the root-only lookup.
                        var dr = unit.GetComponent<DamagableResource>();
                        if (dr == null) dr = unit.GetComponentInChildren<DamagableResource>(true);
                        float hp = -1f, maxHp = -1f;
                        try { if (dr != null && dr.MaxHealth > 0) { hp = dr.CurrentHealth / dr.MaxHealth; maxHp = dr.MaxHealth; } } catch { }
                        byte fire = UnitStatus.ReadFireState(unit);
                        Out($"entity #{netId} {type} pos={pos.x:0.0},{pos.y:0.0} dist={dist:0.0} " +
                            $"owner={(owner == 255 ? "dormant" : "P" + (owner + 1))}{(puppet ? " puppet" : "")} " +
                            $"hp={hp:0.00} maxHp={maxHp:0} fire={fire}");
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
                    // Optional `sec` after the duration drives the SECONDARY holder's shooter.
                    int argAt = 2;
                    bool fireSec = parts.Length >= 3 && parts[2].Equals("sec", StringComparison.OrdinalIgnoreCase);
                    if (fireSec) argAt = 3;
                    _fireTargetNetId = 0; _fireDir = Vector2.zero;
                    if (parts.Length >= argAt + 2 && parts[argAt].Equals("at", StringComparison.OrdinalIgnoreCase))
                        int.TryParse(parts[argAt + 1], out _fireTargetNetId);
                    else if (parts.Length >= argAt + 3 && parts[argAt].Equals("dir", StringComparison.OrdinalIgnoreCase)
                        && float.TryParse(parts[argAt + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float dx)
                        && float.TryParse(parts[argAt + 2], NumberStyles.Float, CultureInfo.InvariantCulture, out float dy))
                        _fireDir = new Vector2(dx, dy).normalized;
                    _fireShooter = FindShooter(ship, fireSec);
                    _fireWeapon = _fireShooter == null ? (fireSec ? ship.SecondaryWeapon : ship.PrimaryWeapon) : null;
                    if (_fireShooter == null && _fireWeapon == null) { Out("fire: no shooter/weapon on ship"); return; }
                    _fireBarrels = ship.GetComponentsInChildren<BarrelTransform>(true);
                    _fireUntil = Time.unscaledTime + fireSecs;
                    Out($"fire: {fireSecs:0.0}s{(fireSec ? " SECONDARY" : "")} via {(_fireShooter != null ? "Shooter" : "weapon trigger")}" +
                        (_fireTargetNetId != 0 ? $" at #{_fireTargetNetId}" : _fireDir != Vector2.zero ? $" dir {_fireDir.x:0.00},{_fireDir.y:0.00}" : ""));
                    return;
                }
                case "owner":
                {
                    var ship = ShipSync.LocalShip;
                    Vector2 origin = ship != null ? (Vector2)ship.transform.position : Vector2.zero;
                    TryParsePos(parts, 1, origin, out var pos); // no args = ship position
                    var key = AuthorityManager.SegmentOf(pos);
                    byte o = AuthorityManager.OwnerOf(key);
                    Out($"owner ({key.X},{key.Y}) = {(o == AuthorityManager.DormantOwner ? "dormant" : "P" + (o + 1))}");
                    return;
                }
                case "probe":
                {
                    // The enemy's own senses, straight from its AIAgent/Vision — answers "can
                    // this enemy see anything / who is it hunting" in one line, no firing needed.
                    if (parts.Length < 2 || !int.TryParse(parts[1], out int netId))
                    { Out("probe: usage probe <netId>"); return; }
                    if (!NetIds.TryGetInstanceId(netId, out int instanceId))
                    { Out($"probe: netId {netId} unknown"); return; }
                    var egm = ServiceLocator.Get<EntityGameObjectManager>();
                    if (egm == null || !egm.TryGetSavableEntity(instanceId, out var se) || se == null)
                    { Out($"probe: #{netId} has no live object here"); return; }
                    var ai = se.GetComponentInChildren<AIAgent>(true);
                    var vision = se.GetComponentInChildren<Vision>(true);
                    var shooter = se.GetComponentInChildren<Shooter>(true);
                    var puppet = se.GetComponent<RemoteEntityPuppet>();
                    string type = se.EntityData != null ? se.EntityData.entityId : se.name;
                    if (ai == null || vision == null)
                    { Out($"probe #{netId} {type}: no AIAgent/Vision{(puppet != null ? " (puppet)" : "")}"); return; }
                    string target = "none";
                    try
                    {
                        if (ai.HasTarget && ai.Target != null)
                        {
                            var tShip = ai.Target.GetComponent<Ship>();
                            var tPup = ai.Target.GetComponent<RemotePuppet>();
                            target = tShip != null
                                ? (tPup != null ? $"shipP{tPup.Slot + 1}(puppet)" : "ship(local)")
                                : ai.Target.name;
                            target += ai.IsTargetVisible ? "/visible" : "/lost";
                        }
                    }
                    catch { target = "err"; }
                    Out($"probe #{netId} {type}{(puppet != null ? " PUPPET" : "")} aiOn={ai.enabled} " +
                        $"visionOn={vision.enabled} range={vision.Range:0} seen={vision.VisibleUnits.Count} " +
                        $"enemies={ai.VisibleEnemyCount} friends={ai.VisibleFriendCount} " +
                        $"target={target} shooter={(shooter != null ? (shooter.enabled ? "on" : "off") : "none")}");
                    // Deep diagnostics: force a scan NOW, dump the mask, and manually overlap so
                    // "scan never ran" / "mask excludes ships" / "no collider found" separate.
                    try
                    {
                        int freshCount = vision.Scan().Count;
                        var maskField = AccessTools.Field(typeof(ComponentScanner<Unit>), "targetLayers");
                        var delayField = AccessTools.Field(typeof(Vision), "refreshDelay");
                        int mask = maskField != null ? ((LayerMask)maskField.GetValue(vision)).value : -1;
                        float delay = delayField != null ? (float)delayField.GetValue(vision) : -1f;
                        var myShip = ShipSync.LocalShip;
                        string shipInfo = "no-ship";
                        int rawHits = -1, rawUnitHits = -1;
                        if (myShip != null)
                        {
                            var shipCols = myShip.GetComponentsInChildren<Collider2D>(false);
                            shipInfo = $"shipLayer={myShip.gameObject.layer} shipCols={shipCols.Length} " +
                                $"colLayers={string.Join("/", System.Linq.Enumerable.Distinct(System.Linq.Enumerable.Select(shipCols, c => c.gameObject.layer)))} " +
                                $"dist={Vector2.Distance(vision.transform.position, myShip.transform.position):0.0}";
                            var hits = Physics2D.OverlapCircleAll(vision.transform.position, vision.Range, mask);
                            rawHits = hits.Length;
                            rawUnitHits = System.Linq.Enumerable.Count(hits, h => h != null && h.GetComponent<Unit>() != null);
                        }
                        Out($"probe2 #{netId} scanNow={freshCount} mask={mask} refresh={delay:0.0}s " +
                            $"rawHits={rawHits} rawUnitHits={rawUnitHits} {shipInfo} visionPos={vision.transform.position.x:0.0},{vision.transform.position.y:0.0}");
                    }
                    catch (Exception e) { Out($"probe2 #{netId} FAILED: {e.Message}"); }
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
                case "loadout":
                {
                    // Weapon-sync diagnostics: every ship's holder weapons + what its module
                    // grid says the weapon clusters hold. A puppet whose grid has a secondary
                    // module but whose holder weapon is null = holder rebuild failed; a puppet
                    // missing the module = grid sync never delivered.
                    foreach (var kv in ShipSync.ShipsBySlot)
                    {
                        var s = kv.Value;
                        if (s == null) continue;
                        bool pup = s.GetComponent<RemotePuppet>() != null;
                        string pri = "?", sec = "?", gPri = "?", gSec = "?", count = "?";
                        try { pri = s.PrimaryWeapon != null ? WeaponName(s.PrimaryWeapon) : "none"; } catch { }
                        try { sec = s.SecondaryWeapon != null ? WeaponName(s.SecondaryWeapon) : "none"; } catch { }
                        string acts = "?";
                        try
                        {
                            var grid = s.ModuleGridOwner != null ? s.ModuleGridOwner.ModuleGrid : null;
                            if (grid != null)
                            {
                                gPri = ClusterMain(grid, ClusterType.PrimaryWeapon);
                                gSec = ClusterMain(grid, ClusterType.SecondaryWeapon);
                                acts = ClusterMain(grid, ClusterType.Active1) + "/"
                                     + ClusterMain(grid, ClusterType.Active2) + "/"
                                     + ClusterMain(grid, ClusterType.Active3);
                                var mg = grid as ModuleGrid;
                                if (mg != null) count = mg.Modules.Count.ToString();
                            }
                        }
                        catch (Exception e) { gPri = $"ERR:{e.Message}"; }
                        Out($"loadout P{kv.Key + 1}{(pup ? "(puppet)" : "(local)")}: pri={pri} sec={sec} " +
                            $"gridPri={gPri} gridSec={gSec} acts={acts} modules={count}");
                    }
                    if (ShipSync.ShipsBySlot.Count == 0) Out("loadout: no ships");
                    return;
                }
                case "equip":
                {
                    // Install a weapon module on the LOCAL ship's grid — the same path real
                    // gameplay uses (grid Install → cluster refresh → holder rebuilds weapon),
                    // so ModuleGridSync must pick it up. `equip list` enumerates ids.
                    var ship = ShipSync.LocalShip;
                    if (ship == null) { Out("equip: no local ship"); return; }
                    var registry = ServiceLocator.Get<ModuleRegistry>();
                    if (registry == null) { Out("equip: no ModuleRegistry"); return; }
                    if (parts.Length < 2 || parts[1].Equals("list", StringComparison.OrdinalIgnoreCase))
                    {
                        // One line per weapon: id + display name (spaces -> _ so the harness can
                        // match by name token) — bare GUIDs made picking a test weapon blind.
                        foreach (var item in registry.AllItems)
                        {
                            if (item is WeaponModuleData w)
                                Out($"equip: {w.Id} {(string.IsNullOrEmpty(w.displayName) ? "?" : w.displayName.Replace(' ', '_'))}");
                            else if (item is WeaponBasedActiveModuleData a)
                                Out($"equip: {a.Id} {(string.IsNullOrEmpty(a.displayName) ? "?" : a.displayName.Replace(' ', '_'))} (active)");
                            else if (item is SpawnMinionModuleData m)
                                Out($"equip: {m.Id} {(string.IsNullOrEmpty(m.displayName) ? "?" : m.displayName.Replace(' ', '_'))} (minion)");
                        }
                        return;
                    }
                    // Slot token: sec = secondary holder; act1/act2/act3 = the ability slots
                    // (weapon-based actives only — the point is projectile replication).
                    bool secondary = false;
                    int active = 0;
                    if (parts.Length >= 3)
                    {
                        var slotTok = parts[2].ToLowerInvariant();
                        secondary = slotTok == "sec";
                        if (slotTok == "act1") active = 1;
                        else if (slotTok == "act2") active = 2;
                        else if (slotTok == "act3") active = 3;
                    }
                    ModuleData found = null;
                    string wanted = parts[1].Replace('_', ' ');
                    foreach (var item in registry.AllItems)
                    {
                        bool typeOk = active > 0
                            ? item is WeaponBasedActiveModuleData || item is SpawnMinionModuleData
                            : item is WeaponModuleData;
                        if (!typeOk) continue;
                        var m = (ModuleData)item;
                        if (m.Id.Equals(parts[1], StringComparison.OrdinalIgnoreCase)
                            || m.Id.IndexOf(parts[1], StringComparison.OrdinalIgnoreCase) >= 0
                            || (!string.IsNullOrEmpty(m.displayName)
                                && m.displayName.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0))
                        { found = m; break; }
                    }
                    if (found == null)
                    { Out($"equip: no {(active > 0 ? "weapon-based active" : "weapon")} module matches '{parts[1]}'"); return; }
                    var grid2 = ship.ModuleGridOwner != null ? ship.ModuleGridOwner.ModuleGrid as ModuleGrid : null;
                    if (grid2 == null) { Out("equip: local ship has no ModuleGrid"); return; }
                    var pos2 = active == 1 ? ModuleGrid.Active1GridPosition
                        : active == 2 ? ModuleGrid.Active2GridPosition
                        : active == 3 ? ModuleGrid.Active3GridPosition
                        : secondary ? ModuleGrid.SecondaryWeaponGridPosition : ModuleGrid.PrimaryWeaponGridPosition;
                    var module = found.DeepCopy();
                    var existing = grid2[pos2];
                    if (existing != null) module.CopyConnectionsFrom(existing);
                    grid2.Install(pos2, module);
                    Out($"equip: installed {found.Id} in {(active > 0 ? "ACTIVE" + active : secondary ? "SECONDARY" : "PRIMARY")} slot");
                    return;
                }
                case "sync":
                {
                    if (parts.Length < 2 || !int.TryParse(parts[1], out int syncId))
                    { Out("sync: usage sync <netId>"); return; }
                    Out(EnemySync.DescribeSyncState(syncId));
                    return;
                }
                case "useactive":
                {
                    // Trigger an ability-slot module through the game's own path (what
                    // ModuleActivator does on key-hold, minus the cooldown gate). A weapon-based
                    // active fires its weapon -> DoShoot -> captured like any ship fire.
                    var ship = ShipSync.LocalShip;
                    if (ship == null) { Out("useactive: no local ship"); return; }
                    int idx = 1;
                    if (parts.Length >= 2) int.TryParse(parts[1], out idx);
                    if (idx < 1 || idx > 3) { Out("useactive: index must be 1-3"); return; }
                    var grid = ship.ModuleGridOwner != null ? ship.ModuleGridOwner.ModuleGrid : null;
                    var cluster = grid != null
                        ? grid.GetCluster(idx == 1 ? ClusterType.Active1 : idx == 2 ? ClusterType.Active2 : ClusterType.Active3)
                        : null;
                    var activeModule = cluster != null && cluster.HasMainModule ? cluster.MainModule as ActiveModule : null;
                    if (activeModule == null) { Out($"useactive: no active module in slot {idx}"); return; }
                    activeModule.Activate(ship.Unit);
                    Out($"useactive: activated slot {idx} ({activeModule.Data.Id})");
                    return;
                }
                case "knockback":
                {
                    // Harness aid: projectile impulses shove ships off their test marks, which
                    // reads as position noise in assertions. Per-machine — issue to BOTH sides.
                    bool off = parts.Length >= 2
                        && (parts[1].Equals("off", StringComparison.OrdinalIgnoreCase) || parts[1] == "0");
                    KnockbackDisabled = off;
                    Out($"knockback: {(off ? "OFF (projectiles push nothing on this machine)" : "on (vanilla)")}");
                    return;
                }
                case "fuel":
                {
                    // Fuel-sync assertion: every ship's fuel fraction by slot, from the authoritative
                    // ShipsBySlot map (FindObjectsOfType missed a puppet mid-transition). A viewer's
                    // puppet fuel must track its owner (esp. after a respawn refuel).
                    if (ShipSync.ShipsBySlot.Count == 0) { Out("fuel: no ships"); return; }
                    foreach (var kv in ShipSync.ShipsBySlot)
                    {
                        if (kv.Value == null) continue;
                        string who = kv.Key == session.LocalSlot ? "local" : "puppet";
                        Out($"fuel P{kv.Key + 1}({who})={UnitStatus.ReadFuelFraction(kv.Value):0.00}");
                    }
                    return;
                }
                case "linkhealth":
                {
                    // WS7.2 harness: force the score this machine ADVERTISES (its receive quality)
                    // so a throttle test can drive owners' budgets through the real message path.
                    // "linkhealth 200" = pretend we're starving; "linkhealth auto" = measured again.
                    if (parts.Length >= 2 && byte.TryParse(parts[1], out byte forced) && forced != 255)
                        Sync.EnemySync.ForcedLinkScore = forced;
                    else Sync.EnemySync.ForcedLinkScore = 255;
                    Out(Sync.EnemySync.ForcedLinkScore == 255
                        ? "linkhealth: AUTO (measured underruns/gaps/jitter)"
                        : $"linkhealth: FORCED score={Sync.EnemySync.ForcedLinkScore} (advertised every 2s)");
                    return;
                }
                case "netbudget":
                {
                    // WS7.1 harness: hard-cap the per-viewer presentation byte budget on THIS
                    // machine (the owner side), or print current budgets. "netbudget 400" caps;
                    // "netbudget auto" returns control to link-health adaptation.
                    if (parts.Length >= 2 && float.TryParse(parts[1], out float cap) && cap > 0f)
                        Sync.EnemySync.ForcedViewerBudget = cap;
                    else if (parts.Length >= 2) Sync.EnemySync.ForcedViewerBudget = 0f;
                    Out($"netbudget: {(Sync.EnemySync.ForcedViewerBudget > 0f ? $"FORCED {Sync.EnemySync.ForcedViewerBudget:0}B/tick for every viewer" : "auto (link-health adaptive)")}"
                        + $" budgetDrops={InstrumentationCounters.StateEntriesBudgetDroppedCount}");
                    return;
                }
                case "vsync":
                {
                    // Clock-dilation harness: an unfocused instance's vsync-aligned frame timing
                    // pins unscaledDeltaTime at 1/refresh, dilating the whole sim under load (see
                    // [Clock]). "vsync 0 [fpsCap]" turns vsync off (optional targetFrameRate cap,
                    // 0 = uncapped); "vsync 1" restores. No args = print current + measured rate.
                    if (parts.Length >= 2 && int.TryParse(parts[1], out int vs))
                        QualitySettings.vSyncCount = Mathf.Clamp(vs, 0, 4);
                    if (parts.Length >= 3 && int.TryParse(parts[2], out int cap))
                        Application.targetFrameRate = cap <= 0 ? -1 : cap;
                    Out($"vsync: vSyncCount={QualitySettings.vSyncCount} targetFrameRate={Application.targetFrameRate} " +
                        $"clockRate={RuntimeInstrumentation.ClockRate:0.00}x real");
                    return;
                }
                case "snaphz":
                {
                    // Jitter A/B harness (owner side): live-set the combat snapshot rate. The send
                    // loop reads CombatStateHz.Value every tick, so this takes effect immediately.
                    // "snaphz 60" doubles combat cadence; "snaphz auto" restores the config default.
                    if (parts.Length >= 2 && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float hz) && hz >= 1f)
                        NetConfig.CombatStateHz.Value = Mathf.Clamp(hz, 1f, 120f);
                    else if (parts.Length >= 2)
                        NetConfig.CombatStateHz.Value = (float)NetConfig.CombatStateHz.DefaultValue;
                    Out($"snaphz: CombatStateHz={NetConfig.CombatStateHz.Value:0} " +
                        $"(tick={Mathf.Max(NetConfig.StateHz.Value, NetConfig.CombatStateHz.Value):0}Hz)");
                    return;
                }
                case "interpdelay":
                {
                    // Jitter A/B harness (viewer side): add fixed headroom to every entity puppet's
                    // render delay. Tests whether underruns come from render time overtaking the
                    // buffer (client frame stalls / sender gaps) — if +N ms kills the underruns and
                    // the wasted-speed table drops with it, buffer depth is the lever. Ships/props
                    // unaffected. "interpdelay 60" = +60ms; "interpdelay auto" = off.
                    if (parts.Length >= 2 && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float ms) && ms > 0f)
                        Sync.RemoteEntityPuppet.ExperimentExtraDelay = Mathf.Clamp(ms, 0f, 500f) / 1000f;
                    else if (parts.Length >= 2) Sync.RemoteEntityPuppet.ExperimentExtraDelay = 0f;
                    Out(Sync.RemoteEntityPuppet.ExperimentExtraDelay > 0f
                        ? $"interpdelay: +{Sync.RemoteEntityPuppet.ExperimentExtraDelay * 1000f:0}ms on every entity puppet"
                        : "interpdelay: auto (adaptive only)");
                    return;
                }
                case "shop":
                {
                    // Harness: simulate the local player having the shop/ship-menu open so routed
                    // damage to their ship should be shielded (co-op shop-invulnerability).
                    bool on = parts.Length >= 2
                        && (parts[1].Equals("on", StringComparison.OrdinalIgnoreCase) || parts[1] == "1");
                    Sync.DamageSync.ShopMenuTestOverride = on;
                    Out($"shop: local player treated as {(on ? "IN SHOP (routed damage shielded)" : "not shopping")}");
                    return;
                }
                case "stall":
                {
                    // Freeze the main thread to reproduce a load/GC stall — the reconnect-in-
                    // place path on the other machine is exactly what this exists to exercise.
                    float stallSecs = 12f;
                    if (parts.Length >= 2)
                        float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out stallSecs);
                    stallSecs = Mathf.Clamp(stallSecs, 1f, 25f);
                    Out($"stall: freezing main thread {stallSecs:0.0}s");
                    System.Threading.Thread.Sleep((int)(stallSecs * 1000f));
                    Out("stall: resumed");
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
                    // Trailing "pin": freeze the spawn's position in place (rotation stays free
                    // so turrets aim) — sweep tests stop fighting chase AI for geometry.
                    bool pinAtSpawn = parts.Length >= 2
                        && parts[parts.Length - 1].Equals("pin", StringComparison.OrdinalIgnoreCase);
                    foreach (var info in egm.savablesCollection.savableObjectInfos)
                    {
                        if (!string.Equals(info.entityId, parts[1], StringComparison.OrdinalIgnoreCase)) continue;
                        // CreateEntity rides MinionSync's generic runtime-spawn capture, so this
                        // spawn replicates to every peer with a proper runtime netId + authority.
                        var spawned = egm.CreateEntity(info.prefab, pos);
                        if (pinAtSpawn && spawned != null) PinBody(spawned.GetComponent<Rigidbody2D>(), true);
                        Out($"spawned {info.entityId} at {pos.x:0.0},{pos.y:0.0}" + (pinAtSpawn ? " (pinned)" : ""));
                        return;
                    }
                    Out($"spawn: unknown EntityId '{parts[1]}' (names are the prefab entityIds, e.g. Unit_Fly)");
                    return;
                }
                case "pin":
                {
                    if (parts.Length < 2 || !int.TryParse(parts[1], out int pinId))
                    {
                        Out("pin: usage pin <netId> [off]");
                        return;
                    }
                    bool release = parts.Length >= 3 && parts[2].Equals("off", StringComparison.OrdinalIgnoreCase);
                    if (!NetIds.TryGetInstanceId(pinId, out int pinInstance))
                    {
                        Out($"pin: netId {pinId} unknown");
                        return;
                    }
                    var pinEgm = ServiceLocator.Get<EntityGameObjectManager>();
                    if (pinEgm == null || !pinEgm.TryGetSavableEntity(pinInstance, out var pinSe) || pinSe == null)
                    {
                        Out($"pin: #{pinId} has no live object here");
                        return;
                    }
                    PinBody(pinSe.GetComponent<Rigidbody2D>(), !release);
                    bool puppetHere = pinSe.GetComponentInChildren<Sync.RemoteEntityPuppet>(true) != null;
                    Out($"pin: #{pinId} {(release ? "released" : "position frozen (AI/aim/fire still live)")}" +
                        (puppetHere ? $" — NOTE: this copy is a puppet; {NetDiag.Owner(EnemySync.OwnerOf(pinId))} simulates it, pin there" : ""));
                    return;
                }
                case "shot": DevUi.Shot(parts, Out); return;
                case "pausemenu": DevUi.PauseMenu(parts, Out); return;
                case "uidump": DevUi.Dump(parts, Out); return;
                case "uitree": DevUi.Tree(parts, Out); return;
                case "click": DevUi.Click(parts, Out); return;
                case "nav": DevUi.Nav(parts, Out); return;
                case "sel": DevUi.Sel(Out); return;
                default:
                    Out($"unknown command '{parts[0]}' (spawn/tp/poke/entities/status/autofly/say/shot/uidump/uitree/click/nav/sel)");
                    return;
            }
        }

        /// <summary>Freeze/release a body's position while leaving rotation (turret aim) and
        /// every behaviour untouched — the harness's "hold still" for spawned test targets.</summary>
        private static void PinBody(Rigidbody2D rb, bool pin)
        {
            if (rb == null) return;
            if (pin)
            {
                rb.linearVelocity = Vector2.zero;
                rb.angularVelocity = 0f;
                rb.constraints |= RigidbodyConstraints2D.FreezePosition;
            }
            else rb.constraints &= ~RigidbodyConstraints2D.FreezePosition;
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

        /// <summary>The ship's shooter for the wanted holder: each Shooter is wired to one
        /// WeaponHolder, so match by the holder's current weapon instance.</summary>
        private static Shooter FindShooter(Ship ship, bool secondary)
        {
            WeaponBase want = null;
            try { want = secondary ? ship.SecondaryWeapon : ship.PrimaryWeapon; } catch { }
            Shooter first = null;
            foreach (var s in ship.GetComponentsInChildren<Shooter>(true))
            {
                if (first == null) first = s;
                if (want != null && s.weaponHolder != null && ReferenceEquals(s.weaponHolder.Weapon, want))
                    return s;
            }
            return secondary ? null : first;
        }

        private static string WeaponName(WeaponBase w)
            => w.TemplateData != null ? w.TemplateData.name : w.GetType().Name;

        private static string ClusterMain(IModuleGrid grid, ClusterType type)
        {
            var cluster = grid.GetCluster(type);
            return cluster != null && cluster.HasMainModule && cluster.MainModule != null
                ? cluster.MainModule.Data.Id : "empty";
        }

        // ---------------------------------------------------------------- knockback switch
        internal static bool KnockbackDisabled;

        /// <summary>`knockback off` suppresses the projectile impulse at its single gate
        /// (Projectile.CanKnockBack) so fire tests keep ships parked on their marks.</summary>
        [HarmonyPatch(typeof(Projectile), "CanKnockBack")]
        internal static class SuppressKnockback
        {
            private static bool Prefix(ref bool __result)
            {
                if (!KnockbackDisabled) return true;
                __result = false;
                return false;
            }
        }

        // ---------------------------------------------------------------- debug menu key
        // Crash-safe port of the standalone PunkDebugKey mod: fires only while the menu's own
        // Update runs, replays its open branch via reflection, never touches its InputActions.
        [HarmonyPatch(typeof(DebugMenu), "Update")]
        internal static class OpenDebugMenuKey
        {
            private static readonly FieldInfo IsOpenedF = AccessTools.Field(typeof(DebugMenu), "isOpened");
            private static readonly FieldInfo ScreenF = AccessTools.Field(typeof(DebugMenu), "screen");
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
                    // Deliberately NOT the vanilla SetTimeScale(0.1f) here: local slow-mo while
                    // peers run full speed starves this machine's share of the shared sim.
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
