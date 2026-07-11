using PunkMultiverse.Core;
using PunkMultiverse.Protocol;

namespace PunkMultiverse.Sync
{
    /// <summary>
    /// Fog is host-authoritative and NOT synced as state — see <see cref="Patches.FogHostAuthority"/>.
    ///
    /// Level.fogLevels is the live state of a cellular-automaton gas simulation, not static
    /// exploration data. This class used to diff fogLevels every few seconds and reliably
    /// broadcast the changed cells, but because every machine runs its own fog simulation that
    /// created a self-reinforcing ping-pong: each side wrote the other's diff, its next sim tick
    /// re-diverged those cells, and they shipped back — thousands of cells per cycle, growing as
    /// fog spread, until the reliable channel saturated and Steam timed the connection out.
    ///
    /// Fog's only observable effect is the SetCell terrain conversions it makes, and WorldSync
    /// already captures + syncs those. So the client simulation is suppressed
    /// (FogHostAuthority) and this sync is a no-op. The FogDiff message type stays defined for
    /// wire compatibility; nothing sends or applies it anymore.
    /// </summary>
    internal static class FogSync
    {
        public static void Reset() { }

        /// <summary>No-op: fog is host-simulated; terrain results flow through WorldSync.</summary>
        public static void Tick(NetSession session) { }

        /// <summary>No-op: legacy inbound FogDiff messages are ignored (kept for wire compat).</summary>
        public static void Apply(NetReader reader) { }
    }
}
