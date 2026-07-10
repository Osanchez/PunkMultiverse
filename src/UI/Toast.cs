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

        private void OnGUI()
        {
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
