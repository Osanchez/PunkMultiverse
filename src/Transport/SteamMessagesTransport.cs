using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Steamworks;

namespace PunkMultiverse.Transport
{
    /// <summary>
    /// Shipping transport over ISteamNetworkingMessages (identity-addressed datagrams with
    /// automatic NAT punch / SDR relay — no listen sockets, no port forwarding). Peer ids are
    /// SteamID64s. Sessions are implicit: the first send opens one, the remote side gets a
    /// session-request callback and accepts if the peer passes <see cref="AllowPeer"/>
    /// (wired to lobby membership by SteamLobbyController in M1).
    /// Relies on the game's own SteamManager for SteamAPI init + RunCallbacks pumping;
    /// NetConfig.PumpSteamCallbacks adds our own pump if that ever proves wrong.
    ///
    /// Receive runs through the shared <see cref="ReceivePump"/> when NetConfig.ThreadedReceive
    /// is on: a background thread copies each datagram into a pooled buffer and Poll() dispatches
    /// on the main thread within the frame budget. ISteamNetworkingMessages is thread-safe, so
    /// receiving overlaps main-thread sends. All events still fire from Poll — handler threading
    /// is unchanged.
    /// </summary>
    public sealed class SteamMessagesTransport : ITransport
    {
        private const int MaxMessagesPerPoll = 64;

        private readonly IntPtr[] _msgPtrs = new IntPtr[MaxMessagesPerPoll];       // inline path
        private readonly IntPtr[] _threadMsgPtrs = new IntPtr[MaxMessagesPerPoll]; // pump thread
        private byte[] _recvBuf = new byte[64 * 1024]; // inline path only
        private readonly HashSet<ulong> _knownPeers = new HashSet<ulong>();

        // _knownPeers stays main-thread-only: the pump thread never touches it, it only reads
        // from Steam and feeds the queue.
        private ReceivePump _pump;

        private Callback<SteamNetworkingMessagesSessionRequest_t> _sessionRequest;
        private Callback<SteamNetworkingMessagesSessionFailed_t> _sessionFailed;

        private ulong _hostSteamId; // client mode
        private bool _announceHostOnPoll;

        /// <summary>Gate for incoming sessions — set by the lobby controller to "is a member of my lobby".</summary>
        public Func<CSteamID, bool> AllowPeer;

        public bool IsRunning { get; private set; }
        public bool IsHost { get; private set; }
        public ulong LocalPeerId { get; private set; }

        public event Action<ulong> PeerConnected;
        public event Action<ulong> PeerDisconnected;
        public event Action<ulong, NetChannel, ArraySegment<byte>> DataReceived;

        public void StartHost() => Start(host: true, hostSteamId: 0);

        public void StartClient(string address)
        {
            if (!ulong.TryParse(address, out var hostId) || hostId == 0)
                throw new ArgumentException($"Steam transport expects a SteamID64, got '{address}'");
            Start(host: false, hostSteamId: hostId);
        }

        private void Start(bool host, ulong hostSteamId)
        {
            LocalPeerId = SteamUser.GetSteamID().m_SteamID; // throws if Steam not initialized — game inits it at boot
            IsHost = host;
            _hostSteamId = hostSteamId;
            _sessionRequest = Callback<SteamNetworkingMessagesSessionRequest_t>.Create(OnSessionRequest);
            _sessionFailed = Callback<SteamNetworkingMessagesSessionFailed_t>.Create(OnSessionFailed);
            IsRunning = true;
            if (NetConfig.ThreadedReceive.Value)
            {
                _pump = new ReceivePump();
                _pump.Start("PunkMultiverse.SteamRecv", ReceiveIntoPump);
            }
            Plugin.Log.LogInfo($"[Steam] transport up as {(host ? "host" : "client")}, local id {LocalPeerId}" +
                (_pump != null ? " (threaded receive)" : ""));
            if (!host)
            {
                // Optimistic connect: sessions are implicit, so treat the host as connected now.
                // A real failure surfaces via SteamNetworkingMessagesSessionFailed_t.
                // Announced from Poll, not here — the caller must finish its own setup after
                // StartClient returns before it can act on the event.
                _knownPeers.Add(hostSteamId);
                _announceHostOnPoll = true;
            }
        }

        private void OnSessionRequest(SteamNetworkingMessagesSessionRequest_t req)
        {
            var remote = req.m_identityRemote;
            var steamId = remote.GetSteamID();
            bool allowed = NetConfig.AcceptAnySteamSession.Value || (AllowPeer?.Invoke(steamId) ?? false);
            if (!allowed)
            {
                Plugin.Log.LogWarning($"[Steam] rejected session request from {steamId.m_SteamID} (not in lobby)");
                return;
            }
            SteamNetworkingMessages.AcceptSessionWithUser(ref remote);
            Plugin.Log.LogInfo($"[Steam] accepted session from {steamId.m_SteamID}");
        }

        private void OnSessionFailed(SteamNetworkingMessagesSessionFailed_t failed)
        {
            var steamId = failed.m_info.m_identityRemote.GetSteamID().m_SteamID;
            Plugin.Log.LogWarning($"[Steam] session failed with {steamId}: {failed.m_info.m_eEndReason}");
            if (_knownPeers.Remove(steamId)) PeerDisconnected?.Invoke(steamId);
        }

        public bool Send(ulong peer, NetChannel channel, ArraySegment<byte> data, bool reliable)
        {
            if (!IsRunning) return false;
            var identity = new SteamNetworkingIdentity();
            identity.SetSteamID(new CSteamID(peer));
            int flags = reliable
                ? (channel == NetChannel.Combat
                    ? Constants.k_nSteamNetworkingSend_ReliableNoNagle // don't sit 5 ms on a hit
                    : Constants.k_nSteamNetworkingSend_Reliable)
                : Constants.k_nSteamNetworkingSend_UnreliableNoNagle;
            var handle = GCHandle.Alloc(data.Array, GCHandleType.Pinned);
            try
            {
                IntPtr ptr = handle.AddrOfPinnedObject() + data.Offset;
                var result = SteamNetworkingMessages.SendMessageToUser(ref identity, ptr, (uint)data.Count, flags, (int)channel);
                // LimitExceeded = send buffer full. Bulk senders (terrain streaming) pace
                // themselves off the return value, so log it quietly instead of once per drop.
                if (result == EResult.k_EResultLimitExceeded)
                    Plugin.Log.LogDebug($"[Steam] send buffer full for {peer} ch{(int)channel}");
                else if (result != EResult.k_EResultOK && result != EResult.k_EResultNoConnection)
                    Plugin.Log.LogWarning($"[Steam] send to {peer} ch{(int)channel} failed: {result}");
                if (result == EResult.k_EResultOK)
                {
                    _knownPeers.Add(peer); // includes host-approved direct state mesh routes
                    Core.NetStats.AddOut(channel, data.Count);
                    Core.NetSeq.NoteSent(peer, channel);
                }
                return result == EResult.k_EResultOK;
            }
            finally { handle.Free(); }
        }

        public void Poll()
        {
            if (!IsRunning) return;
            if (_announceHostOnPoll)
            {
                _announceHostOnPoll = false;
                PeerConnected?.Invoke(_hostSteamId);
            }
            if (_pump != null)
            {
                _pump.Drain(DispatchItem);
                return;
            }
            // Inline path (ThreadedReceive off): receive + dispatch everything this frame.
            // Callback pumping is centralized in SteamBootstrap.Pump (NetSession.Update).
            for (int channel = 0; channel <= (int)NetChannel.Combat; channel++)
            {
                int count;
                do
                {
                    count = SteamNetworkingMessages.ReceiveMessagesOnChannel(channel, _msgPtrs, MaxMessagesPerPoll);
                    for (int i = 0; i < count; i++)
                    {
                        var msg = Marshal.PtrToStructure<SteamNetworkingMessage_t>(_msgPtrs[i]);
                        try
                        {
                            ulong sender = msg.m_identityPeer.GetSteamID().m_SteamID;
                            if (_knownPeers.Add(sender)) PeerConnected?.Invoke(sender);
                            int size = msg.m_cbSize;
                            if (size > 0)
                            {
                                if (size > _recvBuf.Length) Array.Resize(ref _recvBuf, size);
                                Marshal.Copy(msg.m_pData, _recvBuf, 0, size);
                                DataReceived?.Invoke(sender, (NetChannel)channel, new ArraySegment<byte>(_recvBuf, 0, size));
                            }
                        }
                        finally { SteamNetworkingMessage_t.Release(_msgPtrs[i]); }
                    }
                } while (count == MaxMessagesPerPoll);
            }
        }

        private void DispatchItem(in ReceivePump.Item item)
        {
            if (_knownPeers.Add(item.Peer)) PeerConnected?.Invoke(item.Peer);
            if (item.Len > 0)
                DataReceived?.Invoke(item.Peer, (NetChannel)item.Channel, new ArraySegment<byte>(item.Buf, 0, item.Len));
        }

        /// <summary>Pump thread: pull datagrams from Steam into the queue. Per-channel order is
        /// preserved end-to-end (Steam delivers in order per channel, the queue is FIFO, the
        /// drain runs on one thread). Cross-channel interleaving was never guaranteed — messages
        /// already straddled frame boundaries on the inline path.</summary>
        private int ReceiveIntoPump()
        {
            int total = 0;
            for (int channel = 0; channel <= (int)NetChannel.Combat; channel++)
            {
                int count;
                do
                {
                    count = SteamNetworkingMessages.ReceiveMessagesOnChannel(channel, _threadMsgPtrs, MaxMessagesPerPoll);
                    for (int i = 0; i < count; i++)
                    {
                        var msg = Marshal.PtrToStructure<SteamNetworkingMessage_t>(_threadMsgPtrs[i]);
                        try
                        {
                            var item = new ReceivePump.Item
                            {
                                Peer = msg.m_identityPeer.GetSteamID().m_SteamID,
                                Channel = (byte)channel,
                                Len = msg.m_cbSize,
                            };
                            if (item.Len > 0)
                            {
                                item.Buf = _pump.Rent(item.Len);
                                Marshal.Copy(msg.m_pData, item.Buf, 0, item.Len);
                            }
                            _pump.Enqueue(in item);
                        }
                        finally { SteamNetworkingMessage_t.Release(_threadMsgPtrs[i]); }
                    }
                    total += count;
                } while (count == MaxMessagesPerPoll);
            }
            return total;
        }

        public void Stop()
        {
            if (!IsRunning) return;
            // Stop the pump before closing sessions so its thread never reads a dying session.
            _pump?.Stop();
            _pump = null;
            foreach (var peer in _knownPeers)
            {
                var identity = new SteamNetworkingIdentity();
                identity.SetSteamID(new CSteamID(peer));
                SteamNetworkingMessages.CloseSessionWithUser(ref identity);
            }
            _knownPeers.Clear();
            _announceHostOnPoll = false;
            _sessionRequest?.Dispose();
            _sessionRequest = null;
            _sessionFailed?.Dispose();
            _sessionFailed = null;
            IsRunning = false;
            Plugin.Log.LogInfo("[Steam] transport stopped");
        }

        public void Dispose() => Stop();
    }
}
