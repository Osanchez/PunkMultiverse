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
            internal readonly List<Vector2> StationPositions = new List<Vector2>();
            /// <summary>Other biome ids merged into this row (Flesh Solid, Flesh Techno -> Flesh).
            /// A tally keyed on any of them belongs to this option.</summary>
            internal readonly List<byte> AliasBiomeIds = new List<byte>();
            internal int Picks;            // live tally from the host

            internal bool Covers(byte id) => id == BiomeId || AliasBiomeIds.Contains(id);
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
            Deployed = false;
            _protectedUntil = -1f;
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
                    var world = (Vector2)station.entity.position;
                    var biome = level.GetMainBiom(Vector2Int.RoundToInt(world));
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
                    option.StationPositions.Add(world);
                }

                // MERGE VARIANTS OF THE SAME MATERIAL. The generator splits a material into several
                // biome assets — Flesh / Flesh Solid / Flesh Techno, Caverns / Caverns Deep, Crust /
                // Crust Boss — which is meaningful to level generation and meaningless to a player
                // picking a destination under a 30-second clock (Omar, 2026-07-29: "we need to group
                // biomes of the same type"). Three near-identical rows are three ways to fail to
                // read the list. Grouped by leading word, which is how the assets are named and
                // therefore how the family is already expressed.
                foreach (var group in byBiome.Values
                             .GroupBy(o => o.Name.Split(' ')[0], System.StringComparer.OrdinalIgnoreCase))
                {
                    var members = group.OrderBy(o => o.BiomeId).ToList();
                    var merged = members[0];               // lowest id represents the family
                    merged.Name = group.Key;               // "Flesh", not "Flesh Techno"
                    for (int i = 1; i < members.Count; i++)
                    {
                        merged.StationPositions.AddRange(members[i].StationPositions);
                        // Every id in the family answers to the representative, so a tally that
                        // arrives keyed on a variant still lands on the row the player pressed.
                        merged.AliasBiomeIds.Add(members[i].BiomeId);
                    }
                    Options.Add(merged);
                }
                // Stable order so the buttons do not shuffle between machines or frames.
                Options.Sort((a, b) => System.StringComparer.Ordinal.Compare(a.Name, b.Name));
                Plugin.Log.LogInfo($"[BRDrop] {Options.Count} drop regions: " +
                    string.Join(", ", Options.Select(o => $"{o.Name}({o.StationPositions.Count})")));
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
            HoldInTheVoid();
            Plugin.Log.LogInfo($"[BRDrop] drop window open — {NetConfig.BrChooseSpawnSeconds.Value:0}s, " +
                $"{Options.Count} regions");
        }

        /// <summary>Park the ship OUTSIDE THE WORLD until it deploys.
        ///
        /// This is the fix for an instant game over (2026-07-28). While a player sat on the drop
        /// screen their ship was still standing on the shared start pad, in the world, with live
        /// enemies on it — so they were eaten mid-decision, and with two players that is one death
        /// and an immediate "last one standing". A drop screen that kills you for reading it is
        /// worse than no drop screen.
        ///
        /// The void beyond the playable disc is the natural holding pen: BorderGenerator stamps
        /// everything past Width/2 from the grid centre as void, so there are no cells, no
        /// collision and nothing living out there. The ship is also frozen STATIC with input off,
        /// so it cannot drift, and the screen draws an opaque backdrop over it — between them the
        /// player is simply not in the game yet, which is the intended fiction.</summary>
        private static void HoldInTheVoid(bool quiet = false)
        {
            try
            {
                var level = ServiceLocator.Get<Level>();
                if (level == null)
                {
                    Plugin.Log.LogWarning("[BRDrop] no Level when opening the drop window — the ship " +
                        "stays in the world while choosing and CAN be attacked");
                    return;
                }
                // Straight "up" from the grid centre, comfortably past the disc edge.
                var holding = new Vector2(level.Width * 0.5f, level.Height * 0.5f + level.Width * 0.5f + 90f);
                var current = Sync.ShipSync.LocalShip;
                // Only move it if something has dragged it back out of the pen; re-teleporting every
                // frame regardless would fight the camera and the physics for no reason.
                if (current == null || ((Vector2)current.transform.position - holding).sqrMagnitude > 4f)
                    Sync.ShipSync.TeleportLocalShipTo(holding);

                var ship = Sync.ShipSync.LocalShip;
                if (ship == null)
                {
                    // Not an error on the repeat pass: the ship simply has not spawned yet, and the
                    // next tick will park it the moment it does.
                    if (!quiet)
                        Plugin.Log.LogInfo("[BRDrop] ship not spawned yet — it will be parked as soon as it is");
                    return;
                }
                if (ship.shipInput != null) ship.shipInput.enabled = false;
                if (ship.Rigidbody != null) ship.Rigidbody.bodyType = RigidbodyType2D.Static;
                if (ship.Crosshair != null) ship.Crosshair.Visible = false;
                if (!quiet)
                    Plugin.Log.LogInfo($"[BRDrop] holding at ({holding.x:0},{holding.y:0}) in the void until deploy");
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"[BRDrop] could not park the ship for selection: {e.Message} — " +
                    "it stays where the run put it and may be attacked while choosing");
            }
        }

        /// <summary>True once this player has actually dropped into the world.</summary>
        internal static bool Deployed { get; private set; }

        private static float _protectedUntil = -1f;

        /// <summary>No damage may touch this ship yet.
        ///
        /// Two windows, for two different reasons. WHILE CHOOSING, because a player reading a menu
        /// cannot defend themselves and dying to something they never saw would be absurd — the
        /// void pen makes that unlikely, but "unlikely" is not a guarantee and that pen has already
        /// lost one race to the run scene. AFTER DEPLOYING, for a few seconds, because you arrive
        /// somewhere you have never seen with no idea what is beside you (Omar, 2026-07-28:
        /// "sometimes when players spawn into a world they spawn in danger — we need to give them
        /// god mode until they spawn in").
        ///
        /// Read by DamageSync's existing god-mode gate, so it covers exactly what the `god` devcmd
        /// covers — routed damage, world contact damage, and the direct chokepoints — instead of
        /// being a second, subtly different notion of invulnerable that protects against some
        /// damage sources and not others.</summary>
        internal static bool SpawnProtected
        {
            get
            {
                var s = NetSession.Instance;
                if (s == null || !NetSession.Active || s.CurrentMode != GameMode.BattleRoyale) return false;
                if (!NetConfig.BrChooseSpawn.Value) return false;
                // Gate on the window actually being OPEN (_deadline is set by OpenWindow), not on
                // "not deployed yet" — the latter is also true before the window exists and after a
                // reset, which would quietly make a ship invulnerable outside the moment this is
                // meant to cover.
                if (!Deployed && _deadline > 0f) return true;   // still choosing
                return _protectedUntil > 0f && Time.unscaledTime < _protectedUntil;
            }
        }

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
            // KEEP it parked. A one-shot teleport at go-live loses a race it cannot win: the run
            // scene places ships AFTER go-live, so whatever we moved gets put straight back on the
            // start pad — which is why players kept spawning at a station before they had picked
            // one (Omar, 2026-07-28), and why they were in danger while reading the screen. Holding
            // every frame simply outlasts whoever else wants to place the ship.
            HoldInTheVoid(quiet: true);
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
            _protectedUntil = Time.unscaledTime + Mathf.Max(0f, NetConfig.BrSpawnProtectionSeconds.Value);
            try
            {
                var option = Options.FirstOrDefault(o => o.Covers(biomeId)) ?? Options[0];
                if (option.StationPositions.Count == 0) return;
                var pad = option.StationPositions[UnityEngine.Random.Range(0, option.StationPositions.Count)];
                Sync.ShipSync.TeleportLocalShipTo(pad + Vector2.up * 2f); // hover above the platform

                var ship = Sync.ShipSync.LocalShip;
                if (ship != null)
                {
                    if (ship.shipInput != null) ship.shipInput.enabled = true;
                    if (ship.Rigidbody != null) ship.Rigidbody.bodyType = RigidbodyType2D.Dynamic;
                    if (ship.Crosshair != null) ship.Crosshair.Visible = true;
                    ship.UnlockCamera(0f);
                    ship.SetHeadlightsEnabled(true);
                }

                // THE CAMERA MUST BE TOLD TO RUN AGAIN. ProCamera2D is disabled for the opening
                // cinematic and re-enabled on that cinematic's FINAL line — which is unreachable
                // here: the cinematic waits on the start station's GameObject, and parking the ship
                // out in the void unloads exactly that station from streaming, so it spins forever.
                // The result was a ship that had deployed correctly under a camera that never moved
                // again (field-reported 2026-07-28). Deploy therefore owns the camera outright
                // rather than inheriting whatever state a cinematic it no longer waits for left it
                // in. Position first, then enable, so it does not sweep in from the void.
                try
                {
                    var cam = Com.LuisPedroFonseca.ProCamera2D.ProCamera2D.Instance;
                    if (cam != null && !cam.enabled)
                    {
                        cam.enabled = true;
                        Plugin.Log.LogInfo("[BRDrop] re-enabled ProCamera2D — the start cinematic never got to");
                    }
                    if (cam != null) cam.MoveCameraInstantlyToPosition(pad + Vector2.up * 2f);
                }
                catch (System.Exception e) { Plugin.Log.LogWarning($"[BRDrop] camera handover failed: {e.Message}"); }
                Plugin.Log.LogInfo($"[BRDrop] deployed to {option.Name} at ({pad.x:0},{pad.y:0}) ({why})");
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
                // Covers(), not equality: a pick made on a variant id belongs to the family row it
                // was merged into, or a grouped region would show an empty bar for a real choice.
                var option = Options.FirstOrDefault(o => o.Covers(msg.BiomeIds[i]));
                if (option != null) option.Picks += msg.Counts[i];
            }
        }
        // ---------------------------------------------------------------- host side (tally only)

        public static void ApplyChoice(SpawnChoiceMsg msg, byte fromSlot, NetSession session)
        {
            if (session == null || !session.IsHost) return;
            Choices[fromSlot] = msg.BiomeId;
            // Named out loud: a bar appearing on the drop screen should always be traceable to a
            // player who actually pressed something. Without this, "someone is already there" and
            // "the tally is wrong" look identical from a screenshot.
            Plugin.Log.LogInfo($"[BRDrop] P{fromSlot + 1} picked biome {msg.BiomeId} " +
                $"({Choices.Count} choice(s) in)");
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