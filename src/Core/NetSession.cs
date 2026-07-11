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
        private const float ConnectTimeout = 15f;

        public static NetSession Instance { get; private set; }
        /// <summary>True whenever networking is live — every Harmony patch gates on this.</summary>
        public static bool Active => Instance != null && Instance.State != SessionState.Offline;

        public SessionState State { get; private set; } = SessionState.Offline;
        public bool IsHost => _transport?.IsHost ?? false;
        public int LocalSlot { get; private set; } = -1;
        /// <summary>Slot of the current session host — 0 until host migration promotes someone
        /// else. Registrar fallbacks and client send-targets key off this, never literal 0.</summary>
        public byte HostSlot { get; private set; }
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
                // Solo start is allowed — friends can join a run in progress with the code.
                return count >= 1;
            }
        }

        private readonly NetWriter _writer = new NetWriter(8 * 1024);
        private readonly NetReader _reader = new NetReader();
        private float _nextPingAt;
        private float _connectDeadline;

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

            // The overlay join-request callback registers in the lobby controller's ctor —
            // create it NOW, or accepting a Steam invite does nothing until the player has
            // opened PLAY ONLINE at least once.
            if (UsingSteam && SteamBootstrap.Available) EnsureLobbyController();

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

        private int _pendingHostSeed;
        private bool _pendingFriendlyFire;

        /// <summary>Host's game-settings choice: players can damage each other. Replicated in
        /// every LOBBY_STATE; each machine enforces it at its own damage chokepoints.</summary>
        public bool FriendlyFire { get; private set; }

        private bool _pendingHpScaling;
        /// <summary>Host's game-settings choice: scale enemy health by player count.</summary>
        public bool HpScaling { get; private set; }
        /// <summary>Enemy max-health multiplier for the current run, fixed at START GAME:
        /// 1 + (EnemyHealthScalePerPlayer * connected players). Replicated in START_RUN.</summary>
        public float EnemyHpMult { get; private set; } = 1f;

        private NetRunSave.Data _pendingResume;

        /// <summary>Host a lobby that resumes the saved run: same seed and settings; after
        /// go-live the ledgers replay to everyone and players spawn at the saved checkpoint
        /// with their stashed builds.</summary>
        public void HostResume()
        {
            var data = NetRunSave.Load();
            if (data == null)
            {
                LastError = "No saved run to resume.";
                return;
            }
            HostOnline(data.Seed, data.FriendlyFire, data.HpScaling);
            _pendingResume = data; // after HostOnline: a synchronous loopback restart won't clear it
            Plugin.Log.LogInfo($"[Session] hosting resume of run seed {data.Seed} " +
                $"({data.Cells.Count} cells, {data.Kills.Count} kills, {data.Upgrades.Count} upgrades)");
        }

        /// <summary>Host: Steam = create lobby then open transport; loopback = open transport
        /// directly. <paramref name="chosenSeed"/> (0 = random) becomes the lobby's world seed
        /// once the session is up.</summary>
        public void HostOnline(int chosenSeed = 0, bool friendlyFire = false, bool enemyHpScaling = true)
        {
            try
            {
                LastError = null;
                _pendingHostSeed = chosenSeed;
                _pendingFriendlyFire = friendlyFire;
                _pendingHpScaling = enemyHpScaling;
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

        /// <summary>Host: remove a player from the lobby (pre-run only). Their slot frees up;
        /// they get told why. No ban — they can rejoin with the code.</summary>
        public void KickPlayer(byte slot)
        {
            if (!IsHost || State != SessionState.Lobby) return;
            var p = slot < MaxPlayers ? _players[slot] : null;
            if (p == null || p.IsLocal) return;
            Plugin.Log.LogInfo($"[Session] kicked {p}");
            _writer.Reset();
            _writer.WriteMsgType(MsgType.Kicked);
            _transport.Send(p.PeerId, NetChannel.Control, _writer.ToSegment(), reliable: true);
            _players[slot] = null;
            BroadcastLobbyState();
            RosterChanged?.Invoke();
            UI.Toast.Show($"{p.Name} WAS KICKED", 4f);
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
            else if (_players[HostSlot] != null)
            {
                _writer.Reset();
                new SetLobbyPrefsMsg { ColorIndex = colorIndex, Ready = ready }.Write(_writer);
                _transport.Send(_players[HostSlot].PeerId, NetChannel.Control, _writer.ToSegment(), reliable: true);
                RosterChanged?.Invoke();
            }
        }

        // ------------------------------------------------ run start / level barrier / go-live

        private readonly Dictionary<int, ulong> _levelChecksums = new Dictionary<int, ulong>();

        /// <summary>Host's chosen world seed (picked on the GAME SETTINGS screen);
        /// 0 = roll a random one at start. Visible to all in lobby.</summary>
        public int ChosenSeed { get; private set; }

        /// <summary>Host only: broadcast the seed and launch the synchronized run.</summary>
        public void StartRun()
        {
            if (!IsHost || State != SessionState.Lobby) return;
            int seed = ChosenSeed != 0 ? ChosenSeed : UnityEngine.Random.Range(1, int.MaxValue);

            // Enemy health scaling is fixed for the whole run at start:
            // Base Health * (1 + (EnemyHealthScalePerPlayer * number of players)).
            int playerCount = _players.Count(p => p != null && p.Connected);
            EnemyHpMult = HpScaling
                ? 1f + Mathf.Max(0f, NetConfig.EnemyHealthScalePerPlayer.Value) * playerCount
                : 1f;
            if (EnemyHpMult > 1f)
                Plugin.Log.LogInfo($"[Run] enemy HP x{EnemyHpMult:F2} ({playerCount} players)");

            // Resuming a saved run rides the rejoin path for EVERYONE: economy stash restore
            // and checkpoint spawn included. Terrain restores from each machine's OWN save
            // (it can be map-scale); the small ledgers replay after go-live.
            bool resume = _pendingResume != null;
            int spawnStation = resume ? _pendingResume.LatestStationNetId : 0;

            _writer.Reset();
            new StartRunMsg
            {
                Seed = seed,
                IsRejoin = resume,
                IsResume = resume,
                SpawnStationNetId = spawnStation,
                EnemyHpMult = EnemyHpMult,
            }.Write(_writer);
            ForEachRemotePeer(peer => _transport.Send(peer, NetChannel.Control, _writer.ToSegment(), reliable: true));
            _isRejoin = resume;
            _spawnStationNetId = spawnStation;
            BeginRun(seed);
        }

        private void HandleStartRun()
        {
            var msg = StartRunMsg.Read(_reader);
            _isRejoin = msg.IsRejoin; // IsResume needs no client-side handling anymore:
                                      // terrain always streams from the host either way
            _spawnStationNetId = msg.SpawnStationNetId;
            EnemyHpMult = msg.EnemyHpMult > 0f ? msg.EnemyHpMult : 1f;
            BeginRun(msg.Seed);
        }

        private bool _isRejoin;
        private int _spawnStationNetId;
        private bool _migrating;          // host-migration election in progress
        private bool _reattaching;        // reconnecting to the migrated host mid-run
        private float _reattachDeadline;
        private ulong _joinTargetPeerId;  // SteamID64 we connected to (0 on loopback)
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
            ClockSync.Reset();
            EconomyStash.Reset();
            NetRunSave.Reset();
            Sync.HookSync.Reset();
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
                // Fingerprint NOW, same simulation moment every client snapshots at. Waiting
                // for go-live let entities drift first, and every early mover became a
                // manifest mismatch (~2% of the map: phantom orphans on clients).
                NetIds.PrepareLocal();
                _levelChecksums[0] = checksum;
                CheckGoLive();
            }
            else if (_players[HostSlot] != null)
            {
                NetIds.PrepareLocal(); // ready before the host's manifest chunks arrive
                _writer.Reset();
                new LevelReadyMsg { Checksum = checksum }.Write(_writer);
                _transport.Send(_players[HostSlot].PeerId, NetChannel.Control, _writer.ToSegment(), reliable: true);
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
                // Spawn at the party's latest unlocked station instead of the run start.
                if (_spawnStationNetId != 0) Sync.ShipSync.TeleportLocalShip(_spawnStationNetId);
            }
            if (IsHost && _pendingResume != null)
            {
                var data = _pendingResume;
                _pendingResume = null;
                ApplyResumePayload(data);
            }
            // Clients never restore terrain from their own save: the host's ledger is the one
            // source of truth and streams in vicinity-first chunks (resume: after its local
            // restore; rejoin: from SendRejoinState) — no save required, no divergence.
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
            HostSlot = 0;
            ChosenSeed = _pendingHostSeed; // settings picked on the pre-lobby screen
            FriendlyFire = _pendingFriendlyFire;
            HpScaling = _pendingHpScaling;
            _pendingHostSeed = 0;
            _pendingFriendlyFire = false;
            _pendingHpScaling = false;
            SetState(SessionState.Lobby);
            RosterChanged?.Invoke();
            Plugin.Log.LogInfo($"[Session] hosting as {_players[0]}");
        }

        public void JoinSession(string address)
        {
            if (State != SessionState.Offline) StopSession("rejoining");
            HostSlot = 0;
            ulong.TryParse(address, out _joinTargetPeerId); // SteamID64; loopback "ip:port" -> 0
            try
            {
                _transport = CreateTransport();
                WireTransport();
                // Must be Connecting before the transport starts: the HELLO goes out from
                // PeerConnected, which is a no-op in any other state.
                SetState(SessionState.Connecting);
                _connectDeadline = Time.unscaledTime + ConnectTimeout;
                _transport.StartClient(address);
            }
            catch (Exception e)
            {
                Fail($"Join failed: {e.Message}");
                return;
            }
        }

        public void StopSession(string reason)
        {
            bool wasInRun = State == SessionState.Loading || State == SessionState.InGame;

            // Tell everyone this is a deliberate end, not a connection hiccup, while the
            // transport is still up (host only — a quitting client just drops and gets its
            // slot reserved for rejoin).
            if (IsHost && State >= SessionState.Lobby && _transport != null && _transport.IsRunning)
            {
                try
                {
                    _writer.Reset();
                    _writer.WriteMsgType(MsgType.SessionEnded);
                    ForEachRemotePeer(peer => _transport.Send(peer, NetChannel.Control, _writer.ToSegment(), reliable: true));
                }
                catch { }
            }

            if (wasInRun)
            {
                // Final economy stash + run save (the periodic ones may be seconds stale) and
                // world cleanup: the run continues solo, so it must be coherent — teammate
                // puppets despawn and remote-simulated enemies get their AI back.
                try { EconomyStash.Save(CurrentRunSeed); } catch { }
                try { NetRunSave.Save(this); } catch { }
                CleanupAbandonedRun();
            }

            _lobby?.LeaveLobby();
            if (_transport != null)
            {
                _transport.Stop();
                _transport.Dispose();
                _transport = null;
            }
            for (int i = 0; i < MaxPlayers; i++) _players[i] = null;
            LocalSlot = -1;
            HostSlot = 0;
            _migrating = false;
            _reattaching = false;
            ChosenSeed = 0;
            FriendlyFire = false;
            HpScaling = false;
            EnemyHpMult = 1f;
            _pendingResume = null;
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
            ClockSync.Reset();
            EconomyStash.Reset();
            Sync.HookSync.Reset();
            if (State != SessionState.Offline)
            {
                Plugin.Log.LogInfo($"[Session] stopped ({reason})");
                SetState(SessionState.Offline);
                RosterChanged?.Invoke();
            }
        }

        private static void CleanupAbandonedRun()
        {
            try
            {
                foreach (var puppet in UnityEngine.Object.FindObjectsByType<Sync.RemotePuppet>(
                             FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    try { puppet.gameObject.SetActive(false); } catch { }
                }
                foreach (var puppet in UnityEngine.Object.FindObjectsByType<Sync.RemoteEntityPuppet>(
                             FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    try { UnityEngine.Object.Destroy(puppet); } catch { } // OnDestroy unmutes the AI
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Session] world cleanup failed: {e.Message}");
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

            if (State == SessionState.Connecting && Time.unscaledTime >= _connectDeadline)
            {
                Fail("Could not reach host — no response for 15 seconds.");
                return;
            }
            if (_reattaching && Time.unscaledTime >= _reattachDeadline)
            {
                _reattaching = false;
                Fail("Could not reach the new host.");
                return;
            }

            if (State == SessionState.InGame)
            {
                TickAutoFly();
                Sync.ShipSync.Tick(this);
                Sync.WorldSync.Flush(this);
                Sync.WorldSync.Tick(this); // paced cell application + catch-up streams
                Sync.EnemySync.Tick(this);
                Sync.ModuleGridSync.Tick(this);
                Sync.FogSync.Tick(this);
                EconomyStash.Tick(this);
                NetRunSave.Tick(this);
                if (IsHost)
                {
                    AuthorityManager.Tick(this);
                    CheckPartyWipe();
                }
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
            else if (_players[HostSlot] != null)
            {
                send(_players[HostSlot].PeerId);
            }
        }

        /// <summary>Targeted reliable-ish send used by the rejoin catch-up stream.</summary>
        public void SendToPeer(ulong peer, NetChannel channel, ArraySegment<byte> data, bool reliable)
        {
            if (_transport != null && _transport.IsRunning) _transport.Send(peer, channel, data, reliable);
        }

        // ---------------------------------------------------------------- transport events

        // ---------------------------------------------------------------- party wipe

        private float _allDeadSince = -1f;

        /// <summary>Host: when every connected player's ship is dead (debounced), the run is
        /// over. The vanilla game-over Retry can't restart a NET run (its world regenerates
        /// past the mod's start gates) — instead everyone returns to the LOBBY, session
        /// intact, and the host launches the next run through the synchronized path.</summary>
        private void CheckPartyWipe()
        {
            bool anyAlive = false;
            int counted = 0;
            foreach (var p in _players)
            {
                if (p == null || !p.Connected) continue;
                Ship ship = p.IsLocal
                    ? Sync.ShipSync.LocalShip
                    : (Sync.ShipSync.ShipsBySlot.TryGetValue(p.Slot, out var s) ? s : null);
                if (ship == null) continue;
                counted++;
                try { if (!ship.IsDead) anyAlive = true; } catch { anyAlive = true; }
            }
            if (counted == 0 || anyAlive)
            {
                _allDeadSince = -1f;
                return;
            }
            if (_allDeadSince < 0f)
            {
                _allDeadSince = Time.unscaledTime;
                return;
            }
            if (Time.unscaledTime - _allDeadSince < 2f) return;

            Plugin.Log.LogInfo("[Session] party wiped — returning everyone to the lobby");
            _writer.Reset();
            _writer.WriteMsgType(MsgType.RunEnded);
            ForEachRemotePeer(peer => _transport.Send(peer, NetChannel.Control, _writer.ToSegment(), reliable: true));
            EndRunToLobby();
        }

        /// <summary>Host: synchronized retry — the vanilla Restart is intercepted in net runs
        /// and lands here. Ends the current run quietly if needed (everyone drops to Lobby
        /// state under the hood, gates reset), then launches a fresh run for the whole party.</summary>
        public void RestartRun()
        {
            if (!IsHost) return;
            if (State == SessionState.InGame || State == SessionState.Loading)
            {
                _writer.Reset();
                _writer.WriteMsgType(MsgType.RunEnded);
                ForEachRemotePeer(peer => _transport.Send(peer, NetChannel.Control, _writer.ToSegment(), reliable: true));
                EndRunToLobby(announce: false);
            }
            if (State != SessionState.Lobby) return;
            Plugin.Log.LogInfo("[Session] host retry — starting a fresh synchronized run");
            StartRun();
        }

        private bool _quietLobbyOnce;

        /// <summary>One-shot: the lobby state change after a wipe/retry shouldn't pop the
        /// lobby overlay — players are looking at the game-over screen.</summary>
        public bool ConsumeQuietLobby()
        {
            if (!_quietLobbyOnce) return false;
            _quietLobbyOnce = false;
            return true;
        }

        /// <summary>The run is over but the session lives: reset all run-scoped sync state
        /// (including the start gates a vanilla retry would trip over) and clear ready flags.
        /// A wiped run's save is deleted — defeat isn't resumable.</summary>
        private void EndRunToLobby(bool announce = true)
        {
            _allDeadSince = -1f;
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
            EconomyStash.Reset();
            NetRunSave.Reset();
            NetRunSave.Delete();
            Sync.HookSync.Reset();
            foreach (var p in _players)
                if (p != null)
                    p.Ready = false;
            if (announce)
                UI.Toast.Show(IsHost
                    ? "RUN OVER — PRESS RETRY FOR A NEW RUN"
                    : "RUN OVER — WAITING FOR THE HOST TO RETRY", 6f);
            _quietLobbyOnce = true; // stay on the game-over screen, not the lobby overlay
            SetState(SessionState.Lobby);
            RosterChanged?.Invoke();
        }

        private void OnPeerConnected(ulong peer)
        {
            if (!IsHost && (State == SessionState.Connecting || _reattaching))
            {
                // Connected to the host: introduce ourselves.
                var hello = new HelloMsg
                {
                    ProtocolVersion = ProtocolVersion,
                    ModVersion = Plugin.Version,
                    GameVersion = Application.version,
                    SteamId = _transport is SteamMessagesTransport ? _transport.LocalPeerId : 0,
                    Name = LocalName(),
                    Resuming = _reattaching,
                    Mods = ModManifest.Local,
                };
                _writer.Reset();
                hello.Write(_writer);
                _transport.Send(peer, NetChannel.Control, _writer.ToSegment(), reliable: true);
                Plugin.Log.LogInfo(_reattaching ? "[Session] sent HELLO (resume)" : "[Session] sent HELLO");
            }
            // Host side: wait for the HELLO before creating a player.
        }

        // ---------------------------------------------------------------- host migration

        /// <summary>The host is gone. Mid-run on Steam we migrate instead of dying: Steam has
        /// already re-assigned lobby ownership to a remaining member — that member promotes
        /// itself, everyone else reattaches, and the old host's slot is reserved for rejoin.</summary>
        private void OnHostLost(string reason)
        {
            if (State == SessionState.InGame && UsingSteam && _lobby != null && _lobby.InLobby && !_migrating)
            {
                _migrating = true;
                StartCoroutine(MigrateHost(reason));
                return;
            }
            Fail(reason);
        }

        private System.Collections.IEnumerator MigrateHost(string reason)
        {
            Plugin.Log.LogInfo($"[Session] {reason} — electing a new host…");
            var oldHost = _players[HostSlot];
            if (oldHost != null)
            {
                oldHost.Connected = false;
                oldHost.RttMs = -1;
            }
            RosterChanged?.Invoke();

            // Steam migrates lobby ownership to a remaining member; every client sees the same
            // new owner, so no election protocol is needed. Wait for the handover.
            ulong newHostId = 0;
            float deadline = Time.unscaledTime + 15f;
            while (Time.unscaledTime < deadline)
            {
                ulong owner = 0;
                try { owner = Steamworks.SteamMatchmaking.GetLobbyOwner(_lobby.CurrentLobby).m_SteamID; }
                catch { }
                if (owner != 0 && (oldHost == null || owner != oldHost.PeerId)
                    && _players.Any(p => p != null && p.PeerId == owner))
                {
                    newHostId = owner;
                    break;
                }
                yield return new WaitForSecondsRealtime(0.5f);
            }
            if (newHostId == 0)
            {
                _migrating = false;
                Fail(reason);
                yield break;
            }

            if (newHostId == _transport.LocalPeerId) BecomeHost();
            else ReattachTo(newHostId);
            _migrating = false;
        }

        private void BecomeHost()
        {
            Plugin.Log.LogInfo("[Session] promoted to host (migration)");
            _transport.Stop();
            _transport.Dispose();
            _transport = new SteamMessagesTransport();
            WireTransport();
            _transport.StartHost();
            HostSlot = (byte)LocalSlot;
            // Everyone else is disconnected from ME right now — reserve their slots; their
            // resume-HELLOs (or full rejoins) bring them back.
            foreach (var p in _players)
                if (p != null && !p.IsLocal)
                {
                    p.Connected = false;
                    p.RttMs = -1;
                }
            _lobby.TakeOverLobby();
            UI.Toast.Show("HOST LEFT — YOU ARE NOW THE HOST", 6f);
            RosterChanged?.Invoke();
        }

        private void ReattachTo(ulong newHostId)
        {
            var hostPlayer = _players.FirstOrDefault(p => p != null && p.PeerId == newHostId);
            if (hostPlayer == null)
            {
                Fail("Lost connection to host");
                return;
            }
            Plugin.Log.LogInfo($"[Session] reattaching to new host {hostPlayer}");
            HostSlot = hostPlayer.Slot;
            UI.Toast.Show($"HOST LEFT — {hostPlayer.Name} IS NOW HOST", 6f);
            _transport.Stop();
            _transport.Dispose();
            _transport = new SteamMessagesTransport();
            WireTransport();
            _reattaching = true;
            _reattachDeadline = Time.unscaledTime + 20f;
            _joinTargetPeerId = newHostId;
            _transport.StartClient(newHostId.ToString());
            // PeerConnected (first Poll) sends the resume-HELLO; Welcome completes reattach.
        }

        private void OnPeerDisconnected(ulong peer)
        {
            Sync.WorldSync.CancelStream(peer); // a rejoin restarts it from scratch
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
                OnHostLost("Lost connection to host");
            }
        }

        private void OnData(ulong peer, NetChannel channel, ArraySegment<byte> payload)
        {
            _lastPayload = payload;
            _reader.Assign(payload);
            MsgType type;
            try { type = _reader.ReadMsgType(); }
            catch { return; }
            NetStats.AddIn((byte)type, payload.Count);
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
            // Gameplay traffic can only be applied while a world exists (Loading covers rejoin
            // catch-up). After a run ends, in-flight kills/fire/cells from peers would land on
            // a destroyed level — drop them. Ping/Pong keep the lobby RTT alive.
            if (channel != NetChannel.Control && type != MsgType.Ping && type != MsgType.Pong
                && State < SessionState.Loading)
                return;

            switch (type)
            {
                case MsgType.Hello when IsHost: HandleHello(peer); break;
                case MsgType.SetLobbyPrefs when IsHost: HandleSetLobbyPrefs(peer); break;
                case MsgType.Welcome when !IsHost: HandleWelcome(); break;
                case MsgType.Reject when !IsHost: HandleReject(); break;
                case MsgType.SessionEnded when !IsHost: OnHostLost("Host ended the session."); break;
                case MsgType.Kicked when !IsHost:
                    UI.Toast.Show("YOU HAVE BEEN KICKED FROM THE LOBBY", 6f);
                    Fail("You were kicked by the host.");
                    break;
                case MsgType.RunEnded when !IsHost: EndRunToLobby(); break;
                case MsgType.AuthRelease when IsHost:
                    Sync.EnemySync.ApplyAuthRelease(AuthReleaseMsg.Read(_reader), this);
                    break;
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
                case MsgType.ShipDash:
                {
                    var dash = ShipDashMsg.Read(_reader);
                    RelayToOthers(peer, channel, reliable: false);
                    Sync.ShipSync.ApplyDash(dash);
                    break;
                }
                case MsgType.DamageRequest:
                {
                    var dmg = DamageRequestMsg.Read(_reader);
                    if (IsHost && dmg.IsEntity) AuthorityManager.NoteCombat(dmg.TargetNetId);
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
                    if (IsHost) AuthorityManager.NoteAggro(efire.NetId, efire.TargetSlot);
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
                case MsgType.HookState:
                {
                    var hookMsg = HookStateMsg.Read(_reader);
                    RelayToOthers(peer, channel, reliable: true);
                    Sync.HookSync.Apply(hookMsg);
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
                case MsgType.TerrainSync when !IsHost:
                    Sync.WorldSync.ApplyTerrainSync(TerrainSyncMsg.Read(_reader));
                    break;
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
            bool midRun = State != SessionState.Lobby;

            // Other installed mods can change gameplay rules or conflict with the netcode's
            // patches — the host's ModManifestPolicy decides how strict to be.
            bool modsMismatch = false;
            if (!ModManifest.Matches(hello.Mods)
                && !NetConfig.ModManifestPolicy.Value.Equals("Ignore", StringComparison.OrdinalIgnoreCase))
            {
                modsMismatch = true;
                string diff = ModManifest.Describe(hello.Mods);
                Plugin.Log.LogWarning($"[Mods] '{hello.Name}' mod set differs — {diff}");
                if (NetConfig.ModManifestPolicy.Value.Equals("Reject", StringComparison.OrdinalIgnoreCase))
                    reject = $"Installed mods differ from the host's ({diff}). " +
                             "Match the host's mod set, or the host can set ModManifestPolicy=Warn.";
            }

            if (hello.ProtocolVersion != ProtocolVersion || hello.ModVersion != Plugin.Version)
                reject = $"Version mismatch: host has mod {Plugin.Version} (protocol {ProtocolVersion}), you have {hello.ModVersion} (protocol {hello.ProtocolVersion}).";
            else if (hello.GameVersion != Application.version)
                reject = $"Game version mismatch: host {Application.version}, you {hello.GameVersion}.";
            else if (reject == null && midRun)
            {
                // Mid-run: a reserved slot matching (SteamID, else name) is a rejoin; anyone
                // else is a late joiner and takes a free slot below.
                var reserved = _players.FirstOrDefault(p => p != null && !p.Connected
                    && ((hello.SteamId != 0 && p.PeerId == hello.SteamId) || p.Name == hello.Name));
                if (reserved != null)
                {
                    reserved.ModsMismatch = modsMismatch;
                    if (hello.Resuming) HandleResume(peer, hello, reserved);
                    else HandleRejoin(peer, hello, reserved);
                    return;
                }
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
                ModsMismatch = modsMismatch,
            };
            Plugin.Log.LogInfo($"[Session] {_players[slot]} joined{(midRun ? " (mid-run, catching up)" : "")}");
            if (modsMismatch) UI.Toast.Show($"{hello.Name} JOINED WITH A DIFFERENT MOD SET", 5f);

            _writer.Reset();
            new WelcomeMsg { Slot = (byte)slot, HostModVersion = Plugin.Version, Roster = BuildRoster() }.Write(_writer);
            _transport.Send(peer, NetChannel.Control, _writer.ToSegment(), reliable: true);
            BroadcastLobbyState();
            RosterChanged?.Invoke();

            if (midRun)
            {
                // Late joiner rides the rejoin path: their LEVEL_READY triggers the catch-up
                // replay (InGame) or joins the go-live barrier (Loading).
                _writer.Reset();
                new StartRunMsg
                {
                    Seed = CurrentRunSeed,
                    IsRejoin = true,
                    SpawnStationNetId = Sync.ProgressionSync.LatestStationNetId,
                    EnemyHpMult = EnemyHpMult,
                }.Write(_writer);
                _transport.Send(peer, NetChannel.Control, _writer.ToSegment(), reliable: true);
            }
        }

        /// <summary>Host-migration reattach: the peer is already live in the same world — no
        /// regen, just roster + the reliable events it may have missed during the handover gap
        /// (all idempotent on the receiving side).</summary>
        private void HandleResume(ulong peer, HelloMsg hello, NetPlayer reserved)
        {
            reserved.PeerId = peer;
            reserved.Connected = true;
            reserved.Name = hello.Name;
            Plugin.Log.LogInfo($"[Session] {reserved} reattached after host migration");

            _writer.Reset();
            new WelcomeMsg { Slot = reserved.Slot, HostModVersion = Plugin.Version, Roster = BuildRoster() }.Write(_writer);
            _transport.Send(peer, NetChannel.Control, _writer.ToSegment(), reliable: true);
            BroadcastLobbyState();
            RosterChanged?.Invoke();
            SendEventCatchUp(peer);
            // Cell diffs sent during the handover gap are gone for good — stream the ledger
            // (idempotent, vicinity-first) so the reattached peer converges regardless.
            Sync.WorldSync.BeginStreamTo(this, peer, reserved.Slot, StreamFallbackPos());
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
            new StartRunMsg
            {
                Seed = CurrentRunSeed,
                IsRejoin = true,
                SpawnStationNetId = Sync.ProgressionSync.LatestStationNetId,
                EnemyHpMult = EnemyHpMult,
            }.Write(_writer);
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

            // 2) Terrain changes since generation stream in vicinity-first chunks around the
            // rejoiner — the whole ledger regardless of size, budgeted per frame so the
            // reliable buffer can't overflow and no size cutoff can leave them divergent.
            var rejoiner = _players.FirstOrDefault(p => p != null && p.PeerId == peer);
            Sync.WorldSync.BeginStreamTo(this, peer, rejoiner?.Slot ?? 0, StreamFallbackPos());

            // 3) Deaths, ownership, shared progression.
            SendEventCatchUp(peer);

            // 4) Go.
            _writer.Reset();
            _writer.WriteMsgType(MsgType.GoLive);
            _transport.Send(peer, NetChannel.Control, _writer.ToSegment(), reliable: true);
            Plugin.Log.LogInfo($"[Session] rejoin catch-up sent to {peer} (manifest {fps.Count})");
        }

        /// <summary>Where a joiner will appear, for chunk prioritization before their ship
        /// exists: the party's latest station, else the host's own ship, else map center.</summary>
        private Vector2 StreamFallbackPos()
        {
            if (Sync.ShipSync.TryGetEntityPosition(Sync.ProgressionSync.LatestStationNetId, out var station))
                return station;
            if (Sync.ShipSync.LocalShip != null)
                return Sync.ShipSync.LocalShip.transform.position;
            return Vector2.zero;
        }

        /// <summary>Replay a saved run's ledgers on the host: terrain applies locally paced
        /// (it can be millions of cells), then streams to every client in vicinity-first
        /// chunks — clients need no save of their own and can never diverge. The small event
        /// ledgers apply locally and broadcast, throttled so the reliable buffer holds.</summary>
        private void ApplyResumePayload(NetRunSave.Data data)
        {
            StartCoroutine(RestoreTerrainThenStream(data));
            StartCoroutine(BroadcastEventLedgersPaced(data));
        }

        private System.Collections.IEnumerator RestoreTerrainThenStream(NetRunSave.Data data)
        {
            // The paced apply fills the ledger as it goes; streams start once it's complete
            // so every chunk a client receives reflects the full saved history.
            yield return StartCoroutine(ApplyCellsPaced(data.Cells, "resume"));
            if (State != SessionState.InGame) yield break;
            foreach (var p in _players)
                if (p != null && !p.IsLocal && p.Connected)
                    Sync.WorldSync.BeginStreamTo(this, p.PeerId, p.Slot, StreamFallbackPos());
        }

        private System.Collections.IEnumerator ApplyCellsPaced(List<(int index, byte type)> cells, string source)
        {
            const int cellsPerFrame = 20000;
            int sinceYield = 0;
            for (int start = 0; start < cells.Count; start += 500)
            {
                var msg = new CellDiffMsg { Cells = cells.GetRange(start, Math.Min(500, cells.Count - start)) };
                Sync.WorldSync.Apply(msg);
                sinceYield += msg.Cells.Count;
                if (sinceYield >= cellsPerFrame)
                {
                    sinceYield = 0;
                    yield return null;
                }
                if (State != SessionState.InGame) yield break; // run ended mid-restore
            }
            Plugin.Log.LogInfo($"[Session] terrain restored from {source} ({cells.Count} cells)");
        }

        private System.Collections.IEnumerator BroadcastEventLedgersPaced(NetRunSave.Data data)
        {
            const int messagesPerFrame = 32;
            int sent = 0;

            System.Collections.IEnumerator Pace()
            {
                if (++sent >= messagesPerFrame)
                {
                    sent = 0;
                    yield return null;
                }
            }

            void Broadcast()
            {
                var segment = _writer.ToSegment();
                ForEachRemotePeer(peer => _transport.Send(peer, NetChannel.Events, segment, reliable: true));
            }

            foreach (var netId in data.Kills)
            {
                var msg = new EntityKilledMsg { NetId = netId, KillerSlot = 0 };
                Sync.EnemySync.ApplyEntityKilled(msg);
                _writer.Reset();
                msg.Write(_writer);
                Broadcast();
                yield return Pace();
                if (State != SessionState.InGame) yield break;
            }
            foreach (var (netId, hash) in data.Upgrades)
            {
                var msg = new StationUpgradeMsg { StationNetId = netId, UpgradeHash = hash };
                Sync.ProgressionSync.ApplyStationUpgrade(msg);
                _writer.Reset();
                msg.Write(_writer);
                Broadcast();
                yield return Pace();
            }
            foreach (var (netId, hash) in data.Instruments)
            {
                var msg = new InstrumentUsedMsg { NetId = netId, DiscoverableHash = hash };
                Sync.ProgressionSync.ApplyInstrumentUsed(msg);
                _writer.Reset();
                msg.Write(_writer);
                Broadcast();
                yield return Pace();
            }
            foreach (var netId in data.Scanners)
            {
                var msg = new ScannerUsedMsg { NetId = netId };
                Sync.ProgressionSync.ApplyScannerUsed(msg);
                _writer.Reset();
                msg.Write(_writer);
                Broadcast();
                yield return Pace();
            }
            Sync.ProgressionSync.RestoreCheckpoint(data.LatestStationNetId);
            UI.Toast.Show("RUN RESUMED", 4f);
            Plugin.Log.LogInfo($"[Session] resume events replayed ({data.Kills.Count} kills, " +
                $"{data.Upgrades.Count} upgrades, {data.Scanners.Count} scanners)");
        }

        /// <summary>Deaths, ownership, and shared progression — every message idempotent on the
        /// receiver, so it serves both full rejoins and post-migration resume gaps.</summary>
        private void SendEventCatchUp(ulong peer)
        {
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
        }

        private List<RosterEntry> BuildRoster()
        {
            var roster = new List<RosterEntry>();
            foreach (var p in _players)
                if (p != null)
                    roster.Add(new RosterEntry
                    {
                        Slot = p.Slot,
                        PeerId = p.PeerId,
                        Name = p.Name,
                        ColorIndex = p.ColorIndex,
                        Ready = p.Ready,
                        Connected = p.Connected,
                        ModsMismatch = p.ModsMismatch,
                    });
            return roster;
        }

        private void BroadcastLobbyState()
        {
            _writer.Reset();
            new LobbyStateMsg { Roster = BuildRoster(), HostSeed = ChosenSeed, FriendlyFire = FriendlyFire }.Write(_writer);
            ForEachRemotePeer(peer => _transport.Send(peer, NetChannel.Control, _writer.ToSegment(), reliable: true));
        }

        // ---------------------------------------------------------------- handshake (client)

        private void HandleWelcome()
        {
            var welcome = WelcomeMsg.Read(_reader);
            ApplyRoster(welcome.Roster);
            LocalSlot = welcome.Slot;
            if (_players[LocalSlot] != null) _players[LocalSlot].IsLocal = true;
            // The host is whoever we connected to — after a migration that isn't slot 0.
            var hostPlayer = _players.FirstOrDefault(p => p != null && _joinTargetPeerId != 0 && p.PeerId == _joinTargetPeerId);
            HostSlot = hostPlayer != null ? hostPlayer.Slot : (byte)0;
            if (_reattaching)
            {
                _reattaching = false;
                Plugin.Log.LogInfo($"[Session] reattached to new host (slot {welcome.Slot}, host slot {HostSlot})");
                RosterChanged?.Invoke();
                return; // still InGame — the run never stopped
            }
            SetState(SessionState.Lobby);
            Plugin.Log.LogInfo($"[Session] welcomed as slot {welcome.Slot} (host mod v{welcome.HostModVersion})");
            RosterChanged?.Invoke();
        }

        private void HandleReject()
        {
            var reject = RejectMsg.Read(_reader);
            var hint = reject.Reason.Contains("Version mismatch") ? $" Get the latest: {UpdateCheck.ReleasesUrl}" : "";
            Fail($"Rejected by host: {reject.Reason}{hint}");
        }

        private void HandleLobbyState()
        {
            var lobby = LobbyStateMsg.Read(_reader);
            ChosenSeed = lobby.HostSeed;
            FriendlyFire = lobby.FriendlyFire;
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
                    ModsMismatch = e.ModsMismatch,
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
