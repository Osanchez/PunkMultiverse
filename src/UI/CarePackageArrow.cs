using System.Collections.Generic;
using PunkMultiverse.Core;
using PunkMultiverse.Sync;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PunkMultiverse.UI
{
    /// <summary>
    /// The GOLD screen-edge arrow to every live Battle Royale supply drop.
    ///
    /// CarePackages' own doc has promised "every alive player gets an arrow" since the mode
    /// shipped, but nothing ever consumed the dictionary except the map overlay — the only guidance
    /// was a toast saying CHECK YOUR MAP and a small marker on it, and Omar couldn't find the drop
    /// at all (2026-07-29: "I also can't find where the supply drop is. we need a gold identifiable
    /// logo"). This is the missing half: in the WORLD, where the player already is, an arrow
    /// clamped to the screen edge pointing at each drop with its distance — the same mechanism
    /// PlayerTracker uses for offscreen teammates in co-op, in supply-drop gold. It does not
    /// weaken BR's hidden-positions rule: drops are broadcast to everyone by design.
    ///
    /// GOLD is the identity: arrow, glyph and distance all in one colour used by nothing else in
    /// the mode (players get the lobby palette, the ring gets reds). The arrow carries the same
    /// crate glyph the map marker uses, so "the gold thing on my screen" and "the gold thing on
    /// the map" are visibly one object.
    /// </summary>
    internal sealed class CarePackageArrow : MonoBehaviour
    {
        internal static readonly Color Gold = new Color32(255, 200, 40, 255);
        private const float EdgeMargin = 72f; // inside the player arrows' orbit, never on top of them

        private sealed class Marker
        {
            internal RectTransform Arrow;
            internal Image ArrowImage;
            internal RectTransform Crate;
            internal TMP_Text Distance;
        }

        private readonly List<Marker> _markers = new List<Marker>();
        private GameObject _canvasGo;
        private TMP_FontAsset _font;

        private void LateUpdate()
        {
            var session = NetSession.Instance;
            bool live = session != null && session.State == Core.SessionState.InGame
                        && Modes.BattleRoyale.Active && Modes.BattleRoyale.CarePackages.Count > 0;
            if (!live)
            {
                for (int i = 0; i < _markers.Count; i++)
                    if (_markers[i].Arrow != null && _markers[i].Arrow.gameObject.activeSelf)
                        _markers[i].Arrow.gameObject.SetActive(false);
                return;
            }

            var cam = Camera.main;
            if (cam == null) return;
            EnsureCanvas();

            int used = 0;
            foreach (var kv in Modes.BattleRoyale.CarePackages)
            {
                if (used >= 8) break; // more simultaneous drops than that is a config accident
                var marker = used < _markers.Count ? _markers[used] : Make();
                used++;
                Point(marker, cam, kv.Value);
            }
            for (int i = used; i < _markers.Count; i++)
                if (_markers[i].Arrow.gameObject.activeSelf) _markers[i].Arrow.gameObject.SetActive(false);
        }

        /// <summary>Same edge-clamp PlayerTracker uses: point from screen centre at the target and
        /// pin the arrow where that ray leaves the screen. Hidden while the drop itself is visible
        /// — the crate in the world is its own marker then.</summary>
        private void Point(Marker marker, Camera cam, Vector2 world)
        {
            Vector3 vp = cam.WorldToViewportPoint(world);
            bool onScreen = vp.z > 0 && vp.x > 0.02f && vp.x < 0.98f && vp.y > 0.02f && vp.y < 0.98f;
            marker.Arrow.gameObject.SetActive(!onScreen);
            if (onScreen) return;

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
            marker.Arrow.anchoredPosition = dir * scale;
            marker.Arrow.localRotation = Quaternion.Euler(0, 0, Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg - 90f);

            // The crate glyph and distance stay upright while the arrow rotates to point.
            var upright = Quaternion.Inverse(marker.Arrow.localRotation);
            marker.Crate.localRotation = upright;
            if (marker.Distance != null)
            {
                float dist = ShipSync.LocalShip != null
                    ? Vector2.Distance(ShipSync.LocalShip.transform.position, world) : 0f;
                marker.Distance.text = $"{dist:0}m";
                marker.Distance.rectTransform.localRotation = upright;
            }

            // One shared pulse, same rhythm as the map marker — the two surfaces read as one thing.
            float pulse = 1f + 0.12f * Mathf.Sin(Time.unscaledTime * 3.4f);
            marker.Arrow.localScale = new Vector3(pulse, pulse, 1f);
        }

        // ---------------------------------------------------------------- construction

        private void EnsureCanvas()
        {
            if (_canvasGo != null) return;
            _canvasGo = new GameObject("PunkMV_CarePackageArrows");
            _canvasGo.transform.SetParent(transform, false);
            var canvas = _canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 3990; // under the player tracker, over the game
            var scaler = _canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            if (_font == null)
            {
                _font = TMP_Settings.defaultFontAsset;
                if (_font == null)
                {
                    var fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
                    if (fonts.Length > 0) _font = fonts[0];
                }
            }
        }

        private Marker Make()
        {
            var arrowGo = new GameObject("PunkMV_DropArrow", typeof(RectTransform));
            arrowGo.transform.SetParent(_canvasGo.transform, false);
            var marker = new Marker { Arrow = (RectTransform)arrowGo.transform };
            marker.Arrow.sizeDelta = new Vector2(40, 40);
            marker.ArrowImage = arrowGo.AddComponent<Image>();
            marker.ArrowImage.sprite = SupplyCrateSprites.Arrow();
            marker.ArrowImage.color = Gold;
            marker.ArrowImage.raycastTarget = false;

            // The crate glyph sits behind the arrow head, toward the screen centre.
            var crateGo = new GameObject("Crate", typeof(RectTransform), typeof(Image));
            crateGo.transform.SetParent(arrowGo.transform, false);
            marker.Crate = (RectTransform)crateGo.transform;
            marker.Crate.anchoredPosition = new Vector2(0, -34);
            marker.Crate.sizeDelta = new Vector2(34, 34);
            var crateImage = crateGo.GetComponent<Image>();
            crateImage.sprite = SupplyCrateSprites.Crate();
            crateImage.raycastTarget = false;

            var textGo = new GameObject("Dist", typeof(RectTransform));
            textGo.transform.SetParent(arrowGo.transform, false);
            marker.Distance = textGo.AddComponent<TextMeshProUGUI>();
            if (_font != null) marker.Distance.font = _font;
            marker.Distance.fontSize = 17;
            marker.Distance.fontStyle = FontStyles.Bold;
            marker.Distance.color = Gold;
            marker.Distance.outlineWidth = 0.2f;
            marker.Distance.outlineColor = new Color32(0, 0, 0, 255);
            marker.Distance.alignment = TextAlignmentOptions.Center;
            var trt = (RectTransform)textGo.transform;
            trt.anchoredPosition = new Vector2(0, -62);
            trt.sizeDelta = new Vector2(120, 24);

            _markers.Add(marker);
            return marker;
        }
    }

    /// <summary>The supply-drop iconography, generated once: a PIXEL-ART GOLD CRATE under a
    /// parachute (the thing you are actually looking for in the world) and the pointer arrow.
    /// Drawn at 16x16 with point filtering so it lands in the game's own 8-bit language — Omar,
    /// 2026-07-29: "we need a gold identifiable logo or something, even if that means creating a
    /// custom logo to add into the game".</summary>
    internal static class SupplyCrateSprites
    {
        private static Sprite _crate, _arrow;

        internal static Sprite Crate()
        {
            if (_crate != null) return _crate;
            const int S = 16;
            var px = new Color32[S * S];
            var none = new Color32(0, 0, 0, 0);
            var gold = new Color32(255, 200, 40, 255);
            var dark = new Color32(150, 100, 10, 255);
            var line = new Color32(60, 38, 6, 255);
            var chute = new Color32(255, 232, 140, 255);
            for (int i = 0; i < px.Length; i++) px[i] = none;
            void P(int x, int y, Color32 c) { if (x >= 0 && x < S && y >= 0 && y < S) px[y * S + x] = c; }

            // Crate body rows 0..7: gold fill, dark outline, a cross of planks.
            for (int y = 0; y <= 7; y++)
                for (int x = 3; x <= 12; x++)
                {
                    bool edge = y == 0 || y == 7 || x == 3 || x == 12;
                    P(x, y, edge ? line : gold);
                }
            for (int x = 4; x <= 11; x++) P(x, 4, dark);          // horizontal strap
            P(7, 1, dark); P(8, 1, dark); P(7, 2, dark); P(8, 2, dark);
            P(7, 5, dark); P(8, 5, dark); P(7, 6, dark); P(8, 6, dark); // vertical strap

            // Parachute canopy rows 11..15 with suspension lines 8..10.
            for (int x = 4; x <= 11; x++) P(x, 13, chute);
            for (int x = 3; x <= 12; x++) P(x, 12, chute);
            for (int x = 5; x <= 10; x++) P(x, 14, chute);
            for (int x = 6; x <= 9; x++) P(x, 15, chute);
            P(4, 11, line); P(7, 11, line); P(8, 11, line); P(11, 11, line);
            P(4, 10, line); P(11, 10, line);
            P(5, 9, line); P(10, 9, line);
            P(6, 8, line); P(9, 8, line);

            _crate = FromPixels(px, S);
            return _crate;
        }

        /// <summary>A plain solid pointer (up; rotated by the caller). White so the Image tint
        /// owns the colour.</summary>
        internal static Sprite Arrow()
        {
            if (_arrow != null) return _arrow;
            const int S = 16;
            var px = new Color32[S * S];
            var none = new Color32(0, 0, 0, 0);
            var white = new Color32(255, 255, 255, 255);
            for (int i = 0; i < px.Length; i++) px[i] = none;
            for (int y = 0; y < S; y++)
            {
                int halfWidth = (S - 1 - y) / 2; // widest at the bottom, a point at the top
                for (int x = 7 - halfWidth; x <= 8 + halfWidth; x++)
                    if (y >= 6) px[y * S + x] = white;      // head only — a clean chevron
            }
            _arrow = FromPixels(px, S);
            return _arrow;
        }

        private static Sprite FromPixels(Color32[] px, int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
            tex.SetPixels32(px);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
        }
    }
}
