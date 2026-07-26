using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using PunkMultiverse.Core;
using PunkMultiverse.Sync;
using UnityEngine;

namespace PunkMultiverse.UI
{
    /// <summary>
    /// A small health/fuel readout above every OTHER player's ship — red over blue — so you can
    /// tell at a glance whether the ship in front of you is nearly dead or nearly stranded.
    /// Enemies already advertise their health this way; player ships did not.
    ///
    /// Deliberately not the vanilla widget: HealthbarOwner/HealthbarWidget build SEGMENTED bars at
    /// a fixed pixels-per-resource-unit, so an upgraded ship draws a physically longer (eventually
    /// multi-row) bar. These bars are a fixed size filled by FRACTION, so a starter ship and a
    /// fully-upgraded one look identical except for how full they are.
    ///
    /// Drawn in world space and only for ships on screen, so it reveals nothing you cannot already
    /// see — which is why it coexists with Battle Royale hiding player locations.
    /// </summary>
    internal sealed class ShipStatusBars : MonoBehaviour
    {
        private sealed class Bars
        {
            internal GameObject Root;
            internal RectTransform HealthFill;
            internal RectTransform FuelFill;
        }

        private readonly Dictionary<byte, Bars> _bars = new Dictionary<byte, Bars>();
        private Canvas _canvas;
        private GameObject _canvasGo;
        private Resource _fuelResource;
        private bool _fuelLookupDone;

        // Grunt-sized: small enough to read as part of the world, not a UI panel.
        private const float BarWidth = 34f;
        private const float BarHeight = 4f;
        private const float BarGap = 1f;
        private const float WorldYOffset = 2.2f;

        private static readonly Color HealthColor = new Color(0.85f, 0.15f, 0.15f, 0.95f);
        private static readonly Color FuelColor = new Color(0.20f, 0.55f, 1f, 0.95f);
        private static readonly Color BackColor = new Color(0f, 0f, 0f, 0.55f);

        private void LateUpdate()
        {
            var session = NetSession.Instance;
            if (session == null || session.State != SessionState.InGame
                || !NetConfig.ShipStatusBars.Value)
            {
                if (_bars.Count > 0) Clear();
                return;
            }

            var cam = Camera.main;
            if (cam == null) { if (_bars.Count > 0) Clear(); return; }
            EnsureCanvas();

            foreach (var p in session.Players)
            {
                if (p == null) continue;
                // Our own ship has the real HUD; the coordinator has no ship at all.
                if (p.IsLocal || p.IsCoordinator || !p.Connected) { Hide(p.Slot); continue; }
                if (!ShipSync.ShipsBySlot.TryGetValue(p.Slot, out var ship) || ship == null || ship.IsDead)
                { Hide(p.Slot); continue; }

                Vector3 world = ship.transform.position + Vector3.up * WorldYOffset;
                Vector3 screen = cam.WorldToScreenPoint(world);
                if (screen.z <= 0f || screen.x < -50f || screen.y < -50f
                    || screen.x > Screen.width + 50f || screen.y > Screen.height + 50f)
                { Hide(p.Slot); continue; } // off screen: nothing to annotate, nothing revealed

                var bars = Get(p.Slot);
                bars.Root.SetActive(true);
                bars.Root.transform.position = screen;
                SetFill(bars.HealthFill, Fraction(ship, health: true));
                SetFill(bars.FuelFill, Fraction(ship, health: false));
            }
        }

        /// <summary>Fill fraction 0..1 — the whole point of the fixed-size design: upgrades change
        /// how full the bar is, never how big it is.</summary>
        private float Fraction(Ship ship, bool health)
        {
            try
            {
                var unit = ship.GetComponentInParent<Unit>() ?? ship.GetComponent<Unit>();
                if (unit == null) return 0f;
                ResourceTank tank;
                if (health)
                {
                    var dr = unit.GetComponent<DamagableResource>();
                    tank = dr != null ? dr.Tank : null;
                }
                else
                {
                    tank = FuelTank(unit);
                }
                if (tank == null || tank.Capacity <= 0f) return 0f;
                return Mathf.Clamp01(tank.Value / tank.Capacity);
            }
            catch { return 0f; }
        }

        /// <summary>Fuel is just another resource tank; find it the way the game itself does
        /// (by name through the registry) and cache it.</summary>
        private ResourceTank FuelTank(Unit unit)
        {
            if (!_fuelLookupDone)
            {
                _fuelLookupDone = true;
                try
                {
                    var registry = ServiceLocator.Get<ResourceRegistry>();
                    var all = registry != null
                        ? Traverse.Create(registry).Property("AllItems").GetValue() as IEnumerable<Resource>
                        : null;
                    _fuelResource = all?.FirstOrDefault(r => r != null && r.name != null
                        && r.name.IndexOf("Fuel", System.StringComparison.OrdinalIgnoreCase) >= 0);
                    if (_fuelResource != null) Plugin.Log.LogInfo($"[Bars] fuel resource = {_fuelResource.name}");
                }
                catch { }
            }
            if (_fuelResource == null) return null;
            try { return unit.HasTank(_fuelResource) ? unit.GetTank(_fuelResource) : null; }
            catch { return null; }
        }

        private static void SetFill(RectTransform fill, float fraction)
        {
            if (fill == null) return;
            fill.sizeDelta = new Vector2(BarWidth * Mathf.Clamp01(fraction), BarHeight);
        }

        // ---------------------------------------------------------------- widget plumbing

        private void EnsureCanvas()
        {
            if (_canvasGo != null) return;
            _canvasGo = new GameObject("PunkMV_ShipStatusBars");
            DontDestroyOnLoad(_canvasGo);
            _canvas = _canvasGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 90; // under toasts, over the world
        }

        private Bars Get(byte slot)
        {
            if (_bars.TryGetValue(slot, out var existing)) return existing;
            var root = new GameObject($"ShipBars_{slot}", typeof(RectTransform));
            root.transform.SetParent(_canvasGo.transform, worldPositionStays: false);

            var bars = new Bars { Root = root };
            bars.HealthFill = MakeBar(root.transform, 0f, HealthColor);
            bars.FuelFill = MakeBar(root.transform, -(BarHeight + BarGap), FuelColor);
            _bars[slot] = bars;
            return bars;
        }

        /// <summary>One bar = a dark backing at full width plus a colored fill anchored left, so a
        /// partial value visibly drains rather than shrinking a floating block.</summary>
        private static RectTransform MakeBar(Transform parent, float y, Color color)
        {
            var back = new GameObject("Back", typeof(RectTransform), typeof(UnityEngine.UI.Image));
            back.transform.SetParent(parent, worldPositionStays: false);
            var backRt = (RectTransform)back.transform;
            backRt.sizeDelta = new Vector2(BarWidth, BarHeight);
            backRt.anchoredPosition = new Vector2(0f, y);
            back.GetComponent<UnityEngine.UI.Image>().color = BackColor;

            var fill = new GameObject("Fill", typeof(RectTransform), typeof(UnityEngine.UI.Image));
            fill.transform.SetParent(back.transform, worldPositionStays: false);
            var fillRt = (RectTransform)fill.transform;
            fillRt.anchorMin = new Vector2(0f, 0.5f);
            fillRt.anchorMax = new Vector2(0f, 0.5f);
            fillRt.pivot = new Vector2(0f, 0.5f);
            fillRt.anchoredPosition = new Vector2(-BarWidth * 0.5f, 0f);
            fillRt.sizeDelta = new Vector2(BarWidth, BarHeight);
            fill.GetComponent<UnityEngine.UI.Image>().color = color;
            return fillRt;
        }

        private void Hide(byte slot)
        {
            if (_bars.TryGetValue(slot, out var bars) && bars.Root != null && bars.Root.activeSelf)
                bars.Root.SetActive(false);
        }

        private void Clear()
        {
            foreach (var kv in _bars) if (kv.Value?.Root != null) Destroy(kv.Value.Root);
            _bars.Clear();
        }
    }
}
