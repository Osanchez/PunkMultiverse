using System;
using HarmonyLib;
using PunkMultiverse.Sync;

namespace PunkMultiverse.Content
{
    /// <summary>
    /// The pickup half of <see cref="ForgeDiag"/>. Kept in its own file because it patches game
    /// code, and the rest of ForgeDiag is called from ours.
    /// </summary>
    internal static class ForgeDiagPatches
    {
        /// <summary>
        /// A module pickup completed. Hooked at ModulePickup.OnPickedUp — the vanilla grant
        /// itself — rather than at the drop, because "it dropped" and "someone actually got it"
        /// are different claims and only the second one proves the module resolved on this
        /// machine. In Battle Royale the drop is contested, so this also shows WHICH player won
        /// a custom weapon, on every machine that watched it happen.
        /// </summary>
        [HarmonyPatch(typeof(ModulePickup), "OnPickedUp")]
        internal static class TraceModulePickup
        {
            private static void Postfix(ModulePickup __instance, Ship ship)
            {
                try
                {
                    var module = __instance?.ComponentData?.module;
                    if (module?.Data == null) return;
                    int slot = -1;
                    foreach (var kv in ShipSync.ShipsBySlot)
                        if (kv.Value == ship) { slot = kv.Key; break; }
                    ForgeDiag.NotePickup(module.Data, slot);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogDebug($"[ForgeDiag] pickup trace failed: {e.Message}");
                }
            }
        }
    }
}
