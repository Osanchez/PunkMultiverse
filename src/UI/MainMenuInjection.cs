using System.Collections;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace PunkMultiverse.UI
{
    /// <summary>
    /// Injects a "PLAY ONLINE" button into the main menu's game-mode selector by cloning the
    /// CO-OP button (Canvas/GameModeSelector/CoopButton — plain Button + ButtonSounds, verified
    /// by hierarchy dump). Scene-load driven rather than a Harmony patch — resilient to updates.
    /// </summary>
    public sealed class MainMenuInjection : MonoBehaviour
    {
        private void OnEnable() => SceneManager.sceneLoaded += OnSceneLoaded;
        private void OnDisable() => SceneManager.sceneLoaded -= OnSceneLoaded;

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            StartCoroutine(TryInject());
        }

        private IEnumerator TryInject()
        {
            yield return null; // let the menu finish Awake/Start layout

            var menu = FindFirstObjectByType<MainMenu>();
            if (menu == null) yield break;
            if (GameObject.Find("PunkMV_PlayOnline") != null) yield break;

            var selector = FindInScene("GameModeSelector");
            var coop = selector != null ? selector.Find("CoopButton") : null;
            var single = selector != null ? selector.Find("SingleButton") : null;
            if (coop == null)
            {
                Plugin.Log.LogWarning("[UI] GameModeSelector/CoopButton not found — menu layout changed? Dumping buttons:");
                DumpButtons();
                yield break;
            }

            var clone = Instantiate(coop.gameObject, coop.parent);
            clone.name = "PunkMV_PlayOnline";
            clone.transform.SetSiblingIndex(coop.GetSiblingIndex() + 1);

            foreach (var tmp in clone.GetComponentsInChildren<TMP_Text>(true))
                tmp.text = "PLAY ONLINE";
            // Strip anything that could reset the label or call menu logic; keep Button + ButtonSounds.
            foreach (var comp in clone.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (comp == null) continue;
                var tn = comp.GetType().Name;
                if (tn.Contains("Localiz") || tn.Contains("StartGame") || tn.Contains("Continue") || tn == "MainMenuButton")
                    Destroy(comp);
            }

            var button = clone.GetComponentInChildren<Button>(true);
            if (button == null)
            {
                Plugin.Log.LogWarning("[UI] cloned CoopButton has no Button component");
                Destroy(clone);
                yield break;
            }
            button.onClick = new Button.ButtonClickedEvent(); // drops vanilla persistent SelectGameMode call
            button.onClick.AddListener(() => GetComponent<LobbyScreen>()?.Show());

            Plugin.Log.LogInfo("[UI] PLAY ONLINE button injected into main menu");

            // Give whatever arranges the selector a frame to see the new sibling, then
            // re-slot the row so three buttons share it without overlap.
            yield return null;
            SetupRowLayout((RectTransform)selector, single, coop, clone.transform);
        }

        // ---------------------------------------------------------------- row layout

        // The selector row was designed for two buttons; with PLAY ONLINE it holds three and
        // the game packs them edge-to-edge. Re-slot: even widths, gaps between, side margins.
        private const float RowMarginFrac = 0.05f;
        private const float RowGapFrac = 0.03f;

        private RectTransform _row;
        private readonly RectTransform[] _rowButtons = new RectTransform[3];
        private bool _enforceRowLayout;

        private void SetupRowLayout(RectTransform row, Transform single, Transform coop, Transform online)
        {
            _row = row;
            _rowButtons[0] = single as RectTransform;
            _rowButtons[1] = coop as RectTransform;
            _rowButtons[2] = online as RectTransform;
            Plugin.Log.LogInfo("[UI] GameModeSelector components: "
                + string.Join(",", row.GetComponents<Component>().Select(c => c.GetType().Name)));

            // Narrower slots must not clip the labels (SINGLE PLAYER is the widest).
            foreach (var b in _rowButtons)
            {
                if (b == null) continue;
                foreach (var tmp in b.GetComponentsInChildren<TMP_Text>(true))
                {
                    tmp.enableAutoSizing = true;
                    tmp.fontSizeMax = tmp.fontSize;
                    tmp.fontSizeMin = tmp.fontSize * 0.4f;
                }
            }

            float width = row.rect.width;
            int margin = Mathf.RoundToInt(width * RowMarginFrac);
            var hlg = row.GetComponent<HorizontalLayoutGroup>();
            if (hlg != null)
            {
                hlg.padding.left = margin;
                hlg.padding.right = margin;
                hlg.spacing = width * RowGapFrac;
                hlg.childControlWidth = true;
                hlg.childForceExpandWidth = true;
                LayoutRebuilder.MarkLayoutForRebuild(row);
                return;
            }
            var grid = row.GetComponent<GridLayoutGroup>();
            if (grid != null)
            {
                grid.padding.left = margin;
                grid.padding.right = margin;
                grid.spacing = new Vector2(width * RowGapFrac, grid.spacing.y);
                grid.cellSize = new Vector2(SlotWidth(width, CountRowButtons()), grid.cellSize.y);
                LayoutRebuilder.MarkLayoutForRebuild(row);
                return;
            }
            // No layout group, yet something game-side rowed the clone up — keep re-asserting
            // our slots (epsilon-guarded) rather than positioning once and losing the fight.
            _enforceRowLayout = true;
        }

        private int CountRowButtons()
        {
            int n = 0;
            foreach (var b in _rowButtons)
                if (b != null) n++;
            return n;
        }

        private static float SlotWidth(float rowWidth, int count) =>
            (rowWidth - rowWidth * RowMarginFrac * 2f - rowWidth * RowGapFrac * (count - 1)) / count;

        private void LateUpdate()
        {
            if (!_enforceRowLayout) return;
            if (_row == null) { _enforceRowLayout = false; return; }
            int count = CountRowButtons();
            if (count == 0) return;
            float width = _row.rect.width;
            float slotW = SlotWidth(width, count);
            float x = _row.rect.xMin + width * RowMarginFrac;
            foreach (var b in _rowButtons)
            {
                if (b == null) continue;
                if (Mathf.Abs(b.rect.width - slotW) > 0.5f)
                    b.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, slotW);
                float pivotX = x + slotW * b.pivot.x;
                var lp = b.localPosition;
                if (Mathf.Abs(lp.x - pivotX) > 0.5f) { lp.x = pivotX; b.localPosition = lp; }
                x += slotW + width * RowGapFrac;
            }
        }

        private static Transform FindInScene(string name)
        {
            return SceneManager.GetActiveScene().GetRootGameObjects()
                .Select(root => FindRecursive(root.transform, name))
                .FirstOrDefault(t => t != null);
        }

        private static Transform FindRecursive(Transform t, string name)
        {
            if (t.name == name) return t;
            for (int i = 0; i < t.childCount; i++)
            {
                var found = FindRecursive(t.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }

        private void DumpButtons()
        {
            foreach (var b in FindObjectsByType<Button>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (!b.gameObject.scene.IsValid()) continue;
                var txt = b.GetComponentInChildren<TMP_Text>(true);
                var comps = string.Join(",", b.GetComponents<MonoBehaviour>().Select(c => c.GetType().Name));
                Plugin.Log.LogInfo($"[UI]   btn '{Path(b.transform)}' text='{txt?.text}' comps=[{comps}] active={b.gameObject.activeInHierarchy}");
            }
        }

        private static string Path(Transform t)
        {
            var s = t.name;
            while (t.parent != null) { t = t.parent; s = t.name + "/" + s; }
            return s;
        }
    }
}
