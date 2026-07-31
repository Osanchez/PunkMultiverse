using System.Collections.Generic;
using System.Linq;
using PunkMultiverse.Core;
using PunkMultiverse.Protocol;
using PunkMultiverse.Transport;
using UnityEngine;
using UnityEngine.InputSystem;

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
                       && NetConfig.ChooseSpawn
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
            _settling = false;
            _protectedUntil = -1f;
            _inputArmedAt = -1f;
            _nextHoldReportAt = 0f;
            Highlighted = 0;
            _navNextAt = 0f;
            _unfocusedPauseUsed = 0f;
            _opened = false;
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
        private static bool _opened;

        internal static void OpenWindow()
        {
            // ONCE PER RUN. Re-opening is destructive: it clears LocalHasChosen and Deployed, so a
            // player who has already dropped is pulled back into the void pen and made to choose
            // again, and a player mid-decision loses the row they were on plus another 0.75s of
            // input grace — which is what "my controller doesn't register right away" actually was
            // (Omar, 2026-07-30). NetSession now latches GO_LIVE so this should never be reached
            // twice, but the window is worth defending on its own: it is the one screen where a
            // reset silently spends the player's decision.
            if (_opened)
            {
                Plugin.Log.LogWarning("[BRDrop] second OpenWindow IGNORED — the drop window already " +
                    $"opened this run (chosen={LocalHasChosen}, deployed={Deployed}). Re-opening " +
                    "would un-deploy this player and reset their selection.");
                return;
            }
            _opened = true;
            _closed = false;
            LocalHasChosen = false;
            Deployed = false;
            _settling = false;
            if (NetConfig.IsCoordinator || !NetConfig.ChooseSpawn) { _closed = true; return; }
            if (Options.Count == 0) BuildOptions();
            if (Options.Count == 0)
            {
                _closed = true;
                // The spawn-frame and per-frame holds have been parking this ship since it was
                // created, on the assumption a drop screen was coming. There isn't one — hand the
                // ship back (input, physics, crosshair) so the scatter drops a flyable ship and
                // not a statue in the void.
                ReleaseHold();
                Plugin.Log.LogWarning("[BRDrop] no station-bearing biomes — dropping by the scatter instead");
                return;
            }
            _deadline = Time.unscaledTime + Mathf.Max(5f, NetConfig.BrChooseSpawnSeconds.Value);
            ArmInputAfter(InitialInputGrace);
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
        /// player is simply not in the game yet, which is the intended fiction.
        ///
        /// CALLED FROM SPAWN, not just from go-live (Omar, 2026-07-29: "we still seem to be
        /// spawning before spawn selection. why are we doing that"). OpenWindow and Tick both hold,
        /// but Tick only runs once the session is InGame — and vanilla places the ship on the
        /// shared start pad at scene load, BEFORE the first InGame tick. That gap put the player in
        /// the world, on the pad, attackable, ahead of any choice. BattleRoyaleSpawn's
        /// SpawnShipGameObjects postfix now parks the ship the moment it exists, so there is no
        /// frame in which an undeployed ship stands anywhere real.</summary>
        internal static void HoldInTheVoid(bool quiet = false)
        {
            try
            {
                var level = ServiceLocator.Get<Level>();
                if (level == null)
                {
                    // Only worth a warning from the one-shot calls (OpenWindow, the spawn postfix).
                    // The per-frame hold runs through Loading, where "no Level yet" is simply early.
                    if (!quiet)
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

        // ---------------------------------------------------------------- post-deploy settle
        //
        // Omar, 2026-07-29: "my host just had a weird teleport glitch... they're not even at the
        // station, they're below it and they took a little damage."
        //
        // Measured from his own log, which named it exactly:
        //     [BRDrop] DEPLOYED to Flesh at (1014,834)
        //     [CombatHit] contact=CellType Hazard applied=True at (1005,810) hp=8->7
        // The hit landed TWENTY-FOUR UNITS BELOW the pad it deployed to. The ship did not arrive
        // wrong — it arrived correctly and then fell, because Deploy handed physics back in the same
        // frame as the teleport. The pad is ~1250 units from the holding pen, so none of its terrain
        // or station colliders were streamed in: there was nothing there to land on. By the time the
        // world caught up the ship was already under the platform, sitting in hazard cells.
        //
        // So physics stays OFF until there is demonstrably ground beneath the ship, rather than for
        // a guessed number of milliseconds. The timeout is only a backstop for a pad that genuinely
        // has nothing under it, and it says so in the log instead of failing silently.
        private static Vector2 _settlePad;
        private static bool _settling;
        private static float _settleDeadline;
        private static int _groundMask = -1;

        private static void BeginSettle(Vector2 pad)
        {
            _settlePad = pad;
            _settling = true;
            _settleDeadline = Time.unscaledTime + 4f;
            var ship = Sync.ShipSync.LocalShip;
            if (ship?.Rigidbody != null)
            {
                ship.Rigidbody.bodyType = RigidbodyType2D.Kinematic;
                ship.Rigidbody.linearVelocity = Vector2.zero;
                ship.Rigidbody.angularVelocity = 0f;
            }
        }

        /// <summary>Hold the deployed ship at its pad until the ground it is standing on actually
        /// exists, then hand physics back. Runs on RENDER frames from Toast.Update, like the pen —
        /// streaming completes during frames the net tick may not see.</summary>
        internal static void TickSettle()
        {
            if (!_settling) return;
            var ship = Sync.ShipSync.LocalShip;
            if (ship == null) { _settling = false; return; }
            if (_groundMask == -1)
            {
                try { _groundMask = LayerMask.GetMask("Ground"); } catch { _groundMask = 0; }
            }

            // Pin it. Anything that nudges the ship while the world loads is undone next frame.
            if (((Vector2)ship.transform.position - _settlePad).sqrMagnitude > 0.25f)
                Sync.ShipSync.TeleportLocalShipTo(_settlePad);
            if (ship.Rigidbody != null)
            {
                ship.Rigidbody.linearVelocity = Vector2.zero;
                ship.Rigidbody.angularVelocity = 0f;
            }

            bool grounded = false;
            try
            {
                // Stations and terrain both present their solid colliders on the Ground layer
                // (verified via pvpprobe: a station's Hatch and Platform are layer 10 = Ground).
                if (_groundMask != 0)
                    grounded = Physics2D.Raycast(_settlePad, Vector2.down, 10f, _groundMask).collider != null;
            }
            catch { }

            bool timedOut = Time.unscaledTime >= _settleDeadline;
            if (!grounded && !timedOut) return;

            _settling = false;
            if (ship.Rigidbody != null) ship.Rigidbody.bodyType = RigidbodyType2D.Dynamic;
            // Re-arm protection from the moment the ship is actually free, not from the teleport:
            // the whole point is that the player gets their look-around window while in control,
            // not while pinned waiting for terrain.
            _protectedUntil = Time.unscaledTime + Mathf.Max(0f, NetConfig.BrSpawnProtectionSeconds.Value);
            Plugin.Log.LogInfo(grounded
                ? $"[BRDrop] settled at ({_settlePad.x:0},{_settlePad.y:0}) — ground streamed in, physics live, " +
                  $"{NetConfig.BrSpawnProtectionSeconds.Value:0}s protection starts now"
                : $"[BRDrop] settle TIMED OUT at ({_settlePad.x:0},{_settlePad.y:0}) — no ground within 10 units " +
                  "after 4s; releasing anyway (the ship may fall)");
        }

        /// <summary>Undo everything the pen did to the ship: input, physics, crosshair. Deploy has
        /// its own copy of this inline (plus camera work); this exists for the paths that close the
        /// window WITHOUT deploying, which would otherwise inherit a frozen ship.</summary>
        private static void ReleaseHold()
        {
            try
            {
                var ship = Sync.ShipSync.LocalShip;
                if (ship == null) return;
                if (ship.shipInput != null) ship.shipInput.enabled = true;
                if (ship.Rigidbody != null) ship.Rigidbody.bodyType = RigidbodyType2D.Dynamic;
                if (ship.Crosshair != null) ship.Crosshair.Visible = true;
            }
            catch (System.Exception e)
            { Plugin.Log.LogWarning($"[BRDrop] could not release the held ship: {e.Message}"); }
        }

        /// <summary>Hold the pen on RENDER frames, not just net ticks. Called from Toast.Update —
        /// a component that lives regardless of session state — because the two existing holds
        /// both have blind spots: the spawn postfix runs once, and Tick only runs while InGame.
        /// Vanilla's async PlaceShipEntitiesToStartPosition lands between them (during Loading,
        /// after the ship exists), which is how an undeployed ship kept turning up on the shared
        /// start pad (Omar, 2026-07-29: "we still seem to be spawning before spawn selection").
        /// Whoever places the ship, whenever they do it, the very next frame parks it again.</summary>
        internal static void HoldPendingDeploy()
        {
            var s = NetSession.Instance;
            if (s == null || !NetSession.Active) return;
            // CurrentMode, NOT LobbyMode: the mode of the run actually underway. It is set on every
            // machine at run start, before Loading — and LobbyMode can be flipped to BR from the
            // lobby UI while a CO-OP run is still being played, which must not pen anyone.
            if (s.CurrentMode != GameMode.BattleRoyale) return;
            if (s.State != SessionState.Loading && s.State != SessionState.InGame) return;
            if (NetConfig.IsCoordinator || !NetConfig.ChooseSpawn) return;
            if (_closed || Deployed) return;
            HoldInTheVoid(quiet: true);
        }

        /// <summary>Arm the post-arrival protection window from a path that placed the ship WITHOUT
        /// the drop screen (the scatter teleport, the direct station spawn). Deploy arms its own;
        /// this exists so every way of arriving in a BR match gets the same grace.</summary>
        internal static void NoteSpawn(string why)
        {
            float seconds = Mathf.Max(0f, NetConfig.BrSpawnProtectionSeconds.Value);
            if (seconds <= 0f) return;
            _protectedUntil = Time.unscaledTime + seconds;
            Plugin.Log.LogInfo($"[BRDrop] spawn protection armed for {seconds:0}s ({why})");
        }

        /// <summary>True once this player has actually dropped into the world.</summary>
        internal static bool Deployed { get; private set; }

        private static float _protectedUntil = -1f;

        // ---------------------------------------------------------------- input arming
        //
        // A player was dropped into a region "before I even selected anything" (Omar, 2026-07-29).
        // IMGUI buttons fire on a plain mouse-up inside their rect, and the click that FOCUSES a
        // game window is delivered to the game like any other — so alt-tabbing back to a client
        // presses whatever row happens to be under the cursor. The screen also opens under a
        // cursor that is already somewhere, mid-click from the lobby.
        //
        // So selection is dead for a moment after the screen appears, and dead again for a moment
        // after the window regains focus. A drop region is a decision worth one deliberate click;
        // it should never be possible to spend it by accident.
        private const float InitialInputGrace = 0.75f;
        private const float RefocusInputGrace = 0.40f;

        private static float _inputArmedAt = -1f;
        private static bool _wasFocused = true;

        internal static bool InputArmed => _inputArmedAt >= 0f && Time.unscaledTime >= _inputArmedAt;
        internal static float ArmedInSeconds => Mathf.Max(0f, _inputArmedAt - Time.unscaledTime);

        private static void ArmInputAfter(float seconds)
        {
            _inputArmedAt = Time.unscaledTime + Mathf.Max(0f, seconds);
            _wasFocused = Application.isFocused;
        }

        private static void TickInputArming()
        {
            bool focused = Application.isFocused;
            if (focused && !_wasFocused)
            {
                // The click that brought this window forward must not also choose a region.
                _inputArmedAt = Mathf.Max(_inputArmedAt, Time.unscaledTime + RefocusInputGrace);
                Plugin.Log.LogInfo("[BRDrop] window regained focus — selection disarmed briefly so " +
                    "the focusing click cannot pick a region");
            }
            _wasFocused = focused;
        }

        // ---------------------------------------------------------------- keyboard & gamepad
        //
        // The drop screen is IMGUI, and an IMGUI button only ever answers a mouse. On a controller
        // the whole screen was therefore dead — you could watch the clock run out and be dropped
        // somewhere at random, with no way to say otherwise (Omar, 2026-07-28: "our spawn selection
        // screen is not navigatable or selectable with a controller"). PUNK's own menus never run
        // EventSystem navigation either, which is why UI/LobbyScreen polls the pad itself; this does
        // the same thing for the one screen that has no Selectables to walk.
        //
        // A HIGHLIGHT is the whole mechanism: the pad moves it, the mouse moves it by hovering, and
        // both confirm the same row. Mouse users see exactly what they saw before — the row under the
        // pointer lights up and clicking it drops them — so nothing was traded away to add the pad.
        internal static int Highlighted { get; private set; }

        private static float _navNextAt;
        private const float NavFirstRepeat = 0.40f;
        private const float NavRepeat = 0.16f;

        /// <summary>Point the highlight at a row (the mouse does this by hovering).</summary>
        internal static void Highlight(int index)
        {
            if (Options.Count == 0) return;
            Highlighted = Mathf.Clamp(index, 0, Options.Count - 1);
        }

        private static void TickNavigation()
        {
            if (Options.Count == 0) return;
            Highlighted = Mathf.Clamp(Highlighted, 0, Options.Count - 1);

            // `Gamepad.current` is the last-USED pad, and a pad that has not been touched since the
            // process started may not be current yet — on a screen whose whole job is to be the
            // first thing a controller talks to, that reads as "the controller does nothing at
            // first, then starts working". Fall back to the first connected pad so the very first
            // press counts. (Only a fallback: when a pad IS current it stays the one that drives
            // the screen, so two pads on one machine behave exactly as before.)
            var pad = Gamepad.current;
            if (pad == null && Gamepad.all.Count > 0) pad = Gamepad.all[0];
            var kb = Keyboard.current;

            // Confirm. Gated by Choose()'s own arming check, so a controller cannot spend the
            // decision on the press that brought the window forward any more than a mouse can.
            bool confirm =
                (pad != null && (pad.buttonSouth.wasPressedThisFrame || pad.startButton.wasPressedThisFrame))
                || (kb != null && (kb.enterKey.wasPressedThisFrame
                                   || kb.numpadEnterKey.wasPressedThisFrame
                                   || kb.spaceKey.wasPressedThisFrame));
            if (confirm && !LocalHasChosen)
            {
                Choose(Options[Highlighted].BiomeId);
                return;
            }

            // Move, with hold-to-repeat. Stick and d-pad are summed the way LobbyScreen does it, so
            // either works and neither has to be discovered.
            float v = 0f;
            if (pad != null) v = pad.dpad.ReadValue().y + pad.leftStick.ReadValue().y;
            int step = Mathf.Abs(v) > 0.5f ? (v > 0f ? -1 : 1) : 0; // up on the stick = up the list
            bool held = step != 0;
            if (kb != null)
            {
                if (kb.upArrowKey.wasPressedThisFrame || kb.wKey.wasPressedThisFrame) { step = -1; held = false; }
                else if (kb.downArrowKey.wasPressedThisFrame || kb.sKey.wasPressedThisFrame) { step = 1; held = false; }
                else if (step == 0
                         && (kb.upArrowKey.isPressed || kb.wKey.isPressed)) { step = -1; held = true; }
                else if (step == 0
                         && (kb.downArrowKey.isPressed || kb.sKey.isPressed)) { step = 1; held = true; }
            }
            if (step == 0) { _navNextAt = 0f; return; }

            if (held)
            {
                float now = Time.unscaledTime;
                bool fresh = _navNextAt <= 0f;
                if (!fresh && now < _navNextAt) return;
                _navNextAt = now + (fresh ? NavFirstRepeat : NavRepeat);
            }
            else _navNextAt = Time.unscaledTime + NavFirstRepeat;

            // Wraps: a four-item list is short enough that running off the end and starting again is
            // what a player expects, and it means the stick never feels stuck.
            Highlighted = (Highlighted + step + Options.Count) % Options.Count;
            UI.UiTheme.PlayClick();
        }

        private static float _nextHoldReportAt;

        /// <summary>Say where the ship is while it waits. If a player is ever placed before they
        /// choose, this is the line that shows whether the pen was holding at the time.</summary>
        private static void ReportHold()
        {
            if (Time.unscaledTime < _nextHoldReportAt) return;
            _nextHoldReportAt = Time.unscaledTime + 3f;
            var ship = Sync.ShipSync.LocalShip;
            string pos = ship != null
                ? $"({ship.transform.position.x:0},{ship.transform.position.y:0})" : "no ship";
            // Pad state rides along: "the controller does nothing" is only diagnosable if the log
            // says whether a pad was even visible to this screen at the time.
            int pads = Gamepad.all.Count;
            Plugin.Log.LogInfo($"[BRDrop] waiting: {SecondsLeft:0}s left, ship {pos}, " +
                $"armed={InputArmed}, chosen={LocalHasChosen}, deployed={Deployed}, " +
                $"pads={pads}(current={(Gamepad.current != null ? Gamepad.current.name : "none")}), " +
                $"row={Highlighted + 1}/{Options.Count}");
        }

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
                // NOT gated on BrChooseSpawn any more. "You arrive somewhere you have never seen
                // and deserve a moment to look" is true of a SCATTERED spawn too — and with the
                // drop screen off that path set no protection at all, so a player teleported onto
                // a station beside lava burned 8->0 before they could react (2026-07-29, both
                // clients, `contact=CellType Hazard`). NoteSpawn() below arms the window from
                // whichever path actually placed the ship.
                // Gate on the window actually being OPEN (_deadline is set by OpenWindow), not on
                // "not deployed yet" — the latter is also true before the window exists and after a
                // reset, which would quietly make a ship invulnerable outside the moment this is
                // meant to cover.
                if (NetConfig.ChooseSpawn && !Deployed && _deadline > 0f) return true; // still choosing
                return _protectedUntil > 0f && Time.unscaledTime < _protectedUntil;
            }
        }

        /// <summary>Local player picked a region: deploy IMMEDIATELY. Omar, 2026-07-28: "spawn as
        /// soon as they select — don't let other players hold the server." The host is told only so
        /// it can keep the heat map honest; nothing waits on its reply.</summary>
        internal static void Choose(byte biomeId)
        {
            if (_closed || Deployed) return;
            if (!InputArmed)
            {
                Plugin.Log.LogInfo($"[BRDrop] IGNORED a pick of biome {biomeId} — input not armed yet " +
                    $"({ArmedInSeconds:0.00}s to go). This is the click that focused the window or " +
                    "landed in the first moments of the screen, not a decision.");
                return;
            }
            Plugin.Log.LogInfo($"[BRDrop] pick: biome {biomeId} after " +
                $"{Mathf.Max(0f, NetConfig.BrChooseSpawnSeconds.Value) - SecondsLeft:0.0}s on screen");
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

        // The clock PAUSES while the window is unfocused. The timeout exists so a player at the
        // screen can't sit in the void forever — but an UNFOCUSED window has nobody at it, and the
        // clock expiring there took the choice away from anyone running two clients (Omar,
        // 2026-07-29: "I'm assuming you added something to auto choose spawn on the second player
        // — I would like to choose the spawn for both players"). Alt-tab back and the countdown
        // resumes where it stopped. The pause is CAPPED so a player who alt-tabs and walks away on
        // a real server still deploys eventually instead of stalling the match from the void.
        private const float MaxUnfocusedPauseSeconds = 300f;
        private static float _unfocusedPauseUsed;

        /// <summary>Ticked while the window is open: nobody is held past the clock. Running out of
        /// time is a decision too — a random region, not a penalty and not a wait.</summary>
        internal static void Tick()
        {
            if (_closed || Deployed || _deadline < 0f) return;
            if (!Application.isFocused && _unfocusedPauseUsed < MaxUnfocusedPauseSeconds)
            {
                float dt = Time.unscaledDeltaTime;
                _deadline += dt;                 // hold the countdown exactly where it was
                _unfocusedPauseUsed += dt;
                if (_unfocusedPauseUsed >= MaxUnfocusedPauseSeconds)
                    Plugin.Log.LogInfo("[BRDrop] unfocused-pause budget exhausted — the clock runs on");
            }
            // KEEP it parked. A one-shot teleport at go-live loses a race it cannot win: the run
            // scene places ships AFTER go-live, so whatever we moved gets put straight back on the
            // start pad — which is why players kept spawning at a station before they had picked
            // one (Omar, 2026-07-28), and why they were in danger while reading the screen. Holding
            // every frame simply outlasts whoever else wants to place the ship.
            HoldInTheVoid(quiet: true);
            TickInputArming();
            TickNavigation();
            ReportHold();
            if (Time.unscaledTime < _deadline) return;
            Plugin.Log.LogInfo("[BRDrop] timer expired — picking a region at random");
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
                var pad = PickPad(option);
                ClearChosenPad(pad); // nothing hostile greets an arrival — remove, then land
                Sync.ShipSync.TeleportLocalShipTo(pad + Vector2.up * 2f); // hover above the platform

                var ship = Sync.ShipSync.LocalShip;
                if (ship != null)
                {
                    if (ship.shipInput != null) ship.shipInput.enabled = true;
                    if (ship.Crosshair != null) ship.Crosshair.Visible = true;
                    ship.UnlockCamera(0f);
                    ship.SetHeadlightsEnabled(true);
                    // NOT Dynamic yet — see TickSettle. The destination is over a thousand units
                    // from the holding pen and its terrain has not streamed in, so releasing physics
                    // in this frame drops the ship through a platform that does not exist yet.
                    BeginSettle(pad + Vector2.up * 2f);
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
                Plugin.Log.LogInfo($"[BRDrop] DEPLOYED to {option.Name} at ({pad.x:0},{pad.y:0}) — " +
                    $"reason={why}, {SecondsLeft:0}s were left on the clock, " +
                    $"{option.StationPositions.Count} pads in that region");
                UI.Toast.Show($"DROPPING INTO {option.Name.ToUpperInvariant()}", 4f);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"[BRDrop] deploy failed: {e.Message} — the scatter will place this ship");
            }
        }

        // ---------------------------------------------------------------- clearing the chosen pad
        //
        // The go-live sweep (BattleRoyale.ClearSpawnAreas) empties every station once, but a drop
        // lands up to 30 seconds later and enemies wander back — Omar, 2026-07-29: "those red
        // electrons by spawns need to be considered, we shouldn't have any directly on a spawn."
        // So the deployer clears its own landing pad AT THE MOMENT OF LANDING, with the same verb
        // the go-live sweep uses (RemoveSilently: no loot, no VFX, no kill credit, tombstoned).
        //
        // Unlike the go-live sweep this MUST travel: that one is derived identically on every
        // machine at the same instant, while a deploy is one machine's chosen moment. The exact
        // netId set is broadcast (SpawnClearMsg) rather than a position+radius, because peers'
        // entity positions differ slightly (owner vs puppet) and a locally-evaluated radius would
        // remove slightly different sets — the deployer's set is canonical. RemoveSilently is
        // idempotent (KilledNetIds tombstone), so the sender receiving its own echo is a no-op.

        /// <summary>Silently remove every hostile within the spawn-clear radius of the pad this
        /// player is about to land on, and tell everyone which ones.</summary>
        private static void ClearChosenPad(Vector2 pad)
        {
            float radius = NetConfig.BrSpawnClearRadius.Value;
            if (radius <= 0f) return;
            try
            {
                var em = ServiceLocator.Get<EntityManager>();
                if (em == null) return;

                // Ships are savable entities too; never let a prefix match delete a player.
                var shipInstances = new HashSet<int>();
                try
                {
                    foreach (var s in ServiceLocator.Get<ShipManager>().Ships)
                    {
                        var se = s != null ? s.GetComponentInChildren<SavableEntity>() : null;
                        if (se != null && se.EntityData != null) shipInstances.Add(se.EntityData.instanceId);
                    }
                }
                catch { }

                float radiusSq = radius * radius;
                var doomed = new List<int>();
                foreach (var data in em.GetAllEntities())
                {
                    if (data == null || shipInstances.Contains(data.instanceId)) continue;
                    if (!BattleRoyale.IsHostileEntityId(data.entityId)) continue;
                    if (((Vector2)data.position - pad).sqrMagnitude > radiusSq) continue;
                    if (Core.NetIds.TryGetNetId(data.instanceId, out int netId)) doomed.Add(netId);
                    if (doomed.Count == 255) break; // wire cap; a pad with 255+ hostiles has bigger problems
                }
                if (doomed.Count == 0) return;

                foreach (int netId in doomed) Sync.EnemySync.RemoveSilently(netId);
                Plugin.Log.LogInfo($"[BRDrop] pad clear: removed {doomed.Count} hostile(s) within " +
                    $"{radius:0} units of the landing pad ({pad.x:0},{pad.y:0})");

                var session = NetSession.Instance;
                if (session == null) return;
                var w = new NetWriter(8 + doomed.Count * 4);
                new SpawnClearMsg { NetIds = doomed.ToArray() }.Write(w);
                session.SendToAll(NetChannel.Control, w.ToSegment(), reliable: true);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"[BRDrop] pad clear failed: {e.Message} — landing among " +
                    "whatever is there; spawn protection still covers the arrival");
            }
        }

        /// <summary>A peer's pad clear arriving here: remove the same set. The host also relays it
        /// on to everyone else — clients only ever talk to the host.</summary>
        public static void ApplySpawnClear(SpawnClearMsg msg, NetSession session)
        {
            if (msg.NetIds == null || msg.NetIds.Length == 0) return;
            foreach (int netId in msg.NetIds) Sync.EnemySync.RemoveSilently(netId);
            Plugin.Log.LogInfo($"[BRDrop] applied a peer's pad clear — {msg.NetIds.Length} entities");
            if (session != null && session.IsHost)
            {
                var w = new NetWriter(8 + msg.NetIds.Length * 4);
                msg.Write(w);
                session.SendToAll(NetChannel.Control, w.ToSegment(), reliable: true);
            }
        }

        // ---------------------------------------------------------------- choosing WHICH pad
        //
        // The region is the player's decision; the pad inside it is not, and it used to be a coin
        // toss. That is how a drop ends in "TRIHARDEST DIED" with nothing to blame it on (Omar,
        // 2026-07-28: "look at what eliminated my player 1, that is something that is by the shops
        // that shouldn't be"): the go-live spawn-area clear (BattleRoyaleLocal.ClearSpawnAreas)
        // empties every station ONCE, at go-live, and a drop lands up to 30 seconds later — long
        // enough for the map to have moved on. And the clear only ever removed hostile ENTITIES; a
        // damaging Hazard prop or a patch of contact-damage terrain beside the platform was never in
        // its remit at all, which is precisely the kind of thing that kills a ship the instant its
        // spawn protection lapses.
        //
        // So: look before landing. Every pad in the region is scored on what is actually sitting on
        // it right now, and the quietest wins. This CHANGES NOTHING in the world — no removals, no
        // wire traffic, no divergence — it only decides where to put our own ship, which was always
        // a local decision.
        private const float PadDangerRadius = 22f;

        /// <summary>Pick the least dangerous station in the region, breaking ties randomly so a
        /// whole lobby heading for the same biome does not pile onto one platform.</summary>
        private static Vector2 PickPad(BiomeOption option)
        {
            var pads = option.StationPositions;
            var scores = new int[pads.Count];
            bool scored = false;
            try { scored = ScorePads(pads, scores); }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"[BRDrop] could not check the pads for danger ({e.Message}) — " +
                    "landing on a random one");
            }
            if (!scored) return pads[UnityEngine.Random.Range(0, pads.Count)];

            int best = int.MaxValue;
            for (int i = 0; i < scores.Length; i++) best = Mathf.Min(best, scores[i]);
            var safest = new List<int>();
            for (int i = 0; i < scores.Length; i++) if (scores[i] == best) safest.Add(i);
            int chosen = safest[UnityEngine.Random.Range(0, safest.Count)];

            if (best > 0)
                Plugin.Log.LogWarning($"[BRDrop] every pad in {option.Name} has something hostile " +
                    $"within {PadDangerRadius:0} units — landing on the quietest ({best} nearby)");
            else
                Plugin.Log.LogInfo($"[BRDrop] pad chosen from {safest.Count}/{pads.Count} clear " +
                    $"platform(s) in {option.Name}");
            return pads[chosen];
        }

        /// <summary>Count what would greet a ship at each pad: hostile entities within
        /// <see cref="PadDangerRadius"/>, plus damaging terrain right under the platform.
        ///
        /// Entity DATA is used rather than colliders on purpose — the pads are far away and not
        /// streamed in, so a physics query there would find an empty world and call everything safe.
        /// Terrain is read the same way, straight off the cell grid, which is complete everywhere.
        /// Returns false if the world is not in a state that can answer, so the caller falls back to
        /// the old random pick rather than pretending it checked.</summary>
        private static bool ScorePads(List<Vector2> pads, int[] scores)
        {
            var em = ServiceLocator.Get<EntityManager>();
            if (em == null) return false;

            float radiusSq = PadDangerRadius * PadDangerRadius;
            foreach (var data in em.GetAllEntities())
            {
                if (data == null || !BattleRoyale.IsHostileEntityId(data.entityId)) continue;
                Vector2 pos = data.position;
                for (int i = 0; i < pads.Count; i++)
                    if ((pos - pads[i]).sqrMagnitude <= radiusSq) scores[i]++;
            }

            // Contact-damage terrain counts for a lot more than one enemy: an enemy can be shot, and
            // lava cannot. StationGenerator clears the cells around a station, so anything left here
            // is something that arrived afterwards and is worth avoiding outright.
            var level = ServiceLocator.Get<Level>();
            if (level != null)
                for (int i = 0; i < pads.Count; i++)
                    scores[i] += 10 * DamagingCellsAt(level, pads[i]);
            return true;
        }

        /// <summary>Damaging cells in the small box a ship actually occupies on arrival.</summary>
        private static int DamagingCellsAt(Level level, Vector2 pad)
        {
            const int Reach = 3; // the hover offset is +2; this covers the hull around it
            int count = 0;
            var origin = Vector2Int.RoundToInt(pad);
            for (int dy = -Reach; dy <= Reach + 2; dy++)
                for (int dx = -Reach; dx <= Reach; dx++)
                {
                    int x = origin.x + dx, y = origin.y + dy;
                    if (!level.ContainsCell(x, y)) continue;
                    var cell = level.GetCellType(x, y);
                    if (cell != null && Sync.DamageSync.AmountOf(cell.contactDamage) > 0f) count++;
                }
            return count;
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