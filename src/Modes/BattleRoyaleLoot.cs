using System.Collections.Generic;
using HarmonyLib;
using PunkMultiverse.Core;
using PunkMultiverse.Protocol;
using PunkMultiverse.Sync;
using UnityEngine;

namespace PunkMultiverse.Modes
{
    /// <summary>
    /// Battle Royale loot: ONE pile, and only one player may take it.
    ///
    /// Standard co-op deliberately INSTANCES loot — every machine drops its own copy and a player
    /// too far away to reach the pile is granted an equivalent straight into their (never-synced)
    /// Vault, so a kill rewards the whole party and nobody has to race a teammate for a coin. That
    /// is exactly wrong for a battle royale, where the drop IS the contest. Omar, 2026-07-27: "gold
    /// and resources, while they should be destroyed for everyone, should only be granting one item
    /// visible to all clients, but only one may pick it up — items are not instanced like they are
    /// in our standard mode."
    ///
    /// THE MODEL. The pile stays a local copy on each machine — the alternative, replicating every
    /// coin as a real networked entity, would drag hundreds of short-lived pickups into the
    /// authority/streaming pool for the sake of an object whose only interesting state is "has
    /// someone taken it yet". What is replicated instead is that single bit, and the drop is
    /// identified by a value every machine computes independently:
    ///
    ///   key = (Group, Ordinal)
    ///     Group   = the dying entity's netId, or -(cellIndex + 1) for a destroyed terrain cell
    ///     Ordinal = the item's position in that drop's roll
    ///
    /// Both halves are already deterministic and needed no new machinery: the death roll runs
    /// inside <c>LootDiag.DropLootGuard</c>'s seeded scope, cell drops are seeded from the cell
    /// position by vanilla, and both spawn through the one funnel <c>LootFactory.Create</c>. So
    /// ordinal 3 of group #812 is the SAME item on every machine, and a key is a stable name for a
    /// pile that no machine has a shared reference to.
    ///
    /// THE HANDSHAKE. Collecting is a request, not a fact. The pickup is intercepted at the last
    /// moment (the coin's magnet reaching the ship, or the interact-pickup's fly-in), a
    /// <see cref="LootClaimMsg"/> goes to the host, and the pile HOLDS — visibly, glued to the
    /// ship — until the verdict arrives. First claim the host sees wins; it broadcasts
    /// <see cref="LootClaimedMsg"/> and every other machine destroys its copy. The winner's own
    /// pickup then completes through the untouched vanilla path, which is the point of gating
    /// rather than granting: coins, ingredients, consumables and modules each keep their own
    /// collection behaviour and none of them needs a bespoke "give this to that player" routine.
    ///
    /// The cost is one round trip of hold before the item is yours. That is the honest price of a
    /// contest — predicting the win and rolling it back would mean un-granting a module or
    /// subtracting gold a player already saw, which reads far worse than a beat of suspense.
    /// </summary>
    internal static class BattleRoyaleLoot
    {
        /// <summary>Marks a spawned pickup with the key every machine agrees on.</summary>
        internal sealed class BrLootTag : MonoBehaviour
        {
            internal int Group;
            internal byte Ordinal;
            internal long Key => MakeKey(Group, Ordinal);

            private void OnDestroy()
            {
                if (Live.TryGetValue(Key, out var list)) list.Remove(this);
            }
        }

        internal static long MakeKey(int group, byte ordinal) => ((long)group << 8) | ordinal;

        // Every tagged pickup currently on this machine, so a lost claim can find and destroy the
        // right one without walking the scene.
        private static readonly Dictionary<long, List<BrLootTag>> Live
            = new Dictionary<long, List<BrLootTag>>();

        // Resolved verdicts: key -> winning slot. Also the host's arbitration table.
        private static readonly Dictionary<long, byte> Awarded = new Dictionary<long, byte>();
        private static readonly Queue<long> AwardedOrder = new Queue<long>();
        private const int AwardedLimit = 4096; // a long match drops a lot; never grow without bound

        // Claims sent and not yet answered, with the time they were sent — a claim can be lost with
        // the message that carried it, and a pickup that silently never completes is worse than a
        // duplicate request the host idempotently ignores.
        private static readonly Dictionary<long, float> Pending = new Dictionary<long, float>();
        private const float ClaimRetrySeconds = 1.5f;

        private static readonly NetWriter Writer = new NetWriter(32);

        public static void Reset()
        {
            Live.Clear();
            Awarded.Clear();
            AwardedOrder.Clear();
            Pending.Clear();
            _group = 0;
            _ordinal = 0;
            _depth = 0;
        }

        // ---------------------------------------------------------------- tagging

        private static int _group;
        private static byte _ordinal;
        private static int _depth;

        /// <summary>Open a drop group. Nested scopes are counted but only the outermost one names
        /// the group — a drop table that drops a container that drops loot must stay one contest.</summary>
        private static bool BeginGroup(int group)
        {
            if (!BattleRoyale.Active) return false;
            if (_depth++ > 0) return true;
            _group = group;
            _ordinal = 0;
            return true;
        }

        private static void EndGroup(bool opened)
        {
            if (!opened) return;
            if (_depth > 0) _depth--;
        }

        private static void Tag(GameObject spawned)
        {
            if (_depth <= 0 || spawned == null || _ordinal == byte.MaxValue) return;
            var tag = spawned.AddComponent<BrLootTag>();
            tag.Group = _group;
            tag.Ordinal = _ordinal++;
            if (!Live.TryGetValue(tag.Key, out var list)) Live[tag.Key] = list = new List<BrLootTag>();
            list.Add(tag);
        }

        /// <summary>Death drops: the group is the dying entity's netId. An entity with no netId is
        /// an unsynced local prop — nobody else can see its loot, so there is nothing to contest.</summary>
        [HarmonyPatch(typeof(LootDropper), "DropLoot")]
        internal static class GroupDeathDrop
        {
            private static void Prefix(LootDropper __instance, out bool __state)
            {
                __state = false;
                if (!BattleRoyale.Active) return;
                if (!EnemySync.TryGetNetId(__instance, out int netId)) return;
                __state = BeginGroup(netId);
            }

            private static void Postfix(bool __state) => EndGroup(__state);
        }

        /// <summary>Terrain drops: the group is the destroyed cell. Vanilla already seeds these
        /// from the cell position, so every machine rolls the same items for the same cell — the
        /// cell index is therefore a name both machines can compute without talking.</summary>
        [HarmonyPatch(typeof(LevelSegmentComponent), "DropItems")]
        internal static class GroupCellDrop
        {
            // Mining destroys cells constantly, so the level width is resolved once rather than
            // through the service locator on every broken block.
            private static int _levelWidth;

            private static void Prefix(Vector2Int __0, out bool __state)
            {
                __state = false;
                if (!BattleRoyale.Active) return;
                if (_levelWidth <= 0)
                {
                    try { _levelWidth = ServiceLocator.Get<Level>()?.Width ?? 0; } catch { }
                    if (_levelWidth <= 0) return; // no level yet — nothing coherent to key on
                }
                // Negative space so a cell group can never collide with a netId group.
                __state = BeginGroup(-(__0.y * _levelWidth + __0.x + 1));
            }

            private static void Postfix(bool __state) => EndGroup(__state);
        }

        /// <summary>The one funnel every loot type spawns through (coins, ingredients, consumables,
        /// modules) — so one postfix numbers them all in roll order.</summary>
        [HarmonyPatch(typeof(LootFactory), "Create")]
        internal static class TagSpawnedLoot
        {
            private static void Postfix(GameObject __result) => Tag(__result);
        }

        // ---------------------------------------------------------------- the contest

        /// <summary>May the local player complete this pickup right now? False means "not yet, or
        /// never" — the caller holds the pile rather than collecting it. A loss destroys the local
        /// copy here, so the answer is only ever asked once per outcome.</summary>
        internal static bool TryTake(BrLootTag tag)
        {
            var session = NetSession.Instance;
            if (tag == null || session == null || !BattleRoyale.Active) return true;
            long key = tag.Key;

            if (Awarded.TryGetValue(key, out byte winner))
            {
                if (winner == (byte)session.LocalSlot) return true;
                Destroy(key);              // somebody else got there first
                return false;
            }

            // Not resolved yet: ask, and keep asking if the answer never comes.
            if (Pending.TryGetValue(key, out float sentAt)
                && Time.unscaledTime - sentAt < ClaimRetrySeconds) return false;
            Pending[key] = Time.unscaledTime;

            if (session.IsHost)
            {
                Award(session, key, (byte)session.LocalSlot);
                return Awarded.TryGetValue(key, out byte w) && w == (byte)session.LocalSlot;
            }

            Writer.Reset();
            new LootClaimMsg { Group = tag.Group, Ordinal = tag.Ordinal }.Write(Writer);
            session.SendToAll(Transport.NetChannel.Control, Writer.ToSegment(), reliable: true);
            return false;
        }

        /// <summary>Host: settle a claim. Idempotent — a retried or duplicated claim re-broadcasts
        /// the ORIGINAL verdict rather than changing it, so a lost verdict heals without ever
        /// handing the same pile to two players.</summary>
        internal static void Award(NetSession session, long key, byte slot)
        {
            if (session == null || !session.IsHost) return;
            if (!Awarded.TryGetValue(key, out byte winner))
            {
                winner = slot;
                Remember(key, winner);
                Plugin.Log.LogInfo($"[BRLoot] {Describe(key)} -> P{winner + 1}");
            }
            int group = (int)(key >> 8);
            byte ordinal = (byte)(key & 0xFF);
            var msg = new LootClaimedMsg { Group = group, Ordinal = ordinal, Slot = winner };
            ApplyClaimed(msg, session);
            Writer.Reset();
            msg.Write(Writer);
            session.SendToAll(Transport.NetChannel.Control, Writer.ToSegment(), reliable: true);
        }

        public static void ApplyClaim(LootClaimMsg msg, byte fromSlot, NetSession session)
            => Award(session, MakeKey(msg.Group, msg.Ordinal), fromSlot);

        /// <summary>Everyone (host included): the verdict. Losers lose the object; the winner's own
        /// pickup simply proceeds the next time it asks.</summary>
        public static void ApplyClaimed(LootClaimedMsg msg, NetSession session)
        {
            long key = MakeKey(msg.Group, msg.Ordinal);
            Remember(key, msg.Slot);
            Pending.Remove(key);
            if (session != null && msg.Slot == (byte)session.LocalSlot) return;
            Destroy(key);
        }

        private static void Remember(long key, byte slot)
        {
            if (!Awarded.ContainsKey(key)) AwardedOrder.Enqueue(key);
            Awarded[key] = slot;
            while (AwardedOrder.Count > AwardedLimit) Awarded.Remove(AwardedOrder.Dequeue());
        }

        private static void Destroy(long key)
        {
            if (!Live.TryGetValue(key, out var list)) return;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var tag = list[i];
                if (tag != null && tag.gameObject != null) Object.Destroy(tag.gameObject);
            }
            list.Clear();
            Live.Remove(key);
        }

        private static string Describe(long key) => $"loot #{(int)(key >> 8)}.{key & 0xFF}";

        // ---------------------------------------------------------------- collection gates

        /// <summary>Coins and other magnet pickups. Blocking here leaves the coin stuck to the ship
        /// for the round trip, which reads as "being absorbed" rather than as a bug — and if the
        /// claim is lost the coin vanishes from under you, which is the truth.</summary>
        [HarmonyPatch(typeof(Pickup), nameof(Pickup.GetPickedUp))]
        internal static class GateMagnetPickup
        {
            private static bool Prefix(Pickup __instance)
            {
                if (!BattleRoyale.Active) return true;
                var tag = __instance != null ? __instance.GetComponent<BrLootTag>() : null;
                if (tag == null) return true;
                return TryTake(tag);
            }
        }

        /// <summary>A remote player's ship is a real <c>Ship</c> with a real <c>LootCollector</c> on
        /// THIS machine, so vanilla's magnet is perfectly happy to let a puppet hoover up a pile and
        /// charge its own (fake, snapshot-driven) tank — silently destroying loot the local player
        /// was flying toward. In a mode where the pile is contested that is not a cosmetic problem:
        /// the pickup would be consumed by a ship that never claimed it.</summary>
        // ResourcePickup, not the abstract Pickup: CanBePickUpBy has no body on the base class, and
        // Harmony throws on an abstract target — which would take the whole plugin down at load.
        [HarmonyPatch(typeof(ResourcePickup), nameof(ResourcePickup.CanBePickUpBy))]
        internal static class OnlyLocalShipCollects
        {
            private static void Postfix(Unit unit, ref bool __result)
            {
                if (!__result || !BattleRoyale.Active || unit == null) return;
                if (unit.GetComponent<RemotePuppet>() != null) __result = false;
            }
        }

        /// <summary>Interact-to-collect pickups (ingredients, consumables, modules). Their fly-in is
        /// driven by <c>InteractiblePickup&lt;T&gt;.Update</c> — a CLOSED GENERIC method, one
        /// compiled body per pickup data type — so each concrete type resolves to its own target.
        /// Gated only once a ship is actually being flown to, or merely existing near a pile would
        /// claim it.
        ///
        /// Applied by hand from <c>Plugin</c> rather than by attribute discovery: generic-method
        /// patching is the one Harmony corner that can fail on a runtime we don't control, and a
        /// throw inside <c>PatchAll</c> takes the ENTIRE mod down at load. Here the worst case is
        /// one loot type staying uncontested, logged.</summary>
        internal static void ApplyGenericPatches(Harmony harmony)
        {
            var prefix = new HarmonyMethod(AccessTools.Method(typeof(BattleRoyaleLoot), nameof(GateInteractPickupPrefix)));
            foreach (var name in new[] { "IngredientPickup", "ConsumablePickup", "ModulePickup" })
            {
                try
                {
                    var t = AccessTools.TypeByName(name);
                    var m = t != null ? AccessTools.Method(t, "Update") : null;
                    if (m == null)
                    {
                        Plugin.Log.LogWarning($"[BRLoot] no {name}.Update to patch — that pickup type will not be contested");
                        continue;
                    }
                    harmony.Patch(m, prefix);
                }
                catch (System.Exception e)
                {
                    Plugin.Log.LogWarning($"[BRLoot] could not gate {name}: {e.Message} — that pickup type will not be contested");
                }
            }
        }

        private static bool GateInteractPickupPrefix(object __instance)
        {
            if (!BattleRoyale.Active) return true;
            var component = __instance as Component;
            var tag = component != null ? component.GetComponent<BrLootTag>() : null;
            if (tag == null) return true;
            object target = null;
            try { target = Traverse.Create(__instance).Field("targetShip").GetValue(); }
            catch { return true; }
            if (target as Object == null) return true; // nobody is picking it up yet
            return TryTake(tag);
        }
    }
}
