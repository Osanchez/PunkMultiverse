using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using PunkMultiverse.Core;

namespace PunkMultiverse.Patches
{
    /// <summary>
    /// Standard multiplayer rule: opening your menus must not freeze everyone's world. During a
    /// net run, gameplay-UI time pauses and slow-mo modifiers are ignored; system pauses (the
    /// level-build pause GameController holds until StartGame) pass through untouched.
    /// </summary>
    internal static class PausePolicy
    {
        private static readonly HashSet<string> BlockedOwners = new HashSet<string>
        {
            "PauseScreen", "ShipMenuToggler", "ConsumableWheel", "InstrumentMenu", "ShipMenuTab",
            "ModuleGridScreen", "ConsumablesScreen", "UIScreen", "InputSelectorPopup",
        };

        [HarmonyPatch]
        internal static class BlockUiPauses
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                var t = AccessTools.TypeByName("TimeManager");
                if (t == null) yield break;
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    if ((m.Name == "Pause" || m.Name == "AddModifier" || m.Name == "SetTimeScale")
                        && m.GetParameters().Length >= 1)
                        yield return m;
            }

            private static bool Prefix(object[] __args, System.Reflection.MethodBase __originalMethod)
            {
                var session = NetSession.Instance;
                if (session == null || session.State != SessionState.InGame) return true;
                // Slow-mo modifiers desync the shared sim regardless of who asks — block them all.
                if (__originalMethod.Name == "AddModifier") return false;
                if (__originalMethod.Name == "SetTimeScale")
                {
                    // SetTimeScale(float, object) registers its modifier inline, bypassing
                    // AddModifier — the hole the F1 debug menu's 0.1x slow-mo (and its slow-motion
                    // button, owner = a bare object) fell through. The system pause GameController
                    // holds during level build routes here via Pause() and must pass.
                    var scaleOwner = __args.Length > 1 ? __args[1] : null;
                    if (scaleOwner is DebugMenu || scaleOwner?.GetType() == typeof(object))
                    {
                        Plugin.Log.LogInfo("[Pause] blocked debug-menu slow-mo (net run)");
                        return false;
                    }
                    return true;
                }
                var owner = __args[0];
                if (owner == null) return true;
                var tn = owner.GetType().Name;
                if (!BlockedOwners.Contains(tn)) return true;
                Plugin.Log.LogDebug($"[Pause] blocked time pause from {tn} (net run)");
                return false;
            }
        }

        // Locator/scanner QoL (tester ask; Omar approved 2026-07-22): the map-reveal "showcase"
        // DEACTIVATES the player's ship input and blocks closing the menu until the reveal
        // animation finishes — a single-player assumption (the world is paused there). In a net
        // run this policy keeps the world LIVE, so the showcase made the scanning player a
        // sitting duck for the whole animation. Skip showcase mode entirely in net runs: input
        // stays active, the menu opens/closes freely, and the async reveal (delay -> ScanArea)
        // still completes in the background — the revealed area is simply on the map, whether or
        // not the player kept the menu open to watch.
        [HarmonyPatch(typeof(ShipMenuToggler), "EnterShowcaseMode")]
        internal static class NoShowcaseInputLockInNetRuns
        {
            private static bool Prefix()
            {
                var session = NetSession.Instance;
                if (session == null || session.State != SessionState.InGame) return true;
                Plugin.Log.LogInfo("[Pause] scanner reveal running in background (net run — input stays live)");
                return false;
            }
        }

        // Paired skip: with EnterShowcaseMode skipped, Exit would be a stray re-activate (and a
        // HUD/state churn) firing seconds later into whatever the player is doing now.
        [HarmonyPatch(typeof(ShipMenuToggler), "ExitShowcaseMode")]
        internal static class NoShowcaseExitChurnInNetRuns
        {
            private static bool Prefix()
            {
                var session = NetSession.Instance;
                return session == null || session.State != SessionState.InGame;
            }
        }
    }
}
