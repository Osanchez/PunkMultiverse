using HarmonyLib;
using PunkMultiverse.UI;
using UnityEngine;

namespace PunkMultiverse.Core
{
    /// <summary>
    /// Defends net sessions against unfocused-window clock dilation — the 2026-07-22 root cause
    /// of "enemy jitter". With vsync on, an UNFOCUSED instance advances its whole Unity clock
    /// (unscaled + game/fixed time) a fixed 1/refresh per frame regardless of real frame
    /// duration; under load its sim runs at a fraction of real time (measured 0.38-0.65x on a
    /// 240Hz display), its snapshot timeline falls behind, and every OTHER machine's puppets
    /// vibrate from chronic interpolation underruns. See harness.md "clock-dilation trap".
    ///
    /// Defense: while a net session is active and this window is unfocused, swap vsync for a
    /// targetFrameRate cap at the display refresh. targetFrameRate pacing is sleep-based, so
    /// frame deltas measure real elapsed time at ANY refresh rate — the clock stays honest.
    /// The player's own settings are restored the moment focus returns (or the session ends),
    /// so vsync is untouched 100% of the time they are actually looking at the game. A machine
    /// that is genuinely slow (thermal throttle) is NOT this pathology — with vsync off its
    /// clock stays honest and viewers degrade smoothly via adaptive interpolation delay.
    ///
    /// Backstop for what the swap cannot control (driver-forced vsync, unknown compositors):
    /// watches RuntimeInstrumentation.ClockRate and tells the player on-screen that THEIR
    /// instance is running slow — otherwise only the other players see the symptom.
    /// </summary>
    internal sealed class ClockGuard : MonoBehaviour
    {
        internal static ClockGuard Instance;

        private bool _focused = true;
        private bool _swapped;
        private int _savedVsync;
        private int _savedTargetFps;
        private bool _dilatedWhileUnfocused;
        private float _nextFocusedWarnAt;

        private static bool SessionActive =>
            NetSession.Instance != null && NetSession.Instance.State != SessionState.Offline;

        private void Awake() { Instance = this; }

        private void Update()
        {
            // Poll Application.isFocused rather than OnApplicationFocus events: a window that
            // STARTS unfocused (second instance launched behind another) never fires a focus-loss
            // transition, so event-driven state can be stale forever. The property is live.
            bool focused = Application.isFocused;
            if (focused && !_focused && _dilatedWhileUnfocused)
            {
                _dilatedWhileUnfocused = false;
                Toast.Show("GAME RAN SLOW WHILE UNFOCUSED — teammates saw stuttering (driver vsync override?)", 8f);
            }
            _focused = focused;

            // Swap only when it can actually help: session on, window unfocused, vsync engaged
            // (with vsync already off the clock is honest and there is nothing to fix).
            bool want = SessionActive && !_focused && (QualitySettings.vSyncCount > 0 || _swapped);
            if (want && !_swapped) Swap();
            else if (!want && _swapped) Restore();

            // Dilation backstop ([Clock] computes the rate every report interval).
            if (SessionActive && RuntimeInstrumentation.ClockRate < 0.90)
            {
                if (!_focused) _dilatedWhileUnfocused = true;
                else if (Time.unscaledTime >= _nextFocusedWarnAt)
                {
                    _nextFocusedWarnAt = Time.unscaledTime + 10f;
                    Toast.Show($"GAME CLOCK AT {RuntimeInstrumentation.ClockRate:0.0}x REAL TIME — teammates see stuttering (check vsync / driver settings)", 6f);
                }
            }
        }

        private void Swap()
        {
            _savedVsync = QualitySettings.vSyncCount;
            _savedTargetFps = Application.targetFrameRate;
            float rr = 0f;
            try { rr = (float)Screen.currentResolution.refreshRateRatio.value; } catch { }
            int cap = rr > 0f ? Mathf.RoundToInt(rr) : 120;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = cap;
            _swapped = true;
            Plugin.Log.LogInfo($"[ClockGuard] unfocused in session: vsync {_savedVsync}->0, cap {_savedTargetFps}->{cap} (honest clock at any refresh rate)");
        }

        private void Restore()
        {
            QualitySettings.vSyncCount = _savedVsync;
            Application.targetFrameRate = _savedTargetFps;
            _swapped = false;
            Plugin.Log.LogInfo($"[ClockGuard] restored player settings: vsync {_savedVsync}, cap {_savedTargetFps}");
        }

        /// <summary>The game re-applies vsync whenever the player saves video options. If that
        /// happens while we're swapped, adopt the NEW setting as the restore target and keep the
        /// suppression in place until focus returns.</summary>
        internal void OnVideoOptionsApplied()
        {
            if (!_swapped) return;
            _savedVsync = QualitySettings.vSyncCount;
            QualitySettings.vSyncCount = 0;
            Plugin.Log.LogInfo($"[ClockGuard] video options changed while swapped; restore target now vsync {_savedVsync}");
        }

        private void OnDestroy()
        {
            if (_swapped) Restore();
            if (Instance == this) Instance = null;
        }
    }

    /// <summary>SettingsManager.Apply(VideoOptions) is the single place the game writes
    /// QualitySettings.vSyncCount — keep the guard authoritative while it is swapped.</summary>
    [HarmonyPatch(typeof(SettingsManager), nameof(SettingsManager.Apply), typeof(OptionsData.VideoOptions))]
    internal static class SettingsManagerApplyVideoPatch
    {
        private static void Postfix() => ClockGuard.Instance?.OnVideoOptionsApplied();
    }
}
