namespace PunkMultiverse.Core
{
    /// <summary>One occupied lobby slot. The host slot may change during migration.</summary>
    public sealed class NetPlayer
    {
        public byte Slot;
        public ulong PeerId;       // current transport route; changes whenever a peer reattaches
        public ulong IdentityId;   // stable SteamID/install identity; survives transport migration
        public string Name;
        public byte ColorIndex;
        public bool Ready;
        public bool IsLocal;
        public bool Connected = true; // false = slot reserved for a dropped player (rejoin)
        // A real mid-run disconnect destroys the ship. The slot can only re-enter after the
        // party unlocks another station; host migration reattachments do not set this flag.
        public bool NeedsStationRespawn;
        public int RespawnStationNetId;
        public bool ModsMismatch;  // plugin set differs from the host's (Warn policy marker)
        public bool IsCoordinator; // dedicated shipless server slot: no ship, no puppet, plays nobody
        public bool IsAdmin;       // session admin of a coordinator session: gets the host-like UI
        public int RttMs = -1;     // -1 = unknown
        // Mirrored from the roster so the lobby can say WHY someone is not ready. On the host
        // these are the authority; on a client they are whatever the last LobbyState carried.
        public byte ContentState;   // Content.ContentState
        public byte ContentPercent;

        /// <summary>Set once in-game: the Ship this player controls (local) or their puppet (remote).</summary>
        public Ship Ship;

        public override string ToString() => $"P{Slot + 1} '{Name}' peer={PeerId} identity={IdentityId:X}{(IsLocal ? " (local)" : "")}";
    }
}
