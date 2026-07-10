using System.Collections.Generic;
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

        // In a net run the suspend-save is blocked (below) but the run auto-saves through
        // NetRunSave — so the pause menu's "Save & Exit" would lie in both directions. While
        // networking is live it reads just EXIT (localization stripped from that one label).
        [HarmonyPatch(typeof(PauseScreen), "Open")]
        internal static class NetRunExitButtonLabel
        {
            private static string _originalLabel;

            private static void Postfix(PauseScreen __instance)
            {
                try
                {
                    foreach (var button in __instance.GetComponentsInChildren<UnityEngine.UI.Button>(true))
                    {
                        bool isSaveQuit = false;
                        var ev = button.onClick;
                        for (int i = 0; i < ev.GetPersistentEventCount(); i++)
                            if (ev.GetPersistentMethodName(i) == "OnSaveAndQuitButtonClicked") { isSaveQuit = true; break; }
                        if (!isSaveQuit) continue;

                        var label = button.GetComponentInChildren<TMPro.TMP_Text>(true);
                        if (label == null) return;
                        if (NetSession.Active)
                        {
                            if (_originalLabel == null) _originalLabel = label.text;
                            foreach (var comp in label.GetComponents<UnityEngine.MonoBehaviour>())
                                if (comp != null && comp.GetType().Name.Contains("Localiz"))
                                    UnityEngine.Object.Destroy(comp);
                            label.text = "EXIT";
                        }
                        else if (_originalLabel != null)
                        {
                            label.text = _originalLabel;
                        }
                        return;
                    }
                }
                catch { }
            }
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
