using System;
using System.Runtime.InteropServices;
using HarmonyLib;
using PunkMultiverse.Core;
using TMPro;
using UnityEngine;

namespace PunkMultiverse.UI
{
    /// <summary>
    /// Two vanilla-QoL video features the game lacks (Omar, 2026-07-24):
    ///
    /// 1. FPS LIMIT — a new row on the game's own VIDEO options tab (cloned from the vsync row
    ///    for pixel-perfect fit, driven by a mod component). Values: 60..monitor max + MAX;
    ///    default MAX (= the monitor's refresh rate). Applied via Application.targetFrameRate,
    ///    persisted in the mod config. NOTE Unity semantics: with VSYNC ON the display sync
    ///    governs and the cap is inert — the row sits right below the vsync row, where that
    ///    relationship is visible. ClockGuard composes safely: it captures whatever cap is
    ///    active when it flips for an unfocused net session and restores it after.
    ///
    /// 2. RESIZABLE WINDOW — in Windowed screen mode, adds the Win32 resize frame + maximize
    ///    box to the game window (the build ships with a fixed-border window). Unity handles
    ///    WM_SIZE natively, so rendering and canvases follow the new size. Reapplied whenever
    ///    video settings are applied (screen-mode changes rebuild the window style).
    /// </summary>
    internal static class VideoTweaks
    {
        // ------------------------------------------------------------------ fps limit
        internal static int MonitorMaxHz()
        {
            try
            {
                double hz = Screen.currentResolution.refreshRateRatio.value;
                if (hz > 30) return (int)Math.Round(hz);
            }
            catch { }
            return 240;
        }

        /// <summary>0 = MAX (monitor refresh). Anything else = explicit cap.</summary>
        internal static void ApplyFpsLimit()
        {
            int cap = NetConfig.FpsLimit.Value;
            int target = cap <= 0 ? MonitorMaxHz() : Mathf.Max(60, cap);
            Application.targetFrameRate = target;
            Plugin.Log.LogInfo($"[Video] fps limit -> {(cap <= 0 ? $"MAX ({target})" : target.ToString())}"
                + (QualitySettings.vSyncCount > 0 ? " (vsync ON governs while enabled)" : ""));
        }

        // ------------------------------------------------------------------ resizable window
        [DllImport("user32.dll")] private static extern IntPtr GetActiveWindow();
        [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        private const int GWL_STYLE = -16;
        private const int WS_THICKFRAME = 0x00040000;
        private const int WS_MAXIMIZEBOX = 0x00010000;
        private const int WS_MINIMIZEBOX = 0x00020000;
        private const uint SWP_FRAMECHANGED = 0x0020;
        private const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004;

        private static IntPtr _hwnd;

        /// <summary>Capture the game window handle — call once early while the window is active.</summary>
        internal static void CaptureWindowHandle()
        {
            var h = GetActiveWindow();
            if (h != IntPtr.Zero) _hwnd = h;
        }

        internal static void ApplyResizableWindow()
        {
            if (!NetConfig.ResizableWindow.Value) return;
            if (Screen.fullScreenMode != FullScreenMode.Windowed) return; // borderless keeps its style
            if (_hwnd == IntPtr.Zero) CaptureWindowHandle();
            if (_hwnd == IntPtr.Zero) return;
            try
            {
                int style = GetWindowLong(_hwnd, GWL_STYLE);
                int wanted = style | WS_THICKFRAME | WS_MAXIMIZEBOX | WS_MINIMIZEBOX;
                if (wanted == style) return; // already resizable
                SetWindowLong(_hwnd, GWL_STYLE, wanted);
                SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
                    SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER);
                Plugin.Log.LogInfo("[Video] window is now resizable (drag edges / maximize)");
            }
            catch (Exception e) { Plugin.Log.LogWarning($"[Video] resizable-window patch failed: {e.Message}"); }
        }

        /// <summary>Both tweaks re-asserted after the game applies video settings (screen-mode
        /// switches recreate the window style; vsync flips change cap semantics).</summary>
        [HarmonyPatch(typeof(SettingsManager), nameof(SettingsManager.Apply),
            typeof(OptionsData.VideoOptions))]
        internal static class ReapplyAfterVideoSettings
        {
            private static void Postfix()
            {
                if (NetConfig.IsCoordinator) return; // server: ServerFrameRateCap owns the cap
                ApplyFpsLimit();
                // The window style is rebuilt a beat after the mode switch — reapply next frame.
                NetSession.Instance?.StartCoroutine(ReapplyNextFrame());
            }

            private static System.Collections.IEnumerator ReapplyNextFrame()
            {
                yield return null;
                yield return null;
                ApplyResizableWindow();
            }
        }

        // ------------------------------------------------------------------ options-row injection

        /// <summary>The FPS LIMIT row: steps through presets ≤ the monitor's max, plus MAX.
        /// Subclasses the game's own item base so tab navigation/selection animate natively.</summary>
        internal sealed class FpsLimitMenuItem : OptionsMenuitemBase
        {
            private TMP_Text _title;
            private int[] _values;   // 0 = MAX sentinel, else explicit caps
            private int _index;

            internal void Setup(TMP_Text title)
            {
                _title = title;
                int max = MonitorMaxHz();
                var presets = new System.Collections.Generic.List<int>();
                foreach (int v in new[] { 60, 72, 90, 120, 144, 165, 240, 360 })
                    if (v < max) presets.Add(v);
                presets.Add(0); // MAX
                _values = presets.ToArray();
                int saved = NetConfig.FpsLimit.Value;
                _index = Array.IndexOf(_values, saved);
                if (_index < 0) _index = _values.Length - 1; // unknown/0 -> MAX
                RefreshLabel();
            }

            public override void HandleRight() => Step(+1);
            public override void HandleLeft() => Step(-1);

            internal void Step(int dir)
            {
                _index = (_index + dir + _values.Length) % _values.Length;
                NetConfig.FpsLimit.Value = _values[_index]; // persists via BepInEx
                ApplyFpsLimit();
                RefreshLabel();
            }

            private void RefreshLabel()
            {
                if (_title == null) return;
                int v = _values[_index];
                _title.text = v <= 0 ? $"FPS LIMIT: MAX ({MonitorMaxHz()})" : $"FPS LIMIT: {v}";
            }
        }

        /// <summary>Clone the vsync row into an FPS LIMIT row when the video tab opens.</summary>
        [HarmonyPatch(typeof(VideoOptionsTab), "OnOpened")]
        internal static class InjectFpsRow
        {
            private static readonly AccessTools.FieldRef<VideoOptionsTab, OptionsMenuItemButtons> VsyncRowF =
                AccessTools.FieldRefAccess<VideoOptionsTab, OptionsMenuItemButtons>("vSyncButtons");
            private static readonly AccessTools.FieldRef<OptionsTab, OptionsMenuitemBase[]> ItemsF =
                AccessTools.FieldRefAccess<OptionsTab, OptionsMenuitemBase[]>("items");

            private static void Postfix(VideoOptionsTab __instance)
            {
                try
                {
                    if (NetConfig.IsCoordinator) return; // headless: no options UI to decorate
                    if (__instance.transform.Find("PunkMV_FpsLimitRow") != null) return; // once
                    var vsyncRow = VsyncRowF(__instance);
                    if (vsyncRow == null) return;

                    var clone = UnityEngine.Object.Instantiate(vsyncRow.gameObject, vsyncRow.transform.parent);
                    clone.name = "PunkMV_FpsLimitRow";
                    clone.transform.SetSiblingIndex(vsyncRow.transform.GetSiblingIndex() + 1);

                    // Strip the cloned buttons widget; drive the row with our stepper instead.
                    // The base class carries serialized refs (animator, gamepadHints) that tab
                    // navigation calls into — transplant them to our component before the destroy
                    // or SetSelected() NREs the first time the row is focused.
                    var buttonsWidget = clone.GetComponent<OptionsMenuItemButtons>();
                    var punkButtons = clone.GetComponentsInChildren<PunkButton>(true);
                    var animF = AccessTools.FieldRefAccess<OptionsMenuitemBase, Animator>("animator");
                    var hintsF = AccessTools.FieldRefAccess<OptionsMenuitemBase, GameObject>("gamepadHints");
                    Animator rowAnim = buttonsWidget != null ? animF(buttonsWidget) : null;
                    GameObject rowHints = buttonsWidget != null ? hintsF(buttonsWidget) : null;
                    if (buttonsWidget != null) UnityEngine.Object.DestroyImmediate(buttonsWidget);
                    var item = clone.AddComponent<FpsLimitMenuItem>();
                    animF(item) = rowAnim;
                    hintsF(item) = rowHints;

                    // Row title doubles as the value display; the two cloned buttons become < >.
                    var texts = clone.GetComponentsInChildren<TMP_Text>(true);
                    TMP_Text title = texts.Length > 0 ? texts[0] : null;
                    item.Setup(title);
                    for (int i = 0; i < punkButtons.Length && i < 2; i++)
                    {
                        var label = punkButtons[i].GetComponentInChildren<TMP_Text>(true);
                        if (label != null) label.text = i == 0 ? "<" : ">";
                        int dir = i == 0 ? -1 : +1;
                        punkButtons[i].OnClick.RemoveAllListeners();
                        punkButtons[i].OnClick.AddListener(() => item.Step(dir));
                        punkButtons[i].SetToggled(false);
                    }

                    // Register with the tab's navigation so keyboard/gamepad reach the row.
                    ref var items = ref ItemsF(__instance);
                    var extended = new OptionsMenuitemBase[items.Length + 1];
                    Array.Copy(items, extended, items.Length);
                    extended[items.Length] = item;
                    items = extended;
                    Plugin.Log.LogInfo("[Video] FPS LIMIT row injected into the video options tab");
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[Video] FPS LIMIT row injection failed: {e.Message}"); }
            }
        }
    }
}
