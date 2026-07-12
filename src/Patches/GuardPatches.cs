using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using PunkMultiverse.Core;

namespace PunkMultiverse.Patches
{
    /// <summary>
    /// Net runs must not pollute single-player systems: no leaderboard submissions (modded,
    /// multi-pilot runs) and no suspend-saves (v1 has no save-based resume — live-session rejoin
    /// covers reconnects; a half-written net save would load as a broken solo run).
    /// </summary>
    internal static class GuardPatches
    {
        [HarmonyPatch]
        internal static class NoLeaderboardUploads
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                var t = AccessTools.TypeByName("LeaderboardScoreSubmitter");
                if (t != null)
                    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                        if (m.Name == "UploadScore")
                            yield return m;
            }

            private static bool Prefix()
            {
                if (!NetSession.Active) return true;
                Plugin.Log.LogInfo("[Guard] leaderboard upload blocked (net run)");
                return false;
            }
        }

        // GameController.Restart is the single chokepoint for game-over retry, game-won
        // restart, and pause-menu restart. A vanilla restart would regenerate a world past the
        // netcode's gates — in a net run the HOST's restart becomes a synchronized retry for
        // the whole party, and clients are told to wait.
        [HarmonyPatch(typeof(GameController), "Restart")]
        internal static class NetRunRestart
        {
            private static bool Prefix()
            {
                if (!NetSession.Active) return true;
                var session = NetSession.Instance;
                if (session.IsHost)
                {
                    session.RestartRun();
                    return false;
                }
                UI.Toast.Show("ONLY THE HOST CAN RETRY — WAITING FOR THE HOST", 5f);
                return false;
            }
        }

        // Restart is a synchronized full-party retry — it only makes sense after everyone has
        // died, so it lives on the GAME-OVER screen, and even there only the host's does anything.
        // Hidden for clients on game-over; removed for EVERYONE on the in-run pause menu. Hiding a
        // button would leave a hole in the fixed-position button column, so the remaining buttons
        // are compacted up into the freed slots (LayoutMenuColumn).
        [HarmonyPatch(typeof(GameOverScreen), "OnGameOver")]
        internal static class NetRunGameOverButtons
        {
            private static void Postfix(GameOverScreen __instance)
            {
                try
                {
                    if (!NetSession.Active || NetSession.Instance.IsHost) return; // host/solo: default screen
                    LayoutMenuColumn(__instance, hideRestart: true); // clients: no retry, no gap
                }
                catch { }
            }
        }

        // In a net run the suspend-save is blocked (below) but the run auto-saves through
        // NetRunSave — so the pause menu's "Save & Exit" would lie in both directions. While
        // networking is live it reads just EXIT (localization stripped from that one label). The
        // RESTART button is removed entirely — retry belongs on the game-over screen.
        [HarmonyPatch(typeof(PauseScreen), "Open")]
        internal static class NetRunPauseButtons
        {
            private static void Postfix(PauseScreen __instance)
            {
                try
                {
                    if (!NetSession.Active) return; // single-player pause unchanged
                    LayoutMenuColumn(__instance, hideRestart: true, relabelSaveQuitAsExit: true);
                }
                catch { }
            }
        }

        // ---------------------------------------------------------------- shared menu layout

        private static string _savedSaveQuitLabel;
        // Original slot positions per screen instance, captured once so repeated Opens stay stable.
        private static readonly Dictionary<int, List<UnityEngine.Vector2>> SlotCache
            = new Dictionary<int, List<UnityEngine.Vector2>>();

        /// <summary>Hide the restart button (and optionally relabel Save&amp;Quit → EXIT), then
        /// compact the remaining buttons up so no empty slot is left where a hidden one was. Works
        /// whether the column is a layout group or fixed-position: each visible button is reassigned
        /// to the next original slot from the top.</summary>
        private static void LayoutMenuColumn(UnityEngine.Component root, bool hideRestart, bool relabelSaveQuitAsExit = false)
        {
            // Only buttons the game is CURRENTLY showing occupy real slots. PauseScreen carries five
            // menu buttons (Resume/Restart/Quit/SaveAndQuit/Report) but shows a subset — an inactive
            // one's anchoredPosition is stale/overlapping, so including it would pollute the slot list
            // and leave a visible button parked in a dead slot (the gap where Restart used to be).
            var entries = new List<(UnityEngine.UI.Button btn, UnityEngine.RectTransform rt, bool visible)>();
            foreach (var button in root.GetComponentsInChildren<UnityEngine.UI.Button>(true))
            {
                if (!button.gameObject.activeInHierarchy) continue; // skip buttons the game hid
                string handler = MenuHandler(button);
                if (handler == null) continue; // not a menu button (Resume/Restart/Quit/…)
                if (!(button.transform is UnityEngine.RectTransform rt)) continue;
                if (relabelSaveQuitAsExit && handler == "OnSaveAndQuitButtonClicked") RelabelSaveQuit(button);
                bool visible = !(hideRestart && handler == "OnRestartButtonClicked");
                entries.Add((button, rt, visible));
            }
            if (entries.Count == 0) return;

            int key = root.GetInstanceID();
            if (!SlotCache.TryGetValue(key, out var slots))
            {
                slots = entries.Select(e => e.rt.anchoredPosition).OrderByDescending(p => p.y).ToList();
                SlotCache[key] = slots;
            }

            foreach (var e in entries) e.btn.gameObject.SetActive(e.visible);
            var visibleTopDown = entries.Where(e => e.visible)
                .OrderByDescending(e => e.rt.anchoredPosition.y).ToList();
            // A one-shot reposition doesn't stick: these buttons are AnimatedScreenElements whose
            // Animator re-drives the RectTransform when the screen's open animation plays (after
            // this postfix), snapping every button back to its prefab slot — the gap came right
            // back. Pin the compacted slots in LateUpdate instead, which runs after animation.
            var pin = root.GetComponent<MenuColumnPin>();
            if (pin == null) pin = root.gameObject.AddComponent<MenuColumnPin>();
            pin.Pins.Clear();
            for (int i = 0; i < visibleTopDown.Count && i < slots.Count; i++)
            {
                visibleTopDown[i].rt.anchoredPosition = slots[i];
                pin.Pins.Add((visibleTopDown[i].rt, slots[i]));
            }
        }

        /// <summary>Re-asserts compacted menu-slot positions every LateUpdate — the buttons'
        /// open/close animations write the RectTransform each frame and would otherwise undo the
        /// compaction. Cheap (a handful of Vector2 compares) and idle once positions settle.</summary>
        internal sealed class MenuColumnPin : UnityEngine.MonoBehaviour
        {
            internal readonly List<(UnityEngine.RectTransform rt, UnityEngine.Vector2 pos)> Pins
                = new List<(UnityEngine.RectTransform, UnityEngine.Vector2)>();

            private void LateUpdate()
            {
                foreach (var (rt, pos) in Pins)
                    if (rt != null && rt.anchoredPosition != pos)
                        rt.anchoredPosition = pos;
            }
        }

        /// <summary>The button's first "On…ButtonClicked" persistent handler, or null if it isn't
        /// a menu button (so we never reposition unrelated child buttons).</summary>
        private static string MenuHandler(UnityEngine.UI.Button button)
        {
            var ev = button.onClick;
            for (int i = 0; i < ev.GetPersistentEventCount(); i++)
            {
                var h = ev.GetPersistentMethodName(i);
                if (!string.IsNullOrEmpty(h) && h.StartsWith("On") && h.EndsWith("ButtonClicked")) return h;
            }
            return null;
        }

        private static void RelabelSaveQuit(UnityEngine.UI.Button button)
        {
            var label = button.GetComponentInChildren<TMPro.TMP_Text>(true);
            if (label == null) return;
            if (_savedSaveQuitLabel == null) _savedSaveQuitLabel = label.text;
            foreach (var comp in label.GetComponents<UnityEngine.MonoBehaviour>())
                if (comp != null && comp.GetType().Name.Contains("Localiz"))
                    UnityEngine.Object.Destroy(comp);
            label.text = "EXIT";
        }

        // MergedCellsGenerator is the one level-generation step that rolls the UNSEEDED global
        // UnityEngine.Random (everything else uses the shared Seed service), which made merged
        // terrain patches differ per client. Seed it deterministically from the run seed and the
        // cell position — order-independent — and restore the game's RNG stream afterwards.
        [HarmonyPatch]
        internal static class DeterministicMergedCells
        {
            private static MethodBase TargetMethod() =>
                AccessTools.Method(AccessTools.TypeByName("MergedCellsGenerator"), "TryPlaceMergedCell");

            private static void Prefix(int __1, int __2, out (bool seeded, UnityEngine.Random.State state) __state)
            {
                var session = NetSession.Instance;
                if (session == null || !NetSession.Active || session.CurrentRunSeed == 0)
                {
                    __state = (false, default);
                    return;
                }
                __state = (true, UnityEngine.Random.state);
                int seed = unchecked(session.CurrentRunSeed * 397 ^ __1 * 73856093 ^ __2 * 19349663);
                UnityEngine.Random.InitState(seed);
            }

            private static void Postfix((bool seeded, UnityEngine.Random.State state) __state)
            {
                if (__state.seeded) UnityEngine.Random.state = __state.state;
            }
        }

        [HarmonyPatch]
        internal static class NoNetRunSaves
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                var t = AccessTools.TypeByName("Punk.SaveLoad.GameSaver") ?? AccessTools.TypeByName("GameSaver");
                if (t != null)
                    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                        if (m.Name == "Save")
                            yield return m;
            }

            private static bool Prefix()
            {
                if (!NetSession.Active) return true;
                Plugin.Log.LogInfo("[Guard] suspend-save blocked (net run)");
                return false;
            }
        }
    }
}
