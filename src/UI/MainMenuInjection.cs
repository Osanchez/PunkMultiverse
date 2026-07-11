using System.Collections;
using System.Collections.Generic;
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

            // Vanilla-rendered widths, captured before the clone exists — these are the
            // "default sizes" the buttons must keep.
            float wSingle = single != null ? ((RectTransform)single).rect.width : 0f;
            float wCoop = ((RectTransform)coop).rect.width;

            var clone = Instantiate(coop.gameObject, coop.parent);
            clone.name = "PunkMV_PlayOnline";
            clone.transform.SetSiblingIndex(coop.GetSiblingIndex() + 1);

            TMP_FontAsset menuFont = null;
            foreach (var tmp in clone.GetComponentsInChildren<TMP_Text>(true))
            {
                tmp.text = "PLAY ONLINE";
                menuFont = tmp.font;
            }
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

            // Freeze the selector's HorizontalLayoutGroup before its next rebuild and place
            // the row ourselves — letting it re-run with a third child is what packed (and,
            // with child width control, stretched) the buttons.
            SetupRowLayout((RectTransform)selector, single, coop, (RectTransform)clone.transform, wSingle, wCoop);

            Core.UpdateCheck.Kick(this);
            CreateVersionBanner(selector, menuFont);

            Plugin.Log.LogInfo("[UI] PLAY ONLINE button injected into main menu");
        }

        // ---------------------------------------------------------------- version banner

        private TMP_Text _versionBanner;

        // Sits just above the developer-name listing at the bottom of the main menu. Plain
        // version until the GitHub check resolves; then "UP TO DATE" or an update notice.
        private void CreateVersionBanner(Transform selector, TMP_FontAsset font)
        {
            var canvas = selector.GetComponentInParent<Canvas>();
            if (canvas == null) return;
            var go = new GameObject("PunkMV_Version", typeof(RectTransform));
            go.transform.SetParent(canvas.transform, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.anchoredPosition = new Vector2(0, 64);
            rt.sizeDelta = new Vector2(1400, 28);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            if (font != null) tmp.font = font;
            tmp.fontSize = 18;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = new Color(1f, 1f, 1f, 0.45f);
            tmp.text = $"PUNK MULTIVERSE v{Plugin.Version}";
            _versionBanner = tmp;
            StartCoroutine(RefreshVersionBanner());
        }

        private IEnumerator RefreshVersionBanner()
        {
            while (_versionBanner != null && !Core.UpdateCheck.Resolved)
                yield return new WaitForSecondsRealtime(0.5f);
            if (_versionBanner == null) yield break;
            if (Core.UpdateCheck.UpdateAvailable != null)
            {
                var latest = Core.UpdateCheck.UpdateAvailable;
                if (!NetConfig.AutoUpdate.Value)
                {
                    // Manual mode: point at the releases page and stop here.
                    _versionBanner.text =
                        $"PUNK MULTIVERSE v{Plugin.Version} — UPDATE v{latest} AVAILABLE ON GITHUB";
                    _versionBanner.color = new Color(0.98f, 0.63f, 0.24f);
                    yield break;
                }

                // Auto-update: available -> downloading (live %) -> downloaded, restart to apply.
                _versionBanner.color = new Color(0.98f, 0.63f, 0.24f);
                float deadline = Time.unscaledTime + 180f;
                while (_versionBanner != null && Core.UpdateCheck.UpdateStaged == null
                       && !Core.UpdateCheck.StageFailed && Time.unscaledTime < deadline)
                {
                    _versionBanner.text = $"PUNK MULTIVERSE v{Plugin.Version} — " +
                        $"UPDATE v{latest} DOWNLOADING… {Core.UpdateCheck.DownloadProgress * 100f:0}%";
                    yield return new WaitForSecondsRealtime(0.2f);
                }
                if (_versionBanner == null) yield break;
                if (Core.UpdateCheck.UpdateStaged != null)
                {
                    _versionBanner.text =
                        $"PUNK MULTIVERSE v{Plugin.Version} — UPDATE v{Core.UpdateCheck.UpdateStaged} DOWNLOADED, RESTART GAME TO APPLY";
                    _versionBanner.color = new Color(0.31f, 0.85f, 0.47f);
                }
                else
                {
                    // Download didn't land — hand the player the manual path.
                    _versionBanner.text =
                        $"PUNK MULTIVERSE v{Plugin.Version} — UPDATE v{latest} AVAILABLE ON GITHUB (AUTO-DOWNLOAD FAILED)";
                    _versionBanner.color = new Color(1f, 0.44f, 0.44f);
                }
            }
            else
            {
                _versionBanner.text = $"PUNK MULTIVERSE v{Plugin.Version} — UP TO DATE";
            }
        }

        // ---------------------------------------------------------------- row layout

        private void SetupRowLayout(RectTransform row, Transform single, Transform coop,
            RectTransform online, float wSingle, float wCoop)
        {
            Plugin.Log.LogInfo("[UI] GameModeSelector components: "
                + string.Join(",", row.GetComponents<Component>().Select(c => c.GetType().Name)));

            // The selector is a HorizontalLayoutGroup (verified via the log line above).
            // Disable it before its next rebuild so the vanilla button sizes stay untouched.
            var hlg = row.GetComponent<HorizontalLayoutGroup>();
            if (hlg != null) hlg.enabled = false;

            var rts = new List<RectTransform>(3);
            var widths = new List<float>(3);
            if (single != null) { rts.Add((RectTransform)single); widths.Add(wSingle); }
            rts.Add((RectTransform)coop); widths.Add(wCoop);
            rts.Add(online); widths.Add(Mathf.Max(wCoop, wSingle)); // fits the PLAY ONLINE label

            float total = 0f;
            foreach (var w in widths) total += w;

            // Equal side margins and inner gaps; clamped so the row stays grouped near the
            // center on wide screens and never overlaps on narrow ones.
            float gap = Mathf.Clamp((row.rect.width - total) / (rts.Count + 1), 12f, 64f);

            float x = row.rect.center.x - (total + gap * (rts.Count - 1)) / 2f;
            for (int i = 0; i < rts.Count; i++)
            {
                rts[i].SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, widths[i]);
                var lp = rts[i].localPosition;
                lp.x = x + widths[i] * rts[i].pivot.x;
                rts[i].localPosition = lp;
                x += widths[i] + gap;
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
