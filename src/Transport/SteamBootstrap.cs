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

        /// <summary>Call once per frame; pumps callbacks only when we own the Steam init.</summary>
        public static void Pump()
        {
            if (SelfInitialized || NetConfig.PumpSteamCallbacks.Value)
            {
                try { SteamAPI.RunCallbacks(); } catch { }
            }
        }
    }
}
