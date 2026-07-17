using System;
using System.Linq;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace PunkMultiverse.Core
{
    /// <summary>
    /// UI-driving dev commands (devcmd.txt), used by the automated harness to inspect,
    /// navigate, and screenshot menu screens — MKB/controller UI work is verified through
    /// these instead of a human at the keyboard:
    ///     shot [name]              screenshot -> plugin folder/shots/name.png (path in devout)
    ///     uidump [filter]          every active Selectable: path, screen rect, nav links, label
    ///     uitree &lt;name&gt; [depth]    hierarchy dump: rects, sprites, colors, fonts, text
    ///     click &lt;token&gt;            submit-click a Selectable by name/label substring
    ///     nav up|down|left|right|submit|cancel    drive EventSystem like a gamepad
    ///     sel                      current EventSystem selection
    /// All output goes through DevTools.Out via the returned string list convention: the
    /// caller (DevTools.Execute) forwards to devout.txt + LogOutput.log.
    /// </summary>
    internal static class DevUi
    {
        internal static void Shot(string[] parts, Action<string> outFn)
        {
            string name = parts.Length >= 2 ? parts[1] : $"shot_{DateTime.Now:HHmmss}";
            if (!name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) name += ".png";
            string dir = System.IO.Path.Combine(ModFolder.Dir, "shots");
            System.IO.Directory.CreateDirectory(dir);
            string path = System.IO.Path.Combine(dir, name);
            ScreenCapture.CaptureScreenshot(path);
            outFn($"shot: capturing -> {path} ({Screen.width}x{Screen.height})");
        }

        internal static void Dump(string[] parts, Action<string> outFn)
        {
            string filter = parts.Length >= 2 ? parts[1].ToLowerInvariant() : null;
            int listed = 0;
            foreach (var s in UnityEngine.Object.FindObjectsByType<Selectable>(
                         FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                     .OrderBy(s => Path(s.transform)))
            {
                string path = Path(s.transform);
                string label = LabelOf(s.gameObject);
                if (filter != null && path.ToLowerInvariant().IndexOf(filter, StringComparison.Ordinal) < 0
                    && label.ToLowerInvariant().IndexOf(filter, StringComparison.Ordinal) < 0) continue;
                listed++;
                var r = ScreenRect(s.transform as RectTransform);
                var nav = s.navigation;
                string navStr = nav.mode == Navigation.Mode.Explicit
                    ? $"nav=Explicit u={Name(nav.selectOnUp)} d={Name(nav.selectOnDown)} l={Name(nav.selectOnLeft)} r={Name(nav.selectOnRight)}"
                    : $"nav={nav.mode}";
                outFn($"ui {s.GetType().Name} '{path}' label='{label}' rect=({r.x:0},{r.y:0} {r.width:0}x{r.height:0}) " +
                      $"interactable={s.IsInteractable()} {navStr}");
            }
            var es = EventSystem.current;
            outFn($"uidump: {listed} selectables, selected={(es != null && es.currentSelectedGameObject != null ? Path(es.currentSelectedGameObject.transform) : "none")}");
        }

        internal static void Tree(string[] parts, Action<string> outFn)
        {
            if (parts.Length < 2) { outFn("uitree: usage uitree <rootName…> [maxDepth]"); return; }
            // Object names contain spaces ("BasicButton Settings"): join the args; a trailing
            // integer is the depth.
            int maxDepth = 6, nameEnd = parts.Length;
            if (parts.Length >= 3 && int.TryParse(parts[parts.Length - 1], out int d)) { maxDepth = d; nameEnd--; }
            string rootName = string.Join(" ", parts.Skip(1).Take(nameEnd - 1));
            var root = FindAnywhere(rootName);
            if (root == null) { outFn($"uitree: no object named '{rootName}'"); return; }
            int lines = 0;
            DumpNode(root.transform, 0, maxDepth, ref lines, outFn);
            outFn($"uitree: done ({lines} nodes) root={Path(root.transform)}");
        }

        private static void DumpNode(Transform t, int depth, int maxDepth, ref int lines, Action<string> outFn)
        {
            if (depth > maxDepth || lines >= 400) return;
            lines++;
            var sb = new StringBuilder();
            sb.Append(new string(' ', depth * 2)).Append(t.name);
            if (!t.gameObject.activeSelf) sb.Append(" [OFF]");
            var rt = t as RectTransform;
            if (rt != null)
                sb.Append($" rt=({rt.anchoredPosition.x:0},{rt.anchoredPosition.y:0} {rt.rect.width:0}x{rt.rect.height:0}" +
                          $" a={rt.anchorMin.x:0.##},{rt.anchorMin.y:0.##}-{rt.anchorMax.x:0.##},{rt.anchorMax.y:0.##})");
            var img = t.GetComponent<Image>();
            if (img != null)
                sb.Append($" img[{(img.sprite != null ? img.sprite.name : "null")} {ColorHex(img.color)} {img.type}]");
            var tmp = t.GetComponent<TMP_Text>();
            if (tmp != null)
                sb.Append($" tmp['{Trunc(tmp.text, 40)}' {tmp.fontSize:0} {(tmp.font != null ? tmp.font.name : "?")} {ColorHex(tmp.color)} {tmp.alignment}]");
            var others = t.GetComponents<Component>()
                .Where(c => c != null && !(c is Transform) && !(c is Image) && !(c is TMP_Text) && !(c is CanvasRenderer))
                .Select(c => c.GetType().Name);
            var otherStr = string.Join(",", others);
            if (otherStr.Length > 0) sb.Append(" {").Append(otherStr).Append('}');
            outFn(sb.ToString());
            for (int i = 0; i < t.childCount; i++)
                DumpNode(t.GetChild(i), depth + 1, maxDepth, ref lines, outFn);
        }

        internal static void Click(string[] parts, Action<string> outFn)
        {
            if (parts.Length < 2) { outFn("click: usage click <name-or-label-substring>"); return; }
            string token = string.Join(" ", parts.Skip(1)).ToLowerInvariant();
            var candidates = UnityEngine.Object.FindObjectsByType<Selectable>(
                    FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                .Where(s => s.IsInteractable())
                .Where(s => NameChainContains(s.transform, token)
                            || LabelOf(s.gameObject).ToLowerInvariant().Contains(token))
                .OrderBy(s => s.gameObject.name.Length) // shortest name = most exact match
                .ToList();
            if (candidates.Count == 0) { outFn($"click: nothing matches '{token}'"); return; }
            var target = candidates[0];
            var es = EventSystem.current;
            if (es != null) es.SetSelectedGameObject(target.gameObject);
            // Submit only — for a Button it invokes onClick; firing pointerClick too would
            // double-invoke every handler (observed: LEAVE+BACK from one click).
            if (!ExecuteEvents.Execute(target.gameObject, new BaseEventData(es), ExecuteEvents.submitHandler))
                ExecuteEvents.Execute(target.gameObject, new PointerEventData(es), ExecuteEvents.pointerClickHandler);
            outFn($"click: '{Path(target.transform)}' (of {candidates.Count} match(es))");
        }

        internal static void Nav(string[] parts, Action<string> outFn)
        {
            if (parts.Length < 2) { outFn("nav: usage nav up|down|left|right|submit|cancel"); return; }
            var es = EventSystem.current;
            if (es == null) { outFn("nav: no EventSystem"); return; }
            var sel = es.currentSelectedGameObject;
            string dir = parts[1].ToLowerInvariant();
            if (dir == "submit" || dir == "cancel")
            {
                if (sel == null) { outFn("nav: nothing selected"); return; }
                if (dir == "submit")
                    ExecuteEvents.Execute(sel, new BaseEventData(es), ExecuteEvents.submitHandler);
                else
                    ExecuteEvents.Execute(sel, new BaseEventData(es), ExecuteEvents.cancelHandler);
                outFn($"nav {dir}: on '{Path(sel.transform)}'");
                return;
            }
            MoveDirection md;
            switch (dir)
            {
                case "up": md = MoveDirection.Up; break;
                case "down": md = MoveDirection.Down; break;
                case "left": md = MoveDirection.Left; break;
                case "right": md = MoveDirection.Right; break;
                default: outFn($"nav: unknown direction '{dir}'"); return;
            }
            if (sel == null) { outFn("nav: nothing selected (use click or wait for auto-focus)"); return; }
            var axis = new AxisEventData(es) { moveDir = md };
            ExecuteEvents.Execute(sel, axis, ExecuteEvents.moveHandler);
            var now = es.currentSelectedGameObject;
            outFn($"nav {dir}: '{Name(sel)}' -> '{(now != null ? Path(now.transform) : "none")}'");
        }

        internal static void Sel(Action<string> outFn)
        {
            var es = EventSystem.current;
            var sel = es != null ? es.currentSelectedGameObject : null;
            if (sel == null) { outFn("sel: none"); return; }
            var r = ScreenRect(sel.transform as RectTransform);
            outFn($"sel: '{Path(sel.transform)}' label='{LabelOf(sel)}' rect=({r.x:0},{r.y:0} {r.width:0}x{r.height:0})");
        }

        // ---------------------------------------------------------------- helpers

        /// <summary>Match a selectable by its own name or a nearby ancestor's (buttons often live
        /// on a generic "ButtonBody" under a well-named wrapper).</summary>
        private static bool NameChainContains(Transform t, string lowerToken)
        {
            for (int i = 0; t != null && i < 3; i++, t = t.parent)
                if (t.name.ToLowerInvariant().Contains(lowerToken)) return true;
            return false;
        }

        private static GameObject FindAnywhere(string name)
        {
            // Includes inactive objects — panel dumps are usually wanted while hidden too.
            foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (t == null || !t.gameObject.scene.IsValid()) continue;
                if (string.Equals(t.name, name, StringComparison.OrdinalIgnoreCase)) return t.gameObject;
            }
            return null;
        }

        private static string LabelOf(GameObject go)
        {
            var tmp = go.GetComponentInChildren<TMP_Text>(true);
            return tmp != null ? Trunc(tmp.text, 40) : "";
        }

        private static Rect ScreenRect(RectTransform rt)
        {
            if (rt == null) return default;
            var canvas = rt.GetComponentInParent<Canvas>();
            var cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay
                ? canvas.worldCamera : null;
            var corners = new Vector3[4];
            rt.GetWorldCorners(corners);
            Vector2 min = RectTransformUtility.WorldToScreenPoint(cam, corners[0]);
            Vector2 max = RectTransformUtility.WorldToScreenPoint(cam, corners[2]);
            return new Rect(min.x, min.y, max.x - min.x, max.y - min.y);
        }

        private static string Name(Selectable s) => s != null ? s.gameObject.name : "-";
        private static string Name(GameObject go) => go != null ? go.name : "-";

        private static string ColorHex(Color c)
            => $"#{(int)(c.r * 255):x2}{(int)(c.g * 255):x2}{(int)(c.b * 255):x2}@{c.a:0.##}";

        private static string Trunc(string s, int len)
        {
            if (s == null) return "";
            s = s.Replace('\n', ' ').Replace('\r', ' ');
            return s.Length <= len ? s : s.Substring(0, len) + "…";
        }

        private static string Path(Transform t)
        {
            var s = t.name;
            while (t.parent != null) { t = t.parent; s = t.name + "/" + s; }
            return s;
        }
    }
}
