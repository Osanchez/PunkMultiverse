using System;
using System.Collections.Generic;
using System.Linq;
using PunkMultiverse.Protocol;
using PunkMultiverse.Transport;
using UnityEngine;

namespace PunkMultiverse.Core
{
    public enum SessionState
    {
        Offline,
        Connecting,  // client: waiting for transport + Welcome
        Lobby,       // connected, pre-run
        Loading,     // run starting, waiting for all LEVEL_READY (M2)
        InGame,      // GO_LIVE received (M2)
    }

    /// <summary>
    /// The session brain: owns the transport, the 4 player slots, and message dispatch.
    /// Host (slot 0) is registrar and relay. Driven from Update() on the plugin GameObject.
    /// </summary>
    public sealed class NetSession : MonoBehaviour
    {
        public const int ProtocolVersion = 1;
        public const int MaxPlayers = 4;
        private const float PingInterval = 1f;

        public static NetSession Instance { get; private set; }
        /// <summary>True whenever networking is live — every Harmony patch gates on this.</summary>
        public static bool Active => Instance != null && Instance.State != SessionState.Offline;

        public SessionState State { get; private set; } = SessionState.Offline;
        public bool IsHost => _transport?.IsHost ?? false;
        public int LocalSlot { get; private set; } = -1;
        public string LastError { get; private set; }

        private readonly NetPlayer[] _players = new NetPlayer[MaxPlayers];
        public IReadOnlyList<NetPlayer> Players => _players;
        public NetPlayer LocalPlayer => LocalSlot >= 0 ? _players[LocalSlot] : null;

        private ITransport _transport;
        public ITransport Transport => _transport;

        private SteamLobbyController _lobby;
        public SteamLobbyController Lobby => _lobby;
        public bool UsingSteam => !NetConfig.Transport.Value.Equals("Loopback", StringComparison.OrdinalIgnoreCase);

        /// <summary>Pasteable code for the current Steam lobby, or the loopback address.</summary>
        public string CurrentLobbyCode =>
            _lobby != null && _lobby.InLobby ? SteamLobbyController.EncodeLobbyCode(_lobby.CurrentLobby)
            : State != SessionState.Offline && !UsingSteam ? $"{NetConfig.LoopbackHost.Value}:{NetConfig.LoopbackPort.Value}"
            : null;

        public bool AllReady
        {
            get
            {
                int count = 0;
                foreach (var p in _players)
                {
                    if (p == null) continue;
                    count++;
                    if (!p.Ready) return false;
                }
                return count >= 2;
            }
        }

        private readonly NetWriter _writer = new NetWriter(8 * 1024);
        private readonly NetReader _reader = new NetReader();
        private float _nextPingAt;

        public event Action RosterChanged;
        public event Action<SessionState> StateChanged;

        private void Awake()
        {
            Instance = this;
            GameController.LevelGenerated += OnLevelGenerated;
        }

        private System.Collections.IEnumerator Start()
        {
            yield return new WaitForSecondsRealtime(2f);
            SteamBootstrap.EnsureInitialized();

            // Steam overlay "join game" on a cold start.
            var launchLobby = SteamLobbyController.ParseLaunchArgs();

            // DEV: config-driven autostart so two-instance loopback tests need no clicks.
            var mode = NetConfig.AutoStart.Value;
            if (launchLobby.HasValue)
            {
                yield return new WaitForSecondsRealtime(3f);
                JoinLobbyId(launchLobby.Value);
            }
            else if (mode.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                yield return new WaitForSecondsRealtime(1f);
                HostOnline();
            }
            else if (mode.Equals("Join", StringComparison.OrdinalIgnoreCase))
            {
                yield return new WaitForSecondsRealtime(3f);
                JoinByCode(null);
            }
        }

        // ------------------------------------------------ transport-agnostic facade (UI entry points)

        /// <summary>Host: Steam = create lobby then open transport; loopback = open transport directly.</summary>
        public void HostOnline()
        {
            try
            {
                LastError = null;
                if (!UsingSteam) { HostSession(); return; }
                EnsureLobbyController();
                _lobby.CreateLobby(); // -> LobbyCreated -> HostSession()
            }
            catch (Exception e)
            {
                Fail($"Host failed: {e.Message}");
                Plugin.Log.LogError(e);
            }
        }

        /// <summary>Join: Steam = decode lobby code (null = clipboard); loopback = address (null = config default).</summary>
        public void JoinByCode(string codeOrAddress)
        {
            LastError = null;
            if (!UsingSteam)
            {
                JoinSession(string.IsNullOrWhiteSpace(codeOrAddress)
                    ? $"{NetConfig.LoopbackHost.Value}:{NetConfig.LoopbackPort.Value}"
                    : codeOrAddress.Trim());
                return;
            }
            var code = string.IsNullOrWhiteSpace(codeOrAddress) ? GUIUtility.systemCopyBuffer : codeOrAddress;
            if (!SteamLobbyController.TryDecodeLobbyCode(code, out var lobbyId))
            {
                LastError = "Clipboard does not contain a valid lobby code (PMV-XXXXX-XXXXX-XXXX).";
                Plugin.Log.LogWarning($"[Session] {LastError}");
                return;
            }
            JoinLobbyId(lobbyId);
        }

        public void JoinLobbyId(Steamworks.CSteamID lobbyId)
        {
            try
            {
                LastError = null;
                EnsureLobbyController();
                _lobby.JoinLobby(lobbyId); // -> LobbyJoined -> JoinSession(hostId)
            }
            catch (Exception e)
            {
                Fail($"Join failed: {e.Message}");
                Plugin.Log.LogError(e);
            }
        }

        public void CopyLobbyCodeToClipboard()
        {
            var code = CurrentLobbyCode;
            if (code == null) return;
            GUIUtility.systemCopyBuffer = code;
            Plugin.Log.LogInfo($"[Session] lobby code copied: {code}");
        }

        private void EnsureLobbyController()
        {
            if (_lobby != null) return;
            _lobby = new SteamLobbyController();
            _lobby.LobbyCreated += _ => HostSession();
            _lobby.LobbyJoined += (_, hostId) => JoinSession(hostId.ToString());
            _lobby.LobbyError += err => { LastError = err; Plugin.Log.LogWarning($"[Session] {err}"); };
            _lobby.JoinRequested += lobbyId =>
            {
                Plugin.Log.LogInfo($"[Session] overlay join requested -> {lobbyId.m_SteamID}");
                if (State != SessionState.Offline) StopSession("joining invited lobby");
                JoinLobbyId(lobbyId);
            };
        }

        // ------------------------------------------------ lobby prefs (color / ready)

        public void SetLocalPrefs(byte colorIndex, bool ready)
        {
            var me = LocalPlayer;
            if (me == null) return;
            me.ColorIndex = colorIndex;
            me.Ready = ready;
            if (IsHost)
            {
                BroadcastLobbyState();
                RosterChanged?.Invoke();
            }
            else if (_players[0] != null)
            {
                _writer.Reset();
                new SetLobbyPrefsMsg { ColorIndex = colorIndex, Ready = ready }.Write(_writer);
                _transport.Send(_players[0].PeerId, NetChannel.Control, _writer.ToSegment(), reliable: true);
                RosterChanged?.Invoke();
            }
        }

        // ------------------------------------------------ run start / level barrier / go-live

        private readonly Dictionary<int, ulong> _levelChecksums = new Dictionary<int, ulong>();

        /// <summary>Host's chosen world seed; 0 = roll a random one at start. Visible to all in lobby.</summary>
        public int ChosenSeed { get; private set; }

        /// <summary>Host only: set the lobby's world seed (0 = random) and tell everyone.</summary>
        public void SetChosenSeed(int seed)
        {
            if (!IsHost) return;
            ChosenSeed = seed;
            BroadcastLobbyState();
            RosterChanged?.Invoke();
        }

        /// <summary>Host only: broadcast the seed and launch the synchronized run.</summary>
        public void StartRun()
        {
            if (!IsHost || State != SessionState.Lobby) return;
            int seed = ChosenSeed != 0 ? ChosenSeed : UnityEngine.Random.Range(1, int.MaxValue);
            _writer.Reset();
            new StartRunMsg { Seed = seed }.Write(_writer);
            ForEachRemotePeer(peer => _transport.Send(peer, NetChannel.Control, _writer.ToSegment(), reliable: true));
            _isRejoin = false;
            BeginRun(seed);
        }

        private void HandleStartRun()
        {
            var msg = StartRunMsg.Read(_reader);
            _isRejoin = msg.IsRejoin;
            BeginRun(msg.Seed);
        }

        private bool _isRejoin;
        private bool _autoPicked;
        private float _autoPickAt;

        /// <summary>Seed of the run in progress — replayed to rejoining players.</summary>
        public int CurrentRunSeed { get; private set; }

        private void BeginRun(int seed)
        {
            CurrentRunSeed = seed;
            _levelChecksums.Clear();
            Sync.ShipSync.ResetStartGate();
            Sync.ShipSync.Reset();
            Sync.ProjectileSync.Reset();
            Sync.DamageSync.Reset();
            Sync.WorldSync.Reset();
            Sync.EnemySync.Reset();
            Sync.MinionSync.Reset();
            Sync.ProgressionSync.Reset();
            Sync.ModuleGridSync.Reset();
            Sync.FogSync.Reset();
            AuthorityManager.Reset();
            NetIds.Reset();
            NetStats.Reset();
            EconomyStash.Reset();
            _autoPicked = false;
            _autoPickAt = Time.unscaledTime + 2f;
            SetState(SessionState.Loading);
            RunStarter.LaunchRun(seed);
        }

        private void OnLevelGenerated(Level level)
        {
            if (!Active || State != SessionState.Loading) return;
            ulong checksum = RunStarter.ChecksumLevel(level);
            Plugin.Log.LogInfo($"[Run] level generated, checksum {checksum:X16}");
            if (IsHost)
            {
                _levelChecksums[0] = checksum;
                CheckGoLive();
            }
            else if (_players[0] != null)
            {
                NetIds.PrepareLocal(); // ready before the host's manifest chunks arrive
                _writer.Reset();
                new LevelReadyMsg { Checksum = checksum }.Write(_writer);
                _transport.Send(_players[0].PeerId, NetChannel.Control, _writer.ToSegment(), reliable: true);
            }
        }

        private void HandleLevelReady(ulong peer)
        {
            var msg = LevelReadyMsg.Read(_reader);
            var player = _players.FirstOrDefault(p => p != null && p.PeerId == peer);
            if (player == null) return;

            if (State == SessionState.InGame)
            {
                // Rejoiner finished regenerating the level. A cell-checksum mismatch here is the
                // known MergedCellsGenerator cosmetic divergence (unseeded UnityEngine.Random) —
                // the entity manifest matches by fingerprint regardless and terrain edits replay
                // from the ledger, so proceed with a warning rather than refusing the rejoin.
                if (_levelChecksums.TryGetValue(0, out var hostChecksum) && msg.Checksum != hostChecksum)
                    Plugin.Log.LogWarning($"[Run] rejoiner cell checksum differs ({msg.Checksum:X16} vs {hostChecksum:X16}) — merged-cell cosmetic divergence, continuing");
                SendRejoinState(peer);
                return;
            }

            _levelChecksums[player.Slot] = msg.Checksum;
            CheckGoLive();
        }

        private void CheckGoLive()
        {
            if (!IsHost || State != SessionState.Loading) return;
            var present = _players.Where(p => p != null && p.Connected).ToList();
            if (present.Any(p => !_levelChecksums.ContainsKey(p.Slot))) return;

            var distinct = _levelChecksums.Values.Distinct().ToList();
            if (distinct.Count > 1)
            {
                var detail = string.Join(", ", _levelChecksums.Select(kv => $"P{kv.Key + 1}={kv.Value:X16}"));
                Plugin.Log.LogError($"[Run] LEVEL CHECKSUM MISMATCH — aborting net run ({detail})");
                _writer.Reset();
                new RejectMsg { Reason = "Level generation diverged between players (checksum mismatch)." }.Write(_writer);
                ForEachRemotePeer(peer => _transport.Send(peer, NetChannel.Control, _writer.ToSegment(), reliable: true));
                StopSession("checksum mismatch");
                return;
            }

            // Everyone verified: hand out entity netIds, then go live.
            var fps = NetIds.BuildManifest();
            const int chunkSize = 120;
            for (int start = 0; start < fps.Count || start == 0; start += chunkSize)
            {
                var chunk = fps.Skip(start).Take(chunkSize).ToArray();
                _writer.Reset();
                new ManifestMsg { StartIndex = (ushort)start, Total = (ushort)fps.Count, Fps = chunk }.Write(_writer);
                ForEachRemotePeer(peer => _transport.Send(peer, NetChannel.Control, _writer.ToSegment(), reliable: true));
                if (fps.Count == 0) break;
            }

            _writer.Reset();
            _writer.WriteMsgType(MsgType.GoLive);
            ForEachRemotePeer(peer => _transport.Send(peer, NetChannel.Control, _writer.ToSegment(), reliable: true));
            DoGoLive();
        }

        private float _autoFlyUntil;

        private void DoGoLive()
        {
            Plugin.Log.LogInfo("[Run] GO LIVE — all players in, starting gameplay");
            SetState(SessionState.InGame);
            Sync.ShipSync.ReleaseStartGate();
            if (_isRejoin)
            {
                _isRejoin = false;
                EconomyStash.TryRestore(CurrentRunSeed);
            }
            if (NetConfig.AutoFly.Value > 0f)
                _autoFlyUntil = Time.unscaledTime + 3f + NetConfig.AutoFly.Value;
        }

        // DEV: scripted flight so two-instance tests can separate ships without input injection.
        // Drives ShipMovement.flyDirection — the same field player input writes — so takeoff,
        // legs, and physics behave exactly as for a real player.
        private void TickAutoFly()
        {
            if (_autoFlyUntil <= 0f || Sync.ShipSync.LocalShip == null) return;
            var movement = Sync.ShipSync.LocalShip.GetComponent<ShipMovement>();
            if (movement == null) return;
            bool flying = Time.unscaledTime <= _autoFlyUntil
                          && Time.unscaledTime >= _autoFlyUntil - NetConfig.AutoFly.Value;
            try
            {
                HarmonyLib.Traverse.Create(movement).Field("flyDirection")
                    .SetValue(flying ? new Vector2(0.9f, 0.45f) : Vector2.zero);
            }
            catch { _autoFlyUntil = 0f; }
            if (Time.unscaledTime > _autoFlyUntil) _autoFlyUntil = 0f;
        }

        // ------------------------------------------------ helpers for sync modules

        /// <summary>Send to every remote peer (host: all clients; client: the host, who relays).</summary>
        public void SendToAll(NetChannel channel, ArraySegment<byte> data, bool reliable)
        {
            if (_transport == null || !_transport.IsRunning) return;
            ForEachRemotePeer(peer => _transport.Send(peer, channel, data, reliable));
        }

        /// <summary>Host: relay the message currently being handled to every client except the sender.</summary>
        private void RelayToOthers(ulong senderPeer, NetChannel channel, bool reliable)
        {
            if (!IsHost) return;
            foreach (var p in _players)
                if (p != null && !p.IsLocal && p.PeerId != senderPeer)
                    _transport.Send(p.PeerId, channel, _lastPayload, reliable);
        }

        private ArraySegment<byte> _lastPayload;

        private void HandleSetLobbyPrefs(ulong peer)
        {
            var prefs = SetLobbyPrefsMsg.Read(_reader);
            var player = _players.FirstOrDefault(p => p != null && p.PeerId == peer);
            if (player == null) return;
            player.ColorIndex = prefs.ColorIndex;
            player.Ready = prefs.Ready;
            BroadcastLobbyState();
            RosterChanged?.Invoke();
        }

        private void OnDestroy()
        {
            GameController.LevelGenerated -= OnLevelGenerated;
            StopSession("plugin unloaded");
            _lobby?.Dispose();
            _lobby = null;
            if (Instance == this) Instance = null;
        }

        // ---------------------------------------------------------------- lifecycle

        public void HostSession()
        {
            if (State != SessionState.Offline) StopSession("rehosting");
            try
            {
                _transport = CreateTransport();
                WireTransport();
                _transport.StartHost();
            }
            catch (Exception e)
            {
                Fail($"Host failed: {e.Message}");
                return;
            }
            LocalSlot = 0;
            _players[0] = new NetPlayer
            {
                Slot = 0,
                PeerId = _transport.LocalPeerId,
                Name = LocalName(),
                IsLocal = true,
            };
            SetState(SessionState.Lobby);
            RosterChanged?.Invoke();
            if (CurrentLobbyCode != null) LastSessionCode = CurrentLobbyCode;
            Plugin.Log.LogInfo($"[Session] hosting as {_players[0]}");
        }

        public void JoinSession(string address)
        {
            if (State != SessionState.Offline) StopSession("rejoining");
            try
            {
                _transport = CreateTransport();
                WireTransport();
                _transport.StartClient(address);
            }
            catch (Exception e)
            {
                Fail($"Join failed: {e.Message}");
                return;
            }
            SetState(SessionState.Connecting);
        }

        public void StopSession(string reason)
        {
            _lobby?.LeaveLobby();
            if (_transport != null)
            {
                _transport.Stop();
                _transport.Dispose();
                _transport = null;
            }
            for (int i = 0; i < MaxPlayers; i++) _players[i] = null;
            LocalSlot = -1;
            ChosenSeed = 0;
            _levelChecksums.Clear();
            Sync.ShipSync.Reset();
            Sync.ShipSync.ResetStartGate();
            Sync.ProjectileSync.Reset();
            Sync.DamageSync.Reset();
            Sync.WorldSync.Reset();
            Sync.EnemySync.Reset();
            Sync.MinionSync.Reset();
            Sync.ProgressionSync.Reset();
            Sync.ModuleGridSync.Reset();
            Sync.FogSync.Reset();
            AuthorityManager.Reset();
            NetIds.Reset();
            NetStats.Reset();
            EconomyStash.Reset();
            if (State != SessionState.Offline)
            {
                Plugin.Log.LogInfo($"[Session] stopped ({reason})");
                SetState(SessionState.Offline);
                RosterChanged?.Invoke();
            }
        }

        private ITransport CreateTransport()
        {
            LastError = null;
            return NetConfig.Transport.Value.Equals("Loopback", StringComparison.OrdinalIgnoreCase)
                ? new LoopbackUdpTransport(NetConfig.LoopbackHost.Value, NetConfig.LoopbackPort.Value)
                : (ITransport)new SteamMessagesTransport();
        }

        private void WireTransport()
        {
            _transport.PeerConnected += OnPeerConnected;
            _transport.PeerDisconnected += OnPeerDisconnected;
            _transport.DataReceived += OnData;
            if (_transport is SteamMessagesTransport steam)
                steam.AllowPeer = id => _lobby != null && _lobby.IsMember(id);
        }

        private void Fail(string error)
        {
            LastError = error;
            Plugin.Log.LogError($"[Session] {error}");
            StopSession(error);
        }

        /// <summary>Persist the join target so "REJOIN LAST" survives a crash.</summary>
        public static string LastSessionCode
        {
            get { try { var p = System.IO.Path.Combine(ModFolder.Dir, "lastsession.txt"); return System.IO.File.Exists(p) ? System.IO.File.ReadAllText(p).Trim() : null; } catch { return null; } }
            set { try { System.IO.File.WriteAllText(System.IO.Path.Combine(ModFolder.Dir, "lastsession.txt"), value ?? ""); } catch { } }
        }

        private void SetState(SessionState s)
        {
            if (State == s) return;
            State = s;
            StateChanged?.Invoke(s);
        }

        private static string LocalName()
        {
            try
            {
                if (!NetConfig.Transport.Value.Equals("Loopback", StringComparison.OrdinalIgnoreCase))
                    return Steamworks.SteamFriends.GetPersonaName();
            }
            catch { /* Steam not up */ }
            return Environment.UserName;
        }

        // ---------------------------------------------------------------- update loop

        private void Update()
        {
            SteamBootstrap.Pump();
            if (_transport == null || !_transport.IsRunning) return;
            _transport.Poll();

            if (State == SessionState.InGame)
            {
                TickAutoFly();
                Sync.ShipSync.Tick(this);
                Sync.WorldSync.Flush(this);
                Sync.EnemySync.Tick(this);
                Sync.ModuleGridSync.Tick(this);
                Sync.FogSync.Tick(this);
                EconomyStash.Tick(this);
                if (IsHost) AuthorityManager.Tick(this);
            }

            // DEV autostart: clickless loadout pick while loading.
            if (State == SessionState.Loading && NetConfig.AutoReady.Value && !_autoPicked
                && Time.unscaledTime >= _autoPickAt)
            {
                if (RunStarter.TryAutoPickLoadout()) _autoPicked = true;
                else _autoPickAt = Time.unscaledTime + 1f;
            }

            // DEV autostart: auto-ready in lobby, host auto-launches when everyone is ready.
            if (State == SessionState.Lobby && NetConfig.AutoReady.Value && LocalPlayer != null && !LocalPlayer.Ready)
                SetLocalPrefs(LocalPlayer.ColorIndex != 0 ? LocalPlayer.ColorIndex : (byte)LocalSlot, true);
            if (State == SessionState.Lobby && NetConfig.AutoLaunchRun.Value && IsHost && AllReady)
                StartRun();

            if (State >= SessionState.Lobby && Time.unscaledTime >= _nextPingAt)
            {
                _nextPingAt = Time.unscaledTime + PingInterval;
                var ping = new PingMsg { TimeMs = (uint)(Time.unscaledTime * 1000f) };
                _writer.Reset();
                ping.Write(_writer, pong: false);
                ForEachRemotePeer(peer => _transport.Send(peer, NetChannel.State, _writer.ToSegment(), reliable: false));
            }
        }

        private void ForEachRemotePeer(Action<ulong> send)
        {
            if (IsHost)
            {
                foreach (var p in _players)
                    if (p != null && !p.IsLocal && p.Connected)
                        send(p.PeerId);
            }
            else if (_players[0] != null)
            {
                send(_players[0].PeerId);
            }
        }

        /// <summary>Targeted reliable-ish send used by the rejoin catch-up stream.</summary>
        public void SendToPeer(ulong peer, NetChannel channel, ArraySegment<byte> data, bool reliable)
        {
            if (_transport != null && _transport.IsRunning) _transport.Send(peer, channel, data, reliable);
        }

        // ---------------------------------------------------------------- transport events

        private void OnPeerConnected(ulong peer)
        {
            if (!IsHost && State == SessionState.Connecting)
            {
                // Connected to the host: introduce ourselves.
                var hello = new HelloMsg
                {
                    ProtocolVersion = ProtocolVersion,
                    ModVersion = Plugin.Version,
                    GameVersion = Application.version,
                    SteamId = _transport is SteamMessagesTransport ? _transport.LocalPeerId : 0,
                    Name = LocalName(),
                };
                _writer.Reset();
                hello.Write(_writer);
                _transport.Send(peer, NetChannel.Control, _writer.ToSegment(), reliable: true);
                Plugin.Log.LogInfo("[Session] sent HELLO");
            }
            // Host side: wait for the HELLO before creating a player.
        }

        private void OnPeerDisconnected(ulong peer)
        {
            if (IsHost)
            {
                var player = _players.FirstOrDefault(p => p != null && p.PeerId == peer);
                if (player == null) return;
                if (State == SessionState.Loading || State == SessionState.InGame)
                {
                    // Mid-run: reserve the slot so they can rejoin; their puppet freezes in place
                    // and the authority scan reassigns their entities on the next pass.
                    player.Connected = false;
                    player.RttMs = -1;
                    Plugin.Log.LogInfo($"[Session] {player} dropped — slot reserved for rejoin");
                }
                else
                {
                    Plugin.Log.LogInfo($"[Session] {player} disconnected");
                    _players[player.Slot] = null;
                }
                BroadcastLobbyState();
                RosterChanged?.Invoke();
            }
            else
            {
                Fail("Lost connection to host");
            }
        }

        private void OnData(ulong peer, NetChannel channel, ArraySegment<byte> payload)
        {
            _lastPayload = payload;
            _reader.Assign(payload);
            MsgType type;
            try { type = _reader.ReadMsgType(); }
            catch { return; }
            try
            {
                Dispatch(peer, channel, type);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[Session] error handling {type} from {peer}: {e}");
            }
        }

        private void Dispatch(ulong peer, NetChannel channel, MsgType type)
        {
            switch (type)
            {
                case MsgType.Hello when IsHost: HandleHello(peer); break;
                case MsgType.SetLobbyPrefs when IsHost: HandleSetLobbyPrefs(peer); break;
                case MsgType.Welcome when !IsHost: HandleWelcome(); break;
                case MsgType.Reject when !IsHost: HandleReject(); break;
                case MsgType.LobbyState when !IsHost: HandleLobbyState(); break;
                case MsgType.Ping: HandlePing(peer); break;
                case MsgType.Pong: HandlePong(peer); break;
                case MsgType.StartRun when !IsHost: HandleStartRun(); break;
                case MsgType.LevelReady when IsHost: HandleLevelReady(peer); break;
                case MsgType.Manifest when !IsHost: NetIds.ApplyChunk(ManifestMsg.Read(_reader)); break;
                case MsgType.GoLive when !IsHost: DoGoLive(); break;
                case MsgType.ShipState:
                {
                    var state = ShipStateMsg.Read(_reader);
                    RelayToOthers(peer, channel, reliable: false);
                    Sync.ShipSync.ApplyShipState(state);
                    break;
                }
                case MsgType.FireEvent:
                {
                    var fire = FireEventMsg.Read(_reader);
                    RelayToOthers(peer, channel, reliable: false);
                    Sync.ProjectileSync.ReplayFire(fire);
                    break;
                }
                case MsgType.DamageRequest:
                {
                    var dmg = DamageRequestMsg.Read(_reader);
                    byte ownerSlot = dmg.IsEntity ? Sync.EnemySync.OwnerOf(dmg.TargetNetId) : dmg.TargetSlot;
                    if (IsHost && ownerSlot != LocalSlot)
                    {
                        // Route to the victim's current authority.
                        var target = _players.FirstOrDefault(p => p != null && p.Slot == ownerSlot);
                        if (target != null && !target.IsLocal)
                            _transport.Send(target.PeerId, NetChannel.Events, _lastPayload, reliable: true);
                    }
                    else
                    {
                        Sync.DamageSync.ApplyDamageRequest(dmg);
                    }
                    break;
                }
                case MsgType.AuthAssign when !IsHost:
                    Sync.EnemySync.ApplyAuthAssign(AuthAssignMsg.Read(_reader));
                    break;
                case MsgType.EntityState:
                {
                    var state = EntityStateMsg.Read(_reader);
                    RelayToOthers(peer, channel, reliable: false);
                    Sync.EnemySync.ApplyEntityState(state);
                    break;
                }
                case MsgType.EntityFire:
                {
                    var efire = EntityFireMsg.Read(_reader);
                    RelayToOthers(peer, channel, reliable: false);
                    Sync.ProjectileSync.ReplayEntityFire(efire);
                    break;
                }
                case MsgType.EntityKilled:
                {
                    var killed = EntityKilledMsg.Read(_reader);
                    RelayToOthers(peer, channel, reliable: true);
                    Sync.EnemySync.ApplyEntityKilled(killed);
                    break;
                }
                case MsgType.EntitySpawned:
                {
                    var spawned = EntitySpawnedMsg.Read(_reader);
                    RelayToOthers(peer, channel, reliable: true);
                    Sync.MinionSync.ApplyEntitySpawned(spawned);
                    break;
                }
                case MsgType.MinionSpawned:
                {
                    var minion = MinionSpawnedMsg.Read(_reader);
                    RelayToOthers(peer, channel, reliable: true);
                    Sync.MinionSync.ApplyMinionSpawned(minion);
                    break;
                }
                case MsgType.StationUpgrade:
                {
                    var upgrade = StationUpgradeMsg.Read(_reader);
                    RelayToOthers(peer, channel, reliable: true);
                    Sync.ProgressionSync.ApplyStationUpgrade(upgrade);
                    break;
                }
                case MsgType.InstrumentUsed:
                {
                    var inst = InstrumentUsedMsg.Read(_reader);
                    RelayToOthers(peer, channel, reliable: true);
                    Sync.ProgressionSync.ApplyInstrumentUsed(inst);
                    break;
                }
                case MsgType.ScannerUsed:
                {
                    var scanner = ScannerUsedMsg.Read(_reader);
                    RelayToOthers(peer, channel, reliable: true);
                    Sync.ProgressionSync.ApplyScannerUsed(scanner);
                    break;
                }
                case MsgType.ModuleGridState:
                {
                    byte slot = _reader.ReadByte();
                    var blob = _reader.ReadBytes();
                    RelayToOthers(peer, channel, reliable: true);
                    Sync.ModuleGridSync.Apply(slot, blob);
                    break;
                }
                case MsgType.FogDiff:
                {
                    RelayToOthers(peer, channel, reliable: true);
                    Sync.FogSync.Apply(_reader);
                    break;
                }
                case MsgType.ShipDied:
                case MsgType.ShipResurrected:
                {
                    var life = ShipLifeMsg.Read(_reader);
                    RelayToOthers(peer, channel, reliable: true);
                    Sync.DamageSync.ApplyLifeEvent(life, died: type == MsgType.ShipDied);
                    break;
                }
                case MsgType.CellDiff:
                {
                    var diff = CellDiffMsg.Read(_reader);
                    RelayToOthers(peer, channel, reliable: true);
                    Sync.WorldSync.Apply(diff);
                    break;
                }
                default:
                    Plugin.Log.LogDebug($"[Session] unhandled {type} on ch{(int)channel} from {peer}");
                    break;
            }
        }

        // ---------------------------------------------------------------- handshake (host)

        private void HandleHello(ulong peer)
        {
            var hello = HelloMsg.Read(_reader);
            string reject = null;
            if (hello.ProtocolVersion != ProtocolVersion || hello.ModVersion != Plugin.Version)
                reject = $"Version mismatch: host has mod {Plugin.Version} (protocol {ProtocolVersion}), you have {hello.ModVersion} (protocol {hello.ProtocolVersion}).";
            else if (hello.GameVersion != Application.version)
                reject = $"Game version mismatch: host {Application.version}, you {hello.GameVersion}.";
            else if (State != SessionState.Lobby)
            {
                // Mid-run: this is a rejoin if a reserved slot matches (SteamID, else name).
                var reserved = _players.FirstOrDefault(p => p != null && !p.Connected
                    && ((hello.SteamId != 0 && p.PeerId == hello.SteamId) || p.Name == hello.Name));
                if (reserved != null)
                {
                    HandleRejoin(peer, hello, reserved);
                    return;
                }
                reject = "Session already in progress.";
            }

            int slot = -1;
            if (reject == null)
            {
                for (int i = 1; i < MaxPlayers; i++)
                    if (_players[i] == null) { slot = i; break; }
                if (slot < 0) reject = "Lobby is full.";
            }

            if (reject != null)
            {
                _writer.Reset();
                new RejectMsg { Reason = reject }.Write(_writer);
                _transport.Send(peer, NetChannel.Control, _writer.ToSegment(), reliable: true);
                Plugin.Log.LogWarning($"[Session] rejected {peer}: {reject}");
                return;
            }

            _players[slot] = new NetPlayer
            {
                Slot = (byte)slot,
                PeerId = peer,
                Name = hello.Name,
            };
            Plugin.Log.LogInfo($"[Session] {_players[slot]} joined");

            _writer.Reset();
            new WelcomeMsg { Slot = (byte)slot, HostModVersion = Plugin.Version, Roster = BuildRoster() }.Write(_writer);
            _transport.Send(peer, NetChannel.Control, _writer.ToSegment(), reliable: true);
            BroadcastLobbyState();
            RosterChanged?.Invoke();
        }

        private void HandleRejoin(ulong peer, HelloMsg hello, NetPlayer reserved)
        {
            reserved.PeerId = peer;
            reserved.Connected = true;
            reserved.Name = hello.Name;
            Plugin.Log.LogInfo($"[Session] {reserved} REJOINED — replaying run seed {CurrentRunSeed}");

            _writer.Reset();
            new WelcomeMsg { Slot = reserved.Slot, HostModVersion = Plugin.Version, Roster = BuildRoster() }.Write(_writer);
            _transport.Send(peer, NetChannel.Control, _writer.ToSegment(), reliable: true);
            BroadcastLobbyState();
            RosterChanged?.Invoke();

            _writer.Reset();
            new StartRunMsg { Seed = CurrentRunSeed, IsRejoin = true }.Write(_writer);
            _transport.Send(peer, NetChannel.Control, _writer.ToSegment(), reliable: true);
            // Their LEVEL_READY (handled below while we're InGame) triggers the catch-up stream.
        }

        private void SendRejoinState(ulong peer)
        {
            // 1) Entity identity (host's manifest, unchanged since run start).
            var fps = NetIds.LastManifest;
            const int chunkSize = 120;
            for (int start = 0; start < fps.Count || start == 0; start += chunkSize)
            {
                var chunk = fps.Skip(start).Take(chunkSize).ToArray();
                _writer.Reset();
                new ManifestMsg { StartIndex = (ushort)start, Total = (ushort)fps.Count, Fps = chunk }.Write(_writer);
                _transport.Send(peer, NetChannel.Control, _writer.ToSegment(), reliable: true);
                if (fps.Count == 0) break;
            }

            // 2) Terrain changes since generation.
            var ledger = Sync.WorldSync.LedgerSnapshot();
            for (int start = 0; start < ledger.Count; start += 500)
            {
                _writer.Reset();
                new CellDiffMsg { Cells = ledger.GetRange(start, Math.Min(500, ledger.Count - start)) }.Write(_writer);
                _transport.Send(peer, NetChannel.Events, _writer.ToSegment(), reliable: true);
            }

            // 3) Deaths, ownership, shared progression.
            foreach (var netId in Sync.EnemySync.KilledSnapshot())
            {
                _writer.Reset();
                new EntityKilledMsg { NetId = netId, KillerSlot = 0 }.Write(_writer);
                _transport.Send(peer, NetChannel.Events, _writer.ToSegment(), reliable: true);
            }
            var owners = Sync.EnemySync.OwnersSnapshot();
            for (int start = 0; start < owners.Count; start += 64)
            {
                _writer.Reset();
                new AuthAssignMsg { Entries = owners.GetRange(start, Math.Min(64, owners.Count - start)) }.Write(_writer);
                _transport.Send(peer, NetChannel.Events, _writer.ToSegment(), reliable: true);
            }
            foreach (var (netId, hash) in Sync.ProgressionSync.UpgradeSnapshot())
            {
                _writer.Reset();
                new StationUpgradeMsg { StationNetId = netId, UpgradeHash = hash }.Write(_writer);
                _transport.Send(peer, NetChannel.Events, _writer.ToSegment(), reliable: true);
            }
            foreach (var (netId2, hash2) in Sync.ProgressionSync.InstrumentSnapshot())
            {
                _writer.Reset();
                new InstrumentUsedMsg { NetId = netId2, DiscoverableHash = hash2 }.Write(_writer);
                _transport.Send(peer, NetChannel.Events, _writer.ToSegment(), reliable: true);
            }
            foreach (var netId in Sync.ProgressionSync.ScannerSnapshot())
            {
                _writer.Reset();
                new ScannerUsedMsg { NetId = netId }.Write(_writer);
                _transport.Send(peer, NetChannel.Events, _writer.ToSegment(), reliable: true);
            }

            // 4) Go.
            _writer.Reset();
            _writer.WriteMsgType(MsgType.GoLive);
            _transport.Send(peer, NetChannel.Control, _writer.ToSegment(), reliable: true);
            Plugin.Log.LogInfo($"[Session] rejoin catch-up sent to {peer} " +
                $"({ledger.Count} cells, {owners.Count} owners, manifest {fps.Count})");
        }

        private List<RosterEntry> BuildRoster()
        {
            var roster = new List<RosterEntry>();
            foreach (var p in _players)
                if (p != null)
                    roster.Add(new RosterEntry { Slot = p.Slot, PeerId = p.PeerId, Name = p.Name, ColorIndex = p.ColorIndex, Ready = p.Ready, Connected = p.Connected });
            return roster;
        }

        private void BroadcastLobbyState()
        {
            _writer.Reset();
            new LobbyStateMsg { Roster = BuildRoster(), HostSeed = ChosenSeed }.Write(_writer);
            ForEachRemotePeer(peer => _transport.Send(peer, NetChannel.Control, _writer.ToSegment(), reliable: true));
        }

        // ---------------------------------------------------------------- handshake (client)

        private void HandleWelcome()
        {
            var welcome = WelcomeMsg.Read(_reader);
            ApplyRoster(welcome.Roster);
            LocalSlot = welcome.Slot;
            if (_players[LocalSlot] != null) _players[LocalSlot].IsLocal = true;
            if (CurrentLobbyCode != null) LastSessionCode = CurrentLobbyCode;
            SetState(SessionState.Lobby);
            Plugin.Log.LogInfo($"[Session] welcomed as slot {welcome.Slot} (host mod v{welcome.HostModVersion})");
            RosterChanged?.Invoke();
        }

        private void HandleReject()
        {
            var reject = RejectMsg.Read(_reader);
            Fail($"Rejected by host: {reject.Reason}");
        }

        private void HandleLobbyState()
        {
            var lobby = LobbyStateMsg.Read(_reader);
            ChosenSeed = lobby.HostSeed;
            ApplyRoster(lobby.Roster);
            if (LocalSlot >= 0 && _players[LocalSlot] != null) _players[LocalSlot].IsLocal = true;
            RosterChanged?.Invoke();
        }

        private void ApplyRoster(List<RosterEntry> roster)
        {
            var oldRtt = _players.Where(p => p != null).ToDictionary(p => p.PeerId, p => p.RttMs);
            for (int i = 0; i < MaxPlayers; i++) _players[i] = null;
            foreach (var e in roster)
            {
                _players[e.Slot] = new NetPlayer
                {
                    Slot = e.Slot,
                    PeerId = e.PeerId,
                    Name = e.Name,
                    ColorIndex = e.ColorIndex,
                    Ready = e.Ready,
                    Connected = e.Connected,
                    RttMs = oldRtt.TryGetValue(e.PeerId, out var rtt) ? rtt : -1,
                };
            }
        }

        // ---------------------------------------------------------------- ping

        private void HandlePing(ulong peer)
        {
            var ping = PingMsg.Read(_reader);
            _writer.Reset();
            ping.Write(_writer, pong: true);
            _transport.Send(peer, NetChannel.State, _writer.ToSegment(), reliable: false);
        }

        private void HandlePong(ulong peer)
        {
            var pong = PingMsg.Read(_reader);
            int rtt = (int)((uint)(Time.unscaledTime * 1000f) - pong.TimeMs);
            var player = _players.FirstOrDefault(p => p != null && p.PeerId == peer);
            if (player == null) return;
            bool first = player.RttMs < 0;
            player.RttMs = rtt;
            if (first) Plugin.Log.LogInfo($"[Session] PING OK: first PONG from {player}, rtt={rtt}ms");
        }
    }
}
