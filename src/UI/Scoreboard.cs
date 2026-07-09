using PunkMultiverse.Core;
using PunkMultiverse.Sync;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace PunkMultiverse.UI
{
    /// <summary>
    /// Hold Tab during a net run for the party scoreboard: each player's color, name, live HP bar,
    /// kills, deaths, and distance from you (spiritual successor of PunkScoreboard, net-aware).
    /// </summary>
    public sealed class Scoreboard : MonoBehaviour
    {
        private GameObject _canvasGo;
        private TMP_FontAsset _font;

        private sealed class Row
        {
            public GameObject Root;
            public Image Swatch;
            public TMP_Text Name;
            public RectTransform HpFill;
            public Image HpFillImage;
            public TMP_Text Stats;
        }

        private readonly Row[] _rows = new Row[NetSession.MaxPlayers];

        private void Update()
        {
            var session = NetSession.Instance;
            bool show = session != null && session.State == SessionState.InGame
                        && NetConfig.Scoreboard.Value
                        && Keyboard.current != null && Keyboard.current[Key.Tab].isPressed;
            if (show && _canvasGo == null) Build();
            if (_canvasGo == null) return;
            if (_canvasGo.activeSelf != show) _canvasGo.SetActive(show);
            if (show) Refresh(session);
        }

        private void OnDestroy()
        {
            if (_canvasGo != null) Destroy(_canvasGo);
        }

        private void Build()
        {
            _font = TMP_Settings.defaultFontAsset;
            if (_font == null)
            {
                var fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
                if (fonts.Length > 0) _font = fonts[0];
            }

            _canvasGo = new GameObject("PunkMV_Scoreboard");
            _canvasGo.transform.SetParent(transform, false);
            var canvas = _canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 4500;
            var scaler = _canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);

            var panel = MakeImage(_canvasGo.transform, "Panel", new Color(0.05f, 0.06f, 0.09f, 0.92f));
            var prt = panel.rectTransform;
            prt.anchorMin = prt.anchorMax = new Vector2(0.5f, 1f);
            prt.pivot = new Vector2(0.5f, 1f);
            prt.anchoredPosition = new Vector2(0, -60);
            prt.sizeDelta = new Vector2(640, 64 + NetSession.MaxPlayers * 56);

            MakeText(panel.transform, "Title", "PARTY", 26, new Color(0.98f, 0.55f, 0.18f), y: -8, height: 34);
            var header = MakeText(panel.transform, "Header", "", 16, new Color(1, 1, 1, 0.5f), y: -40, height: 20);
            header.text = "<pos=8%>PLAYER<pos=52%>HP<pos=72%>KILLS<pos=84%>DEATHS<pos=94%>DIST";
            header.alignment = TextAlignmentOptions.MidlineLeft;

            for (int i = 0; i < NetSession.MaxPlayers; i++)
            {
                var row = new Row();
                var bg = MakeImage(panel.transform, $"Row{i}", new Color(1, 1, 1, 0.05f));
                var rt = bg.rectTransform;
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
                rt.pivot = new Vector2(0.5f, 1f);
                rt.anchoredPosition = new Vector2(0, -64 - i * 56);
                rt.sizeDelta = new Vector2(616, 48);
                row.Root = bg.gameObject;

                var swatch = MakeImage(bg.transform, "Swatch", Color.gray);
                var srt = swatch.rectTransform;
                srt.anchorMin = srt.anchorMax = new Vector2(0f, 0.5f);
                srt.pivot = new Vector2(0f, 0.5f);
                srt.anchoredPosition = new Vector2(10, 0);
                srt.sizeDelta = new Vector2(26, 26);
                row.Swatch = swatch;

                row.Name = MakeText(bg.transform, "Name", "", 20, Color.white, y: 0, height: 48);
                row.Name.alignment = TextAlignmentOptions.MidlineLeft;
                var nrt = row.Name.rectTransform;
                nrt.anchorMin = Vector2.zero;
                nrt.anchorMax = Vector2.one;
                nrt.offsetMin = new Vector2(48, 0);
                nrt.offsetMax = new Vector2(-330, 0);

                // HP bar: background track + fill whose anchorMax.x is the HP fraction.
                var track = MakeImage(bg.transform, "HpTrack", new Color(1, 1, 1, 0.12f));
                var trt = track.rectTransform;
                trt.anchorMin = new Vector2(0.46f, 0.5f);
                trt.anchorMax = new Vector2(0.66f, 0.5f);
                trt.pivot = new Vector2(0f, 0.5f);
                trt.anchoredPosition = Vector2.zero;
                trt.sizeDelta = new Vector2(0, 14);
                var fill = MakeImage(track.transform, "HpFill", new Color(0.31f, 0.85f, 0.47f));
                row.HpFill = fill.rectTransform;
                row.HpFill.anchorMin = Vector2.zero;
                row.HpFill.anchorMax = new Vector2(1f, 1f);
                row.HpFill.offsetMin = Vector2.zero;
                row.HpFill.offsetMax = Vector2.zero;
                row.HpFillImage = fill;

                row.Stats = MakeText(bg.transform, "Stats", "", 18, Color.white, y: 0, height: 48);
                row.Stats.alignment = TextAlignmentOptions.MidlineRight;
                var strt = row.Stats.rectTransform;
                strt.anchorMin = Vector2.zero;
                strt.anchorMax = Vector2.one;
                strt.offsetMin = new Vector2(420, 0);
                strt.offsetMax = new Vector2(-12, 0);

                _rows[i] = row;
            }
        }

        private void Refresh(NetSession session)
        {
            for (int i = 0; i < NetSession.MaxPlayers; i++)
            {
                var row = _rows[i];
                var p = i < session.Players.Count ? session.Players[i] : null;
                if (p == null)
                {
                    row.Root.SetActive(false);
                    continue;
                }
                row.Root.SetActive(true);
                var color = PlayerColors.Get(p.ColorIndex);
                row.Swatch.color = color;

                string tags = p.IsLocal ? " <color=#888888>(YOU)</color>" : "";
                if (!p.Connected) tags += " <color=#ff7070>OFFLINE</color>";
                row.Name.text = $"<color=#{ColorUtility.ToHtmlStringRGB(color)}>{p.Name}</color>{tags}";

                float hp = 0f;
                float dist = -1f;
                if (ShipSync.ShipsBySlot.TryGetValue(p.Slot, out var ship) && ship != null)
                {
                    var dr = ship.GetComponent<DamagableResource>();
                    try { if (dr != null && dr.MaxHealth > 0) hp = Mathf.Clamp01(dr.CurrentHealth / dr.MaxHealth); } catch { }
                    if (!p.IsLocal && ShipSync.LocalShip != null)
                        dist = Vector2.Distance(ShipSync.LocalShip.transform.position, ship.transform.position);
                }
                row.HpFill.anchorMax = new Vector2(Mathf.Max(0.001f, hp), 1f);
                row.HpFillImage.color = hp > 0.5f ? new Color(0.31f, 0.85f, 0.47f)
                    : hp > 0.25f ? new Color(0.98f, 0.83f, 0.22f) : new Color(0.95f, 0.30f, 0.24f);

                string distText = p.IsLocal ? "—" : (dist >= 0 ? $"{dist:0}m" : "?");
                row.Stats.text = $"{NetStats.Kills[i]}   {NetStats.Deaths[i]}   <color=#888888>{distText}</color>";
            }
        }

        // ---------------------------------------------------------------- widget helpers

        private Image MakeImage(Transform parent, string name, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            img.color = color;
            return img;
        }

        private TMP_Text MakeText(Transform parent, string name, string text, float size, Color color, float y, float height)
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
    }
}
