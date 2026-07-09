using UnityEngine;

namespace PunkMultiverse.Core
{
    /// <summary>The 8 selectable ship colors. Index travels on the wire as a byte.</summary>
    public static class PlayerColors
    {
        public static readonly Color[] All =
        {
            new Color(0.95f, 0.30f, 0.24f), // red
            new Color(0.23f, 0.55f, 0.96f), // blue
            new Color(0.30f, 0.85f, 0.39f), // green
            new Color(0.98f, 0.83f, 0.22f), // yellow
            new Color(0.68f, 0.40f, 0.94f), // purple
            new Color(0.98f, 0.55f, 0.18f), // orange
            new Color(0.25f, 0.88f, 0.86f), // cyan
            new Color(0.96f, 0.48f, 0.78f), // pink
        };

        public static Color Get(int index) => All[Mathf.Abs(index) % All.Length];
    }
}
