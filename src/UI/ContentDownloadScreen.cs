using PunkMultiverse.Core;
using UnityEngine;

namespace PunkMultiverse.UI
{
    /// <summary>
    /// "DOWNLOADING CUSTOM CONTENT" — the modal a joiner sees while the host's weapons transfer.
    ///
    /// It exists because the alternative is a player sitting in a lobby wondering why START is
    /// greyed out. The go-live gate already refuses to start a run for anyone still syncing
    /// (NetSession.HandleSetLobbyPrefs); without this screen that refusal is silent, and a silent
    /// refusal reads as a broken lobby.
    ///
    /// WHEN. Whenever this machine is downloading or installing — which is from the moment it
    /// joins, not from the moment the host presses START. The transfer is deliberately kicked off
    /// straight after the Welcome so it runs while the player reads the lobby, and on a small pack
    /// or a warm cache it finishes before anyone could have pressed anything. So the common case
    /// is that this screen never appears at all, and when it does appear it is because there was
    /// genuinely something to wait for. Either way it is always gone before ship selection in
    /// co-op and before drop selection in Battle Royale, because neither can begin until the run
    /// starts and the run cannot start while it is up.
    ///
    /// IMGUI, drawn from Toast.OnGUI, for the same reason the drop screen is: it has to be able to
    /// come up during Lobby AND Loading, and IMGUI needs no canvas to attach to.
    ///
    /// CANCEL leaves. Not "cancel and keep waiting" — there is nothing to wait for once you have
    /// refused the content, since the run cannot start without you and you cannot play with a
    /// weapon set that differs from everyone else's. So the button says what it does: LEAVE.
    /// Partial downloads stay on disk as digest-keyed .part files, so coming back later resumes.
    /// </summary>
    internal static class ContentDownloadScreen
    {
        private static GUIStyle _title, _sub, _pct, _button, _hint;
        private static bool _focused = true;     // the one button; keyboard/gamepad Submit fires it
        private static float _shownAt;

        private static readonly Color Barrel = new Color(0.13f, 0.13f, 0.15f);

        internal static void Draw()
        {
            var session = NetSession.Instance;
            if (session == null || !Content.ContentSync.Busy) { _shownAt = 0f; return; }
            // Never over a live game. Content is lobby work, and if this somehow ran mid-match the
            // right answer is to be invisible rather than to blind the player.
            if (session.State >= SessionState.InGame) return;

            if (_shownAt <= 0f) _shownAt = Time.unscaledTime;
            EnsureStyles();

            int w = Mathf.Min(560, Screen.width - 80);
            int h = 250;
            var panel = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);

            // Dimmed, not opaque: the player IS in the lobby and should keep seeing it. This is a
            // wait, not a separate place — the opposite of the drop screen, where a solid backdrop
            // is honest because the player genuinely is not in the game yet.
            var prev = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.78f);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = new Color(0.07f, 0.07f, 0.08f, 0.98f);
            GUI.DrawTexture(panel, Texture2D.whiteTexture);
            GUI.color = UiTheme.Accent;
            DrawBorder(panel, 2);
            GUI.color = prev;

            float x = panel.x + 28, iw = panel.width - 56;
            GUI.Label(new Rect(x, panel.y + 24, iw, 30), "DOWNLOADING CUSTOM CONTENT", _title);
            GUI.Label(new Rect(x, panel.y + 58, iw, 22),
                "This server uses custom weapons. Getting them now.", _sub);

            // ---- the bar ----------------------------------------------------------------------
            byte pct = Content.ContentSync.LocalPercent;
            var bar = new Rect(x, panel.y + 100, iw, 22);
            GUI.color = Barrel;
            GUI.DrawTexture(bar, Texture2D.whiteTexture);
            GUI.color = UiTheme.Accent;
            GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * (pct / 100f), bar.height), Texture2D.whiteTexture);
            GUI.color = new Color(1f, 1f, 1f, 0.18f);
            DrawBorder(bar, 1);
            GUI.color = prev;

            GUI.Label(new Rect(x, bar.y + 30, iw, 24), $"{pct}%", _pct);
            GUI.Label(new Rect(x, bar.y + 56, iw, 20), StatusLine(), _sub);

            // ---- leave -------------------------------------------------------------------------
            var btn = new Rect(panel.x + (panel.width - 220) / 2f, panel.y + panel.height - 62, 220, 34);
            bool hover = btn.Contains(Event.current.mousePosition);
            GUI.color = hover || _focused ? UiTheme.Accent : new Color(0.35f, 0.35f, 0.38f);
            DrawBorder(btn, 2);
            GUI.color = prev;
            GUI.Label(btn, "CANCEL AND LEAVE", _button);

            GUI.Label(new Rect(x, panel.y + panel.height - 26, iw, 18),
                "Cancelling returns you to the main menu.", _hint);

            bool clicked = GUI.Button(btn, GUIContent.none);
            if (clicked || SubmitPressed() || CancelPressed()) Leave(session);
        }

        /// <summary>What is actually happening, in bytes. A percentage alone cannot distinguish
        /// "slow" from "stuck", and this is the line a player screenshots when it IS stuck.</summary>
        private static string StatusLine()
        {
            if (Content.ContentSync.LocalState == Content.ContentState.Installing)
                return "Installing…";
            long done = Content.ContentSync.BytesDone, need = Content.ContentSync.BytesNeeded;
            if (need <= 0) return "Starting…";
            return $"{Mb(done)} of {Mb(need)}";
        }

        private static string Mb(long bytes) =>
            bytes >= 1024 * 1024 ? $"{bytes / 1048576.0:0.0} MB" : $"{Mathf.Max(1, (int)(bytes / 1024))} KB";

        private static void Leave(NetSession session)
        {
            Content.ContentSync.CancelLocal(session);
            _shownAt = 0f;
            Toast.Show("LEFT — CUSTOM CONTENT NOT DOWNLOADED", 5f);
            // Same exit the pause menu uses, so the disconnect-on-menu patch and every teardown it
            // drives run exactly as they do for a normal leave. Reimplementing the exit here is how
            // you end up with a half-torn-down session.
            MainMenuScene.Load();
        }

        // Keyboard/gamepad. The drop screen had to be made navigable after the fact; a modal with
        // one button should be operable without a mouse from the start. Escape/B also leaves,
        // because a modal you cannot dismiss with Escape is the definition of a stuck lobby.
        private static bool SubmitPressed()
        {
            if (Event.current.type != EventType.KeyDown) return false;
            var k = Event.current.keyCode;
            return k == KeyCode.Return || k == KeyCode.KeypadEnter || k == KeyCode.Space;
        }

        private static bool CancelPressed()
        {
            if (Event.current.type != EventType.KeyDown) return false;
            return Event.current.keyCode == KeyCode.Escape;
        }

        private static void DrawBorder(Rect r, int t)
        {
            GUI.DrawTexture(new Rect(r.x, r.y, r.width, t), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(r.x, r.yMax - t, r.width, t), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(r.x, r.y, t, r.height), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(r.xMax - t, r.y, t, r.height), Texture2D.whiteTexture);
        }

        private static void EnsureStyles()
        {
            if (_title != null) return;
            _title = new GUIStyle(GUI.skin.label)
            { alignment = TextAnchor.MiddleCenter, fontSize = 20, fontStyle = FontStyle.Bold };
            _title.normal.textColor = UiTheme.TextBright;
            _sub = new GUIStyle(GUI.skin.label)
            { alignment = TextAnchor.MiddleCenter, fontSize = 13 };
            _sub.normal.textColor = UiTheme.TextBody;
            _pct = new GUIStyle(GUI.skin.label)
            { alignment = TextAnchor.MiddleCenter, fontSize = 18, fontStyle = FontStyle.Bold };
            _pct.normal.textColor = UiTheme.Accent;
            _button = new GUIStyle(GUI.skin.label)
            { alignment = TextAnchor.MiddleCenter, fontSize = 15, fontStyle = FontStyle.Bold };
            _button.normal.textColor = UiTheme.TextBright;
            _hint = new GUIStyle(GUI.skin.label)
            { alignment = TextAnchor.MiddleCenter, fontSize = 11 };
            _hint.normal.textColor = UiTheme.TextDim;
        }
    }
}
