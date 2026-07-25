using PunkMultiverse.Core;
using PunkMultiverse.Sync;
using UnityEngine;

namespace PunkMultiverse.Patches
{
    /// <summary>
    /// Recovery for a hard softlock at run start (tester report 2026-07-24: "the starting station
    /// just refused to open and spit us out — we were stuck staring at it").
    ///
    /// The vanilla opening cinematic is `GameController.PlayStartSequence()`, launched
    /// fire-and-forget (`.Forget()`, so any exception disappears silently). It:
    ///   1. sets `shipInput.enabled = false` on every ship,
    ///   2. freezes every ship (`Rigidbody.bodyType = Static`) inside the station's own sequence,
    ///   3. restores input, camera, crosshair and Dynamic bodies ONLY on its final line.
    ///
    /// Three ways it never reaches that final line, all reachable in a net run:
    ///   - `startStationData` is resolved as "the station with an installed upgrade". If no station
    ///     reports one on this machine, the very next lines dereference null -> NRE -> the task
    ///     dies with input already disabled.
    ///   - `while (startStation == null) await NextFrame()` spins forever if the start station's
    ///     GameObject is not streamed in here.
    ///   - anything else throwing between 1 and 3.
    /// A start station that reads as LOCKED also keeps its `enemyCollider` active (the game only
    /// disables it for unlocked stations), which physically shoves ships off the pad — the
    /// "spit us out" half of the report.
    ///
    /// Single-player can recover by restarting the run; a co-op session cannot (everyone is stuck,
    /// and the run is shared). So: if control has not been restored within a grace window after
    /// go-live, restore it here and log exactly what was found — a stuck run becomes a hiccup, and
    /// the next occurrence names its own cause.
    /// </summary>
    internal static class StartSequenceWatchdog
    {
        // The vanilla sequence is a few seconds of animation plus fades; 25s is far past any
        // legitimate completion, including a slow client still streaming the start area.
        private const float GraceSeconds = 25f;

        private static float _armedAt;
        private static bool _resolved;

        /// <summary>Called at go-live. Starts the grace window.</summary>
        internal static void Arm()
        {
            _armedAt = Time.unscaledTime;
            _resolved = false;
        }

        internal static void Reset()
        {
            _armedAt = 0f;
            _resolved = true;
        }

        /// <summary>Ticked while InGame. Cheap: two field reads until the window expires.</summary>
        internal static void Tick()
        {
            if (_resolved || _armedAt <= 0f) return;
            var ship = ShipSync.LocalShip;
            if (ship == null) return; // ship not spawned yet — keep waiting, don't start the clock over

            // Healthy completion: the cinematic gave control back. Disarm and never touch anything.
            bool inputOn = ship.shipInput != null && ship.shipInput.enabled;
            bool dynamic = ship.Rigidbody == null || ship.Rigidbody.bodyType == RigidbodyType2D.Dynamic;
            if (inputOn && dynamic) { _resolved = true; return; }

            if (Time.unscaledTime - _armedAt < GraceSeconds) return;
            _resolved = true;

            Plugin.Log.LogWarning("[StartSequence] control was never restored " +
                $"{GraceSeconds:0}s after go-live (input={(inputOn ? "on" : "OFF")}, " +
                $"body={(dynamic ? "Dynamic" : "STATIC")}) — the vanilla start cinematic did not " +
                "finish. Recovering; see the station report below for the likely reason.");
            ReportStartStation();

            try
            {
                if (ship.shipInput != null) ship.shipInput.enabled = true;
                if (ship.Rigidbody != null && ship.Rigidbody.bodyType != RigidbodyType2D.Dynamic)
                    ship.Rigidbody.bodyType = RigidbodyType2D.Dynamic;
                if (ship.Crosshair != null) ship.Crosshair.Visible = true;
                ship.UnlockCamera(0.5f);
                ship.SetHeadlightsEnabled(true);
                Plugin.Log.LogInfo("[StartSequence] control restored (input, physics, camera, crosshair)");
                UI.Toast.Show("RUN START RECOVERED - CONTROLS RESTORED", 6f);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"[StartSequence] recovery failed: {e.Message}");
            }
        }

        /// <summary>Name the state the cinematic tripped over: whether any station reports an
        /// installed upgrade (its start-station lookup), and whether the start station's object is
        /// actually streamed in here (its infinite await).</summary>
        private static void ReportStartStation()
        {
            try
            {
                int stationsWithUpgrade = 0, stationDatas = 0;
                var em = ServiceLocator.Get<EntityManager>();
                if (em != null)
                {
                    foreach (var data in em.GetEntitiesWithComponent<Station.Data>())
                    {
                        stationDatas++;
                        if (data != null && data.installedUpgrades != null && data.installedUpgrades.Count > 0)
                            stationsWithUpgrade++;
                    }
                }
                var live = Object.FindObjectsByType<Station>(FindObjectsSortMode.None);
                int unlockedLive = 0;
                foreach (var s in live)
                    if (s != null && s.ComponentData != null && s.ComponentData.IsUnlocked) unlockedLive++;

                Plugin.Log.LogWarning($"[StartSequence] stations: data={stationDatas} " +
                    $"withInstalledUpgrade={stationsWithUpgrade} liveObjects={live.Length} liveUnlocked={unlockedLive}. " +
                    (stationsWithUpgrade == 0
                        ? "NO station reports an installed upgrade -> the cinematic's start-station "
                          + "lookup returned null and threw (this is the softlock's cause)."
                        : live.Length == 0
                            ? "Start station data exists but NO Station object is streamed in here -> "
                              + "the cinematic is still awaiting one."
                            : "Start station data and objects both present -> the sequence failed later."));
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"[StartSequence] station report failed: {e.Message}");
            }
        }
    }
}
