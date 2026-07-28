using PunkMultiverse.Core;
using UnityEngine;

namespace PunkMultiverse.UI
{
    /// <summary>
    /// The Battle Royale drop screen: pick a region before the match places you.
    ///
    /// Drawn with IMGUI for the same reason the match HUD is (UI/Toast.cs calls both from OnGUI) —
    /// it appears during LOADING, before the game scene's canvases are up, so there is no UI
    /// hierarchy to attach to yet. IMGUI has no such dependency.
    ///
    /// The heat is deliberately a COLOUR, never a number. Omar asked for "not actual numbers but a
    /// heat map" and that is the better read: an exact count invites arithmetic ("two there, so I
    /// win a 1v1"), while a colour communicates the only thing that actually matters — whether you
    /// are dropping somewhere contested. Green is empty, amber is filling, red is crowded, judged
    /// against the busiest region rather than an absolute, so it stays meaningful at any lobby size.
    /// </summary>
    internal static class SpawnSelectScreen
    {
        private static GUIStyle _title, _sub, _button, _chosen;

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

            // A dimmed backdrop: this is a modal decision, and the loading screen behind it is not
            // information anyone needs right now.
            var full = new Rect(0, 0, Screen.width, Screen.height);
            var prev = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.72f);
            GUI.DrawTexture(full, Texture2D.whiteTexture);
            GUI.color = prev;

            float panelW = Mathf.Min(760f, Screen.width - 80f);
            float rowH = 46f, gap = 8f;
            float panelH = 150f + count * (rowH + gap);
            var panel = new Rect((Screen.width - panelW) * 0.5f,
                Mathf.Max(30f, (Screen.height - panelH) * 0.5f), panelW, panelH);

            GUI.Label(new Rect(panel.x, panel.y, panel.width, 40f), "CHOOSE YOUR DROP", _title);

            int left = Mathf.CeilToInt(Modes.BattleRoyaleSpawnSelect.SecondsLeft);
            string sub = Modes.BattleRoyaleSpawnSelect.LocalHasChosen
                ? $"WAITING FOR THE OTHERS — {left}s"
                : $"PICK A REGION — {left}s, OR ONE IS PICKED FOR YOU";
            GUI.Label(new Rect(panel.x, panel.y + 38f, panel.width, 26f), sub, _sub);

            // Busiest region sets the scale, so the colours mean "relative to where everyone else
            // is going" rather than an arbitrary threshold that breaks at 2 players or at 16.
            int busiest = 0;
            for (int i = 0; i < count; i++) busiest = Mathf.Max(busiest, options[i].Picks);

            float y = panel.y + 84f;
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
                float fill = busiest <= 0 ? 0f : Mathf.Clamp01(option.Picks / (float)busiest);
                GUI.color = HeatColor(option.Picks, busiest);
                // Always show a sliver so an empty region still reads as "green and open" rather
                // than as a missing bar.
                GUI.DrawTexture(new Rect(heat.x, heat.y, Mathf.Max(10f, heat.width * fill), heat.height),
                    Texture2D.whiteTexture);
                GUI.color = prev;
            }
        }

        private static Color HeatColor(int picks, int busiest)
        {
            if (picks <= 0) return Empty;
            if (busiest <= 1) return Filling;
            float t = Mathf.Clamp01(picks / (float)busiest);
            return t < 0.5f ? Color.Lerp(Empty, Filling, t * 2f)
                            : Color.Lerp(Filling, Crowded, (t - 0.5f) * 2f);
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
        }
    }
}
