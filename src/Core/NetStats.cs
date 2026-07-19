using PunkMultiverse.Transport;

namespace PunkMultiverse.Core
{
    /// <summary>Per-slot session stats shown on the scoreboard (kill credit comes from
    /// ENTITY_KILLED's killerSlot, deaths from ship life events) plus cumulative network
    /// telemetry — the F11 overlay computes rates from deltas between samples. Optimize from
    /// measurements, not intuition.</summary>
    public static class NetStats
    {
        public static readonly int[] Kills = new int[NetSession.MaxPlayers];
        public static readonly int[] Deaths = new int[NetSession.MaxPlayers];

        public static long BytesIn, BytesOut;
        public static long MsgsIn, MsgsOut;
        // Byte-plane split of outbound traffic. The State channel is the elastic presentation plane
        // (position/rotation snapshots, droppable); Control/Events/Combat are the correctness plane
        // (reliable authoritative mutations that must arrive). A ballooning presentation share under
        // load is where receiver byte budgets (WS7.2) should shed first.
        public static long BytesOutCorrectness, BytesOutPresentation;
        public static readonly long[] BytesInByType = new long[128]; // indexed by MsgType
        public static int AuthFlips;    // ownership changes applied (host)
        public static int AuthReleases; // releases handled (host) / sent (client)

        public static void Reset()
        {
            for (int i = 0; i < NetSession.MaxPlayers; i++)
            {
                Kills[i] = 0;
                Deaths[i] = 0;
            }
            BytesIn = BytesOut = MsgsIn = MsgsOut = 0;
            BytesOutCorrectness = BytesOutPresentation = 0;
            for (int i = 0; i < BytesInByType.Length; i++) BytesInByType[i] = 0;
            AuthFlips = 0;
            AuthReleases = 0;
        }

        public static void AddIn(byte type, int bytes)
        {
            BytesIn += bytes;
            MsgsIn++;
            if (type < BytesInByType.Length) BytesInByType[type] += bytes;
        }

        public static void AddOut(NetChannel channel, int bytes)
        {
            BytesOut += bytes;
            MsgsOut++;
            if (channel == NetChannel.State) BytesOutPresentation += bytes;
            else BytesOutCorrectness += bytes;
        }

        public static void AddKill(int slot)
        {
            if (slot >= 0 && slot < Kills.Length) Kills[slot]++;
        }

        public static void AddDeath(int slot)
        {
            if (slot >= 0 && slot < Deaths.Length) Deaths[slot]++;
        }
    }

    /// <summary>WS8.2 sequencer counters: accepted-send and received message counts per (peer,
    /// channel). Because Events/Combat are reliable AND ordered per channel, a periodic checkpoint
    /// carrying the sender's count is a barrier — on its arrival the receiver must have at least
    /// that many messages, or something was silently lost (outbox overflow drop, migration gap).
    /// Detection triggers a targeted SendEventCatchUp replay: the correctness plane becomes a
    /// gap-detected log instead of trusting per-channel delivery end-to-end.</summary>
    public static class NetSeq
    {
        private static readonly System.Collections.Generic.Dictionary<(ulong peer, byte channel), uint> Sent
            = new System.Collections.Generic.Dictionary<(ulong, byte), uint>();
        private static readonly System.Collections.Generic.Dictionary<(ulong peer, byte channel), uint> Received
            = new System.Collections.Generic.Dictionary<(ulong, byte), uint>();

        public static void NoteSent(ulong peer, NetChannel channel)
        {
            var key = (peer, (byte)channel);
            Sent.TryGetValue(key, out uint n);
            Sent[key] = n + 1;
        }

        public static void NoteReceived(ulong peer, NetChannel channel)
        {
            var key = (peer, (byte)channel);
            Received.TryGetValue(key, out uint n);
            Received[key] = n + 1;
        }

        public static uint SentTo(ulong peer, NetChannel channel)
            => Sent.TryGetValue((peer, (byte)channel), out uint n) ? n : 0;

        public static uint ReceivedFrom(ulong peer, NetChannel channel)
            => Received.TryGetValue((peer, (byte)channel), out uint n) ? n : 0;

        /// <summary>Peer ids can be reused across connections (loopback small ints) — stale counts
        /// would misfire immediately. Reset the pair on connect AND disconnect.</summary>
        public static void ResetPeer(ulong peer)
        {
            for (byte c = 0; c < 4; c++)
            {
                Sent.Remove((peer, c));
                Received.Remove((peer, c));
            }
        }

        public static void Reset()
        {
            Sent.Clear();
            Received.Clear();
        }
    }
}
