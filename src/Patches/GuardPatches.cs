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

        /// <summary>Remove the restart button from a fixed-slot menu column WITHOUT moving any
        /// button. Each button is an AnimatedScreenElement whose Animator re-drives its RectTransform
        /// to its own prefab slot every frame (the "Visible" open animation), so repositioning never
        /// sticks — the gap kept coming back, and fighting it in LateUpdate was a per-frame tug-of-war.
        /// Instead we leave every button in place and REASSIGN ROLES: the top N physical slots are
        /// relabeled/rewired to the N buttons we keep (top-down order), and the one leftover bottom
        /// slot is hidden. Every surviving role now lives in a slot that was always occupied, so the
        /// visible column is contiguous and the animator can't reopen a gap. Also relabels the
        /// Save&amp;Quit role → EXIT (its suspend-save is blocked in net runs; the run auto-saves).</summary>
        private static void LayoutMenuColumn(UnityEngine.Component root, bool hideRestart, bool relabelSaveQuitAsExit = false)
        {
            // Physical slots = the menu buttons the game is CURRENTLY showing, top-down. PauseScreen
            // carries five (Resume/Restart/Quit/SaveAndQuit/Report) but only shows a subset.
            var slots = new List<(UnityEngine.UI.Button btn, string handler, UnityEngine.Object target)>();
            foreach (var button in root.GetComponentsInChildren<UnityEngine.UI.Button>(true))
            {
                if (!button.gameObject.activeInHierarchy) continue; // skip buttons the game hid
                var (handler, target) = MenuHandlerAndTarget(button);
                if (handler == null) continue; // not a menu button (Resume/Restart/Quit/…)
                slots.Add((button, handler, target));
            }
            if (slots.Count == 0) return;
            slots.Sort((a, b) => SlotY(b.btn).CompareTo(SlotY(a.btn))); // top-down

            // Roles to keep, same top-down order, minus restart. Snapshot each role's label and
            // click target NOW — before any relabel/rewire mutates a button we still read below.
            var roles = new List<(string handler, UnityEngine.Object target, string label)>();
            foreach (var s in slots)
            {
                if (hideRestart && s.handler == "OnRestartButtonClicked") continue;
                string label = relabelSaveQuitAsExit && s.handler == "OnSaveAndQuitButtonClicked"
                    ? "EXIT" : LabelOf(s.btn);
                roles.Add((s.handler, s.target, label));
            }

            for (int i = 0; i < slots.Count; i++)
            {
                var slot = slots[i];
                if (i >= roles.Count) { slot.btn.gameObject.SetActive(false); continue; } // extra slot
                var role = roles[i];
                bool sameRole = slot.handler == role.handler && ReferenceEquals(slot.target, role.target);
                if (sameRole)
                {
                    // Already the right button in this slot; only the label may need changing.
                    if (relabelSaveQuitAsExit && role.handler == "OnSaveAndQuitButtonClicked")
                        SetLabel(slot.btn, role.label);
                    continue;
                }
                // Make this slot play the kept role: adopt its label and call its screen method.
                SetLabel(slot.btn, role.label);
                RewireClick(slot.btn, role.target, role.handler);
            }
        }

        private static float SlotY(UnityEngine.UI.Button b) =>
            b.transform is UnityEngine.RectTransform rt ? rt.anchoredPosition.y : b.transform.localPosition.y;

        /// <summary>The button's first "On…ButtonClicked" persistent handler and the object it calls,
        /// or (null, null) if it isn't a menu button (so we never touch unrelated child buttons).</summary>
        private static (string handler, UnityEngine.Object target) MenuHandlerAndTarget(UnityEngine.UI.Button button)
        {
            var ev = button.onClick;
            for (int i = 0; i < ev.GetPersistentEventCount(); i++)
            {
                var h = ev.GetPersistentMethodName(i);
                if (!string.IsNullOrEmpty(h) && h.StartsWith("On") && h.EndsWith("ButtonClicked"))
                    return (h, ev.GetPersistentTarget(i));
            }
            return (null, null);
        }

        private static string LabelOf(UnityEngine.UI.Button button)
        {
            var t = button.GetComponentInChildren<TMPro.TMP_Text>(true);
            return t != null ? t.text : "";
        }

        private static void SetLabel(UnityEngine.UI.Button button, string text)
        {
            var label = button.GetComponentInChildren<TMPro.TMP_Text>(true);
            if (label == null) return;
            // Kill any localizer that would overwrite our text on the next locale refresh.
            foreach (var comp in label.GetComponents<UnityEngine.MonoBehaviour>())
                if (comp != null && comp.GetType().Name.Contains("Localiz"))
                    UnityEngine.Object.Destroy(comp);
            label.text = text;
        }

        // Rewire a slot to invoke another role's screen method directly (via reflection on the
        // captured target), NOT by chaining to another button's onClick — a button we hand a new
        // role to may itself be a remap target whose onClick we clear, which would break a chain.
        private static void RewireClick(UnityEngine.UI.Button button, UnityEngine.Object target, string method)
        {
            button.onClick = new UnityEngine.UI.Button.ButtonClickedEvent(); // drop the old role's call
            if (target == null) return;
            var mi = target.GetType().GetMethod(method,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (mi == null) return;
            button.onClick.AddListener(() => { try { mi.Invoke(target, null); } catch { } });
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
