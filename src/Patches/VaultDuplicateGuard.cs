using System;
using System.Collections.Generic;
using HarmonyLib;
using PunkMultiverse.Core;

namespace PunkMultiverse.Patches
{
    /// <summary>
    /// The station shop that opens half-built and cannot sell — caught live, 2026-08-09:
    ///
    /// <code>
    /// ArgumentException: An item with the same key has already been added. Key: Module
    ///   Dictionary.Add
    ///   VaultGridWidget.AddItem (Module module, int index)
    ///   VaultGridWidget.Refresh ()
    ///   ModuleGridScreen.OnOpened ()
    ///   ShipMenuToggler.ShowTab -> Open -> OpenShop -> Shop.StartShopping -> Station.OnUseActivated
    /// </code>
    ///
    /// The vault holds the SAME <c>Module</c> instance twice. <c>Vault.Store</c> is a bare
    /// <c>modules.Add(module)</c> with no membership check, and <c>VaultGridWidget.Refresh</c>
    /// keys a dictionary by module — so the second copy throws, and the throw lands in the middle
    /// of the shop opening.
    ///
    /// What the player sees, and why:
    /// <list type="bullet">
    /// <item>the crosshair stays on screen — <c>Open</c> died before the lines that switch action
    /// maps and disable ship control, and <c>Ship.Update</c> ties the crosshair to that map;</item>
    /// <item>pressing interact AGAIN clears it — <c>ShowTab</c> assigns <c>currentTabIndex</c>
    /// before it throws, so the second pass skips the tab entirely and the tail of <c>Open</c>
    /// finally runs;</item>
    /// <item>nothing can be bought — <c>Refresh</c> throws before
    /// <c>input.RegisterPlayerInput</c>, so the "Shop" action map is never enabled;</item>
    /// <item>prices look unaffordable — the widgets never finished rebuilding.</item>
    /// </list>
    ///
    /// Two guards, because an already-corrupted vault has to heal too: refuse a duplicate store
    /// (the source), and drop duplicates that are already in there when the widget rebuilds (the
    /// running save). Both name what they dropped — a module that gets refused every session is a
    /// second bug wearing this one as a coat.
    /// </summary>
    internal static class VaultDuplicateGuard
    {
        private static int _refused;
        private static int _pruned;

        internal static void Reset() { _refused = 0; _pruned = 0; }

        private static string Name(Module module)
        {
            try { return module?.Data != null ? module.Data.name : "<no data>"; }
            catch { return "<unnamed>"; }
        }

        /// <summary>Source: the same instance may not enter the vault twice.</summary>
        [HarmonyPatch(typeof(Vault), "Store")]
        internal static class RefuseDuplicateStore
        {
            private static bool Prefix(Vault __instance, Module module)
            {
                if (!NetSession.Active) return true;
                try
                {
                    if (module == null || !__instance.Contains(module)) return true;
                    _refused++;
                    Plugin.Log.LogWarning($"[Vault] refused a duplicate store of '{Name(module)}' " +
                                          $"(refused {_refused} this run) — it is already in the vault");
                    return false;
                }
                catch { return true; }
            }
        }

        /// <summary>The running save: a vault that already holds a duplicate would keep throwing on
        /// every shop open. Prune before the widget keys its dictionary by module.</summary>
        [HarmonyPatch(typeof(VaultGridWidget), "Refresh")]
        internal static class PruneBeforeRefresh
        {
            private static void Prefix()
            {
                if (!NetSession.Active) return;
                try
                {
                    var vault = ServiceLocator.Get<Vault>();
                    if (vault == null) return;
                    var list = Traverse.Create(vault).Field("modules").GetValue<List<Module>>();
                    if (list == null || list.Count < 2) return;

                    var seen = new HashSet<Module>();
                    int removed = 0;
                    for (int i = list.Count - 1; i >= 0; i--)
                    {
                        var module = list[i];
                        // A null slot would crash the widget just as surely as a duplicate.
                        if (module == null) { list.RemoveAt(i); removed++; continue; }
                        if (!seen.Add(module)) { list.RemoveAt(i); removed++; }
                    }
                    // seen was filled back-to-front; the survivors keep their original order.
                    if (removed == 0) return;
                    _pruned += removed;
                    Plugin.Log.LogWarning($"[Vault] pruned {removed} duplicate/empty module slot(s) before the " +
                                          $"grid rebuilt ({_pruned} this run) — this is what breaks the shop open");
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[Vault] prune failed: {e.Message}"); }
            }
        }
    }
}
