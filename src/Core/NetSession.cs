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
        public const int ProtocolVersion = 14; // 14 = main's 13 (EntityFireMsg.WeaponHash, fire
                                                // fidelity) + this branch's sidecar/admin additions
                                                // (RosterEntry.IsAdmin, LobbyMembers 88, AdminGrant 89,
                                                // AdminCommand 90) — both 13s were parallel, distinct
                                                // wire formats; the union is 14.
        public const int MaxPlayers = 4;
        /// <summary>Reserved slot for a dedicated coordinator — OUTSIDE the 0..MaxPlayers-1 player
        /// range, so a shipless server never consumes one of the four player/ship slots. Only ever
        /// occupied in a coordinator session; always null in normal play (which leaves every
        /// "for i &lt; MaxPlayers" player loop untouched).</summary>
        public const int CoordinatorSlot = MaxPlayers; // = 4
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

        private readonly NetPlayer[] _players = new NetPlayer[MaxPlayers + 1]; // +1 = CoordinatorSlot
        public IReadOnlyList<NetPlayer> Players => _players;
        public NetPlayer LocalPlayer => LocalSlot >= 0 ? _players[LocalSlot] : null;

        private ITransport _transport;
        public ITransport Transport => _transport;

        private SteamLobbyController _lobby;
        public SteamLobbyController Lobby => _lobby;
        // True while this session runs against a locally-spawned sidecar coordinator: the join uses
        // the launcher-chosen transport (below) for THIS session, not the player's config value.
        private bool _sidecarSession;
        private string _sidecarTransport = "Loopback"; // set at spawn from SidecarLauncher.ChosenTransport
        // Sidecar parity (#1): the settings the hosting player picked on the Host screen, forwarded
        // to the shipless coordinator once we reach its lobby so it hosts THEIR world, not defaults.
        private bool _haveLeaderSettings, _leaderSettingsSent, _leaderFriendlyFire, _leaderHpScaling;
        private int _leaderSeed;
        // Sidecar parity (#2/#3): a SteamServer session's discovery lobby. A remote friend who enters
        // it connects to the server id (not the lobby owner), so their transport must resolve to
        // SteamServer even though their config says "Steam".
        private string _lobbyServerTransport; // non-null on a friend who joined a SteamServer discovery lobby
        private bool _serverLobbyRequested;   // the invite-owner asked Steam to create the discovery lobby
        private float _nextServerLobbyRetryAt; // backoff after a failed discovery-lobby creation
        private bool _allowlistDirty;         // discovery-lobby membership changed; re-relay to the coordinator
        // Coordinator side of #2: the SteamID64s the party leader says are lobby members. Null until the
        // first relay arrives (accept-all bootstrap); once set, a HELLO from a non-member is refused.
        private HashSet<ulong> _allowedPeers;
        // Session admin (standalone/sidecar): the first real player to connect gets host-like controls.
        // Coordinator side tracks who holds it and the secret token that authorizes their commands; the
        // token is transport-agnostic (never trusts the peer id), so it holds for the LiteNetLib server.
        private int _adminSlot = -1;      // coordinator: slot of the current admin, -1 = none
        private ulong _adminToken;        // coordinator: capability token issued to the admin
        private ulong _localAdminToken;   // client: my token when I'm the admin (0 = not admin)
        private float _nextAdminGrantResendAt; // periodic re-grant: heals an admin whose game restarted
                                               // (roster flag survives the rejoin; the token didn't)

        /// <summary>The single point where a session's transport is decided. Adding a transport is
        /// ONE config value + ONE case in CreateTransport:
        ///   Steam       — user P2P (normal friend play)
        ///   Loopback    — dev/LAN UDP
        ///   SteamServer — connect to an anonymous game-server identity (dedicated/sidecar)
        ///   (planned) Udp — LiteNetLib for Docker/no-Steam
        /// A coordinator uses its launch env; a sidecar SESSION uses the launcher's choice; everyone
        /// else uses config. A coordinator is never a Steam USER endpoint, so config "Steam" there
        /// falls back to Loopback via the env default.</summary>
        internal string ResolvedTransport =>
            NetConfig.IsCoordinator ? NetConfig.EnvCoordinatorTransport
            : _sidecarSession ? _sidecarTransport
            : _directTransport ?? _lobbyServerTransport ?? NetConfig.Transport.Value;

        // Per-session transport override for a direct IP:port connect from the PLAY ONLINE screen.
        // Lets a player join a Udp server by typing an address — no config edit, no persisted
        // change to their default transport. Cleared on StopSession like _lobbyServerTransport.
        private string _directTransport;

        public bool UsingSteam => ResolvedTransport.Equals("Steam", StringComparison.OrdinalIgnoreCase);

        /// <summary>The INVITE FRIENDS button lights up whenever we hold a Steam lobby — an ordinary
        /// user-P2P lobby OR a SteamServer discovery lobby (listen-server host or sidecar leader).</summary>
        public bool CanInvite => _lobby != null && _lobby.InLobby;

        /// <summary>Whoever holds host-like controls (START / KICK). A normal player-host is always its
        /// own admin; in a shipless-coordinator session it's the promoted player, not the headless
        /// server. Drives which client sees the host UI; the server still re-checks the token.</summary>
        public bool IsSessionAdmin => (IsHost && !NetConfig.IsCoordinator) || (LocalPlayer?.IsAdmin ?? false);

        /// <summary>Pasteable code for the current Steam lobby, or the direct-connect address.</summary>
        public string CurrentLobbyCode =>
            _lobby != null && _lobby.InLobby ? SteamLobbyController.EncodeLobbyCode(_lobby.CurrentLobby)
            : State != SessionState.Offline && ResolvedTransport.Equals("Udp", StringComparison.OrdinalIgnoreCase)
                ? $"{NetConfig.UdpAddress.Value}:{NetConfig.UdpPort.Value}"
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
        private float _nextSeqCheckpointAt;                 // WS8.2 host: next checkpoint stamp
        private readonly float[] _nextGapReportAt = new float[4];   // WS8.2 client: per-channel rate limit
        private readonly Dictionary<ulong, float> _nextGapCatchUpAt = new Dictionary<ulong, float>(); // host: per-peer
        // WS8.2 client: previous checkpoint baseline per channel. Gap detection compares GROWTH
        // between consecutive checkpoints (sentDelta vs receivedDelta), never absolute counts —
        // a reconnect-in-place resets the client's receive counters while the host (which never
        // saw a disconnect) keeps counting, and an absolute comparison then reports a huge frozen
        // phantom deficit forever (caught by the release soak: "2395 lost" repeating). The first
        // checkpoint after any reset only re-baselines.
        private readonly uint[] _seqBaselineSent = new uint[4];
        private readonly uint[] _seqBaselineReceived = new uint[4];
        private readonly bool[] _seqBaselined = new bool[4];
        private float _connectDeadline;
        private ulong _localIdentityId;

        public event Action RosterChanged;
        public event Action<SessionState> StateChanged;

        private void Awake()
        {
            Instance = this;
            _localIdentityId = ComputeLoopbackIdentity();
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
            // A dedicated coordinator always hosts — that's its entire job.
            var mode = NetConfig.IsCoordinator ? "Host" : NetConfig.AutoStart.Value;
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

        /// <summary>Rejoin the session this machine last went live in (RejoinMemory) — the
        /// normal join path; the host's rejoin machinery does the catch-up. Callers should
        /// gate on ProbeRejoinTarget so this is only offered while the session still exists.</summary>
        public void RejoinLastSession()
        {
            if (!RejoinMemory.TryLoad(out var record))
            {
                LastError = "No recent session to rejoin.";
                return;
            }
            LastError = null;
            if (record.Steam)
            {
                if (!UsingSteam) { LastError = "Last session was a Steam session (transport is Loopback)."; return; }
                Plugin.Log.LogInfo($"[Session] rejoining last session (lobby {record.LobbyId})");
                JoinLobbyId(new Steamworks.CSteamID(record.LobbyId));
            }
            else
            {
                if (UsingSteam) { LastError = "Last session was a loopback session (transport is Steam)."; return; }
                Plugin.Log.LogInfo($"[Session] rejoining last session ({record.Address})");
                JoinSession(record.Address);
            }
        }

        /// <summary>Is the remembered session still alive and joinable? Steam: asks the lobby
        /// directory (async; "no reply" never invokes the callback — treat the button as hidden
        /// until a probe says otherwise). Loopback (dev): no directory to ask — reports true and
        /// lets the connect timeout arbitrate. Safe to call repeatedly; overlapping Steam probes
        /// are dropped.</summary>
        public void ProbeRejoinTarget(Action<bool> done)
        {
            try
            {
                if (State != SessionState.Offline || !RejoinMemory.TryLoad(out var record)) { done(false); return; }
                if (!record.Steam) { done(!UsingSteam); return; }
                if (!UsingSteam || !SteamBootstrap.Available) { done(false); return; }
                EnsureLobbyController();
                _lobby.ProbeLobby(new Steamworks.CSteamID(record.LobbyId), done);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Rejoin] probe failed: {e.Message}");
                done(false);
            }
        }

        /// <summary>Host: Steam = create lobby then open transport; loopback = open transport
        /// directly. <paramref name="chosenSeed"/> (0 = random) becomes the lobby's world seed
        /// once the session is up.</summary>
        public void HostOnline(int chosenSeed = 0, bool friendlyFire = false, bool enemyHpScaling = true)
        {
            // Server sidecar (local/LAN only): hosting spawns a dedicated coordinator process and
            // this game joins it as a regular player. Falls back to classic in-process hosting if
            // the sidecar can't start. Seed/settings forwarding to the coordinator is not built
            // yet (party-leader protocol) — the coordinator uses defaults; say so.
            if (NetConfig.HostViaSidecar.Value && !NetConfig.IsCoordinator)
            {
                if (SidecarLauncher.LaunchIfNeeded(out string sidecarError))
                {
                    // Carry the host player's world choice to the coordinator (#1). Sent when we
                    // reach its lobby (see Update); the coordinator adopts it before StartRun.
                    _leaderSeed = chosenSeed;
                    _leaderFriendlyFire = friendlyFire;
                    _leaderHpScaling = enemyHpScaling;
                    _haveLeaderSettings = true;
                    _leaderSettingsSent = false;
                    LastError = null;
                    _sidecarTransport = SidecarLauncher.ChosenTransport;
                    StartCoroutine(JoinSidecarWhenUp());
                    return;
                }
                Plugin.Log.LogWarning($"[Sidecar] could not spawn coordinator ({sidecarError}) — hosting in-process instead");
            }
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

        /// <summary>Join the freshly-spawned sidecar coordinator, retrying while it boots (a cold
        /// Punk.exe takes 10-25s to reach hosting). Each attempt uses the normal join path with its
        /// own connect timeout; the coordinator's lobby appears to this player exactly like any
        /// remote host's.</summary>
        private System.Collections.IEnumerator JoinSidecarWhenUp()
        {
            _sidecarSession = true;
            bool steamServer = _sidecarTransport.Equals("SteamServer", StringComparison.OrdinalIgnoreCase);
            float deadline = Time.unscaledTime + 90f;
            Plugin.Log.LogInfo($"[Sidecar] waiting for the coordinator ({_sidecarTransport}), then joining");
            while (Time.unscaledTime < deadline)
            {
                if (State >= SessionState.Lobby) yield break; // connected — lobby reached
                if (State == SessionState.Offline)
                {
                    if (!SidecarLauncher.IsRunning)
                    {
                        _sidecarSession = false;
                        Fail("Sidecar coordinator exited before the session came up.");
                        yield break;
                    }
                    _sidecarSession = true; // StopSession clears it after each failed attempt

                    // SteamServer: the coordinator publishes its server SteamID to the id file once
                    // logged on — that file existing IS the readiness signal (and the join code).
                    // Loopback: join the fixed local address directly.
                    string address = null;
                    if (steamServer)
                    {
                        ulong serverId = GameServerBootstrap.ReadPublishedId();
                        if (serverId != 0) address = serverId.ToString();
                    }
                    else address = $"{NetConfig.LoopbackHost.Value}:{NetConfig.LoopbackPort.Value}";

                    if (address != null) JoinSession(address); // else wait for the id file to appear
                }
                yield return new WaitForSecondsRealtime(3f);
            }
            if (State < SessionState.Lobby)
            {
                _sidecarSession = false;
                Fail("Sidecar coordinator did not come up within 90s.");
            }
        }

        /// <summary>Keep a SteamServer session's discovery lobby open and its allowlist fresh (#2/#3).
        /// The invite OWNER — a listen-server host, or the player that launched a sidecar coordinator —
        /// holds a Steam lobby stamped with the server id so friends join-by-invite; the same player
        /// relays lobby membership to a shipless coordinator so it can gate joins. Remote friends and
        /// the coordinator itself skip all of this.</summary>
        private void MaintainServerLobby()
        {
            bool inviteOwner = !NetConfig.IsCoordinator
                && State >= SessionState.Lobby
                && ResolvedTransport.Equals("SteamServer", StringComparison.OrdinalIgnoreCase)
                && (IsHost || (_sidecarSession && _haveLeaderSettings)); // not a plain lobby-joined friend
            if (!inviteOwner) return;

            ulong serverId = SteamServerCode; // 0 until the server id is known (welcome / logon)
            if (serverId == 0) return;

            if (_lobby == null || !_lobby.InLobby)
            {
                if (_serverLobbyRequested) return; // creation already in flight
                if (Time.unscaledTime < _nextServerLobbyRetryAt) return; // backoff after a failure
                _serverLobbyRequested = true;
                EnsureLobbyController();
                _lobby.CreateServerLobby(serverId);
                return;
            }

            // Coordinator sessions only: push the current member set when it changed. A listen-server
            // host gates at accept via IsMember and needs no relay.
            if (!IsHost && _allowlistDirty && _players[HostSlot] != null)
            {
                _allowlistDirty = false;
                SendAllowlistToCoordinator();
            }
        }

        private void SendAllowlistToCoordinator()
        {
            var ids = _lobby.MemberIds();
            _writer.Reset();
            new LobbyMembersMsg { Members = ids }.Write(_writer);
            SendReliable(_players[HostSlot].PeerId, NetChannel.Control, _writer.ToSegment());
            Plugin.Log.LogInfo($"[Sidecar] relayed {ids.Length} lobby member(s) to the coordinator");
        }

        /// <summary>Join: Steam = decode lobby code (null = clipboard); loopback = address (null = config default).</summary>
        /// <summary>Direct-connect to a Udp server by address (PLAY ONLINE -> DIRECT CONNECT).
        /// Forces the Udp transport for this session only — the player never edits config. On an
        /// unreachable server the join times out (ConnectTimeout) and Fail() sets LastError, which
        /// the UI surfaces as a toast. host may be an IP or hostname; port is validated by caller.</summary>
        public void JoinDirect(string host, int port)
        {
            if (State != SessionState.Offline) StopSession("direct connect"); // clears prior overrides
            _directTransport = "Udp"; // set AFTER StopSession so it survives into the join
            LastError = null;
            JoinSession($"{host.Trim()}:{port}");
        }

        public void JoinByCode(string codeOrAddress)
        {
            LastError = null;
            // SteamServer join code = the server's SteamID64. A remote friend pastes it (the host
            // shares it via the SERVER CODE display / `servercode` devcmd). Read the clipboard when
            // the button passes null, exactly like the Steam lobby-code path.
            if (ResolvedTransport.Equals("SteamServer", StringComparison.OrdinalIgnoreCase))
            {
                var raw = string.IsNullOrWhiteSpace(codeOrAddress) ? GUIUtility.systemCopyBuffer : codeOrAddress;
                raw = raw?.Trim();
                if (!ulong.TryParse(raw, out ulong serverId) || serverId == 0)
                {
                    LastError = "Paste a server code (17-digit ID) to join a dedicated server.";
                    Plugin.Log.LogWarning($"[Session] {LastError}");
                    return;
                }
                JoinSession(serverId.ToString());
                return;
            }
            if (!UsingSteam)
            {
                bool udp = ResolvedTransport.Equals("Udp", StringComparison.OrdinalIgnoreCase);
                JoinSession(string.IsNullOrWhiteSpace(codeOrAddress)
                    ? (udp ? $"{NetConfig.UdpAddress.Value}:{NetConfig.UdpPort.Value}"
                           : $"{NetConfig.LoopbackHost.Value}:{NetConfig.LoopbackPort.Value}")
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

        /// <summary>The join code to SHARE for the current SteamServer session (the coordinator's
        /// server SteamID64), or 0 when this isn't a SteamServer session. A sidecar/remote player
        /// shares the host slot's id; a listen-server host shares its own. Remote friends paste it
        /// into Join.</summary>
        public ulong SteamServerCode
        {
            get
            {
                if (!ResolvedTransport.Equals("SteamServer", StringComparison.OrdinalIgnoreCase)) return 0;
                if (IsHost) return _transport?.LocalPeerId ?? 0;                 // listen-server host
                // A coordinator sits in the reserved slot (CoordinatorSlot == MaxPlayers), so the
                // upper bound is inclusive — the pre-#4 `< MaxPlayers` guard read the server id as 0.
                return HostSlot <= MaxPlayers ? (_players[HostSlot]?.PeerId ?? 0) : 0; // player of a coordinator
            }
        }

        public void JoinLobbyId(Steamworks.CSteamID lobbyId)
        {
            try
            {
                // Leave any live session BEFORE entering the lobby (the overlay path already does).
                // If it happened inside LobbyJoined->JoinSession instead, StopSession would leave the
                // lobby we JUST joined (breaking the host's IsMember gate) and clear the discovery
                // lobby's SteamServer transport override before CreateTransport reads it.
                if (State != SessionState.Offline) StopSession("joining another lobby");
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
            if (slot == _adminSlot) { _adminSlot = -1; _adminToken = 0; } // promote a successor next tick
            BroadcastLobbyState();
            RosterChanged?.Invoke();
            UI.Toast.Show($"{p.Name} WAS KICKED", 4f);
        }

        // ------------------------------------------------ session admin (standalone/sidecar)

        /// <summary>UI entry: start the run. A normal player-host starts locally; a coordinator's admin
        /// sends a token-proven request the server validates. Either way the START button calls this.</summary>
        public void RequestStart()
        {
            if (IsHost && !NetConfig.IsCoordinator) { StartRun(); return; }
            if (LocalPlayer?.IsAdmin == true && _localAdminToken != 0)
                SendAdminCommand(AdminCmd.StartRun, 0);
        }

        /// <summary>UI entry: kick a player. Player-host kicks locally; a coordinator's admin sends a
        /// token-proven request. The KICK buttons call this instead of KickPlayer directly.</summary>
        public void RequestKick(byte slot)
        {
            if (IsHost && !NetConfig.IsCoordinator) { KickPlayer(slot); return; }
            if (LocalPlayer?.IsAdmin == true && _localAdminToken != 0)
                SendAdminCommand(AdminCmd.Kick, slot);
        }

        private void SendAdminCommand(AdminCmd cmd, byte arg)
        {
            if (_players[HostSlot] == null) return;
            _writer.Reset();
            new AdminCommandMsg { Token = _localAdminToken, Command = cmd, Arg = arg }.Write(_writer);
            SendReliable(_players[HostSlot].PeerId, NetChannel.Control, _writer.ToSegment());
            Plugin.Log.LogInfo($"[Admin] sent {cmd}({arg}) to the server");
        }

        /// <summary>Coordinator: keep exactly one connected player flagged as admin. The first real
        /// joiner is promoted; if the admin leaves, the next connected player inherits it with a FRESH
        /// token (the old one is void). Idempotent — the fast path returns when the admin is unchanged.</summary>
        private void EnsureAdminAssigned()
        {
            var current = _adminSlot >= 0 && _adminSlot <= MaxPlayers ? _players[_adminSlot] : null;
            if (current != null && current.Connected && !current.IsCoordinator)
            {
                if (!current.IsAdmin) { current.IsAdmin = true; BroadcastLobbyState(); RosterChanged?.Invoke(); }
                // Re-send the SAME grant on a slow cadence: an admin whose game restarted keeps the
                // roster flag through the rejoin but lost the token with the process (on SteamServer
                // its peer id is the same SteamID64, so a route change can't be detected). Idempotent
                // — the client just overwrites its copy with the identical value.
                if (Time.unscaledTime >= _nextAdminGrantResendAt)
                {
                    _nextAdminGrantResendAt = Time.unscaledTime + 5f;
                    _writer.Reset();
                    new AdminGrantMsg { Token = _adminToken }.Write(_writer);
                    SendReliable(current.PeerId, NetChannel.Control, _writer.ToSegment());
                }
                return; // admin still valid
            }

            var next = _players
                .Where(p => p != null && p.Connected && !p.IsCoordinator)
                .OrderBy(p => p.Slot)
                .FirstOrDefault();

            foreach (var p in _players) if (p != null) p.IsAdmin = false;
            if (next == null) { _adminSlot = -1; _adminToken = 0; return; } // nobody to promote

            next.IsAdmin = true;
            _adminSlot = next.Slot;
            _adminToken = NewCapabilityToken();
            _nextAdminGrantResendAt = Time.unscaledTime + 5f;
            _writer.Reset();
            new AdminGrantMsg { Token = _adminToken }.Write(_writer);
            SendReliable(next.PeerId, NetChannel.Control, _writer.ToSegment());
            Plugin.Log.LogInfo($"[Admin] {next} is now session admin (token issued)");
            BroadcastLobbyState();
            RosterChanged?.Invoke();
        }

        private static ulong NewCapabilityToken()
        {
            var b = new byte[8];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create()) rng.GetBytes(b);
            ulong t = System.BitConverter.ToUInt64(b, 0);
            return t == 0 ? 1UL : t; // 0 means "no token"
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
            _lobby.LobbyCreated += _ =>
            {
                // A discovery lobby (SteamServer) is opened AFTER the session is already up — it must
                // not (re)host. An ordinary Steam lobby's creation IS the cue to open the host socket.
                if (_serverLobbyRequested) { _allowlistDirty = true; return; }
                HostSession();
            };
            _lobby.LobbyJoined += (_, hostId) =>
            {
                // SteamServer discovery lobby: connect to the anonymous server it points at, forcing
                // this session onto the SteamServer transport regardless of our own config value.
                if (_lobby.IsServerLobby && _lobby.LobbyServerId != 0)
                {
                    _lobbyServerTransport = "SteamServer";
                    Plugin.Log.LogInfo($"[Session] discovery lobby -> joining server {_lobby.LobbyServerId}");
                    JoinSession(_lobby.LobbyServerId.ToString());
                    return;
                }
                JoinSession(hostId.ToString());
            };
            _lobby.MembershipChanged += () => _allowlistDirty = true;
            _lobby.LobbyError += err =>
            {
                LastError = err;
                // A failed DISCOVERY-lobby creation must be retryable — otherwise one transient Steam
                // hiccup means no invites (and for a listen-server host, no joins) all session.
                if (_serverLobbyRequested) { _serverLobbyRequested = false; _nextServerLobbyRetryAt = Time.unscaledTime + 10f; }
                Plugin.Log.LogWarning($"[Session] {err}");
            };
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
        private readonly Dictionary<int, LevelReadyMsg> _levelFingerprints = new Dictionary<int, LevelReadyMsg>();
        // LEVEL_READY is used both by the initial start barrier and by peers that regenerate
        // the world for a late join/rejoin.  Track the latter explicitly: once a catch-up has
        // started, duplicate readiness packets must never enqueue another map-sized replay.
        private readonly HashSet<ulong> _peersAwaitingRejoinState = new HashSet<ulong>();
        private readonly Dictionary<ulong, float> _nextGoLiveRecoveryAt = new Dictionary<ulong, float>();
        private bool _hasLocalLevelChecksum;
        private ulong _localLevelChecksum;
        private LevelReadyMsg _localLevelReady;
        private float _nextLevelReadyRetryAt;
        private bool _levelReadyVisualPending;
        private float _levelReadyVisualStartedAt;

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
            int playerCount = _players.Count(p => p != null && p.Connected && !p.IsCoordinator);
            EnemyHpMult = HpScaling
                ? 1f + Mathf.Max(0f, NetConfig.EnemyHealthScalePerPlayer.Value) * playerCount
                : 1f;
            if (EnemyHpMult > 1f)
                Plugin.Log.LogInfo($"[Run] enemy HP x{EnemyHpMult:F2} ({playerCount} players)");

            _writer.Reset();
            new StartRunMsg
            {
                Seed = seed,
                IsRejoin = false,
                IsResume = false, // wire field kept for compatibility; save-based resume removed
                SpawnStationNetId = 0,
                EnemyHpMult = EnemyHpMult,
            }.Write(_writer);
            ForEachRemotePeer(peer => _transport.Send(peer, NetChannel.Control, _writer.ToSegment(), reliable: true));
            _isRejoin = false;
            _spawnStationNetId = 0;
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
        private byte _joinTargetHostSlot;
        private bool _autoPicked;
        private float _autoPickAt;

        /// <summary>Seed of the run in progress — replayed to rejoining players.</summary>
        public int CurrentRunSeed { get; private set; }

        private void BeginRun(int seed)
        {
            CurrentRunSeed = seed;
            _levelChecksums.Clear();
            _levelFingerprints.Clear();
            _peersAwaitingRejoinState.Clear();
            _nextGoLiveRecoveryAt.Clear();
            _hasLocalLevelChecksum = false;
            _localLevelChecksum = 0;
            _localLevelReady = default;
            _nextLevelReadyRetryAt = 0f;
            _goLiveDeadline = 0f; // re-armed when this machine's LEVEL_READY goes out
            _levelReadyVisualPending = false;
            _levelReadyVisualStartedAt = 0f;
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
            Sync.MapShareSync.Reset();
            DevTools.Reset();
            AuthorityManager.Reset();
            NetDiag.Reset();
            NetIds.Reset();
            NetStats.Reset();
            NetSeq.Reset();
            _nextSeqCheckpointAt = 0;
            for (int i = 0; i < _nextGapReportAt.Length; i++) _nextGapReportAt[i] = 0;
            _nextGapCatchUpAt.Clear();
            ResetSeqBaselines();
            NetProfiler.Reset();
            RuntimeInstrumentation.ResetRun();
            ClockSync.Reset();
            EconomyStash.Reset();
            Sync.HookSync.Reset();
            _autoPicked = false;
            _autoPickAt = Time.unscaledTime + 2f;
            SetState(SessionState.Loading);
            RunStarter.LaunchRun(seed);
        }

        private void OnLevelGenerated(Level level)
        {
            if (!Active || State != SessionState.Loading) return;
            Sync.WorldSync.CaptureBaseline(level);
            ulong checksum = RunStarter.ChecksumLevel(level);
            var audit = DeterminismAudit.Capture();
            var ready = new LevelReadyMsg
            {
                Checksum = checksum,
                EntityCount = audit.EntityCount,
                EntityDigest = audit.EntityDigest,
                PlantCount = audit.PlantCount,
                PlantDigest = audit.PlantDigest,
            };
            Plugin.Log.LogInfo($"[Run] level generated, checksum {checksum:X16}, " +
                $"entities {audit.EntityCount}/{audit.EntityDigest:X16}, plants {audit.PlantCount}/{audit.PlantDigest:X16}");
            // Capture entity identity NOW, before early movers drift. Tile renderers fill their
            // private variant tables in another LevelGenerated subscriber, so finalize the
            // readiness packet on a later Update after every subscriber has completed.
            NetIds.PrepareLocal();
            _localLevelChecksum = checksum;
            _localLevelReady = ready;
            _levelReadyVisualPending = true;
            _levelReadyVisualStartedAt = Time.unscaledTime;
        }

        private void TryFinalizeLevelReadyVisual()
        {
            if (!_levelReadyVisualPending || State != SessionState.Loading) return;
            var visual = DeterminismAudit.CaptureVisual(log: false);
            if (visual.VariantCount == 0)
            {
                if (Time.unscaledTime - _levelReadyVisualStartedAt < 3f) return;
                Plugin.Log.LogError("[Determinism] visual tile variant table was not available; refusing an unverifiable run");
                StopSession("visual determinism audit unavailable");
                return;
            }

            _levelReadyVisualPending = false;
            _localLevelReady.VisualVariantCount = visual.VariantCount;
            _localLevelReady.VisualVariantDigest = visual.Digest;
            Plugin.Log.LogInfo($"[Determinism] visuals={visual.VariantCount}/{visual.Digest:X16} " +
                $"renderers={visual.RendererCount}");
            if (IsHost)
            {
                _levelChecksums[HostSlot] = _localLevelChecksum;
                _levelFingerprints[HostSlot] = _localLevelReady;
                CheckGoLive();
            }
            else if (_players[HostSlot] != null)
            {
                _hasLocalLevelChecksum = true;
                _goLiveDeadline = Time.unscaledTime + GoLiveTimeout;
                SendLevelReady();
            }
        }

        // Field reports (tester, 2026-07-20): a rejoiner can end up on a permanent black screen —
        // level generated, LEVEL_READY sent, but go-live never completes (unreproducible on loopback;
        // suspected Steam-transport catch-up wedge on hour-old worlds). A player stuck there can't
        // even quit. Convert "black screen forever" into a clean failure back to the menu.
        private const float GoLiveTimeout = 120f; // generous: initial go-live also waits on SLOW peers
        private float _goLiveDeadline;

        private void SendLevelReady()
        {
            if (IsHost || !_hasLocalLevelChecksum || _players[HostSlot] == null) return;
            _writer.Reset();
            _localLevelReady.Write(_writer);
            SendReliable(_players[HostSlot].PeerId, NetChannel.Control, _writer.ToSegment());
            _nextLevelReadyRetryAt = Time.unscaledTime + 1f;
        }

        private void HandleLevelReady(ulong peer)
        {
            var msg = LevelReadyMsg.Read(_reader);
            var player = _players.FirstOrDefault(p => p != null && p.PeerId == peer);
            if (player == null) return;

            if (State == SessionState.InGame)
            {
                if (_peersAwaitingRejoinState.Remove(peer))
                {
                    if (_levelFingerprints.TryGetValue(0, out var hostReady) && !SameGeneration(hostReady, msg))
                    {
                        if (!SameVisualGeneration(hostReady, msg))
                            InstrumentationCounters.VisualGenerationMismatch();
                        Plugin.Log.LogError($"[Determinism] rejecting rejoin P{player.Slot + 1}: " +
                            $"host={DescribeGeneration(hostReady)}, peer={DescribeGeneration(msg)}");
                        _writer.Reset();
                        new RejectMsg { Reason = "World generation diverged from the host; refusing an unsafe rejoin." }.Write(_writer);
                        SendReliable(peer, NetChannel.Control, _writer.ToSegment());
                        return;
                    }
                    Plugin.Log.LogInfo($"[Run] LEVEL_READY from rejoining P{player.Slot + 1} — sending one full catch-up");
                    SendRejoinState(peer);
                }
                else
                {
                    // The client retries LEVEL_READY until GO_LIVE arrives.  On an initial start,
                    // a retry can already be queued when the host crosses the barrier.  It is only
                    // recovery for a lost GO_LIVE, not evidence of a rejoin.
                    SendGoLiveRecovery(peer, player.Slot);
                }
                return;
            }

            _levelChecksums[player.Slot] = msg.Checksum;
            _levelFingerprints[player.Slot] = msg;
            CheckGoLive();
        }

        private void SendGoLiveRecovery(ulong peer, byte slot)
        {
            float now = Time.unscaledTime;
            if (_nextGoLiveRecoveryAt.TryGetValue(peer, out var next) && now < next) return;
            _nextGoLiveRecoveryAt[peer] = now + 1f;

            _writer.Reset();
            _writer.WriteMsgType(MsgType.GoLive);
            SendReliable(peer, NetChannel.Control, _writer.ToSegment());
            Plugin.Log.LogInfo($"[Run] repeated LEVEL_READY from active P{slot + 1} — GO_LIVE-only recovery sent");
        }

        private void CheckGoLive()
        {
            if (!IsHost || State != SessionState.Loading) return;
            var present = _players.Where(p => p != null && p.Connected).ToList();
            if (present.Any(p => !_levelChecksums.ContainsKey(p.Slot))) return;

            if (present.Any(p => !_levelFingerprints.ContainsKey(p.Slot))) return;
            var host = _levelFingerprints[HostSlot];
            if (_levelFingerprints.Values.Any(value => !SameGeneration(host, value)))
            {
                if (_levelFingerprints.Values.Any(value => !SameVisualGeneration(host, value)))
                    InstrumentationCounters.VisualGenerationMismatch();
                var detail = string.Join(", ", _levelFingerprints.Select(kv => $"P{kv.Key + 1}={DescribeGeneration(kv.Value)}"));
                Plugin.Log.LogError($"[Determinism] GENERATION MISMATCH — aborting net run ({detail})");
                _writer.Reset();
                new RejectMsg { Reason = "World generation diverged between players (terrain/entity/plant/visual fingerprint mismatch)." }.Write(_writer);
                ForEachRemotePeer(peer => SendReliable(peer, NetChannel.Control, _writer.ToSegment()));
                StopSession("generation fingerprint mismatch");
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
                ForEachRemotePeer(peer => SendReliable(peer, NetChannel.Control, _writer.ToSegment()));
                if (fps.Count == 0) break;
            }

            SendEntityBaseline(0);

            _writer.Reset();
            _writer.WriteMsgType(MsgType.GoLive);
            ForEachRemotePeer(peer => SendReliable(peer, NetChannel.Control, _writer.ToSegment()));
            DoGoLive();
        }

        private static bool SameGeneration(LevelReadyMsg a, LevelReadyMsg b) =>
            a.Checksum == b.Checksum && a.EntityCount == b.EntityCount && a.EntityDigest == b.EntityDigest &&
            a.PlantCount == b.PlantCount && a.PlantDigest == b.PlantDigest &&
            a.VisualVariantCount == b.VisualVariantCount && a.VisualVariantDigest == b.VisualVariantDigest;

        private static bool SameVisualGeneration(LevelReadyMsg a, LevelReadyMsg b) =>
            a.VisualVariantCount == b.VisualVariantCount && a.VisualVariantDigest == b.VisualVariantDigest;

        private static string DescribeGeneration(LevelReadyMsg value) =>
            $"terrain={value.Checksum:X16} entities={value.EntityCount}/{value.EntityDigest:X16} " +
            $"plants={value.PlantCount}/{value.PlantDigest:X16} " +
            $"visuals={value.VisualVariantCount}/{value.VisualVariantDigest:X16}";

        private float _autoFlyUntil;

        private void DoGoLive()
        {
            Plugin.Log.LogInfo("[Run] GO LIVE — all players in, starting gameplay");
            DiagWatch.NotifyRunStarted(); // skip warmup in the growth watchdog
            SetState(SessionState.InGame);
            // Same value on every machine (seed + host identity are already shared): the run id
            // groups all players' `uploadlogs` under one S3 folder and names bug reports.
            LogUpload.SetRun(CurrentRunSeed, _players[HostSlot]?.IdentityId ?? LocalIdentityId());
            Sync.ShipSync.ReleaseStartGate();
            if (_isRejoin)
            {
                _isRejoin = false;
                Plugin.Log.LogInfo($"[Stash] rejoin go-live for run {CurrentRunSeed} — attempting economy restore");
                EconomyStash.TryRestore(CurrentRunSeed);
                // Spawn at the party's latest unlocked station instead of the run start.
                if (_spawnStationNetId != 0) Sync.ShipSync.TeleportLocalShip(_spawnStationNetId);
            }
            // Remember this session as the rejoin target: if this machine disconnects, crashes,
            // or quits mid-run, the CONNECT screen can offer REJOIN while the session lives.
            try
            {
                // A SteamServer discovery lobby is remembered like a Steam lobby: rejoin re-enters the
                // lobby, reads the server id from its metadata, and reconnects over SteamServer. The
                // old `!UsingSteam` fallback would have stored the server id as a LOOPBACK address —
                // unjoinable after a restart (config transport is Steam).
                if (_lobby != null && _lobby.InLobby && (UsingSteam || _lobby.IsServerLobby))
                    RejoinMemory.Remember(steam: true, _lobby.CurrentLobby.m_SteamID, null);
                else if (!UsingSteam)
                    RejoinMemory.Remember(steam: false, 0, IsHost
                        ? $"{NetConfig.LoopbackHost.Value}:{NetConfig.LoopbackPort.Value}"
                        : _lastJoinAddress);
            }
            catch { }
            // Scripted flight is a test-harness aid — only auto-arm it for auto-launched runs.
            // Otherwise a leftover AutoFlySeconds in a dev's config hijacks a real hosted session:
            // the ship thrusts up-right on its own for the first several seconds on entry. The
            // on-demand harness path (RearmAutoFly via the 'autofly' devcmd / command file) is
            // unaffected, and scripted tests set AutoLaunchRun=true so they still get it.
            if (NetConfig.AutoFly.Value > 0f && NetConfig.AutoLaunchRun.Value)
                _autoFlyUntil = Time.unscaledTime + 3f + NetConfig.AutoFly.Value;
        }

        /// <summary>DEV: re-arm the scripted flight mid-run (DevTools 'autofly' command).</summary>
        internal void RearmAutoFly(float seconds)
            => _autoFlyUntil = seconds > 0f ? Time.unscaledTime + seconds : 0f;

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
            if (reliable) ForEachRemotePeer(peer => SendReliable(peer, channel, data));
            else ForEachRemotePeer(peer => _transport.Send(peer, channel, data, reliable: false));
        }

        /// <summary>Host: relay the message currently being handled to every client except the sender.</summary>
        private void RelayToOthers(ulong senderPeer, NetChannel channel, bool reliable)
        {
            if (!IsHost) return;
            foreach (var p in _players)
            {
                if (p == null || p.IsLocal || p.PeerId == senderPeer) continue;
                if (reliable) SendReliable(p.PeerId, channel, _lastPayload);
                else _transport.Send(p.PeerId, channel, _lastPayload, reliable: false);
            }
        }

        /// <summary>Host relay with one favored recipient: unreliable on <paramref name="channel"/> to
        /// everyone except the sender, but reliable+no-nagle on <paramref name="reliableChannel"/> to
        /// <paramref name="reliableToSlot"/>. Used for entity fire aimed at a specific victim — the one
        /// player the shot can damage always sees it, without re-inflating the broadcast for the rest.</summary>
        private void RelayToOthers(ulong senderPeer, NetChannel channel, byte reliableToSlot, NetChannel reliableChannel)
        {
            if (!IsHost) return;
            foreach (var p in _players)
            {
                if (p == null || p.IsLocal || p.PeerId == senderPeer) continue;
                if (p.Slot == reliableToSlot) SendReliable(p.PeerId, reliableChannel, _lastPayload);
                else _transport.Send(p.PeerId, channel, _lastPayload, reliable: false);
            }
        }

        /// <summary>Broadcast an entity-fire payload with victim-favored reliability: reliable+no-nagle
        /// to the aimed slot, unreliable to everyone else. A client simulator sends one unreliable copy
        /// to the host, which completes the victim guarantee on relay. Replay dedups on (netId, shotId),
        /// so any overlap between the reliable and unreliable copies is harmless.</summary>
        public void SendEntityFire(ArraySegment<byte> data, byte targetSlot)
        {
            if (_transport == null || !_transport.IsRunning) return;
            if (!IsHost)
            {
                if (_players[HostSlot] != null)
                    _transport.Send(_players[HostSlot].PeerId, NetChannel.State, data, reliable: false);
                return;
            }
            foreach (var p in _players)
            {
                if (p == null || p.IsLocal || !p.Connected) continue;
                if (p.Slot == targetSlot) SendReliable(p.PeerId, NetChannel.Combat, data);
                else _transport.Send(p.PeerId, NetChannel.State, data, reliable: false);
            }
        }

        // ------------------------------------------------ reliable outbox
        //
        // "Reliable" at the transport only means reliable ONCE ACCEPTED — a full send buffer
        // refuses the message and it would be gone forever (a lost kill or cell diff is a
        // permanent desync). Refused sends queue here and retry every frame; once a
        // (peer, channel) lane has a backlog, later sends queue behind it to keep ordering.

        private readonly Dictionary<(ulong peer, NetChannel channel), Queue<byte[]>> _outbox
            = new Dictionary<(ulong, NetChannel), Queue<byte[]>>();
        private readonly List<(ulong peer, NetChannel channel)> _outboxScratch
            = new List<(ulong, NetChannel)>();

        /// <summary>Total queued reliable messages across all peers/channels. Should sit near zero in
        /// steady state — sustained growth means sends aren't draining (backpressure / leak).</summary>
        internal int OutboxDepth
        {
            get { int n = 0; foreach (var q in _outbox.Values) n += q.Count; return n; }
        }

        /// <summary>Reliable send that can never silently drop; may deliver next frame(s).</summary>
        public void SendReliable(ulong peer, NetChannel channel, ArraySegment<byte> data)
        {
            var key = (peer, channel);
            if (_outbox.TryGetValue(key, out var backlog) && backlog.Count > 0)
            {
                EnqueueOutbox(key, backlog, data);
                return;
            }
            if (!_transport.Send(peer, channel, data, reliable: true))
                EnqueueOutbox(key, backlog, data);
        }

        private void EnqueueOutbox((ulong, NetChannel) key, Queue<byte[]> backlog, ArraySegment<byte> data)
        {
            if (backlog == null) _outbox[key] = backlog = new Queue<byte[]>();
            if (backlog.Count >= 8192)
            {
                // Minutes of refusal — the connection is effectively dead; the peer-timeout
                // path will clean up. Dropping the oldest keeps memory bounded.
                backlog.Dequeue();
                Plugin.Log.LogWarning($"[Session] reliable outbox overflow for {key.Item1} ch{(int)key.Item2}");
            }
            var copy = new byte[data.Count];
            Buffer.BlockCopy(data.Array, data.Offset, copy, 0, data.Count);
            backlog.Enqueue(copy);
        }

        private void DrainOutbox()
        {
            if (_outbox.Count == 0) return;
            _outboxScratch.Clear();
            _outboxScratch.AddRange(_outbox.Keys);
            foreach (var key in _outboxScratch)
            {
                var backlog = _outbox[key];
                while (backlog.Count > 0)
                {
                    var payload = backlog.Peek();
                    if (!_transport.Send(key.peer, key.channel, new ArraySegment<byte>(payload), reliable: true))
                        break; // still congested — retry next frame
                    backlog.Dequeue();
                }
                if (backlog.Count == 0) _outbox.Remove(key);
            }
        }

        private void ClearOutboxFor(ulong peer)
        {
            _outboxScratch.Clear();
            foreach (var key in _outbox.Keys)
                if (key.peer == peer) _outboxScratch.Add(key);
            foreach (var key in _outboxScratch) _outbox.Remove(key);
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
            // A coordinator seats itself in the reserved slot (4), leaving all four player slots
            // (0-3) for real players. A normal host is player slot 0 as always.
            LocalSlot = NetConfig.IsCoordinator ? CoordinatorSlot : 0;
            _players[LocalSlot] = new NetPlayer
            {
                Slot = (byte)LocalSlot,
                PeerId = _transport.LocalPeerId,
                IdentityId = LocalIdentityId(),
                Name = LocalDisplayName(),
                IsLocal = true,
                IsCoordinator = NetConfig.IsCoordinator, // shipless server slot — clients spawn no puppet
            };
            HostSlot = (byte)LocalSlot;
            ChosenSeed = _pendingHostSeed; // settings picked on the pre-lobby screen
            FriendlyFire = _pendingFriendlyFire;
            HpScaling = _pendingHpScaling;
            _pendingHostSeed = 0;
            _pendingFriendlyFire = false;
            _pendingHpScaling = false;
            SetState(SessionState.Lobby);
            RosterChanged?.Invoke();
            Plugin.Log.LogInfo($"[Session] hosting as {_players[LocalSlot]}");
        }

        private string _lastJoinAddress; // loopback rejoin target (Steam rejoins use the lobby id)

        public void JoinSession(string address)
        {
            if (State != SessionState.Offline) StopSession("rejoining");
            _lastJoinAddress = address;
            HostSlot = 0;
            _joinTargetHostSlot = 0;
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
            _sidecarSession = false; // future sessions choose their transport from config again
            _haveLeaderSettings = false; _leaderSettingsSent = false;
            _lobbyServerTransport = null; _directTransport = null; _serverLobbyRequested = false; _allowlistDirty = false;
            _allowedPeers = null;
            _adminSlot = -1; _adminToken = 0; _localAdminToken = 0;

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
                // Final economy stash (the periodic ones may be seconds stale) and world
                // cleanup: the run continues solo, so it must be coherent — teammate
                // puppets despawn and remote-simulated enemies get their AI back.
                try { EconomyStash.Save(CurrentRunSeed); } catch { }
                CleanupAbandonedRun();
            }

            _lobby?.LeaveLobby();
            _outbox.Clear();
            if (_transport != null)
            {
                _transport.Stop();
                _transport.Dispose();
                _transport = null;
            }
            for (int i = 0; i <= MaxPlayers; i++) _players[i] = null;
            LocalSlot = -1;
            HostSlot = 0;
            _migrating = false;
            _reattaching = false;
            _joinTargetPeerId = 0;
            _joinTargetHostSlot = 0;
            ChosenSeed = 0;
            FriendlyFire = false;
            HpScaling = false;
            EnemyHpMult = 1f;
            _levelChecksums.Clear();
            _levelFingerprints.Clear();
            _peersAwaitingRejoinState.Clear();
            _nextGoLiveRecoveryAt.Clear();
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
            Sync.MapShareSync.Reset();
            DevTools.Reset();
            AuthorityManager.Reset();
            NetDiag.Reset();
            NetIds.Reset();
            NetStats.Reset();
            NetSeq.Reset();
            _nextSeqCheckpointAt = 0;
            for (int i = 0; i < _nextGapReportAt.Length; i++) _nextGapReportAt[i] = 0;
            _nextGapCatchUpAt.Clear();
            ResetSeqBaselines();
            NetProfiler.Reset();
            RuntimeInstrumentation.ResetRun();
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
            switch (ResolvedTransport.ToLowerInvariant())
            {
                case "loopback":
                    return new LoopbackUdpTransport(NetConfig.LoopbackHost.Value, NetConfig.LoopbackPort.Value);
                case "steamserver":
                    return new SteamServerTransport();
                case "udp":
                    // LiteNetLib direct UDP — Docker/LAN/no-Steam. Join code is host:port.
                    return new LiteNetTransport(NetConfig.UdpAddress.Value, NetConfig.UdpPort.Value);
                default:
                    return new SteamMessagesTransport();
            }
        }

        private void WireTransport()
        {
            _transport.PeerConnected += OnPeerConnected;
            _transport.PeerDisconnected += OnPeerDisconnected;
            _transport.DataReceived += OnData;
            if (_transport is SteamMessagesTransport steam)
                steam.AllowPeer = id => _lobby != null && _lobby.IsMember(id);
            else if (_transport is SteamServerTransport ss && !NetConfig.IsCoordinator)
                // A listen-server host owns its discovery lobby in-process, so it can gate at accept —
                // race-free, since a friend is a lobby member before they connect. A shipless
                // coordinator has no lobby; it stays accept-all here and gates HELLOs against the
                // relayed allowlist instead (see the LobbyMembers handler).
                ss.AllowPeer = id => _lobby != null && _lobby.InLobby && _lobby.IsMember(id);
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

        private static string LocalDisplayName() => NetConfig.IsCoordinator ? "SERVER" : LocalName();

        private ulong LocalIdentityId()
        {
            // Steam's transport address is already a durable account identity. Loopback peer IDs
            // are reassigned by every new host, so use a stable hash of this game installation;
            // the host and OD test copy have distinct paths and retain their slots after restart.
            if (UsingSteam && _transport != null && _transport.LocalPeerId != 0)
                return _transport.LocalPeerId;
            return _localIdentityId;
        }

        private static ulong ComputeLoopbackIdentity()
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong hash = offset;
            string key = (Application.dataPath ?? Environment.CurrentDirectory).ToLowerInvariant();
            foreach (char c in key)
            {
                hash ^= (byte)c;
                hash *= prime;
                hash ^= (byte)(c >> 8);
                hash *= prime;
            }
            // Keep 0 reserved for old/fallback peers and avoid the loopback route IDs 1..4.
            hash |= 0x8000000000000000UL;
            return hash <= MaxPlayers ? hash + 0x100UL : hash;
        }

        // ---------------------------------------------------------------- update loop

        private void Update()
        {
            RuntimeInstrumentation.UpdateStart(State);
            bool profiling = false;
            try
            {
                RuntimeInstrumentation.SetPhase(PerfPhase.SteamPump);
                SteamBootstrap.Pump();
                // Game-server callbacks (logon, incoming connections, backend drop) pump here too —
                // BEFORE the IsRunning gate, so a coordinator's server connection-status callbacks
                // fire even between logon and the first transport Poll.
                if (GameServerBootstrap.InitOk) GameServerBootstrap.Pump();
                // No transport (main menu, offline): still poll the dev command file so the
                // harness can drive/screenshot menu UI. In-session ticking stays below, after
                // Poll/Drain, so scenario commands keep acting on post-dispatch state.
                if (_transport == null || !_transport.IsRunning) { DevTools.Tick(this); return; }
                profiling = State == SessionState.InGame;
                if (profiling) NetProfiler.FrameStart();

                RuntimeInstrumentation.SetPhase(PerfPhase.TransportPoll);
                _transport.Poll();                          // processes all inbound msgs (Dispatch)
                if (profiling) NetProfiler.Mark("Transport.Poll(recv)");
                RuntimeInstrumentation.SetPhase(PerfPhase.TransportDrain);
                DrainOutbox();
                if (profiling) NetProfiler.Mark("Transport.Drain");
                // Lease commits applied during the dispatch above (client lease waves, host
                // handoff completions) flip their entities in one batched pass, same frame.
                Sync.EnemySync.FlushSegmentOwnership();
                DevTools.Tick(this);

                // LevelGenerated subscribers finish in engine-defined order. Finalizing here
                // guarantees UnityTilemapRenderer has populated the sprite-variant table.
                TryFinalizeLevelReadyVisual();

                if (State == SessionState.Connecting && Time.unscaledTime >= _connectDeadline)
                {
                    Fail("Could not reach host — no response for 15 seconds.");
                    return;
                }
                if (_reattaching && Time.unscaledTime >= _reattachDeadline)
                {
                    _reattaching = false;
                    Fail("Could not reach the host to resume the run.");
                    return;
                }

                if (State == SessionState.InGame)
                {
                    RuntimeInstrumentation.SetPhase(PerfPhase.AutoFly);
                    TickAutoFly();                              NetProfiler.Mark("AutoFly");
                    RuntimeInstrumentation.SetPhase(PerfPhase.ShipSync);
                    Sync.ShipSync.Tick(this);                   NetProfiler.Mark("ShipSync");
                    Sync.ProjectileSync.Tick();
                    RuntimeInstrumentation.SetPhase(PerfPhase.WorldFlush);
                    Sync.WorldSync.Flush(this);                 NetProfiler.Mark("WorldSync.Flush");
                    RuntimeInstrumentation.SetPhase(PerfPhase.WorldTick);
                    Sync.WorldSync.Tick(this);                  NetProfiler.Mark("WorldSync.Tick");
                    RuntimeInstrumentation.SetPhase(PerfPhase.EnemySync);
                    Sync.EnemySync.Tick(this);                  NetProfiler.Mark("EnemySync");
                    RuntimeInstrumentation.SetPhase(PerfPhase.DamageWatchdog);
                    Sync.DamageSync.TickLifeWatchdog();         NetProfiler.Mark("DamageWatchdog");
                    RuntimeInstrumentation.SetPhase(PerfPhase.ModuleGrid);
                    Sync.ModuleGridSync.Tick(this);             NetProfiler.Mark("ModuleGrid");
                    RuntimeInstrumentation.SetPhase(PerfPhase.Fog);
                    Sync.FogSync.Tick(this);                    NetProfiler.Mark("Fog");
                    RuntimeInstrumentation.SetPhase(PerfPhase.Economy);
                    EconomyStash.Tick(this);                    NetProfiler.Mark("Economy");
                    RuntimeInstrumentation.SetPhase(PerfPhase.Diagnostics);
                    NetDiag.TickPeriodic();                     NetProfiler.Mark("Diag");
                    if (IsHost)
                    {
                        RuntimeInstrumentation.SetPhase(PerfPhase.Authority);
                        AuthorityManager.Tick(this);            NetProfiler.Mark("Authority");
                        RuntimeInstrumentation.SetPhase(PerfPhase.PartyWipe);
                        CheckPartyWipe();                       NetProfiler.Mark("PartyWipe");
                    }
                }

                // DEV autostart: clickless loadout pick while loading.
                if (State == SessionState.Loading && NetConfig.AutoReady.Value && !_autoPicked
                    && Time.unscaledTime >= _autoPickAt)
                {
                    if (RunStarter.TryAutoPickLoadout()) _autoPicked = true;
                    else _autoPickAt = Time.unscaledTime + 1f;
                }

                // LEVEL_READY is also the client's run-barrier retry. If the final GO_LIVE was
                // lost (notably on the dev UDP transport), the now-InGame host answers with one
                // tiny GO_LIVE recovery instead of rebuilding the world catch-up.
                if (State == SessionState.Loading && !IsHost && _hasLocalLevelChecksum
                    && Time.unscaledTime >= _nextLevelReadyRetryAt)
                    SendLevelReady();

                // Black-screen watchdog: level is generated and LEVEL_READY retries at 1 Hz, but
                // go-live never completed. Tear down cleanly to the menu instead of leaving the
                // player on an unresponsive black screen (they can rejoin — the slot is reserved).
                if (State == SessionState.Loading && !IsHost && _hasLocalLevelChecksum
                    && _goLiveDeadline > 0f && Time.unscaledTime >= _goLiveDeadline)
                {
                    _goLiveDeadline = 0f;
                    Fail($"Timed out waiting for the host's go-live ({(int)GoLiveTimeout}s) — " +
                         "left the loading screen. Use REJOIN to try again.");
                    return;
                }

                // Sidecar parity (#1): once in the coordinator's lobby, forward the world settings
                // the host player picked so the coordinator hosts THEIR world. Send before readying
                // (below) so it lands before the coordinator can auto-launch.
                if (_haveLeaderSettings && !_leaderSettingsSent && !IsHost
                    && State == SessionState.Lobby && _players[HostSlot] != null)
                {
                    _leaderSettingsSent = true;
                    _writer.Reset();
                    new PartyLeaderSettingsMsg
                    {
                        Seed = _leaderSeed, FriendlyFire = _leaderFriendlyFire, HpScaling = _leaderHpScaling,
                    }.Write(_writer);
                    SendReliable(_players[HostSlot].PeerId, NetChannel.Control, _writer.ToSegment());
                    Plugin.Log.LogInfo($"[Sidecar] sent world choice to coordinator (seed={_leaderSeed}, ff={_leaderFriendlyFire}, hp={_leaderHpScaling})");
                }

                // Sidecar parity (#2/#3): the invite owner opens/refreshes the SteamServer discovery
                // lobby, and (coordinator sessions) relays its membership so joins are lobby-gated.
                MaintainServerLobby();

                // DEV autostart: auto-ready in lobby, host auto-launches when everyone is ready.
                // Coordinator: keep a session admin designated (the first real joiner gets host-like
                // controls; a handoff picks the next player). The admin, not the headless server,
                // presses START.
                if (NetConfig.IsCoordinator && State == SessionState.Lobby)
                    EnsureAdminAssigned();

                // A coordinator auto-readies its own (shipless) slot, but no longer auto-LAUNCHES — the
                // admin drives start. The dev/harness escape hatch is an explicit AutoLaunchRun=true,
                // which still fires below (the coordinator inherits the host install's config).
                bool autoReady = NetConfig.AutoReady.Value || NetConfig.IsCoordinator;
                bool autoLaunch = NetConfig.AutoLaunchRun.Value;
                if (State == SessionState.Lobby && autoReady && LocalPlayer != null && !LocalPlayer.Ready)
                    SetLocalPrefs(LocalPlayer.ColorIndex != 0 ? LocalPlayer.ColorIndex : (byte)LocalSlot, true);
                if (State == SessionState.Lobby && autoLaunch && IsHost && AllReady
                    && (!NetConfig.IsCoordinator
                        || _players.Any(p => p != null && p.Connected && !p.IsLocal)))
                    StartRun();

                if (State >= SessionState.Lobby && Time.unscaledTime >= _nextPingAt)
                {
                    _nextPingAt = Time.unscaledTime + PingInterval;
                    var ping = new PingMsg { TimeMs = (uint)(Time.unscaledTime * 1000f) };
                    _writer.Reset();
                    ping.Write(_writer, pong: false);
                    ForEachRemotePeer(peer => _transport.Send(peer, NetChannel.State, _writer.ToSegment(), reliable: false));
                }

                // WS8.2: the host stamps each correctness channel with a periodic sequence
                // checkpoint. Ordered delivery makes it a barrier — a client holding fewer
                // messages at its arrival lost something silently and requests catch-up.
                if (IsHost && State >= SessionState.Loading && Time.unscaledTime >= _nextSeqCheckpointAt)
                {
                    _nextSeqCheckpointAt = Time.unscaledTime + 2f;
                    foreach (var p in _players)
                    {
                        if (p == null || p.IsLocal || !p.Connected || p.PeerId == 0) continue;
                        foreach (var ch in new[] { NetChannel.Events, NetChannel.Combat })
                        {
                            _writer.Reset();
                            new EventSeqCheckpointMsg { Channel = (byte)ch, Count = NetSeq.SentTo(p.PeerId, ch) }
                                .Write(_writer);
                            SendReliable(p.PeerId, ch, _writer.ToSegment());
                        }
                    }
                }
            }
            finally
            {
                if (profiling) NetProfiler.FrameEnd();
                RuntimeInstrumentation.UpdateEnd(State);
            }
        }

        private void ResetSeqBaselines()
        {
            for (int i = 0; i < 4; i++)
            {
                _seqBaselined[i] = false;
                _seqBaselineSent[i] = 0;
                _seqBaselineReceived[i] = 0;
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

        internal bool TryGetPeerId(byte slot, out ulong peerId)
        {
            peerId = 0;
            if (slot >= MaxPlayers) return false;
            var player = _players[slot];
            if (player == null || !player.Connected || player.IsLocal || player.PeerId == 0) return false;
            peerId = player.PeerId;
            return true;
        }

        internal void SendReliableToSlot(byte slot, NetChannel channel, ArraySegment<byte> data)
        {
            if (slot == LocalSlot) return;
            if (TryGetPeerId(slot, out ulong peer)) SendReliable(peer, channel, data);
        }

        internal bool SendUnreliableDirect(byte slot, NetChannel channel, ArraySegment<byte> data)
        {
            return TryGetPeerId(slot, out ulong peer)
                   && _transport != null && _transport.IsRunning
                   && _transport.Send(peer, channel, data, reliable: false);
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
            _levelFingerprints.Clear();
            _peersAwaitingRejoinState.Clear();
            _nextGoLiveRecoveryAt.Clear();
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
            Sync.MapShareSync.Reset();
            DevTools.Reset();
            AuthorityManager.Reset();
            NetDiag.Reset();
            NetIds.Reset();
            NetProfiler.Reset();
            RuntimeInstrumentation.ResetRun();
            EconomyStash.Reset();
            Sync.HookSync.Reset();
            foreach (var p in _players)
                if (p != null)
                    p.Ready = false;
            // Mid-run slot reservations end with the run: a ghost left in the roster collides
            // with its owner coming back through the lobby-join path (same identity seated in
            // two slots crashed the client-side LobbyState apply). They can rejoin normally.
            if (IsHost)
            {
                bool dropped = false;
                for (int i = 0; i < MaxPlayers; i++)
                {
                    var p = _players[i];
                    if (p == null || p.IsLocal || p.Connected) continue;
                    Plugin.Log.LogInfo($"[Session] run over — releasing reserved slot of {p}");
                    _players[i] = null;
                    dropped = true;
                }
                if (dropped) BroadcastLobbyState();
            }
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
            NetSeq.ResetPeer(peer); // peer ids can be reused (loopback) — stale counts would misfire
            ResetSeqBaselines();    // growth comparison restarts at the next checkpoint
            if (!IsHost && (State == SessionState.Connecting || _reattaching))
            {
                // Connected to the host: introduce ourselves.
                var hello = new HelloMsg
                {
                    ProtocolVersion = ProtocolVersion,
                    ModVersion = Plugin.Version,
                    GameVersion = Application.version,
                    SteamId = LocalIdentityId(),
                    Name = LocalDisplayName(),
                    Resuming = _reattaching,
                    Mods = ModManifest.Local,
                };
                _writer.Reset();
                hello.Write(_writer);
                SendReliable(peer, NetChannel.Control, _writer.ToSegment());
                Plugin.Log.LogInfo(_reattaching ? "[Session] sent HELLO (resume)" : "[Session] sent HELLO");
            }
            // Host side: wait for the HELLO before creating a player.
        }

        // ---------------------------------------------------------------- host migration

        /// <summary>The host is gone. Steam follows lobby ownership; loopback deterministically
        /// elects the lowest connected slot and reconnects through the fixed dev port.</summary>
        private void OnHostLost(string reason, bool hostQuit = false)
        {
            // SessionEnded and the transport disconnect commonly arrive back-to-back. The second
            // signal used to call Fail() and tear down a migration that was already in progress.
            if (_migrating || _reattaching)
            {
                Plugin.Log.LogInfo($"[Session] duplicate host-loss signal ignored during migration/reattach ({reason})");
                return;
            }
            if (State == SessionState.InGame && !UsingSteam)
            {
                // A loopback timeout is almost always a stalled host (level load, GC, debugger),
                // not a dead one — and self-promotion while the real host lives can't bind the
                // shared dev port, so a false migration always killed the session. Only migrate
                // when the host provably departed (SessionEnded or an explicit DISCONNECT frame,
                // both of which mean its socket closed and the port is free); otherwise hold the
                // session and reconnect in place.
                bool hostReallyGone = hostQuit
                    || (_transport is LoopbackUdpTransport lb && lb.LastDisconnectWasRemote)
                    || (_transport is LiteNetTransport ln && ln.LastDisconnectWasRemote);
                if (!hostReallyGone)
                {
                    BeginLoopbackReconnect(reason);
                    return;
                }
                if (_transport is LiteNetTransport)
                {
                    // Udp can't migrate: the join address names the departed host's machine, so
                    // no elected peer is reachable by the rest of the roster. (The dedicated-
                    // server deployment never hits this — the server IS the host.)
                    Fail("Server closed the session.");
                    return;
                }
                _migrating = true;
                StartCoroutine(MigrateLoopbackHost(reason));
                return;
            }
            if (State == SessionState.InGame && UsingSteam && _lobby != null && _lobby.InLobby)
            {
                _migrating = true;
                StartCoroutine(MigrateHost(reason));
                return;
            }
            Fail(reason);
        }

        /// <summary>Timeout-driven host loss on loopback: keep the run alive and reconnect to
        /// the same fixed address. The transport already retries CONNECT on its own once its
        /// host route drops; arming _reattaching makes the reconnect send a resume-HELLO, and
        /// HandleWelcome completes the reattach with the run still live. No sync state is torn
        /// down — the host's world resumes from snapshots the moment it answers. If it never
        /// answers, the reattach deadline fails the session cleanly instead of a doomed
        /// self-promotion onto a port the stalled host still holds.</summary>
        private void BeginLoopbackReconnect(string reason)
        {
            Plugin.Log.LogInfo($"[Session] {reason} — treating as a host stall; reconnecting in place (loopback never migrates on timeout)");
            UI.Toast.Show("CONNECTION TO HOST LOST — RECONNECTING…", 6f);
            _reattaching = true;
            _reattachDeadline = Time.unscaledTime + 30f;
            _joinTargetHostSlot = HostSlot;
            _joinTargetPeerId = 0;
        }

        private System.Collections.IEnumerator MigrateHost(string reason)
        {
            Plugin.Log.LogInfo($"[Session] {reason} — electing a new host…");
            var oldHost = _players[HostSlot];
            ulong oldHostId = oldHost?.PeerId ?? 0;
            if (oldHost != null)
            {
                oldHost.Connected = false;
                oldHost.NeedsStationRespawn = true;
                oldHost.RespawnStationNetId = 0;
                oldHost.RttMs = -1;
                oldHost.PeerId = 0;
                Sync.ShipSync.RemoveRemoteShip(oldHost.Slot, "host departed");
                AuthorityManager.OnPeerLost(oldHost.Slot);
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
                if (owner != 0 && owner != oldHostId
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

            var electedPlayer = _players.FirstOrDefault(p => p != null && p.PeerId == newHostId);
            if (electedPlayer != null)
                Sync.EnemySync.ReassignFixedOwners(oldHost?.Slot ?? (byte)0, electedPlayer.Slot);
            if (newHostId == _transport.LocalPeerId) BecomeHost();
            else ReattachTo(newHostId);
            _migrating = false;
        }

        private System.Collections.IEnumerator MigrateLoopbackHost(string reason)
        {
            byte oldHostSlot = HostSlot;
            Plugin.Log.LogInfo($"[Session] {reason} — loopback host election starting (old=P{oldHostSlot + 1})");
            var oldHost = _players[oldHostSlot];
            if (oldHost != null)
            {
                oldHost.Connected = false;
                oldHost.NeedsStationRespawn = true;
                oldHost.RespawnStationNetId = 0;
                oldHost.RttMs = -1;
                oldHost.PeerId = 0;
                Sync.ShipSync.RemoveRemoteShip(oldHostSlot, "host departed");
                AuthorityManager.OnPeerLost(oldHostSlot);
            }
            RosterChanged?.Invoke();

            var elected = _players.Where(p => p != null && p.Connected && p.Slot != oldHostSlot)
                                  .OrderBy(p => p.Slot).FirstOrDefault();
            if (elected == null)
            {
                _migrating = false;
                Fail("Host left and no migration candidate remains.");
                yield break;
            }

            HostSlot = elected.Slot;
            Sync.EnemySync.ReassignFixedOwners(oldHostSlot, elected.Slot);
            Plugin.Log.LogInfo($"[Session] loopback elected P{elected.Slot + 1} identity={elected.IdentityId:X}");
            // Do not replace a transport from inside its PeerDisconnected callback.
            yield return null;
            if (elected.IsLocal) BecomeHost();
            else ReattachLoopback(elected);
            _migrating = false;
        }

        private void BecomeHost()
        {
            Plugin.Log.LogInfo("[Session] promoted to host (migration)");
            _transport.Stop();
            _transport.Dispose();
            try
            {
                _transport = CreateTransport();
                WireTransport();
                _transport.StartHost();
            }
            catch (Exception e)
            {
                Fail($"Host migration takeover failed: {e.Message}");
                return;
            }
            HostSlot = (byte)LocalSlot;
            var local = _players[LocalSlot];
            if (local != null)
            {
                local.PeerId = _transport.LocalPeerId;
                local.IdentityId = LocalIdentityId();
                local.Connected = true;
                local.RttMs = 0;
            }
            // Capture the segments the departing peers owned BEFORE OnPeerLost flips them Dormant, so
            // we can seed their last-known entity states to every surviving peer (WS4.2). A HashSet
            // dedups (a segment has a single owner, but be defensive).
            var orphanedSegments = new HashSet<AuthorityManager.SegmentKey>();
            foreach (var p in _players)
                if (p != null && !p.IsLocal)
                    foreach (var seg in AuthorityManager.SegmentsOwnedBy(p.Slot))
                        orphanedSegments.Add(seg);

            // Everyone else is disconnected from ME right now — reserve their slots; their
            // resume-HELLOs (or full rejoins) bring them back.
            foreach (var p in _players)
                if (p != null && !p.IsLocal)
                {
                    p.Connected = false;
                    p.RttMs = -1;
                    p.PeerId = 0;
                    AuthorityManager.OnPeerLost(p.Slot);
                }
            if (UsingSteam) _lobby?.TakeOverLobby();
            // Continue the epoch sequence — a promoted host restarting at 1 would lose every
            // PREPARE to the higher epochs peers already hold.
            AuthorityManager.OnPromotedToHost();
            // Registrar fallback ownership changed immediately. Re-arm local/puppet components
            // before the next state tick; explicit segment leases converge on the next scan.
            Sync.EnemySync.ApplyAllOwnership();
            // Seed every surviving peer with the ex-host's last-known entity states for its orphaned
            // segments, from our own FullState cache — BEFORE the scan re-grants ownership, so entities
            // resume from a canonical baseline instead of a slow/lossy grace fallback (WS4.2).
            foreach (var seg in orphanedSegments)
                Sync.EnemySync.SeedMigrationDormancyCommit(seg, this);
            // Fog is host-authoritative: this machine now resumes the sim, but as a former client
            // its fogLevels is stale (see FogHostAuthority). Reconcile it with current terrain
            // before the next tick, or fog snaps back toward its gen-time layout.
            Patches.FogHostAuthority.ReseedFromTerrain();
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
            BeginReattach(hostPlayer, newHostId.ToString(), newHostId);
        }

        private void ReattachLoopback(NetPlayer hostPlayer)
        {
            BeginReattach(hostPlayer, $"{NetConfig.LoopbackHost.Value}:{NetConfig.LoopbackPort.Value}", 0);
        }

        private void BeginReattach(NetPlayer hostPlayer, string address, ulong targetPeerId)
        {
            Plugin.Log.LogInfo($"[Session] reattaching to new host {hostPlayer}");
            HostSlot = hostPlayer.Slot;
            _joinTargetHostSlot = hostPlayer.Slot;
            UI.Toast.Show($"HOST LEFT — {hostPlayer.Name} IS NOW HOST", 6f);
            _transport.Stop();
            _transport.Dispose();
            _transport = CreateTransport();
            WireTransport();
            _reattaching = true;
            _reattachDeadline = Time.unscaledTime + 20f;
            _joinTargetPeerId = targetPeerId;
            _transport.StartClient(address);
            // PeerConnected (first Poll) sends the resume-HELLO; Welcome completes reattach.
        }

        private void OnPeerDisconnected(ulong peer)
        {
            Sync.WorldSync.CancelStream(peer); // a rejoin restarts it from scratch
            ClearOutboxFor(peer);              // rejoin catch-up re-serves everything anyway
            NetSeq.ResetPeer(peer);            // WS8.2: counts are per-connection
            ResetSeqBaselines();
            _nextGapCatchUpAt.Remove(peer);
            _peersAwaitingRejoinState.Remove(peer);
            _nextGoLiveRecoveryAt.Remove(peer);
            if (IsHost)
            {
                var player = _players.FirstOrDefault(p => p != null && p.PeerId == peer);
                if (player == null) return;
                if (State == SessionState.Loading || State == SessionState.InGame)
                {
                    // Mid-run: reserve the identity/slot for rejoin, remove its ship puppet, and
                    // let the authority scan reassign its world simulation on the next pass.
                    Plugin.Log.LogInfo($"[Session] {player} dropped — slot reserved for rejoin");
                    player.Connected = false;
                    player.NeedsStationRespawn = true;
                    player.RespawnStationNetId = 0;
                    player.RttMs = -1;
                    player.PeerId = 0; // transport routes are not durable reconnect identities
                    Sync.ShipSync.SuspendRemoteShip(player.Slot, "peer disconnected"); // hide + 60s reclaim (WS4.1)
                    AuthorityManager.OnPeerLost(player.Slot); // no holds/denies for a gone machine
                    Sync.EnemySync.ReassignFixedOwners(player.Slot, HostSlot);
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
                ulong hostPeer = _players[HostSlot]?.PeerId ?? 0;
                if (peer != hostPeer)
                {
                    var meshPlayer = _players.FirstOrDefault(p => p != null && p.PeerId == peer);
                    if (meshPlayer != null) Sync.EnemySync.OnDirectPeerLost(meshPlayer.Slot);
                    Plugin.Log.LogInfo($"[Session] direct state route to peer {peer} closed; host relay remains active");
                    return;
                }
                OnHostLost("Lost connection to host");
            }
        }

        private void OnData(ulong peer, NetChannel channel, ArraySegment<byte> payload)
        {
            _lastPayload = payload;
            NetSeq.NoteReceived(peer, channel); // WS8.2: count BEFORE parsing — the checkpoint barrier
                                                // must include malformed/unknown messages too
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
            // Publish the handler about to run so a hitch inside Transport.Poll names its message type
            // (a disp# frozen across "ongoing" lines is the wedged handler).
            RuntimeInstrumentation.SetDispatchHandler(type);

            // Gameplay traffic can only be applied while a world exists (Loading covers rejoin
            // catch-up). After a run ends, in-flight kills/fire/cells from peers would land on
            // a destroyed level — drop them. Ping/Pong keep the lobby RTT alive.
            if (channel != NetChannel.Control && type != MsgType.Ping && type != MsgType.Pong
                && State < SessionState.Loading)
                return;

            // Client mesh sessions are data-plane only. Control, combat and durable mutations
            // still pass through the elected host; a direct peer may send only entity snapshots.
            if (!IsHost && State >= SessionState.Loading)
            {
                ulong hostPeer = _players[HostSlot]?.PeerId ?? 0;
                if (peer != hostPeer && type != MsgType.EntityStateBundle) return;
            }

            switch (type)
            {
                case MsgType.Hello when IsHost: HandleHello(peer); break;
                case MsgType.SetLobbyPrefs when IsHost: HandleSetLobbyPrefs(peer); break;
                case MsgType.Welcome when !IsHost: HandleWelcome(); break;
                case MsgType.Reject when !IsHost: HandleReject(); break;
                case MsgType.SessionEnded when !IsHost: OnHostLost("Host ended the session.", hostQuit: true); break;
                case MsgType.Kicked when !IsHost:
                    UI.Toast.Show("YOU HAVE BEEN KICKED FROM THE LOBBY", 6f);
                    RejoinMemory.Clear(); // never auto-offer a session we were removed from
                    Fail("You were kicked by the host.");
                    break;
                case MsgType.RunEnded when !IsHost: EndRunToLobby(); break;
                case MsgType.AuthRelease when IsHost:
                {
                    var release = AuthReleaseMsg.Read(_reader);
                    var sender = _players.FirstOrDefault(p => p != null && p.Connected && p.PeerId == peer);
                    if (sender != null && Sync.EnemySync.OwnerOf(release.NetId) == sender.Slot)
                        Sync.EnemySync.ApplyAuthRelease(release, this);
                    break;
                }
                case MsgType.EntityStarvedRequest when IsHost:
                {
                    var request = EntityStarvedRequestMsg.Read(_reader);
                    var sender = _players.FirstOrDefault(p => p != null && p.Connected && p.PeerId == peer);
                    if (sender != null)
                        Sync.EnemySync.ApplyStarvedOwnershipRequest(request.NetId, sender.Slot, this);
                    else
                        Plugin.Log.LogWarning($"[Availability] starved request for #{request.NetId} from UNKNOWN peer {peer} — dropped");
                    break;
                }
                case MsgType.EntityAuthorityPrepare when !IsHost:
                    Sync.EnemySync.ApplyAuthorityPrepare(EntityAuthorityPrepareMsg.Read(_reader), this);
                    break;
                case MsgType.EntityAuthorityAck when IsHost:
                {
                    var ack = EntityAuthorityAckMsg.Read(_reader);
                    var sender = _players.FirstOrDefault(p => p != null && p.Connected && p.PeerId == peer);
                    if (sender != null)
                        Sync.EnemySync.ApplyAuthorityReadyAck(ack.NetId, sender.Slot, this);
                    break;
                }
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
                    if (IsHost)
                    {
                        var sender = _players.FirstOrDefault(p => p != null && p.PeerId == peer);
                        if (sender == null || sender.Slot != fire.Slot) break;
                    }
                    RelayToOthers(peer, channel, reliable: false);
                    Sync.ProjectileSync.ReplayFire(fire);
                    break;
                }
                case MsgType.ProjectileDetonate:
                {
                    var det = ProjectileDetonateMsg.Read(_reader);
                    RelayToOthers(peer, channel, reliable: true);
                    Sync.ProjectileSync.ApplyDetonate(det);
                    break;
                }
                case MsgType.ProjectileState:
                {
                    var pstate = ProjectileStateMsg.Read(_reader);
                    RelayToOthers(peer, channel, reliable: false);
                    Sync.ProjectileSync.ApplyProjectileState(pstate);
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
                    if (IsHost)
                    {
                        var sender = _players.FirstOrDefault(p => p != null && p.PeerId == peer);
                        if (sender == null || sender.Slot != dmg.AttackerSlot) break;
                        if (dmg.IsEntity && !NetIds.LifetimeMatches(dmg.TargetNetId, dmg.TargetLifetime))
                        {
                            InstrumentationCounters.StaleLifetimeDropped();
                            break;
                        }
                    }
                    if (IsHost && dmg.IsEntity) AuthorityManager.NoteCombat(dmg.TargetNetId);
                    byte ownerSlot = dmg.IsEntity ? Sync.EnemySync.OwnerOf(dmg.TargetNetId) : dmg.TargetSlot;
                    if (IsHost && dmg.IsEntity && ownerSlot == AuthorityManager.DormantOwner)
                    {
                        // Nobody simulates the target: queue the claim and force the segment
                        // toward the attacker (who provably holds a live object).
                        Sync.DamageSync.QueueDormantClaim(dmg);
                    }
                    else if (IsHost && ownerSlot != LocalSlot)
                    {
                        // Route to the victim's current authority.
                        var target = _players.FirstOrDefault(p => p != null && p.Slot == ownerSlot);
                        if (target != null && !target.IsLocal)
                            SendReliable(target.PeerId, NetChannel.Combat, _lastPayload);
                    }
                    else
                    {
                        Sync.DamageSync.ApplyDamageRequest(dmg);
                    }
                    break;
                }
                case MsgType.DamageUnservable when IsHost:
                {
                    var claim = DamageRequestMsg.Read(_reader);
                    var sender = _players.FirstOrDefault(p => p != null && p.Connected && p.PeerId == peer);
                    if (sender != null) Sync.DamageSync.ApplyDamageUnservable(claim, sender.Slot, this);
                    break;
                }
                case MsgType.AuthAssign when !IsHost:
                    Sync.EnemySync.ApplyAuthAssign(AuthAssignMsg.Read(_reader));
                    break;
                case MsgType.SegmentLease when !IsHost:
                    AuthorityManager.ApplyLease(SegmentLeaseMsg.Read(_reader), this);
                    break;
                case MsgType.ResidencyReport when IsHost:
                {
                    var report = ResidencyReportMsg.Read(_reader);
                    var reporter = _players.FirstOrDefault(p => p != null && p.Connected && p.PeerId == peer);
                    if (reporter != null && reporter.Slot == report.Slot)
                        AuthorityManager.ApplyResidencyReport(report, this);
                    break;
                }
                case MsgType.LinkHealth:
                {
                    var health = LinkHealthMsg.Read(_reader);
                    if (IsHost)
                    {
                        var sender = _players.FirstOrDefault(p => p != null && p.Connected && p.PeerId == peer);
                        if (sender == null || sender.Slot != health.Slot) break;
                        // Every peer that streams state to this viewer needs its budget (WS7.2):
                        // the host applies it for its own fanout and relays for direct-route owners.
                        RelayToOthers(peer, NetChannel.Events, reliable: true);
                    }
                    else if (_players[HostSlot] == null || _players[HostSlot].PeerId != peer) break;
                    if (health.Slot != LocalSlot) Sync.EnemySync.ApplyLinkHealth(health.Slot, health.Score);
                    break;
                }
                case MsgType.SegmentStateSummary:
                {
                    var summary = SegmentStateSummaryMsg.Read(_reader);
                    if (IsHost)
                        RelayToOthers(peer, channel, reliable: channel != NetChannel.State);
                    else if (_players[HostSlot] == null || _players[HostSlot].PeerId != peer) break;
                    Sync.EnemySync.ApplySegmentStateSummary(summary, this);
                    break;
                }
                case MsgType.EventSeqCheckpoint when !IsHost:
                {
                    var checkpoint = EventSeqCheckpointMsg.Read(_reader);
                    if (_players[HostSlot] == null || _players[HostSlot].PeerId != peer) break;
                    if (checkpoint.Channel != (byte)channel) break; // a barrier only on its own channel
                    // OnData counted this checkpoint too — messages before it = received - 1.
                    uint before = NetSeq.ReceivedFrom(peer, channel) - 1;
                    int ch = checkpoint.Channel & 3;
                    if (!_seqBaselined[ch])
                    {
                        // First checkpoint since a (re)connect: absolute counts are not comparable
                        // across a reset — record the pair and only compare growth from here on.
                        _seqBaselined[ch] = true;
                        _seqBaselineSent[ch] = checkpoint.Count;
                        _seqBaselineReceived[ch] = before;
                        break;
                    }
                    uint sentDelta = checkpoint.Count - _seqBaselineSent[ch];
                    uint recvDelta = before - _seqBaselineReceived[ch];
                    _seqBaselineSent[ch] = checkpoint.Count;
                    _seqBaselineReceived[ch] = before;
                    if (recvDelta >= sentDelta) break;
                    if (Time.unscaledTime < _nextGapReportAt[ch]) break;
                    _nextGapReportAt[ch] = Time.unscaledTime + 30f;
                    Plugin.Log.LogError($"[Seq] GAP on channel {channel}: host sent {sentDelta} " +
                        $"messages since the last checkpoint, we received {recvDelta} — " +
                        $"{sentDelta - recvDelta} lost silently; requesting catch-up");
                    _writer.Reset();
                    new EventGapReportMsg { Channel = checkpoint.Channel, Expected = sentDelta, Received = recvDelta }
                        .Write(_writer);
                    SendReliable(peer, NetChannel.Control, _writer.ToSegment());
                    break;
                }
                case MsgType.EventGapReport when IsHost:
                {
                    var gap = EventGapReportMsg.Read(_reader);
                    var victim = _players.FirstOrDefault(p => p != null && p.Connected && p.PeerId == peer);
                    if (victim == null) break;
                    if (_nextGapCatchUpAt.TryGetValue(peer, out float nextAt) && Time.unscaledTime < nextAt) break;
                    _nextGapCatchUpAt[peer] = Time.unscaledTime + 30f;
                    Plugin.Log.LogWarning($"[Seq] {victim} reports a channel-{gap.Channel} gap " +
                        $"({gap.Received}/{gap.Expected}) — replaying event catch-up");
                    SendEventCatchUp(peer);
                    break;
                }
                case MsgType.PartyLeaderSettings when IsHost:
                {
                    var settings = PartyLeaderSettingsMsg.Read(_reader);
                    // Only a coordinator adopts a leader's world choice, and only before the run
                    // starts (a normal player-host already owns its own settings).
                    if (!NetConfig.IsCoordinator || State != SessionState.Lobby) break;
                    var leader = _players.FirstOrDefault(p => p != null && p.Connected && p.PeerId == peer);
                    if (leader == null) break;
                    ChosenSeed = settings.Seed;
                    FriendlyFire = settings.FriendlyFire;
                    HpScaling = settings.HpScaling;
                    Plugin.Log.LogInfo($"[Coordinator] adopted {leader}'s world: seed={settings.Seed} ff={settings.FriendlyFire} hp={settings.HpScaling}");
                    BroadcastLobbyState(); // so every lobby screen shows the chosen seed/FF
                    break;
                }
                case MsgType.LobbyMembers when IsHost:
                {
                    var msg = LobbyMembersMsg.Read(_reader);
                    // Only a shipless coordinator gates on a relayed lobby (a normal host owns the
                    // lobby directly). Trust the relay only from a seated, connected player.
                    if (!NetConfig.IsCoordinator) break;
                    var relay = _players.FirstOrDefault(p => p != null && p.Connected && p.PeerId == peer);
                    if (relay == null) break;
                    _allowedPeers = new HashSet<ulong>(msg.Members);
                    Plugin.Log.LogInfo($"[Coordinator] lobby allowlist now {_allowedPeers.Count} member(s) (via {relay})");
                    break;
                }
                case MsgType.AdminGrant when !IsHost:
                {
                    var grant = AdminGrantMsg.Read(_reader);
                    if (grant.Token == _localAdminToken) break; // periodic idempotent re-send
                    _localAdminToken = grant.Token;
                    Plugin.Log.LogInfo("[Admin] granted session-admin controls by the server");
                    RosterChanged?.Invoke(); // surface the host UI once the roster flag lands too
                    break;
                }
                case MsgType.AdminCommand when IsHost:
                {
                    var cmd = AdminCommandMsg.Read(_reader);
                    // Only a coordinator delegates control (a player-host is its own admin). Authorize on
                    // the SECRET TOKEN — never the peer id — so a modded/spoofing client is refused even
                    // on an untrusted transport. Defence in depth: the sender must also be the admin's
                    // live connection.
                    if (!NetConfig.IsCoordinator) break;
                    var admin = _adminSlot >= 0 && _adminSlot <= MaxPlayers ? _players[_adminSlot] : null;
                    if (_adminToken == 0 || cmd.Token != _adminToken
                        || admin == null || !admin.Connected || admin.PeerId != peer)
                    {
                        Plugin.Log.LogWarning($"[Admin] refused {cmd.Command} from peer {peer}: bad token or not the admin");
                        break;
                    }
                    switch (cmd.Command)
                    {
                        case AdminCmd.StartRun:
                            if (State == SessionState.Lobby && AllReady) StartRun();
                            else Plugin.Log.LogInfo($"[Admin] start ignored (state={State}, allReady={AllReady})");
                            break;
                        case AdminCmd.Kick:
                            if (cmd.Arg != _adminSlot) KickPlayer(cmd.Arg); // an admin can't kick itself
                            break;
                    }
                    break;
                }
                case MsgType.SegmentDormancyCommit:
                {
                    var commit = SegmentDormancyCommitMsg.Read(_reader);
                    if (IsHost)
                    {
                        var committer = _players.FirstOrDefault(p => p != null && p.Connected && p.PeerId == peer);
                        if (committer == null || committer.Slot != commit.Slot) break;
                        RelayToOthers(peer, NetChannel.Events, reliable: true); // every peer keeps the store (I-12)
                    }
                    else if (_players[HostSlot] == null || _players[HostSlot].PeerId != peer) break;
                    Sync.EnemySync.ApplySegmentDormancyCommit(commit, this);
                    break;
                }
                case MsgType.DormantState when !IsHost:
                    Sync.EnemySync.ApplyDormantState(DormantStateMsg.Read(_reader));
                    break;
                case MsgType.SegmentRosterAudit:
                {
                    var audit = SegmentRosterAuditMsg.Read(_reader);
                    if (IsHost)
                    {
                        var auditor = _players.FirstOrDefault(p => p != null && p.Connected && p.PeerId == peer);
                        if (auditor == null || auditor.Slot != audit.Slot) break;
                        RelayToOthers(peer, NetChannel.Events, reliable: true); // every peer audits its own world
                    }
                    else if (_players[HostSlot] == null || _players[HostSlot].PeerId != peer) break;
                    Sync.EnemySync.ApplySegmentRosterAudit(audit, this);
                    break;
                }
                case MsgType.RuntimeBaselineRequest when !IsHost:
                    Sync.EnemySync.ApplyRuntimeBaselineRequest(RuntimeBaselineRequestMsg.Read(_reader), this);
                    break;
                case MsgType.RuntimeBaseline:
                {
                    var baseline = RuntimeBaselineMsg.Read(_reader);
                    if (IsHost)
                    {
                        var sender = _players.FirstOrDefault(p => p != null && p.Connected && p.PeerId == peer);
                        if (sender == null || sender.Slot != baseline.SourceSlot) break;
                        Sync.EnemySync.HostRouteRuntimeBaseline(baseline, this);
                    }
                    else Sync.EnemySync.ApplyRuntimeBaseline(baseline, this);
                    break;
                }
                case MsgType.RuntimeBaselineAck when IsHost:
                {
                    var ack = RuntimeBaselineAckMsg.Read(_reader);
                    var sender = _players.FirstOrDefault(p => p != null && p.Connected && p.PeerId == peer);
                    if (sender != null) Sync.EnemySync.ApplyRuntimeBaselineAck(ack, sender.Slot, this);
                    break;
                }
                case MsgType.DirectRoute when !IsHost:
                    Sync.EnemySync.ApplyDirectRoute(DirectRouteMsg.Read(_reader), this);
                    break;
                case MsgType.DirectRoutePulse when IsHost:
                {
                    var pulse = DirectRoutePulseMsg.Read(_reader);
                    var sender = _players.FirstOrDefault(p => p != null && p.Connected && p.PeerId == peer);
                    if (sender != null) Sync.EnemySync.ApplyDirectRoutePulse(pulse, sender.Slot, this);
                    break;
                }
                case MsgType.EntityBoundaryHandoff:
                {
                    var handoff = EntityBoundaryHandoffMsg.Read(_reader);
                    if (IsHost)
                    {
                        var sender = _players.FirstOrDefault(p => p != null && p.Connected && p.PeerId == peer);
                        if (sender == null || !Sync.EnemySync.ValidateBoundaryHandoff(handoff, sender.Slot))
                        {
                            InstrumentationCounters.UnauthorizedMutationDropped();
                            break;
                        }
                    }
                    Sync.EnemySync.ApplyBoundaryHandoff(handoff);
                    RelayToOthers(peer, channel, reliable: true);
                    break;
                }
                case MsgType.KillLedger when !IsHost:
                    Sync.EnemySync.ApplyKillLedger(KillLedgerMsg.Read(_reader));
                    break;
                case MsgType.EntityBaseline when !IsHost:
                    NetIds.ApplyBaseline(EntityBaselineMsg.Read(_reader));
                    break;
                case MsgType.EntityState:
                {
                    var state = EntityStateMsg.Read(_reader);
                    RelayToOthers(peer, channel, reliable: false);
                    Sync.EnemySync.ApplyEntityState(state);
                    break;
                }
                case MsgType.EntityStateBundle:
                {
                    var bundle = EntityStateBundleMsg.Read(_reader);
                    var sender = _players.FirstOrDefault(p => p != null && p.Connected && p.PeerId == peer);
                    if (IsHost)
                    {
                        if (sender == null || sender.Slot != bundle.Slot) break;
                    }
                    else
                    {
                        bool fromHost = _players[HostSlot] != null && _players[HostSlot].PeerId == peer;
                        if (!fromHost && (sender == null || sender.Slot != bundle.Slot)) break;
                        if (!fromHost) Sync.EnemySync.NoteDirectBundle(peer, bundle, this);
                    }
                    // Route before touching host-side puppets/data. Large-room apply work must not
                    // sit on the latency-critical owner -> host -> viewer path.
                    if (IsHost)
                    {
                        float relayStarted = Time.realtimeSinceStartup;
                        Sync.EnemySync.ForwardEntityStateBundle(this, bundle, peer);
                        InstrumentationCounters.HostRelayCompleted((Time.realtimeSinceStartup - relayStarted) * 1000f,
                            _lastPayload.Count);
                    }
                    Sync.EnemySync.ApplyEntityStateBundle(bundle);
                    break;
                }
                case MsgType.EntityFire:
                {
                    var efire = EntityFireMsg.Read(_reader);
                    if (IsHost)
                    {
                        var sender = _players.FirstOrDefault(p => p != null && p.PeerId == peer);
                        if (sender == null || sender.Slot != efire.SourceSlot)
                        {
                            InstrumentationCounters.StaleFireDropped();
                            break;
                        }
                        if (!NetIds.LifetimeMatches(efire.NetId, efire.Lifetime))
                        {
                            InstrumentationCounters.StaleLifetimeDropped();
                            break;
                        }
                    }
                    if (IsHost) AuthorityManager.NoteAggro(efire.NetId, efire.TargetSlot);
                    // Aimed fire: reliable to the one victim it can damage, unreliable to the rest.
                    if (efire.TargetSlot != 255)
                        RelayToOthers(peer, channel, efire.TargetSlot, NetChannel.Combat);
                    else
                        RelayToOthers(peer, channel, reliable: false);
                    Sync.ProjectileSync.ReplayEntityFire(efire);
                    break;
                }
                case MsgType.EntityKilled:
                {
                    var killed = EntityKilledMsg.Read(_reader);
                    if (IsHost)
                    {
                        var sender = _players.FirstOrDefault(p => p != null && p.Connected && p.PeerId == peer);
                        if (sender == null) break;
                        // Do NOT gate kills on CURRENT ownership: a kill legitimately sent by
                        // the owner races its own release/handoff/dormancy, and a dropped kill
                        // is a PERMANENT divergence (the kill ledger only heals host->client;
                        // nothing heals a host-side miss — caught live by the entity sweep:
                        // dead-on-client, alive-on-host after an ownership flip). Lifetime,
                        // revision monotonicity, and the KilledNetIds dedup inside
                        // ApplyEntityKilled already reject stale/duplicate kills.
                        if (Sync.EnemySync.OwnerOf(killed.NetId) != sender.Slot && NetDiag.Enabled)
                            NetDiag.Log("Auth", $"accepting kill #{killed.NetId} from " +
                                $"P{sender.Slot + 1} past an ownership transition");
                    }
                    if (Sync.EnemySync.ApplyEntityKilled(killed))
                        RelayToOthers(peer, channel, reliable: true);
                    break;
                }
                case MsgType.PlantFruitKilled:
                {
                    var killed = PlantFruitKilledMsg.Read(_reader);
                    if (IsHost && _players.FirstOrDefault(p => p != null && p.Connected && p.PeerId == peer) == null)
                        break;
                    // Same reasoning as EntityKilled: kills race ownership transitions, and the
                    // fruit tombstone machinery (revision + KilledPlantFruits dedup) already
                    // rejects stale/duplicate events.
                    if (Sync.EnemySync.ApplyPlantFruitKilled(killed))
                        RelayToOthers(peer, channel, reliable: true);
                    break;
                }
                case MsgType.EntitySpawned:
                {
                    var spawned = EntitySpawnedMsg.Read(_reader);
                    if (IsHost)
                    {
                        var sender = _players.FirstOrDefault(p => p != null && p.Connected && p.PeerId == peer);
                        if (sender == null || sender.Slot != spawned.OwnerSlot) break;
                    }
                    RelayToOthers(peer, channel, reliable: true);
                    Sync.MinionSync.ApplyEntitySpawned(spawned);
                    break;
                }
                case MsgType.MinionSpawned:
                {
                    var minion = MinionSpawnedMsg.Read(_reader);
                    if (IsHost)
                    {
                        var sender = _players.FirstOrDefault(p => p != null && p.Connected && p.PeerId == peer);
                        if (sender == null || sender.Slot != minion.OwnerSlot) break;
                    }
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
                case MsgType.MapDiscovered:
                {
                    var disc = MapDiscoveredMsg.Read(_reader);
                    RelayToOthers(peer, channel, reliable: true);
                    Sync.ProgressionSync.ApplyMapDiscovered(disc);
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
                    if (IsHost && diff.Revision == 0)
                    {
                        var canonical = Sync.WorldSync.AcceptProposal(diff);
                        _writer.Reset(); canonical.Write(_writer);
                        SendToAll(NetChannel.Events, _writer.ToSegment(), reliable: true); // includes proposer
                    }
                    else if (!IsHost)
                        Sync.WorldSync.ApplyCanonical(diff);
                    break;
                }
                case MsgType.TerrainDigest when !IsHost:
                    Sync.WorldSync.ApplyDigest(TerrainDigestMsg.Read(_reader), this);
                    break;
                case MsgType.TerrainRepairRequest when IsHost:
                    Sync.WorldSync.SendRepair(this, peer, TerrainRepairRequestMsg.Read(_reader));
                    break;
                case MsgType.TerrainRepairChunk when !IsHost:
                    Sync.WorldSync.ApplyRepair(TerrainRepairChunkMsg.Read(_reader));
                    break;
                case MsgType.TerrainSync when !IsHost:
                    Sync.WorldSync.ApplyTerrainSync(TerrainSyncMsg.Read(_reader));
                    break;
                case MsgType.IdResolveRequest when IsHost:
                {
                    var req = IdResolveRequestMsg.Read(_reader);
                    _writer.Reset();
                    new IdResolveReplyMsg { Entries = NetIds.DescribeNetIds(req.NetIds) }.Write(_writer);
                    SendReliable(peer, NetChannel.Control, _writer.ToSegment());
                    Plugin.Log.LogInfo($"[Ids] resolve request from {peer}: described {req.NetIds.Count} netIds");
                    break;
                }
                case MsgType.IdResolveReply when !IsHost:
                    NetIds.ApplyResolve(IdResolveReplyMsg.Read(_reader));
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

            // Sidecar lobby-gated joins (#2): a shipless coordinator can't see the Steam lobby, so it
            // admits a peer only once the party leader has relayed a membership set that includes it.
            // Before the first relay arrives (_allowedPeers == null) it's accept-all — the leader's own
            // connection bootstraps the session. A SteamServer peer id IS the connecting user's
            // SteamID64, matching the relayed lobby-member ids.
            // A peer whose identity already holds a seat (connected or reserved-for-rejoin) was
            // admitted once — always let it back in, even if the CURRENT allowlist is stale (e.g. the
            // leader re-hosted and opened a fresh discovery lobby the old friend hasn't re-entered).
            bool seatedIdentity = _players.Any(p => p != null && !p.IsLocal
                && (hello.SteamId != 0 ? p.IdentityId == hello.SteamId : p.Name == hello.Name));
            if (NetConfig.IsCoordinator && _allowedPeers != null && !seatedIdentity && !_allowedPeers.Contains(peer))
            {
                Plugin.Log.LogWarning($"[Coordinator] refused HELLO from {peer}: not a discovery-lobby member");
                _writer.Reset();
                new RejectMsg { Reason = "This server is invite-only — ask the host for a Steam invite." }.Write(_writer);
                SendReliable(peer, NetChannel.Control, _writer.ToSegment());
                return;
            }

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
                // Mid-run: a slot matching (SteamID, else name) is a rejoin/resume; anyone else
                // is a late joiner and takes a free slot below. The match deliberately includes
                // slots still marked Connected: a reconnect can beat the old route's timeout
                // (LastHeard is stamped at drain time, so a host waking from a stall sees the
                // dead route as fresh) — the same identity on a new route supersedes the old
                // one; it must never be seated in a second slot.
                var reserved = _players.FirstOrDefault(p => p != null && !p.IsLocal
                    && (hello.SteamId != 0 ? p.IdentityId == hello.SteamId : p.Name == hello.Name));
                if (reserved != null)
                {
                    if (reserved.Connected && reserved.PeerId != 0 && reserved.PeerId != peer)
                    {
                        Plugin.Log.LogInfo($"[Session] {reserved} reconnected on a new route before the old one timed out — superseding peer {reserved.PeerId}");
                        Sync.WorldSync.CancelStream(reserved.PeerId);
                        ClearOutboxFor(reserved.PeerId);
                        _peersAwaitingRejoinState.Remove(reserved.PeerId);
                        _nextGoLiveRecoveryAt.Remove(reserved.PeerId);
                    }
                    reserved.ModsMismatch = modsMismatch;
                    if (hello.Resuming) HandleResume(peer, hello, reserved);
                    else HandleRejoin(peer, hello, reserved);
                    return;
                }
            }
            else if (reject == null)
            {
                // Lobby state: the same identity may still be seated (stale route, or a ghost
                // reservation that outlived its run) — release it so one identity never holds
                // two slots. The joiner then seats normally, often into the freed slot.
                for (int i = 0; i < MaxPlayers; i++)
                {
                    var p = _players[i];
                    if (p == null || p.IsLocal) continue;
                    if (!(hello.SteamId != 0 ? p.IdentityId == hello.SteamId : p.Name == hello.Name)) continue;
                    Plugin.Log.LogInfo($"[Session] {p} rejoined the lobby on a new route — releasing the old seat");
                    if (p.Connected && p.PeerId != 0 && p.PeerId != peer)
                    {
                        Sync.WorldSync.CancelStream(p.PeerId);
                        ClearOutboxFor(p.PeerId);
                    }
                    _players[i] = null;
                }
            }

            int slot = -1;
            if (reject == null)
            {
                for (int i = 0; i < MaxPlayers; i++)
                    if (_players[i] == null) { slot = i; break; }
                if (slot < 0) reject = "Lobby is full.";
            }

            if (reject != null)
            {
                _writer.Reset();
                new RejectMsg { Reason = reject }.Write(_writer);
                SendReliable(peer, NetChannel.Control, _writer.ToSegment());
                Plugin.Log.LogWarning($"[Session] rejected {peer}: {reject}");
                return;
            }

            _players[slot] = new NetPlayer
            {
                Slot = (byte)slot,
                PeerId = peer,
                IdentityId = hello.SteamId,
                Name = hello.Name,
                ModsMismatch = modsMismatch,
            };
            Plugin.Log.LogInfo($"[Session] {_players[slot]} joined{(midRun ? " (mid-run, catching up)" : "")}");
            if (modsMismatch) UI.Toast.Show($"{hello.Name} JOINED WITH A DIFFERENT MOD SET", 5f);

            _writer.Reset();
            new WelcomeMsg { Slot = (byte)slot, HostModVersion = Plugin.Version, Roster = BuildRoster() }.Write(_writer);
            SendReliable(peer, NetChannel.Control, _writer.ToSegment());
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
                if (State == SessionState.InGame)
                {
                    _peersAwaitingRejoinState.Add(peer);
                    _nextGoLiveRecoveryAt.Remove(peer);
                }
                Sync.ModuleGridSync.ForceRebroadcast(); // late joiner builds our puppet from defaults
            }
        }

        /// <summary>Host-migration reattach: the peer is already live in the same world — no
        /// regen, just roster + the reliable events it may have missed during the handover gap
        /// (all idempotent on the receiving side).</summary>
        private void HandleResume(ulong peer, HelloMsg hello, NetPlayer reserved)
        {
            reserved.PeerId = peer;
            if (hello.SteamId != 0) reserved.IdentityId = hello.SteamId;
            reserved.Connected = true;
            reserved.Name = hello.Name;
            Plugin.Log.LogInfo($"[Session] {reserved} reattached after host migration");

            _writer.Reset();
            new WelcomeMsg { Slot = reserved.Slot, HostSlot = HostSlot, HostModVersion = Plugin.Version, Roster = BuildRoster() }.Write(_writer);
            _transport.Send(peer, NetChannel.Control, _writer.ToSegment(), reliable: true);
            BroadcastLobbyState();
            RosterChanged?.Invoke();
            SendEventCatchUp(peer);
            Sync.ModuleGridSync.ForceRebroadcast(); // their puppet of us may hold a stale build
            // Cell diffs sent during the handover gap are gone for good — stream the ledger
            // (idempotent, vicinity-first) so the reattached peer converges regardless.
            Sync.WorldSync.BeginStreamTo(this, peer, reserved.Slot, StreamFallbackPos());
        }

        private void HandleRejoin(ulong peer, HelloMsg hello, NetPlayer reserved)
        {
            // Respawn at the station assigned when we dropped, else the party's latest checkpoint.
            // (Previously this rejected outright unless a NEW station was unlocked AFTER the
            // disconnect — RespawnStationNetId != 0 — which stranded a returning player on
            // "your ship was destroyed" for the rest of the run whenever the party hadn't since
            // reached another station, even though LatestStationNetId is a perfectly good respawn.)
            int respawnStation = reserved.RespawnStationNetId != 0
                ? reserved.RespawnStationNetId
                : Sync.ProgressionSync.LatestStationNetId;
            if (reserved.NeedsStationRespawn && respawnStation == 0)
            {
                // Genuinely nowhere to respawn yet — no station reached this run.
                _writer.Reset();
                new RejectMsg
                {
                    Reason = "No checkpoint reached yet — rejoin once the party unlocks a station."
                }.Write(_writer);
                SendReliable(peer, NetChannel.Control, _writer.ToSegment());
                Plugin.Log.LogInfo($"[Session] rejoin for P{reserved.Slot + 1} deferred — no station checkpoint yet");
                return;
            }
            reserved.PeerId = peer;
            if (hello.SteamId != 0) reserved.IdentityId = hello.SteamId;
            reserved.Connected = true;
            reserved.NeedsStationRespawn = false;
            reserved.RespawnStationNetId = 0;
            reserved.Name = hello.Name;
            Plugin.Log.LogInfo($"[Session] {reserved} REJOINED — replaying run seed {CurrentRunSeed}");

            _writer.Reset();
            new WelcomeMsg { Slot = reserved.Slot, HostSlot = HostSlot, HostModVersion = Plugin.Version, Roster = BuildRoster() }.Write(_writer);
            _transport.Send(peer, NetChannel.Control, _writer.ToSegment(), reliable: true);
            BroadcastLobbyState();
            RosterChanged?.Invoke();

            _writer.Reset();
            new StartRunMsg
            {
                Seed = CurrentRunSeed,
                IsRejoin = true,
                SpawnStationNetId = respawnStation,
                EnemyHpMult = EnemyHpMult,
            }.Write(_writer);
            _transport.Send(peer, NetChannel.Control, _writer.ToSegment(), reliable: true);
            if (State == SessionState.InGame)
            {
                _peersAwaitingRejoinState.Add(peer);
                _nextGoLiveRecoveryAt.Remove(peer);
            }
            Sync.ModuleGridSync.ForceRebroadcast(); // rejoiner rebuilds our puppet from defaults
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
                SendReliable(peer, NetChannel.Control, _writer.ToSegment());
                if (fps.Count == 0) break;
            }

            SendEntityBaseline(peer);

            // 2) Terrain changes since generation stream in vicinity-first chunks around the
            // rejoiner — the whole ledger regardless of size, budgeted per frame so the
            // reliable buffer can't overflow and no size cutoff can leave them divergent.
            var rejoiner = _players.FirstOrDefault(p => p != null && p.PeerId == peer);
            Sync.WorldSync.BeginStreamTo(this, peer, rejoiner?.Slot ?? 0, StreamFallbackPos());

            // 3) Deaths, ownership, shared progression.
            SendEventCatchUp(peer);

            // 3b) Canonical dormant state: last-simulated vitals/poses so the rejoiner's world
            // reflects what actually happened, not generation guesses.
            Sync.EnemySync.SendDormantState(this, peer);

            // 4) Go.
            _writer.Reset();
            _writer.WriteMsgType(MsgType.GoLive);
            SendReliable(peer, NetChannel.Control, _writer.ToSegment());
            // Stage sizes matter for field triage: a rejoiner black-screening on a big/old world
            // (unreproducible locally) needs this line + the client's watchdog to say WHERE it died.
            Plugin.Log.LogInfo($"[Session] rejoin catch-up sent to {peer} (manifest {fps.Count}, " +
                $"upgrades {Sync.ProgressionSync.UpgradeSnapshot().Count}, " +
                $"kills {Sync.EnemySync.KilledCount})");
        }

        private void SendEntityBaseline(ulong onlyPeer)
        {
            var baseline = NetIds.BuildBaseline();
            const int chunk = 120;
            for (int start = 0; start < baseline.Count || start == 0; start += chunk)
            {
                int count = Math.Min(chunk, baseline.Count - start);
                _writer.Reset();
                new EntityBaselineMsg
                {
                    Start = (ushort)start,
                    Total = (ushort)baseline.Count,
                    Entries = count > 0 ? baseline.GetRange(start, count) : new List<EntityBaselineEntry>(),
                }.Write(_writer);
                if (onlyPeer != 0) SendReliable(onlyPeer, NetChannel.Control, _writer.ToSegment());
                else ForEachRemotePeer(p => SendReliable(p, NetChannel.Control, _writer.ToSegment()));
                if (baseline.Count == 0) break;
            }
            Plugin.Log.LogInfo($"[Ids] canonical entity baseline sent ({baseline.Count} positions)");
        }

        /// <summary>Client: ask the host to describe manifest netIds we couldn't match, so
        /// orphans can be adopted by type + position (see NetIds.ApplyResolve).</summary>
        public void RequestIdResolve(List<int> netIds)
        {
            if (IsHost || netIds == null || netIds.Count == 0 || _players[HostSlot] == null) return;
            _writer.Reset();
            new IdResolveRequestMsg { NetIds = netIds }.Write(_writer);
            SendReliable(_players[HostSlot].PeerId, NetChannel.Control, _writer.ToSegment());
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

        /// <summary>Deaths, ownership, and shared progression — every message idempotent on the
        /// receiver, so it serves both full rejoins and post-migration resume gaps.</summary>
        private void SendEventCatchUp(ulong peer)
        {
            foreach (var netId in Sync.EnemySync.KilledSnapshot())
            {
                _writer.Reset();
                new EntityKilledMsg
                {
                    NetId = netId, Lifetime = NetIds.LifetimeOf(netId),
                    MutationRevision = Sync.EnemySync.MutationRevisionOf(netId), KillerSlot = 0,
                }.Write(_writer);
                _transport.Send(peer, NetChannel.Events, _writer.ToSegment(), reliable: true);
            }
            foreach (var (plantNetId, fruitId) in Sync.EnemySync.PlantFruitKilledSnapshot())
            {
                _writer.Reset();
                new PlantFruitKilledMsg
                {
                    PlantNetId = plantNetId, FruitId = fruitId,
                    Lifetime = NetIds.LifetimeOf(plantNetId),
                    MutationRevision = Sync.EnemySync.PlantFruitRevisionOf(plantNetId, fruitId), KillerSlot = 0,
                }.Write(_writer);
                _transport.Send(peer, NetChannel.Events, _writer.ToSegment(), reliable: true);
            }
            var owners = Sync.EnemySync.OwnersSnapshot();
            for (int start = 0; start < owners.Count; start += 64)
            {
                _writer.Reset();
                new AuthAssignMsg { Entries = owners.GetRange(start, Math.Min(64, owners.Count - start)) }.Write(_writer);
                _transport.Send(peer, NetChannel.Events, _writer.ToSegment(), reliable: true);
            }
            foreach (var lease in AuthorityManager.Snapshot())
            {
                _writer.Reset();
                lease.Write(_writer);
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
            foreach (var netId in Sync.ProgressionSync.DiscoveredSnapshot())
            {
                _writer.Reset();
                new MapDiscoveredMsg { NetId = netId }.Write(_writer);
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
                        IdentityId = p.IdentityId,
                        Name = p.Name,
                        ColorIndex = p.ColorIndex,
                        Ready = p.Ready,
                        Connected = p.Connected,
                        NeedsStationRespawn = p.NeedsStationRespawn,
                        RespawnStationNetId = p.RespawnStationNetId,
                        ModsMismatch = p.ModsMismatch,
                        IsCoordinator = p.IsCoordinator,
                        IsAdmin = p.IsAdmin,
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
            bool wasReattaching = _reattaching;
            ApplyRoster(welcome.Roster);
            LocalSlot = welcome.Slot;
            if (_players[LocalSlot] != null)
            {
                _players[LocalSlot].IsLocal = true;
                _players[LocalSlot].IdentityId = LocalIdentityId();
            }
            // The host tells us its own slot (a coordinator sits at slot 4, not 0). Reattach keeps
            // the slot it migrated to. Authoritative — no peer-id inference (loopback has peer 0).
            HostSlot = wasReattaching ? _joinTargetHostSlot : welcome.HostSlot;
            if (wasReattaching)
            {
                _reattaching = false;
                // Reconnect-in-place / host-migration reattach: the run never stopped, so the live
                // in-memory economy is authoritative and we deliberately do NOT re-restore from the
                // stash (that would roll back to the last 60s snapshot). If a tester reports lost
                // inventory after leave->migrate->rejoin, this line firing (instead of a "[Stash]
                // rejoin go-live") is the tell that the reconnect took the reattach path with a
                // torn-down economy — see the inventory-loss investigation.
                Plugin.Log.LogInfo($"[Session] reattached to new host (slot {welcome.Slot}, host slot {HostSlot}) — run continues in place; economy kept as-is (no stash restore on this path)");
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
            var hint = reject.Reason.Contains("Version mismatch")
                ? $" Restart your game to apply an auto-downloaded update, or get it manually: {UpdateCheck.ReleasesUrl}"
                : "";
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
            // Clients are not directly connected to one another, so only the host sees a remote
            // transport disconnect. The roster transition is every other peer's cleanup signal.
            var incoming = roster.ToDictionary(e => e.Slot, e => e);
            foreach (var old in _players)
                if (old != null && old.Connected
                    && (!incoming.TryGetValue(old.Slot, out var next) || !next.Connected))
                {
                    Sync.ShipSync.SuspendRemoteShip(old.Slot, "roster disconnected"); // hide + 60s reclaim (WS4.1)
                    Sync.EnemySync.ReassignFixedOwners(old.Slot, HostSlot);
                }

            // A peer coming (back) online builds our ship's puppet from prefab defaults —
            // resend our module grid so its build (weapons included) matches ours.
            foreach (var e in roster)
                if (e.Connected && e.Slot != LocalSlot)
                {
                    var previous = _players[e.Slot];
                    if (previous == null || !previous.Connected)
                    {
                        Sync.ModuleGridSync.ForceRebroadcast();
                        break;
                    }
                }

            // Not ToDictionary: a malformed roster seating one identity in two slots (seen when
            // a ghost reservation collided with its owner rejoining) must not throw and leave
            // the whole roster un-applied.
            var oldRtt = new Dictionary<ulong, int>();
            foreach (var p in _players)
                if (p != null && !oldRtt.ContainsKey(p.IdentityId))
                    oldRtt[p.IdentityId] = p.RttMs;
            for (int i = 0; i <= MaxPlayers; i++) _players[i] = null;
            foreach (var e in roster)
            {
                _players[e.Slot] = new NetPlayer
                {
                    Slot = e.Slot,
                    PeerId = e.PeerId,
                    IdentityId = e.IdentityId,
                    Name = e.Name,
                    ColorIndex = e.ColorIndex,
                    Ready = e.Ready,
                    Connected = e.Connected,
                    NeedsStationRespawn = e.NeedsStationRespawn,
                    RespawnStationNetId = e.RespawnStationNetId,
                    ModsMismatch = e.ModsMismatch,
                    IsCoordinator = e.IsCoordinator,
                    IsAdmin = e.IsAdmin,
                    RttMs = oldRtt.TryGetValue(e.IdentityId, out var rtt) ? rtt : -1,
                };
            }
        }

        /// <summary>The first station unlocked after a real disconnect becomes that slot's
        /// respawn point. Kept in the roster so a promoted host preserves the decision.</summary>
        internal void OnStationUnlocked(int stationNetId)
        {
            if (!IsHost || stationNetId == 0 || (State != SessionState.InGame && State != SessionState.Loading)) return;
            int assigned = 0;
            foreach (var player in _players)
            {
                if (player == null || player.Connected || !player.NeedsStationRespawn
                    || player.RespawnStationNetId != 0) continue;
                player.RespawnStationNetId = stationNetId;
                assigned++;
                InstrumentationCounters.StationRespawnAssigned();
                Plugin.Log.LogInfo($"[Session] P{player.Slot + 1} may respawn at newly unlocked station #{stationNetId}");
            }
            if (assigned == 0) return;
            BroadcastLobbyState();
            RosterChanged?.Invoke();
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
