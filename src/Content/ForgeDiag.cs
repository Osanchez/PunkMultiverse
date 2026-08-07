using System;
using System.Collections.Generic;
using UnityEngine;

namespace PunkMultiverse.Content
{
    /// <summary>
    /// Traces a custom (content-mod) weapon end to end across machines: picked up, fired,
    /// replayed on the peer, and the damage it caused.
    ///
    /// The reason this exists rather than "watch the screen": a custom weapon reaches a remote
    /// client along a longer chain than a stock one, and every link is silent when it breaks.
    /// FireEventMsg carries only the shooter's slot and which holder fired — NOT the weapon —
    /// so a peer resolves the weapon from the puppet's own module grid, which arrived earlier
    /// over ModuleGridSync as a string module id, which only resolves if that id is registered
    /// on the peer. A custom weapon can therefore be fired locally with the right art and sound
    /// and arrive on the peer as nothing at all, or as the wrong weapon.
    ///
    /// So both ends log the weapon they actually used, by id. Same id on both = the chain held.
    /// Different ids, or a shot with no matching replay, is the bug — visible in a log diff
    /// rather than in someone's recollection of what they saw.
    ///
    /// Everything is gated on a content mod being installed AND having weapons registered, so on
    /// an ordinary machine this costs one bool test per shot.
    /// </summary>
    internal static class ForgeDiag
    {
        private static readonly HashSet<string> ForgeModuleIds = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> ForgeWeaponIds = new HashSet<string>(StringComparer.Ordinal);
        private static float _nextRefreshAt;
        private static bool _any;

        // Per-weapon shot counters, so a long test reports "142 shots / 141 replays" instead of
        // 142 individual lines. The first of each is always logged: a weapon that fires once and
        // never replays is the failure this is looking for, and a summary alone could hide it.
        private sealed class Counter { public int Local; public int Replay; public int Damage; public float NextLogAt; }
        private static readonly Dictionary<string, Counter> Counters = new Dictionary<string, Counter>(StringComparer.Ordinal);

        internal static bool Active => _any;

        /// <summary>Cheap enough to call from a fire path: refreshes at most every few seconds.</summary>
        private static void Refresh()
        {
            if (Time.unscaledTime < _nextRefreshAt) return;
            _nextRefreshAt = Time.unscaledTime + 5f;
            ForgeModuleIds.Clear();
            ForgeWeaponIds.Clear();
            ForgeBridge.CollectForgeIds(ForgeModuleIds, ForgeWeaponIds);
            bool any = ForgeModuleIds.Count > 0 || ForgeWeaponIds.Count > 0;
            if (any != _any)
            {
                _any = any;
                if (any)
                    Plugin.Log.LogInfo($"[ForgeDiag] tracing {ForgeModuleIds.Count} custom module(s) / " +
                        $"{ForgeWeaponIds.Count} custom weapon id(s)");
            }
        }

        internal static void Reset()
        {
            Counters.Clear();
            _nextRefreshAt = 0f;
        }

        /// <summary>
        /// Identify a shot by the MODULE in the firing slot, not by the weapon asset.
        ///
        /// WeaponForge builds a custom weapon by cloning a stock one and overriding fields — and
        /// the clone keeps the template's id. A PLASMA LANCE therefore reports its WeaponData.Id
        /// as "Weapon White Popper", identical to the stock Popper it came from, so matching on
        /// weapon id cannot tell them apart and would label every stock Popper shot as custom.
        /// The module id is the half that IS unique ("FORGE-MVPLASMALANCE"), because that is what
        /// gets registered in ModuleRegistry and what travels in ModuleGridSync — which makes it
        /// both the correct discriminator and the one whose cross-machine agreement actually
        /// matters.
        /// </summary>
        private static string ModuleIdInSlot(Ship ship, int holder)
        {
            try
            {
                // IModuleGrid has no indexer; the concrete ModuleGrid does. Same cast DevTools
                // uses to reach the grid for `equip`.
                var grid = ship?.ModuleGridOwner?.ModuleGrid as ModuleGrid;
                if (grid == null) return null;
                var pos = holder == 1 ? ModuleGrid.SecondaryWeaponGridPosition
                                      : ModuleGrid.PrimaryWeaponGridPosition;
                var module = grid[pos];
                string id = null;
                try { id = module?.Data?.Id; } catch { }
                return id;
            }
            catch { return null; }
        }

        private static bool IsForgeModule(string id) =>
            !string.IsNullOrEmpty(id) && ForgeModuleIds.Contains(id);

        private static Counter CounterFor(string id)
        {
            if (!Counters.TryGetValue(id, out var c)) Counters[id] = c = new Counter();
            return c;
        }

        // ShotId -> custom weapon id. DamageRequestMsg carries a ShotId but not a weapon, so this
        // is what lets a VICTIM name the weapon that hit it. Bounded: a match fires thousands of
        // shots and this is a diagnostic, not a ledger.
        private const int MaxShotIds = 512;
        private static readonly Dictionary<uint, string> ShotWeapon = new Dictionary<uint, string>();
        private static readonly Queue<uint> ShotOrder = new Queue<uint>();

        private static void RememberShot(uint shotId, string weaponId)
        {
            if (shotId == 0 || ShotWeapon.ContainsKey(shotId)) return;
            ShotWeapon[shotId] = weaponId;
            ShotOrder.Enqueue(shotId);
            while (ShotOrder.Count > MaxShotIds) ShotWeapon.Remove(ShotOrder.Dequeue());
        }

        /// <summary>The custom weapon behind a shot id, or null if it was not one of ours.</summary>
        internal static string WeaponForShot(uint shotId) =>
            shotId != 0 && ShotWeapon.TryGetValue(shotId, out var id) ? id : null;

        /// <summary>A ship fired. `replayed` distinguishes the shooter's own shot from a peer
        /// reproducing it, which is the whole comparison.</summary>
        internal static void NoteShot(int slot, Ship ship, int holder, bool replayed, uint shotId = 0)
        {
            Refresh();
            if (!_any) return;
            var id = ModuleIdInSlot(ship, holder);
            if (!IsForgeModule(id)) return;
            RememberShot(shotId, id);

            var c = CounterFor(id);
            bool first = (replayed ? c.Replay : c.Local) == 0;
            if (replayed) c.Replay++; else c.Local++;

            if (first)
            {
                Plugin.Log.LogInfo($"[ForgeDiag] shot {(replayed ? "REPLAYED" : "LOCAL")} '{id}' " +
                    $"P{slot + 1} — first of this kind on this machine");
                return;
            }
            if (Time.unscaledTime < c.NextLogAt) return;
            c.NextLogAt = Time.unscaledTime + 10f;
            Plugin.Log.LogInfo($"[ForgeDiag] '{id}' P{slot + 1} local={c.Local} replayed={c.Replay} damage={c.Damage}");
        }

        /// <summary>Damage that a custom weapon caused, logged where it is APPLIED — so a victim
        /// on another machine proves the whole chain, not just that a projectile was drawn.</summary>
        internal static void NoteDamage(string weaponId, float amount, int victimSlot, bool remote)
        {
            Refresh();
            // weaponId here is whatever RememberShot stored — a module id — so it is already
            // known-custom by construction; no second filter.
            if (!_any || string.IsNullOrEmpty(weaponId)) return;
            var c = CounterFor(weaponId);
            bool first = c.Damage == 0;
            c.Damage++;
            if (first || Time.unscaledTime >= c.NextLogAt)
            {
                if (!first) c.NextLogAt = Time.unscaledTime + 10f;
                Plugin.Log.LogInfo($"[ForgeDiag] damage {amount:0.##} from '{weaponId}' " +
                    $"on {(victimSlot >= 0 ? "P" + (victimSlot + 1) : "an entity")} " +
                    $"({(remote ? "applied from the wire" : "local")}) total={c.Damage}");
            }
        }

        /// <summary>A custom module was picked up. Named separately from the shot trace because
        /// "it dropped but nobody could take it" and "it was taken but never fired" are different
        /// failures with the same symptom.</summary>
        internal static void NotePickup(ModuleData module, int slot)
        {
            Refresh();
            if (!_any || module == null) return;
            string id = null;
            try { id = module.Id; } catch { }
            if (string.IsNullOrEmpty(id) || !ForgeModuleIds.Contains(id)) return;
            Plugin.Log.LogInfo($"[ForgeDiag] pickup '{id}' ({module.displayName}) by " +
                $"{(slot >= 0 ? "P" + (slot + 1) : "someone")}");
        }
    }
}
