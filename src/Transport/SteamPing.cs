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
        private static bool _relayStarted;
        private static int _waits;

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
                // Ask for the relay network explicitly, once. Polling CheckPingDataUpToDate is not
                // reliable enough to make the measurement START -- PUNK Nexus proved that on the
                // browsing side, where the ping column stayed empty indefinitely until this call
                // was added. A host that opens SteamNetworkingSockets brings the relay up as a side
                // effect and never notices, but that is a side effect of the transport, not
                // something this class should depend on: a LiteNetLib or dedicated host publishes
                // the same listing and gets no such favour.
                if (!_relayStarted)
                {
                    SteamNetworkingUtils.InitRelayNetworkAccess();
                    _relayStarted = true;
                }

                // Returns false while the measurement is still in flight, which is not an error.
                if (!SteamNetworkingUtils.CheckPingDataUpToDate(MaxAgeSeconds))
                {
                    // Say so once at 10 and once at 100 tries rather than every 3 seconds. Silence
                    // here is what made an empty ping column impossible to explain: nothing in the
                    // log distinguished "still measuring" from "never asked".
                    _waits++;
                    if (_waits == 10 || _waits == 100)
                        Plugin.Log.LogInfo($"[Lobby] still waiting on Steam ping data ({_waits} tries); " +
                                           "browser rows will show no ping until it resolves.");
                    return null;
                }

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
        public static void Reset() { _cached = null; _waits = 0; }
    }
}
