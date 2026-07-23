using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using LiteNetLib;

namespace PunkMultiverse.Transport
{
    /// <summary>
    /// Direct-UDP transport on LiteNetLib — the Docker/LAN/no-Steam deployment ("Udp" in
    /// config). Same star topology as every other transport: clients talk only to the host.
    ///
    /// Channel mapping (the WS8.2 contract — each reliable NetChannel must be its own ordered
    /// stream so sequencer barriers hold, and Combat must never head-of-line block behind an
    /// Events terrain burst): Control/Events/Combat ride ReliableOrdered on LiteNetLib channels
    /// 0/1/2 — independently ordered per channel by the library. State rides Unreliable.
    /// A 1-byte NetChannel prefix travels in the payload (the SteamServerTransport pattern) so
    /// the receive side never has to reverse-map delivery metadata.
    ///
    /// Threading: LiteNetLib services the socket on its own thread and queues events;
    /// PollEvents dispatches them on the main thread from Poll() — same shape as the
    /// ReceivePump used by the other transports (background receive, main-thread dispatch).
    ///
    /// Peer ids: host is always 1 (the loopback convention NetSession expects); clients get
    /// 2 + LiteNetLib's per-manager peer id. A client learns its own id from NetPeer.RemoteId
    /// (its id inside the HOST's manager), available once the connection completes — which is
    /// before PeerConnected fires, so the HELLO always carries the right id.
    /// </summary>
    public sealed class LiteNetTransport : ITransport, INetEventListener
    {
        private const ulong HostPeerId = 1;
        private const int MaxPeers = 8;
        // The accept key gates random UDP traffic, not security (HELLO still validates
        // protocol + mod versions). Bump if the wire framing here ever changes shape.
        private const string ConnectionKey = "punkmv-udp-1";

        private readonly string _defaultAddress;
        private readonly int _port;
        private NetManager _manager;
        private NetPeer _hostPeer;   // client mode: our single connection
        private string _connectHost; // client mode: last target, for auto-reconnect
        private int _connectPort;
        private int _reconnectAtTick = -1; // Environment.TickCount deadline; -1 = no retry armed
        private readonly Dictionary<int, ulong> _idByPeer = new Dictionary<int, ulong>();
        private readonly Dictionary<ulong, NetPeer> _peerById = new Dictionary<ulong, NetPeer>();
        private byte[] _sendBuf = new byte[64 * 1024];

        public bool IsRunning { get; private set; }
        public bool IsHost { get; private set; }
        public ulong LocalPeerId { get; private set; }

        /// <summary>Mirrors LoopbackUdpTransport: true when the peer's connection really closed
        /// (remote shutdown) rather than timing out (possibly just stalled). NetSession's
        /// host-loss policy can branch on this the same way it does for loopback.</summary>
        public bool LastDisconnectWasRemote { get; private set; }

        public event Action<ulong> PeerConnected;
        public event Action<ulong> PeerDisconnected;
        public event Action<ulong, NetChannel, ArraySegment<byte>> DataReceived;

        public LiteNetTransport(string defaultAddress, int port)
        {
            _defaultAddress = defaultAddress;
            _port = port;
        }

        private NetManager CreateManager()
        {
            var m = new NetManager(this)
            {
                AutoRecycle = true,
                UpdateTime = 10,             // logic tick: acks/resends/flush every 10ms
                DisconnectTimeout = 10000,   // match loopback's stall tolerance (level load, GC)
                ChannelsCount = 4,           // one ordered stream per NetChannel
                UnconnectedMessagesEnabled = false,
                IPv6Enabled = false,         // Docker/LAN target; avoids dual-stack bind surprises
            };
            return m;
        }

        public void StartHost()
        {
            _manager = CreateManager();
            if (!_manager.Start(_port))
                throw new InvalidOperationException($"UDP port {_port} unavailable");
            IsHost = true;
            IsRunning = true;
            LocalPeerId = HostPeerId;
            Plugin.Log.LogInfo($"[Udp] hosting on *:{_port} (LiteNetLib)");
        }

        public void StartClient(string address)
        {
            string host = _defaultAddress;
            int port = _port;
            if (!string.IsNullOrWhiteSpace(address))
            {
                var trimmed = address.Trim();
                int colon = trimmed.LastIndexOf(':');
                if (colon > 0 && int.TryParse(trimmed.Substring(colon + 1), out int parsedPort))
                {
                    host = trimmed.Substring(0, colon);
                    port = parsedPort;
                }
                else host = trimmed;
            }
            _manager = CreateManager();
            if (!_manager.Start())
                throw new InvalidOperationException("UDP client socket failed to start");
            IsHost = false;
            IsRunning = true;
            _connectHost = host;
            _connectPort = port;
            _manager.Connect(host, port, ConnectionKey);
            Plugin.Log.LogInfo($"[Udp] connecting to {host}:{port} (LiteNetLib)");
        }

        public bool Send(ulong peer, NetChannel channel, ArraySegment<byte> data, bool reliable)
        {
            if (!IsRunning) return false;
            NetPeer target;
            if (IsHost)
            {
                if (!_peerById.TryGetValue(peer, out target) || target == null) return false;
            }
            else
            {
                target = _hostPeer;
                if (target == null) return false;
            }
            if (target.ConnectionState != ConnectionState.Connected) return false;

            int len = data.Count + 1;
            if (_sendBuf.Length < len) _sendBuf = new byte[Math.Max(len, _sendBuf.Length * 2)];
            _sendBuf[0] = (byte)channel;
            Buffer.BlockCopy(data.Array, data.Offset, _sendBuf, 1, data.Count);
            try
            {
                if (reliable)
                    // Per-channel ReliableOrdered: each NetChannel is its own ordered stream.
                    target.Send(_sendBuf, 0, len, (byte)channel, DeliveryMethod.ReliableOrdered);
                else
                    target.Send(_sendBuf, 0, len, DeliveryMethod.Unreliable);
                return true;
            }
            catch (TooBigPacketException)
            {
                // Unreliable has a hard MTU; senders already chunk snapshots to ~1100B, so this
                // is a bug siren, not backpressure.
                Plugin.Log.LogWarning($"[Udp] oversized {(reliable ? "reliable" : "unreliable")} send dropped ({len}B on {channel})");
                return false;
            }
        }

        public void Poll()
        {
            _manager?.PollEvents();
            // Client-side auto-reconnect after a host stall — the contract the reconnect-in-
            // place policy (BeginLoopbackReconnect) expects from non-Steam transports. Armed
            // only for timeouts, never for a remote close (that fails the session upstream).
            if (!IsHost && IsRunning && _reconnectAtTick != -1 && _hostPeer == null
                && unchecked(Environment.TickCount - _reconnectAtTick) >= 0
                && _manager != null && _connectHost != null)
            {
                _reconnectAtTick = Environment.TickCount + 2000;
                Plugin.Log.LogInfo($"[Udp] retrying connect to {_connectHost}:{_connectPort}");
                try { _manager.Connect(_connectHost, _connectPort, ConnectionKey); } catch { }
            }
        }

        public void Stop()
        {
            IsRunning = false;
            try { _manager?.Stop(true); } catch { }
            _manager = null;
            _hostPeer = null;
            _idByPeer.Clear();
            _peerById.Clear();
        }

        public void Dispose() => Stop();

        // ---------------------------------------------------------------- INetEventListener

        void INetEventListener.OnConnectionRequest(ConnectionRequest request)
        {
            if (!IsHost || _manager.ConnectedPeersCount >= MaxPeers) { request.Reject(); return; }
            request.AcceptIfKey(ConnectionKey);
        }

        void INetEventListener.OnPeerConnected(NetPeer peer)
        {
            if (IsHost)
            {
                ulong id = (ulong)(peer.Id + 2); // 1 is the host; LiteNetLib ids are 0-based
                _idByPeer[peer.Id] = id;
                _peerById[id] = peer;
                Plugin.Log.LogInfo($"[Udp] peer {id} connected ({peer.Address}:{peer.Port})");
                PeerConnected?.Invoke(id);
            }
            else
            {
                _hostPeer = peer;
                _reconnectAtTick = -1;
                // RemoteId = our id inside the host's manager — the same number the host
                // computes, so both sides agree on who we are before the HELLO goes out.
                LocalPeerId = (ulong)(peer.RemoteId + 2);
                Plugin.Log.LogInfo($"[Udp] connected to host as peer {LocalPeerId}");
                PeerConnected?.Invoke(HostPeerId);
            }
        }

        void INetEventListener.OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
        {
            LastDisconnectWasRemote = info.Reason == DisconnectReason.RemoteConnectionClose
                                   || info.Reason == DisconnectReason.DisconnectPeerCalled;
            if (IsHost)
            {
                if (_idByPeer.TryGetValue(peer.Id, out ulong id))
                {
                    _idByPeer.Remove(peer.Id);
                    _peerById.Remove(id);
                    Plugin.Log.LogInfo($"[Udp] peer {id} disconnected ({info.Reason})");
                    PeerDisconnected?.Invoke(id);
                }
            }
            else
            {
                _hostPeer = null;
                // A timeout means the host may just be stalled — arm the reconnect loop.
                _reconnectAtTick = LastDisconnectWasRemote ? -1 : Environment.TickCount + 2000;
                Plugin.Log.LogInfo($"[Udp] host connection lost ({info.Reason})");
                PeerDisconnected?.Invoke(HostPeerId);
            }
        }

        void INetEventListener.OnNetworkReceive(NetPeer peer, NetPacketReader reader,
            byte channelNumber, DeliveryMethod deliveryMethod)
        {
            int size = reader.UserDataSize;
            if (size < 1) return;
            var channel = (NetChannel)reader.RawData[reader.UserDataOffset];
            ulong from = IsHost
                ? (_idByPeer.TryGetValue(peer.Id, out ulong id) ? id : 0)
                : HostPeerId;
            if (from == 0) return; // data from a peer we never admitted
            DataReceived?.Invoke(from, channel,
                new ArraySegment<byte>(reader.RawData, reader.UserDataOffset + 1, size - 1));
        }

        void INetEventListener.OnNetworkError(IPEndPoint endPoint, SocketError socketError)
            => Plugin.Log.LogWarning($"[Udp] socket error {socketError} from {endPoint}");

        void INetEventListener.OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint,
            NetPacketReader reader, UnconnectedMessageType messageType) { }

        void INetEventListener.OnNetworkLatencyUpdate(NetPeer peer, int latency) { }
    }
}
