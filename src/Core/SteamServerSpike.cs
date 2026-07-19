using System;
using System.Runtime.InteropServices;
using System.Text;
using Steamworks;

namespace PunkMultiverse.Core
{
    /// <summary>
    /// SPIKE A (server sidecar): can a coordinator log into Steam as an ANONYMOUS GAME SERVER
    /// (the mechanism real dedicated servers use) and exchange traffic with a user client over
    /// Steam networking? Two phases, each with a hard verdict in the log:
    ///
    ///   A1 — GameServer.Init + LogOnAnonymous on the playtest appid.
    ///        PASS: "[SteamServerSpike] LOGON OK id=..." (an anonymous server SteamID exists).
    ///        FAIL: connect-failure callback or 30s timeout.
    ///   A2 — client `steamconnect &lt;serverSteamID64&gt;` devcmd: user-side NetworkingSockets
    ///        ConnectP2P to the server identity; server accepts, receives "ping", echoes.
    ///        PASS: server logs the ping AND client logs the echo — SDR routes user->server
    ///        for this appid. FAIL: connection state ProblemDetected/ClosedByPeer or timeout.
    ///
    /// The bundled Steamworks.NET has NO SteamGameServerNetworkingMessages, so the server side
    /// uses connection-oriented Sockets — which is also what a real SteamServer transport would
    /// be built on. Spike-only code: enabled by PUNKMV_STEAMSERVER_SPIKE=1, never in normal play.
    /// </summary>
    internal static class SteamServerSpike
    {
        internal static readonly bool Enabled =
            Environment.GetEnvironmentVariable("PUNKMV_STEAMSERVER_SPIKE") == "1";

        // ---- server (coordinator) side ----
        private static bool _serverStarted;
        private static bool _loggedOn;
        private static float _logonDeadline;
        private static Callback<SteamServersConnected_t> _cbConnected;
        private static Callback<SteamServerConnectFailure_t> _cbConnectFail;
        private static Callback<SteamNetConnectionStatusChangedCallback_t> _cbServerConnStatus;
        private static HSteamListenSocket _listen;
        private static HSteamNetConnection _serverConn;

        // ---- client side ----
        private static Callback<SteamNetConnectionStatusChangedCallback_t> _cbClientConnStatus;
        private static HSteamNetConnection _clientConn;
        private static bool _clientActive;
        private static float _clientDeadline;

        private static readonly IntPtr[] MsgPtrs = new IntPtr[8];

        /// <summary>Per-frame pump (from Plugin.Update, env-gated). Coordinator runs the server
        /// half; any instance can run the client half after a `steamconnect` devcmd.</summary>
        internal static void Tick()
        {
            if (!Enabled) return;
            try
            {
                if (NetConfig.IsCoordinator) TickServer();
                if (_clientActive) TickClient();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[SteamServerSpike] tick failed: {e.Message}");
            }
        }

        // ------------------------------------------------------------------ server half

        private static bool _initOk;
        private static float _startAt = -1f;

        private static void TickServer()
        {
            // Let the game's own Steam client-side init settle first (listen-server order:
            // client API up, then game-server API alongside it).
            if (_startAt < 0f) _startAt = UnityEngine.Time.unscaledTime + 12f;
            if (!_serverStarted)
            {
                if (UnityEngine.Time.unscaledTime < _startAt) return;
                _serverStarted = true;
                StartServer();
                return;
            }
            if (!_initOk) return;
            GameServer.RunCallbacks();
            if (!_loggedOn && UnityEngine.Time.unscaledTime > _logonDeadline && _logonDeadline > 0f)
            {
                _logonDeadline = 0f;
                Plugin.Log.LogError("[SteamServerSpike] A1 FAIL — no logon callback within 30s");
            }
            if (_serverConn != HSteamNetConnection.Invalid) PumpConnection(serverSide: true, _serverConn);
        }

        private static void StartServer()
        {
            try
            {
                Plugin.Log.LogWarning("[SteamServerSpike] A1 — GameServer.Init + LogOnAnonymous starting");
                // GameServer.Init resolves the appid the dedicated-server way: SteamAppId env var
                // (there is no steam_appid.txt in this install — attempt 1 failed on exactly this).
                // Same pattern SteamBootstrap uses for direct-launch client init.
                Environment.SetEnvironmentVariable("SteamAppId", NetConfig.SteamAppId.Value.ToString());
                Environment.SetEnvironmentVariable("SteamGameId", NetConfig.SteamAppId.Value.ToString());

                // Init FIRST — Steamworks.NET's callback dispatcher comes up inside Init, and
                // registering callbacks before it exists both no-ops and poisons RunCallbacks
                // ("Callback dispatcher is not initialized", attempt 1's second failure).
                if (!GameServer.Init(0, 27015, 27016, EServerMode.eServerModeAuthentication, Plugin.Version))
                {
                    Plugin.Log.LogError("[SteamServerSpike] A1 FAIL — GameServer.Init returned false");
                    return;
                }
                _initOk = true;
                _cbConnected = Callback<SteamServersConnected_t>.CreateGameServer(_ =>
                {
                    _loggedOn = true;
                    var id = SteamGameServer.GetSteamID();
                    Plugin.Log.LogWarning($"[SteamServerSpike] A1 PASS — LOGON OK id={id.m_SteamID} " +
                        "(client devcmd: steamconnect " + id.m_SteamID + ")");
                    OpenListenSocket();
                });
                _cbConnectFail = Callback<SteamServerConnectFailure_t>.CreateGameServer(f =>
                    Plugin.Log.LogError($"[SteamServerSpike] A1 FAIL — logon failure result={f.m_eResult} retrying={f.m_bStillRetrying}"));
                _cbServerConnStatus = Callback<SteamNetConnectionStatusChangedCallback_t>.CreateGameServer(OnServerConnStatus);

                SteamGameServer.SetProduct("PUNK");
                SteamGameServer.SetGameDescription("PunkMultiverse coordinator spike");
                SteamGameServer.LogOnAnonymous();
                _logonDeadline = UnityEngine.Time.unscaledTime + 30f;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[SteamServerSpike] A1 FAIL — init threw: {e.Message}");
            }
        }

        private static void OpenListenSocket()
        {
            try
            {
                _listen = SteamGameServerNetworkingSockets.CreateListenSocketP2P(0, 0, null);
                Plugin.Log.LogInfo("[SteamServerSpike] A2 — listen socket open on virtual port 0; awaiting client");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[SteamServerSpike] A2 FAIL — listen socket threw: {e.Message}");
            }
        }

        private static void OnServerConnStatus(SteamNetConnectionStatusChangedCallback_t s)
        {
            var state = s.m_info.m_eState;
            if (state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting)
            {
                var r = SteamGameServerNetworkingSockets.AcceptConnection(s.m_hConn);
                Plugin.Log.LogInfo($"[SteamServerSpike] A2 — incoming connection from {s.m_info.m_identityRemote.GetSteamID64()}, accept={r}");
            }
            else if (state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected)
            {
                _serverConn = s.m_hConn;
                Plugin.Log.LogWarning("[SteamServerSpike] A2 — server side CONNECTED");
            }
            else if (state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally
                     || state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer)
            {
                Plugin.Log.LogError($"[SteamServerSpike] A2 server conn ended: state={state} reason={s.m_info.m_eEndReason} '{s.m_info.m_szEndDebug}'");
                _serverConn = HSteamNetConnection.Invalid;
            }
        }

        // ------------------------------------------------------------------ client half

        /// <summary>devcmd `steamconnect &lt;steamid64&gt;`: user-side ConnectP2P to the server identity.</summary>
        internal static string ClientConnect(ulong serverId)
        {
            try
            {
                if (_cbClientConnStatus == null)
                    _cbClientConnStatus = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnClientConnStatus);
                var identity = new SteamNetworkingIdentity();
                identity.SetSteamID64(serverId);
                _clientConn = SteamNetworkingSockets.ConnectP2P(ref identity, 0, 0, null);
                _clientActive = true;
                _clientDeadline = UnityEngine.Time.unscaledTime + 30f;
                return $"steamconnect: dialing {serverId} on vport 0 (watch log for CONNECTED/echo)";
            }
            catch (Exception e)
            {
                return $"steamconnect FAILED: {e.Message}";
            }
        }

        private static void OnClientConnStatus(SteamNetConnectionStatusChangedCallback_t s)
        {
            if (s.m_hConn != _clientConn) return;
            var state = s.m_info.m_eState;
            if (state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected)
            {
                Plugin.Log.LogWarning("[SteamServerSpike] A2 — client CONNECTED to server identity; sending ping");
                Send(_clientConn, serverSide: false, "ping from client");
            }
            else if (state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally
                     || state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer)
            {
                Plugin.Log.LogError($"[SteamServerSpike] A2 FAIL (client): state={state} reason={s.m_info.m_eEndReason} '{s.m_info.m_szEndDebug}'");
                _clientActive = false;
            }
        }

        private static void TickClient()
        {
            if (_clientConn != HSteamNetConnection.Invalid) PumpConnection(serverSide: false, _clientConn);
            if (UnityEngine.Time.unscaledTime > _clientDeadline)
            {
                Plugin.Log.LogError("[SteamServerSpike] A2 FAIL (client) — no echo within 30s");
                _clientActive = false;
            }
        }

        // ------------------------------------------------------------------ shared plumbing

        private static void PumpConnection(bool serverSide, HSteamNetConnection conn)
        {
            int count = serverSide
                ? SteamGameServerNetworkingSockets.ReceiveMessagesOnConnection(conn, MsgPtrs, MsgPtrs.Length)
                : SteamNetworkingSockets.ReceiveMessagesOnConnection(conn, MsgPtrs, MsgPtrs.Length);
            for (int i = 0; i < count; i++)
            {
                try
                {
                    var msg = Marshal.PtrToStructure<SteamNetworkingMessage_t>(MsgPtrs[i]);
                    var buf = new byte[msg.m_cbSize];
                    Marshal.Copy(msg.m_pData, buf, 0, msg.m_cbSize);
                    string text = Encoding.UTF8.GetString(buf);
                    if (serverSide)
                    {
                        Plugin.Log.LogWarning($"[SteamServerSpike] A2 — SERVER RECEIVED '{text}' — echoing");
                        Send(conn, serverSide: true, "echo: " + text);
                    }
                    else
                    {
                        Plugin.Log.LogWarning($"[SteamServerSpike] A2 PASS — CLIENT RECEIVED '{text}' (full round trip over SDR)");
                        _clientActive = false;
                    }
                }
                finally { SteamNetworkingMessage_t.Release(MsgPtrs[i]); }
            }
        }

        private static void Send(HSteamNetConnection conn, bool serverSide, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                const int k_nSteamNetworkingSend_Reliable = 8;
                if (serverSide)
                    SteamGameServerNetworkingSockets.SendMessageToConnection(conn, handle.AddrOfPinnedObject(),
                        (uint)bytes.Length, k_nSteamNetworkingSend_Reliable, out _);
                else
                    SteamNetworkingSockets.SendMessageToConnection(conn, handle.AddrOfPinnedObject(),
                        (uint)bytes.Length, k_nSteamNetworkingSend_Reliable, out _);
            }
            finally { handle.Free(); }
        }
    }
}
