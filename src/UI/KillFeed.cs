using System.Collections.Generic;
using UnityEngine;

namespace PunkMultiverse.UI
{
    /// <summary>
    /// The kill feed: a stack of recent deaths in the TOP RIGHT, on its own channel.
    ///
    /// Deaths used to go through <see cref="Toast"/>, which is deliberately single-slot — "one at a
    /// time; a new Show replaces the current one". So the moment a player died, their own
    /// "YOU WERE KILLED BY X" was overwritten a frame later by Battle Royale's
    /// "ELIMINATED — YOU PLACED #N OF M", and the one player most entitled to know what killed them
    /// saw nothing at all (Omar, 2026-07-29: "I didn't see the player killed by toast... didn't see
    /// anything pop up for my client who died"). Every kill was competing for the same single line
    /// as the ring warnings and the alive counter, so any of them could erase any other.
    ///
    /// Separating the channels is the fix, and it is also how the genre does it: kills stack on the
    /// right and expire on their own, while the centre strip keeps the one-off announcements (ring
    /// closing, how many remain, the placement screen). Neither can delete the other now.
    /// </summary>
    public sealed class KillFeed : MonoBehaviour
    {
        private sealed class Entry
        {
            internal string Text;
            internal float Until;
            internal bool Mine;     // this death involves the local player — worth emphasising
        }

        private const int MaxVisible = 5;
        private const float DefaultSeconds = 7f;
        private static readonly List<Entry> Entries = new List<Entry>();
        private GUIStyle _style;

        /// <summary>Add a line to the feed. `mine` marks a death that involves the local player, so
        /// it reads differently from the ambient traffic of other people dying.</summary>
        public static void Show(string text, bool mine = false, float seconds = DefaultSeconds)
        {
            if (string.IsNullOrEmpty(text)) return;
            Entries.Add(new Entry { Text = text, Until = Time.unscaledTime + seconds, Mine = mine });
            // Oldest first: the list is drawn newest-at-top, so trimming from the front keeps the
            // most recent kills and drops what is already scrolling away.
            while (Entries.Count > MaxVisible) Entries.RemoveAt(0);
            Plugin.Log.LogInfo($"[KillFeed] {text}");
        }

        /// <summary>Wipe the feed — a new run must not open with last run's deaths on screen.</summary>
        public static void Clear() => Entries.Clear();

        private void OnGUI()
        {
            if (Entries.Count == 0) return;
            float now = Time.unscaledTime;
            for (int i = Entries.Count - 1; i >= 0; i--)
                if (now > Entries[i].Until) Entries.RemoveAt(i);
            if (Entries.Count == 0) return;

            if (_style == null)
                _style = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleRight,
                    fontSize = 16,
                    fontStyle = FontStyle.Bold,
                };

            const float width = 460f, lineHeight = 24f, margin = 12f;
            float x = Screen.width - width - margin;
            // Below the F9 overlay's corner and clear of the top-centre ring clock.
            float y = margin;
            for (int i = Entries.Count - 1; i >= 0; i--)   // newest at the top
            {
                var e = Entries[i];
                // Fade the last second so lines leave rather than vanish mid-read.
                float remaining = e.Until - now;
                float alpha = remaining < 1f ? Mathf.Clamp01(remaining) : 1f;
                var rect = new Rect(x, y, width, lineHeight);

                GUI.color = new Color(0f, 0f, 0f, 0.75f * alpha);
                GUI.Label(new Rect(rect.x + 1, rect.y + 1, rect.width, rect.height), e.Text, _style);
                GUI.color = e.Mine
                    ? new Color(1f, 0.45f, 0.35f, alpha)      // you were involved
                    : new Color(0.95f, 0.95f, 0.95f, alpha);  // someone else's business
                GUI.Label(rect, e.Text, _style);
                y += lineHeight;
            }
            GUI.color = Color.white;
        }
    }
}
