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

        public static void AddOut(int bytes)
        {
            BytesOut += bytes;
            MsgsOut++;
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
}
