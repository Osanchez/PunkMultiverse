namespace PunkMultiverse.Core
{
    /// <summary>Per-slot session stats shown on the scoreboard. Kill credit comes from
    /// ENTITY_KILLED's killerSlot (deduped by netId on every peer), deaths from ship life events.</summary>
    public static class NetStats
    {
        public static readonly int[] Kills = new int[NetSession.MaxPlayers];
        public static readonly int[] Deaths = new int[NetSession.MaxPlayers];

        public static void Reset()
        {
            for (int i = 0; i < NetSession.MaxPlayers; i++)
            {
                Kills[i] = 0;
                Deaths[i] = 0;
            }
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
