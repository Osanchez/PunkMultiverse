using System.Collections.Generic;
using System.Linq;
using PunkMultiverse.Core;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace PunkMultiverse.UI
{
    /// <summary>
    /// The player-facing PLAY ONLINE screens, styled after the game's own SETTINGS/prompt
    /// screens (assets harvested by UiTheme — vanilla button clones with animator FX and
    /// click/select sounds, placeholder-art frames, both menu fonts). Three panels on one
    /// full-screen takeover:
    ///   CONNECT       — HOST GAME / JOIN FROM CLIPBOARD / REJOIN LAST SESSION (liveness-gated)
    ///   GAME SETTINGS — options-style rows: WORLD SEED (input + PASTE/RANDOM),
    ///                   FRIENDLY FIRE and ENEMY HP SCALING as vanilla OFF/ON toggles
    ///   LOBBY         — code + copy, 4 player rows, ship color, READY/START/INVITE
    /// MKB and gamepad both fully drive it: explicit navigation grids (rewired on every
    /// visibility change), Esc/B backs out one level, BACK/LEAVE swaps to a B-glyph hint
    /// when a gamepad was used last. Auto-shows when a session reaches Lobby state; hides
    /// on game start / stop.
    /// </summary>
    public sealed class LobbyScreen : MonoBehaviour
    {
        private const float WindowW = 1240f;
        private const float WindowH = 820f;
        private const float ContentHalf = WindowW / 2f - 90f; // inner layout half-width

        private GameObject _canvasGo;
        private GameObject _window;
        private GameObject _connectPanel;
        private GameObject _lobbyPanel;
        private GameObject _seedPanel;
        private TMP_Text _title;
        private TMP_Text _titleShadow;
        private TMP_Text _statusText;
        private TMP_Text _codeText;
        private TMP_Text _copyChipLabel;
        private RectTransform _copyChipRoot;
        private Coroutine _copyFlash;
        private TMP_Text _seedText;
        private TMP_Text _readyLabel;
        private TMP_Text _startLabel;
        private TMP_Text _inviteLabel;
        private TMP_Text _hostGameLabel;
        private TMP_Text _joinLabel;
        private TMP_Text _rejoinLabel;
        private GameObject _rejoinNote;
        private bool _rejoinAvailable;
        private float _nextRejoinProbeAt;
        private TMP_Text _backLabel;
        private GameObject _padHint;
        private TMP_Text _padHintLabel;
        private TMP_InputField _seedInput;
        private TMP_Text _pasteLabel;
        private TMP_Text _randomLabel;
        private TMP_Text _hostLobbyLabel;
        private TMP_Text _ffOff, _ffOn, _hpOff, _hpOn;
        private bool _seedSetupOpen;
        // DIRECT CONNECT screen (join a Udp server by IP:port — no config editing).
        private GameObject _serverPanel;
        private bool _serverSetupOpen;
        private TMP_InputField _ipInput;
        private TMP_InputField _portInput;
        private TMP_Text _connectLabel;   // CONNECT button on the server panel
        private TMP_Text _directLabel;    // DIRECT CONNECT button on the connect panel
        private bool _awaitingDirect;     // a direct-connect attempt is in flight (toast on failure)
        private bool _friendlyFire;
        private bool _hpScaling = true;
        private byte _localColor;
        private bool _localReady;
        private bool _lastPadMode;

        private readonly List<RowWidgets> _rows = new List<RowWidgets>();
        private readonly List<SettingsRow> _settingsRows = new List<SettingsRow>();
        private readonly List<Button> _swatches = new List<Button>();
        private readonly List<Image> _swatchFrames = new List<Image>();

        private sealed class RowWidgets
        {
            public Image Swatch;
            public TMP_Text Name;
            public TMP_Text Status;
            public TMP_Text KickLabel;
        }

        private sealed class SettingsRow
        {
            public RectTransform Root;
            public TMP_Text Label;
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
            // Direct-connect attempt resolved. Connecting -> Offline means it timed out / was
            // refused: toast the reason and return to the DIRECT CONNECT screen to retry. (The
            // Offline that JoinDirect itself emits comes from Lobby/None, never Connecting, so it
            // can't false-positive here.)
            if (_awaitingDirect && state == SessionState.Offline && _lastState == SessionState.Connecting)
            {
                _awaitingDirect = false;
                var err = NetSession.Instance?.LastError ?? "Could not reach the server.";
                Toast.Show(err.ToUpperInvariant(), 6f);
                if (!Visible) Show();
                _serverSetupOpen = true; // after Show() (which resets it) so we land on the retry screen
            }
            if (_awaitingDirect && state == SessionState.Lobby) _awaitingDirect = false; // joined
            _lastState = state;
            Refresh();
        }

        /// <summary>Build now (called from MainMenuInjection while the menu scene — and with it
        /// every vanilla template — is loaded). Safe to call repeatedly.</summary>
        public void EnsureBuilt()
        {
            if (_canvasGo == null) Build();
        }

        public void Show()
        {
            EnsureBuilt();
            _seedSetupOpen = false;
            _serverSetupOpen = false;
            _nextRejoinProbeAt = 0f; // probe immediately on open
            _canvasGo.SetActive(true);
            SetMenuBlocked(true);
            Refresh();
        }

        public void Hide()
        {
            _canvasGo?.SetActive(false);
            SetMenuBlocked(false);
            RestoreMenuSelection();
        }

        public void Toggle()
        {
            if (Visible) Hide(); else Show();
        }

        // ---------------------------------------------------------------- menu blocking

        // The dim layer swallows mouse rays, but the menu BEHIND us still runs: keyboard/gamepad
        // navigation could reach its buttons, and MainMenu.Update's own Esc handler would pop the
        // exit prompt over our screen. Block its canvas AND the component.
        private CanvasGroup _menuBlock;
        private MainMenu _blockedMenu;

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
                        if (canvas != null)
                        {
                            _menuBlock = canvas.GetComponent<CanvasGroup>();
                            if (_menuBlock == null) _menuBlock = canvas.gameObject.AddComponent<CanvasGroup>();
                        }
                    }
                    if (_menuBlock != null) _menuBlock.interactable = false;
                    if (_blockedMenu == null)
                    {
                        _blockedMenu = FindFirstObjectByType<MainMenu>();
                        if (_blockedMenu != null) _blockedMenu.enabled = false; // kills its Esc/exit handler
                    }
                    // MainMenu.OnDisable hid the cursor — claim it back with our own handle.
                    if (ServiceLocator.TryGet<CursorController>(out var cursor)) cursor.ShowCursor(this);
                }
                else
                {
                    if (_menuBlock != null) _menuBlock.interactable = true;
                    if (_blockedMenu != null) { _blockedMenu.enabled = true; _blockedMenu = null; }
                    if (ServiceLocator.TryGet<CursorController>(out var cursor)) cursor.HideCursor(this);
                }
            }
            catch { }
        }

        // ---------------------------------------------------------------- focus management

        /// <summary>Select the panel's primary action (falling back to its first nav-wired
        /// selectable) so a gamepad has somewhere useful to navigate from. Nav-mode-None
        /// elements (the click-to-copy code text) are mouse-only dead ends — never focus them.</summary>
        private void SelectFirstIn(GameObject panel)
        {
            if (panel == null || !panel.activeInHierarchy) return;
            var es = UnityEngine.EventSystems.EventSystem.current;
            if (es == null) return;

            // Direct-connect: land on the address field so you can type immediately.
            if (panel == _serverPanel && _ipInput != null && _ipInput.gameObject.activeInHierarchy)
            {
                es.SetSelectedGameObject(_ipInput.gameObject);
                return;
            }

            var preferred = UiTheme.ButtonOf(
                panel == _lobbyPanel ? _readyLabel
                : panel == _seedPanel ? _pasteLabel
                : _hostGameLabel);
            if (preferred != null && preferred.gameObject.activeInHierarchy && preferred.IsInteractable())
            {
                es.SetSelectedGameObject(preferred.gameObject);
                return;
            }

            Selectable target = null;
            foreach (var s in panel.GetComponentsInChildren<Selectable>(false))
            {
                if (s == null || !s.IsInteractable() || !s.gameObject.activeInHierarchy) continue;
                if (s.navigation.mode == Navigation.Mode.None) continue;
                if (s is Button) { target = s; break; }
                if (target == null) target = s; // fallback: input field / color swatch
            }
            if (target != null) es.SetSelectedGameObject(target.gameObject);
        }

        /// <summary>On close, hand gamepad focus back to the main menu's game-mode row (if we're
        /// sitting over it) so the controller keeps working; otherwise clear it.</summary>
        private static void RestoreMenuSelection()
        {
            var es = UnityEngine.EventSystems.EventSystem.current;
            if (es == null) return;
            var selector = GameObject.Find("GameModeSelector");
            if (selector != null)
            {
                foreach (var s in selector.GetComponentsInChildren<Selectable>(false))
                {
                    if (s == null || !s.IsInteractable() || !s.gameObject.activeInHierarchy) continue;
                    es.SetSelectedGameObject(s.gameObject);
                    return;
                }
            }
            es.SetSelectedGameObject(null);
        }

        // ---------------------------------------------------------------- per-frame input

        private bool _seedFocusGrace; // seed field was focused last frame

        private void Update()
        {
            // Escape hatch: a client stuck on the loading screen (e.g. a host that never reaches
            // go-live) can press Esc/B to leave immediately, instead of being locked out until the
            // 120s go-live timeout. Runs even while our panel is hidden behind the loading screen.
            var sess = NetSession.Instance;
            if (sess != null && sess.State == SessionState.Loading && !sess.IsHost)
            {
                var k = Keyboard.current; var g0 = Gamepad.current;
                if ((k != null && k.escapeKey.wasPressedThisFrame)
                    || (g0 != null && g0.buttonEast.wasPressedThisFrame))
                {
                    UiTheme.PlayClick();
                    sess.StopSession("left the loading screen");
                    return;
                }
            }

            if (!Visible) return;

            // Esc / gamepad B backs out one level. Never while typing in the seed field — Esc
            // there just unfocuses the input, and TMP may clear isFocused before we poll in the
            // same frame, hence the one-frame grace.
            bool back = false;
            var kb = Keyboard.current;
            if (kb != null && kb.escapeKey.wasPressedThisFrame) back = true;
            var gp = Gamepad.current;
            if (gp != null && gp.buttonEast.wasPressedThisFrame) back = true;
            bool seedFocused = _seedInput != null && _seedInput.isFocused;
            if (back && !seedFocused && !_seedFocusGrace)
            {
                _seedFocusGrace = false;
                UiTheme.PlayClick();
                BackAction();
                return;
            }
            _seedFocusGrace = seedFocused;

            // REJOIN liveness: while the connect panel is up, re-ask every few seconds whether
            // the remembered session still exists — the button appears/disappears live. The
            // Steam probe is async; "no reply" leaves the button hidden (the safe default).
            if (_connectPanel != null && _connectPanel.activeSelf && Time.unscaledTime >= _nextRejoinProbeAt)
            {
                _nextRejoinProbeAt = Time.unscaledTime + 5f;
                NetSession.Instance?.ProbeRejoinTarget(alive =>
                {
                    if (_rejoinAvailable == alive) return;
                    _rejoinAvailable = alive;
                    Refresh(); // visibility + nav rewire
                });
            }

            // BACK button <-> B-glyph hint follows the last-used device, vanilla ButtonHint style.
            bool pad = UiTheme.GamepadLastUsed && UiTheme.PadGlyphB != null;
            if (pad != _lastPadMode)
            {
                _lastPadMode = pad;
                ApplyPadHint();
                WireNav();
            }

            // Keep gamepad focus alive: a mouse click on empty space (or a panel switch) clears
            // the EventSystem selection and strands controller navigation.
            var es = UnityEngine.EventSystems.EventSystem.current;
            if (es != null)
            {
                var sel = es.currentSelectedGameObject;
                var activePanel = ActivePanel();
                var backRoot = UiTheme.RootOf(_backLabel);
                bool selValid = sel != null && sel.activeInHierarchy && activePanel != null
                    && (sel.transform.IsChildOf(activePanel.transform)
                        || (backRoot != null && sel.transform.IsChildOf(backRoot.transform)));
                if (!selValid) SelectFirstIn(activePanel);
            }

            // Options-style row highlight: the row owning the selection gets the accent label.
            if (_seedPanel != null && _seedPanel.activeSelf)
            {
                var selected = es != null ? es.currentSelectedGameObject : null;
                foreach (var row in _settingsRows)
                {
                    bool inRow = selected != null && selected.transform.IsChildOf(row.Root);
                    row.Label.color = inRow ? UiTheme.Accent : UiTheme.TextBody;
                }
            }
        }

        private GameObject ActivePanel()
        {
            if (_lobbyPanel != null && _lobbyPanel.activeSelf) return _lobbyPanel;
            if (_seedPanel != null && _seedPanel.activeSelf) return _seedPanel;
            if (_serverPanel != null && _serverPanel.activeSelf) return _serverPanel;
            return _connectPanel;
        }

        private void BackAction()
        {
            var session = NetSession.Instance;
            bool inLobby = session != null
                && (session.State == SessionState.Lobby || session.State == SessionState.Connecting);
            if (inLobby) { Leave(); return; }
            if (_seedSetupOpen) { _seedSetupOpen = false; Refresh(); return; }
            if (_serverSetupOpen) { _serverSetupOpen = false; Refresh(); return; }
            Hide();
        }

        // ---------------------------------------------------------------- building

        private void Build()
        {
            UiTheme.Harvest(transform);

            _canvasGo = new GameObject("PunkMV_LobbyScreen");
            _canvasGo.transform.SetParent(transform, false);
            var canvas = _canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 5000;
            var scaler = _canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 1f; // match height: ultrawide gains width, not zoom
            _canvasGo.AddComponent<GraphicRaycaster>();

            // Near-opaque backdrop, vanilla prompt style (#000 @ 0.87 over the menu).
            var dim = UiTheme.MakeImage(_canvasGo.transform, "Dim", new Color(0, 0, 0, 0.87f));
            UiTheme.Stretch(dim.rectTransform);

            // Big pixel title, top-left like the vanilla SETTINGS header — with the doubled
            // offset copy that gives it the glitch look.
            _titleShadow = UiTheme.MakeText(_canvasGo.transform, "TitleShadow", "PLAY ONLINE", 58,
                new Color(0.14f, 0.14f, 0.14f, 1f), UiTheme.PixelFont);
            PlaceTopLeft(_titleShadow.rectTransform, 74, -52, 900, 70);
            _title = UiTheme.MakeText(_canvasGo.transform, "Title", "PLAY ONLINE", 58,
                new Color(0.30f, 0.30f, 0.30f, 1f), UiTheme.PixelFont);
            PlaceTopLeft(_title.rectTransform, 68, -46, 900, 70);
            _title.alignment = _titleShadow.alignment = TextAlignmentOptions.TopLeft;

            var version = UiTheme.MakeText(_canvasGo.transform, "Version",
                $"PUNK MULTIVERSE · MOD V{Plugin.Version}", 15, UiTheme.TextFaint);
            PlaceTopLeft(version.rectTransform, 72, -122, 900, 22);
            version.alignment = TextAlignmentOptions.TopLeft;

            // Content window: the prompt panel frame over the dim.
            var window = UiTheme.MakeImage(_canvasGo.transform, "Window",
                UiTheme.PromptSprite != null ? new Color(0.34f, 0.34f, 0.34f, 1f) : new Color(0.06f, 0.07f, 0.10f, 0.96f),
                UiTheme.PromptSprite);
            var wrt = window.rectTransform;
            wrt.anchorMin = wrt.anchorMax = new Vector2(0.5f, 0.5f);
            wrt.pivot = new Vector2(0.5f, 0.5f);
            wrt.anchoredPosition = new Vector2(0, -24);
            wrt.sizeDelta = new Vector2(WindowW, WindowH);
            _window = window.gameObject;

            _statusText = UiTheme.MakeText(_window.transform, "Status", "", 18, UiTheme.Bad);
            PlaceTop(_statusText.rectTransform, -(WindowH - 46), 30);

            BuildConnectPanel(_window.transform);
            BuildSeedPanel(_window.transform);
            BuildServerPanel(_window.transform);
            BuildLobbyPanel(_window.transform);
            _lobbyPanel.SetActive(false);
            _seedPanel.SetActive(false);
            _serverPanel.SetActive(false);

            // BACK/LEAVE sits just below the window's bottom-left corner — anchored to the
            // window, not the screen, so it can't overlap the modal at narrower aspect ratios.
            // Hidden for gamepad in favor of the B glyph.
            _backLabel = UiTheme.MakeButton(_window.transform, "Btn_Back", "BACK",
                Vector2.zero, new Vector2(240, 58), () => BackAction(), 20);
            var backRt = (RectTransform)UiTheme.RootOf(_backLabel).transform;
            backRt.anchorMin = backRt.anchorMax = new Vector2(0f, 0f);
            backRt.pivot = new Vector2(0f, 1f);
            backRt.anchoredPosition = new Vector2(4, -16);
            BuildPadHint();

            _canvasGo.SetActive(false);
        }

        /// <summary>Gamepad hint (chip + B glyph + action name), vanilla ButtonHint layout.</summary>
        private void BuildPadHint()
        {
            _padHint = new GameObject("PadHint", typeof(RectTransform));
            _padHint.transform.SetParent(_window.transform, false);
            var rt = (RectTransform)_padHint.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 0f); // window bottom-left
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(4, -16);
            rt.sizeDelta = new Vector2(240, 58);

            var chip = UiTheme.MakeImage(_padHint.transform, "Chip", new Color(0.19f, 0.19f, 0.19f, 1f), UiTheme.ChipSprite);
            var crt = chip.rectTransform;
            crt.anchorMin = crt.anchorMax = new Vector2(0f, 0.5f);
            crt.pivot = new Vector2(0f, 0.5f);
            crt.anchoredPosition = new Vector2(0, 0);
            crt.sizeDelta = new Vector2(58, 58);

            if (UiTheme.PadGlyphB != null)
            {
                var glyph = UiTheme.MakeImage(chip.transform, "Glyph", Color.white);
                glyph.sprite = UiTheme.PadGlyphB;
                glyph.type = Image.Type.Simple;
                glyph.preserveAspect = true;
                var grt = glyph.rectTransform;
                grt.anchorMin = grt.anchorMax = new Vector2(0.5f, 0.5f);
                grt.pivot = new Vector2(0.5f, 0.5f);
                grt.sizeDelta = new Vector2(34, 34);
            }

            _padHintLabel = UiTheme.MakeText(_padHint.transform, "Action", "BACK", 17, UiTheme.TextDim);
            var art = _padHintLabel.rectTransform;
            art.anchorMin = art.anchorMax = new Vector2(0f, 0.5f);
            art.pivot = new Vector2(0f, 0.5f);
            art.anchoredPosition = new Vector2(70, 0);
            art.sizeDelta = new Vector2(180, 30);
            _padHintLabel.alignment = TextAlignmentOptions.MidlineLeft;
            _padHint.SetActive(false);
        }

        private void ApplyPadHint()
        {
            var backRoot = UiTheme.RootOf(_backLabel);
            if (backRoot != null) backRoot.SetActive(!_lastPadMode);
            if (_padHint != null) _padHint.SetActive(_lastPadMode);
        }

        private void BuildConnectPanel(Transform parent)
        {
            _connectPanel = MakeGroup(parent, "Connect");
            MakeHeader(_connectPanel.transform, "PUNK MULTIVERSE — ONLINE CO-OP");

            var hint = UiTheme.MakeText(_connectPanel.transform, "Hint",
                "HOST A LOBBY, JOIN A FRIEND'S CODE FROM YOUR CLIPBOARD,\nOR DIRECT-CONNECT TO A SERVER BY ITS ADDRESS.",
                19, UiTheme.TextBody);
            PlaceTop(hint.rectTransform, -122, 60);

            _hostGameLabel = UiTheme.MakeButton(_connectPanel.transform, "Btn_Host", "HOST GAME",
                new Vector2(0, 128), new Vector2(620, 92), ShowSeedSetup, 38);
            _joinLabel = UiTheme.MakeButton(_connectPanel.transform, "Btn_Join", "JOIN FROM CLIPBOARD",
                new Vector2(0, 26), new Vector2(620, 92), () => NetSession.Instance.JoinByCode(null), 38);
            _directLabel = UiTheme.MakeButton(_connectPanel.transform, "Btn_Direct", "DIRECT CONNECT",
                new Vector2(0, -76), new Vector2(620, 92), ShowServerSetup, 38);

            // Only offered while a liveness probe says the remembered session still exists
            // (disconnect / crash / mid-run quit); see RejoinMemory + ProbeRejoinTarget.
            _rejoinLabel = UiTheme.MakeButton(_connectPanel.transform, "Btn_Rejoin", "REJOIN LAST SESSION",
                new Vector2(0, -178), new Vector2(620, 70), () => NetSession.Instance.RejoinLastSession(), 20);
            var rejoinNote = UiTheme.MakeText(_connectPanel.transform, "RejoinNote",
                "YOUR LAST SESSION IS STILL RUNNING — JUMP BACK IN", 14, UiTheme.TextFaint);
            var nrt = rejoinNote.rectTransform;
            nrt.anchorMin = nrt.anchorMax = new Vector2(0.5f, 0.5f);
            nrt.pivot = new Vector2(0.5f, 0.5f);
            nrt.anchoredPosition = new Vector2(0, -226);
            nrt.sizeDelta = new Vector2(900, 22);
            _rejoinNote = rejoinNote.gameObject;
        }

        // ---------------------------------------------------------------- direct connect

        private void BuildServerPanel(Transform parent)
        {
            _serverPanel = MakeGroup(parent, "DirectConnect");
            MakeHeader(_serverPanel.transform, "DIRECT CONNECT — JOIN A SERVER");

            var hint = UiTheme.MakeText(_serverPanel.transform, "Hint",
                "ENTER THE SERVER'S ADDRESS AND PORT, THEN CONNECT.", 19, UiTheme.TextBody);
            PlaceTop(hint.rectTransform, -108, 40);

            var ipRow = MakeSettingsRow(_serverPanel.transform, "SERVER ADDRESS",
                "THE HOST'S PUBLIC IP OR HOSTNAME", 96);
            _ipInput = MakeInput(ipRow, "IpInput", new Vector2(150, 0), new Vector2(440, 58),
                "e.g. 203.0.113.5", TMP_InputField.ContentType.Standard, 64, 22);

            var portRow = MakeSettingsRow(_serverPanel.transform, "PORT",
                "UDP PORT (DEFAULT 7778)", -20);
            _portInput = MakeInput(portRow, "PortInput", new Vector2(150, 0), new Vector2(220, 58),
                "7778", TMP_InputField.ContentType.IntegerNumber, 5, 22);

            _connectLabel = UiTheme.MakeButton(_serverPanel.transform, "Btn_Connect", "CONNECT",
                new Vector2(0, -170), new Vector2(500, 92), OnDirectConnect, 38);
        }

        private void ShowServerSetup()
        {
            _serverSetupOpen = true;
            if (_portInput != null && string.IsNullOrEmpty(_portInput.text)) _portInput.text = "7778";
            Refresh();
        }

        private void OnDirectConnect()
        {
            string host = _ipInput != null ? _ipInput.text.Trim() : "";
            string portStr = _portInput != null ? _portInput.text.Trim() : "";
            if (string.IsNullOrEmpty(host)) { Toast.Show("ENTER A SERVER ADDRESS", 4f); return; }
            if (!int.TryParse(portStr, out int port) || port < 1 || port > 65535)
            {
                Toast.Show("ENTER A VALID PORT (1-65535)", 4f);
                return;
            }
            _awaitingDirect = true; // a Connecting -> Offline after this = failure -> toast + return
            NetSession.Instance.JoinDirect(host, port);
            Refresh();
        }

        // ---------------------------------------------------------------- game settings

        private void BuildSeedPanel(Transform parent)
        {
            _seedPanel = MakeGroup(parent, "GameSettings");
            MakeHeader(_seedPanel.transform, "GAME SETTINGS — NEW ONLINE RUN");

            // WORLD SEED row -----------------------------------------------------------
            var seedRow = MakeSettingsRow(_seedPanel.transform, "WORLD SEED",
                "TYPE ONE, PASTE, OR LEAVE ON RANDOM", 168);
            _seedInput = MakeSeedInput(seedRow, new Vector2(42, 0), new Vector2(320, 58));
            _pasteLabel = UiTheme.MakeButton(seedRow, "Btn_Paste", "PASTE",
                new Vector2(291, 0), new Vector2(150, 54), PasteSeedIntoInput, 16);
            _randomLabel = UiTheme.MakeButton(seedRow, "Btn_Random", "RANDOM",
                new Vector2(455, 0), new Vector2(150, 54), () => { if (_seedInput != null) _seedInput.text = ""; }, 16);

            // FRIENDLY FIRE row --------------------------------------------------------
            var ffRow = MakeSettingsRow(_seedPanel.transform, "FRIENDLY FIRE",
                "YOUR SHOTS DAMAGE YOUR FRIENDS' SHIPS", 42);
            _ffOff = UiTheme.MakeButton(ffRow, "Btn_FFOff", "OFF",
                new Vector2(235, 0), new Vector2(190, 60), () => SetFriendlyFire(false), 30);
            _ffOn = UiTheme.MakeButton(ffRow, "Btn_FFOn", "ON",
                new Vector2(440, 0), new Vector2(190, 60), () => SetFriendlyFire(true), 30);

            // ENEMY HP SCALING row -----------------------------------------------------
            var hpRow = MakeSettingsRow(_seedPanel.transform, "ENEMY HP SCALING",
                "+25% ENEMY HEALTH PER PLAYER", -84);
            _hpOff = UiTheme.MakeButton(hpRow, "Btn_HPOff", "OFF",
                new Vector2(235, 0), new Vector2(190, 60), () => SetHpScaling(false), 30);
            _hpOn = UiTheme.MakeButton(hpRow, "Btn_HPOn", "ON",
                new Vector2(440, 0), new Vector2(190, 60), () => SetHpScaling(true), 30);

            _hostLobbyLabel = UiTheme.MakeButton(_seedPanel.transform, "Btn_HostLobby", "HOST LOBBY",
                new Vector2(0, -238), new Vector2(500, 92), HostWithSeed, 38);

            SetFriendlyFire(false, silent: true);
            SetHpScaling(true, silent: true);
        }

        /// <summary>Options-screen style row: label + faint sub-note on the left, controls
        /// placed by the caller on the right. Returns the row transform (center-anchored).</summary>
        private Transform MakeSettingsRow(Transform parent, string label, string note, float centerY)
        {
            var go = new GameObject($"Row_{label}", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(0, centerY);
            rt.sizeDelta = new Vector2(WindowW - 120, 100);

            var text = UiTheme.MakeText(go.transform, "Label", label, 24, UiTheme.TextBody);
            var lrt = text.rectTransform;
            lrt.anchorMin = new Vector2(0f, 0.5f);
            lrt.anchorMax = new Vector2(0f, 0.5f);
            lrt.pivot = new Vector2(0f, 0.5f);
            lrt.anchoredPosition = new Vector2(10, 16);
            lrt.sizeDelta = new Vector2(440, 30);
            text.alignment = TextAlignmentOptions.MidlineLeft;

            var noteText = UiTheme.MakeText(go.transform, "Note", note, 13, UiTheme.TextFaint);
            var nrt = noteText.rectTransform;
            nrt.anchorMin = nrt.anchorMax = new Vector2(0f, 0.5f);
            nrt.pivot = new Vector2(0f, 0.5f);
            nrt.anchoredPosition = new Vector2(10, -16);
            nrt.sizeDelta = new Vector2(440, 22);
            noteText.alignment = TextAlignmentOptions.MidlineLeft;

            _settingsRows.Add(new SettingsRow { Root = rt, Label = text });
            return go.transform;
        }

        private void SetFriendlyFire(bool on, bool silent = false)
        {
            _friendlyFire = on;
            UiTheme.SetToggled(_ffOff, !on);
            UiTheme.SetToggled(_ffOn, on);
        }

        // Base Health * (1 + (EnemyHealthScalePerPlayer * number of players)), counted at
        // START GAME; the per-player value (default 0.25) lives in config.cfg.
        private void SetHpScaling(bool on, bool silent = false)
        {
            _hpScaling = on;
            UiTheme.SetToggled(_hpOff, !on);
            UiTheme.SetToggled(_hpOn, on);
        }

        private void ShowSeedSetup()
        {
            _seedSetupOpen = true;
            if (_seedInput != null) _seedInput.text = "";
            SetFriendlyFire(false); // settings screen always opens at defaults
            SetHpScaling(true);
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
            => MakeInput(parent, "SeedInput", centerOffset, size, "RANDOM",
                         TMP_InputField.ContentType.IntegerNumber, 9);

        /// <summary>Themed text-entry chip (same look as the seed field): chip-sprite background,
        /// masked text area, placeholder, accent caret. Nav-mode None (grids wire it explicitly).</summary>
        private TMP_InputField MakeInput(Transform parent, string name, Vector2 centerOffset, Vector2 size,
            string placeholder, TMP_InputField.ContentType contentType, int charLimit, int fontSize = 24)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = centerOffset;
            rt.sizeDelta = size;
            var bg = go.AddComponent<Image>();
            if (UiTheme.ChipSprite != null)
            {
                bg.sprite = UiTheme.ChipSprite;
                bg.type = Image.Type.Sliced;
                bg.color = new Color(0.19f, 0.19f, 0.19f, 1f);
            }
            else bg.color = new Color(0.10f, 0.12f, 0.17f, 1f);

            var areaGo = new GameObject("TextArea", typeof(RectTransform));
            areaGo.transform.SetParent(go.transform, false);
            var area = (RectTransform)areaGo.transform;
            area.anchorMin = Vector2.zero;
            area.anchorMax = Vector2.one;
            area.offsetMin = new Vector2(16, 8);
            area.offsetMax = new Vector2(-16, -8);
            areaGo.AddComponent<RectMask2D>();

            var text = UiTheme.MakeText(areaGo.transform, "Text", "", fontSize, UiTheme.TextBright);
            UiTheme.Stretch(text.rectTransform);

            var ph = UiTheme.MakeText(areaGo.transform, "Placeholder", placeholder, fontSize, new Color(1f, 1f, 1f, 0.28f));
            UiTheme.Stretch(ph.rectTransform);

            var input = go.AddComponent<TMP_InputField>();
            input.targetGraphic = bg;
            input.textViewport = area;
            input.textComponent = text;
            input.placeholder = ph;
            input.contentType = contentType;
            input.characterLimit = charLimit;
            input.caretColor = UiTheme.Accent;
            input.customCaretColor = true;
            input.selectionColor = new Color(UiTheme.Accent.r, UiTheme.Accent.g, UiTheme.Accent.b, 0.45f);
            input.navigation = new Navigation { mode = Navigation.Mode.None };
            go.AddComponent<UiSelectSfx>();
            return input;
        }

        // ---------------------------------------------------------------- lobby

        private void BuildLobbyPanel(Transform parent)
        {
            _lobbyPanel = MakeGroup(parent, "Lobby");

            _codeText = UiTheme.MakeText(_lobbyPanel.transform, "Code", "", 30, UiTheme.TextBright, UiTheme.PixelFont);
            PlaceTop(_codeText.rectTransform, -34, 44);

            // The code line copies itself: click it, or the COPY chip riding its right edge
            // (positioned per-frame in Refresh — the code width varies).
            var codeBtn = _codeText.gameObject.AddComponent<Button>();
            codeBtn.targetGraphic = _codeText;
            codeBtn.transition = Selectable.Transition.None;
            codeBtn.navigation = new Navigation { mode = Navigation.Mode.None };
            codeBtn.onClick.AddListener(CopyCodeWithFeedback);
            codeBtn.onClick.AddListener(UiTheme.PlayClick);
            _copyChipLabel = UiTheme.MakeButton(_codeText.transform, "Btn_Copy", "COPY",
                Vector2.zero, new Vector2(110, 42), CopyCodeWithFeedback, 14);
            _copyChipRoot = (RectTransform)UiTheme.RootOf(_copyChipLabel).transform;
            _copyChipRoot.gameObject.SetActive(false);

            UiTheme.MakeLine(_lobbyPanel.transform, -92, 46);

            // World seed + rules: read-only here — chosen on GAME SETTINGS before hosting.
            _seedText = UiTheme.MakeText(_lobbyPanel.transform, "Seed", "", 17, UiTheme.TextBody);
            PlaceTop(_seedText.rectTransform, -106, 26);

            // Player rows.
            for (int i = 0; i < NetSession.MaxPlayers; i++)
            {
                var row = new RowWidgets();
                var bg = UiTheme.MakeImage(_lobbyPanel.transform, $"Row{i}", new Color(1f, 1f, 1f, 0.045f));
                var rt = bg.rectTransform;
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
                rt.pivot = new Vector2(0.5f, 1f);
                rt.anchoredPosition = new Vector2(0, -152 - i * 86);
                rt.sizeDelta = new Vector2(WindowW - 120, 76);

                var swatch = UiTheme.MakeImage(bg.transform, "Swatch", Color.gray);
                var srt = swatch.rectTransform;
                srt.anchorMin = srt.anchorMax = new Vector2(0f, 0.5f);
                srt.pivot = new Vector2(0f, 0.5f);
                srt.anchoredPosition = new Vector2(20, 0);
                srt.sizeDelta = new Vector2(40, 40);
                row.Swatch = swatch;

                // Hud font, not Font_Minimum: player names are arbitrary text and the pixel
                // font loses its 'I' stroke below ~30 ("WAITING" rendered as "WA TING").
                row.Name = UiTheme.MakeText(bg.transform, "Name", "", 20, UiTheme.TextBright);
                row.Name.alignment = TextAlignmentOptions.MidlineLeft;
                StretchWithMargins(row.Name.rectTransform, 84, 300);

                row.Status = UiTheme.MakeText(bg.transform, "RowStatus", "", 17, UiTheme.TextBright);
                row.Status.alignment = TextAlignmentOptions.MidlineRight;
                StretchWithMargins(row.Status.rectTransform, 84, 130);

                int slotIndex = i;
                row.KickLabel = UiTheme.MakeButton(bg.transform, "Btn_Kick", "KICK",
                    new Vector2((WindowW - 120) / 2f - 70, 0), new Vector2(96, 44),
                    () => NetSession.Instance.RequestKick((byte)slotIndex), 13);
                UiTheme.RootOf(row.KickLabel).SetActive(false);

                _rows.Add(row);
            }

            // Ship color: label left, the 8 swatch buttons to the right.
            var colorLabel = UiTheme.MakeText(_lobbyPanel.transform, "ColorLabel", "SHIP COLOR", 20, UiTheme.TextBody);
            var clrt = colorLabel.rectTransform;
            clrt.anchorMin = clrt.anchorMax = new Vector2(0.5f, 0.5f);
            clrt.pivot = new Vector2(0f, 0.5f);
            clrt.anchoredPosition = new Vector2(-ContentHalf + 10, -134);
            clrt.sizeDelta = new Vector2(260, 30);
            colorLabel.alignment = TextAlignmentOptions.MidlineLeft;

            for (int c = 0; c < PlayerColors.All.Length; c++)
            {
                int colorIndex = c;
                var go = new GameObject($"Swatch{c}", typeof(RectTransform));
                go.transform.SetParent(_lobbyPanel.transform, false);
                var rt = (RectTransform)go.transform;
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.anchoredPosition = new Vector2(-30 + c * 62, -134);
                rt.sizeDelta = new Vector2(48, 48);

                // Selection frame first (drawn under), color fill on top — the frame sprite has
                // a filled center that would otherwise hide the color.
                var frame = UiTheme.MakeImage(go.transform, "Frame", Color.white,
                    UiTheme.FrameOnSprite != null ? UiTheme.FrameOnSprite : null);
                var frt = frame.rectTransform;
                frt.anchorMin = Vector2.zero;
                frt.anchorMax = Vector2.one;
                frt.offsetMin = new Vector2(-7, -7);
                frt.offsetMax = new Vector2(7, 7);
                frame.raycastTarget = false;
                frame.gameObject.SetActive(false);
                _swatchFrames.Add(frame);

                var fill = UiTheme.MakeImage(go.transform, "Fill", PlayerColors.Get(c));
                UiTheme.Stretch(fill.rectTransform);

                var btn = go.AddComponent<Button>();
                btn.targetGraphic = fill;
                var colors = btn.colors;
                colors.highlightedColor = new Color(1.25f, 1.25f, 1.25f, 1f);
                colors.selectedColor = new Color(1.25f, 1.25f, 1.25f, 1f);
                colors.pressedColor = Color.white;
                btn.colors = colors;
                btn.navigation = new Navigation { mode = Navigation.Mode.None };
                btn.onClick.AddListener(() => { SetLocalColor((byte)colorIndex); UiTheme.PlayClick(); });
                go.AddComponent<UiSelectSfx>();
                _swatches.Add(btn);
            }

            _readyLabel = UiTheme.MakeButton(_lobbyPanel.transform, "Btn_Ready", "READY",
                new Vector2(-320, -228), new Vector2(300, 80), ToggleReady, 32);
            _startLabel = UiTheme.MakeButton(_lobbyPanel.transform, "Btn_Start", "START GAME",
                new Vector2(0, -228), new Vector2(300, 80), StartGame, 32);
            _inviteLabel = UiTheme.MakeButton(_lobbyPanel.transform, "Btn_Invite", "INVITE FRIENDS",
                new Vector2(320, -228), new Vector2(300, 80), () => NetSession.Instance.Lobby?.OpenInviteOverlay(), 18);
        }

        private GameObject _startButton => UiTheme.RootOf(_startLabel);
        private GameObject _inviteButton => UiTheme.RootOf(_inviteLabel);
        private GameObject _rejoinButton => UiTheme.RootOf(_rejoinLabel);

        // ---------------------------------------------------------------- shared widgets

        private GameObject MakeGroup(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            UiTheme.Stretch((RectTransform)go.transform);
            return go;
        }

        /// <summary>Faint header caption + full-width divider at the top of the window,
        /// vanilla settings-window style.</summary>
        private void MakeHeader(Transform parent, string caption)
        {
            var text = UiTheme.MakeText(parent, "Header", caption, 19, UiTheme.TextFaint);
            PlaceTop(text.rectTransform, -30, 28);
            UiTheme.MakeLine(parent, -64, 46);
        }

        private static void PlaceTop(RectTransform rt, float y, float height)
        {
            rt.anchorMin = new Vector2(0, 1);
            rt.anchorMax = new Vector2(1, 1);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0, y);
            rt.sizeDelta = new Vector2(0, height);
        }

        private static void PlaceTopLeft(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, y);
            rt.sizeDelta = new Vector2(w, h);
        }

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
            RefreshSwatchFrames();
        }

        private void ToggleReady()
        {
            _localReady = !_localReady;
            NetSession.Instance.SetLocalPrefs(_localColor, _localReady);
        }

        private void StartGame()
        {
            NetSession.Instance.RequestStart();
        }

        private void Leave()
        {
            // Deliberately walking out of the lobby means "don't offer this session again" —
            // unlike a mid-run disconnect/quit, which keeps the rejoin target.
            Core.RejoinMemory.Clear();
            _rejoinAvailable = false;
            NetSession.Instance.StopSession("left lobby");
            Refresh();
        }

        // ---------------------------------------------------------------- copy code

        private void CopyCodeWithFeedback()
        {
            ulong serverCode = NetSession.Instance.SteamServerCode;
            if (serverCode != 0) { try { GUIUtility.systemCopyBuffer = serverCode.ToString(); } catch { } }
            else NetSession.Instance.CopyLobbyCodeToClipboard();
            if (_copyFlash != null) StopCoroutine(_copyFlash);
            _copyFlash = StartCoroutine(FlashCopied());
        }

        private System.Collections.IEnumerator FlashCopied()
        {
            _copyChipLabel.text = "COPIED!";
            _copyChipLabel.color = UiTheme.Good;
            yield return new WaitForSecondsRealtime(1.2f);
            _copyChipLabel.text = "COPY";
            _copyChipLabel.color = UiTheme.TextBright;
            _copyFlash = null;
        }

        // ---------------------------------------------------------------- refresh

        private void Refresh()
        {
            if (_canvasGo == null || !_canvasGo.activeSelf) return;
            var session = NetSession.Instance;
            bool inLobby = session.State == SessionState.Lobby || session.State == SessionState.Connecting;
            if (inLobby) { _seedSetupOpen = false; _serverSetupOpen = false; }
            bool showRejoin = !inLobby && !_seedSetupOpen && !_serverSetupOpen && _rejoinAvailable;
            if (_rejoinButton != null) _rejoinButton.SetActive(showRejoin);
            if (_rejoinNote != null) _rejoinNote.SetActive(showRejoin);
            _connectPanel.SetActive(!inLobby && !_seedSetupOpen && !_serverSetupOpen);
            if (_seedPanel != null) _seedPanel.SetActive(!inLobby && _seedSetupOpen);
            if (_serverPanel != null) _serverPanel.SetActive(!inLobby && _serverSetupOpen);
            _lobbyPanel.SetActive(inLobby);

            _title.text = _titleShadow.text =
                inLobby ? "LOBBY" : _seedSetupOpen ? "GAME SETTINGS"
                : _serverSetupOpen ? "DIRECT CONNECT" : "PLAY ONLINE";
            if (_backLabel != null) _backLabel.text = inLobby ? "LEAVE" : "BACK";
            if (_padHintLabel != null) _padHintLabel.text = inLobby ? "LEAVE" : "BACK";

            _statusText.text = session.LastError ?? (session.State == SessionState.Connecting ? "CONNECTING…" : "");
            _statusText.color = session.LastError != null ? UiTheme.Bad : UiTheme.Accent;

            if (inLobby) RefreshLobby(session);

            // Panel switches deactivate the selected button: keep gamepad focus inside the
            // active panel (Update's guard does the per-frame policing).
            var es = UnityEngine.EventSystems.EventSystem.current;
            if (es != null)
            {
                var sel = es.currentSelectedGameObject;
                var activePanel = ActivePanel();
                bool selValid = sel != null && sel.activeInHierarchy && activePanel != null
                    && sel.transform.IsChildOf(activePanel.transform);
                if (!selValid) SelectFirstIn(activePanel);
            }

            ApplyPadHint();
            WireNav();
        }

        private void RefreshLobby(NetSession session)
        {
            // SteamServer sessions have no Steam lobby — surface the server join code (the SteamID64
            // a remote friend pastes into Join) in the same slot, copyable the same way.
            ulong serverCode = session.SteamServerCode;
            _codeText.text = serverCode != 0 ? $"SERVER CODE   {serverCode}"
                : session.CurrentLobbyCode != null ? $"LOBBY CODE   {session.CurrentLobbyCode}" : "";
            bool hasCode = !string.IsNullOrEmpty(_codeText.text);
            _copyChipRoot.gameObject.SetActive(hasCode);
            if (hasCode) // hug the right edge of the (centered, width-varying) code text
                _copyChipRoot.anchoredPosition = new Vector2(
                    _codeText.GetPreferredValues(_codeText.text).x * 0.5f + 78f, 0f);
            _seedText.text = $"WORLD SEED   {(session.ChosenSeed != 0 ? session.ChosenSeed.ToString() : "RANDOM")}"
                + (session.FriendlyFire ? "   ·   <color=#f08c2e>FRIENDLY FIRE ON</color>" : "");
            _inviteButton.SetActive(session.CanInvite); // Steam P2P or a SteamServer discovery lobby
            _startButton.SetActive(session.IsSessionAdmin);
            var startBtn = UiTheme.ButtonOf(_startLabel);
            if (startBtn != null)
            {
                startBtn.interactable = session.AllReady;
                _startLabel.color = session.AllReady ? UiTheme.TextBright : UiTheme.TextFaint;
            }

            var me = session.LocalPlayer;
            if (me != null)
            {
                _localColor = me.ColorIndex;
                _localReady = me.Ready;
            }
            _readyLabel.text = _localReady ? "UNREADY" : "READY";
            UiTheme.SetToggled(_readyLabel, _localReady);
            RefreshSwatchFrames();

            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                var p = i < session.Players.Count ? session.Players[i] : null;
                if (p == null)
                {
                    row.Swatch.color = new Color(1, 1, 1, 0.08f);
                    row.Name.text = "<color=#4d4d4d>WAITING FOR PLAYER…</color>";
                    row.Status.text = "";
                    UiTheme.RootOf(row.KickLabel).SetActive(false);
                    continue;
                }
                UiTheme.RootOf(row.KickLabel).SetActive(
                    session.IsSessionAdmin && !p.IsLocal && !p.IsCoordinator && session.State == SessionState.Lobby);
                row.Swatch.color = PlayerColors.Get(p.ColorIndex);
                string tags = p.Slot == session.HostSlot ? "  <color=#f08c2e>HOST</color>"
                    : p.IsAdmin ? "  <color=#f08c2e>ADMIN</color>" : "";
                if (p.IsLocal) tags += "  <color=#717171>(YOU)</color>";
                if (p.ModsMismatch) tags += "  <color=#ffb84d>[!] MODS</color>";
                row.Name.text = p.Name + tags;
                string rtt = p.IsLocal || p.RttMs < 0 ? "" : $"<color=#717171>{p.RttMs} MS</color>  ";
                row.Status.text = !p.Connected
                    ? "<color=#ff7070>OFFLINE</color>"
                    : rtt + (p.Ready ? "<color=#50d878>READY</color>" : "<color=#717171>NOT READY</color>");
            }
        }

        private void RefreshSwatchFrames()
        {
            for (int i = 0; i < _swatchFrames.Count; i++)
                _swatchFrames[i].gameObject.SetActive(i == _localColor);
        }

        // ---------------------------------------------------------------- controller nav

        /// <summary>Explicit navigation for whichever panel is live. Called on every visibility
        /// change (panel switch, roster change, device switch) so the chains always match what's
        /// actually on screen.</summary>
        private void WireNav()
        {
            if (_canvasGo == null || !_canvasGo.activeSelf) return;
            var grid = new List<Selectable[]>();
            var back = UiTheme.ButtonOf(_backLabel);

            if (_connectPanel.activeSelf)
            {
                grid.Add(new Selectable[] { UiTheme.ButtonOf(_hostGameLabel) });
                grid.Add(new Selectable[] { UiTheme.ButtonOf(_joinLabel) });
                grid.Add(new Selectable[] { UiTheme.ButtonOf(_directLabel) });
                grid.Add(new Selectable[] { UiTheme.ButtonOf(_rejoinLabel) });
                grid.Add(new Selectable[] { back });
            }
            else if (_serverPanel != null && _serverPanel.activeSelf)
            {
                grid.Add(new Selectable[] { _ipInput });
                grid.Add(new Selectable[] { _portInput });
                grid.Add(new Selectable[] { UiTheme.ButtonOf(_connectLabel) });
                grid.Add(new Selectable[] { back });
            }
            else if (_seedPanel != null && _seedPanel.activeSelf)
            {
                grid.Add(new Selectable[] { _seedInput, UiTheme.ButtonOf(_pasteLabel), UiTheme.ButtonOf(_randomLabel) });
                grid.Add(new Selectable[] { UiTheme.ButtonOf(_ffOff), UiTheme.ButtonOf(_ffOn) });
                grid.Add(new Selectable[] { UiTheme.ButtonOf(_hpOff), UiTheme.ButtonOf(_hpOn) });
                grid.Add(new Selectable[] { UiTheme.ButtonOf(_hostLobbyLabel) });
                grid.Add(new Selectable[] { back });
            }
            else if (_lobbyPanel.activeSelf)
            {
                grid.Add(new Selectable[] { UiTheme.ButtonOf(_copyChipLabel) });
                foreach (var row in _rows)
                    grid.Add(new Selectable[] { UiTheme.ButtonOf(row.KickLabel) });
                grid.Add(_swatches.ToArray());
                grid.Add(new Selectable[] { UiTheme.ButtonOf(_readyLabel), UiTheme.ButtonOf(_startLabel), UiTheme.ButtonOf(_inviteLabel) });
                grid.Add(new Selectable[] { back });
            }

            UiTheme.WireGrid(grid);
        }
    }
}
