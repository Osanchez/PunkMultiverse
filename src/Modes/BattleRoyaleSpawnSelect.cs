using System.Collections.Generic;
using System.Linq;
using PunkMultiverse.Core;
using PunkMultiverse.Protocol;
using PunkMultiverse.Transport;
using UnityEngine;

namespace PunkMultiverse.Modes
{
    /// <summary>
    /// Battle Royale drop selection: players choose which BIOME to drop into before anyone is
    /// placed, and land on a station there.
    ///
    /// WHERE IT LIVES, and why it has to. The choice must resolve BEFORE ships are placed, or the
    /// mode is back to spawning everyone on one pad and teleporting them apart — the thing
    /// Patches/BattleRoyaleSpawn.cs exists to remove. The only moment where the world exists but no
    /// ship does is the GO-LIVE BARRIER: clients have generated and verified the world, the host has
    /// every checksum, and nothing has been spawned yet. So selection is a gate inside that barrier
    /// — the host holds GO LIVE until every player has picked or the clock runs out.
    ///
    /// REGIONS ARE MAIN BIOMES. <c>Level.GetMainBiom</c>, not <c>GetBiom</c>: the latter includes
    /// sub-biomes and border noise, which would shatter the map into slivers nobody could point at.
    /// The option list is BUILT FROM STATIONS rather than from biomes, so a biome with nowhere to
    /// land cannot appear — Omar's "be careful not to display an option with no station" is
    /// structural here rather than a check that can be forgotten.
    ///
    /// SHARING IS ALLOWED (Omar, 2026-07-28: "let them choose and if they fight so be it"). That
    /// removes the only reason the assignment had to be derived identically everywhere, so the host
    /// simply decides and broadcasts it.
    ///
    /// A COORDINATOR NEVER PICKS. A dedicated server or sidecar has no ship; it arbitrates, holds
    /// the clock, and is skipped entirely. Its clients still choose normally.
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
        private static readonly Dictionary<byte, int> Assignment = new Dictionary<byte, int>(); // slot -> station netId

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
                       && s.State == SessionState.Loading
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
            Assignment.Clear();
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

        // ---------------------------------------------------------------- client side

        /// <summary>Local player picked a region. Sent to the host; the host owns the outcome.</summary>
        internal static void Choose(byte biomeId)
        {
            var session = NetSession.Instance;
            if (session == null || _closed) return;
            LocalHasChosen = true;
            LocalChoice = biomeId;
            if (session.IsHost) { RecordChoice(session, (byte)session.LocalSlot, biomeId); return; }
            var w = new NetWriter(8);
            new SpawnChoiceMsg { BiomeId = biomeId }.Write(w);
            session.SendToAll(NetChannel.Control, w.ToSegment(), reliable: true);
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

        public static void ApplyAssignment(SpawnAssignMsg msg)
        {
            Assignment.Clear();
            if (msg.Slots == null) return;
            for (int i = 0; i < msg.Slots.Length; i++) Assignment[msg.Slots[i]] = msg.StationNetIds[i];
            _closed = true;
            Plugin.Log.LogInfo($"[BRDrop] assignment received for {Assignment.Count} player(s)");
        }

        /// <summary>The station this slot drops on, or 0 if selection never ran (the caller then
        /// falls back to the deterministic scatter).</summary>
        internal static int StationFor(byte slot) => Assignment.TryGetValue(slot, out int id) ? id : 0;

        // ---------------------------------------------------------------- host side

        public static void ApplyChoice(SpawnChoiceMsg msg, byte fromSlot, NetSession session)
            => RecordChoice(session, fromSlot, msg.BiomeId);

        private static void RecordChoice(NetSession session, byte slot, byte biomeId)
        {
            if (session == null || !session.IsHost || _closed) return;
            Choices[slot] = biomeId;
            BroadcastTally(session);
            session.PokeGoLive(); // the last chooser should not wait on the timer
        }

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

        /// <summary>Host: may GO LIVE proceed? Opens the window on first call, then holds until
        /// every ship-flying player has chosen or the clock expires. Anything unexpected — no
        /// options, the feature off — returns true immediately rather than wedging the barrier.</summary>
        internal static bool HostReadyToGoLive(NetSession session, IEnumerable<NetPlayer> present)
        {
            if (session == null || !session.IsHost) return true;
            if (session.LobbyMode != GameMode.BattleRoyale) return true;
            if (!NetConfig.BrChooseSpawn.Value) return true;
            if (_closed) return true;

            if (Options.Count == 0) BuildOptions();
            if (Options.Count == 0)
            {
                _closed = true; // nothing to choose between; the scatter handles placement
                Plugin.Log.LogWarning("[BRDrop] no station-bearing biomes found — skipping drop selection");
                return true;
            }

            if (_deadline < 0f)
            {
                _deadline = Time.unscaledTime + Mathf.Max(5f, NetConfig.BrChooseSpawnSeconds.Value);
                Plugin.Log.LogInfo($"[BRDrop] drop selection open — {NetConfig.BrChooseSpawnSeconds.Value:0}s");
                BroadcastTally(session); // clients get an empty heat map to start from
            }

            // Only players who actually fly need to choose; a coordinator never sees the screen.
            var choosers = present.Where(p => p != null && p.Connected && !p.IsCoordinator).ToList();
            bool everyone = choosers.All(p => Choices.ContainsKey(p.Slot));
            if (!everyone && Time.unscaledTime < _deadline) return false;

            CloseAndAssign(session, choosers, everyone);
            return true;
        }

        /// <summary>Settle it: everyone gets a station in the biome they picked, and anyone who did
        /// not pick gets a random region — the timer is a decision, not a punishment.</summary>
        private static void CloseAndAssign(NetSession session, List<NetPlayer> choosers, bool everyoneChose)
        {
            _closed = true;
            Assignment.Clear();
            var rnd = new System.Random(session.CurrentRunSeed ^ 0x44524F50);

            foreach (var p in choosers)
            {
                BiomeOption option = null;
                if (Choices.TryGetValue(p.Slot, out byte biomeId))
                    option = Options.FirstOrDefault(o => o.BiomeId == biomeId);
                if (option == null || option.StationNetIds.Count == 0)
                    option = Options[rnd.Next(Options.Count)];
                // Sharing a pad is allowed, so this is a plain random pick with no de-duplication.
                int netId = option.StationNetIds[rnd.Next(option.StationNetIds.Count)];
                Assignment[p.Slot] = netId;
                Plugin.Log.LogInfo($"[BRDrop] P{p.Slot + 1} -> {option.Name} station #{netId}" +
                    (Choices.ContainsKey(p.Slot) ? "" : " (no choice made — random)"));
            }

            var msg = new SpawnAssignMsg
            {
                Slots = Assignment.Keys.ToArray(),
                StationNetIds = Assignment.Values.ToArray(),
            };
            var w = new NetWriter(128);
            msg.Write(w);
            session.SendToAll(NetChannel.Control, w.ToSegment(), reliable: true);
            Plugin.Log.LogInfo($"[BRDrop] selection closed ({(everyoneChose ? "everyone chose" : "timer expired")}) — " +
                $"{Assignment.Count} assignment(s) sent");
        }
    }
}
