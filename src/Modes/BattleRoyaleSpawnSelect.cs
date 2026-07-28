using System.Collections.Generic;
using System.Linq;
using PunkMultiverse.Core;
using PunkMultiverse.Protocol;
using PunkMultiverse.Transport;
using UnityEngine;

namespace PunkMultiverse.Modes
{
    /// <summary>
    /// Battle Royale drop selection: pick a BIOME and deploy onto a station there.
    ///
    /// NOTHING WAITS ON ANYONE. The window is entirely local — the match goes live on schedule and
    /// each player drops the instant they choose (Omar, 2026-07-28: "spawn as soon as they select,
    /// don't let other players hold the server"). An earlier version gated GO LIVE on everyone
    /// having chosen, which let one idle player keep the whole lobby staring at a loading screen;
    /// that is the opposite of what a drop screen is for. The 30s clock
    /// (<c>BrChooseSpawnSeconds</c>) starts when the screen appears and ends in a random region,
    /// because running out of time is a decision too, not a penalty and not a wait.
    ///
    /// The consequence is that a deploy IS a teleport — but a chosen one, which is the genre's own
    /// shape: you are not in the world until you drop. That is a different thing from the
    /// involuntary "spawn on a shared pad and get yanked away" this replaced.
    ///
    /// REGIONS ARE MAIN BIOMES. <c>Level.GetMainBiom</c>, not <c>GetBiom</c>: the latter includes
    /// sub-biomes and border noise, which would shatter the map into slivers nobody could point at.
    /// The option list is BUILT FROM STATIONS rather than from biomes, so a biome with nowhere to
    /// land cannot appear — Omar's "be careful not to display an option with no station" is
    /// structural here rather than a check that can be forgotten.
    ///
    /// SHARING IS ALLOWED ("let them choose and if they fight so be it"), which is what lets a
    /// deploy be instant: with no distinctness to enforce there is nothing for machines to agree
    /// on, so the station is picked locally and the host is told only so it can keep the heat map
    /// honest.
    ///
    /// A COORDINATOR NEVER PICKS. A dedicated server or sidecar has no ship; it tallies for the
    /// heat map and is otherwise skipped. Its clients choose normally.
    /// </summary>
    internal static class BattleRoyaleSpawnSelect
    {
        internal sealed class BiomeOption
        {
            internal byte BiomeId;
            internal string Name;          // friendly, for the button
            internal Color Color;          // the biome's own map colour
            internal readonly List<int> StationNetIds = new List<int>();
            internal int Picks;            // live tally from the host
        }

        private static readonly List<BiomeOption> Options = new List<BiomeOption>();
        private static readonly Dictionary<byte, byte> Choices = new Dictionary<byte, byte>(); // slot -> biomeId

        private static float _deadline = -1f;
        private static bool _closed;

        internal static IReadOnlyList<BiomeOption> AvailableOptions => Options;
        internal static bool LocalHasChosen { get; private set; }
        internal static byte LocalChoice { get; private set; }

        /// <summary>Seconds left to pick, or 0 when the window is not open.</summary>
        internal static float SecondsLeft =>
            _deadline < 0f ? 0f : Mathf.Max(0f, _deadline - Time.unscaledTime);

        /// <summary>True while this machine should be showing the drop screen: a Battle Royale run
        /// that is still loading, on a machine that actually flies a ship.</summary>
        internal static bool ShouldShow
        {
            get
            {
                var s = NetSession.Instance;
                return s != null && NetSession.Active
                       && s.LobbyMode == GameMode.BattleRoyale
                       && s.State == SessionState.InGame
                       && !NetConfig.IsCoordinator
                       && NetConfig.BrChooseSpawn.Value
                       && Options.Count > 0
                       && !_closed;
            }
        }

        public static void Reset()
        {
            Options.Clear();
            Choices.Clear();
            _deadline = -1f;
            _closed = false;
            LocalHasChosen = false;
            LocalChoice = 0;
        }

        // ---------------------------------------------------------------- the option list

        /// <summary>Classify every station by the main biome it stands in. Called once the local
        /// world is generated (both host and client build their own; the list is only used to draw
        /// buttons, and the host's assignment is authoritative).</summary>
        internal static void BuildOptions()
        {
            Options.Clear();
            try
            {
                var level = ServiceLocator.Get<Level>();
                var em = ServiceLocator.Get<EntityManager>();
                if (level == null || em == null) return;

                var byBiome = new Dictionary<byte, BiomeOption>();
                foreach (var station in em.GetEntitiesWithComponent<Station.Data>())
                {
                    if (station?.entity == null) continue;
                    if (!NetIds.TryGetNetId(station.entity.instanceId, out int netId)) continue;
                    var pos = Vector2Int.RoundToInt((Vector2)station.entity.position);
                    var biome = level.GetMainBiom(pos);
                    if (biome == null) continue;
                    if (!byBiome.TryGetValue(biome.id, out var option))
                    {
                        byBiome[biome.id] = option = new BiomeOption
                        {
                            BiomeId = biome.id,
                            Name = FriendlyName(biome.name),
                            Color = biome.mapColor,
                        };
                    }
                    option.StationNetIds.Add(netId);
                }

                // Stable order so the buttons do not shuffle between machines or frames.
                Options.AddRange(byBiome.Values.OrderBy(o => o.Name, System.StringComparer.Ordinal));
                Plugin.Log.LogInfo($"[BRDrop] {Options.Count} drop regions: " +
                    string.Join(", ", Options.Select(o => $"{o.Name}({o.StationNetIds.Count})")));
            }
            catch (System.Exception e)
            {
                Options.Clear();
                Plugin.Log.LogWarning($"[BRDrop] could not classify stations by biome: {e.Message} — " +
                    "drop selection is skipped and spawns fall back to the scatter");
            }
        }

        /// <summary>Asset names are internal shorthand ("Biom_IceCav", "Biom_lava_deep"). Players
        /// should see words. Underscores and camel humps become spaces, the redundant "Biom" prefix
        /// goes, and the abbreviations the artists actually used are spelled out.</summary>
        internal static string FriendlyName(string assetName)
        {
            if (string.IsNullOrEmpty(assetName)) return "Unknown";
            string s = assetName;
            foreach (var prefix in new[] { "Biom_", "Biome_", "Biom", "Biome" })
                if (s.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase))
                { s = s.Substring(prefix.Length); break; }
            s = s.Replace('_', ' ').Replace('-', ' ');
            // Split camelCase / PascalCase into words.
            var sb = new System.Text.StringBuilder(s.Length + 8);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (i > 0 && char.IsUpper(c) && !char.IsUpper(s[i - 1]) && s[i - 1] != ' ') sb.Append(' ');
                sb.Append(c);
            }
            s = sb.ToString();

            var words = s.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries)
                         .Select(ExpandWord)
                         .Where(w => w.Length > 0)
                         .ToArray();
            if (words.Length == 0) return "Unknown";
            return string.Join(" ", words);
        }

        /// <summary>Spell out the shorthand the level assets use. Anything not listed is simply
        /// title-cased, so an unknown name still reads as words rather than being mangled.</summary>
        private static string ExpandWord(string w)
        {
            switch (w.ToLowerInvariant())
            {
                case "cav": case "cave": case "cavern": case "caves": return "Caverns";
                case "mtn": case "mount": case "mountain": return "Mountains";
                case "ice": case "frozen": case "frost": return "Ice";
                case "lava": case "magma": return "Lava";
                case "des": case "desert": return "Desert";
                case "for": case "forest": return "Forest";
                case "swmp": case "swamp": return "Swamp";
                case "und": case "under": case "deep": return "Deep";
                case "surf": case "surface": return "Surface";
                case "crys": case "crystal": return "Crystal";
                case "fung": case "fungus": case "fungal": return "Fungal";
                case "tox": case "toxic": return "Toxic";
                case "main": return "Central";
                case "start": return "Landing";
                case "border": return "Border";
                default:
                    return w.Length <= 1 ? w.ToUpperInvariant()
                        : char.ToUpperInvariant(w[0]) + w.Substring(1).ToLowerInvariant();
            }
        }

        // ---------------------------------------------------------------- client side (deploying)

        /// <summary>Open the drop window on THIS machine. Called at go-live. The match is already
        /// running — nobody is waiting on this player — so the window is purely local: a screen, a
        /// clock, and a ship that has not deployed yet.</summary>
        internal static void OpenWindow()
        {
            _closed = false;
            LocalHasChosen = false;
            Deployed = false;
            if (NetConfig.IsCoordinator || !NetConfig.BrChooseSpawn.Value) { _closed = true; return; }
            if (Options.Count == 0) BuildOptions();
            if (Options.Count == 0)
            {
                _closed = true;
                Plugin.Log.LogWarning("[BRDrop] no station-bearing biomes — dropping by the scatter instead");
                return;
            }
            _deadline = Time.unscaledTime + Mathf.Max(5f, NetConfig.BrChooseSpawnSeconds.Value);
            Plugin.Log.LogInfo($"[BRDrop] drop window open — {NetConfig.BrChooseSpawnSeconds.Value:0}s, " +
                $"{Options.Count} regions");
        }

        /// <summary>True once this player has actually dropped into the world.</summary>
        internal static bool Deployed { get; private set; }

        /// <summary>Local player picked a region: deploy IMMEDIATELY. Omar, 2026-07-28: "spawn as
        /// soon as they select — don't let other players hold the server." The host is told only so
        /// it can keep the heat map honest; nothing waits on its reply.</summary>
        internal static void Choose(byte biomeId)
        {
            if (_closed || Deployed) return;
            LocalHasChosen = true;
            LocalChoice = biomeId;

            var session = NetSession.Instance;
            if (session != null)
            {
                if (session.IsHost) { Choices[(byte)session.LocalSlot] = biomeId; BroadcastTally(session); }
                else
                {
                    var w = new NetWriter(8);
                    new SpawnChoiceMsg { BiomeId = biomeId }.Write(w);
                    session.SendToAll(NetChannel.Control, w.ToSegment(), reliable: true);
                }
            }
            Deploy(biomeId, "chosen");
        }

        /// <summary>Ticked while the window is open: nobody is held past the clock. Running out of
        /// time is a decision too — a random region, not a penalty and not a wait.</summary>
        internal static void Tick()
        {
            if (_closed || Deployed || _deadline < 0f) return;
            if (Time.unscaledTime < _deadline) return;
            byte biomeId = Options[UnityEngine.Random.Range(0, Options.Count)].BiomeId;
            LocalHasChosen = true;
            LocalChoice = biomeId;
            var session = NetSession.Instance;
            if (session != null && !session.IsHost)
            {
                var w = new NetWriter(8);
                new SpawnChoiceMsg { BiomeId = biomeId }.Write(w);
                session.SendToAll(NetChannel.Control, w.ToSegment(), reliable: true);
            }
            else if (session != null) { Choices[(byte)session.LocalSlot] = biomeId; BroadcastTally(session); }
            Deploy(biomeId, "timed out");
        }

        /// <summary>Put the ship on a station in the chosen region and hand control back.
        ///
        /// The station is picked LOCALLY. Players are allowed to share a pad, so there is nothing
        /// left for machines to agree on — which is what lets a deploy be instant instead of a round
        /// trip to the host and back.
        ///
        /// Control is forced on here rather than left to the opening cinematic: a player who
        /// deploys while it is still running would otherwise sit frozen on their new pad until it
        /// finishes. If the cinematic does complete later it sets the same values again.</summary>
        private static void Deploy(byte biomeId, string why)
        {
            _closed = true;
            Deployed = true;
            try
            {
                var option = Options.FirstOrDefault(o => o.BiomeId == biomeId) ?? Options[0];
                if (option.StationNetIds.Count == 0) return;
                int netId = option.StationNetIds[UnityEngine.Random.Range(0, option.StationNetIds.Count)];
                Sync.ShipSync.TeleportLocalShip(netId);

                var ship = Sync.ShipSync.LocalShip;
                if (ship != null)
                {
                    if (ship.shipInput != null) ship.shipInput.enabled = true;
                    if (ship.Rigidbody != null) ship.Rigidbody.bodyType = RigidbodyType2D.Dynamic;
                    if (ship.Crosshair != null) ship.Crosshair.Visible = true;
                    ship.UnlockCamera(0f);
                    ship.SetHeadlightsEnabled(true);
                }
                Plugin.Log.LogInfo($"[BRDrop] deployed to {option.Name} station #{netId} ({why})");
                UI.Toast.Show($"DROPPING INTO {option.Name.ToUpperInvariant()}", 4f);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"[BRDrop] deploy failed: {e.Message} — the scatter will place this ship");
            }
        }

        public static void ApplyTally(SpawnTallyMsg msg)
        {
            if (msg.BiomeIds == null) return;
            foreach (var option in Options) option.Picks = 0;
            for (int i = 0; i < msg.BiomeIds.Length; i++)
            {
                var option = Options.FirstOrDefault(o => o.BiomeId == msg.BiomeIds[i]);
                if (option != null) option.Picks = msg.Counts[i];
            }
        }
        // ---------------------------------------------------------------- host side (tally only)

        public static void ApplyChoice(SpawnChoiceMsg msg, byte fromSlot, NetSession session)
        {
            if (session == null || !session.IsHost) return;
            Choices[fromSlot] = msg.BiomeId;
            BroadcastTally(session);
        }

        /// <summary>The host's ONLY job here is the heat map. It does not assign stations and does
        /// not gate anything: a player picks, and that player deploys. Holding the match until
        /// everyone had chosen let one idle player keep the lobby waiting, which is the opposite of
        /// what a drop screen is for.</summary>
        private static void BroadcastTally(NetSession session)
        {
            var counts = new Dictionary<byte, byte>();
            foreach (var kv in Choices)
            {
                counts.TryGetValue(kv.Value, out byte n);
                counts[kv.Value] = (byte)Mathf.Min(255, n + 1);
            }
            var msg = new SpawnTallyMsg
            {
                BiomeIds = counts.Keys.ToArray(),
                Counts = counts.Values.ToArray(),
            };
            ApplyTally(msg); // the host draws the same heat
            var w = new NetWriter(64);
            msg.Write(w);
            session.SendToAll(NetChannel.Control, w.ToSegment(), reliable: true);
        }
    }
}