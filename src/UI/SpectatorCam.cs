using PunkMultiverse.Core;
using PunkMultiverse.Sync;
using UnityEngine;
using UnityEngine.InputSystem;

namespace PunkMultiverse.UI
{
    /// <summary>
    /// Death spectating. While the local ship is dead in a net run the camera follows an alive
    /// teammate's puppet; Q/E or the arrow keys cycle between alive players. The camera returns
    /// to the local ship the moment it resurrects. The two camera-target sweeps (ShipSync and
    /// RemotePuppet) exempt whatever <see cref="SpectatedSlot"/> points at.
    /// </summary>
    public sealed class SpectatorCam : MonoBehaviour
    {
        /// <summary>Slot currently followed; -1 = not spectating.</summary>
        public static int SpectatedSlot { get; private set; } = -1;
        public static bool Active => SpectatedSlot >= 0;

        private float _nextEnsureAt;
        private Transform _addedTarget;
        private GUIStyle _hintStyle;

        private void Update()
        {
            var session = NetSession.Instance;
            if (session == null || session.State != SessionState.InGame || ShipSync.LocalShip == null)
            {
                StopSpectating();
                return;
            }
            bool dead = false;
            try { dead = ShipSync.LocalShip.IsDead; } catch { }
            if (!dead)
            {
                StopSpectating();
                return;
            }

            var kb = Keyboard.current;
            if (kb != null && (kb[Key.Q].wasPressedThisFrame || kb[Key.LeftArrow].wasPressedThisFrame))
            {
                Cycle(-1);
            }
            else if (kb != null && (kb[Key.E].wasPressedThisFrame || kb[Key.RightArrow].wasPressedThisFrame))
            {
                Cycle(+1);
            }
            else if (Time.unscaledTime >= _nextEnsureAt)
            {
                // Auto-pick on death, and re-pick if the spectated player died or dropped.
                _nextEnsureAt = Time.unscaledTime + 0.5f;
                if (!IsWatchable(SpectatedSlot)) Cycle(+1);
            }
        }

        private static bool IsWatchable(int slot)
        {
            if (slot < 0) return false;
            var session = NetSession.Instance;
            var p = session != null && slot < session.Players.Count ? session.Players[slot] : null;
            if (p == null || p.IsLocal || !p.Connected) return false;
            if (!ShipSync.ShipsBySlot.TryGetValue(slot, out var ship) || ship == null) return false;
            try { return !ship.IsDead; } catch { return false; }
        }

        private void Cycle(int dir)
        {
            int start = SpectatedSlot >= 0 ? SpectatedSlot : 0;
            for (int i = 1; i <= NetSession.MaxPlayers; i++)
            {
                int slot = ((start + dir * i) % NetSession.MaxPlayers + NetSession.MaxPlayers) % NetSession.MaxPlayers;
                if (IsWatchable(slot))
                {
                    Follow(slot);
                    return;
                }
            }
            StopSpectating(); // nobody alive to watch — stay at the corpse until someone revives
        }

        private void Follow(int slot)
        {
            if (slot == SpectatedSlot) return;
            if (!ShipSync.ShipsBySlot.TryGetValue(slot, out var ship) || ship == null) return;
            try
            {
                var cam = Com.LuisPedroFonseca.ProCamera2D.ProCamera2D.Instance;
                if (cam == null) return;
                ReleaseAddedTarget(cam);
                // The corpse must stop steering the camera while we're away.
                RemoveTargetsOf(cam, ShipSync.LocalShip != null ? ShipSync.LocalShip.transform : null);
                cam.AddCameraTarget(ship.transform);
                _addedTarget = ship.transform;
                SpectatedSlot = slot;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"[Spectate] follow failed: {e.Message}");
            }
        }

        private void StopSpectating()
        {
            if (!Active) return;
            SpectatedSlot = -1;
            try
            {
                var cam = Com.LuisPedroFonseca.ProCamera2D.ProCamera2D.Instance;
                if (cam == null) return;
                ReleaseAddedTarget(cam);
                var local = ShipSync.LocalShip;
                if (local != null && !HasTarget(cam, local.transform))
                    cam.AddCameraTarget(local.transform);
            }
            catch { }
            _addedTarget = null;
        }

        private void ReleaseAddedTarget(Com.LuisPedroFonseca.ProCamera2D.ProCamera2D cam)
        {
            if (_addedTarget != null) cam.RemoveCameraTarget(_addedTarget);
            _addedTarget = null;
        }

        private static void RemoveTargetsOf(Com.LuisPedroFonseca.ProCamera2D.ProCamera2D cam, Transform root)
        {
            if (root == null) return;
            for (int i = cam.CameraTargets.Count - 1; i >= 0; i--)
            {
                var t = cam.CameraTargets[i].TargetTransform;
                if (t != null && (t == root || t.IsChildOf(root)))
                    cam.RemoveCameraTarget(t);
            }
        }

        private static bool HasTarget(Com.LuisPedroFonseca.ProCamera2D.ProCamera2D cam, Transform t)
        {
            for (int i = 0; i < cam.CameraTargets.Count; i++)
                if (cam.CameraTargets[i].TargetTransform == t)
                    return true;
            return false;
        }

        private void OnGUI()
        {
            if (!Active) return;
            var session = NetSession.Instance;
            var p = session != null && SpectatedSlot < session.Players.Count ? session.Players[SpectatedSlot] : null;
            if (p == null) return;
            if (_hintStyle == null)
                _hintStyle = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 18,
                    fontStyle = FontStyle.Bold,
                };
            var rect = new Rect(0, Screen.height - 92, Screen.width, 28);
            GUI.color = new Color(0f, 0f, 0f, 0.7f);
            GUI.Label(new Rect(rect.x + 1, rect.y + 1, rect.width, rect.height),
                $"SPECTATING  {p.Name}      Q ◄   ► E", _hintStyle);
            GUI.color = PlayerColors.Get(p.ColorIndex);
            GUI.Label(rect, $"SPECTATING  {p.Name}      Q ◄   ► E", _hintStyle);
            GUI.color = Color.white;
        }
    }
}
