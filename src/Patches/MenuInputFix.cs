using HarmonyLib;
using PunkMultiverse.Core;
using PunkMultiverse.Sync;
using UnityEngine.InputSystem;

namespace PunkMultiverse.Patches
{
    /// <summary>
    /// UIScreen.Open records previousActionMap by iterating EVERY PlayerInput in the scene and
    /// keeping the LAST one's currentActionMap. In a net run the puppet ship carries a disabled
    /// PlayerInput (RemotePuppet.Neuter) whose currentActionMap is null; when it enumerates after
    /// the local player's, the captured map is null and UIScreen.CloseCoroutine skips the
    /// switch-back entirely — the local player stays on the menu action map and gameplay controls
    /// are dead after closing any menu (pause screen, ship menu, …). Vanilla never sees this:
    /// solo has exactly one PlayerInput. Capture the LOCAL player's map and repair the field.
    /// </summary>
    internal static class MenuInputFix
    {
        [HarmonyPatch(typeof(UIScreen), "Open")]
        internal static class RepairPreviousActionMap
        {
            private static void Prefix(out InputActionMap __state)
            {
                __state = null;
                if (!NetSession.Active) return;
                var ship = ShipSync.LocalShip;
                if (ship == null) return;
                var input = ship.GetComponentInChildren<PlayerInput>(true);
                if (input != null && input.enabled) __state = input.currentActionMap;
            }

            private static void Postfix(UIScreen __instance, InputActionMap __state)
            {
                if (__state == null) return;
                AccessTools.Field(typeof(UIScreen), "previousActionMap")?.SetValue(__instance, __state);
            }
        }
    }
}
