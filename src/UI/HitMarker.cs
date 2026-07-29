using UnityEngine;

namespace PunkMultiverse.UI
{
    /// <summary>
    /// Attacker-side hit confirmation: you should know you connected.
    ///
    /// Omar, 2026-07-29: "when players get hit, the client that hit the other player does not have
    /// any sort of indicator that the other player was hit... even an audio sound or indicator would
    /// help."
    ///
    /// There WAS feedback — <c>DamageSync.RouteTakeDamage</c> plays the victim puppet's
    /// <c>DamageHighlight</c> flash on the attacker's screen the moment a hit is routed. The problem
    /// is that it is the wrong instrument for the job: it is a brief tint on a small sprite that may
    /// be most of a screen away, behind terrain, or off-camera entirely, and in a fight the attacker
    /// is watching their crosshair rather than the target's paintwork. Feedback that depends on
    /// noticing a subtle change on a distant object is feedback that is not there.
    ///
    /// So this reports at the one place the attacker is definitely looking — the centre of their own
    /// screen — and makes a noise, which needs no attention at all. Both are driven from the routed
    /// damage, so the marker only appears for a hit that was actually sent to the victim's machine:
    /// it confirms a real hit rather than a shot that merely looked close.
    /// </summary>
    public sealed class HitMarker : MonoBehaviour
    {
        private const float VisibleSeconds = 0.28f;
        private static float _shownAt = -999f;
        private static float _weight;          // grows with damage, drives size/opacity
        private static float _nextSoundAt;
        private static Texture2D _pixel;

        /// <summary>A hit we dealt landed on another player. `amount` is the damage as it went on
        /// the wire (before the victim's armour and the PvP scale), used only to size the marker.</summary>
        public static void Note(float amount)
        {
            _shownAt = Time.unscaledTime;
            _weight = Mathf.Clamp01(amount / 4f);
            // Rate-limited: a burst weapon routes several hits within a few frames and stacking the
            // same sfx that often turns a confirmation into a rattle.
            if (Time.unscaledTime >= _nextSoundAt)
            {
                _nextSoundAt = Time.unscaledTime + 0.06f;
                try
                {
                    // The ship's own damage sfx, played WITHOUT a position so it reads as a UI
                    // confirmation rather than something happening next to us. Reusing the game's
                    // asset keeps it in the mix the rest of the audio was mastered for.
                    var local = Sync.ShipSync.LocalShip;
                    if (local != null && !string.IsNullOrEmpty(local.damageSfx))
                        AudioManager.PlaySfx(local.damageSfx);
                }
                catch { }
            }
        }

        public static void Reset() { _shownAt = -999f; _weight = 0f; _nextSoundAt = 0f; }

        private void OnGUI()
        {
            float age = Time.unscaledTime - _shownAt;
            if (age < 0f || age > VisibleSeconds) return;

            if (_pixel == null)
            {
                _pixel = new Texture2D(1, 1);
                _pixel.SetPixel(0, 0, Color.white);
                _pixel.Apply();
            }

            // Four ticks angled around the crosshair — the shape reads as "hit" at a glance and,
            // unlike a dot, cannot be mistaken for part of the aiming reticle.
            float fade = 1f - (age / VisibleSeconds);
            float gap = 6f + _weight * 3f;
            float len = 7f + _weight * 5f;
            float thick = 2f;
            float cx = Screen.width * 0.5f;
            float cy = Screen.height * 0.5f;

            GUI.color = new Color(1f, 0.25f, 0.2f, 0.9f * fade);
            // Horizontal pair
            GUI.DrawTexture(new Rect(cx - gap - len, cy - thick * 0.5f, len, thick), _pixel);
            GUI.DrawTexture(new Rect(cx + gap, cy - thick * 0.5f, len, thick), _pixel);
            // Vertical pair
            GUI.DrawTexture(new Rect(cx - thick * 0.5f, cy - gap - len, thick, len), _pixel);
            GUI.DrawTexture(new Rect(cx - thick * 0.5f, cy + gap, thick, len), _pixel);
            GUI.color = Color.white;
        }
    }
}
