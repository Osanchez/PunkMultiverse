using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using UnityEngine;

namespace PunkMultiverse.Transport
{
    /// <summary>
    /// Dev-only UDP transport for testing two game instances on one machine without Steam
    /// (copy the game folder, launch Punk.exe directly). Reliable-marked traffic is paced in send
    /// order so manifest/baseline bursts cannot overflow the localhost receive buffer. The run
    /// barrier also retries at the session layer; Steam remains the shipping reliable transport.
    /// Frames: [1]=connect  [2]=connectAck+peerId  [3]=disconnect  [4]=data+channel+payload  [5]=keepalive
    /// </summary>
    public sealed class LoopbackUdpTransport : ITransport
    {
        private const byte FrameConnect = 1, FrameConnectAck = 2, FrameDisconnect = 3, FrameData = 4, FrameKeepalive = 5;
        private const ulong HostPeerId = 1;
        private const float KeepaliveInterval = 2f, PeerTimeout = 10f, ConnectRetryInterval = 0.5f;

        private readonly int _port;
        private readonly string _hostAddress;
        private Socket _socket;
        private byte[] _recvBuf = new byte[64 * 1024];
        private byte[] _sendBuf = new byte[64 * 1024];
        private EndPoint _recvFrom = new IPEndPoint(IPAddress.Any, 0);

        // Shared threaded-receive mechanism (same as the Steam transport — loopback is the local
        // test bed for shipping behavior, so the mechanisms must match). The pump thread only
        // reads the socket and feeds the queue; ALL frame handling (peers, events, keepalives)
        // stays on the main thread via Poll's budgeted drain.
        private ReceivePump _pump;
        private readonly byte[] _threadRecvBuf = new byte[64 * 1024];

        private sealed class Peer
        {
            public ulong Id;
            public IPEndPoint EndPoint;
            public float LastHeard;
            public float LastSent;
        }

        private readonly Dictionary<ulong, Peer> _peers = new Dictionary<ulong, Peer>();
        private readonly Dictionary<long, Peer> _peersByEndpoint = new Dictionary<long, Peer>(); // key = addr hash
        private ulong _nextPeerId = 2;

        private sealed class PendingSend
        {
            public IPEndPoint EndPoint;
            public byte[] Frame;
        }

        private readonly Queue<PendingSend> _pacedReliable = new Queue<PendingSend>();
        private const int ReliableDatagramsPerPoll = 8;

        private float _nextQueueFullWarnAt;
        private IPEndPoint _hostEndPoint;   // client mode
        private bool _connectedToHost;
        private float _lastConnectAttempt;

        public bool IsRunning { get; private set; }
        public bool IsHost { get; private set; }
        public ulong LocalPeerId { get; private set; }

        public event Action<ulong> PeerConnected;
        public event Action<ulong> PeerDisconnected;
        public event Action<ulong, NetChannel, ArraySegment<byte>> DataReceived;

        public LoopbackUdpTransport(string hostAddress, int port)
        {
            _hostAddress = hostAddress;
            _port = port;
        }

        public void StartHost()
        {
            _socket = CreateSocket();
            _socket.Bind(new IPEndPoint(IPAddress.Loopback, _port));
            IsHost = true;
            LocalPeerId = HostPeerId;
            IsRunning = true;
            StartPumpIfConfigured();
            Plugin.Log.LogInfo($"[Loopback] Hosting on 127.0.0.1:{_port}" +
                (_pump != null ? " (threaded receive)" : ""));
        }

        public void StartClient(string address)
        {
            var parts = (string.IsNullOrEmpty(address) ? $"{_hostAddress}:{_port}" : address).Split(':');
            var ip = IPAddress.Parse(parts[0]);
            int port = parts.Length > 1 ? int.Parse(parts[1]) : _port;
            _hostEndPoint = new IPEndPoint(ip, port);
            _socket = CreateSocket();
            _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            IsHost = false;
            IsRunning = true;
            _connectedToHost = false;
            _lastConnectAttempt = -99f;
            StartPumpIfConfigured();
            Plugin.Log.LogInfo($"[Loopback] Connecting to {_hostEndPoint}" +
                (_pump != null ? " (threaded receive)" : ""));
        }

        private void StartPumpIfConfigured()
        {
            if (!NetConfig.ThreadedReceive.Value) return;
            _pump = new ReceivePump();
            _pump.Start("PunkMultiverse.LoopbackRecv", ReceiveIntoPump);
        }

        /// <summary>Pump thread: copy every available datagram into the queue. The frame is kept
        /// whole (frame byte + payload) so the main-thread drain runs the exact same HandleFrame
        /// logic as the inline path.</summary>
        private int ReceiveIntoPump()
        {
            int moved = 0;
            var socket = _socket;
            while (socket != null && socket.Available > 0)
            {
                int len;
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                try { len = socket.ReceiveFrom(_threadRecvBuf, ref from); }
                catch (SocketException) { break; }
                catch (ObjectDisposedException) { break; }
                if (len < 1) continue;
                var item = new ReceivePump.Item { Len = len, Buf = _pump.Rent(len), Tag = from };
                Buffer.BlockCopy(_threadRecvBuf, 0, item.Buf, 0, len);
                _pump.Enqueue(in item);
                moved++;
            }
            return moved;
        }

        private void DispatchItem(in ReceivePump.Item item)
            => HandleFrame((IPEndPoint)item.Tag, item.Buf, item.Len);

        private static Socket CreateSocket()
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { Blocking = false };
            // Loopback "reliable" is paced-but-unacknowledged UDP — under sustained load the
            // OS default receive buffer (~8-64KB) overflows and drops datagrams silently, which
            // read as protocol bugs (observed live: ~96% of availability requests lost while a
            // kill-ledger/combat flood ran). A big buffer makes localhost loss effectively zero.
            try { s.ReceiveBufferSize = 4 * 1024 * 1024; s.SendBufferSize = 1024 * 1024; } catch { }
            return s;
        }

        public bool Send(ulong peer, NetChannel channel, ArraySegment<byte> data, bool reliable)
        {
            if (!IsRunning) return false;
            var ep = ResolveEndpoint(peer);
            if (ep == null) return false;
            if (reliable)
            {
                if (_pacedReliable.Count >= 8192)
                {
                    // A silently-dropped reliable message here breaks protocol invariants
                    // downstream (lost leases, lost availability requests) — say so, loudly.
                    if (Time.unscaledTime >= _nextQueueFullWarnAt)
                    {
                        _nextQueueFullWarnAt = Time.unscaledTime + 2f;
                        Plugin.Log.LogWarning($"[Loopback] paced reliable queue FULL ({_pacedReliable.Count}) — dropping ch{(int)channel}");
                    }
                    return false;
                }
                var frame = new byte[data.Count + 2];
                frame[0] = FrameData;
                frame[1] = (byte)channel;
                Buffer.BlockCopy(data.Array, data.Offset, frame, 2, data.Count);
                _pacedReliable.Enqueue(new PendingSend { EndPoint = ep, Frame = frame });
                Core.NetStats.AddOut(data.Count);
                return true;
            }
            _sendBuf[0] = FrameData;
            _sendBuf[1] = (byte)channel;
            Buffer.BlockCopy(data.Array, data.Offset, _sendBuf, 2, data.Count);
            SendRaw(ep, _sendBuf, data.Count + 2);
            Core.NetStats.AddOut(data.Count);
            return true;
        }

        private IPEndPoint ResolveEndpoint(ulong peer)
        {
            if (!IsHost) return _hostEndPoint;
            return _peers.TryGetValue(peer, out var p) ? p.EndPoint : null;
        }

        private void SendRaw(IPEndPoint ep, byte[] buf, int len)
        {
            try { _socket.SendTo(buf, len, SocketFlags.None, ep); }
            catch (SocketException e) { Plugin.Log.LogWarning($"[Loopback] send failed: {e.SocketErrorCode}"); }
        }

        public void Poll()
        {
            if (!IsRunning) return;
            if (_pump != null) _pump.Drain(DispatchItem);
            else ReceiveAll();
            DrainPacedReliable();
            float now = Time.unscaledTime;

            if (IsHost)
            {
                List<ulong> dead = null;
                foreach (var p in _peers.Values)
                {
                    if (now - p.LastHeard > PeerTimeout) (dead ??= new List<ulong>()).Add(p.Id);
                    else if (now - p.LastSent > KeepaliveInterval) SendFrame(p.EndPoint, FrameKeepalive, ref p.LastSent, now);
                }
                if (dead != null)
                    foreach (var id in dead) DropPeer(id, "timeout");
            }
            else
            {
                if (!_connectedToHost)
                {
                    if (now - _lastConnectAttempt > ConnectRetryInterval)
                    {
                        _lastConnectAttempt = now;
                        _sendBuf[0] = FrameConnect;
                        SendRaw(_hostEndPoint, _sendBuf, 1);
                    }
                }
                else if (_peers.TryGetValue(HostPeerId, out var host))
                {
                    if (now - host.LastHeard > PeerTimeout) { DropPeer(HostPeerId, "timeout"); _connectedToHost = false; }
                    else if (now - host.LastSent > KeepaliveInterval) SendFrame(host.EndPoint, FrameKeepalive, ref host.LastSent, now);
                }
            }
        }

        private void DrainPacedReliable()
        {
            int budget = ReliableDatagramsPerPoll;
            while (budget-- > 0 && _pacedReliable.Count > 0)
            {
                var pending = _pacedReliable.Dequeue();
                SendRaw(pending.EndPoint, pending.Frame, pending.Frame.Length);
            }
        }

        private void SendFrame(IPEndPoint ep, byte frame, ref float lastSent, float now)
        {
            _sendBuf[0] = frame;
            SendRaw(ep, _sendBuf, 1);
            lastSent = now;
        }

        private void ReceiveAll()
        {
            while (_socket != null && _socket.Available > 0)
            {
                int len;
                try
                {
                    _recvFrom = new IPEndPoint(IPAddress.Any, 0);
                    len = _socket.ReceiveFrom(_recvBuf, ref _recvFrom);
                }
                catch (SocketException) { return; }
                if (len < 1) continue;
                HandleFrame((IPEndPoint)_recvFrom, _recvBuf, len);
            }
        }

        private static long EndpointKey(IPEndPoint ep) => ((long)ep.Port << 32) | (uint)ep.Address.GetHashCode();

        private void HandleFrame(IPEndPoint from, byte[] buf, int len)
        {
            float now = Time.unscaledTime;
            byte frame = buf[0];
            switch (frame)
            {
                case FrameConnect when IsHost:
                {
                    var key = EndpointKey(from);
                    if (!_peersByEndpoint.TryGetValue(key, out var peer))
                    {
                        peer = new Peer { Id = _nextPeerId++, EndPoint = from, LastHeard = now };
                        _peers[peer.Id] = peer;
                        _peersByEndpoint[key] = peer;
                        Plugin.Log.LogInfo($"[Loopback] peer {peer.Id} connected from {from}");
                        PeerConnected?.Invoke(peer.Id);
                    }
                    peer.LastHeard = now;
                    _sendBuf[0] = FrameConnectAck;
                    for (int i = 0; i < 8; i++) _sendBuf[1 + i] = (byte)(peer.Id >> (i * 8));
                    SendRaw(from, _sendBuf, 9);
                    break;
                }
                case FrameConnectAck when !IsHost:
                {
                    if (_connectedToHost) { Touch(from, now); break; }
                    ulong assigned = 0;
                    for (int i = 0; i < 8; i++) assigned |= (ulong)buf[1 + i] << (i * 8);
                    LocalPeerId = assigned;
                    _connectedToHost = true;
                    var host = new Peer { Id = HostPeerId, EndPoint = _hostEndPoint, LastHeard = now };
                    _peers[HostPeerId] = host;
                    Plugin.Log.LogInfo($"[Loopback] connected to host, assigned peer id {assigned}");
                    PeerConnected?.Invoke(HostPeerId);
                    break;
                }
                case FrameData:
                {
                    var peer = Touch(from, now);
                    if (peer == null || len < 2) break;
                    var channel = (NetChannel)buf[1];
                    DataReceived?.Invoke(peer.Id, channel, new ArraySegment<byte>(buf, 2, len - 2));
                    break;
                }
                case FrameKeepalive:
                    Touch(from, now);
                    break;
                case FrameDisconnect:
                {
                    var peer = Touch(from, now);
                    if (peer != null) DropPeer(peer.Id, "remote disconnect");
                    if (!IsHost) _connectedToHost = false;
                    break;
                }
            }
        }

        private Peer Touch(IPEndPoint from, float now)
        {
            Peer peer = null;
            if (IsHost) _peersByEndpoint.TryGetValue(EndpointKey(from), out peer);
            else _peers.TryGetValue(HostPeerId, out peer);
            if (peer != null) peer.LastHeard = now;
            return peer;
        }

        private void DropPeer(ulong id, string reason)
        {
            if (!_peers.TryGetValue(id, out var peer)) return;
            _peers.Remove(id);
            _peersByEndpoint.Remove(EndpointKey(peer.EndPoint));
            Plugin.Log.LogInfo($"[Loopback] peer {id} disconnected ({reason})");
            PeerDisconnected?.Invoke(id);
        }

        public void Stop()
        {
            if (!IsRunning) return;
            // Stop the pump before touching the socket so its thread never reads a dying socket.
            _pump?.Stop();
            _pump = null;
            try
            {
                _sendBuf[0] = FrameDisconnect;
                if (IsHost)
                    foreach (var p in _peers.Values) SendRaw(p.EndPoint, _sendBuf, 1);
                else if (_connectedToHost)
                    SendRaw(_hostEndPoint, _sendBuf, 1);
            }
            catch { /* best effort */ }
            _socket?.Close();
            _socket = null;
            _peers.Clear();
            _peersByEndpoint.Clear();
            _pacedReliable.Clear();
            _nextPeerId = 2;
            _connectedToHost = false;
            IsRunning = false;
            Plugin.Log.LogInfo("[Loopback] stopped");
        }

        public void Dispose() => Stop();
    }
}
