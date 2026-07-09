namespace PunkMultiverse.Core
{
    /// <summary>One occupied lobby slot. Slot 0 is always the host.</summary>
    public sealed class NetPlayer
    {
        public byte Slot;
        public ulong PeerId;       // SteamID64 on Steam, small int on loopback; the local player's own id for IsLocal
        public string Name;
        public byte ColorIndex;
        public bool Ready;
        public bool IsLocal;
        public bool Connected = true; // false = slot reserved for a dropped player (rejoin)
        public int RttMs = -1;     // -1 = unknown

        /// <summary>Set once in-game: the Ship this player controls (local) or their puppet (remote).</summary>
        public Ship Ship;

        public override string ToString() => $"P{Slot + 1} '{Name}' peer={PeerId}{(IsLocal ? " (local)" : "")}";
    }
}
