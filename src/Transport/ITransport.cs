using System;

namespace PunkMultiverse.Transport
{
    public enum NetChannel : byte
    {
        Control = 0,  // reliable: handshake, lobby, run flow, manifest, rejoin
        State = 1,    // unreliable: ship/entity snapshots, fire events, ping
        Events = 2,   // reliable: cells, authority, progression (bulk-tolerant)
        Combat = 3,   // reliable + no-nagle: damage, kills, ship life — small and
                      // latency-critical, never queued behind a terrain burst
    }

    /// <summary>
    /// Minimal peer transport. Star topology: clients only ever talk to the host peer; the host
    /// talks to every client and relays. Peer ids are SteamID64s on Steam, small ints on loopback
    /// (host is always 1).
    /// </summary>
    public interface ITransport : IDisposable
    {
        bool IsRunning { get; }
        bool IsHost { get; }
        ulong LocalPeerId { get; }

        event Action<ulong> PeerConnected;
        event Action<ulong> PeerDisconnected;
        /// <summary>Payload segment is only valid for the duration of the callback.</summary>
        event Action<ulong, NetChannel, ArraySegment<byte>> DataReceived;

        void StartHost();
        /// <summary>Loopback: "host:port". Steam: decimal SteamID64 of the host.</summary>
        void StartClient(string address);
        /// <summary>False = the transport did not accept the message (send buffer full, no
        /// session). Bulk senders (terrain streaming) treat that as backpressure and retry;
        /// fire-and-forget callers may ignore the result.</summary>
        bool Send(ulong peer, NetChannel channel, ArraySegment<byte> data, bool reliable);
        /// <summary>Pump receives + timers. Call once per frame.</summary>
        void Poll();
        void Stop();
    }
}
