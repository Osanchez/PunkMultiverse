using System.Collections.Generic;
using PunkMultiverse.Core;
using PunkMultiverse.Protocol;
using PunkMultiverse.Transport;
using UnityEngine;

namespace PunkMultiverse.Sync
{
    /// <summary>
    /// Replicates the TRIGGER STATE of held weapons, so a beam is visible on other players' screens.
    ///
    /// ProjectileSync replicates discrete SHOTS, and for a projectile that is the whole job: the
    /// replay spawns one on the puppet and it flies away carrying its own visual. A hitscan beam
    /// has no such object. HitscanWeapon.OnBarrelMoved redraws it EVERY FRAME and only while
    /// IsTriggerPulled — and the else branch explicitly sets Firing = false. A puppet's trigger is
    /// never pulled, so every frame it actively switches the beam off.
    ///
    /// Measured before writing any of this: over one 40-second hold the owner fired 196 casts and
    /// the observer replayed 152 of them, and drew nothing. 78% delivery is not a packet problem.
    /// The shots were arriving; the STATE that makes a beam visible was not, because nothing sent
    /// it. Sending more shot events would not have helped — between two damage ticks there is
    /// still no beam to draw.
    ///
    /// So: one message on each transition, plus a slow keepalive carrying fresh aim while the
    /// trigger is down. The aim has to keep coming — a beam frozen at the angle it started reads
    /// as a bug, not as latency. The keepalive is deliberately far below the damage tick rate;
    /// this is a presentation channel, and the damage it accompanies is already replicated
    /// exactly by the seeded shot replay.
    /// </summary>
    internal static class HeldFireSync
    {
        // Aim refreshes while held. 15/s is smooth enough for a beam that mostly tracks slowly,
        // and an order of magnitude cheaper than the per-frame rate the visual actually redraws at.
        private const float AimHz = 15f;
        // Stop drawing if the owner goes quiet. A dropped release message must not leave a beam
        // burning on someone's screen forever, and a session that hitches must not either.
        private const float StaleSeconds = 0.6f;

        private struct Held { internal bool Active; internal float NextSendAt; }
        private static readonly Dictionary<int, Held> LocalHeld = new Dictionary<int, Held>();

        private struct Remote
        {
            internal bool Active;
            internal Vector2 BodyPos, Pos, Dir;
            internal float LastSeenAt;
        }
        private static readonly Dictionary<(byte slot, byte holder), Remote> RemoteHeld
            = new Dictionary<(byte, byte), Remote>();

        /// <summary>
        /// Our own AudioManager handles for the continuous fire loop, one per held weapon.
        ///
        /// Vanilla plays that loop from Shooter.Update, gated on `isTryingToShoot` — LOCAL input,
        /// not the trigger state. A puppet has no input, so the beam drew in silence: the whole
        /// point of WeaponForge's continousShootSfx (a looping Sfx registered into the game's own
        /// AudioDatabase) was inaudible to everyone except the shooter.
        ///
        /// We play it ourselves rather than calling Shooter.PlayContinousSound(), and that choice
        /// is what avoids a fight. Shooter.StopContinousSound() only stops `continousSoundHandle`,
        /// its OWN field, and on a puppet that field is null forever because nothing ever calls
        /// PlayContinousSound there — so its every-frame stop is a no-op that cannot touch this
        /// handle. Driving Shooter's would have meant it stopping our loop each Update and us
        /// restarting it each LateUpdate: a handle created and destroyed at frame rate, which is a
        /// buzz, not a beam.
        ///
        /// PlaySfx(guid, Transform) follows the transform, so a puppet that is still interpolating
        /// carries its sound with it and there is no position to push per frame.
        /// </summary>
        private static readonly Dictionary<(byte slot, byte holder), int> LoopHandles
            = new Dictionary<(byte, byte), int>();

        internal static void Reset()
        {
            LocalHeld.Clear();
            RemoteHeld.Clear();
            // Before the dictionary is dropped, or a loop outlives the session that started it.
            // A beam sound with no beam and no way to stop it is the worst version of this bug.
            foreach (var h in LoopHandles.Values) { try { AudioManager.Stop(h); } catch { } }
            LoopHandles.Clear();
        }

        // ---- owner side ----------------------------------------------------------------------

        /// <summary>Watch the local ship's held weapons and announce trigger changes.</summary>
        internal static void Tick(NetSession session)
        {
            if (session == null || session.State != SessionState.InGame) return;

            var ship = ShipSync.LocalShip;
            if (ship == null) { if (LocalHeld.Count > 0) ReleaseAllLocal(session); return; }

            for (byte holder = 0; holder < 2; holder++)
            {
                var weapon = ProjectileSync.HolderWeaponOf(ship, holder);
                // Only CONTINUOUS weapons need this. A projectile weapon's shots already carry
                // everything a peer needs, and sending trigger state for them would be pure noise.
                if (!(weapon is HitscanWeapon)) { ClearLocal(session, ship, holder); continue; }

                bool down;
                try { down = weapon.IsTriggerPulled; } catch { continue; }

                LocalHeld.TryGetValue(holder, out var prev);
                bool changed = prev.Active != down;
                bool due = down && Time.unscaledTime >= prev.NextSendAt;
                if (!changed && !due) continue;

                Vector2 muzzle = ship.transform.position;
                Vector2 dir = Vector2.right;
                try
                {
                    var barrel = ProjectileSync.BarrelOf(weapon);
                    if (barrel != null) { muzzle = barrel.Position; dir = barrel.Direction; }
                }
                catch { }

                Send(session, new HeldFireMsg
                {
                    Slot = (byte)session.LocalSlot,
                    Holder = holder,
                    Active = down,
                    BodyPos = ship.transform.position,
                    Pos = muzzle,
                    Dir = dir,
                });

                LocalHeld[holder] = new Held
                {
                    Active = down,
                    NextSendAt = Time.unscaledTime + (1f / AimHz),
                };
            }
        }

        private static void ClearLocal(NetSession session, Ship ship, byte holder)
        {
            if (!LocalHeld.TryGetValue(holder, out var prev) || !prev.Active) return;
            Send(session, new HeldFireMsg
            {
                Slot = (byte)session.LocalSlot, Holder = holder, Active = false,
                BodyPos = ship != null ? (Vector2)ship.transform.position : Vector2.zero,
            });
            LocalHeld[holder] = default;
        }

        /// <summary>The ship is gone (death, run end). Tell peers to stop drawing rather than
        /// leaving a beam hanging in the air on their screens.</summary>
        private static void ReleaseAllLocal(NetSession session)
        {
            foreach (var kv in new List<int>(LocalHeld.Keys))
            {
                if (!LocalHeld[kv].Active) continue;
                Send(session, new HeldFireMsg
                { Slot = (byte)session.LocalSlot, Holder = (byte)kv, Active = false });
            }
            LocalHeld.Clear();
        }

        private static readonly NetWriter Writer = new NetWriter();

        private static void Send(NetSession session, HeldFireMsg msg)
        {
            // UNRELIABLE, on the same lane FireEvent uses. This is presentation that must keep
            // pace with aim and is worthless late: a dropped update is corrected by the next one
            // ~66ms later, and a dropped RELEASE is covered by the staleness timeout rather than
            // by retransmission — the cheaper guarantee for something that only decides whether a
            // line is drawn.
            Writer.Reset();
            msg.Write(Writer);
            session.SendToAll(NetChannel.State, Writer.ToSegment(), reliable: false);
        }

        // ---- peer side -----------------------------------------------------------------------

        internal static void Apply(HeldFireMsg msg)
        {
            var key = (msg.Slot, msg.Holder);
            if (!msg.Active) { RemoteHeld.Remove(key); StopBeam(msg.Slot, msg.Holder); return; }
            RemoteHeld[key] = new Remote
            {
                Active = true, BodyPos = msg.BodyPos, Pos = msg.Pos, Dir = msg.Dir,
                LastSeenAt = Time.unscaledTime,
            };
        }

        /// <summary>
        /// Drive every held remote beam, every frame.
        ///
        /// EVERY FRAME is the point. OnBarrelMoved is what draws the beam and it must be called
        /// continuously; calling it only when a packet arrives would produce a beam that blinks at
        /// the packet rate. The aim comes from the last message and the muzzle is re-based onto the
        /// puppet's CURRENT position, so the beam stays attached to a ship that is still
        /// interpolating rather than trailing behind it.
        /// </summary>
        internal static void LateTick()
        {
            if (RemoteHeld.Count == 0) return;
            var stale = new List<(byte, byte)>();

            foreach (var kv in RemoteHeld)
            {
                var (slot, holder) = kv.Key;
                var r = kv.Value;
                if (Time.unscaledTime - r.LastSeenAt > StaleSeconds) { stale.Add(kv.Key); continue; }

                if (!ShipSync.ShipsBySlot.TryGetValue(slot, out var ship) || ship == null) { stale.Add(kv.Key); continue; }
                // Never drive a local ship. Both of these skip WITHOUT going stale, because the
                // entry is still valid and may become drivable again (an authority handoff can flip
                // a puppet local and back). The loop has to stop anyway: while messages keep
                // arriving the entry never ages out, so without this an ownership flip mid-hold
                // would leave the sound playing with nothing left to stop it.
                if (ship.GetComponent<RemotePuppet>() == null) { StopLoop(kv.Key); continue; }
                var weapon = ProjectileSync.HolderWeaponOf(ship, holder);
                if (!(weapon is HitscanWeapon)) { StopLoop(kv.Key); continue; }

                // Re-base the muzzle: the offset the owner reported, applied to where the puppet
                // is NOW. Using the raw position would hang the beam off the ship by exactly the
                // interpolation error.
                Vector2 muzzle = r.BodyPos != Vector2.zero
                    ? (Vector2)ship.transform.position + (r.Pos - r.BodyPos)
                    : r.Pos;

                try
                {
                    weapon.IsTriggerPulled = true;      // the state OnBarrelMoved gates on
                    weapon.OnBarrelMoved(muzzle, r.Dir);
                    StartLoopIfNeeded(kv.Key, weapon, ship);
                }
                catch { stale.Add(kv.Key); }
            }

            foreach (var k in stale) { RemoteHeld.Remove(k); StopBeam(k.Item1, k.Item2); }
        }

        /// <summary>Start the continuous-fire loop for a puppet that has just begun holding, if the
        /// weapon declares one. Idempotent: called every frame the trigger is down, and does nothing
        /// once a handle exists.</summary>
        private static void StartLoopIfNeeded((byte slot, byte holder) key, WeaponBase weapon, Ship ship)
        {
            if (LoopHandles.ContainsKey(key)) return;
            var sfx = weapon.ContinousShootSfx;
            if (string.IsNullOrEmpty(sfx)) return;      // most weapons have no loop, including vanilla ones
            int handle = AudioManager.PlaySfx(sfx, ship.transform);
            // -1 is PlaySfx's "did not play" (no manager, unknown guid). Storing it would make this
            // look started forever and never retry, so leave the slot empty and try again next frame.
            if (handle == -1) return;
            LoopHandles[key] = handle;
            // Once per hold, not per frame. This is the only externally visible evidence that a
            // remote beam is audible, so a harness can assert on it instead of needing ears.
            Plugin.Log.LogInfo($"[HeldFire] P{key.slot + 1} holder {key.holder}: continuous sfx '{sfx}' started (handle {handle})");
        }

        /// <summary>Silence a held weapon's loop without touching its visual or its entry.</summary>
        private static void StopLoop((byte slot, byte holder) key)
        {
            if (!LoopHandles.TryGetValue(key, out var handle)) return;
            LoopHandles.Remove(key);
            try { AudioManager.Stop(handle); } catch { }
            Plugin.Log.LogInfo($"[HeldFire] P{key.slot + 1} holder {key.holder}: continuous sfx stopped (handle {handle})");
        }

        /// <summary>Release the puppet's trigger and let OnBarrelMoved's else branch clear the
        /// visual — the same path the game itself uses, rather than reaching into the visual.</summary>
        private static void StopBeam(byte slot, byte holder)
        {
            // Audio first, and outside the visual's try block: a weapon that has been destroyed or
            // swapped still has a loop playing, and that must be stopped even when nothing below
            // this can run. This is the path a dropped release message lands on (via StaleSeconds),
            // so it is the one that decides whether a lost packet costs a stuck sound.
            StopLoop((slot, holder));

            try
            {
                if (!ShipSync.ShipsBySlot.TryGetValue(slot, out var ship) || ship == null) return;
                if (ship.GetComponent<RemotePuppet>() == null) return;
                var weapon = ProjectileSync.HolderWeaponOf(ship, holder);
                if (!(weapon is HitscanWeapon)) return;
                weapon.IsTriggerPulled = false;
                weapon.OnBarrelMoved(ship.transform.position, Vector2.right);
                // Vanilla's StopContinousSound plays ReleaseSfx as it stops the loop, so a peer
                // hears the same tail-off the shooter does. Matching it here keeps the two machines
                // sounding alike; skipping it would make every remote beam end abruptly.
                if (!string.IsNullOrEmpty(weapon.ReleaseSfx))
                    AudioManager.PlaySfx(weapon.ReleaseSfx, ship.transform.position);
            }
            catch { }
        }
    }
}
