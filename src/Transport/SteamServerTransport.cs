using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Steamworks;

namespace PunkMultiverse.Transport
{
    /// <summary>
    /// Transport over an anonymous Steam GAME SERVER identity (SDR NAT traversal + encryption +
    /// IP privacy, no port forwarding) — the friends-facing sidecar/dedicated deployment. Proven
    /// by spike A. Peer ids are SteamID64s; a client's join "address" is the server's SteamID64.
    ///
    /// Two connections per peer, split by virtual port, because a single reliable Sockets stream
    /// would head-of-line-block Combat behind an Events/terrain burst (see spec §3):
    ///   vport 0 (bulk) = Control + Events   — both bulk-tolerant reliable
    ///   vport 1 (fast) = Combat + State     — Combat reliable-no-nagle on its own uncongested
    ///                                          stream; State unreliable, never blocks anything
    /// A 1-byte channel prefix preserves all four logical channels across the two connections.
    /// Each reliable channel lives in exactly one ordered stream, so the WS8.2 sequence barriers
    /// hold ([Seq]=0 is the proof gate).
    ///
    /// Roles never mix in one process: the coordinator runs the SERVER half
    /// (SteamGameServerNetworkingSockets); every player runs the CLIENT half (user
    /// SteamNetworkingSockets). Logon lifecycle belongs to <see cref="GameServerBootstrap"/>.
    /// </summary>
    public sealed class SteamServerTransport : ITransport
    {
        private const int MaxMessagesPerPoll = 64;
        private const int LogonWaitMs = 30_000;
        private const float HalfConnectTimeout = 15f; // a pair that half-connects and stalls is dead

        private static int Vport(NetChannel channel) => (int)channel & 1; // Control/Events=0, State/Combat=1

        private static int Flags(NetChannel channel) =>
            channel == NetChannel.State ? Constants.k_nSteamNetworkingSend_UnreliableNoNagle
            : channel == NetChannel.Combat ? Constants.k_nSteamNetworkingSend_ReliableNoNagle
            : Constants.k_nSteamNetworkingSend_Reliable;

        private sealed class PeerLink
        {
            public ulong PeerId;
            public HSteamNetConnection Bulk = HSteamNetConnection.Invalid;
            public HSteamNetConnection Fast = HSteamNetConnection.Invalid;
            public bool BulkUp, FastUp, Announced;
            public float FirstSeen;
            public bool BothUp => BulkUp && FastUp;
        }

        // server role
        private bool _serverListenersOpen;
        private HSteamListenSocket _listenBulk, _listenFast;
        private HSteamNetPollGroup _pollGroup;
        private readonly Dictionary<HSteamListenSocket, int> _listenVport = new Dictionary<HSteamListenSocket, int>();
        private readonly Dictionary<ulong, PeerLink> _peers = new Dictionary<ulong, PeerLink>();
        private readonly Dictionary<HSteamNetConnection, ulong> _connToPeer = new Dictionary<HSteamNetConnection, ulong>();
        private Callback<SteamNetConnectionStatusChangedCallback_t> _cbServerConn;

        // client role
        private PeerLink _server;
        private Callback<SteamNetConnectionStatusChangedCallback_t> _cbClientConn;

        private readonly IntPtr[] _msgPtrs = new IntPtr[MaxMessagesPerPoll];
        private byte[] _frameBuf = new byte[64 * 1024 + 1];
        private byte[] _recvBuf = new byte[64 * 1024];

        /// <summary>Incoming-connection gate. Session-layer HELLO + mod-manifest check still gate a
        /// real join; v1 defaults to accept-all (playtest posture).</summary>
        public Func<CSteamID, bool> AllowPeer;

        public bool IsRunning { get; private set; }
        public bool IsHost { get; private set; }
        public ulong LocalPeerId { get; private set; }

        public event Action<ulong> PeerConnected;
        public event Action<ulong> PeerDisconnected;
        public event Action<ulong, NetChannel, ArraySegment<byte>> DataReceived;

        // ------------------------------------------------------------------ host (server) role

        public void StartHost()
        {
            IsHost = true;
            if (!GameServerBootstrap.EnsureStarted())
                throw new InvalidOperationException("game server init failed");

            // Block until logon so LocalPeerId (the server id) is valid before HostSession
            // continues — a headless coordinator blocking a couple seconds at startup is fine, and
            // it avoids a whole class of "peer id 0 in the roster" ordering bugs. Pump the game
            // server callbacks ourselves during the wait (NetSession.Update isn't running inside us).
            var deadline = DateTime.UtcNow.AddMilliseconds(LogonWaitMs);
            while (!GameServerBootstrap.LoggedOn && DateTime.UtcNow < deadline)
            {
                GameServer.RunCallbacks();
                if (GameServerBootstrap.LogonFailed) break;
                System.Threading.Thread.Sleep(50);
            }
            if (!GameServerBootstrap.LoggedOn)
                throw new InvalidOperationException("game server logon timed out");

            LocalPeerId = GameServerBootstrap.ServerSteamId;
            _cbServerConn = Callback<SteamNetConnectionStatusChangedCallback_t>.CreateGameServer(OnServerConn);
            OpenListeners();
            IsRunning = true;
            Plugin.Log.LogInfo($"[SteamServer] host up — server id {LocalPeerId}, join code {LocalPeerId}");
        }

        private void OpenListeners()
        {
            if (_serverListenersOpen) return;
            _pollGroup = SteamGameServerNetworkingSockets.CreatePollGroup();
            _listenBulk = SteamGameServerNetworkingSockets.CreateListenSocketP2P(0, 0, null);
            _listenFast = SteamGameServerNetworkingSockets.CreateListenSocketP2P(1, 0, null);
            _listenVport[_listenBulk] = 0;
            _listenVport[_listenFast] = 1;
            _serverListenersOpen = true;
        }

        private void OnServerConn(SteamNetConnectionStatusChangedCallback_t s)
        {
            var state = s.m_info.m_eState;
            var conn = s.m_hConn;
            ulong remote = s.m_info.m_identityRemote.GetSteamID64();
            switch (state)
            {
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting:
                {
                    bool allowed = NetConfig.AcceptAnySteamSession.Value
                        || AllowPeer == null // v1 default: accept-all, session HELLO gates the real join
                        || AllowPeer(new CSteamID(remote));
                    if (!allowed)
                    {
                        SteamGameServerNetworkingSockets.CloseConnection(conn, 0, "not permitted", false);
                        Plugin.Log.LogWarning($"[SteamServer] rejected connection from {remote}");
                        return;
                    }
                    SteamGameServerNetworkingSockets.AcceptConnection(conn);
                    break;
                }
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
                {
                    int vport = _listenVport.TryGetValue(s.m_info.m_hListenSocket, out int v) ? v : 0;
                    SteamGameServerNetworkingSockets.SetConnectionPollGroup(conn, _pollGroup);
                    _connToPeer[conn] = remote;
                    if (!_peers.TryGetValue(remote, out var link))
                        _peers[remote] = link = new PeerLink { PeerId = remote, FirstSeen = UnityEngine.Time.unscaledTime };
                    if (vport == 0) { link.Bulk = conn; link.BulkUp = true; }
                    else { link.Fast = conn; link.FastUp = true; }
                    if (link.BothUp && !link.Announced)
                    {
                        link.Announced = true;
                        Plugin.Log.LogInfo($"[SteamServer] peer {remote} fully connected (both lanes)");
                        PeerConnected?.Invoke(remote);
                    }
                    break;
                }
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
                    DropServerPeer(remote, conn, s.m_info.m_eEndReason, s.m_info.m_szEndDebug);
                    break;
            }
        }

        private void DropServerPeer(ulong remote, HSteamNetConnection conn, int reason, string debug)
        {
            SteamGameServerNetworkingSockets.CloseConnection(conn, 0, null, false);
            _connToPeer.Remove(conn);
            if (!_peers.TryGetValue(remote, out var link)) return;
            // Either lane dying takes the peer down; close its twin too.
            var twin = link.Bulk == conn ? link.Fast : link.Bulk;
            if (twin != HSteamNetConnection.Invalid)
            {
                SteamGameServerNetworkingSockets.CloseConnection(twin, 0, null, false);
                _connToPeer.Remove(twin);
            }
            _peers.Remove(remote);
            bool wasAnnounced = link.Announced;
            Plugin.Log.LogInfo($"[SteamServer] peer {remote} lane down (reason={reason} '{debug}')");
            if (wasAnnounced) PeerDisconnected?.Invoke(remote);
        }

        // ------------------------------------------------------------------ client (user) role

        public void StartClient(string address)
        {
            if (!ulong.TryParse(address, out ulong serverId) || serverId == 0)
                throw new ArgumentException($"SteamServer transport expects a SteamID64 join code, got '{address}'");
            IsHost = false;
            LocalPeerId = SteamUser.GetSteamID().m_SteamID; // game inits Steam at boot
            _cbClientConn = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnClientConn);
            _server = new PeerLink { PeerId = serverId, FirstSeen = UnityEngine.Time.unscaledTime };

            var identity = new SteamNetworkingIdentity();
            identity.SetSteamID64(serverId);
            _server.Bulk = SteamNetworkingSockets.ConnectP2P(ref identity, 0, 0, null);
            _server.Fast = SteamNetworkingSockets.ConnectP2P(ref identity, 1, 0, null);
            IsRunning = true;
            Plugin.Log.LogInfo($"[SteamServer] client dialing server {serverId} (bulk+fast lanes)");
        }

        private void OnClientConn(SteamNetConnectionStatusChangedCallback_t s)
        {
            if (_server == null) return;
            var conn = s.m_hConn;
            bool isBulk = conn == _server.Bulk, isFast = conn == _server.Fast;
            if (!isBulk && !isFast) return;
            switch (s.m_info.m_eState)
            {
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
                    if (isBulk) _server.BulkUp = true; else _server.FastUp = true;
                    if (_server.BothUp && !_server.Announced)
                    {
                        _server.Announced = true;
                        Plugin.Log.LogInfo($"[SteamServer] connected to server {_server.PeerId} (both lanes)");
                        PeerConnected?.Invoke(_server.PeerId);
                    }
                    break;
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
                case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
                {
                    ulong id = _server.PeerId;
                    bool wasAnnounced = _server.Announced;
                    Plugin.Log.LogWarning($"[SteamServer] server lane down: state={s.m_info.m_eState} reason={s.m_info.m_eEndReason} '{s.m_info.m_szEndDebug}'");
                    CloseClient();
                    if (wasAnnounced) PeerDisconnected?.Invoke(id);
                    break;
                }
            }
        }

        // ------------------------------------------------------------------ send / receive

        public bool Send(ulong peer, NetChannel channel, ArraySegment<byte> data, bool reliable)
        {
            if (!IsRunning) return false;
            HSteamNetConnection conn = ConnFor(peer, channel);
            if (conn == HSteamNetConnection.Invalid) return false;

            int total = data.Count + 1;
            if (_frameBuf.Length < total) Array.Resize(ref _frameBuf, total);
            _frameBuf[0] = (byte)channel;
            if (data.Count > 0) Buffer.BlockCopy(data.Array, data.Offset, _frameBuf, 1, data.Count);

            var handle = GCHandle.Alloc(_frameBuf, GCHandleType.Pinned);
            try
            {
                EResult result = IsHost
                    ? SteamGameServerNetworkingSockets.SendMessageToConnection(conn, handle.AddrOfPinnedObject(), (uint)total, Flags(channel), out _)
                    : SteamNetworkingSockets.SendMessageToConnection(conn, handle.AddrOfPinnedObject(), (uint)total, Flags(channel), out _);
                if (result == EResult.k_EResultLimitExceeded)
                    Plugin.Log.LogDebug($"[SteamServer] send buffer full for {peer} ch{(int)channel}");
                else if (result != EResult.k_EResultOK && result != EResult.k_EResultNoConnection)
                    Plugin.Log.LogWarning($"[SteamServer] send to {peer} ch{(int)channel} failed: {result}");
                if (result == EResult.k_EResultOK)
                {
                    Core.NetStats.AddOut(channel, data.Count);
                    Core.NetSeq.NoteSent(peer, channel);
                    return true;
                }
                return false;
            }
            finally { handle.Free(); }
        }

        private HSteamNetConnection ConnFor(ulong peer, NetChannel channel)
        {
            bool fast = Vport(channel) == 1;
            if (IsHost)
                return _peers.TryGetValue(peer, out var link) ? (fast ? link.Fast : link.Bulk) : HSteamNetConnection.Invalid;
            if (_server == null || _server.PeerId != peer) return HSteamNetConnection.Invalid;
            return fast ? _server.Fast : _server.Bulk;
        }

        public void Poll()
        {
            if (!IsRunning) return;
            if (IsHost)
            {
                int count;
                do
                {
                    count = SteamGameServerNetworkingSockets.ReceiveMessagesOnPollGroup(_pollGroup, _msgPtrs, MaxMessagesPerPoll);
                    for (int i = 0; i < count; i++) DispatchServer(_msgPtrs[i]);
                } while (count == MaxMessagesPerPoll);
            }
            else if (_server != null)
            {
                DrainClientConn(_server.Bulk);
                DrainClientConn(_server.Fast);
                if (!_server.Announced && UnityEngine.Time.unscaledTime - _server.FirstSeen > HalfConnectTimeout)
                {
                    ulong id = _server.PeerId;
                    Plugin.Log.LogWarning($"[SteamServer] server {id} did not fully connect within {HalfConnectTimeout:0}s");
                    CloseClient();
                    PeerDisconnected?.Invoke(id);
                }
            }
        }

        private void DispatchServer(IntPtr msgPtr)
        {
            var msg = Marshal.PtrToStructure<SteamNetworkingMessage_t>(msgPtr);
            try
            {
                if (!_connToPeer.TryGetValue(msg.m_conn, out ulong peer)) return;
                Deliver(peer, msg);
            }
            finally { SteamNetworkingMessage_t.Release(msgPtr); }
        }

        private void DrainClientConn(HSteamNetConnection conn)
        {
            if (conn == HSteamNetConnection.Invalid) return;
            int count;
            do
            {
                count = SteamNetworkingSockets.ReceiveMessagesOnConnection(conn, _msgPtrs, MaxMessagesPerPoll);
                for (int i = 0; i < count; i++)
                {
                    var msg = Marshal.PtrToStructure<SteamNetworkingMessage_t>(_msgPtrs[i]);
                    try { Deliver(_server.PeerId, msg); }
                    finally { SteamNetworkingMessage_t.Release(_msgPtrs[i]); }
                }
            } while (count == MaxMessagesPerPoll);
        }

        private void Deliver(ulong peer, SteamNetworkingMessage_t msg)
        {
            int size = msg.m_cbSize;
            if (size < 1) return; // must at least hold the channel byte
            int payload = size - 1;
            if (payload > _recvBuf.Length) Array.Resize(ref _recvBuf, payload);
            byte channel = Marshal.ReadByte(msg.m_pData, 0);
            if (channel > (byte)NetChannel.Combat) return; // corrupt frame
            if (payload > 0) Marshal.Copy(msg.m_pData + 1, _recvBuf, 0, payload);
            DataReceived?.Invoke(peer, (NetChannel)channel, new ArraySegment<byte>(_recvBuf, 0, payload));
        }

        // ------------------------------------------------------------------ teardown

        private void CloseClient()
        {
            if (_server == null) return;
            if (_server.Bulk != HSteamNetConnection.Invalid) SteamNetworkingSockets.CloseConnection(_server.Bulk, 0, null, false);
            if (_server.Fast != HSteamNetConnection.Invalid) SteamNetworkingSockets.CloseConnection(_server.Fast, 0, null, false);
            _server = null;
        }

        public void Stop()
        {
            if (!IsRunning && _server == null && !_serverListenersOpen) return;
            IsRunning = false;
            if (IsHost)
            {
                foreach (var link in _peers.Values)
                {
                    if (link.Bulk != HSteamNetConnection.Invalid) SteamGameServerNetworkingSockets.CloseConnection(link.Bulk, 0, null, false);
                    if (link.Fast != HSteamNetConnection.Invalid) SteamGameServerNetworkingSockets.CloseConnection(link.Fast, 0, null, false);
                }
                _peers.Clear();
                _connToPeer.Clear();
                if (_serverListenersOpen)
                {
                    SteamGameServerNetworkingSockets.CloseListenSocket(_listenBulk);
                    SteamGameServerNetworkingSockets.CloseListenSocket(_listenFast);
                    SteamGameServerNetworkingSockets.DestroyPollGroup(_pollGroup);
                    _listenVport.Clear();
                    _serverListenersOpen = false;
                }
                _cbServerConn?.Dispose(); _cbServerConn = null;
                // Note: the game-server IDENTITY stays logged on (GameServerBootstrap owns it,
                // shut down at process exit) — a re-hosted session reuses it.
            }
            else
            {
                CloseClient();
                _cbClientConn?.Dispose(); _cbClientConn = null;
            }
            Plugin.Log.LogInfo("[SteamServer] transport stopped");
        }

        public void Dispose() => Stop();
    }
}
