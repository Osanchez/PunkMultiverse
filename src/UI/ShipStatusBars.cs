using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using PunkMultiverse.Core;
using PunkMultiverse.Sync;
using UnityEngine;
using UnityEngine.UI;

namespace PunkMultiverse.UI
{
    /// <summary>
    /// A health/fuel readout above every OTHER player's ship, built out of the game's OWN enemy
    /// healthbar so a player ship advertises itself in exactly the language every hostile on the
    /// map already does — segmented units, same shader, same pop animation — stacked health over
    /// fuel, fuel tinted blue, and scaled down so it reads as an annotation rather than a panel.
    ///
    /// This replaces a hand-drawn two-rectangle widget. That version was chosen because
    /// <c>ResourceBar</c> draws one segment per unit of CAPACITY, so an upgraded ship grows a
    /// physically longer (eventually multi-row) bar instead of a fuller one — a real trade, and
    /// Omar's call (2026-07-27) was that matching the game beats the fixed-size behaviour. The bar
    /// therefore GROWS with upgrades; <see cref="MaxUnitsPerRow"/> wraps it instead of letting it
    /// run off across the screen.
    ///
    /// Parented to the vanilla <c>HealthbarManager</c>'s transform and positioned in world space
    /// the way <c>HealthbarWidget.UpdateTransform</c> does, so it sits in the same layer, at the
    /// same depth, under the same camera as the enemy bars — which is also what stops it punching
    /// through the map screen. Belt and braces: it hides outright whenever the ship menu is open,
    /// because a status bar drawn over a full-screen map was the reported symptom.
    ///
    /// It reveals nothing you cannot already see: bars are drawn only for ships on screen, which is
    /// why this coexists with Battle Royale hiding player positions everywhere else.
    /// </summary>
    internal sealed class ShipStatusBars : MonoBehaviour
    {
        private sealed class Bars
        {
            internal GameObject Root;
            internal ResourceBar Health;
            internal ResourceBar Fuel;
            internal TMPro.TextMeshProUGUI Name;
            internal Ship Ship;
        }

        private readonly Dictionary<byte, Bars> _bars = new Dictionary<byte, Bars>();
        private Resource _fuelResource;
        private bool _fuelLookupDone;

        /// <summary>Small enough to read as part of the world next to a grunt's bar, which is drawn
        /// at full size — a player ship is not more important than the fight around it.</summary>
        private const float BarScale = 0.6f;
        // 2.2 floated the stack a full ship-height clear of the hull (Omar, 2026-07-29: "the
        // health and fuel bars are a little too high") — 1.4 sits it just above the sprite.
        private const float WorldYOffset = 1.4f;
        /// <summary>Segments per row before the bar wraps. A late-game hull is worth far more units
        /// than any enemy on the map, and an unwrapped bar would be wider than the ship is tall.</summary>
        private const int MaxUnitsPerRow = 16;

        // Blue fuel: the vanilla bar takes its colour from the Resource asset, which is not ours to
        // edit, so the instanced row material is re-tinted after the game has set it.
        private static readonly Color FuelFull = new Color(0.30f, 0.62f, 1f, 1f);
        private static readonly Color FuelEmpty = new Color(0.08f, 0.16f, 0.32f, 1f);

        private void LateUpdate()
        {
            var session = NetSession.Instance;
            if (session == null || session.State != SessionState.InGame
                || !NetConfig.ShipStatusBars.Value || ShipMenuOpen())
            {
                if (_bars.Count > 0) Clear();
                return;
            }

            var cam = Camera.main;
            if (cam == null) { if (_bars.Count > 0) Clear(); return; }
            if (!EnsurePrefabs()) return;

            foreach (var p in session.Players)
            {
                if (p == null) continue;
                // Our own ship has the real HUD; the coordinator has no ship at all.
                if (p.IsLocal || p.IsCoordinator || !p.Connected) { Hide(p.Slot); continue; }
                if (!ShipSync.ShipsBySlot.TryGetValue(p.Slot, out var ship) || ship == null || ship.IsDead)
                { Hide(p.Slot); continue; }

                Vector3 world = ship.transform.position + Vector3.up * WorldYOffset;
                Vector3 viewport = cam.WorldToViewportPoint(world);
                if (viewport.z <= 0f || viewport.x < -0.1f || viewport.x > 1.1f
                    || viewport.y < -0.1f || viewport.y > 1.1f)
                { Hide(p.Slot); continue; } // off screen: nothing to annotate, nothing revealed

                var bars = Get(p.Slot, ship);
                if (bars == null) { Hide(p.Slot); continue; }
                bars.Root.SetActive(true);
                bars.Root.transform.position = world;
                Refresh(bars);
            }
        }

        /// <summary>True while this player has the ship menu (map, shop, inventory) open. The bars
        /// live on the world canvas the enemy bars use, and a full-screen menu is drawn over that —
        /// but "drawn over" depends on canvas ordering we do not own, so this makes it explicit.</summary>
        private static System.Reflection.FieldInfo _shipMenuIsOpen;

        private static bool ShipMenuOpen()
        {
            try
            {
                var toggler = ServiceLocator.Get<ShipMenuToggler>();
                if (toggler == null) return false;
                if (_shipMenuIsOpen == null)
                    _shipMenuIsOpen = AccessTools.Field(typeof(ShipMenuToggler), "isOpen");
                return _shipMenuIsOpen != null && (bool)_shipMenuIsOpen.GetValue(toggler);
            }
            catch { return false; }
        }

        // ---------------------------------------------------------------- vanilla prefabs

        private Transform _barParent;      // HealthbarManager's transform — the enemy-bar layer
        private ResourceBar _resourceBarPrefab;
        private bool _prefabLookupFailed;

        /// <summary>Borrow the game's own bar prefab. It is a private field two levels down
        /// (HealthbarManager -> HealthbarWidget prefab -> ResourceBar prefab); there is no public
        /// accessor, and building a lookalike is precisely what this rewrite exists to stop.</summary>
        private bool EnsurePrefabs()
        {
            if (_resourceBarPrefab != null && _barParent != null) return true;
            if (_prefabLookupFailed) return false;
            try
            {
                var manager = ServiceLocator.Get<HealthbarManager>();
                if (manager == null) return false; // not spawned yet — try again next frame
                _barParent = manager.transform;
                var widgetPrefab = Traverse.Create(manager).Field("healthbarWidgetPrefab")
                    .GetValue() as HealthbarWidget;
                if (widgetPrefab != null)
                    _resourceBarPrefab = Traverse.Create(widgetPrefab).Field("resourceBarPrefab")
                        .GetValue() as ResourceBar;
                if (_resourceBarPrefab == null)
                {
                    _prefabLookupFailed = true;
                    Plugin.Log.LogWarning("[Bars] vanilla ResourceBar prefab not found — " +
                        "remote ship health/fuel bars are disabled for this session");
                    return false;
                }
                Plugin.Log.LogInfo("[Bars] using the vanilla enemy healthbar prefab for remote ships");
            }
            catch (System.Exception e)
            {
                _prefabLookupFailed = true;
                Plugin.Log.LogWarning($"[Bars] healthbar prefab lookup failed: {e.Message}");
                return false;
            }
            return true;
        }

        // ---------------------------------------------------------------- per-ship widgets

        private Bars Get(byte slot, Ship ship)
        {
            if (_bars.TryGetValue(slot, out var existing))
            {
                if (existing.Ship == ship) return existing;
                Destroy(existing);          // resurrected into a new ship object — rebuild
                _bars.Remove(slot);
            }

            var healthTank = HealthTank(ship);
            if (healthTank == null) return null;
            var fuelTank = FuelTank(ship);

            var root = new GameObject($"PunkMV_ShipBars_{slot}", typeof(RectTransform));
            root.transform.SetParent(_barParent, worldPositionStays: false);
            root.transform.localScale = Vector3.one * BarScale;

            var bars = new Bars { Root = root, Ship = ship };
            float y = 0f;
            bars.Health = MakeBar(root.transform, healthTank, ref y);
            if (fuelTank != null) bars.Fuel = MakeBar(root.transform, fuelTank, ref y);
            bars.Name = MakeNameLabel(root.transform, slot, bars.Health);
            _bars[slot] = bars;
            return bars;
        }

        /// <summary>The player's NAME above the stack, tinted their ship colour — the same colour
        /// the hull flies in, so the label and the ship identify each other (Omar, 2026-07-29:
        /// "there is no player name above the players ship"). Sized RELATIVE to the vanilla bar's
        /// own rect (auto-fit inside 1.6 bar-heights) rather than in absolute canvas units, because
        /// the enemy-bar canvas's scale is vanilla's business and may change under us.</summary>
        private TMPro.TextMeshProUGUI MakeNameLabel(Transform parent, byte slot, ResourceBar reference)
        {
            try
            {
                var session = NetSession.Instance;
                var player = session != null && slot < session.Players.Count ? session.Players[slot] : null;
                string name = !string.IsNullOrEmpty(player?.Name) ? player.Name : $"P{slot + 1}";

                float rowH = reference != null ? reference.RectTransform.sizeDelta.y : 0.5f;
                var go = new GameObject("Name", typeof(RectTransform));
                go.transform.SetParent(parent, worldPositionStays: false);
                var rect = go.GetComponent<RectTransform>();
                rect.sizeDelta = new Vector2(rowH * 24f, rowH * 1.6f); // wide, centered on the stack
                rect.anchoredPosition = new Vector2(0f, rowH * 1.5f);  // one row-and-a-half above

                var text = go.AddComponent<TMPro.TextMeshProUGUI>();
                text.text = name.ToUpperInvariant();
                text.alignment = TMPro.TextAlignmentOptions.Bottom;
                text.enableAutoSizing = true;             // fit the rect, whatever the canvas scale
                text.fontSizeMin = 0f;
                text.fontSizeMax = 500f;
                text.fontStyle = TMPro.FontStyles.Bold;
                text.color = PlayerColors.Get(player?.ColorIndex ?? slot);
                text.outlineWidth = 0.2f;                 // readable over bright terrain
                text.outlineColor = new Color32(0, 0, 0, 220);
                return text;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"[Bars] name label failed: {e.Message} — bars only");
                return null;
            }
        }

        /// <summary>One vanilla bar, stacked under the previous one — the same layout
        /// <c>HealthbarWidget.GenerateResourceBars</c> uses for an enemy's shield-over-health.</summary>
        private ResourceBar MakeBar(Transform parent, ResourceTank tank, ref float y)
        {
            var bar = Instantiate(_resourceBarPrefab, parent);
            bar.MaxResourcePerRow = MaxUnitsPerRow;
            bar.Assign(tank);
            bar.CheckCapacityChanged();
            bar.RectTransform.anchoredPosition =
                new Vector2(bar.RectTransform.anchoredPosition.x, -y);
            y += bar.RectTransform.sizeDelta.y * bar.RectTransform.localScale.y;
            return bar;
        }

        /// <summary>Re-check capacity (upgrades change it mid-run) and hold the fuel tint. Rows are
        /// re-instantiated by the vanilla bar whenever capacity changes, and each new row's material
        /// starts from the Resource asset's colours, so the tint has to be reapplied rather than set
        /// once.</summary>
        private void Refresh(Bars bars)
        {
            if (bars.Health != null) bars.Health.CheckCapacityChanged();
            if (bars.Fuel == null) return;
            bars.Fuel.CheckCapacityChanged();
            foreach (var image in bars.Fuel.GetComponentsInChildren<RawImage>(true))
            {
                var material = image != null ? image.material : null;
                if (material == null || !material.HasProperty("_ResourceColor")) continue;
                if (material.GetColor("_ResourceColor") != FuelFull)
                {
                    material.SetColor("_ResourceColor", FuelFull);
                    material.SetColor("_ResourceColorEmpty", FuelEmpty);
                }
            }
        }

        // ---------------------------------------------------------------- tanks

        private static ResourceTank HealthTank(Ship ship)
        {
            try
            {
                var unit = ship.GetComponentInParent<Unit>() ?? ship.GetComponent<Unit>();
                var dr = unit != null ? unit.GetComponent<DamagableResource>() : null;
                return dr != null ? dr.Tank : null;
            }
            catch { return null; }
        }

        /// <summary>Fuel is just another resource tank; find it the way the game itself does
        /// (by name through the registry) and cache it.</summary>
        private ResourceTank FuelTank(Ship ship)
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
            try
            {
                var unit = ship.GetComponentInParent<Unit>() ?? ship.GetComponent<Unit>();
                if (unit == null) return null;
                return unit.HasTank(_fuelResource) ? unit.GetTank(_fuelResource) : null;
            }
            catch { return null; }
        }

        // ---------------------------------------------------------------- lifecycle

        private void Hide(byte slot)
        {
            if (_bars.TryGetValue(slot, out var bars) && bars.Root != null && bars.Root.activeSelf)
                bars.Root.SetActive(false);
        }

        private void OnDestroy() => Clear();

        private void Clear()
        {
            foreach (var kv in _bars) Destroy(kv.Value);
            _bars.Clear();
        }

        /// <summary>Unassign before destroying: <c>ResourceBar.Assign</c> subscribes to the tank's
        /// events, and a ship's tank outlives its bar.</summary>
        private void Destroy(Bars bars)
        {
            if (bars == null) return;
            try
            {
                bars.Health?.Unassign();
                bars.Fuel?.Unassign();
            }
            catch { }
            if (bars.Root != null) Object.Destroy(bars.Root);
        }
    }
}
