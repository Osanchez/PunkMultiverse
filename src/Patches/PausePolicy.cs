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
                    if ((m.Name == "Pause" || m.Name == "AddModifier") && m.GetParameters().Length >= 1)
                        yield return m;
            }

            private static bool Prefix(object[] __args, System.Reflection.MethodBase __originalMethod)
            {
                var session = NetSession.Instance;
                if (session == null || session.State != SessionState.InGame) return true;
                // Slow-mo modifiers desync the shared sim regardless of who asks — block them all.
                if (__originalMethod.Name == "AddModifier") return false;
                var owner = __args[0];
                if (owner == null) return true;
                var tn = owner.GetType().Name;
                if (!BlockedOwners.Contains(tn)) return true;
                Plugin.Log.LogDebug($"[Pause] blocked time pause from {tn} (net run)");
                return false;
            }
        }
    }
}
