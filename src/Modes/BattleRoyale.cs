using System.Collections.Generic;
using PunkMultiverse.Core;
using PunkMultiverse.Protocol;
using PunkMultiverse.Transport;
using UnityEngine;

namespace PunkMultiverse.Modes
{
    /// <summary>
    /// Battle Royale: last player standing (full design in docs/BATTLE_ROYALE.md).
    ///
    /// The mode is deliberately built out of systems that already work rather than new sync
    /// primitives. The host paints the lava ring with the game's own <c>Level.SetCell</c>, so it
    /// replicates through the existing terrain pipeline exactly like vanilla fog conversions;
    /// stations open through the game's own unlock primitive, which the existing progression sync
    /// already replicates; care-package loot rides the existing kill-credit and
    /// killer-drops-locally paths. What is genuinely new is only the match's own state: the ring
    /// schedule, placements, and the announcements that tie them together.
    ///
    /// Authority: the host owns the match clock, the ring, placements, and the win condition, and
    /// broadcasts them. Clients never recompute the ring — they render the HUD from
    /// <see cref="RingStateMsg"/> and apply their OWN out-of-zone damage from it, because a ship's
    /// health belongs to the machine that owns the ship.
    /// </summary>
    internal static partial class BattleRoyale
    {
        // ---------------------------------------------------------------- match state (host)

        private static bool _active;              // a BR match is running on this host
        private static float _matchStart;         // Time.unscaledTime at go-live
        private static float _matchSeconds;       // total match length
        private static float _ringStartSeconds;   // grace before stage 1
        private static int _stages;               // announced shrink stages
        private static int _lastAnnouncedStage;
        private static float _nextRingBroadcastAt;

        private static Vector2 _center;           // ring center (open-area pick)
        private static float _startRadius;        // covers the whole map from _center

        // Placement bookkeeping: the roster is snapshotted at go-live and the match is sealed, so
        // "how many players are in this match" never changes after it starts.
        private static readonly List<byte> MatchPlayers = new List<byte>();
        private static readonly HashSet<byte> Eliminated = new HashSet<byte>();
        private static readonly Dictionary<byte, byte> Placements = new Dictionary<byte, byte>();
        private static float _lastAliveSince = -1f;

        // ---------------------------------------------------------------- client-side mirror

        /// <summary>Last ring state received (or, on the host, produced). Clients read this for the
        /// HUD and for their own out-of-zone burn damage.</summary>
        public static RingStateMsg Ring { get; private set; }
        public static bool RingKnown { get; private set; }

        /// <summary>Whether the ring circles should be DRAWN yet (minimap and map screen).
        ///
        /// During the opening grace the ring is wider than the map and has not moved, so drawing
        /// "here is the boundary and here is where it is going" marks ground that is not yet in
        /// play and reads as though the match has already started closing in. Omar, 2026-07-28:
        /// don't show it "until the first ring starts". The zone DAMAGE and the rendered lava are
        /// not gated by this — they follow the real radius, which during the grace encloses
        /// everything anyway.</summary>
        public static bool RingVisible => RingKnown && (Ring.Closing || Ring.Stage >= 1);

        /// <summary>Whether the ring should still be DRAWN, including after the match has been
        /// decided. <see cref="Active"/> goes false the moment the win condition resolves, which
        /// used to make the rings and the zone vanish instantly — the map blanking out at the exact
        /// moment players are looking at where they died (Omar, 2026-07-28: "when the game is over
        /// the rings are just gone; we should retain them on the client"). The ring is still true
        /// until the run actually tears down, so it keeps being drawn while the session is InGame,
        /// whether or not the match is still being contested.</summary>
        public static bool RingPersists
        {
            get
            {
                var s = NetSession.Instance;
                return s != null && NetSession.Active && s.IsBattleRoyale
                       && s.State == SessionState.InGame && RingKnown;
            }
        }

        /// <summary>This player's final placement once eliminated (0 = still alive/none).</summary>
        public static byte LocalPlacement { get; private set; }
        public static byte LocalTotalPlayers { get; private set; }
        public static bool LocalIsWinner { get; private set; }

        /// <summary>True while a BR match is live on this machine — the single predicate every
        /// mode-specific behaviour keys off (damage scaling, hidden trackers, spawn scatter).</summary>
        public static bool Active
        {
            get
            {
                var s = NetSession.Instance;
                return s != null && NetSession.Active && s.IsBattleRoyale
                       && s.State == SessionState.InGame;
            }
        }

        public static void Reset()
        {
            _active = false;
            _matchStart = 0f;
            _lastAnnouncedStage = 0;
            _nextRingBroadcastAt = 0f;
            _lastAliveSince = -1f;
            MatchPlayers.Clear();
            Eliminated.Clear();
            Placements.Clear();
            Ring = default;
            RingKnown = false;
            LocalPlacement = 0;
            LocalTotalPlayers = 0;
            LocalIsWinner = false;
            ResetSelfDestruct();
            // The saved burn settings belong to the PREVIOUS run's ship object; carrying them into
            // the next run would restore another ship's numbers onto this one.
            ResetZoneFire();
            Patches.BattleRoyaleSpawn.Reset();
        }

        // ---------------------------------------------------------------- announcements

        /// <summary>Host: toast on every machine, including this one. The mod had no
        /// server-to-everyone channel before BR; this is it.</summary>
        public static void Announce(NetSession session, string text, float seconds = 6f)
        {
            if (session == null) return;
            Plugin.Log.LogInfo($"[BR] announce: {text}");
            UI.Toast.Show(text, seconds);
            if (!session.IsHost) return;
            var w = new NetWriter(128);
            new AnnounceMsg { Text = text, Seconds = seconds }.Write(w);
            session.SendToAll(NetChannel.Control, w.ToSegment(), reliable: true);
        }

        /// <summary>Client: an announcement arrived.</summary>
        public static void ApplyAnnounce(AnnounceMsg msg)
        {
            if (string.IsNullOrEmpty(msg.Text)) return;
            UI.Toast.Show(msg.Text, msg.Seconds > 0f ? msg.Seconds : 6f);
        }

        /// <summary>Client (and host, locally): the ring moved.</summary>
        public static void ApplyRingState(RingStateMsg msg)
        {
            Ring = msg;
            RingKnown = true;
        }

        // ---------------------------------------------------------------- care packages

        /// <summary>Live supply drops by netId → world position. Every alive player gets an arrow
        /// to these; they are the ONLY tracker arrows BR allows (players stay hidden).</summary>
        public static readonly Dictionary<int, Vector2> CarePackages = new Dictionary<int, Vector2>();

        public static void ApplyCarePackage(CarePackageMsg msg)
        {
            if (msg.Gone)
            {
                if (CarePackages.Remove(msg.NetId))
                    Plugin.Log.LogInfo($"[BR] care package #{msg.NetId} gone");
                return;
            }
            CarePackages[msg.NetId] = new Vector2(msg.X, msg.Y);
            Plugin.Log.LogInfo($"[BR] care package #{msg.NetId} at ({msg.X:0},{msg.Y:0})");
        }

        /// <summary>Someone was eliminated (or won). Drives the callout and, when it is us, the
        /// placement screen.</summary>
        public static void ApplyPlacement(PlacementMsg msg, NetSession session)
        {
            if (session == null) return;
            var player = msg.Slot < session.Players.Count ? session.Players[msg.Slot] : null;
            string name = player?.Name ?? $"P{msg.Slot + 1}";
            if (msg.Slot == session.LocalSlot)
            {
                LocalPlacement = msg.Placement;
                LocalTotalPlayers = msg.TotalPlayers;
                LocalIsWinner = msg.IsWinner;
            }
            if (msg.IsWinner)
            {
                UI.Toast.Show(msg.Slot == session.LocalSlot
                    ? $"VICTORY — YOU ARE THE LAST ONE STANDING (#1 OF {msg.TotalPlayers})"
                    : $"{name.ToUpperInvariant()} WINS", 8f);
                return;
            }
            if (msg.Slot == session.LocalSlot)
                UI.Toast.Show($"ELIMINATED — YOU PLACED #{msg.Placement} OF {msg.TotalPlayers}", 8f);
            else
                UI.Toast.Show($"{name.ToUpperInvariant()} ELIMINATED — {msg.AliveRemaining} REMAIN", 5f);
        }
    }
}
