using System;
using System.IO;
using Steamworks;

namespace PunkMultiverse.Transport
{
    /// <summary>
    /// Process-level lifecycle for an ANONYMOUS Steam GAME SERVER identity — the mechanism real
    /// dedicated servers use, proven for this appid by spike A. A coordinator running the
    /// <see cref="SteamServerTransport"/> logs on here once; the transport rides the resulting
    /// server SteamID. Init-once: GameServer.Init cannot be cleanly re-run in-process, so a
    /// stopped+restarted session REUSES the same logged-on identity.
    ///
    /// Ordering facts (both burned spike attempt 1): the appid resolves ONLY via the SteamAppId/
    /// SteamGameId env vars (no steam_appid.txt in this install), and Steamworks.NET's callback
    /// dispatcher exists only AFTER GameServer.Init — register callbacks after Init or
    /// RunCallbacks self-poisons.
    /// </summary>
    internal static class GameServerBootstrap
    {
        internal const string IdFileName = "coordinator-steamid.txt";

        public static bool InitAttempted { get; private set; }
        public static bool InitOk { get; private set; }
        public static bool LoggedOn { get; private set; }
        public static ulong ServerSteamId { get; private set; }

        private static Callback<SteamServersConnected_t> _cbConnected;
        private static Callback<SteamServerConnectFailure_t> _cbConnectFail;
        private static Callback<SteamServersDisconnected_t> _cbDisconnected;
        private static float _logonDeadline;
        private static bool _logonTimedOut;

        /// <summary>Whoever wrote the id file removes it on shutdown so a stale id never
        /// misdirects a fresh sidecar join.</summary>
        internal static string IdFilePath => Path.Combine(ModFolder.Dir, IdFileName);

        /// <summary>Start GameServer.Init + LogOnAnonymous once. Returns false only if Init itself
        /// failed (a hard, non-recoverable failure the caller reports); logon is async — poll
        /// <see cref="LoggedOn"/> / <see cref="LogonFailed"/>.</summary>
        public static bool EnsureStarted()
        {
            if (InitAttempted) return InitOk;
            InitAttempted = true;
            try
            {
                Environment.SetEnvironmentVariable("SteamAppId", NetConfig.SteamAppId.Value.ToString());
                Environment.SetEnvironmentVariable("SteamGameId", NetConfig.SteamAppId.Value.ToString());

                // eServerModeAuthentication is required for a real anonymous SteamID
                // (NoAuthentication never contacts Steam). Ports are nominal — SDR does the routing.
                if (!GameServer.Init(0, 27015, 27016, EServerMode.eServerModeAuthentication, Plugin.Version))
                {
                    Plugin.Log.LogError("[GameServer] GameServer.Init returned false — SteamServer transport unavailable");
                    return false;
                }
                InitOk = true;

                _cbConnected = Callback<SteamServersConnected_t>.CreateGameServer(_ =>
                {
                    LoggedOn = true;
                    ServerSteamId = SteamGameServer.GetSteamID().m_SteamID;
                    WriteIdFile(ServerSteamId);
                    Plugin.Log.LogWarning($"[GameServer] logged on — server id {ServerSteamId} (join code)");
                });
                _cbConnectFail = Callback<SteamServerConnectFailure_t>.CreateGameServer(f =>
                    Plugin.Log.LogError($"[GameServer] logon failure result={f.m_eResult} stillRetrying={f.m_bStillRetrying}"));
                _cbDisconnected = Callback<SteamServersDisconnected_t>.CreateGameServer(d =>
                {
                    // SDR sessions usually survive a brief backend blip; do not tear the transport
                    // down here — if connections actually die, the peer-timeout path handles it.
                    LoggedOn = false;
                    Plugin.Log.LogWarning($"[GameServer] disconnected from Steam backend: {d.m_eResult} (connections may persist)");
                });

                SteamGameServer.SetProduct("PUNK");
                SteamGameServer.SetGameDescription("PunkMultiverse coordinator");
                SteamGameServer.LogOnAnonymous();
                RaiseSendLimits();
                _logonDeadline = UnityEngine.Time.unscaledTime + 30f;
                Plugin.Log.LogInfo("[GameServer] init OK, anonymous logon requested");
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[GameServer] init threw: {e.Message} — SteamServer transport unavailable");
                return false;
            }
        }

        public static bool LogonFailed => _logonTimedOut;

        /// <summary>Pump game-server callbacks. Called every frame from NetSession.Update while a
        /// coordinator is up (logon completes before the transport reports IsRunning, so this can't
        /// live in the transport's Poll).</summary>
        public static void Pump()
        {
            if (!InitOk) return;
            try { GameServer.RunCallbacks(); } catch { }
            if (!LoggedOn && !_logonTimedOut && _logonDeadline > 0f
                && UnityEngine.Time.unscaledTime > _logonDeadline)
            {
                _logonTimedOut = true;
                Plugin.Log.LogError("[GameServer] no logon within 30s — SteamServer transport failed");
            }
        }

        private static void WriteIdFile(ulong id)
        {
            try { File.WriteAllText(IdFilePath, id.ToString()); }
            catch (Exception e) { Plugin.Log.LogWarning($"[GameServer] could not write id file: {e.Message}"); }
        }

        /// <summary>Read the coordinator id a local sidecar published (the hosting player's join
        /// path). 0 = not yet written.</summary>
        public static ulong ReadPublishedId()
        {
            try
            {
                if (File.Exists(IdFilePath) && ulong.TryParse(File.ReadAllText(IdFilePath).Trim(), out ulong id))
                    return id;
            }
            catch { }
            return 0;
        }

        private static void RaiseSendLimits()
        {
            try
            {
                SetGlobalInt32(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax, 1024 * 1024);
                SetGlobalInt32(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendBufferSize, 2 * 1024 * 1024);
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[GameServer] could not raise send limits: {e.Message}"); }
        }

        private static void SetGlobalInt32(ESteamNetworkingConfigValue key, int value)
        {
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(value,
                System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                SteamGameServerNetworkingUtils.SetConfigValue(key,
                    ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global,
                    IntPtr.Zero,
                    ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32,
                    handle.AddrOfPinnedObject());
            }
            finally { handle.Free(); }
        }

        /// <summary>Process exit: log off + shut down the game server (mirror SteamBootstrap —
        /// a live server identity torn down under the loader lock can deadlock the process).</summary>
        public static void Shutdown()
        {
            if (!InitOk) return;
            try { File.Delete(IdFilePath); } catch { }
            try
            {
                SteamGameServer.LogOff();
                GameServer.Shutdown();
                Plugin.Log.LogInfo("[GameServer] logged off + shut down");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[GameServer] shutdown failed: {e.Message}"); }
            InitOk = false;
            LoggedOn = false;
        }
    }
}
