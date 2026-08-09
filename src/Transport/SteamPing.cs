using Steamworks;

namespace PunkMultiverse.Transport
{
    /// <summary>
    /// The host's network location, as an opaque string a browsing client can turn into an
    /// estimated ping — without either machine sending the other a single packet.
    ///
    /// This is how a server browser shows latency for sessions reached over Steam's relay, where
    /// there is no host address to ping and deliberately so. Valve measures every client's latency
    /// to its own relay points of presence and hands back a blob describing where you sit on that
    /// map; two such blobs can be compared offline to estimate the route between them. So the host
    /// publishes its blob once in lobby metadata, and every browsing client estimates its own ping
    /// locally. Nobody probes anybody.
    /// </summary>
    internal static class SteamPing
    {
        /// <summary>How stale the local measurement may be before Steam re-measures it.</summary>
        private const float MaxAgeSeconds = 600f;

        private static string _cached;

        /// <summary>
        /// The local ping location, or null while Steam is still measuring.
        ///
        /// Null is the ordinary answer for the first seconds after boot — the measurement needs a
        /// round of probes to Valve's relays — so callers must treat it as "not yet" and ask again,
        /// never as "unsupported".
        /// </summary>
        public static string LocalLocation()
        {
            if (_cached != null) return _cached;

            try
            {
                // Kicks off the measurement if it has not run or has gone stale. Returns false while
                // it is still in flight, which is not an error.
                if (!SteamNetworkingUtils.CheckPingDataUpToDate(MaxAgeSeconds)) return null;

                SteamNetworkPingLocation_t location;
                if (SteamNetworkingUtils.GetLocalPingLocation(out location) < 0f) return null;

                string text;
                SteamNetworkingUtils.ConvertPingLocationToString(ref location, out text, 1024);
                if (string.IsNullOrEmpty(text)) return null;

                _cached = text;
                Plugin.Log.LogInfo($"[Lobby] ping location resolved ({text.Length} chars)");
                return _cached;
            }
            catch (System.Exception e)
            {
                // Steam not initialized, or an older client without relay support. A listing without
                // a ping location is still a perfectly good listing.
                Plugin.Log.LogWarning($"[Lobby] no ping location available: {e.Message}");
                return null;
            }
        }

        /// <summary>Drops the cached location so the next publish re-reads it (session teardown).</summary>
        public static void Reset() => _cached = null;
    }
}
