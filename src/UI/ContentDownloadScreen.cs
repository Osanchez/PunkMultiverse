using PunkMultiverse.Core;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

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
    /// straight after the Welcome so it runs while the player reads the lobby, so on a small pack
    /// or a warm cache it finishes before anyone could have pressed anything and this never
    /// appears at all. Either way it is gone before ship selection in co-op and drop selection in
    /// Battle Royale, because neither can begin until the run starts and the run cannot start
    /// while it is up. The gate is the mechanism; this only explains it.
    ///
    /// uGUI, not IMGUI. The first version drew with GUI.skin and looked like a debug overlay
    /// bolted onto the game — because it was. IMGUI cannot use the game's font at all: GUIStyle
    /// takes a legacy UnityEngine.Font and PUNK's is a TMP_FontAsset, so there is no amount of
    /// styling that would have fixed it. UiTheme exists precisely so injected UI is built from
    /// the vanilla assets (prompt frame, Font_Minimum, the 8-bit hud SDF font, the real button
    /// prefab) rather than approximating them.
    ///
    /// CANCEL leaves. Not "cancel and keep waiting" — there is nothing to wait for once you have
    /// refused the content, since the run cannot start without you and you cannot play with a
    /// weapon set that differs from everyone else's. Partial downloads stay on disk as
    /// digest-keyed .part files, so coming back later resumes.
    /// </summary>
    internal sealed class ContentDownloadScreen : MonoBehaviour
    {
        private const float PanelW = 780f, PanelH = 380f;
        private const float BarW = 620f, BarH = 26f;

        private GameObject _canvasGo;
        private GameObject _panel;
        private TMP_Text _title, _sub, _pct, _bytes, _hint;
        private RectTransform _barFill;
        private byte _shownPct = 255;      // force the first refresh

        private void Update()
        {
            var session = NetSession.Instance;
            bool want = session != null
                        && Content.ContentSync.Busy
                        && session.State < SessionState.InGame;

            if (!want)
            {
                if (_canvasGo != null && _canvasGo.activeSelf) _canvasGo.SetActive(false);
                return;
            }

            if (_canvasGo == null) Build();
            if (_canvasGo == null) return;               // theme not harvested yet; try next frame
            if (!_canvasGo.activeSelf) { _canvasGo.SetActive(true); _shownPct = 255; }

            Refresh();

            // A modal you cannot dismiss with Escape is a stuck lobby. Gamepad B as well —
            // the drop screen had to be made navigable after the fact, and a modal with one
            // button should be operable without a mouse from the start.
            var kb = Keyboard.current; var pad = Gamepad.current;
            if ((kb != null && kb.escapeKey.wasPressedThisFrame)
                || (pad != null && pad.buttonEast.wasPressedThisFrame))
            {
                UiTheme.PlayClick();
                Leave();
            }
        }

        private void Refresh()
        {
            byte pct = Content.ContentSync.LocalPercent;
            bool installing = Content.ContentSync.LocalState == Content.ContentState.Installing;

            if (pct != _shownPct)
            {
                _shownPct = pct;
                _pct.text = pct + "%";
                if (_barFill != null) _barFill.sizeDelta = new Vector2(BarW * (pct / 100f), BarH);
            }

            // Bytes, every frame: a percentage alone cannot tell "slow" from "stuck", and this is
            // the line a player screenshots when it IS stuck.
            long done = Content.ContentSync.BytesDone, need = Content.ContentSync.BytesNeeded;
            _bytes.text = installing ? "INSTALLING…"
                : need <= 0 ? "STARTING…"
                : $"{Mb(done)} OF {Mb(need)}";
        }

        private static string Mb(long bytes) =>
            bytes >= 1024 * 1024 ? $"{bytes / 1048576.0:0.0} MB" : $"{Mathf.Max(1, (int)(bytes / 1024))} KB";

        private void Leave()
        {
            Content.ContentSync.CancelLocal(NetSession.Instance);
            if (_canvasGo != null) _canvasGo.SetActive(false);
            Toast.Show("LEFT — CUSTOM CONTENT NOT DOWNLOADED", 5f);
            // The same exit the pause menu uses, so the disconnect-on-menu patch and every
            // teardown it drives run exactly as they do for a normal leave. Reimplementing the
            // exit here is how you end up with a half-torn-down session.
            MainMenuScene.Load();
        }

        private void Build()
        {
            // Its own canvas, ABOVE the lobby's 5000. The lobby is what this is explaining, so it
            // has to stay visible underneath rather than be replaced.
            _canvasGo = new GameObject("PunkMV_ContentScreen");
            _canvasGo.transform.SetParent(transform, false);
            var canvas = _canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 5100;
            var scaler = _canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 1f;   // match height, same as the lobby
            _canvasGo.AddComponent<GraphicRaycaster>();

            // Dimmed rather than opaque: the player IS in the lobby and should keep seeing it.
            // This is a wait, not a separate place — the opposite of the drop screen, where a
            // solid backdrop is honest because the player genuinely is not in the game yet.
            var dim = UiTheme.MakeImage(_canvasGo.transform, "Dim", new Color(0, 0, 0, 0.72f));
            UiTheme.Stretch(dim.rectTransform);

            var panel = UiTheme.MakeImage(_canvasGo.transform, "Panel",
                UiTheme.PromptSprite != null ? new Color(0.34f, 0.34f, 0.34f, 1f)
                                             : new Color(0.06f, 0.07f, 0.10f, 0.98f),
                UiTheme.PromptSprite);
            var prt = panel.rectTransform;
            prt.anchorMin = prt.anchorMax = prt.pivot = new Vector2(0.5f, 0.5f);
            prt.anchoredPosition = Vector2.zero;
            prt.sizeDelta = new Vector2(PanelW, PanelH);
            _panel = panel.gameObject;

            _title = UiTheme.MakeText(_panel.transform, "Title", "DOWNLOADING CUSTOM CONTENT",
                34, UiTheme.TextBright, UiTheme.PixelFont);
            Place(_title.rectTransform, 0, PanelH / 2 - 52, PanelW - 60, 46);

            _sub = UiTheme.MakeText(_panel.transform, "Sub",
                "THIS SERVER USES CUSTOM WEAPONS", 17, UiTheme.TextBody);
            Place(_sub.rectTransform, 0, PanelH / 2 - 92, PanelW - 60, 26);
            // Layout below is spaced so nothing overlaps the button. The first version put the
            // byte counts where the button lands and the button simply covered them -- invisible
            // in code, obvious the moment anyone looked at the screen.

            // Bar: a dark track with an accent fill whose WIDTH is the progress. Two images and
            // a sizeDelta — no shader, no sprite, nothing that can fail to load.
            var track = UiTheme.MakeImage(_panel.transform, "BarTrack", new Color(0.13f, 0.13f, 0.15f, 1f));
            Place(track.rectTransform, 0, 30, BarW, BarH);

            var fill = UiTheme.MakeImage(_panel.transform, "BarFill", UiTheme.Accent);
            var frt = fill.rectTransform;
            // Pinned to the track's LEFT edge so growing the width grows it rightwards.
            frt.anchorMin = frt.anchorMax = new Vector2(0.5f, 0.5f);
            frt.pivot = new Vector2(0f, 0.5f);
            frt.anchoredPosition = new Vector2(-BarW / 2f, 30);
            frt.sizeDelta = new Vector2(0, BarH);
            _barFill = frt;

            // HudFont, NOT PixelFont. Font_Minimum is a bitmap asset that mangles glyphs at small
            // sizes -- UiTheme.MakeButton documents the same trap and auto-switches below size 30.
            // At 30 this rendered "7%" as "*%": a progress readout that cannot be trusted to show
            // a digit is worse than no readout. Letters-only text (the title) is fine in it.
            _pct = UiTheme.MakeText(_panel.transform, "Pct", "0%", 26, UiTheme.Accent);
            Place(_pct.rectTransform, 0, -14, PanelW - 60, 40);

            _bytes = UiTheme.MakeText(_panel.transform, "Bytes", "", 16, UiTheme.TextBody);
            Place(_bytes.rectTransform, 0, -50, PanelW - 60, 24);

            UiTheme.MakeButton(_panel.transform, "Btn_Cancel", "CANCEL AND LEAVE",
                new Vector2(0, -PanelH / 2 + 80), new Vector2(360, 66), Leave, 26);

            _hint = UiTheme.MakeText(_panel.transform, "Hint",
                "CANCELLING RETURNS YOU TO THE MAIN MENU", 13, UiTheme.TextDim);
            Place(_hint.rectTransform, 0, -PanelH / 2 + 30, PanelW - 60, 20);
        }

        private static void Place(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(x, y);
            rt.sizeDelta = new Vector2(w, h);
        }

        private void OnDestroy()
        {
            if (_canvasGo != null) Destroy(_canvasGo);
        }
    }
}
