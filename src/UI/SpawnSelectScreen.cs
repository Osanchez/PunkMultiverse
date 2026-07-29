using PunkMultiverse.Core;
using UnityEngine;

namespace PunkMultiverse.UI
{
    /// <summary>
    /// The Battle Royale drop screen: pick a region before the match places you.
    ///
    /// Drawn with IMGUI for the same reason the match HUD is (UI/Toast.cs calls both from OnGUI):
    /// it comes up the instant the match goes live, before anything of ours has a canvas to attach
    /// to, and IMGUI needs none.
    ///
    /// The backdrop is FULLY OPAQUE and the ship is parked out in the void behind it
    /// (BattleRoyaleSpawnSelect.HoldInTheVoid), so the player is genuinely not in the game while
    /// this is up — which is both the fiction and, after an instant game over caused by choosing
    /// while standing on a pad full of enemies, the safety.
    ///
    /// The heat is deliberately a COLOUR, never a number. Omar asked for "not actual numbers but a
    /// heat map" and that is the better read: an exact count invites arithmetic ("two there, so I
    /// win a 1v1"), while a colour communicates the only thing that actually matters — whether you
    /// are dropping somewhere contested. Green is empty, amber is filling, red is crowded, judged
    /// against the busiest region rather than an absolute, so it stays meaningful at any lobby size.
    /// </summary>
    internal static class SpawnSelectScreen
    {
        private static GUIStyle _title, _sub, _button, _chosen, _clock, _clockUrgent;

        private static readonly Color Empty = new Color(0.35f, 0.85f, 0.40f);
        private static readonly Color Filling = new Color(0.95f, 0.80f, 0.25f);
        private static readonly Color Crowded = new Color(0.95f, 0.30f, 0.25f);

        internal static void Draw()
        {
            if (!Modes.BattleRoyaleSpawnSelect.ShouldShow) return;
            EnsureStyles();

            var options = Modes.BattleRoyaleSpawnSelect.AvailableOptions;
            int count = options.Count;
            if (count == 0) return;

            // FULLY OPAQUE, not dimmed. The ship is parked out in the void while this is up
            // (BattleRoyaleSpawnSelect.HoldInTheVoid) and the player is not in the game yet — a
            // see-through backdrop would show them empty blackness anyway and, worse, imply they
            // are somewhere. Solid black is the honest frame for "you have not dropped".
            var full = new Rect(0, 0, Screen.width, Screen.height);
            var prev = GUI.color;
            GUI.color = Color.black;
            GUI.DrawTexture(full, Texture2D.whiteTexture);
            GUI.color = prev;

            float panelW = Mathf.Min(760f, Screen.width - 80f);
            float rowH = 46f, gap = 8f;
            float panelH = 172f + count * (rowH + gap);
            // Clear of the top edge: match announcements ("BATTLE ROYALE - 2 PLAYERS...") toast in
            // at y=8 and were landing on the title.
            const float topMargin = 96f;
            var panel = new Rect((Screen.width - panelW) * 0.5f,
                Mathf.Max(topMargin, (Screen.height - panelH) * 0.5f), panelW, panelH);

            GUI.Label(new Rect(panel.x, panel.y, panel.width, 40f), "SELECT SPAWN", _title);

            // The clock is the loudest thing after the title: it is the only pressure in the
            // screen, and it counts real seconds down from BrChooseSpawnSeconds.
            float secs = Modes.BattleRoyaleSpawnSelect.SecondsLeft;
            int left = Mathf.CeilToInt(secs);
            var clockStyle = secs <= 5f ? _clockUrgent : _clock;
            GUI.Label(new Rect(panel.x, panel.y + 34f, panel.width, 44f), $"{left}", clockStyle);
            GUI.Label(new Rect(panel.x, panel.y + 74f, panel.width, 24f),
                "A REGION IS CHOSEN FOR YOU WHEN THIS REACHES ZERO", _sub);

            // Scaled against the NUMBER OF PLAYERS, not against the busiest region. Scaling to the
            // busiest made the first pick in a lobby fill its bar completely — one player out of
            // two read as "everybody is here" (Omar, 2026-07-28: "before I even select a spawn
            // there is a bar"). The data was right and the picture was not. Against the roster,
            // one of two is half a bar, which is what it actually means.
            int roster = 0;
            var session = Core.NetSession.Instance;
            if (session != null)
                foreach (var p in session.Players)
                    if (p != null && p.Connected && !p.IsCoordinator) roster++;
            roster = Mathf.Max(1, roster);

            float y = panel.y + 106f;
            for (int i = 0; i < count; i++)
            {
                var option = options[i];
                var row = new Rect(panel.x, y, panel.width, rowH);
                y += rowH + gap;

                bool mine = Modes.BattleRoyaleSpawnSelect.LocalHasChosen
                            && Modes.BattleRoyaleSpawnSelect.LocalChoice == option.BiomeId;

                GUI.backgroundColor = mine ? new Color(1f, 1f, 1f, 0.95f) : new Color(1f, 1f, 1f, 0.35f);
                if (GUI.Button(row, GUIContent.none) && !Modes.BattleRoyaleSpawnSelect.LocalHasChosen)
                    Modes.BattleRoyaleSpawnSelect.Choose(option.BiomeId);
                GUI.backgroundColor = Color.white;

                // The biome's own map colour as a swatch, so the button ties to what the map shows.
                var swatch = new Rect(row.x + 10f, row.y + 11f, 24f, 24f);
                prev = GUI.color;
                GUI.color = option.Color;
                GUI.DrawTexture(swatch, Texture2D.whiteTexture);
                GUI.color = prev;

                GUI.Label(new Rect(row.x + 46f, row.y + 10f, row.width - 220f, 26f),
                    option.Name.ToUpperInvariant(), mine ? _chosen : _button);

                // Heat bar, right-aligned.
                var heat = new Rect(row.xMax - 160f, row.y + 17f, 140f, 12f);
                prev = GUI.color;
                GUI.color = new Color(1f, 1f, 1f, 0.15f);
                GUI.DrawTexture(heat, Texture2D.whiteTexture);
                // EMPTY MEANS EMPTY — no minimum sliver. It used to always draw a small green mark
                // so a quiet region still "read as green", and that was a lie the eye believes:
                // every untouched region looked like it already had somebody in it (Omar,
                // 2026-07-28: "sometimes reports players already there"). A bar with nothing in it
                // is the clearest possible way to say nobody has picked this.
                if (option.Picks > 0)
                {
                    float share = Mathf.Clamp01(option.Picks / (float)roster);
                    GUI.color = HeatColor(share);
                    GUI.DrawTexture(new Rect(heat.x, heat.y, Mathf.Max(14f, heat.width * share), heat.height),
                        Texture2D.whiteTexture);
                }
                GUI.color = prev;
            }
        }

        /// <summary>Colour by the SHARE OF THE LOBBY heading there: green while a region is a small
        /// part of where people are going, red once most of them are. An absolute threshold cannot
        /// work at both 2 players and 16, and scaling to the busiest region makes the very first
        /// pick look maximal.</summary>
        private static Color HeatColor(float share)
        {
            if (share <= 0f) return Empty;
            return share < 0.5f ? Color.Lerp(Empty, Filling, share * 2f)
                                : Color.Lerp(Filling, Crowded, (share - 0.5f) * 2f);
        }

        private static void EnsureStyles()
        {
            if (_title != null) return;
            _title = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.UpperCenter, fontSize = 26, fontStyle = FontStyle.Bold,
            };
            _title.normal.textColor = Color.white;
            _sub = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.UpperCenter, fontSize = 15,
            };
            _sub.normal.textColor = new Color(0.85f, 0.85f, 0.85f);
            _button = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleLeft, fontSize = 18, fontStyle = FontStyle.Bold,
            };
            _button.normal.textColor = Color.white;
            _chosen = new GUIStyle(_button);
            _chosen.normal.textColor = new Color(0.55f, 1f, 0.6f);
            _clock = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.UpperCenter, fontSize = 40, fontStyle = FontStyle.Bold,
            };
            _clock.normal.textColor = Color.white;
            _clockUrgent = new GUIStyle(_clock);
            _clockUrgent.normal.textColor = new Color(1f, 0.35f, 0.28f);
        }
    }
}
