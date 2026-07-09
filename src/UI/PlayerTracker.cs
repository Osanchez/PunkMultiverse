using System.Collections.Generic;
using PunkMultiverse.Core;
using PunkMultiverse.Sync;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PunkMultiverse.UI
{
    /// <summary>
    /// Lets players find each other. Each remote player's ship carries their name in their chosen
    /// color (bold, outlined, drawn above everything) — no ring. When a teammate is offscreen, an
    /// arrow in their color sits clamped to the screen edge pointing at them with the distance,
    /// and disappears the moment they come into view.
    /// </summary>
    public sealed class PlayerTracker : MonoBehaviour
    {
        private const float EdgeMargin = 56f;

        private sealed class Visuals
        {
            public byte Slot;
            public Ship Ship;
            public TextMeshPro Label;
            public RectTransform Arrow;
            public Image ArrowImage;
            public TMP_Text ArrowText;
        }

        private readonly Dictionary<int, Visuals> _visuals = new Dictionary<int, Visuals>();
        private GameObject _canvasGo;
        private static Sprite _arrowSprite;
        private TMP_FontAsset _font;

        private void LateUpdate()
        {
            var session = NetSession.Instance;
            if (session == null || session.State != SessionState.InGame)
            {
                if (_visuals.Count > 0) Clear();
                return;
            }

            foreach (var p in session.Players)
            {
                if (p == null || p.IsLocal) continue;
                if (!ShipSync.ShipsBySlot.TryGetValue(p.Slot, out var ship) || ship == null) continue;
                if (!_visuals.TryGetValue(p.Slot, out var v) || v.Ship != ship)
                    _visuals[p.Slot] = v = Create(p, ship);
                UpdateVisuals(v, p);
            }
        }

        private void OnDestroy() => Clear();

        private void Clear()
        {
            foreach (var v in _visuals.Values)
            {
                if (v.Label != null) Destroy(v.Label.gameObject);
                if (v.Arrow != null) Destroy(v.Arrow.gameObject);
            }
            _visuals.Clear();
        }

        // ---------------------------------------------------------------- creation

        private Visuals Create(NetPlayer player, Ship ship)
        {
            var color = PlayerColors.Get(player.ColorIndex);
            var v = new Visuals { Slot = player.Slot, Ship = ship };
            if (_font == null) _font = TMP_Settings.defaultFontAsset ?? FindAnyFont();

            if (NetConfig.TrackerNames.Value)
            {
                var labelGo = new GameObject("PunkMV_Name");
                labelGo.transform.SetParent(ship.transform, false);
                labelGo.transform.localPosition = new Vector3(0f, 1.15f, 0f);
                v.Label = labelGo.AddComponent<TextMeshPro>();
                if (_font != null) v.Label.font = _font;
                v.Label.text = player.Name;
                v.Label.fontSize = 6f;
                v.Label.fontStyle = FontStyles.Bold;
                v.Label.color = color;
                v.Label.outlineWidth = 0.25f;
                v.Label.outlineColor = new Color32(0, 0, 0, 255);
                v.Label.alignment = TextAlignmentOptions.Center;
                v.Label.rectTransform.sizeDelta = new Vector2(14f, 2f);
                var renderer = labelGo.GetComponent<Renderer>();
                if (renderer != null)
                {
                    // Above everything the ship renders.
                    var shipSr = ship.GetComponentInChildren<SpriteRenderer>();
                    if (shipSr != null) renderer.sortingLayerID = shipSr.sortingLayerID;
                    renderer.sortingOrder = 5000;
                }
            }

            if (NetConfig.TrackerArrows.Value)
            {
                EnsureCanvas();
                var arrowGo = new GameObject($"PunkMV_Arrow{player.Slot}", typeof(RectTransform));
                arrowGo.transform.SetParent(_canvasGo.transform, false);
                v.Arrow = (RectTransform)arrowGo.transform;
                v.Arrow.sizeDelta = new Vector2(38, 38);
                v.ArrowImage = arrowGo.AddComponent<Image>();
                v.ArrowImage.sprite = ArrowSprite();
                v.ArrowImage.color = color;

                var textGo = new GameObject("Dist", typeof(RectTransform));
                textGo.transform.SetParent(arrowGo.transform, false);
                v.ArrowText = textGo.AddComponent<TextMeshProUGUI>();
                if (_font != null) v.ArrowText.font = _font;
                v.ArrowText.fontSize = 19;
                v.ArrowText.fontStyle = FontStyles.Bold;
                v.ArrowText.color = color;
                v.ArrowText.outlineWidth = 0.2f;
                v.ArrowText.outlineColor = new Color32(0, 0, 0, 255);
                v.ArrowText.alignment = TextAlignmentOptions.Center;
                var trt = (RectTransform)textGo.transform;
                trt.anchoredPosition = new Vector2(0, -32);
                trt.sizeDelta = new Vector2(160, 26);
            }

            return v;
        }

        private static TMP_FontAsset FindAnyFont()
        {
            var fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
            return fonts.Length > 0 ? fonts[0] : null;
        }

        private void EnsureCanvas()
        {
            if (_canvasGo != null) return;
            _canvasGo = new GameObject("PunkMV_Tracker");
            _canvasGo.transform.SetParent(transform, false);
            var canvas = _canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 4000;
            var scaler = _canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
        }

        // ---------------------------------------------------------------- per-frame

        private void UpdateVisuals(Visuals v, NetPlayer player)
        {
            var cam = Camera.main;
            if (cam == null || v.Ship == null) return;
            var color = PlayerColors.Get(player.ColorIndex);

            if (v.Label != null)
            {
                if (v.Label.color != color) v.Label.color = color;
                if (v.Label.text != player.Name) v.Label.text = player.Name;
                // Billboard: keep the name upright even if the ship rotates.
                v.Label.transform.rotation = Quaternion.identity;
            }

            if (v.Arrow == null) return;
            Vector3 shipPos = v.Ship.transform.position;
            Vector3 vp = cam.WorldToViewportPoint(shipPos);
            bool onScreen = vp.z > 0 && vp.x > 0.02f && vp.x < 0.98f && vp.y > 0.02f && vp.y < 0.98f;
            v.Arrow.gameObject.SetActive(!onScreen);
            if (onScreen) return;

            if (v.ArrowImage.color != color) v.ArrowImage.color = color;

            // Point from screen center toward the ship; clamp to the screen edge with a margin.
            if (vp.z < 0) { vp.x = 1f - vp.x; vp.y = 1f - vp.y; }
            var canvasRt = (RectTransform)_canvasGo.transform;
            Vector2 half = canvasRt.rect.size * 0.5f;
            Vector2 dir = new Vector2(vp.x - 0.5f, vp.y - 0.5f);
            if (dir.sqrMagnitude < 0.0001f) dir = Vector2.right;
            dir.Normalize();
            Vector2 bounds = half - new Vector2(EdgeMargin, EdgeMargin);
            float scale = Mathf.Min(
                Mathf.Abs(dir.x) > 0.0001f ? bounds.x / Mathf.Abs(dir.x) : float.MaxValue,
                Mathf.Abs(dir.y) > 0.0001f ? bounds.y / Mathf.Abs(dir.y) : float.MaxValue);
            v.Arrow.anchoredPosition = dir * scale;
            v.Arrow.localRotation = Quaternion.Euler(0, 0, Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg - 90f);

            if (v.ArrowText != null)
            {
                if (v.ArrowText.color != color) v.ArrowText.color = color;
                float dist = ShipSync.LocalShip != null
                    ? Vector2.Distance(ShipSync.LocalShip.transform.position, shipPos)
                    : 0f;
                v.ArrowText.text = $"{player.Name}  {dist:0}m";
                v.ArrowText.rectTransform.localRotation = Quaternion.Inverse(v.Arrow.localRotation);
            }
        }

        // ---------------------------------------------------------------- generated sprite

        private static Sprite ArrowSprite()
        {
            if (_arrowSprite != null) return _arrowSprite;
            const int size = 32;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float progress = y / (float)(size - 1);
                    float halfWidth = (1f - progress) * (size * 0.45f);
                    bool inside = Mathf.Abs(x - (size - 1) / 2f) <= halfWidth && y > size * 0.1f;
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(inside ? 255 : 0));
                }
            tex.SetPixels32(pixels);
            tex.Apply();
            _arrowSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
            return _arrowSprite;
        }
    }
}
