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

            // Center it under the SINGLE/CO-OP pair (the selector has no layout group).
            var rt = (RectTransform)clone.transform;
            var coopRt = (RectTransform)coop;
            float centerX = single != null
                ? (((RectTransform)single).anchoredPosition.x + coopRt.anchoredPosition.x) / 2f
                : coopRt.anchoredPosition.x;
            rt.anchoredPosition = new Vector2(centerX, coopRt.anchoredPosition.y - coopRt.rect.height * 1.15f);

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
