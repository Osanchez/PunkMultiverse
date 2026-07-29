using UnityEngine;

namespace PunkMultiverse.UI
{
    /// <summary>Brief top-center announcement ("HOST LEFT — X IS NOW HOST"). One at a time;
    /// a new Show replaces the current one.</summary>
    public sealed class Toast : MonoBehaviour
    {
        private static string _text;
        private static float _until;
        private GUIStyle _style;

        public static void Show(string text, float seconds)
        {
            _text = text;
            _until = Time.unscaledTime + seconds;
            Plugin.Log.LogInfo($"[Toast] {text}");
        }

        private void Update()
        {
            // The drop-screen holding pen must win EVERY frame, including Loading frames where the
            // net tick isn't running — vanilla's async ship placement lands exactly there. This
            // component exists in every state, which is why the hold lives here and not in a tick.
            Modes.BattleRoyaleSpawnSelect.HoldPendingDeploy();
            // And the mirror of the pen: once deployed, hold the ship at its pad until the
            // terrain it landed on has streamed in. Same reason this lives here — it must run
            // on render frames, which is when streaming finishes.
            Modes.BattleRoyaleSpawnSelect.TickSettle();
        }

        private void OnGUI()
        {
            // The Battle Royale match clock / ring readout shares this OnGUI host: it belongs in
            // the same top-center strip as toasts and needs no separate component.
            Modes.BattleRoyale.DrawHud();
            SpawnSelectScreen.Draw(); // pre-run drop selection; no-ops outside that window
            if (string.IsNullOrEmpty(_text) || Time.unscaledTime > _until) return;
            if (_style == null)
                _style = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 22,
                    fontStyle = FontStyle.Bold,
                };
            var rect = new Rect(0, 48, Screen.width, 32);
            GUI.color = new Color(0f, 0f, 0f, 0.75f);
            GUI.Label(new Rect(rect.x + 1, rect.y + 1, rect.width, rect.height), _text, _style);
            GUI.color = new Color(0.98f, 0.63f, 0.24f);
            GUI.Label(rect, _text, _style);
            GUI.color = Color.white;
        }
    }
}
