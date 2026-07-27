using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using PunkMultiverse.Protocol;

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
    /// Threading (v0.1.156 — the "packets on a metronome" fix): LiteNetLib services the socket
    /// on its own logic thread; callbacks fire UNSYNCED on that thread. The host FAST-RELAYS
    /// high-volume State traffic (ShipState / EntityStateBundle) to the other peers directly in
    /// the receive callback — relay cadence is therefore immune to main-thread frame stalls,
    /// which on the Wine coordinator ran 140-600ms and turned relayed state into bursts (the
    /// multiplayer "lost frames": jitter -> interp underruns at every client, 2026-07-23).
    /// Everything is ALSO queued (a copy) for main-thread dispatch from Poll(), preserving the
    /// existing NetSession pipeline; the host consumes fast-relayed state itself that way, and
    /// NetSession skips its own main-thread relay for the fast-pathed types (InlineStateRelay).
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
        // -2: State-channel FEC framing (seq byte + parity packets), 2026-07-24.
        private const string ConnectionKey = "punkmv-udp-2";

        // ---------------------------------------------------------------- XOR-FEC (State channel)
        //
        // The live WAN path measured 16-19% packet loss; every lost State packet is a hole the
        // interpolation buffer must absorb (underruns -> inflated delay). Low-latency FEC: each
        // unreliable State packet carries a per-LINK sequence byte, and after every FecGroup of
        // them one parity packet (XOR of [len:2][payload] blocks, zero-padded) goes out — any
        // SINGLE loss in the group is reconstructed on arrival of the parity, no retransmit
        // round-trip. Cost: +1/FecGroup packets (~25%) of a single-digit-KB/s stream.
        //
        // Framing (unreliable State only; everything else unchanged):
        //   data   = [NetChannel.State][seq][payload...]
        //   parity = [FecParityChannel][startSeq][count][xorBlock...]
        // Encoders are per-link+direction (main Send + relay thread share them under a lock);
        // decode rings live on the socket thread only. Recovered payloads are injected into the
        // normal dispatch queue AND (host) the relay queue — a packet the server never received
        // could never have been forwarded, so recovery feeds the other clients too.
        private const byte FecParityChannel = 0xEE;
        private const int FecGroup = 4;
        private const int FecBuf = 2048;

        private sealed class FecTx
        {
            public readonly object Lock = new object();
            public byte Seq;
            public int Count;
            public byte StartSeq;
            public int MaxLen;
            public readonly byte[] Xor = new byte[FecBuf];
        }

        private sealed class FecRx
        {
            // Small ring keyed by wrapping seq byte; socket-thread only.
            public readonly byte[] Seqs = new byte[16];
            public readonly byte[][] Frames = new byte[16][]; // full data frame incl. [chan][seq]
            public int Next;
        }

        private readonly Dictionary<int, FecTx> _fecTxByPeer = new Dictionary<int, FecTx>();   // host: lib peer id
        private readonly Dictionary<int, FecRx> _fecRxByPeer = new Dictionary<int, FecRx>();   // host: lib peer id
        private FecTx _fecTxHost;  // client: single link
        private FecRx _fecRxHost;
        private long _fecRecovered, _fecUnrecoverable, _fecParityTx;

        private FecTx GetFecTx(NetPeer target)
        {
            if (!IsHost) return _fecTxHost ?? (_fecTxHost = new FecTx());
            lock (_fecTxByPeer)
            {
                if (!_fecTxByPeer.TryGetValue(target.Id, out var tx))
                    _fecTxByPeer[target.Id] = tx = new FecTx();
                return tx;
            }
        }

        /// <summary>Wrap + send one unreliable State payload on a link, accumulating parity.
        /// Safe from any thread (per-link lock; NetPeer.Send is thread-safe).</summary>
        private void SendStateWithFec(NetPeer target, byte[] payload, int offset, int count)
        {
            var tx = GetFecTx(target);
            byte[] frame = new byte[count + 2];
            frame[0] = (byte)NetChannel.State;
            byte[] parity = null;
            int parityLen = 0;
            lock (tx.Lock)
            {
                frame[1] = tx.Seq;
                if (tx.Count == 0) { tx.StartSeq = tx.Seq; Array.Clear(tx.Xor, 0, tx.MaxLen + 2); tx.MaxLen = 0; }
                tx.Seq++;
                // Accumulate [len:2][payload] into the XOR block.
                int blockLen = count + 2;
                if (blockLen <= FecBuf)
                {
                    tx.Xor[0] ^= (byte)(count & 0xFF);
                    tx.Xor[1] ^= (byte)((count >> 8) & 0xFF);
                    for (int i = 0; i < count; i++) tx.Xor[2 + i] ^= payload[offset + i];
                    if (blockLen > tx.MaxLen + 2) tx.MaxLen = count;
                    tx.Count++;
                    if (tx.Count >= FecGroup)
                    {
                        parityLen = tx.MaxLen + 2 + 3;
                        parity = new byte[parityLen];
                        parity[0] = FecParityChannel;
                        parity[1] = tx.StartSeq;
                        parity[2] = (byte)tx.Count;
                        Buffer.BlockCopy(tx.Xor, 0, parity, 3, tx.MaxLen + 2);
                        Array.Clear(tx.Xor, 0, tx.MaxLen + 2);
                        tx.Count = 0;
                        tx.MaxLen = 0;
                    }
                }
            }
            Buffer.BlockCopy(payload, offset, frame, 2, count);
            try
            {
                target.Send(frame, 0, frame.Length, DeliveryMethod.Unreliable);
                if (parity != null)
                {
                    target.Send(parity, 0, parityLen, DeliveryMethod.Unreliable);
                    System.Threading.Interlocked.Increment(ref _fecParityTx);
                }
            }
            catch (TooBigPacketException) { }
        }

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

        // Unsynced-callback plumbing: events arrive on LiteNetLib's logic thread and are queued
        // here for main-thread dispatch in Poll(). The peer maps above are mutated ONLY on the
        // main thread (during drain) so main-thread Send() never races a socket-thread write.
        private enum EvtKind : byte { Connected, Disconnected, Data }
        private struct Evt
        {
            public EvtKind Kind;
            public ulong From;
            public NetChannel Channel;
            public byte[] Data;      // Data events: payload WITHOUT the channel prefix
            public NetPeer Peer;     // Connected events: to seat in the maps on drain
            public bool RemoteClose; // Disconnected events
        }
        private readonly System.Collections.Concurrent.ConcurrentQueue<Evt> _events
            = new System.Collections.Concurrent.ConcurrentQueue<Evt>();

        /// <summary>True while hosting: ShipState/EntityStateBundle are relayed to the other
        /// peers by the relay thread — NetSession must NOT relay them again on the main thread.</summary>
        public bool InlineStateRelay => IsHost;

        // Dedicated relay thread. Targets are a volatile immutable snapshot rebuilt by the
        // main thread on every connect/disconnect drain — the relay thread only ever reads the
        // array reference, so no locks and no racing LiteNetLib's own peer list.
        private struct RelayItem { public int SenderPeerId; public byte[] Data; public int Offset; public int Count; }
        private readonly System.Collections.Concurrent.ConcurrentQueue<RelayItem> _relayQueue
            = new System.Collections.Concurrent.ConcurrentQueue<RelayItem>();
        private readonly System.Threading.AutoResetEvent _relaySignal = new System.Threading.AutoResetEvent(false);
        private System.Threading.Thread _relayThread;
        private volatile bool _relayRunning;
        private volatile NetPeer[] _relayTargets = new NetPeer[0];

        private void StartRelayThread()
        {
            _relayRunning = true;
            _relayThread = new System.Threading.Thread(RelayLoop)
            {
                IsBackground = true,
                Name = "PunkMV-StateRelay",
                // The container runs near its CPU allocation (game sim eats it); an even-priority
                // relay thread gets descheduled tens of ms — measured as ~60ms client jitter on
                // the live server vs ~8ms local (2026-07-24). Relay work is microseconds per
                // packet, so highest priority steals nothing meaningful from the sim.
                Priority = System.Threading.ThreadPriority.Highest,
            };
            _relayThread.Start();
        }

        private void RelayLoop()
        {
            while (_relayRunning)
            {
                _relaySignal.WaitOne(250); // wake on signal; periodic wake checks shutdown
                bool sentAny = false;
                while (_relayQueue.TryDequeue(out var item))
                {
                    var targets = _relayTargets;
                    for (int i = 0; i < targets.Length; i++)
                    {
                        var t = targets[i];
                        if (t == null || t.Id == item.SenderPeerId
                            || t.ConnectionState != ConnectionState.Connected) continue;
                        // Payload only (source framing stripped) — each target link gets its own
                        // FEC sequence/parity stream.
                        try { SendStateWithFec(t, item.Data, item.Offset, item.Count); sentAny = true; }
                        catch { /* peer mid-teardown — the next snapshot rebuild drops it */ }
                    }
                }
                // Send() only enqueues; the logic loop transmits. Kick it NOW so relayed state
                // hits the wire this instant instead of on the next (Wine-wobbly) loop tick.
                if (sentAny) { try { _manager?.TriggerUpdate(); } catch { } }
            }
        }

        private void RebuildRelayTargets()
        {
            var arr = new NetPeer[_peerById.Count];
            int i = 0;
            foreach (var kv in _peerById) arr[i++] = kv.Value;
            _relayTargets = arr;
        }

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
                // Send flushes ride this loop (Send() only ENQUEUES) — 10ms was fine on native
                // Windows but Wine's timer wobble stretched flush spacing into the tens of ms,
                // the leading suspect for the live server's ~60ms client jitter (local control
                // ~8ms, CPU unlimited, relay threaded — process of elimination, 2026-07-24).
                // 1ms + TriggerUpdate() after relay enqueues = tightest cadence the lib offers.
                UpdateTime = 1,
                DisconnectTimeout = 10000,   // match loopback's stall tolerance (level load, GC)
                ChannelsCount = 4,           // one ordered stream per NetChannel
                UnconnectedMessagesEnabled = false,
                IPv6Enabled = false,         // Docker/LAN target; avoids dual-stack bind surprises
                EnableStatistics = true,     // loss/resend counters for the reliable-backlog probe
                UnsyncedEvents = true,       // callbacks on the logic thread -> fast State relay
            };
            return m;
        }

        // ---------------------------------------------------------------- reliable-backlog probe
        //
        // Field signature this exists for (dedicated server, 2026-07-23): the go-live burst
        // (manifest + entity baseline + GO_LIVE, all ReliableOrdered on Control) never reached the
        // client — it kept retrying LEVEL_READY for 26s+ while small traffic flowed fine. This
        // probe distinguishes the three possible worlds every 3s while a reliable queue is
        // non-empty: depth frozen = send-side wedge; depth draining = congestion/starvation;
        // depth zero while the peer misbehaves = delivered, receiver-side bug.
        private int _nextHealthTick;
        private readonly Dictionary<ulong, int> _lastDepth = new Dictionary<ulong, int>();
        private readonly Dictionary<ulong, int> _stalledSinceTick = new Dictionary<ulong, int>();

        private void ProbeReliableBacklog()
        {
            if (!IsHost || _manager == null) return;
            int now = Environment.TickCount;
            if (unchecked(now - _nextHealthTick) < 0) return;
            _nextHealthTick = now + 3000;

            foreach (var kv in _peerById)
            {
                var peer = kv.Value;
                if (peer == null || peer.ConnectionState != ConnectionState.Connected) continue;
                int c0 = peer.GetPacketsCountInReliableQueue(0, true);
                int c1 = peer.GetPacketsCountInReliableQueue(1, true);
                int c2 = peer.GetPacketsCountInReliableQueue(2, true);
                int depth = c0 + c1 + c2;
                _lastDepth.TryGetValue(kv.Key, out int prev);
                _lastDepth[kv.Key] = depth;
                if (depth == 0) { _stalledSinceTick.Remove(kv.Key); continue; }

                string trend;
                if (prev > 0 && depth >= prev)
                {
                    if (!_stalledSinceTick.TryGetValue(kv.Key, out int since))
                        _stalledSinceTick[kv.Key] = since = now;
                    trend = $"STALLED {unchecked(now - since) / 1000}s";
                }
                else
                {
                    _stalledSinceTick.Remove(kv.Key);
                    trend = prev > 0 ? $"draining {prev}->{depth}" : "new";
                }
                var s = _manager.Statistics;
                Plugin.Log.LogWarning(
                    $"[Udp] reliable backlog peer={kv.Key} ch0={c0} ch1={c1} ch2={c2} ({trend}) " +
                    $"mtu={peer.Mtu} ping={peer.Ping}ms | mgr sent={s.PacketsSent} recv={s.PacketsReceived} loss={s.PacketLossPercent}%");
            }
        }

        /// <summary>One-line transport health snapshot for the `udpstats` devcmd (both roles).</summary>
        public string DescribeHealth()
        {
            if (_manager == null) return "[Udp] not running";
            var s = _manager.Statistics;
            string peers;
            if (IsHost)
            {
                var parts = new List<string>();
                foreach (var kv in _peerById)
                {
                    var p = kv.Value;
                    if (p == null) continue;
                    parts.Add($"peer{kv.Key}: state={p.ConnectionState} mtu={p.Mtu} ping={p.Ping}ms " +
                        $"relq={p.GetPacketsCountInReliableQueue(0, true)}/{p.GetPacketsCountInReliableQueue(1, true)}/{p.GetPacketsCountInReliableQueue(2, true)}");
                }
                peers = parts.Count > 0 ? string.Join(" | ", parts.ToArray()) : "no peers";
            }
            else
            {
                var p = _hostPeer;
                peers = p == null ? "no host connection"
                    : $"host: state={p.ConnectionState} mtu={p.Mtu} ping={p.Ping}ms " +
                      $"relq={p.GetPacketsCountInReliableQueue(0, true)}/{p.GetPacketsCountInReliableQueue(1, true)}/{p.GetPacketsCountInReliableQueue(2, true)}";
            }
            return $"[Udp] {peers} | mgr sent={s.PacketsSent} recv={s.PacketsReceived} " +
                   $"bytesOut={s.BytesSent} bytesIn={s.BytesReceived} loss={s.PacketLossPercent}% " +
                   $"| fec parityTx={System.Threading.Interlocked.Read(ref _fecParityTx)} " +
                   $"recovered={System.Threading.Interlocked.Read(ref _fecRecovered)} " +
                   $"unrecoverable={System.Threading.Interlocked.Read(ref _fecUnrecoverable)}";
        }

        public void StartHost()
        {
            _manager = CreateManager();
            if (!_manager.Start(_port))
                throw new InvalidOperationException($"UDP port {_port} unavailable");
            IsHost = true;
            IsRunning = true;
            LocalPeerId = HostPeerId;
            StartRelayThread();
            Plugin.Log.LogInfo($"[Udp] hosting on *:{_port} (LiteNetLib, threaded state relay)");
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

            // Unreliable State rides the FEC framing (seq byte + periodic parity).
            if (!reliable && channel == NetChannel.State)
            {
                SendStateWithFec(target, data.Array, data.Offset, data.Count);
                return true;
            }

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
            DrainEvents(); // UnsyncedEvents mode: callbacks already fired on the logic thread
            ProbeReliableBacklog();
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
            _relayRunning = false;
            _relaySignal.Set();
            try { _relayThread?.Join(500); } catch { }
            _relayThread = null;
            try { _manager?.Stop(true); } catch { }
            _manager = null;
            _hostPeer = null;
            _idByPeer.Clear();
            _peerById.Clear();
            _relayTargets = new NetPeer[0];
            lock (_fecTxByPeer) { _fecTxByPeer.Clear(); }
            _fecRxByPeer.Clear();
            _fecTxHost = null;
            _fecRxHost = null;
            while (_events.TryDequeue(out _)) { }     // stale events must not leak into a new session
            while (_relayQueue.TryDequeue(out _)) { }
        }

        public void Dispose() => Stop();

        // ---------------------------------------------------------------- INetEventListener

        void INetEventListener.OnConnectionRequest(ConnectionRequest request)
        {
            if (!IsHost || _manager.ConnectedPeersCount >= MaxPeers) { request.Reject(); return; }
            request.AcceptIfKey(ConnectionKey);
        }

        // ---- callbacks below run on LiteNetLib's LOGIC THREAD (UnsyncedEvents) ----

        void INetEventListener.OnPeerConnected(NetPeer peer)
        {
            // Map/roster mutations happen on drain (main thread); only note the arrival here.
            _events.Enqueue(new Evt { Kind = EvtKind.Connected, Peer = peer });
        }

        void INetEventListener.OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
        {
            _fecRxByPeer.Remove(peer.Id); // this dict is socket-thread-owned; prune here, not on drain
            bool remote = info.Reason == DisconnectReason.RemoteConnectionClose
                       || info.Reason == DisconnectReason.DisconnectPeerCalled;
            _events.Enqueue(new Evt
            {
                Kind = EvtKind.Disconnected,
                From = IsHost ? (ulong)(peer.Id + 2) : HostPeerId,
                RemoteClose = remote,
            });
        }

        void INetEventListener.OnNetworkReceive(NetPeer peer, NetPacketReader reader,
            byte channelNumber, DeliveryMethod deliveryMethod)
        {
            int size = reader.UserDataSize;
            if (size < 2) return; // channel prefix + at least a message type
            int off = reader.UserDataOffset;
            byte chanByte = reader.RawData[off];

            if (chanByte == FecParityChannel)
            {
                HandleFecParity(peer, reader.RawData, off, size);
                return;
            }

            var channel = (NetChannel)chanByte;
            // One copy serves all consumers (main dispatch, FEC ring, relay). State frames are
            // [chan][seq][payload]; everything else stays [chan][payload].
            var data = new byte[size];
            Buffer.BlockCopy(reader.RawData, off, data, 0, size);

            if (channel == NetChannel.State)
            {
                if (size < 3) return;
                StoreFecFrame(GetFecRx(peer), data);
                MaybeRelayState(peer, data);
            }

            ulong from = IsHost ? (ulong)(peer.Id + 2) : HostPeerId;
            // Counted BEFORE the enqueue so the drain can never see a frame it thinks is the
            // newest when a fresher one is already in flight behind it.
            if (channel == NetChannel.State)
                System.Threading.Interlocked.Increment(ref PendingSlot(from)[0]);
            _events.Enqueue(new Evt
            {
                Kind = EvtKind.Data,
                From = from,
                Channel = channel,
                Data = data,
            });
        }

        // ---- FEC receive-side helpers (socket thread only) ----

        private FecRx GetFecRx(NetPeer peer)
        {
            if (!IsHost) return _fecRxHost ?? (_fecRxHost = new FecRx());
            if (!_fecRxByPeer.TryGetValue(peer.Id, out var rx))
                _fecRxByPeer[peer.Id] = rx = new FecRx(); // socket thread owns this dict's writes
            return rx;
        }

        private static void StoreFecFrame(FecRx rx, byte[] frame)
        {
            rx.Seqs[rx.Next] = frame[1];
            rx.Frames[rx.Next] = frame;
            rx.Next = (rx.Next + 1) % rx.Frames.Length;
        }

        private static byte[] LookupFecFrame(FecRx rx, byte seq)
        {
            for (int i = 0; i < rx.Frames.Length; i++)
                if (rx.Frames[i] != null && rx.Seqs[i] == seq) return rx.Frames[i];
            return null;
        }

        /// <summary>FAST RELAY (host): high-volume presentation state goes to the dedicated relay
        /// thread — never sent from inside the receive callback. ONLY ShipState/EntityStateBundle;
        /// other State traffic has host-only or validated-relay semantics on the main thread.</summary>
        private void MaybeRelayState(NetPeer peer, byte[] stateFrame)
        {
            if (!IsHost || stateFrame.Length < 3) return;
            var msgType = (MsgType)stateFrame[2];
            if (msgType != MsgType.ShipState && msgType != MsgType.EntityStateBundle) return;
            _relayQueue.Enqueue(new RelayItem
            {
                SenderPeerId = peer.Id,
                Data = stateFrame,
                Offset = 2,                      // strip [chan][seq]; targets get fresh per-link FEC
                Count = stateFrame.Length - 2,
            });
            _relaySignal.Set();
        }

        private void HandleFecParity(NetPeer peer, byte[] raw, int off, int size)
        {
            if (size < 3 + 3) return; // header + at least [len:2]+1
            var rx = GetFecRx(peer);
            byte start = raw[off + 1];
            int count = raw[off + 2];
            int blockLen = size - 3;
            if (count < 2 || count > 8 || blockLen > FecBuf) return;

            int missingIdx = -1;
            for (int i = 0; i < count; i++)
            {
                if (LookupFecFrame(rx, (byte)(start + i)) != null) continue;
                if (missingIdx >= 0) { _fecUnrecoverable++; return; } // 2+ lost — XOR can't help
                missingIdx = i;
            }
            if (missingIdx < 0) return; // nothing lost in this group

            var block = new byte[blockLen];
            Buffer.BlockCopy(raw, off + 3, block, 0, blockLen);
            for (int i = 0; i < count; i++)
            {
                if (i == missingIdx) continue;
                var frame = LookupFecFrame(rx, (byte)(start + i));
                int payloadLen = frame.Length - 2;
                block[0] ^= (byte)(payloadLen & 0xFF);
                block[1] ^= (byte)((payloadLen >> 8) & 0xFF);
                int limit = Math.Min(payloadLen, blockLen - 2);
                for (int j = 0; j < limit; j++) block[2 + j] ^= frame[2 + j];
            }
            int len = block[0] | (block[1] << 8);
            if (len < 1 || len + 2 > blockLen) { _fecUnrecoverable++; return; }

            var recovered = new byte[len + 2];
            recovered[0] = (byte)NetChannel.State;
            recovered[1] = (byte)(start + missingIdx);
            Buffer.BlockCopy(block, 2, recovered, 2, len);
            _fecRecovered++;
            if (_fecRecovered % 100 == 1)
                Plugin.Log.LogInfo($"[Udp] fec recovered={_fecRecovered} unrecoverable={_fecUnrecoverable} (lost packets reconstructed from parity)");
            StoreFecFrame(rx, recovered); // later parities in overlapping windows see it
            MaybeRelayState(peer, recovered); // the origin's loss starved everyone downstream too
            _events.Enqueue(new Evt
            {
                Kind = EvtKind.Data,
                From = IsHost ? (ulong)(peer.Id + 2) : HostPeerId,
                Channel = NetChannel.State,
                Data = recovered,
            });
        }

        // ---- main-thread drain (called from Poll) ----

        // How many State frames from one peer may still be queued behind the one we are about to
        // dispatch before we treat it as superseded. 0 would shed on the slightest interleaving;
        // 2 sheds only under real backlog, so healthy traffic is never touched.
        private const int StateBacklogKeep = 2;
        // Ceiling on how long one Poll may spend dispatching. Frames were spiking to 550ms because
        // the drain was an unbounded `while (TryDequeue)`: whatever had piled up got swallowed in a
        // single frame, which stalled the server, which piled up more — a catch-up spiral. Anything
        // still queued now waits for the next frame instead.
        private const double DrainBudgetMs = 6.0;
        private Evt _deferred;
        private bool _hasDeferred;

        private readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, int[]> _pendingState
            = new System.Collections.Concurrent.ConcurrentDictionary<ulong, int[]>();

        private int[] PendingSlot(ulong from) => _pendingState.GetOrAdd(from, _ => new int[1]);

        private void DrainEvents()
        {
            var budget = System.Diagnostics.Stopwatch.StartNew();
            // A frame that ran out of budget last time parked one event here to keep FIFO order.
            if (_hasDeferred)
            {
                _hasDeferred = false;
                DispatchEvent(_deferred);
                _deferred = default;
            }
            while (_events.TryDequeue(out var e))
            {
                if (e.Kind == EvtKind.Data && e.Channel == NetChannel.State)
                {
                    // "Process this now, or just get the next one." State is periodic and
                    // self-superseding: a newer frame from this peer makes the older one worthless,
                    // so under backlog we drop it rather than pay to apply a position that is about
                    // to be overwritten. Reliable/Control traffic is never shed — only this channel,
                    // which is already unreliable by design.
                    int remaining = System.Threading.Interlocked.Decrement(ref PendingSlot(e.From)[0]);
                    if (remaining > StateBacklogKeep)
                    {
                        Core.InstrumentationCounters.StateFrameShed();
                        continue;
                    }
                    if (budget.Elapsed.TotalMilliseconds > DrainBudgetMs)
                    {
                        _deferred = e;
                        _hasDeferred = true;
                        Core.InstrumentationCounters.DrainDeferred();
                        return;
                    }
                }
                DispatchEvent(e);
            }
        }

        private void DispatchEvent(Evt e)
        {
            {
                switch (e.Kind)
                {
                    case EvtKind.Connected:
                        if (IsHost)
                        {
                            ulong id = (ulong)(e.Peer.Id + 2); // 1 is the host; lib ids are 0-based
                            _idByPeer[e.Peer.Id] = id;
                            _peerById[id] = e.Peer;
                            RebuildRelayTargets();
                            Plugin.Log.LogInfo($"[Udp] peer {id} connected ({e.Peer.Address}:{e.Peer.Port})");
                            PeerConnected?.Invoke(id);
                        }
                        else
                        {
                            _hostPeer = e.Peer;
                            _reconnectAtTick = -1;
                            // RemoteId = our id inside the host's manager — the same number the
                            // host computes, so both sides agree before the HELLO goes out.
                            LocalPeerId = (ulong)(e.Peer.RemoteId + 2);
                            Plugin.Log.LogInfo($"[Udp] connected to host as peer {LocalPeerId}");
                            PeerConnected?.Invoke(HostPeerId);
                        }
                        break;

                    case EvtKind.Disconnected:
                        LastDisconnectWasRemote = e.RemoteClose;
                        if (IsHost)
                        {
                            if (_peerById.Remove(e.From))
                            {
                                _idByPeer.Remove((int)(e.From - 2));
                                lock (_fecTxByPeer) { _fecTxByPeer.Remove((int)(e.From - 2)); }
                                RebuildRelayTargets();
                                // Probe tracking too: the lib never reuses peer ids, so a
                                // long-lived server would accrue one dead entry per connection.
                                _lastDepth.Remove(e.From);
                                _stalledSinceTick.Remove(e.From);
                                Plugin.Log.LogInfo($"[Udp] peer {e.From} disconnected ({(e.RemoteClose ? "remote close" : "timeout")})");
                                PeerDisconnected?.Invoke(e.From);
                            }
                        }
                        else
                        {
                            _hostPeer = null;
                            // A timeout means the host may just be stalled — arm the reconnect loop.
                            _reconnectAtTick = e.RemoteClose ? -1 : Environment.TickCount + 2000;
                            Plugin.Log.LogInfo($"[Udp] host connection lost ({(e.RemoteClose ? "remote close" : "timeout")})");
                            PeerDisconnected?.Invoke(HostPeerId);
                        }
                        break;

                    case EvtKind.Data:
                        // Admission check at drain time (maps are main-thread-owned): host drops
                        // data from peers whose Connected event hasn't seated them yet this drain
                        // only if they've since vanished — same-drain ordering seats them first.
                        if (IsHost && !_peerById.ContainsKey(e.From)) break;
                        // State frames = [chan][fecSeq][payload]; everything else = [chan][payload].
                        int hdr = e.Channel == NetChannel.State ? 2 : 1;
                        DataReceived?.Invoke(e.From, e.Channel, new ArraySegment<byte>(e.Data, hdr, e.Data.Length - hdr));
                        break;
                }
            }
        }

        void INetEventListener.OnNetworkError(IPEndPoint endPoint, SocketError socketError)
            => Plugin.Log.LogWarning($"[Udp] socket error {socketError} from {endPoint}");

        void INetEventListener.OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint,
            NetPacketReader reader, UnconnectedMessageType messageType) { }

        void INetEventListener.OnNetworkLatencyUpdate(NetPeer peer, int latency) { }
    }
}
