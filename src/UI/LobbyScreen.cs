using System.Collections.Generic;
using System.Linq;
using PunkMultiverse.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PunkMultiverse.UI
{
    /// <summary>
    /// The player-facing lobby screen (NEW-style), built entirely at runtime on its own overlay
    /// canvas — no asset bundles. Two panels:
    ///   CONNECT  — Host Lobby / Join from Clipboard / Back
    ///   LOBBY    — 4 player rows (name, color swatch, ready), Copy Code, Invite, Ready, Start (host)
    /// Auto-shows when a session reaches Lobby state; hides on game start / stop.
    /// Layout: panel is 760x700; texts anchor to the panel top (negative y down), buttons to the
    /// panel center (positive y up).
    /// </summary>
    public sealed class LobbyScreen : MonoBehaviour
    {
        private static readonly Color PanelBg = new Color(0.06f, 0.07f, 0.10f, 0.96f);
        private static readonly Color RowBg = new Color(1f, 1f, 1f, 0.05f);
        private static readonly Color ButtonBg = new Color(0.16f, 0.19f, 0.26f, 1f);
        private static readonly Color ButtonHover = new Color(0.24f, 0.29f, 0.40f, 1f);
        private static readonly Color Accent = new Color(0.98f, 0.55f, 0.18f);

        private GameObject _canvasGo;
        private GameObject _connectPanel;
        private GameObject _lobbyPanel;
        private GameObject _seedPanel;
        private GameObject _resumeButton;
        private TMP_InputField _seedInput;
        private bool _seedSetupOpen;
        private TMP_Text _statusText;
        private TMP_Text _versionText;
        private TMP_Text _codeText;
        private TMP_Text _copyChipLabel;
        private RectTransform _copyChipRt;
        private Coroutine _copyFlash;
        private TMP_Text _seedText;
        private TMP_Text _readyButtonLabel;
        private GameObject _startButton;
        private GameObject _inviteButton;
        private readonly List<RowWidgets> _rows = new List<RowWidgets>();
        private TMP_FontAsset _font;
        private byte _localColor;
        private bool _localReady;

        private sealed class RowWidgets
        {
            public Image Swatch;
            public TMP_Text Name;
            public TMP_Text Status;
            public GameObject Kick;
        }

        public bool Visible => _canvasGo != null && _canvasGo.activeSelf;

        private void Start()
        {
            var session = NetSession.Instance;
            if (session != null)
            {
                session.RosterChanged += Refresh;
                session.StateChanged += OnSessionState;
            }
        }

        private void OnDestroy()
        {
            var session = NetSession.Instance;
            if (session != null)
            {
                session.RosterChanged -= Refresh;
                session.StateChanged -= OnSessionState;
            }
            SetMenuBlocked(false);
            if (_canvasGo != null) Destroy(_canvasGo);
        }

        private SessionState _lastState;

        private void OnSessionState(SessionState state)
        {
            if (state == SessionState.Lobby)
            {
                bool quiet = NetSession.Instance != null && NetSession.Instance.ConsumeQuietLobby();
                if (!Visible && !quiet) Show();
            }
            if (state == SessionState.Loading || state == SessionState.InGame) Hide();
            // Session died mid-run (host quit / connection lost): surface it instead of silently
            // letting the player continue in a now-solo world.
            if (state == SessionState.Offline && _lastState == SessionState.InGame
                && !string.IsNullOrEmpty(NetSession.Instance?.LastError))
                Show();
            _lastState = state;
            Refresh();
        }

        public void Show()
        {
            if (_canvasGo == null) Build();
            _seedSetupOpen = false;
            _canvasGo.SetActive(true);
            SetMenuBlocked(true);
            Refresh();
        }

        public void Hide()
        {
            _canvasGo?.SetActive(false);
            SetMenuBlocked(false);
        }

        // The dim layer swallows mouse rays, but keyboard/gamepad NAVIGATION still reaches the
        // menu behind — especially after a panel switch deactivates the selected button and the
        // EventSystem re-grabs a menu selectable. Block the menu's interactivity outright.
        private CanvasGroup _menuBlock;

        private void SetMenuBlocked(bool blocked)
        {
            try
            {
                if (blocked)
                {
                    if (_menuBlock == null) // also re-finds after a scene reload destroyed it
                    {
                        var selector = GameObject.Find("GameModeSelector");
                        var canvas = selector != null ? selector.GetComponentInParent<Canvas>() : null;
                        if (canvas == null) return;
                        _menuBlock = canvas.GetComponent<CanvasGroup>();
                        if (_menuBlock == null) _menuBlock = canvas.gameObject.AddComponent<CanvasGroup>();
                    }
                    _menuBlock.interactable = false;
                }
                else if (_menuBlock != null)
                {
                    _menuBlock.interactable = true;
                }
            }
            catch { }
        }

        public void Toggle()
        {
            if (Visible) Hide(); else Show();
        }

        // ---------------------------------------------------------------- building

        private void Build()
        {
            _font = TMP_Settings.defaultFontAsset;
            if (_font == null)
            {
                var fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
                if (fonts.Length > 0) _font = fonts[0];
            }

            _canvasGo = new GameObject("PunkMV_LobbyScreen");
            _canvasGo.transform.SetParent(transform, false);
            var canvas = _canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 5000;
            var scaler = _canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            _canvasGo.AddComponent<GraphicRaycaster>();

            // Dim background that swallows clicks to the menu behind.
            var dim = MakeImage(_canvasGo.transform, "Dim", new Color(0, 0, 0, 0.6f));
            Stretch(dim.rectTransform);

            // Center panel.
            var panel = MakeImage(_canvasGo.transform, "Panel", PanelBg);
            var prt = panel.rectTransform;
            prt.anchorMin = prt.anchorMax = new Vector2(0.5f, 0.5f);
            prt.pivot = new Vector2(0.5f, 0.5f);
            prt.sizeDelta = new Vector2(760, 700);

            MakeText(panel.transform, "Title", "PUNK MULTIVERSE", 42, Accent, y: -10, height: 60);
            _versionText = MakeText(panel.transform, "Version", $"mod v{Plugin.Version}", 16, new Color(1, 1, 1, 0.45f), y: -56, height: 22);
            _statusText = MakeText(panel.transform, "Status", "", 20, new Color(1f, 0.55f, 0.45f), y: -660, height: 36);

            BuildConnectPanel(panel.transform);
            BuildLobbyPanel(panel.transform);
            BuildSeedPanel(panel.transform);
            _lobbyPanel.SetActive(false);
            _seedPanel.SetActive(false);
        }

        private void BuildConnectPanel(Transform parent)
        {
            _connectPanel = MakeGroup(parent, "Connect");
            MakeText(_connectPanel.transform, "Hint",
                "Host a lobby and send the code to your friends,\nor copy their code and join from clipboard.",
                22, Color.white, y: -140, height: 70);
            MakeButton(_connectPanel.transform, "HOST LOBBY", new Vector2(0, 60), new Vector2(420, 64),
                ShowSeedSetup); // seed screen first, then the lobby
            MakeButton(_connectPanel.transform, "JOIN FROM CLIPBOARD", new Vector2(0, -20), new Vector2(420, 64),
                () => NetSession.Instance.JoinByCode(null));
            _resumeButton = ButtonRoot(MakeButton(_connectPanel.transform, "RESUME LAST RUN", new Vector2(0, -100),
                new Vector2(420, 52), () => NetSession.Instance.HostResume()));
            MakeButton(_connectPanel.transform, "BACK", new Vector2(0, -170), new Vector2(220, 52), Hide);
        }

        private void BuildLobbyPanel(Transform parent)
        {
            _lobbyPanel = MakeGroup(parent, "Lobby");

            _codeText = MakeText(_lobbyPanel.transform, "Code", "", 24, Color.white, y: -74, height: 30);

            // The code line copies itself: click anywhere on it, or the small COPY chip that
            // rides at its right edge (positioned per-frame in Refresh — the code width varies).
            var codeBtn = _codeText.gameObject.AddComponent<Button>();
            codeBtn.targetGraphic = _codeText;
            codeBtn.onClick.AddListener(CopyCodeWithFeedback);
            _copyChipLabel = MakeButton(_codeText.transform, "COPY", Vector2.zero, new Vector2(84, 26),
                CopyCodeWithFeedback);
            _copyChipRt = (RectTransform)_copyChipLabel.transform.parent;
            _copyChipRt.gameObject.SetActive(false);

            // World seed: read-only here — it's chosen on the GAME SETTINGS screen before hosting.
            _seedText = MakeText(_lobbyPanel.transform, "Seed", "", 20, new Color(1f, 1f, 1f, 0.85f), y: -106, height: 26);

            MakeButton(_lobbyPanel.transform, "COPY CODE", new Vector2(-140, 186), new Vector2(240, 48),
                CopyCodeWithFeedback);
            _inviteButton = ButtonRoot(MakeButton(_lobbyPanel.transform, "INVITE FRIENDS", new Vector2(140, 186),
                new Vector2(240, 48), () => NetSession.Instance.Lobby?.OpenInviteOverlay()));

            // Player rows: top-anchored band from y=-196 to y=-452.
            for (int i = 0; i < NetSession.MaxPlayers; i++)
            {
                var row = new RowWidgets();
                var bg = MakeImage(_lobbyPanel.transform, $"Row{i}", RowBg);
                var rt = bg.rectTransform;
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
                rt.pivot = new Vector2(0.5f, 1f);
                rt.anchoredPosition = new Vector2(0, -196 - i * 66);
                rt.sizeDelta = new Vector2(640, 58);

                var swatch = MakeImage(bg.transform, "Swatch", Color.gray);
                var srt = swatch.rectTransform;
                srt.anchorMin = srt.anchorMax = new Vector2(0f, 0.5f);
                srt.pivot = new Vector2(0f, 0.5f);
                srt.anchoredPosition = new Vector2(14, 0);
                srt.sizeDelta = new Vector2(34, 34);
                row.Swatch = swatch;

                row.Name = MakeText(bg.transform, "Name", "", 24, Color.white, y: 0, height: 58);
                row.Name.alignment = TextAlignmentOptions.MidlineLeft;
                StretchWithMargins(row.Name.rectTransform, 64, 200);

                row.Status = MakeText(bg.transform, "RowStatus", "", 20, Color.white, y: 0, height: 58);
                row.Status.alignment = TextAlignmentOptions.MidlineRight;
                StretchWithMargins(row.Status.rectTransform, 64, 92); // leave room for KICK

                int slotIndex = i;
                row.Kick = ButtonRoot(MakeButton(bg.transform, "KICK", new Vector2(274, 0), new Vector2(64, 36),
                    () => NetSession.Instance.KickPlayer((byte)slotIndex)));
                row.Kick.SetActive(false);

                _rows.Add(row);
            }

            MakeText(_lobbyPanel.transform, "ColorLabel", "SHIP COLOR", 18, new Color(1, 1, 1, 0.6f), y: -462, height: 26);
            for (int c = 0; c < PlayerColors.All.Length; c++)
            {
                int colorIndex = c;
                var label = MakeButton(_lobbyPanel.transform, "", new Vector2(-158 + c * 45, -160), new Vector2(38, 38),
                    () => SetLocalColor((byte)colorIndex));
                ButtonRoot(label).GetComponent<Image>().color = PlayerColors.Get(colorIndex);
            }

            _readyButtonLabel = MakeButton(_lobbyPanel.transform, "READY", new Vector2(-140, -216), new Vector2(240, 56),
                ToggleReady);
            _startButton = ButtonRoot(MakeButton(_lobbyPanel.transform, "START GAME", new Vector2(140, -216),
                new Vector2(240, 56), StartGame));

            MakeButton(_lobbyPanel.transform, "LEAVE", new Vector2(0, -282), new Vector2(180, 44), Leave);
        }

        // ---------------------------------------------------------------- seed setup (pre-lobby)

        private TMP_Text _ffToggleLabel;
        private TMP_Text _hpToggleLabel;
        private bool _friendlyFire;
        private bool _hpScaling = true;

        private void BuildSeedPanel(Transform parent)
        {
            _seedPanel = MakeGroup(parent, "GameSettings");
            MakeText(_seedPanel.transform, "SetupTitle", "GAME SETTINGS", 28, Color.white, y: -130, height: 40);
            MakeText(_seedPanel.transform, "SeedHint",
                "WORLD SEED — type one, PASTE from clipboard, or leave empty for random.",
                18, new Color(1f, 1f, 1f, 0.7f), y: -176, height: 30);

            _seedInput = MakeSeedInput(_seedPanel.transform, new Vector2(0, 70), new Vector2(420, 56));

            MakeButton(_seedPanel.transform, "PASTE", new Vector2(-110, 0), new Vector2(200, 48), PasteSeedIntoInput);
            MakeButton(_seedPanel.transform, "RANDOM", new Vector2(110, 0), new Vector2(200, 48),
                () => { if (_seedInput != null) _seedInput.text = ""; });

            _ffToggleLabel = MakeButton(_seedPanel.transform, "FRIENDLY FIRE: OFF", new Vector2(0, -60),
                new Vector2(420, 52), ToggleFriendlyFire);
            _hpToggleLabel = MakeButton(_seedPanel.transform, "ENEMY HP SCALING: ON", new Vector2(0, -120),
                new Vector2(420, 52), ToggleHpScaling);

            MakeButton(_seedPanel.transform, "HOST LOBBY", new Vector2(0, -190), new Vector2(420, 64), HostWithSeed);
            MakeButton(_seedPanel.transform, "BACK", new Vector2(0, -255), new Vector2(220, 52),
                () => { _seedSetupOpen = false; Refresh(); });
        }

        private void ToggleFriendlyFire()
        {
            _friendlyFire = !_friendlyFire;
            if (_ffToggleLabel == null) return;
            _ffToggleLabel.text = _friendlyFire ? "FRIENDLY FIRE: ON" : "FRIENDLY FIRE: OFF";
            _ffToggleLabel.color = _friendlyFire ? new Color(1f, 0.72f, 0.3f) : Color.white;
        }

        // Base Health * (1 + (EnemyHealthScalePerPlayer * number of players)), counted at
        // START GAME; the per-player value (default 0.25) lives in config.cfg.
        private void ToggleHpScaling()
        {
            _hpScaling = !_hpScaling;
            if (_hpToggleLabel == null) return;
            _hpToggleLabel.text = _hpScaling ? "ENEMY HP SCALING: ON" : "ENEMY HP SCALING: OFF";
            _hpToggleLabel.color = _hpScaling ? Color.white : new Color(1f, 0.72f, 0.3f);
        }

        private void ShowSeedSetup()
        {
            _seedSetupOpen = true;
            if (_seedInput != null) _seedInput.text = "";
            if (_friendlyFire) ToggleFriendlyFire(); // settings screen always opens at defaults
            if (!_hpScaling) ToggleHpScaling();
            Refresh();
        }

        private void PasteSeedIntoInput()
        {
            var digits = new string((GUIUtility.systemCopyBuffer ?? "").Where(char.IsDigit).Take(9).ToArray());
            if (_seedInput != null) _seedInput.text = digits;
        }

        private void HostWithSeed()
        {
            int seed = 0;
            if (_seedInput != null)
            {
                var digits = new string(_seedInput.text.Where(char.IsDigit).Take(9).ToArray());
                if (digits.Length > 0) int.TryParse(digits, out seed);
            }
            _seedSetupOpen = false;
            NetSession.Instance.HostOnline(seed, _friendlyFire, _hpScaling);
            Refresh();
        }

        private TMP_InputField MakeSeedInput(Transform parent, Vector2 centerOffset, Vector2 size)
        {
            var go = new GameObject("SeedInput", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = centerOffset;
            rt.sizeDelta = size;
            var bg = go.AddComponent<Image>();
            bg.color = new Color(0.10f, 0.12f, 0.17f, 1f);

            var areaGo = new GameObject("TextArea", typeof(RectTransform));
            areaGo.transform.SetParent(go.transform, false);
            var area = (RectTransform)areaGo.transform;
            area.anchorMin = Vector2.zero;
            area.anchorMax = Vector2.one;
            area.offsetMin = new Vector2(16, 8);
            area.offsetMax = new Vector2(-16, -8);
            areaGo.AddComponent<RectMask2D>();

            var textGo = new GameObject("Text", typeof(RectTransform));
            textGo.transform.SetParent(areaGo.transform, false);
            var text = textGo.AddComponent<TextMeshProUGUI>();
            if (_font != null) text.font = _font;
            text.fontSize = 26;
            text.color = Color.white;
            text.alignment = TextAlignmentOptions.Center;
            Stretch(text.rectTransform);

            var phGo = new GameObject("Placeholder", typeof(RectTransform));
            phGo.transform.SetParent(areaGo.transform, false);
            var ph = phGo.AddComponent<TextMeshProUGUI>();
            if (_font != null) ph.font = _font;
            ph.fontSize = 26;
            ph.color = new Color(1f, 1f, 1f, 0.3f);
            ph.alignment = TextAlignmentOptions.Center;
            ph.text = "RANDOM";
            Stretch(ph.rectTransform);

            var input = go.AddComponent<TMP_InputField>();
            input.targetGraphic = bg;
            input.textViewport = area;
            input.textComponent = text;
            input.placeholder = ph;
            input.contentType = TMP_InputField.ContentType.IntegerNumber;
            input.characterLimit = 9;
            return input;
        }

        private static GameObject ButtonRoot(TMP_Text label) => label.transform.parent.gameObject;

        private static void StretchWithMargins(RectTransform rt, float left, float right)
        {
            rt.anchorMin = new Vector2(0, 0);
            rt.anchorMax = new Vector2(1, 1);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = new Vector2(left, 0);
            rt.offsetMax = new Vector2(-right, 0);
        }

        // ---------------------------------------------------------------- actions

        private void SetLocalColor(byte color)
        {
            _localColor = color;
            NetSession.Instance.SetLocalPrefs(_localColor, _localReady);
        }

        private void ToggleReady()
        {
            _localReady = !_localReady;
            NetSession.Instance.SetLocalPrefs(_localColor, _localReady);
        }

        private void StartGame()
        {
            NetSession.Instance.StartRun();
        }

        private void Leave()
        {
            NetSession.Instance.StopSession("left lobby");
            Refresh();
        }

        // ---------------------------------------------------------------- copy code

        private void CopyCodeWithFeedback()
        {
            NetSession.Instance.CopyLobbyCodeToClipboard();
            if (_copyFlash != null) StopCoroutine(_copyFlash);
            _copyFlash = StartCoroutine(FlashCopied());
        }

        private System.Collections.IEnumerator FlashCopied()
        {
            _copyChipLabel.text = "COPIED!";
            _copyChipLabel.color = new Color(0.31f, 0.85f, 0.47f);
            yield return new WaitForSecondsRealtime(1.2f);
            _copyChipLabel.text = "COPY";
            _copyChipLabel.color = Color.white;
            _copyFlash = null;
        }

        // ---------------------------------------------------------------- refresh

        private void Refresh()
        {
            if (_canvasGo == null || !_canvasGo.activeSelf) return;
            var session = NetSession.Instance;
            bool inLobby = session.State == SessionState.Lobby || session.State == SessionState.Connecting;
            if (inLobby) _seedSetupOpen = false;
            if (_resumeButton != null) _resumeButton.SetActive(Core.NetRunSave.Exists());
            _connectPanel.SetActive(!inLobby && !_seedSetupOpen);
            if (_seedPanel != null) _seedPanel.SetActive(!inLobby && _seedSetupOpen);
            _lobbyPanel.SetActive(inLobby);

            // Panel switches deactivate the selected button; if the EventSystem's selection
            // escaped our canvas, clear it so nav input can't drive the menu behind us.
            var es = UnityEngine.EventSystems.EventSystem.current;
            if (es != null && es.currentSelectedGameObject != null
                && !es.currentSelectedGameObject.transform.IsChildOf(_canvasGo.transform))
                es.SetSelectedGameObject(null);
            _statusText.text = session.LastError ?? (session.State == SessionState.Connecting ? "Connecting…" : "");

            if (!inLobby) return;

            _codeText.text = session.CurrentLobbyCode != null ? $"LOBBY CODE   {session.CurrentLobbyCode}" : "";
            bool hasCode = !string.IsNullOrEmpty(_codeText.text);
            _copyChipRt.gameObject.SetActive(hasCode);
            if (hasCode) // hug the right edge of the (centered, width-varying) code text
                _copyChipRt.anchoredPosition = new Vector2(
                    _codeText.GetPreferredValues(_codeText.text).x * 0.5f + 58f, 0f);
            _seedText.text = $"WORLD SEED   {(session.ChosenSeed != 0 ? session.ChosenSeed.ToString() : "RANDOM")}"
                + (session.FriendlyFire ? "   <color=#ffb84d>FRIENDLY FIRE ON</color>" : "");
            _inviteButton.SetActive(session.UsingSteam);
            _startButton.SetActive(session.IsHost);
            var startBtn = _startButton.GetComponent<Button>();
            if (startBtn != null) startBtn.interactable = session.AllReady;

            var me = session.LocalPlayer;
            if (me != null)
            {
                _localColor = me.ColorIndex;
                _localReady = me.Ready;
            }
            _readyButtonLabel.text = _localReady ? "UNREADY" : "READY";

            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                var p = i < session.Players.Count ? session.Players[i] : null;
                if (p == null)
                {
                    row.Swatch.color = new Color(1, 1, 1, 0.08f);
                    row.Name.text = "<color=#666666>WAITING FOR PLAYER…</color>";
                    row.Status.text = "";
                    row.Kick.SetActive(false);
                    continue;
                }
                row.Kick.SetActive(session.IsHost && !p.IsLocal && session.State == SessionState.Lobby);
                row.Swatch.color = PlayerColors.Get(p.ColorIndex);
                string tags = p.Slot == session.HostSlot ? "  <color=#f08c2e>HOST</color>" : "";
                if (p.IsLocal) tags += "  <color=#888888>(YOU)</color>";
                if (p.ModsMismatch) tags += "  <color=#ffb84d>[!] MODS</color>";
                row.Name.text = p.Name + tags;
                string rtt = p.IsLocal || p.RttMs < 0 ? "" : $"<color=#888888>{p.RttMs} ms</color>  ";
                row.Status.text = !p.Connected
                    ? "<color=#ff7070>OFFLINE</color>"
                    : rtt + (p.Ready ? "<color=#50d878>READY</color>" : "<color=#aaaaaa>not ready</color>");
            }
        }

        // ---------------------------------------------------------------- widget factory

        private GameObject MakeGroup(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            Stretch((RectTransform)go.transform);
            return go;
        }

        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        private Image MakeImage(Transform parent, string name, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            img.color = color;
            return img;
        }

        private TMP_Text MakeText(Transform parent, string name, string text, float size, Color color,
            float y, float height)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            if (_font != null) tmp.font = _font;
            tmp.text = text;
            tmp.fontSize = size;
            tmp.color = color;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.richText = true;
            var rt = tmp.rectTransform;
            rt.anchorMin = new Vector2(0, 1);
            rt.anchorMax = new Vector2(1, 1);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0, y);
            rt.sizeDelta = new Vector2(0, height);
            return tmp;
        }

        /// <summary>Builds a button; returns its label (button root = label.transform.parent).</summary>
        private TMP_Text MakeButton(Transform parent, string label, Vector2 centerOffset, Vector2 size,
            UnityEngine.Events.UnityAction onClick)
        {
            var go = new GameObject($"Btn_{label}", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = centerOffset;
            rt.sizeDelta = size;

            var img = go.AddComponent<Image>();
            img.color = ButtonBg;
            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            var colors = btn.colors;
            colors.highlightedColor = ButtonHover;
            colors.pressedColor = Accent;
            btn.colors = colors;
            btn.onClick.AddListener(onClick);

            var text = MakeText(go.transform, "Label", label, Mathf.Min(26f, size.y * 0.45f), Color.white, 0, size.y);
            var trt = text.rectTransform;
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = Vector2.zero;
            trt.offsetMax = Vector2.zero;
            text.alignment = TextAlignmentOptions.Center;
            return text;
        }
    }
}
