using System;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace PunkMultiverse.UI
{
    /// <summary>
    /// The game's menu look, harvested at runtime from the main-menu scene (no asset bundles):
    /// vanilla button prefab clones (Animator + PunkButton + ButtonSounds — the real hover/press
    /// FX and click/select sfx), the placeholder-art sprite family, and both menu fonts.
    /// Harvest() must run while the main menu is loaded; everything degrades to the old flat
    /// style if an asset is missing (e.g. building mid-game after a host quit).
    /// </summary>
    internal static class UiTheme
    {
        // Palette lifted from the vanilla SETTINGS screen (labels #4d4d4d/#717171, selected row
        // orange, START-style bright white) — keep in sync with what uitree reports.
        public static readonly Color Accent = new Color(0.941f, 0.549f, 0.180f);      // #f08c2e
        public static readonly Color TextBright = Color.white;
        public static readonly Color TextBody = new Color(0.63f, 0.63f, 0.63f);
        public static readonly Color TextDim = new Color(0.443f, 0.443f, 0.443f);     // #717171
        public static readonly Color TextFaint = new Color(0.30f, 0.30f, 0.30f);      // #4d4d4d
        public static readonly Color Good = new Color(0.31f, 0.85f, 0.47f);
        public static readonly Color Bad = new Color(1f, 0.44f, 0.44f);

        public static Sprite PanelSprite;       // "Sprite PopupPanel" (settings window bg)
        public static Sprite PromptSprite;      // MainMenu_placeholders_10 (prompt panel frame)
        public static Sprite FrameSprite;       // MainMenu_placeholders_3  (basic button frame)
        public static Sprite FrameOnSprite;     // MainMenu_placeholders_11 (selected tab frame)
        public static Sprite FrameOffSprite;    // MainMenu_placeholders_12 (unselected tab frame)
        public static Sprite ChipSprite;        // MainMenu_placeholders_17 (button-hint chip)
        public static Sprite PadGlyphB;         // Buttonhints_XBOX_B (gamepad BACK hint)

        public static TMP_FontAsset PixelFont;  // Font_Minimum — big display/button font
        public static TMP_FontAsset HudFont;    // 8-bit-hud SDF — small labels/body text

        private static GameObject _buttonTemplate; // inactive master clone, owned by our canvas
        private static string _clickSfx, _selectSfx;

        public static bool HasVanillaButtons => _buttonTemplate != null;

        // ---------------------------------------------------------------- harvest

        /// <summary>Harvest theme assets from the currently loaded scene. Idempotent; re-runs
        /// only fill holes (so a later menu visit can repair a partial early harvest).</summary>
        public static void Harvest(Transform templateOwner)
        {
            try { HarvestInner(templateOwner); }
            catch (Exception e) { Plugin.Log.LogWarning($"[UI] theme harvest failed: {e.Message}"); }
        }

        private static void HarvestInner(Transform templateOwner)
        {
            if (_buttonTemplate == null)
            {
                // The footer SETTINGS button is the game's generic menu button (same family the
                // options screen uses for BACK/APPLY and its ON/OFF toggles).
                var src = FindSceneObject("BasicButton Settings") ?? FindSceneObject("CoopButton");
                if (src != null)
                {
                    _buttonTemplate = UnityEngine.Object.Instantiate(src, templateOwner);
                    _buttonTemplate.name = "PunkMV_ButtonTemplate";
                    _buttonTemplate.SetActive(false);
                    SanitizeButtonClone(_buttonTemplate);
                    var btn = _buttonTemplate.GetComponentInChildren<Button>(true);
                    if (btn != null) HarvestSfx(btn.GetComponent<ButtonSounds>());
                    var tmp = _buttonTemplate.GetComponentInChildren<TMP_Text>(true);
                    if (tmp != null && PixelFont == null) PixelFont = tmp.font;
                }
            }

            if (HudFont == null)
                HudFont = Resources.FindObjectsOfTypeAll<TMP_FontAsset>()
                    .FirstOrDefault(f => f != null && f.name.StartsWith("8-bit-hud", StringComparison.OrdinalIgnoreCase));
            if (PixelFont == null)
                PixelFont = Resources.FindObjectsOfTypeAll<TMP_FontAsset>()
                    .FirstOrDefault(f => f != null && f.name.StartsWith("Font_Minimum", StringComparison.OrdinalIgnoreCase));

            if (PanelSprite == null || FrameSprite == null || ChipSprite == null || PadGlyphB == null)
            {
                foreach (var s in Resources.FindObjectsOfTypeAll<Sprite>())
                {
                    if (s == null) continue;
                    switch (s.name)
                    {
                        case "Sprite PopupPanel": if (PanelSprite == null) PanelSprite = s; break;
                        case "MainMenu_placeholders_10": if (PromptSprite == null) PromptSprite = s; break;
                        case "MainMenu_placeholders_3": if (FrameSprite == null) FrameSprite = s; break;
                        case "MainMenu_placeholders_11": if (FrameOnSprite == null) FrameOnSprite = s; break;
                        case "MainMenu_placeholders_12": if (FrameOffSprite == null) FrameOffSprite = s; break;
                        case "MainMenu_placeholders_17": if (ChipSprite == null) ChipSprite = s; break;
                        case "Buttonhints_XBOX_B": if (PadGlyphB == null) PadGlyphB = s; break;
                    }
                }
            }

            Plugin.Log.LogInfo($"[UI] theme harvest: template={_buttonTemplate != null} panel={PanelSprite != null} " +
                $"frame={FrameSprite != null} tabOn={FrameOnSprite != null} chip={ChipSprite != null} " +
                $"glyphB={PadGlyphB != null} pixelFont={PixelFont != null} hudFont={HudFont != null} " +
                $"sfx={_clickSfx != null}/{_selectSfx != null}");
        }

        /// <summary>Strip everything from a vanilla button clone that could run menu logic;
        /// keep Animator (hover/toggle FX), PunkButton, Button, ButtonSounds, visuals.</summary>
        private static void SanitizeButtonClone(GameObject clone)
        {
            foreach (var comp in clone.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (comp == null) continue;
                var tn = comp.GetType().Name;
                if (tn.Contains("Localiz") || tn.Contains("StartGame") || tn.Contains("Continue")
                    || tn == "MainMenuButton" || tn == "ContinueButton" || tn == "LoadLoadoutButton")
                    UnityEngine.Object.DestroyImmediate(comp, false);
            }
            foreach (var le in clone.GetComponentsInChildren<LayoutElement>(true))
                UnityEngine.Object.DestroyImmediate(le, false);
            var button = clone.GetComponentInChildren<Button>(true);
            if (button != null) button.onClick = new Button.ButtonClickedEvent();
        }

        private static void HarvestSfx(ButtonSounds sounds)
        {
            if (sounds == null || _clickSfx != null) return;
            try
            {
                _clickSfx = typeof(ButtonSounds).GetField("clickSfx", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.GetValue(sounds) as string;
                _selectSfx = typeof(ButtonSounds).GetField("selectSfx", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.GetValue(sounds) as string;
            }
            catch { }
        }

        private static GameObject FindSceneObject(string name)
        {
            foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (t == null || !t.gameObject.scene.IsValid()) continue;
                if (string.Equals(t.name, name, StringComparison.Ordinal)) return t.gameObject;
            }
            return null;
        }

        // ---------------------------------------------------------------- sfx

        public static void PlayClick() { PlaySfx(_clickSfx); }
        public static void PlaySelect() { PlaySfx(_selectSfx); }

        private static void PlaySfx(string sfx)
        {
            if (string.IsNullOrEmpty(sfx)) return;
            try { AudioManager.PlaySfx(sfx); } catch { }
        }

        // ---------------------------------------------------------------- widget factory

        /// <summary>A themed button: clone of the vanilla menu button (animator FX + sounds)
        /// when the harvest succeeded, old flat style otherwise. Returns the label; wrapper GO
        /// is label-parent's parent (vanilla) or label parent (fallback).</summary>
        public static TMP_Text MakeButton(Transform parent, string name, string label, Vector2 pos,
            Vector2 size, UnityEngine.Events.UnityAction onClick, float fontSize = 34f)
        {
            if (_buttonTemplate != null)
            {
                var go = UnityEngine.Object.Instantiate(_buttonTemplate, parent);
                go.name = name;
                go.SetActive(true);
                var rt = (RectTransform)go.transform;
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.anchoredPosition = pos;
                rt.sizeDelta = size;
                var body = go.GetComponentInChildren<Button>(true);
                var bodyRt = (RectTransform)body.transform;
                bodyRt.anchorMin = Vector2.zero;
                bodyRt.anchorMax = Vector2.one;
                bodyRt.offsetMin = Vector2.zero;
                bodyRt.offsetMax = Vector2.zero;
                var icon = body.transform.Find("Icon");
                if (icon != null) icon.gameObject.SetActive(false);
                var tmp = go.GetComponentInChildren<TMP_Text>(true);
                tmp.text = label;
                tmp.fontSize = fontSize;
                tmp.enableAutoSizing = false;
                tmp.color = TextBright;
                var trt = tmp.rectTransform;
                trt.anchorMin = Vector2.zero;
                trt.anchorMax = Vector2.one;
                trt.offsetMin = new Vector2(8, 4);
                trt.offsetMax = new Vector2(-8, -2);
                tmp.alignment = TextAlignmentOptions.Center;
                body.onClick = new Button.ButtonClickedEvent();
                body.onClick.AddListener(onClick);
                body.navigation = new Navigation { mode = Navigation.Mode.None };
                return tmp;
            }
            return MakeFlatButton(parent, name, label, pos, size, onClick, fontSize);
        }

        private static TMP_Text MakeFlatButton(Transform parent, string name, string label, Vector2 pos,
            Vector2 size, UnityEngine.Events.UnityAction onClick, float fontSize)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            var img = go.AddComponent<Image>();
            if (FrameSprite != null) { img.sprite = FrameSprite; img.type = Image.Type.Sliced; img.color = Color.white; }
            else img.color = new Color(0.16f, 0.19f, 0.26f, 1f);
            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            var colors = btn.colors;
            colors.highlightedColor = new Color(1.4f, 1.4f, 1.4f, 1f);
            colors.pressedColor = Accent;
            colors.selectedColor = new Color(1.3f, 1.3f, 1.3f, 1f);
            btn.colors = colors;
            btn.onClick.AddListener(onClick);
            btn.onClick.AddListener(PlayClick);
            btn.navigation = new Navigation { mode = Navigation.Mode.None };
            go.AddComponent<UiSelectSfx>();
            var tmp = MakeText(go.transform, "Label", label, fontSize, TextBright, PixelFont);
            Stretch(tmp.rectTransform);
            return tmp;
        }

        /// <summary>The Button component of a MakeButton result. Walks parents manually —
        /// GetComponentInParent skips inactive objects, which broke re-showing hidden buttons
        /// (SetActive landed on the label instead of the wrapper and it never came back).</summary>
        public static Button ButtonOf(TMP_Text label)
        {
            if (label == null) return null;
            for (var t = label.transform; t != null; t = t.parent)
            {
                var b = t.GetComponent<Button>(); // works regardless of active state
                if (b != null) return b;
            }
            return null;
        }

        /// <summary>The outermost GO of a MakeButton result (what to SetActive on).</summary>
        public static GameObject RootOf(TMP_Text label)
        {
            var btn = ButtonOf(label);
            if (btn == null) return label != null ? label.gameObject : null;
            var t = btn.transform;
            // vanilla clone: wrapper(Animator)/ButtonBody(Button); fallback: single GO
            if (t.parent != null && t.parent.GetComponent<PunkButton>() != null) t = t.parent;
            return t.gameObject;
        }

        /// <summary>Toggle a cloned button's vanilla selected/unselected look (tab-frame sprite
        /// swap + PunkButton animator bool + label color).</summary>
        public static void SetToggled(TMP_Text label, bool on)
        {
            var btn = ButtonOf(label);
            if (btn == null) return;
            var punk = btn.GetComponentInParent<PunkButton>();
            try { if (punk != null) punk.SetToggled(on); } catch { }
            var img = btn.targetGraphic as Image;
            if (img != null && FrameOnSprite != null && FrameOffSprite != null)
                img.sprite = on ? FrameOnSprite : FrameOffSprite;
            label.color = on ? TextBright : new Color(Accent.r, Accent.g, Accent.b, 0.85f);
        }

        // 8-bit-hud's cap height is ~1.6x its em size (bitmap-font conversion), so a "17" reads
        // like a 27 — vanilla screens compensate with tiny point sizes (15/30). Normalize so
        // callers can specify visual size in Font_Minimum-equivalent points.
        private const float HudFontScale = 0.62f;

        public static TMP_Text MakeText(Transform parent, string name, string text, float size,
            Color color, TMP_FontAsset font = null)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            var f = font != null ? font : HudFont != null ? HudFont : PixelFont;
            if (f != null) tmp.font = f;
            tmp.text = text;
            if (f != null && f == HudFont) size *= HudFontScale;
            tmp.fontSize = size;
            tmp.color = color;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.richText = true;
            return tmp;
        }

        public static Image MakeImage(Transform parent, string name, Color color, Sprite sprite = null)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            img.color = color;
            if (sprite != null) { img.sprite = sprite; img.type = Image.Type.Sliced; }
            return img;
        }

        /// <summary>Thin horizontal divider line, vanilla settings-window style (#484848).</summary>
        public static Image MakeLine(Transform parent, float y, float sideMargin)
        {
            var img = MakeImage(parent, "Line", new Color(0.282f, 0.282f, 0.282f, 1f));
            var rt = img.rectTransform;
            rt.anchorMin = new Vector2(0, 1);
            rt.anchorMax = new Vector2(1, 1);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0, y);
            rt.offsetMin = new Vector2(sideMargin, rt.offsetMin.y);
            rt.offsetMax = new Vector2(-sideMargin, rt.offsetMax.y);
            rt.sizeDelta = new Vector2(rt.sizeDelta.x, 3);
            return img;
        }

        public static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        /// <summary>Explicit-wire a grid of selectables for gamepad navigation: left/right within
        /// a row, up/down across rows (column clamped). Inactive/null entries are skipped.</summary>
        public static void WireGrid(System.Collections.Generic.List<Selectable[]> grid)
        {
            var rows = grid
                .Select(r => r.Where(s => s != null && s.gameObject.activeInHierarchy && s.IsInteractable()).ToArray())
                .Where(r => r.Length > 0)
                .ToList();
            for (int i = 0; i < rows.Count; i++)
            {
                for (int j = 0; j < rows[i].Length; j++)
                {
                    var nav = new Navigation { mode = Navigation.Mode.Explicit };
                    nav.selectOnLeft = j > 0 ? rows[i][j - 1] : null;
                    nav.selectOnRight = j < rows[i].Length - 1 ? rows[i][j + 1] : null;
                    if (i > 0) nav.selectOnUp = rows[i - 1][Mathf.Min(j, rows[i - 1].Length - 1)];
                    if (i < rows.Count - 1) nav.selectOnDown = rows[i + 1][Mathf.Min(j, rows[i + 1].Length - 1)];
                    rows[i][j].navigation = nav;
                }
            }
        }

        public static bool GamepadLastUsed
        {
            get
            {
                try
                {
                    var tracker = ServiceLocator.Get<LastUsedDeviceTracker>();
                    if (tracker != null) return tracker.GamepadLastUsed;
                }
                catch { }
                return false;
            }
        }
    }

    /// <summary>Select-sound for custom (non-cloned) selectables — same sfx the vanilla
    /// ButtonSounds plays when gamepad focus lands on a button.</summary>
    internal sealed class UiSelectSfx : MonoBehaviour, ISelectHandler
    {
        public void OnSelect(BaseEventData eventData) => UiTheme.PlaySelect();
    }
}
