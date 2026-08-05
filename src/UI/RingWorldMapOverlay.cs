using System.Collections.Generic;
using HarmonyLib;
using PunkMultiverse.Core;
using UnityEngine;
using UnityEngine.UI;

namespace PunkMultiverse.UI
{
    /// <summary>
    /// Draws the Battle Royale ring on the FULL MAP SCREEN.
    ///
    /// <see cref="RingMapOverlay"/> stamps the ring into the HUD minimap's texture, but that
    /// minimap is a 100x100-cell window around your own ship — it can only ever show the sliver of
    /// the boundary you are already standing next to. The map screen is where a player actually
    /// plans a route to the next zone, and it had no ring at all: the only thing visible there was
    /// the lava the host had already painted, which tells you where the ring HAS BEEN, never where
    /// it is going. Field-reported 2026-07-27: "I only see the new ring on the minimap in the HUD
    /// and not the main map".
    ///
    /// Drawn as UI on the map's own icon layer rather than stamped into <c>MapDrawer.mapTexture</c>,
    /// for two reasons: the map texture is authoritative terrain art that <c>DrawSegment</c>
    /// repaints from cell data whenever terrain changes (our pixels would flicker in and out), and
    /// a ring stamped at one radius leaves its old pixels behind at the next — a smear of stale
    /// boundaries. Parented to the same transform the vanilla map icons use and positioned with the
    /// same formula, so it pans and zooms with the map for free, and costs nothing when the map is
    /// closed (its parent is inactive, so nothing renders and Unity skips the subtree).
    ///
    /// One Image per circle, textured with a generated dashed ring. The texture is regenerated only
    /// when the on-screen diameter crosses a power-of-two bucket, which keeps the dashes at a
    /// roughly constant ~2px whether the ring is the whole world or the last hundred metres.
    /// </summary>
    internal sealed class RingWorldMapOverlay : MonoBehaviour
    {
        // Same palette roles as the minimap overlay, so the two surfaces read as one language.
        // (The drop marker's gold lives in UI/CarePackageArrow.cs with the shared crate sprite.)
        private static readonly Color CurrentRing = new Color32(255, 70, 30, 255);
        private static readonly Color IncomingRing = new Color32(255, 170, 40, 255);

        private Transform _iconsParent;
        private MapMover _mapMover;
        private Level _level;

        private RectTransform _current;
        private RectTransform _target;
        private Image _currentImage;
        private Image _targetImage;
        private readonly List<RectTransform> _drops = new List<RectTransform>();
        private GameObject _root;

        private void LateUpdate()
        {
            // Gate on the SESSION being live, not on the ring being visible: RingVisible is false
            // for the whole first hold (deliberately, for the circles — a boundary that hasn't
            // moved marks ground that isn't in play), and the SUPPLY DROP markers were caught
            // behind the same gate. A drop that lands during the opening hold — which the first
            // one now always does — was simply absent from the map (Omar, 2026-07-29: "you are not
            // showing its location in the big map where players usually go to look").
            if (!Modes.BattleRoyale.RingPersists)
            {
                if (_root != null) Destroy(_root);
                _root = null;
                _current = _target = null;
                _drops.Clear();
                return;
            }
            try { Draw(); }
            catch { /* the map must never be the thing that breaks a match */ }
        }

        private void Draw()
        {
            if (!Resolve()) return;
            // The map tab is inactive most of the time; nothing below costs anything then, but
            // skipping the work outright keeps it off the profile entirely.
            if (!_iconsParent.gameObject.activeInHierarchy) return;

            float zoom = _mapMover.Zoom;
            if (zoom <= 0f) return;
            var ring = Modes.BattleRoyale.Ring;
            var ringCenter = Modes.BattleRoyale.RingCenter;

            EnsureWidgets();

            // The CURRENT boundary: where the lava is now. Hidden until the first closure — see
            // RingVisible — while the drops below draw regardless.
            bool ringVisible = Modes.BattleRoyale.RingVisible;
            _current.gameObject.SetActive(ringVisible);
            if (ringVisible)
                Place(_current, _currentImage, ringCenter, Modes.BattleRoyale.RingRadius, zoom,
                    CurrentRing, dashes: 64);
            // Where you have to be standing after this closure — shown only in the run-up to it and
            // during it (BattleRoyale.NextRingVisible), so the amber circle keeps meaning "move".
            // On its OWN centre: the zone drifts, so the amber circle is somewhere else on the map,
            // and the gap between the two is what tells a player which way to cross.
            bool hasTarget = ringVisible && ring.TargetRadius > 1f && Modes.BattleRoyale.NextRingVisible;
            _target.gameObject.SetActive(hasTarget);
            if (hasTarget)
                Place(_target, _targetImage, Modes.BattleRoyale.RingTargetCenter, ring.TargetRadius,
                    zoom, IncomingRing, dashes: 32);

            DrawDrops(zoom);
        }

        /// <summary>Map a world position onto the map's icon layer — the exact expression
        /// <c>MapIconManager.RefreshIconsPositionAndVisibility</c> uses, so our circles land on the
        /// same pixels as the station icons instead of half a cell away at high zoom.</summary>
        private Vector3 ToMapLocal(Vector2 world, float zoom)
        {
            var half = new Vector2Int(_level.Width, _level.Height) / 2;
            return (Vector3)(Vector2)(Vector2Int.RoundToInt(world - (Vector2)half)) * zoom;
        }

        private void Place(RectTransform rt, Image image, Vector2 center, float radius,
            float zoom, Color color, int dashes)
        {
            float diameter = Mathf.Max(1f, 2f * radius * zoom);
            rt.localPosition = ToMapLocal(center, zoom);
            rt.sizeDelta = new Vector2(diameter, diameter);
            image.color = color;
            image.sprite = RingSprite(diameter, dashes);
        }

        private void DrawDrops(float zoom)
        {
            var packages = Modes.BattleRoyale.CarePackages;
            // One shared pulse so every drop breathes together — staggering them would read as
            // several unrelated things blinking rather than one kind of objective.
            float pulse = 1f + 0.22f * Mathf.Sin(Time.unscaledTime * 3.4f);
            int i = 0;
            foreach (var kv in packages)
            {
                if (i >= _drops.Count) _drops.Add(MakeDrop());
                var rt = _drops[i++];
                rt.gameObject.SetActive(true);
                rt.localPosition = ToMapLocal(kv.Value, zoom);
                rt.localScale = new Vector3(pulse, pulse, 1f);
            }
            for (; i < _drops.Count; i++) _drops[i].gameObject.SetActive(false);
        }

        // ---------------------------------------------------------------- plumbing

        private static System.Reflection.FieldInfo _iconsParentField;
        private static System.Reflection.FieldInfo _mapMoverField;

        private bool Resolve()
        {
            if (_iconsParent != null && _mapMover != null && _level != null) return true;
            try
            {
                var manager = ServiceLocator.Get<MapIconManager>();
                if (manager == null) return false;
                if (_iconsParentField == null)
                    _iconsParentField = AccessTools.Field(typeof(MapIconManager), "iconsParent");
                if (_mapMoverField == null)
                    _mapMoverField = AccessTools.Field(typeof(MapIconManager), "mapMover");
                _iconsParent = _iconsParentField?.GetValue(manager) as Transform;
                _mapMover = _mapMoverField?.GetValue(manager) as MapMover;
                _level = ServiceLocator.Get<Level>();
            }
            catch { return false; }
            return _iconsParent != null && _mapMover != null && _level != null;
        }

        private void EnsureWidgets()
        {
            if (_root != null && _current != null && _target != null) return;
            _root = new GameObject("PunkMV_MapRing", typeof(RectTransform));
            _root.transform.SetParent(_iconsParent, worldPositionStays: false);
            ((RectTransform)_root.transform).localPosition = Vector3.zero;
            _drops.Clear();
            _current = MakeCircle("CurrentRing", out _currentImage);
            _target = MakeCircle("TargetRing", out _targetImage);
        }

        private RectTransform MakeCircle(string name, out Image image)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(_root.transform, worldPositionStays: false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            image = go.GetComponent<Image>();
            image.raycastTarget = false; // the map is click-to-teleport; never eat that click
            return rt;
        }

        /// <summary>The supply-drop marker: the GOLD PIXEL-ART CRATE-UNDER-A-PARACHUTE, shared with
        /// the in-world edge arrow (UI/CarePackageArrow.cs) so the map and the world visibly point
        /// at the same object. It replaced a teal bracket reticle that still went unfound in the
        /// field — Omar, 2026-07-29: "I also can't find where the supply drop is. we need a gold
        /// identifiable logo or something." Gold is reserved for drops (players get the lobby
        /// palette, the ring gets reds), and the pulse in Draw() keeps it moving so the eye snags
        /// on it at any zoom.</summary>
        private RectTransform MakeDrop()
        {
            var go = new GameObject("SupplyDrop", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(_root.transform, worldPositionStays: false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(34f, 34f);
            var image = go.GetComponent<Image>();
            image.sprite = SupplyCrateSprites.Crate();
            image.raycastTarget = false; // the map is click-to-teleport; never eat that click
            return rt;
        }

        // ---------------------------------------------------------------- generated ring sprite

        // Cache by (texture size, dash count): a match uses a handful of these for its whole life.
        private static readonly Dictionary<int, Sprite> RingSprites = new Dictionary<int, Sprite>();

        /// <summary>A dashed ring drawn at a resolution close to how big it will be shown, so the
        /// line stays about two pixels wide at every zoom instead of turning into a fat band when
        /// the ring is small and vanishing when it is the whole world.</summary>
        private static Sprite RingSprite(float displayDiameter, int dashes)
        {
            int size = Mathf.Clamp(Mathf.NextPowerOfTwo(Mathf.CeilToInt(displayDiameter)), 32, 512);
            int key = size * 1000 + dashes;
            if (RingSprites.TryGetValue(key, out var cached) && cached != null) return cached;

            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
            var pixels = new Color32[size * size];
            var clear = new Color32(255, 255, 255, 0);
            for (int i = 0; i < pixels.Length; i++) pixels[i] = clear;

            float radius = size * 0.5f - 1.5f;
            float center = size * 0.5f;
            // Walk by arc length so the dashes are evenly spaced at any radius, and step in half
            // pixels so the line has no gaps where it runs diagonally.
            int steps = Mathf.Max(64, Mathf.CeilToInt(2f * Mathf.PI * radius * 2f));
            int perDash = Mathf.Max(1, steps / Mathf.Max(1, dashes * 2));
            var white = new Color32(255, 255, 255, 255);
            for (int i = 0; i < steps; i++)
            {
                if ((i / perDash) % 2 == 1) continue; // dashed: on, off, on, ...
                float a = i / (float)steps * Mathf.PI * 2f;
                float cx = center + Mathf.Cos(a) * radius;
                float cy = center + Mathf.Sin(a) * radius;
                for (int dy = 0; dy <= 1; dy++)
                    for (int dx = 0; dx <= 1; dx++)
                    {
                        int px = Mathf.RoundToInt(cx) + dx, py = Mathf.RoundToInt(cy) + dy;
                        if (px < 0 || py < 0 || px >= size || py >= size) continue;
                        pixels[py * size + px] = white;
                    }
            }
            tex.SetPixels32(pixels);
            tex.Apply();
            var sprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
            RingSprites[key] = sprite;
            return sprite;
        }
    }
}
