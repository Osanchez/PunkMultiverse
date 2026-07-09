using PunkMultiverse.Core;
using UnityEngine;
using UnityEngine.InputSystem;

namespace PunkMultiverse.UI
{
    /// <summary>
    /// F8 debug overlay (IMGUI — zero setup, dev-facing; the player-facing lobby is uGUI in M1).
    /// Shows session state, roster with RTT, and host/join/stop controls.
    /// </summary>
    public sealed class NetHud : MonoBehaviour
    {
        private bool _visible;
        private Rect _rect = new Rect(12, 12, 340, 100);
        private string _joinAddress = "";

        private void Update()
        {
            // F11: F5-F9 are taken by PunkSimController/PunkDebugKey, F10 by PunkDevReload.
            var kb = Keyboard.current;
            if (kb != null && kb[Key.F11].wasPressedThisFrame)
                _visible = !_visible;
        }

        private void OnGUI()
        {
            if (!_visible) return;
            _rect = GUILayout.Window(GetInstanceID(), _rect, DrawWindow, "Punk Multiverse [F11]");
        }

        private void DrawWindow(int id)
        {
            var session = NetSession.Instance;
            if (session == null) { GUILayout.Label("No session component."); return; }

            GUILayout.Label($"State: {session.State}   Transport: {NetConfig.Transport.Value}" +
                            (session.State != SessionState.Offline ? $"   {(session.IsHost ? "HOST" : "CLIENT")}" : ""));
            if (!string.IsNullOrEmpty(session.LastError))
                GUILayout.Label($"<color=#ff7070>{session.LastError}</color>");

            if (session.State == SessionState.Offline)
            {
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Host")) session.HostSession();
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                GUILayout.Label("Addr:", GUILayout.Width(38));
                _joinAddress = GUILayout.TextField(_joinAddress);
                if (GUILayout.Button("Join", GUILayout.Width(60)))
                {
                    var addr = string.IsNullOrWhiteSpace(_joinAddress)
                        ? $"{NetConfig.LoopbackHost.Value}:{NetConfig.LoopbackPort.Value}"
                        : _joinAddress.Trim();
                    session.JoinSession(addr);
                }
                GUILayout.EndHorizontal();
                GUILayout.Label(NetConfig.Transport.Value == "Loopback"
                    ? "Loopback: leave Addr empty for 127.0.0.1 default."
                    : "Steam: Addr = host's SteamID64 (lobby flow lands in M1).");
            }
            else
            {
                foreach (var p in session.Players)
                {
                    if (p == null) continue;
                    string rtt = p.IsLocal ? "local" : (p.RttMs >= 0 ? $"{p.RttMs} ms" : "…");
                    GUILayout.Label($"P{p.Slot + 1}  {p.Name}  [{rtt}]");
                }
                if (GUILayout.Button("Stop / Disconnect"))
                    session.StopSession("user request");
            }
            GUI.DragWindow();
        }
    }
}
