using PunkMultiverse.Core;
using UnityEngine;
using UnityEngine.InputSystem;

namespace PunkMultiverse.UI
{
    /// <summary>
    /// Net debug overlay (IMGUI — zero setup, dev-facing; the player-facing lobby is uGUI in M1).
    /// Shows session state, roster with RTT, per-slot ownership, and host/join/stop controls.
    /// F9 toggles it; F10 toggles sync diagnostics; F11 dumps the ownership table to the log.
    /// </summary>
    public sealed class NetHud : MonoBehaviour
    {
        private bool _visible;
        private Rect _rect = new Rect(12, 12, 340, 100);
        private string _joinAddress = "";

        // Telemetry sampled once a second from NetStats' cumulative counters.
        private float _nextSampleAt;
        private float _lastSampleAt;
        private long _lastIn, _lastOut;
        private int _lastFlips, _lastReleases;
        private float _inRate, _outRate, _flipsPerMin, _releasesPerMin;
        private readonly long[] _lastByType = new long[64];
        private string _topTypes = "";
        private string _ownSummary = "";

        private void Update()
        {
            // F9 = overlay, F10 = toggle sync diagnostics. (F11/F12 are Steam screenshot keys, so
            // avoid them.) With diagnostics on, ownership + render-state dumps happen automatically
            // and land in the log, which auto-sends to the webhook on game close — no manual dump key.
            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb[Key.F9].wasPressedThisFrame) _visible = !_visible;
                if (kb[Key.F10].wasPressedThisFrame)
                {
                    bool on = !NetConfig.SyncDiagnostics.Value;
                    NetConfig.SyncDiagnostics.Value = on;
                    Toast.Show(on ? "SYNC DIAGNOSTICS ON" : "SYNC DIAGNOSTICS OFF", 2.5f);
                    Plugin.Log.LogInfo($"[Diag] sync diagnostics {(on ? "ON" : "OFF")} (F10)");
                }
            }
            if (_visible && Time.unscaledTime >= _nextSampleAt) Sample();
        }

        private void Sample()
        {
            float dt = _lastSampleAt > 0f ? Mathf.Max(0.001f, Time.unscaledTime - _lastSampleAt) : float.MaxValue;
            _lastSampleAt = Time.unscaledTime;
            _nextSampleAt = Time.unscaledTime + 1f;

            _inRate = (NetStats.BytesIn - _lastIn) / dt;
            _outRate = (NetStats.BytesOut - _lastOut) / dt;
            _flipsPerMin = (NetStats.AuthFlips - _lastFlips) / dt * 60f;
            _releasesPerMin = (NetStats.AuthReleases - _lastReleases) / dt * 60f;
            _lastIn = NetStats.BytesIn;
            _lastOut = NetStats.BytesOut;
            _lastFlips = NetStats.AuthFlips;
            _lastReleases = NetStats.AuthReleases;

            // Top inbound message types over the window — where the bandwidth actually goes.
            (int type, long delta) a = (0, 0), b = (0, 0), c = (0, 0);
            for (int i = 0; i < _lastByType.Length; i++)
            {
                long d = NetStats.BytesInByType[i] - _lastByType[i];
                _lastByType[i] = NetStats.BytesInByType[i];
                if (d > a.delta) { c = b; b = a; a = (i, d); }
                else if (d > b.delta) { c = b; b = (i, d); }
                else if (d > c.delta) c = (i, d);
            }
            _topTypes = a.delta <= 0 ? "" : $"{(Protocol.MsgType)a.type} {a.delta / dt / 1024f:0.0}"
                + (b.delta > 0 ? $", {(Protocol.MsgType)b.type} {b.delta / dt / 1024f:0.0}" : "")
                + (c.delta > 0 ? $", {(Protocol.MsgType)c.type} {c.delta / dt / 1024f:0.0}" : "")
                + " KB/s";

            // Sampled once a second — OwnershipSummary walks the owner table, too heavy per-frame.
            _ownSummary = NetSession.Instance != null && NetSession.Instance.State != SessionState.Offline
                ? NetDiag.OwnershipSummary() : "";
        }

        private void OnGUI()
        {
            if (!_visible) return;
            _rect = GUILayout.Window(GetInstanceID(), _rect, DrawWindow, "Punk Multiverse [F9]");
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
                GUILayout.Label($"Net  in {_inRate / 1024f:0.0} KB/s   out {_outRate / 1024f:0.0} KB/s");
                if (!string.IsNullOrEmpty(_topTypes))
                    GUILayout.Label($"Top in: {_topTypes}");
                GUILayout.Label($"Auth  flips {NetStats.AuthFlips} ({_flipsPerMin:0}/min)   " +
                                $"releases {NetStats.AuthReleases} ({_releasesPerMin:0}/min)");
                if (!string.IsNullOrEmpty(_ownSummary))
                    GUILayout.Label($"Owns  {_ownSummary}");
                GUILayout.Label($"Diag  {(NetConfig.SyncDiagnostics.Value ? "<color=#7CFC70>ON</color>" : "OFF")}" +
                                "   [F10 toggle · auto-dumps to log]");
                if (GUILayout.Button("Stop / Disconnect"))
                    session.StopSession("user request");
            }
            GUI.DragWindow();
        }
    }
}
