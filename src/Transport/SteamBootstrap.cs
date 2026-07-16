using System;
using Steamworks;

namespace PunkMultiverse.Transport
{
    /// <summary>
    /// The game's own SteamManager only initializes Steam when launched through Steam. For direct
    /// Punk.exe launches (dev two-instance testing) we initialize SteamAPI ourselves — the appid
    /// comes from the SteamAppId env var we set in-process. When we self-init, nobody else pumps
    /// callbacks, so Pump() must be called every frame (NetSession does).
    /// </summary>
    internal static class SteamBootstrap
    {
        public static bool Available { get; private set; }
        public static bool SelfInitialized { get; private set; }

        public static void EnsureInitialized()
        {
            if (Available) return;
            try
            {
                var id = SteamUser.GetSteamID();
                Available = id.IsValid();
                if (Available)
                {
                    Plugin.Log.LogInfo($"[Steam] game initialized Steam, identity {id.m_SteamID}");
                    RaiseSendLimits();
                    return;
                }
            }
            catch
            {
                // Game didn't init (direct launch) — try ourselves.
            }

            try
            {
                Environment.SetEnvironmentVariable("SteamAppId", NetConfig.SteamAppId.Value.ToString());
                Environment.SetEnvironmentVariable("SteamGameId", NetConfig.SteamAppId.Value.ToString());
                if (SteamAPI.Init())
                {
                    Available = true;
                    SelfInitialized = true;
                    Plugin.Log.LogInfo($"[Steam] self-initialized (appid {NetConfig.SteamAppId.Value}), identity {SteamUser.GetSteamID().m_SteamID}");
                    RaiseSendLimits();
                }
                else
                {
                    Plugin.Log.LogWarning("[Steam] SteamAPI.Init failed — is Steam running? Steam transport unavailable.");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Steam] init failed: {e.Message} — Steam transport unavailable.");
            }
        }

        /// <summary>Terrain catch-up streams push far more reliable data than the Steam
        /// defaults expect (256 KB/s rate, 512 KB buffer). Raise both so a stream and live
        /// gameplay traffic coexist; the streamer still paces itself off send backpressure.</summary>
        private static void RaiseSendLimits()
        {
            try
            {
                SetGlobalInt32(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendRateMax, 1024 * 1024);
                SetGlobalInt32(ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendBufferSize, 2 * 1024 * 1024);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Steam] could not raise send limits: {e.Message}");
            }
        }

        private static void SetGlobalInt32(ESteamNetworkingConfigValue key, int value)
        {
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(value,
                System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                SteamNetworkingUtils.SetConfigValue(key,
                    ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global,
                    IntPtr.Zero,
                    ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32,
                    handle.AddrOfPinnedObject());
            }
            finally { handle.Free(); }
        }

        /// <summary>Call once per frame; pumps callbacks only when we own the Steam init.</summary>
        public static void Pump()
        {
            if (SelfInitialized || NetConfig.PumpSteamCallbacks.Value)
            {
                try { SteamAPI.RunCallbacks(); } catch { }
            }
        }

        /// <summary>Whoever initializes SteamAPI must shut it down before process exit (the
        /// game's own SteamManager.OnDestroy does exactly this for Steam launches). A
        /// still-running steamclient64 tears down inside DLL_PROCESS_DETACH under the loader
        /// lock, which intermittently deadlocks — the windowless Punk.exe that stays in Task
        /// Manager after closing a direct-launch instance.</summary>
        public static void Shutdown()
        {
            if (!SelfInitialized) return;
            SelfInitialized = false;
            Available = false;
            try
            {
                SteamAPI.Shutdown();
                Plugin.Log.LogInfo("[Steam] self-initialized API shut down");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[Steam] shutdown failed: {e.Message}");
            }
        }
    }
}
