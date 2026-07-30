using HarmonyLib;
using PunkMultiverse.Core;
using UnityEngine;

namespace PunkMultiverse.Patches
{
    /// <summary>
    /// A station whose DATA is unlocked must LOOK unlocked.
    ///
    /// Omar, 2026-07-29: "I spawned at a station that never opened, but I can use the shop teleport
    /// options listed" — functionally unlocked, visually shut. Vanilla opens the hatch on exactly
    /// two paths: the live <c>UpgradeInstalled</c> event (<c>platform.Open</c>) and bind-time
    /// (<c>Bind → OpenImmediate</c>, gated on <c>!skipOpenAnim</c> and
    /// <c>!isFastTravelDestination</c>). Both paths have windows where a replicated unlock can slip
    /// through — an unlock applied mid-bind, a stale <c>skipOpenAnim</c>/<c>isFastTravelDestination</c>
    /// flag left set by a sequence that never ran to completion in a net run — and once missed,
    /// NOTHING in vanilla ever re-checks. The pose is written only at those two moments.
    ///
    /// So this re-checks: a slow sweep (5s) over live stations that compares the truth
    /// (<c>Data.IsUnlocked</c>) with the presentation (the platform animator's <c>IsOpen</c> bool)
    /// and repairs any disagreement with the game's own <c>OpenImmediate()</c> — idempotent, no
    /// animation replay, exactly what Bind itself would have done. The heal also LOGS the two gate
    /// flags at repair time, so every firing is evidence toward the real root cause rather than a
    /// silent cover-up.
    /// </summary>
    internal static class StationVisualHeal
    {
        private static float _nextSweepAt;

        /// <summary>Called every render frame from Toast.Update (the same always-alive host the
        /// drop-screen pen uses); self-throttles to one sweep per 5s.</summary>
        internal static void Tick()
        {
            if (!NetSession.Active || Time.unscaledTime < _nextSweepAt) return;
            _nextSweepAt = Time.unscaledTime + 5f;
            try
            {
                foreach (var station in Object.FindObjectsByType<Station>(FindObjectsSortMode.None))
                {
                    if (station == null) continue;
                    Station.Data data;
                    try { data = station.ComponentData; } catch { continue; } // not bound yet
                    if (data == null || !data.IsUnlocked) continue;

                    var walker = Traverse.Create(station);
                    var platform = walker.Field("platform").GetValue() as StationPlatform;
                    if (platform == null) continue;
                    var animator = Traverse.Create(platform).Field("animator").GetValue() as Animator;
                    if (animator == null || !animator.isActiveAndEnabled) continue;
                    if (animator.GetBool("IsOpen")) continue;   // presentation agrees with the data

                    // Disagreement: unlocked data, closed hatch. Repair with vanilla's own pieces —
                    // the same set Bind's unlocked branch and OnUpgradeInstalled apply.
                    platform.OpenImmediate();
                    try { walker.Method("UpdatePrompt").GetValue(); } catch { }
                    try
                    {
                        var tracking = walker.Field("enemyTrackingSystem").GetValue() as Behaviour;
                        if (tracking != null) tracking.enabled = false;
                        var enemyCollider = walker.Field("enemyCollider").GetValue() as GameObject;
                        if (enemyCollider != null) enemyCollider.SetActive(false);
                    }
                    catch { }
                    Plugin.Log.LogWarning($"[BR] station visual REPAIRED at " +
                        $"({station.transform.position.x:0},{station.transform.position.y:0}) — data was " +
                        $"unlocked but the hatch was closed (skipOpenAnim={data.skipOpenAnim}, " +
                        $"isFastTravelDestination={data.isFastTravelDestination}). These flags are the " +
                        "root-cause evidence; if one is always true here, that path is the bug.");
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"[BR] station visual sweep failed: {e.Message}");
            }
        }
    }
}
