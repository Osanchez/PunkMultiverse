using System.Collections.Generic;
using HarmonyLib;
using PunkMultiverse.Core;
using PunkMultiverse.Protocol;
using PunkMultiverse.Transport;
using UnityEngine;

namespace PunkMultiverse.Sync
{
    /// <summary>
    /// Grappling-hook visuals: the owner's attach/detach broadcasts with the target's netId;
    /// peers set the puppet hook's attachment so HookVisuals draws the tether. The spring physics
    /// on the puppet side is cosmetic — the ship follows snapshots and prop positions stream from
    /// their authority. Fail-open: if the Hook's internals don't match, this feature no-ops.
    /// </summary>
    internal static class HookSync
    {
        private static readonly NetWriter Writer = new NetWriter(32);
        // Attaches whose target hasn't streamed in locally yet: slot -> targetNetId. Retried
        // from the entity spawn hook, so the tether appears the moment the target exists here.
        private static readonly Dictionary<byte, int> PendingAttach = new Dictionary<byte, int>();
        private static bool _warned;

        public static void Reset()
        {
            PendingAttach.Clear();
            _warned = false;
        }

        /// <summary>Called when an entity streams in — completes any tether waiting on it.</summary>
        public static void ApplyPendingFor(int netId)
        {
            List<byte> slots = null;
            foreach (var kv in PendingAttach)
                if (kv.Value == netId)
                    (slots ??= new List<byte>()).Add(kv.Key);
            if (slots == null) return;
            foreach (var slot in slots)
            {
                PendingAttach.Remove(slot);
                Apply(new HookStateMsg { Slot = slot, Attached = true, TargetNetId = netId });
            }
        }

        [HarmonyPatch(typeof(Hook), "Activate")]
        internal static class CaptureHook
        {
            private static void Postfix(Hook __instance)
            {
                var session = NetSession.Instance;
                if (session == null || session.State != SessionState.InGame) return;
                try
                {
                    if (ShipSync.LocalShip == null || !__instance.transform.IsChildOf(ShipSync.LocalShip.transform)) return;
                    int targetNetId = 0;
                    bool attached = __instance.HasAttachment;
                    if (attached)
                    {
                        var rb = __instance.AttachedRigidbody;
                        var se = rb != null ? rb.GetComponentInParent<SavableEntity>() : null;
                        if (se == null || se.EntityData == null
                            || !NetIds.TryGetNetId(se.EntityData.instanceId, out targetNetId))
                            return; // hooked something we can't name across the wire — skip
                    }
                    Writer.Reset();
                    new HookStateMsg { Slot = (byte)session.LocalSlot, Attached = attached, TargetNetId = targetNetId }.Write(Writer);
                    session.SendToAll(NetChannel.Events, Writer.ToSegment(), reliable: true);
                }
                catch { }
            }
        }

        public static void Apply(HookStateMsg msg)
        {
            var session = NetSession.Instance;
            if (msg.Slot == session.LocalSlot) return;
            if (!ShipSync.ShipsBySlot.TryGetValue(msg.Slot, out var ship) || ship == null) return;
            var hook = ship.GetComponentInChildren<Hook>(true);
            if (hook == null) return;
            try
            {
                Rigidbody2D target = null;
                if (msg.Attached && NetIds.TryGetInstanceId(msg.TargetNetId, out int instanceId))
                {
                    var egm = ServiceLocator.Get<EntityGameObjectManager>();
                    if (egm.TryGetSavableEntity(instanceId, out var se) && se != null)
                        target = se.GetComponentInChildren<Rigidbody2D>();
                }
                if (msg.Attached && target == null)
                {
                    PendingAttach[msg.Slot] = msg.TargetNetId; // attach once it streams in
                    return;
                }
                if (!msg.Attached) PendingAttach.Remove(msg.Slot);

                if (!TrySetAttachment(hook, target) && !_warned)
                {
                    _warned = true;
                    Plugin.Log.LogWarning("[Hook] could not set puppet hook attachment (internals changed?)");
                }
            }
            catch { }
        }

        private static bool TrySetAttachment(Hook hook, Rigidbody2D target)
        {
            // Prefer a property setter; fall back to common backing-field names.
            var setter = AccessTools.PropertySetter(typeof(Hook), "AttachedRigidbody");
            if (setter != null)
            {
                setter.Invoke(hook, new object[] { target });
                return true;
            }
            foreach (var name in new[] { "attachedRigidbody", "attachedBody", "<AttachedRigidbody>k__BackingField" })
            {
                var field = AccessTools.Field(typeof(Hook), name);
                if (field != null)
                {
                    field.SetValue(hook, target);
                    return true;
                }
            }
            return false;
        }
    }
}
