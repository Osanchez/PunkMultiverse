using System;
using System.IO;

namespace PunkMultiverse.Core
{
    /// <summary>
    /// The rejoin target: which session this machine was last playing in. Written at every
    /// go-live (host and clients), so a disconnect, crash, or mid-run quit leaves a way back —
    /// the CONNECT screen offers REJOIN only after a live probe confirms the session still
    /// exists (see NetSession.ProbeRejoinTarget). Cleared when the player is kicked or
    /// deliberately leaves from the lobby (both mean "don't offer this session again").
    /// Replaces the old save-based RESUME LAST RUN feature.
    /// </summary>
    internal static class RejoinMemory
    {
        private const int FormatVersion = 1;
        private const double MaxAgeHours = 24; // a day-old record is stale even if a lobby answers

        public sealed class Record
        {
            public bool Steam;
            public ulong LobbyId;    // Steam: lobby SteamID64
            public string Address;   // loopback: "host:port" the session ran on
        }

        private static string PathFor() => Path.Combine(ModFolder.Dir, "lastsession.txt");

        public static void Remember(bool steam, ulong lobbyId, string address)
        {
            try
            {
                DeleteLegacyRunSave();
                File.WriteAllLines(PathFor(), new[]
                {
                    FormatVersion.ToString(),
                    steam ? "steam" : "loopback",
                    lobbyId.ToString(),
                    address ?? "",
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
                });
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Rejoin] remember failed: {e.Message}"); }
        }

        public static bool TryLoad(out Record record)
        {
            record = null;
            try
            {
                if (!File.Exists(PathFor())) return false;
                var lines = File.ReadAllLines(PathFor());
                if (lines.Length < 5 || !int.TryParse(lines[0], out int ver) || ver != FormatVersion) return false;
                if (!long.TryParse(lines[4], out long savedAt)) return false;
                if ((DateTimeOffset.UtcNow.ToUnixTimeSeconds() - savedAt) > MaxAgeHours * 3600) return false;
                bool steam = lines[1] == "steam";
                if (!ulong.TryParse(lines[2], out ulong lobbyId)) return false;
                if (steam && lobbyId == 0) return false;
                if (!steam && string.IsNullOrWhiteSpace(lines[3])) return false;
                record = new Record { Steam = steam, LobbyId = lobbyId, Address = lines[3] };
                return true;
            }
            catch { return false; }
        }

        public static void Clear()
        {
            try { if (File.Exists(PathFor())) File.Delete(PathFor()); }
            catch (Exception e) { Plugin.Log.LogWarning($"[Rejoin] clear failed: {e.Message}"); }
        }

        /// <summary>One-time cleanup of the removed resume feature's save file.</summary>
        private static void DeleteLegacyRunSave()
        {
            try
            {
                var legacy = Path.Combine(ModFolder.Dir, "lastrun.bin");
                if (File.Exists(legacy)) File.Delete(legacy);
            }
            catch { }
        }
    }
}
