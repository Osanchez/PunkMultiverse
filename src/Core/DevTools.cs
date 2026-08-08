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
    ///        quit | stop | shutdown       clean shutdown: end session (save + notify), then exit
    ///    Every execution logs "[Dev] ..." so scenarios are assertable from LogOutput.log.
    /// </summary>
    internal static class DevTools
    {
        private static float _nextPollAt;
        private static bool _warnedPath;

        // Deferred-quit deadline (unscaledTime). `quit`/`stop`/`shutdown` ends the session
        // synchronously (economy save + client disconnect packets) then arms this so the process
        // exits a beat later — long enough for the outgoing UDP disconnect datagrams to leave the
        // socket before teardown. -1 = not armed. Deliberately NOT cleared by Reset().
        private static float _quitAt = -1f;

        /// <summary>Dev shield for sweep tests: the local ship's damage is BLOCKED at the
        /// routing chokepoints (DamageSync), so every incoming hit still logs its
        /// [CombatHit] audit line with source attribution (applied=False) — the test proves
        /// enemy damage reaches the player pipeline without ever losing the test ship.</summary>
        internal static bool GodMode { get; private set; }

        internal static void Reset()
        {
            GodMode = false;
            Patches.MenuMutex.Reset(); // clear pause/item-wheel flags so they can't stick across runs
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
        private static int _fireTargetSlot = -1; // aim: track this PLAYER's ship while firing
        private static Vector2 _fireDir;       // aim: fixed direction (zero = don't steer)
        private static BarrelTransform[] _fireBarrels;
        private static Aimer[] _fireAimers;
        private static float _nextDirectShotAt;

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
                    _fireTargetNetId = 0; _fireTargetSlot = -1;
                    _fireDir = Vector2.zero; _fireBarrels = null; _fireAimers = null;
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
                // Track another PLAYER: a ship has no netId (ships are keyed by slot), so the
                // entity-tracking branch below can never aim at one. Player-vs-player fire is the
                // only way to test that PvP hits register at all, so it gets its own aim source.
                if (_fireTargetSlot >= 0 && ship != null
                    && ShipSync.ShipsBySlot.TryGetValue((byte)_fireTargetSlot, out var targetShip)
                    && targetShip != null)
                {
                    dir = ((Vector2)targetShip.transform.position - (Vector2)ship.transform.position).normalized;
                }
                else if (_fireTargetNetId != 0 && ship != null
                    && NetIds.TryGetInstanceId(_fireTargetNetId, out int inst))
                {
                    var egm = ServiceLocator.Get<EntityGameObjectManager>();
                    if (egm != null && egm.TryGetSavableEntity(inst, out var target) && target != null)
                        dir = ((Vector2)target.transform.position - (Vector2)ship.transform.position).normalized;
                }
                else if (_fireDir != Vector2.zero) dir = _fireDir;
                // Feed the AIMER, not just the barrel. A direct BarrelTransform.Direction write on a
                // LOCAL ship loses every frame: the ship's Aimer is enabled, its Update runs after
                // this one, and it rotates the barrel back toward its own target. Sync/RemotePuppet
                // documents exactly this race for puppets and solves it the same way. The comment
                // that used to sit here claimed an unfocused harness window meant the crosshair was
                // not fighting us; that was an assumption, and it was wrong — measured 2026-07-29,
                // with the target proven reachable, hostile and in range, the bot's shots went
                // somewhere else entirely and hitAnotherShip stayed 0 across four runs.
                if (dir != Vector2.zero)
                {
                    if (_fireAimers == null && ship != null) _fireAimers = ship.GetComponentsInChildren<Aimer>(true);
                    if (_fireAimers != null)
                        foreach (var aimer in _fireAimers)
                            if (aimer != null)
                                aimer.AimAt((Vector2)aimer.transform.position + dir * 20f);
                    if (ship != null && ship.Crosshair != null)
                        ship.Crosshair.transform.position = (Vector2)ship.transform.position + dir * 20f;
                    if (_fireBarrels != null)
                        foreach (var b in _fireBarrels)
                            if (b != null) b.Direction = dir;

                    // Aiming at a PLAYER: drive the weapon exactly the way ProjectileSync's own
                    // replay does — DoShoot with a FakeBarrel at a chosen muzzle and direction.
                    // Neither of the other two paths can be aimed: the Shooter is an auto-turret
                    // that re-targets itself, and IsTriggerPulled + Warmup produced no projectiles
                    // at all (measured 2026-07-29: playerProjTicks=0 through a full 12s burst).
                    // This is the game's real fire path, so everything downstream — identity
                    // stamping, the fire broadcast, collision, routing — behaves like a live shot.
                    // The muzzle is pushed clear of the shooter's own hull, which is now IN the
                    // sweep mask and would otherwise be the first thing every round meets.
                    if (_fireTargetSlot >= 0 && _fireWeapon != null && ship != null
                        && Time.unscaledTime >= _nextDirectShotAt)
                    {
                        _nextDirectShotAt = Time.unscaledTime + 0.2f;
                        Vector2 muzzle = (Vector2)ship.transform.position + dir * 3f;
                        try
                        {
                            AccessTools.Method(typeof(WeaponBase), "DoShoot")
                                ?.Invoke(_fireWeapon, new object[] { new FakeBarrel(muzzle, dir) });
                        }
                        catch (System.Exception e)
                        {
                            Out($"fire: direct shot failed: {e.InnerException?.Message ?? e.Message}");
                            _fireUntil = 0f;
                        }
                    }
                }
            }
            catch { _fireShooter = null; _fireWeapon = null; _fireUntil = 0f; _fireBarrels = null; _fireAimers = null; }
        }

        /// <summary>Health and fuel exactly as UI/ShipStatusBars binds them: health from the unit's
        /// DamagableResource tank, fuel from the tank whose Resource is named "Fuel". Capacities are
        /// reported too — a bar can be wrong either by showing a stale VALUE or by binding a tank
        /// whose capacity never followed an upgrade.</summary>
        private static void ReadBarTanks(Ship ship, out float hp, out float hpMax,
            out float fuel, out float fuelMax)
        {
            hp = hpMax = fuel = fuelMax = 0f;
            try
            {
                var unit = ship.GetComponentInParent<Unit>() ?? ship.GetComponent<Unit>();
                if (unit == null) return;
                var dr = unit.GetComponent<DamagableResource>();
                if (dr?.Tank != null) { hp = dr.Tank.Value; hpMax = dr.Tank.Capacity; }
                if (_barFuelResource == null)
                {
                    var registry = ServiceLocator.Get<ResourceRegistry>();
                    var all = registry != null
                        ? HarmonyLib.Traverse.Create(registry).Property("AllItems").GetValue()
                            as System.Collections.Generic.IEnumerable<Resource>
                        : null;
                    if (all != null)
                        foreach (var r in all)
                            if (r != null && r.name != null
                                && r.name.IndexOf("Fuel", StringComparison.OrdinalIgnoreCase) >= 0)
                            { _barFuelResource = r; break; }
                }
                if (_barFuelResource != null && unit.HasTank(_barFuelResource))
                {
                    var tank = unit.GetTank(_barFuelResource);
                    if (tank != null) { fuel = tank.Value; fuelMax = tank.Capacity; }
                }
            }
            catch { }
        }

        private static Resource _barFuelResource;

        public static void Tick(NetSession session)
        {
            if (_quitAt >= 0f && Time.unscaledTime >= _quitAt)
            {
                _quitAt = -1f;
                Plugin.Log.LogInfo("[Dev] quit grace elapsed — exiting process");
                Application.Quit();
                return;
            }
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
        // RENDER-frame smoothness probe — measures what is actually DRAWN, per screen frame,
        // where the fixed-step metrics (motionprofile, jitterstats) are structurally blind:
        // they sample physics steps (or the ideal interp target), so fixed-step aliasing,
        // interpolation failures, and rotation twitch are invisible to them while being fully
        // visible to a player on a high-refresh display. Run on the SAME netId owner-side and
        // puppet-side; a smooth render path has stall% near 0 and speedCV well under 1.
        //  - stall% : moving-entity frames that advanced <10% of the mean step (fixed-step
        //             aliasing signature: at 240fps/50Hz physics without interpolation ~80%)
        //  - speedCV: stddev/mean of per-frame speed (burstiness of drawn motion)
        //  - rotWasted: |angle steps| summed minus net rotation, per second (facing twitch)
        /// <summary>On-demand render-fps benchmark (`fpsbench` devcmd). One sample per drawn
        /// frame; the BENCH: line is stable for harness parsing.</summary>
        private static System.Collections.IEnumerator FpsBench(float secs)
        {
            var samples = new List<float>(16384);
            float start = Time.unscaledTime;
            yield return null; // skip the partial frame the command landed in
            float prev = Time.unscaledTime;
            while (Time.unscaledTime - start < secs)
            {
                yield return null;
                float now = Time.unscaledTime;
                samples.Add((now - prev) * 1000f);
                prev = now;
            }
            if (samples.Count < 10) { Out("BENCH: too few frames"); yield break; }
            samples.Sort();
            int n = samples.Count;
            float sum = 0f; foreach (var s in samples) sum += s;
            float avg = sum / n;
            float P(double q) => samples[Mathf.Clamp((int)(n * q), 0, n - 1)];
            int over240 = 0, over144 = 0, over120 = 0, over90 = 0;
            foreach (var s in samples)
            {
                if (s > 1000f / 240f) over240++;
                if (s > 1000f / 144f) over144++;
                if (s > 1000f / 120f) over120++;
                if (s > 1000f / 90f) over90++;
            }
            Out(string.Format(CultureInfo.InvariantCulture,
                "BENCH: {0:0.0}s frames={1} avgFps={2:0.0} | frameMs p50={3:0.00} p95={4:0.00} p99={5:0.00} max={6:0.0} " +
                "| slowerThan 240Hz={7:0.0}% 144Hz={8:0.0}% 120Hz={9:0.0}% 90Hz={10:0.0}%",
                secs, n, 1000f / avg, P(0.50), P(0.95), P(0.99), samples[n - 1],
                100f * over240 / n, 100f * over144 / n, 100f * over120 / n, 100f * over90 / n));
        }

        private static System.Collections.IEnumerator RenderSmooth(int netId, Transform t, float secs,
            string label = null)
        {
            label = label ?? $"#{netId}";
            int frames = 0, stallFrames = 0, movingFrames = 0;
            float speedSum = 0f, speedSqSum = 0f, speedMax = 0f;
            float rotPath = 0f, rotMaxStep = 0f, prevAngle = t.eulerAngles.z, startAngle = prevAngle;
            Vector2 prevPos = t.position;
            var samples = new System.Collections.Generic.List<float>(2048);
            float t0 = Time.unscaledTime, prevTime = t0;
            while (Time.unscaledTime - t0 < secs)
            {
                yield return null; // end of Update — transform holds the interpolated DRAWN pose
                if (t == null) { Out($"rendersmooth {label}: target died mid-sample"); yield break; }
                float now = Time.unscaledTime;
                float dt = Mathf.Max(0.0001f, now - prevTime);
                prevTime = now;
                Vector2 pos = t.position;
                float step = Vector2.Distance(pos, prevPos);
                prevPos = pos;
                float angle = t.eulerAngles.z;
                float dAngle = Mathf.Abs(Mathf.DeltaAngle(prevAngle, angle));
                prevAngle = angle;
                rotPath += dAngle;
                rotMaxStep = Mathf.Max(rotMaxStep, dAngle);
                frames++;
                float speed = step / dt;
                samples.Add(speed);
                speedSum += speed; speedSqSum += speed * speed; speedMax = Mathf.Max(speedMax, speed);
            }
            float dur = Time.unscaledTime - t0;
            float mean = speedSum / Mathf.Max(1, frames);
            // Stall detection needs the mean first — second pass over the recorded samples.
            foreach (float s in samples)
            {
                if (mean < 1f) break; // entity ~stationary; stall% meaningless
                movingFrames++;
                if (s < mean * 0.1f) stallFrames++;
            }
            float variance = frames > 1 ? Mathf.Max(0f, speedSqSum / frames - mean * mean) : 0f;
            float cv = mean > 0.01f ? Mathf.Sqrt(variance) / mean : 0f;
            float netRot = Mathf.Abs(Mathf.DeltaAngle(startAngle, prevAngle));
            Out(string.Format(CultureInfo.InvariantCulture,
                "rendersmooth {0}: {1:0.0}s {2} frames ({3:0}fps) | drawn speed mean={4:0.0} max={5:0.0} u/s " +
                "CV={6:0.00} | stall%={7:0.0} | rotWasted={8:0.0}deg/s maxStep={9:0.0}deg",
                label, dur, frames, frames / Mathf.Max(0.1f, dur), mean, speedMax, cv,
                movingFrames > 0 ? 100f * stallFrames / movingFrames : 0f,
                (rotPath - netRot) / Mathf.Max(0.1f, dur), rotMaxStep));
        }

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
                case "start":
                    // Drive the session admin's START (a coordinator no longer auto-launches). Works for
                    // a normal host too. No-op with a reason if we aren't the admin or not all-ready.
                    Out($"start: admin={session.IsSessionAdmin} allReady={session.AllReady} state={session.State}");
                    session.RequestStart();
                    return;
                case "mainmenu":
                    // Leave to the main menu via the same code path the buttons use — exercises
                    // the disconnect-on-menu patch headless, and doubles as an escape hatch.
                    Out("mainmenu: loading menu scene (disconnects from any live session)");
                    MainMenuScene.Load();
                    return;
                case "endrun":
                    // End the run for the whole party and return everyone to the lobby (admin
                    // in-game, host, or the server console). The dedicated server immediately
                    // starts pre-building the next world.
                    Out($"endrun: admin={session.IsSessionAdmin} state={session.State}");
                    session.RequestEndRun();
                    return;
                case "ready":
                {
                    bool want = parts.Length < 2 || !parts[1].Equals("off", StringComparison.OrdinalIgnoreCase);
                    var me = session.LocalPlayer;
                    if (me != null) session.SetLocalPrefs(me.ColorIndex, want);
                    Out($"ready {(want ? "ON" : "OFF")}");
                    return;
                }
                case "quit":
                case "stop":
                case "shutdown":
                    // Clean process shutdown for the dedicated server: end the session (as host
                    // this broadcasts SessionEnded + disconnect packets to clients and saves the
                    // economy stash synchronously), then exit after a short grace so the outgoing
                    // datagrams flush. The container stop hook writes this instead of hard-killing
                    // Wine, so `docker stop`/`restart` preserves state.
                    Out("quit: ending session and shutting down");
                    Plugin.Log.LogInfo("[Dev] quit requested — ending session and exiting");
                    try { session.StopSession("server shutdown"); }
                    catch (Exception e) { Plugin.Log.LogWarning($"[Dev] StopSession during quit failed: {e.Message}"); }
                    _quitAt = Time.unscaledTime + 0.5f;
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
                case "udpstats":
                    // Transport-level truth for the go-live wedge hunt: per-peer reliable queue
                    // depths, MTU, ping, and manager loss counters (Udp transport only).
                    Out(session.TransportHealth());
                    return;
                case "loglevel":
                {
                    // Live log verbosity switch (see NetConfig.LogLevel): flip a running instance
                    // (or the server via console) to Verbose before reproducing a bug, back after.
                    string level = parts.Length > 1 ? parts[1].Trim() : null;
                    if (string.IsNullOrEmpty(level)) { Out($"loglevel: {NetConfig.LogLevel.Value} (use: loglevel Normal|Verbose|Quiet)"); return; }
                    if (!level.Equals("Normal", StringComparison.OrdinalIgnoreCase)
                        && !level.Equals("Verbose", StringComparison.OrdinalIgnoreCase)
                        && !level.Equals("Quiet", StringComparison.OrdinalIgnoreCase))
                    { Out($"loglevel: unknown '{level}' (use: Normal|Verbose|Quiet)"); return; }
                    NetConfig.LogLevel.Value = char.ToUpperInvariant(level[0]) + level.Substring(1).ToLowerInvariant();
                    Out($"loglevel: {NetConfig.LogLevel.Value}");
                    return;
                }
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
                case "fpsbench":
                {
                    // Render-fps benchmark: sample every drawn frame for N seconds, report the
                    // distribution. The [Frame] instrumentation aggregates 30s windows; this is
                    // the on-demand, harness-parsable version (BENCH: prefix).
                    float fbSecs = 20f;
                    if (parts.Length >= 2) float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out fbSecs);
                    Out($"fpsbench: sampling {fbSecs:0.0}s...");
                    session.StartCoroutine(FpsBench(Mathf.Clamp(fbSecs, 2f, 120f)));
                    return;
                }
                case "tpnearest":
                {
                    // Teleport beside the nearest live enemy Unit — the calm alternative to
                    // autofly for combat-adjacent benchmarking (Omar: "teleport to nearby
                    // enemies and hover"). Ship arrives with zero velocity and just hovers.
                    var ship = ShipSync.LocalShip;
                    if (ship == null) { Out("tpnearest: no local ship"); return; }
                    Vector2 here = ship.transform.position;
                    int bestId = EnemySync.NearestLiveUnit(here, out Vector2 best);
                    if (bestId == 0) { Out("tpnearest: no live enemy found"); return; }
                    float bestSq = (best - here).sqrMagnitude;
                    var dst = best + new Vector2(0f, 6f); // hover above, out of contact damage
                    ship.Unit.ComponentData.entity.MoveTo(dst);
                    var shipRb = ship.GetComponent<Rigidbody2D>();
                    if (shipRb != null)
                    {
                        RemoteEntityPuppet.TeleportWithChildren(shipRb, dst);
                        shipRb.linearVelocity = Vector2.zero;
                    }
                    ship.transform.position = dst;
                    Out($"tpnearest: -> #{bestId} at {best.x:0.0},{best.y:0.0} (dist was {Mathf.Sqrt(bestSq):0.0})");
                    return;
                }
                case "tpplayer":
                {
                    // Put the two ships in each other's faces. Position staleness is only
                    // observable against a target you are actually tracking, so every ship-sync
                    // test starts by collapsing the distance BR's spawn scatter deliberately
                    // creates (slots land ~1600 units apart).
                    if (parts.Length < 2 || !byte.TryParse(parts[1], out byte tpSlot))
                    { Out("tpplayer <slot> [offset]"); return; }
                    var myShip = ShipSync.LocalShip;
                    if (myShip == null) { Out("tpplayer: no local ship"); return; }
                    if (!ShipSync.ShipsBySlot.TryGetValue(tpSlot, out var target) || target == null)
                    { Out($"tpplayer: no ship known for slot {tpSlot}"); return; }
                    float off = 12f;
                    if (parts.Length >= 3) float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out off);
                    Vector2 dest = (Vector2)target.transform.position + new Vector2(off, 0f);
                    myShip.Unit.ComponentData.entity.MoveTo(dest);
                    var myRb = myShip.GetComponent<Rigidbody2D>();
                    if (myRb != null) { RemoteEntityPuppet.TeleportWithChildren(myRb, dest); myRb.linearVelocity = Vector2.zero; }
                    myShip.transform.position = dest;
                    Out($"tpplayer: -> beside slot {tpSlot} at {dest.x:0.0},{dest.y:0.0}");
                    return;
                }
                // Empty the terrain around the local ship so a PvP probe has a clear line of fire.
                // Measured 2026-07-29: `tpplayer` collapses the distance between two ships but says
                // nothing about what is BETWEEN them — both bots sit on a station, embedded in
                // ground, and every shot logged `player bullet HIT layer=10(Ground)` at the muzzle.
                // A test that cannot land a shot in an empty room proves nothing about PvP.
                case "clearterrain":
                {
                    float ctRadius = 14f;
                    if (parts.Length >= 2) float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out ctRadius);
                    var ctShip = ShipSync.LocalShip;
                    if (ctShip == null) { Out("clearterrain: no local ship"); return; }
                    var ctLevel = ServiceLocator.Get<Level>();
                    if (ctLevel == null) { Out("clearterrain: no level"); return; }
                    Vector2 c = ctShip.transform.position;
                    int cleared = 0;
                    float r2 = ctRadius * ctRadius;
                    for (int y = Mathf.FloorToInt(c.y - ctRadius); y <= Mathf.CeilToInt(c.y + ctRadius); y++)
                        for (int x = Mathf.FloorToInt(c.x - ctRadius); x <= Mathf.CeilToInt(c.x + ctRadius); x++)
                        {
                            if (!ctLevel.ContainsCell(x, y)) continue;
                            float dx = x - c.x, dy = y - c.y;
                            if (dx * dx + dy * dy > r2) continue;
                            if (ctLevel.GetCellTypeId(x, y) == 0) continue;
                            ctLevel.SetCell(new Vector2Int(x, y), 0);
                            cleared++;
                        }
                    Out($"clearterrain: emptied {cleared} cells within {ctRadius:0} of ({c.x:0},{c.y:0})");
                    return;
                }
                // The one command that answers "why did my shot not hurt the other player" with a
                // fact instead of a theory. Run it while standing in sight of them.
                // Verify the shutdown safety net without needing a real deadlock to happen.
                // Set the local ship alight on demand. Burn is applied straight from
                // DamagableResource.Update and never touches the damage pipeline, so it is the one
                // source no existing probe could reach — and the one that was bypassing every shield.
                // Pick a drop region without a mouse. The drop screen has been manual-test-only,
                // which is exactly why "deploy drops you through unstreamed terrain" reached a real
                // match: no automated run could reach Deploy at all.
                case "drop":
                {
                    var dropOpts = Modes.BattleRoyaleSpawnSelect.AvailableOptions;
                    if (dropOpts == null || dropOpts.Count == 0) { Out("drop: no regions offered (no window open?)"); return; }
                    byte dropBiome = dropOpts[0].BiomeId;
                    if (parts.Length >= 2 && byte.TryParse(parts[1], out byte wanted)) dropBiome = wanted;
                    Out($"drop: choosing biome {dropBiome} of {dropOpts.Count} region(s), " +
                        $"armed={Modes.BattleRoyaleSpawnSelect.InputArmed}, " +
                        $"deployed={Modes.BattleRoyaleSpawnSelect.Deployed}");
                    Modes.BattleRoyaleSpawnSelect.Choose(dropBiome);
                    return;
                }
                case "burn":
                {
                    var burnShip = ShipSync.LocalShip;
                    if (burnShip == null) { Out("burn: no local ship"); return; }
                    var burnData = burnShip.GetComponent<Unit>()?.ComponentData;
                    if (burnData == null) { Out("burn: no unit data"); return; }
                    // No argument = REPORT ONLY. The measurement that matters is what the burn
                    // level is a few seconds AFTER being set, which needs a read that does not
                    // itself re-light the ship.
                    if (parts.Length >= 2
                        && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float burnLevel))
                        burnData.BurnLevel = burnLevel;
                    var burnDr = burnShip.GetComponent<DamagableResource>();
                    Out($"burn: BurnLevel={burnData.BurnLevel:0.#} onFire={burnData.IsOnFire} " +
                        $"hp={(burnDr != null ? burnDr.CurrentHealth : -1f):0.##} " +
                        $"shielded={(GodMode || Sync.DamageSync.LocalShopMenuOpen() || Modes.BattleRoyaleSpawnSelect.SpawnProtected)}");
                    return;
                }
                case "exitkill":
                {
                    int ekSecs = 3;
                    if (parts.Length >= 2) int.TryParse(parts[1], out ekSecs);
                    Out($"exitkill: process will be force-closed in {ekSecs}s (testing Core/ExitWatchdog.cs)");
                    ExitWatchdog.ForceTest(ekSecs);
                    return;
                }
                case "pvpprobe":
                {
                    foreach (var probeLine in Patches.PvPDiag.Probe().Split('\n'))
                        Out(probeLine.TrimEnd());
                    return;
                }
                // Put two ships in an empty room and hold them there. The PvP probe kept failing on
                // rig geometry, not on the code under test: teleporting a ship next to another drops
                // it into terrain, and every shot detonated on the ground at the muzzle.
                case "pvpstage":
                {
                    var stageShip = ShipSync.LocalShip;
                    if (stageShip == null) { Out("pvpstage: no local ship"); return; }
                    float stageR = 26f;
                    if (parts.Length >= 2) float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out stageR);
                    // Optional LIFT. Clearing terrain is not enough on its own: ships spawn docked at
                    // a station, and a station is a prefab with its own colliders on the Ground layer
                    // — `clearterrain` deletes cells and leaves the Hatch and Platform standing. The
                    // measured consequence (2026-07-29) was a probe reporting the target reachable at
                    // 9.2 units with station geometry at 6.0, so every round detonated on the station
                    // and not one bullet ever reached a ship. Lifting into open air removes the whole
                    // class of obstruction instead of trying to delete it piece by piece.
                    float stageLift = 0f;
                    if (parts.Length >= 3) float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out stageLift);
                    if (stageLift > 0f)
                    {
                        Vector2 lifted = (Vector2)stageShip.transform.position + new Vector2(0f, stageLift);
                        stageShip.Unit.ComponentData.entity.MoveTo(lifted);
                        var liftRb = stageShip.GetComponent<Rigidbody2D>();
                        if (liftRb != null) RemoteEntityPuppet.TeleportWithChildren(liftRb, lifted);
                        stageShip.transform.position = lifted;
                    }
                    var stageLevel = ServiceLocator.Get<Level>();
                    int stageCleared = 0;
                    if (stageLevel != null)
                    {
                        Vector2 sc = stageShip.transform.position;
                        float sr2 = stageR * stageR;
                        for (int y = Mathf.FloorToInt(sc.y - stageR); y <= Mathf.CeilToInt(sc.y + stageR); y++)
                            for (int x = Mathf.FloorToInt(sc.x - stageR); x <= Mathf.CeilToInt(sc.x + stageR); x++)
                            {
                                if (!stageLevel.ContainsCell(x, y)) continue;
                                float dx = x - sc.x, dy = y - sc.y;
                                if (dx * dx + dy * dy > sr2) continue;
                                if (stageLevel.GetCellTypeId(x, y) == 0) continue;
                                stageLevel.SetCell(new Vector2Int(x, y), 0);
                                stageCleared++;
                            }
                    }
                    // Hold the hull still: a ship that falls out of the cleared pocket while the
                    // burst is in flight re-introduces exactly the terrain the clear removed.
                    var stageRb = stageShip.GetComponent<Rigidbody2D>();
                    if (stageRb != null)
                    {
                        stageRb.gravityScale = 0f;
                        stageRb.linearVelocity = Vector2.zero;
                        stageRb.angularVelocity = 0f;
                    }
                    Out($"pvpstage: emptied {stageCleared} cells within {stageR:0}, gravity held at 0 " +
                        $"({stageShip.transform.position.x:0},{stageShip.transform.position.y:0})");
                    return;
                }
                case "freezeprobe":
                {
                    float fpSecs = 40f;
                    if (parts.Length >= 2) float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out fpSecs);
                    Out($"freezeprobe: {FreezeProbe.Start(fpSecs)}");
                    return;
                }
                case "hostinfo":
                {
                    float hiSecs = 30f;
                    if (parts.Length >= 2) float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out hiSecs);
                    Out($"hostinfo: {HostInfo.Start(hiSecs)}");
                    return;
                }
                case "allocprof":
                {
                    string arg = parts.Length >= 2 ? parts[1] : "report";
                    Out($"allocprof: {RuntimeInstrumentation.ToggleAllocProfiling(arg)}");
                    return;
                }
                case "nostream":
                {
                    string arg = parts.Length >= 2 ? parts[1] : "off";
                    string state = Patches.NoStreamOnServer.Toggle(arg);
                    Out($"nostream: {state} (segments skipped so far: {Patches.NoStreamOnServer.Blocked})");
                    return;
                }
                case "livedemand":
                {
                    string arg = parts.Length >= 2 ? parts[1] : "report";
                    Out($"livedemand: {Patches.LiveObjectDemand.Toggle(arg)}");
                    return;
                }
                case "htrim":
                {
                    string arg = parts.Length >= 2 ? string.Join(",", parts, 1, parts.Length - 1) : null;
                    if (arg == null) { Out($"htrim: valid names = {string.Join(",", Patches.HeadlessTrim.All)} (also 'all' / 'off')"); return; }
                    Out($"htrim: now {Patches.HeadlessTrim.Configure(arg)}");
                    return;
                }
                case "simprof":
                {
                    float spSecs = 20f;
                    if (parts.Length >= 2) float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out spSecs);
                    Patches.SimProfiler.Start(spSecs);
                    Out($"simprof: profiling vanilla per-frame methods for {spSecs:0}s (results -> [SimProf] in the log)");
                    return;
                }
                case "orbit":
                {
                    // Full-throttle circle. autofly holds ONE heading, which an interpolator
                    // extrapolates perfectly — it hides the very defect we are measuring.
                    float obSecs = 30f, obPeriod = 4f;
                    if (parts.Length >= 2) float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out obSecs);
                    if (parts.Length >= 3) float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out obPeriod);
                    session.ArmOrbit(obSecs, obPeriod);
                    Out($"orbit: {obSecs:0}s at full throttle, one lap per {obPeriod:0.0}s");
                    return;
                }
                case "shipdelay":
                {
                    // Live A/B on the SHIP playout ceiling (compiled default 120ms). The measured
                    // defect is saturation at that cap, so this is the knob that tests the fix
                    // without a rebuild-release-restart cycle. "shipdelay auto" restores default.
                    if (parts.Length >= 2 && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float sdMs) && sdMs > 0f)
                        Sync.AdaptiveSnapshotTiming.ShipCeilingOverride = Mathf.Clamp(sdMs, 30f, 600f) / 1000f;
                    else Sync.AdaptiveSnapshotTiming.ShipCeilingOverride = 0f;
                    Out(Sync.AdaptiveSnapshotTiming.ShipCeilingOverride > 0f
                        ? $"shipdelay: ship playout ceiling = {Sync.AdaptiveSnapshotTiming.ShipCeilingOverride * 1000f:0}ms"
                        : "shipdelay: auto (compiled 120ms ceiling)");
                    return;
                }
                case "shipsmooth":
                {
                    // rendersmooth, but for another PLAYER's ship: samples the DRAWN pose every
                    // render frame. This is the one that answers "how does my movement look on
                    // your screen" - CV and stall% are the shape of the streamed motion, which
                    // buffer counters alone cannot show.
                    if (parts.Length < 2 || !byte.TryParse(parts[1], out byte ssSlot))
                    { Out("shipsmooth <slot> [secs]"); return; }
                    if (!ShipSync.ShipsBySlot.TryGetValue(ssSlot, out var ssShip) || ssShip == null)
                    { Out($"shipsmooth: no ship known for slot {ssSlot}"); return; }
                    float ssSecs = 10f;
                    if (parts.Length >= 3) float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out ssSecs);
                    Out($"shipsmooth: sampling slot {ssSlot} for {ssSecs:0.0}s...");
                    session.StartCoroutine(RenderSmooth(ssSlot, ssShip.transform, Mathf.Clamp(ssSecs, 2f, 120f), $"slot {ssSlot}"));
                    return;
                }
                case "rendersmooth":
                {
                    if (parts.Length < 2 || !int.TryParse(parts[1], out int rsId)) { Out("rendersmooth <netId> [secs]"); return; }
                    float rsSecs = 5f;
                    if (parts.Length >= 3) float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out rsSecs);
                    if (!NetIds.TryGetInstanceId(rsId, out int rsInst)) { Out($"rendersmooth: netId {rsId} unknown here"); return; }
                    var rsEgm = ServiceLocator.Get<EntityGameObjectManager>();
                    if (rsEgm == null || !rsEgm.TryGetSavableEntity(rsInst, out var rsSe) || rsSe == null)
                    { Out($"rendersmooth: #{rsId} not instantiated here"); return; }
                    Out($"rendersmooth: sampling DRAWN pose of #{rsId} for {rsSecs:0.0}s...");
                    session.StartCoroutine(RenderSmooth(rsId, rsSe.transform, Mathf.Clamp(rsSecs, 1f, 20f)));
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
                case "breakables":
                {
                    // Health-based breakables (fiber plants etc.) are generation-only — never in
                    // the spawnable roster and invisible to `entities` (units only). List nearby
                    // ones with netIds so a scenario can tp to one and `fire at` it (the
                    // Health-damage routing test path).
                    var bship = ShipSync.LocalShip;
                    if (bship == null) { Out("breakables: no local ship"); return; }
                    float bradius = 60f;
                    if (parts.Length >= 2)
                        float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out bradius);
                    UnityEngine.Vector2 borigin = bship.transform.position;
                    var bem = ServiceLocator.Get<EntityManager>();
                    var begm = ServiceLocator.Get<EntityGameObjectManager>();
                    int blisted = 0;
                    foreach (var data in bem.GetAllEntities())
                    {
                        if (data == null) continue;
                        UnityEngine.Vector2 bpos = data.position;
                        float bdist = UnityEngine.Vector2.Distance(borigin, bpos);
                        if (bdist > bradius) continue;
                        if (!NetIds.TryGetNetId(data.instanceId, out int bnetId)) continue;
                        if (!begm.TryGetSavableEntity(data.instanceId, out var bse) || bse == null) continue;
                        if (bse.GetComponent<Unit>() != null) continue;
                        var bhb = bse.GetComponent<Health>();
                        if (bhb == null) continue;
                        if (++blisted > 20) { Out("breakables: ...truncated at 20"); break; }
                        Out($"breakable #{bnetId} {data.entityId} pos={bpos.x:0.0},{bpos.y:0.0} dist={bdist:0.0} " +
                            $"hp={bhb.CurrentHealth:0.#}/{bhb.MaxHealth:0.#} owner={(EnemySync.OwnerOf(bnetId) == 255 ? "dormant" : "P" + (EnemySync.OwnerOf(bnetId) + 1))}");
                    }
                    if (blisted == 0) Out($"breakables: none within {bradius:0}");
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
                        $"host={session.IsHost} admin={session.IsSessionAdmin} ship={pos}{dead} " +
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
                    _fireTargetNetId = 0; _fireTargetSlot = -1; _fireDir = Vector2.zero;
                    if (parts.Length >= argAt + 2 && parts[argAt].Equals("player", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(parts[argAt + 1], out int aimSlot))
                        _fireTargetSlot = aimSlot;
                    else if (parts.Length >= argAt + 2 && parts[argAt].Equals("at", StringComparison.OrdinalIgnoreCase))
                        int.TryParse(parts[argAt + 1], out _fireTargetNetId);
                    else if (parts.Length >= argAt + 3 && parts[argAt].Equals("dir", StringComparison.OrdinalIgnoreCase)
                        && float.TryParse(parts[argAt + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float dx)
                        && float.TryParse(parts[argAt + 2], NumberStyles.Float, CultureInfo.InvariantCulture, out float dy))
                        _fireDir = new Vector2(dx, dy).normalized;
                    // Aiming at a PLAYER must not go through the ship's Shooter. A Shooter is an
                    // auto-turret: it picks its own target and re-aims the barrels every frame, so
                    // `fire ... player N` quietly became "fire at whatever the turret already
                    // wanted". Measured 2026-07-29 with the target staged 13.9 units away on a
                    // completely clear line: every logged impact was an Enemy_Larva off in the
                    // opposite direction. Pull the weapon's trigger directly instead, which leaves
                    // the aim to the Aimer feed below.
                    _fireShooter = _fireTargetSlot >= 0 ? null : FindShooter(ship, fireSec);
                    _fireWeapon = _fireShooter == null ? (fireSec ? ship.SecondaryWeapon : ship.PrimaryWeapon) : null;
                    if (_fireShooter == null && _fireWeapon == null) { Out("fire: no shooter/weapon on ship"); return; }
                    _fireBarrels = ship.GetComponentsInChildren<BarrelTransform>(true);
                    _fireAimers = ship.GetComponentsInChildren<Aimer>(true);
                    _fireUntil = Time.unscaledTime + fireSecs;
                    Out($"fire: {fireSecs:0.0}s{(fireSec ? " SECONDARY" : "")} via {(_fireShooter != null ? "Shooter" : "weapon trigger")}" +
                        (_fireTargetSlot >= 0 ? $" at P{_fireTargetSlot + 1}"
                            : _fireTargetNetId != 0 ? $" at #{_fireTargetNetId}"
                            : _fireDir != Vector2.zero ? $" dir {_fireDir.x:0.00},{_fireDir.y:0.00}" : ""));
                    return;
                }
                case "cellfanout":
                {
                    // Which subscriber to LevelChangeBuffer.CellsChanged is actually costing the
                    // frame. simprof can only blame the publisher (the whole body of
                    // LevelChangeBuffer.Update is one Invoke), which is true and useless.
                    bool on = parts.Length < 2 || !parts[1].Equals("off", StringComparison.OrdinalIgnoreCase);
                    Patches.CellFanoutProfiler.SetEnabled(on);
                    Out($"cellfanout: {(on ? "ON — per-handler breakdown every 10s" : "off")}");
                    return;
                }
                case "shipbars":
                {
                    // What the health/fuel bars above other players' ships would READ, printed from
                    // the same tanks they bind to. The bars themselves are UI and a harness bot runs
                    // -nographics, so the only testable half is the data — and the data is also the
                    // half that actually breaks (a puppet whose tanks stop being fed by ship sync
                    // shows full bars on a ship that is nearly dead).
                    var barSession = NetSession.Instance;
                    if (barSession == null) { Out("shipbars: no session"); return; }
                    foreach (var p in barSession.Players)
                    {
                        if (p == null || !p.Connected || p.IsCoordinator) continue;
                        Ship barShip = p.IsLocal ? ShipSync.LocalShip
                            : (ShipSync.ShipsBySlot.TryGetValue(p.Slot, out var s) ? s : null);
                        if (barShip == null) { Out($"[Bars] P{p.Slot + 1} no ship"); continue; }
                        ReadBarTanks(barShip, out float hp, out float hpMax, out float fuel, out float fuelMax);
                        Out($"[Bars] P{p.Slot + 1}{(p.IsLocal ? " (local)" : "")} " +
                            $"hp={hp:0.##}/{hpMax:0.##} fuel={fuel:0.##}/{fuelMax:0.##} " +
                            $"dead={barShip.IsDead}");
                    }
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
                    // Typeless Damage through TakeDamage — the ROUTED path: puppets forward a
                    // damage request to the owner, dormant targets queue a claim (wake-on-hit),
                    // owned targets apply locally. Exactly what a projectile hit exercises,
                    // minus the projectile. Health-based breakables (fiber plants) route the
                    // same way since v0.1.127; their damageConditions may reject typeless hits.
                    if (dr != null) dr.TakeDamage(new Damage(amount, null));
                    else
                    {
                        var hb = se.GetComponent<Health>();
                        if (hb == null) { Out($"poke: #{netId} not damagable"); return; }
                        hb.TakeDamage(new Damage(amount, null));
                    }
                    Out($"poke: #{netId} hit for {amount:0.#} (owner=" +
                        $"{(EnemySync.OwnerOf(netId) == 255 ? "dormant" : "P" + (EnemySync.OwnerOf(netId) + 1))})");
                    return;
                }
                case "contenthash":
                {
                    // Hash a directory and print the set digest plus every per-file digest.
                    //
                    // This is the highest-value check in the whole content feature and it needs no
                    // session at all: run it over the SAME files on a Windows client and on the
                    // Wine/Linux server and the two outputs must be byte-identical strings. That
                    // proves the separator, case, ordering and encoding rules agree across
                    // platforms in seconds, which is the one thing that, if wrong, makes clients
                    // re-download forever or accept content that does not match.
                    string dir = parts.Length > 1
                        ? string.Join(" ", parts, 1, parts.Length - 1)
                        : (NetConfig.ContentRoot != null ? NetConfig.ContentRoot.Value : "");
                    if (string.IsNullOrWhiteSpace(dir)) { Out("contenthash: usage `contenthash <dir>`"); return; }
                    if (!Path.IsPathRooted(dir)) dir = Path.Combine(ModFolder.Dir, dir);
                    if (!Directory.Exists(dir)) { Out($"contenthash: no such directory: {dir}"); return; }

                    var list = Content.ContentStore.ScanDirectory(
                        dir, (long)NetConfig.ContentMaxFileMB.Value * 1024 * 1024, out var skippedFiles);
                    var setHash = Content.ContentHash.SetDigest(list);
                    Out($"contenthash: files={list.Count} set={Content.ContentHash.ToHex(setHash)}");
                    foreach (var e in list)
                        Out($"contenthash:   {Content.ContentHash.ToHex(e.Digest)} {e.Length} {e.Path}");
                    foreach (var problem in Content.ContentHash.Validate(list))
                        Out($"contenthash:   UNPUBLISHABLE {problem}");
                    foreach (var sk in skippedFiles) Out($"contenthash:   skipped {sk}");
                    return;
                }
                case "screenshot":
                {
                    // The game photographs its own frame. Every Win32 route to this either reads a
                    // screen REGION (which captures whatever else is on the desktop -- once, a
                    // Zoom call) or asks a D3D window to redraw into a DC, which Unity answers
                    // with black. ScreenCapture has neither problem: it is the framebuffer, so it
                    // contains the game and nothing else, needs no focus, and includes the IMGUI
                    // layer our modals draw in.
                    string shotDir = Path.Combine(ModFolder.Dir, "screenshots");
                    Directory.CreateDirectory(shotDir);
                    string name = parts.Length > 1 ? parts[1] : "shot";
                    foreach (var bad in Path.GetInvalidFileNameChars()) name = name.Replace(bad, '_');
                    string file = Path.Combine(shotDir, name + ".png");
                    try { if (File.Exists(file)) File.Delete(file); } catch { }
                    // Written asynchronously, at the end of the current frame — the caller has to
                    // wait for the file to appear rather than assume it is there on return.
                    ScreenCapture.CaptureScreenshot(file);
                    Out($"screenshot: capturing to {file}");
                    return;
                }
                case "classes":
                {
                    // The selectable ship classes, as the loadout screen would offer them. This is
                    // the co-op half of the content feature: a joiner must be able to CHOOSE the
                    // host's custom classes, which is a different claim from "the modules are
                    // registered" and was never checked by anything.
                    int pools = 0, total = 0, custom = 0;
                    // Match on the names WEAPONFORGE gave its classes, not on a prefix we assume.
                    // A forge class is named by the pack author; the first version of this matched
                    // "FORGE-" and reported 0 custom for a pack whose classes were all present.
                    var forgeClasses = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    Content.ForgeBridge.CollectForgeLoadoutNames(forgeClasses);
                    foreach (var pool in Resources.FindObjectsOfTypeAll<LoadoutPool>())
                    {
                        if (pool == null || pool.loadouts == null) continue;
                        pools++;
                        foreach (var lt in pool.loadouts)
                        {
                            if (lt == null) continue;
                            total++;
                            // A class is "custom" when any module it installs came from the
                            // content mod -- the class itself carries no marker.
                            bool isCustom = false;
                            try { isCustom = lt.name != null && forgeClasses.Contains(lt.name); }
                            catch { }
                            if (isCustom) custom++;
                            Out($"classes:   {(isCustom ? "*" : " ")} {lt.name}");
                        }
                    }
                    Out($"classes: pools={pools} loadouts={total} custom={custom} " +
                        $"(WeaponForge offers {forgeClasses.Count})");
                    return;
                }
                case "tpshop":
                {
                    // Land on a SHOP, because a shop is the one place in the world the mode itself
                    // guarantees is survivable: BattleRoyaleHost.ClearHazardsAroundStations already
                    // scrubs damaging terrain around every station, precisely so a spawn is not a
                    // coin toss.
                    //
                    // Written after a long run of tests staged in "open ground" chosen by
                    // arithmetic -- the midpoint between two spawns -- which turned out to contain,
                    // in successive runs, a station turret, a Floater, and a hazard cell that
                    // killed the ship before a shot was fired. Every one of those was read as a
                    // weapon result first. A known-good landing pad removes the entire class.
                    //
                    // `tpshop [index] [offset]` -- index picks among the shops (default nearest),
                    // offset nudges sideways so two ships do not land inside each other.
                    var myShip = ShipSync.LocalShip;
                    if (myShip == null) { Out("tpshop: no local ship"); return; }
                    var em = ServiceLocator.Get<EntityManager>();
                    if (em == null) { Out("tpshop: no EntityManager"); return; }

                    var pads = new System.Collections.Generic.List<Vector2>();
                    foreach (var st in em.GetEntitiesWithComponent<Station.Data>())
                        if (st?.entity != null) pads.Add((Vector2)st.entity.position);
                    if (pads.Count == 0) { Out("tpshop: no stations in this world"); return; }

                    int idx = -1;
                    if (parts.Length > 1) int.TryParse(parts[1], out idx);
                    float off = 0f;
                    if (parts.Length > 2) float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out off);

                    Vector2 here = myShip.transform.position;
                    Vector2 pad;
                    if (idx >= 0 && idx < pads.Count) pad = pads[idx];
                    else
                    {
                        pad = pads[0];
                        float best = float.MaxValue;
                        for (int i = 0; i < pads.Count; i++)
                        {
                            float d = (pads[i] - here).sqrMagnitude;
                            if (d < best) { best = d; pad = pads[i]; }
                        }
                    }

                    Vector2 dest = pad + new Vector2(off, 0f);
                    myShip.Unit.ComponentData.entity.MoveTo(dest);
                    var rb2 = myShip.GetComponent<Rigidbody2D>();
                    if (rb2 != null) { RemoteEntityPuppet.TeleportWithChildren(rb2, dest); rb2.linearVelocity = Vector2.zero; }
                    myShip.transform.position = dest;
                    Out($"tpshop: -> shop at {dest.x:0.0},{dest.y:0.0} (of {pads.Count} station(s))");
                    return;
                }
                case "shipcolliders":
                {
                    // Exactly what a ship presents to a physics cast: layer, trigger flag, size.
                    // Written because three separate "fixes" to beam-vs-ship were reasoned out
                    // from assumptions about this and every one of them was wrong -- a hull
                    // measured 20.39u, a hiding layer turned out to be inside the search mask, and
                    // a filter that looked correct silently disabled the whole guard.
                    //
                    // Physics2D.queriesHitTriggers matters as much as the colliders: if it is
                    // false, a cast cannot hit a trigger collider AT ALL, so a ship whose
                    // damageable surface is a trigger is unhittable by any hitscan weapon no
                    // matter what the layer mask says.
                    Out($"shipcolliders: queriesHitTriggers={Physics2D.queriesHitTriggers}");
                    foreach (var kv in ShipSync.ShipsBySlot)
                    {
                        var sh = kv.Value;
                        if (sh == null) continue;
                        bool pup = sh.GetComponent<RemotePuppet>() != null;
                        Vector2 centre = sh.transform.position;
                        int n = 0;
                        foreach (var col in sh.GetComponentsInChildren<Collider2D>(true))
                        {
                            if (col == null) continue;
                            var b = col.bounds;
                            float d = Vector2.Distance(centre, new Vector2(b.max.x, b.max.y));
                            Out($"shipcolliders:   P{kv.Key + 1}{(pup ? "(puppet)" : "(local)")} " +
                                $"{col.GetType().Name} '{col.name}' layer={col.gameObject.layer} " +
                                $"trigger={col.isTrigger} enabled={col.enabled} " +
                                $"size={b.size.x:0.##}x{b.size.y:0.##} distFromCentre={d:0.##}");
                            n++;
                        }
                        Out($"shipcolliders: P{kv.Key + 1} has {n} collider(s)");
                    }
                    if (ShipSync.ShipsBySlot.Count == 0) Out("shipcolliders: no ships");
                    return;
                }
                case "clearmobs":
                {
                    // Empty a radius of HOSTILES around the local ship, so a weapon test measures
                    // the weapon and nothing else. `pvpstage` clears terrain; it does not clear
                    // the things that shoot back, and open ground is full of them -- a staged run
                    // died to `Floater SoldierPurple` before a single shot was fired.
                    //
                    // Reuses BattleRoyale.IsHostileEntityId rather than inventing a second
                    // opinion about what counts as hostile: two definitions would drift and the
                    // harness would quietly stop clearing something that still shoots.
                    float radius = 120f;
                    if (parts.Length > 1) float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out radius);
                    var myShip = ShipSync.LocalShip;
                    if (myShip == null) { Out("clearmobs: no local ship"); return; }
                    var em = ServiceLocator.Get<EntityManager>();
                    if (em == null) { Out("clearmobs: no EntityManager"); return; }

                    Vector2 centre = myShip.transform.position;
                    float r2 = radius * radius;

                    // Never remove a SHIP. Collect first, remove second: removal destroys entity
                    // data, and mutating the manager's collection mid-enumeration throws halfway.
                    var shipInstances = new System.Collections.Generic.HashSet<int>();
                    foreach (var kv in ShipSync.ShipsBySlot)
                    {
                        var se = kv.Value != null ? kv.Value.GetComponentInChildren<SavableEntity>() : null;
                        if (se != null && se.EntityData != null) shipInstances.Add(se.EntityData.instanceId);
                    }

                    var doomed = new System.Collections.Generic.List<int>();
                    foreach (var data in em.GetAllEntities())
                    {
                        if (data == null || shipInstances.Contains(data.instanceId)) continue;
                        if (!Modes.BattleRoyale.IsHostileEntityId(data.entityId)) continue;
                        if (((Vector2)data.position - centre).sqrMagnitude > r2) continue;
                        if (NetIds.TryGetNetId(data.instanceId, out int netId)) doomed.Add(netId);
                    }
                    foreach (int netId in doomed) Sync.EnemySync.RemoveSilently(netId);
                    Out($"clearmobs: removed {doomed.Count} hostile(s) within {radius:0} units");
                    return;
                }
                case "hpsnap":
                {
                    // Per-ship health, for a harness that needs to know WHO took damage rather
                    // than merely that damage happened. Deliberately reads the same tank
                    // UI/ShipStatusBars binds, so a number here is the number a player sees.
                    //
                    // Every ship is listed, but only the LOCAL line is authoritative: a puppet's
                    // tank is whatever the last snapshot carried, which lags. A harness should ask
                    // each machine about itself.
                    foreach (var kv in ShipSync.ShipsBySlot)
                    {
                        var sh = kv.Value;
                        if (sh == null) continue;
                        ReadBarTanks(sh, out float hp, out float hpMax, out _, out _);
                        bool puppet = sh.GetComponent<RemotePuppet>() != null;
                        Out($"hpsnap: P{kv.Key + 1} hp={hp:0.###}/{hpMax:0.###} {(puppet ? "puppet" : "local")}");
                    }
                    if (ShipSync.ShipsBySlot.Count == 0) Out("hpsnap: no ships");
                    return;
                }
                case "hpfull":
                {
                    // Refill the LOCAL ship's health so a weapon matrix can start each weapon from
                    // a known state. Without it the second weapon in a run measures damage against
                    // whatever the first one left behind, and the fourth kills the target outright.
                    int healed = 0;
                    foreach (var kv in ShipSync.ShipsBySlot)
                    {
                        var sh = kv.Value;
                        if (sh == null || sh.GetComponent<RemotePuppet>() != null) continue;
                        try
                        {
                            var unit = sh.GetComponentInParent<Unit>() ?? sh.GetComponent<Unit>();
                            var dr = unit != null ? unit.GetComponent<DamagableResource>() : null;
                            if (dr?.Tank == null) continue;
                            float missing = dr.Tank.Capacity - dr.Tank.Value;
                            if (missing > 0f) dr.Tank.Charge(missing);
                            healed++;
                            Out($"hpfull: P{kv.Key + 1} hp={dr.Tank.Value:0.###}/{dr.Tank.Capacity:0.###}");
                        }
                        catch (Exception e) { Out($"hpfull: P{kv.Key + 1} FAILED: {e.Message}"); }
                    }
                    if (healed == 0) Out("hpfull: no local ship");
                    return;
                }
                case "weaponlist":
                {
                    // Every equippable weapon module, so a harness can iterate them instead of
                    // being handed a hardcoded list that goes stale the moment content changes.
                    // `weaponlist forge` narrows it to content-mod weapons.
                    bool forgeOnly = parts.Length > 1 && parts[1].Equals("forge", StringComparison.OrdinalIgnoreCase);
                    var forgeIds = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var forgeWeapons = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    Content.ForgeBridge.CollectForgeIds(forgeIds, forgeWeapons);

                    var reg = ServiceLocator.Get<ModuleRegistry>();
                    if (reg == null) { Out("weaponlist: no ModuleRegistry"); return; }
                    int n = 0;
                    foreach (var m in reg.AllItems)
                    {
                        if (!(m is WeaponModuleData)) continue;
                        string id = null;
                        try { id = m.Id; } catch { }
                        if (id == null) continue;
                        bool custom = forgeIds.Contains(id);
                        if (forgeOnly && !custom) continue;
                        Out($"weaponlist:   {(custom ? "*" : " ")} {id} | {m.displayName}");
                        n++;
                    }
                    Out($"weaponlist: {n} weapon module(s){(forgeOnly ? " (custom only)" : "")}");
                    return;
                }
                case "brpools":
                {
                    // The BR drop pools, ordered-fingerprinted. This is the instrument for the
                    // claim the whole content feature exists to protect: every machine must roll
                    // from the SAME ordered pool, since selection is by index and contested loot
                    // is matched by ordinal. Asking directly beats inferring it from drop logs,
                    // which only appear if something dropped -- a short match proved nothing.
                    var fp = Modes.BattleRoyaleLootTables.PoolFingerprint(out int w, out int c, out int custom);
                    Out($"brpools: white={w} coloured={c} custom={custom} fingerprint={fp}");
                    return;
                }
                case "contentcancel":
                {
                    // Exactly what the modal's CANCEL AND LEAVE button does, so the headless
                    // harness exercises the real path rather than a parallel one — and so a
                    // player on a support call can be walked through it without a mouse.
                    var s = NetSession.Instance;
                    Out($"contentcancel: state={Content.ContentSync.LocalState} pct={Content.ContentSync.LocalPercent}");
                    Content.ContentSync.CancelLocal(s);
                    return;
                }
                case "contentstat":
                {
                    Out($"contentstat: local={Content.ContentSync.LocalState} " +
                        $"pct={Content.ContentSync.LocalPercent} " +
                        $"bytes={Content.ContentSync.BytesDone}/{Content.ContentSync.BytesNeeded} " +
                        $"set={Content.ContentHash.ToHex(Content.ContentSync.LocalSetHash)} " +
                        $"active={Content.ContentSync.ActiveContentPath ?? "none"}");
                    var host = NetSession.Instance;
                    if (host != null && host.IsHost)
                        for (byte i = 0; i < 4; i++)
                            if (host.Players[i] != null && !host.Players[i].IsLocal)
                                Out($"contentstat:   P{i + 1} {Content.ContentSync.StateOf(i)} " +
                                    $"{Content.ContentSync.PercentOf(i)}%");
                    // The ROSTER view — what this machine would draw in the lobby for everyone
                    // else. On a client this is the only source there is, and it is the thing
                    // that turns "NOT READY" into "SYNCING 42%". Printed on the host too, so the
                    // two views can be compared when they disagree.
                    if (host != null)
                        for (byte i = 0; i < 4; i++)
                        {
                            var pl = host.Players[i];
                            if (pl == null) continue;
                            Out($"contentstat: roster P{i + 1} {(Content.ContentState)pl.ContentState} " +
                                $"{pl.ContentPercent}%{(pl.IsLocal ? " (me)" : "")}");
                        }
                    return;
                }
                case "dumpsprites":
                {
                    // Write real PUNK sprites out as PNG, so custom art can be generated against
                    // the game's actual palette and line weight instead of a guess at its style.
                    //
                    // The textures are in memory but not readable — Unity uploads sprite atlases
                    // to the GPU with CPU access stripped, so texture.GetPixels() throws. The way
                    // round it is a Blit into a temporary RenderTexture and a ReadPixels back,
                    // which is how any runtime sprite export has to work.
                    //
                    // `dumpsprites [filter] [max]` — filter matches the sprite name, default 40.
                    string filter = parts.Length > 1 ? parts[1] : "";
                    int max = parts.Length > 2 && int.TryParse(parts[2], out var m) ? m : 40;
                    string dir = Path.Combine(ModFolder.Dir, "spritedump");
                    Directory.CreateDirectory(dir);
                    int written = 0, skipped = 0;
                    var seen = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
                    foreach (var sp in Resources.FindObjectsOfTypeAll<Sprite>())
                    {
                        if (written >= max) break;
                        if (sp == null || sp.texture == null) continue;
                        if (filter.Length > 0 && sp.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (!seen.Add(sp.name)) continue;
                        try
                        {
                            var r = sp.textureRect;
                            int w = Mathf.Max(1, (int)r.width), h = Mathf.Max(1, (int)r.height);
                            if (w > 512 || h > 512) { skipped++; continue; }   // atlases, not sprites
                            var rt = RenderTexture.GetTemporary(sp.texture.width, sp.texture.height,
                                0, RenderTextureFormat.ARGB32);
                            Graphics.Blit(sp.texture, rt);
                            var prev = RenderTexture.active;
                            RenderTexture.active = rt;
                            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                            tex.ReadPixels(new Rect(r.x, r.y, w, h), 0, 0);
                            tex.Apply();
                            RenderTexture.active = prev;
                            RenderTexture.ReleaseTemporary(rt);
                            var safe = string.Join("_", sp.name.Split(Path.GetInvalidFileNameChars()));
                            File.WriteAllBytes(Path.Combine(dir, safe + ".png"), tex.EncodeToPNG());
                            UnityEngine.Object.Destroy(tex);
                            written++;
                        }
                        catch { skipped++; }
                    }
                    Out($"dumpsprites: wrote {written} sprite(s) to {dir} (skipped {skipped})");
                    return;
                }
                case "forgeids":
                {
                    // What ForgeDiag believes is a custom weapon, and what the ship is actually
                    // holding. A shot that logs nothing is either "the id sets are empty" or
                    // "the weapon we fired is not in them", and those have opposite fixes —
                    // this prints both sides so it is one look rather than a guess.
                    var mods = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
                    var weps = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
                    Content.ForgeBridge.CollectForgeIds(mods, weps);
                    Out($"forgeids: modules={mods.Count} weapons={weps.Count}");
                    foreach (var m in mods) Out($"forgeids:   module  {m}");
                    foreach (var w in weps) Out($"forgeids:   weapon  {w}");
                    var s = ShipSync.LocalShip;
                    if (s == null) { Out("forgeids: no local ship"); return; }
                    var fgrid = s.ModuleGridOwner != null ? s.ModuleGridOwner.ModuleGrid as ModuleGrid : null;
                    foreach (var (label, w, pos) in new[]
                             {
                                 ("primary", s.PrimaryWeapon, ModuleGrid.PrimaryWeaponGridPosition),
                                 ("secondary", s.SecondaryWeapon, ModuleGrid.SecondaryWeaponGridPosition),
                             })
                    {
                        // Weapon id AND module id. They disagree for a content-mod weapon: the
                        // clone keeps the template's weapon id while the module id is namespaced,
                        // and the module id is the one that is actually unique and that travels
                        // in ModuleGridSync — so it is the one worth trusting.
                        string wid = null;
                        if (w != null)
                        {
                            try { wid = w.TemplateData?.Id; } catch { }
                            if (string.IsNullOrEmpty(wid)) wid = w.TemplateData?.name;
                        }
                        string mid = null;
                        try { mid = fgrid?[pos]?.Data?.Id; } catch { }
                        Out($"forgeids: {label} weapon='{wid ?? "none"}' module='{mid ?? "none"}' " +
                            $"custom={(mid != null && mods.Contains(mid))}");
                    }
                    return;
                }
                case "moduledigest":
                {
                    // The fingerprint the go-live barrier compares. Printing it on demand turns
                    // "why was my run refused" into a one-line diff between two machines.
                    var snap = DeterminismAudit.CaptureModules(log: false);
                    Out($"moduledigest: modules={snap.Count} digest={snap.Digest:X16}");
                    return;
                }
                case "modulefake":
                {
                    // Register a module that exists on THIS machine only, which is precisely what
                    // a content mod does when its weapon set differs from the host's. Exists so
                    // the go-live barrier's refusal path can be tested without installing a
                    // content mod on one machine and not another — the barrier is the one thing
                    // here that must never silently pass.
                    if (parts.Length < 2) { Out("modulefake: usage `modulefake <id>`"); return; }
                    var reg = ServiceLocator.Get<ModuleRegistry>();
                    if (reg == null) { Out("modulefake: no ModuleRegistry"); return; }
                    var listField = AccessTools.Field(
                        typeof(ScriptableObjectRegistry<ModuleData, string>), "itemList");
                    var list = listField?.GetValue(reg) as System.Collections.IList;
                    if (list == null) { Out("modulefake: itemList not reachable"); return; }
                    // CLONE a registered module rather than CreateInstance a blank one. A blank
                    // ModuleData has a null connectionCountDistribution, and Distribution.Draw
                    // throws UnityException("Can't draw an item from an empty distribution!") the
                    // moment anything deep-copies it — which took the client out of the run
                    // entirely and back to the menu, so the test proved nothing. Cloning also
                    // matches what a content mod actually does (WeaponForge instantiates existing
                    // assets and overrides fields), so the divergence being staged is a faithful one.
                    ModuleData template = null;
                    foreach (var m in reg.AllItems) { if (m != null) { template = m; break; } }
                    if (template == null) { Out("modulefake: registry is empty, nothing to clone"); return; }
                    var fake = ScriptableObject.Instantiate(template);
                    fake.name = "Module_" + parts[1];
                    fake.hideFlags = HideFlags.HideAndDontSave;
                    AccessTools.Field(typeof(ModuleData), "id")?.SetValue(fake, parts[1]);
                    list.Add(fake);
                    reg.Initialize();   // rebuild id -> module, same as the game does after a load
                    var after = DeterminismAudit.CaptureModules(log: false);
                    Out($"modulefake: added '{parts[1]}' — modules={after.Count} digest={after.Digest:X16}");
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
                case "servercode":
                {
                    // Print + copy the SteamServer join code to share with a remote friend.
                    ulong code = session.SteamServerCode;
                    if (code == 0) { Out("servercode: not a SteamServer session"); return; }
                    try { UnityEngine.GUIUtility.systemCopyBuffer = code.ToString(); } catch { }
                    Out($"servercode: {code} (copied to clipboard — friend pastes into Join)");
                    return;
                }
                case "join":
                {
                    // Harness: drive a join to an explicit address/code (loopback "ip:port",
                    // SteamID64 for Steam/SteamServer). Lets a test feed a SteamServer coordinator's
                    // id (from coordinator-steamid.txt) without a lobby/clipboard.
                    if (parts.Length < 2) { Out("join <address|steamid64>"); return; }
                    session.JoinByCode(parts[1]);
                    Out($"join: dialing {parts[1]} (transport {session.ResolvedTransport})");
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
                case "desync":
                {
                    // WS9.1 heal harness: deliberately break THIS machine's world. "desync drop"
                    // swallows the next incoming spawn replica — the summary/heal pipeline must
                    // then detect the divergence and repair it in bounded time.
                    if (parts.Length >= 2 && parts[1].Equals("drop", StringComparison.OrdinalIgnoreCase))
                    {
                        Sync.MinionSync.DropNextReplica = true;
                        Out("desync: next incoming spawn replica will be DROPPED (one-shot)");
                    }
                    else if (parts.Length >= 2 && parts[1].Equals("dropkill", StringComparison.OrdinalIgnoreCase))
                    {
                        Sync.EnemySync.DropNextKill = true;
                        Out("desync: next incoming entity kill will be DROPPED (one-shot ghost)");
                    }
                    else Out($"desync: dropArmed={Sync.MinionSync.DropNextReplica} " +
                             $"dropkillArmed={Sync.EnemySync.DropNextKill} (usage: desync drop|dropkill)");
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
            private static readonly FieldInfo ShowActionF = AccessTools.Field(typeof(DebugMenu), "showDebugInputAction");
            private static readonly MethodInfo SetHoverM = AccessTools.Method(typeof(DebugMenu), "SetShipsHovering");
            private static readonly FieldInfo SecondaryF = AccessTools.Field(typeof(DebugMenu), "secondaryMenu");
            private static readonly FieldInfo EnemyListF = AccessTools.Field(typeof(DebugMenu), "enemyList");
            private static readonly FieldInfo TimeManagerF = AccessTools.Field(typeof(DebugMenu), "timeManager");
            private static readonly FieldInfo ScreenCanvasF = AccessTools.Field(typeof(UIScreen), "canvas");
            private static readonly MethodInfo RemoveModifiersM =
                AccessTools.Method(AccessTools.TypeByName("TimeManager"), "RemoveAllModifiers");
            private static bool _warned;

            // The PLAYTEST BUILD ships this action live: vanilla F1 disables ship control, sets
            // ships hovering (reads as "F1 does something with the camera"), drops timescale to
            // 0.1x and opens the dev screen — for EVERY player, regardless of our config (field
            // report 2026-07-23). Kill the vanilla binding outright; the config-gated opener in
            // the Postfix below (which deliberately skips the net-hostile slow-mo) is the only
            // way F1 does anything.
            private static void Prefix(DebugMenu __instance)
            {
                try
                {
                    var action = ShowActionF?.GetValue(__instance) as UnityEngine.InputSystem.InputAction;
                    if (action != null && action.enabled)
                    {
                        action.Disable();
                        Plugin.Log.LogInfo("[Dev] vanilla F1 debug-menu binding disabled (DebugMenuKey config is the only gate)");
                    }
                }
                catch { }
            }

            private static void Postfix(DebugMenu __instance)
            {
                if (NetConfig.DebugMenuKey == null || !NetConfig.DebugMenuKey.Value) return;
                if (IsOpenedF == null) return;
                // Something else may have taken the screen down while our latch still says open —
                // the pause menu does exactly that, and it is the workaround Omar had been using.
                // Left unreconciled the latch stays true (so F1 is dead for the rest of the session)
                // and the ship's control map stays disabled (so he "loses ship control").
                ReconcileClosedElsewhere(__instance);
                var kb = Keyboard.current;
                if (kb == null || !kb.f1Key.wasPressedThisFrame) return;
                // F1 is a TOGGLE. It only ever opened: the second press hit the `already open` guard
                // and returned, and vanilla's own close is bound to a DIFFERENT action
                // (hideDebugInputAction), so nothing closed it. Omar, 2026-07-29: "I can't reclose it
                // pressing F1 again, it gets stuck open."
                if ((bool)IsOpenedF.GetValue(__instance)) { CloseMenu(__instance); return; }
                OpenMenu(__instance);
            }

            /// <summary>The debug screen is not actually on screen, but our latch says it is: give the
            /// ship back and clear the latch so F1 works again.</summary>
            private static void ReconcileClosedElsewhere(DebugMenu menu)
            {
                try
                {
                    if (!(bool)IsOpenedF.GetValue(menu)) return;
                    var screen = ScreenF?.GetValue(menu) as UIScreen;
                    var canvas = screen != null ? ScreenCanvasF?.GetValue(screen) as Canvas : null;
                    if (canvas == null || canvas.enabled) return;   // still up (or unreadable) — leave it
                    IsOpenedF.SetValue(menu, false);
                    RestoreLocalShipControl();
                    Plugin.Log.LogInfo("[Dev] debug menu was closed by another screen — ship control restored, F1 re-armed");
                }
                catch { }
            }

            /// <summary>Vanilla's Close(), made safe for a net run. The latch clear and the control
            /// restore are in a finally: whatever else fails, F1 must keep working and the player
            /// must keep their ship.</summary>
            private static void CloseMenu(DebugMenu menu)
            {
                try
                {
                    try { (SecondaryF?.GetValue(menu) as SecondaryDebugMenu)?.Hide(); } catch { }
                    try { (EnemyListF?.GetValue(menu) as GameObject)?.SetActive(false); } catch { }
                    try { (ScreenF?.GetValue(menu) as UIScreen)?.Close(); } catch { }
                    try { SetHoverM?.Invoke(menu, new object[] { false }); } catch { }
                    // We never set vanilla's 0.1x slow-mo on open, but the menu's own slow-motion
                    // BUTTON can still be armed under this owner — clear it or the world stays slow.
                    try { RemoveModifiersM?.Invoke(TimeManagerF?.GetValue(menu), new object[] { menu }); } catch { }
                }
                finally
                {
                    RestoreLocalShipControl();
                    try { IsOpenedF.SetValue(menu, false); } catch { }
                    Plugin.Log.LogInfo("[Dev] debug menu closed (F1)");
                }
            }

            /// <summary>Only the LOCAL ship — puppets have no input to re-enable, and reaching for
            /// theirs is what threw inside vanilla's loop.</summary>
            private static void RestoreLocalShipControl()
            {
                try
                {
                    var input = Sync.ShipSync.LocalShip != null ? Sync.ShipSync.LocalShip.shipInput : null;
                    if (input != null) input.ShipControlActionMap.Enable();
                }
                catch { }
            }

            private static void OpenMenu(DebugMenu __instance)
            {
                try
                {
                    // NOT ShipManager.DisableShipControl(): in a net run Ships contains PUPPETS,
                    // whose null shipInput NRE'd vanilla's loop mid-iteration. That exception left
                    // this open HALF-APPLIED — isOpened already true (so F1 was dead for the rest
                    // of the session), the local ship's control already off — because the flag was
                    // set first and nothing rolled back (2026-07-29 match log: one "[Dev] F1 debug
                    // menu open failed" and every later F1 press silently ignored). Only the LOCAL
                    // ship's control matters here anyway; puppets have no input to disable.
                    var localInput = Sync.ShipSync.LocalShip != null ? Sync.ShipSync.LocalShip.shipInput : null;
                    if (localInput != null) localInput.ShipControlActionMap.Disable();
                    try
                    {
                        SetHoverM?.Invoke(__instance, new object[] { true });
                        // Deliberately NOT the vanilla SetTimeScale(0.1f) here: local slow-mo while
                        // peers run full speed starves this machine's share of the shared sim.
                        (ScreenF?.GetValue(__instance) as UIScreen)?.Open();
                        (WeaponDropF?.GetValue(__instance) as WeaponDropdown)?.Refresh();
                        // The latch is set LAST: a failure above must leave the menu closable and
                        // reopenable, not wedged "open" with no screen.
                        IsOpenedF.SetValue(__instance, true);
                        Plugin.Log.LogInfo("[Dev] debug menu opened (F1)");
                    }
                    catch
                    {
                        if (localInput != null) localInput.ShipControlActionMap.Enable(); // give the ship back
                        throw;
                    }
                }
                catch (Exception e)
                {
                    if (!_warned) { _warned = true; Plugin.Log.LogWarning($"[Dev] F1 debug menu open failed: {e.Message}"); }
                }
            }
        }
    }
}
