using System;
using System.Collections.Generic;
using HarmonyLib;
using PunkMultiverse.Core;

namespace PunkMultiverse.Patches
{
    /// <summary>
    /// 12 174 NullReferenceExceptions in one harness run, all from the same two frames of vanilla
    /// (found 2026-08-09, the first run with Unity's log captured into ours):
    ///
    /// <code>
    /// NullReferenceException
    ///   ShipLogOutput.Clear (System.Int32 id)
    ///   Ship.Update ()
    /// </code>
    ///
    /// <c>ShipLogOutput</c> raises its two events inconsistently:
    ///
    /// <code>
    /// public void Log(...)   { entries.Add(e); this.LogAdded?.Invoke(e); }   // guarded
    /// public void Clear(int id) { ... this.LogRemoved(entries[num]); ... }   // NOT guarded
    /// public void Update()      { ... this.LogRemoved(entries[num]); ... }   // NOT guarded
    /// </code>
    ///
    /// In single-player every ship owns a visible HUD, so <c>LogRemoved</c> always has a
    /// subscriber and the asymmetry never shows. In a net run each remote player's puppet is a
    /// full Ship whose HUD this mod switches off ("disabled HUD 1 (puppet ship)") — no subscriber,
    /// and every Clear on that ship throws.
    ///
    /// Then it repeats forever, because of where the throw lands:
    ///
    /// <code>
    /// else if (!HasNoFuel &amp;&amp; selfDestructHintShown)
    /// {
    ///     logOutput.Clear(4);          // throws here
    ///     selfDestructHintShown = false;   // so this never runs
    /// }
    /// </code>
    ///
    /// The flag stays true, the branch is taken again next frame, and a refuelled teammate's
    /// puppet quietly burns a stack trace per frame for the rest of the run.
    ///
    /// Do the removal the events were meant to accompany, minus the events nobody is listening to.
    /// </summary>
    internal static class ShipLogGuard
    {
        private static int _clears;
        private static int _expiries;

        internal static void Reset() { _clears = 0; _expiries = 0; }

        private static List<ShipLogEntry> Entries(ShipLogOutput log)
            => Traverse.Create(log).Field("entries").GetValue<List<ShipLogEntry>>();

        /// <summary>True when nobody is listening — the exact case vanilla forgot to guard.</summary>
        private static bool NoListener(ShipLogOutput log)
            => Traverse.Create(log).Field("LogRemoved").GetValue<Action<ShipLogEntry>>() == null;

        [HarmonyPatch(typeof(ShipLogOutput), "Clear")]
        internal static class ClearWithoutListeners
        {
            private static bool Prefix(ShipLogOutput __instance, int __0)
            {
                if (!NetSession.Active) return true;
                try
                {
                    if (!NoListener(__instance)) return true;   // vanilla path is safe here
                    var entries = Entries(__instance);
                    if (entries == null) return true;
                    int removed = entries.RemoveAll(e => e != null && e.id == __0);
                    if (removed > 0 && ++_clears == 1)
                        Plugin.Log.LogInfo("[ShipLog] clearing log entries on a ship with no HUD listener " +
                                           "(puppet) — vanilla would have thrown here every frame");
                    return false;
                }
                catch { return true; }
            }
        }

        [HarmonyPatch(typeof(ShipLogOutput), "Update")]
        internal static class ExpireWithoutListeners
        {
            private static bool Prefix(ShipLogOutput __instance)
            {
                if (!NetSession.Active) return true;
                try
                {
                    if (!NoListener(__instance)) return true;
                    var entries = Entries(__instance);
                    if (entries == null) return true;
                    int removed = entries.RemoveAll(e => e != null && e.duration > 0f
                                                         && e.TimeSinceCreation > e.duration);
                    if (removed > 0 && ++_expiries == 1)
                        Plugin.Log.LogInfo("[ShipLog] expiring log entries on a ship with no HUD listener (puppet)");
                    return false;
                }
                catch { return true; }
            }
        }
    }
}
