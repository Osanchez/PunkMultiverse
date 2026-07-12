using System.Collections.Generic;
using PunkMultiverse.Sync;
using UnityEngine;

namespace PunkMultiverse.Core
{
    /// <summary>
    /// Cheap, anomaly-only render check for the "host-owned boss / minions / projectiles deal damage
    /// but don't render on the client" bug. Runs automatically while diagnostics are on (see
    /// <see cref="NetDiag.TickPeriodic"/>). It ONLY scans nearby puppets (not the whole scene — an
    /// includeInactive SpriteRenderer sweep was tanking frame rate) and logs ONLY when a puppet is
    /// inside the camera view yet nothing on it is actually drawing — the invisible-enemy signature.
    /// Stays silent (and near-free) during normal play, so it never spams or hitches.
    /// </summary>
    internal static class RenderDiag
    {
        public static void DumpNearby(float radius = 70f)
        {
            try
            {
                var cam = Camera.main;
                if (cam == null) return;
                var ship = ShipSync.LocalShip;
                Vector3 origin = ship != null ? ship.transform.position : (Vector3)cam.transform.position;

                // Active puppets only — far cheaper than an includeInactive full-scene scan.
                var puppets = Object.FindObjectsByType<RemoteEntityPuppet>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
                var anomalies = new List<RemoteEntityPuppet>();
                int near = 0;
                foreach (var p in puppets)
                {
                    if (p == null || Vector2.Distance(origin, p.transform.position) > radius) continue;
                    near++;
                    if (IsInvisibleInView(p, cam)) anomalies.Add(p);
                }
                if (anomalies.Count == 0) return; // nothing wrong — stay quiet

                Plugin.Log.LogInfo($"[Diag:Render] {anomalies.Count}/{near} nearby puppets are IN-VIEW but NOT drawing (origin {origin.x:F0},{origin.y:F0}):");
                if (ship != null) LogEntity("LOCAL SHIP (baseline, visible)", ship.transform, origin, cam);
                int shown = 0;
                foreach (var p in anomalies)
                {
                    LogEntity($"INVISIBLE puppet netId={p.NetId} '{p.name}'", p.transform, origin, cam);
                    if (++shown >= 25) { Plugin.Log.LogInfo("[Diag:Render] …(more truncated)"); break; }
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"[Diag:Render] check failed: {e.Message}");
            }
        }

        // In the camera frustum but every renderer on it is off / inactive / transparent = the bug.
        private static bool IsInvisibleInView(RemoteEntityPuppet p, Camera cam)
        {
            var vp = cam.WorldToViewportPoint(p.transform.position);
            if (!(vp.z > 0 && vp.x >= 0 && vp.x <= 1 && vp.y >= 0 && vp.y <= 1)) return false; // off-screen
            foreach (var r in p.GetComponentsInChildren<Renderer>(true))
            {
                if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
                if (r is SpriteRenderer sr && sr.color.a <= 0.01f) continue; // transparent doesn't count
                return false; // something is genuinely drawing → not the anomaly
            }
            return true;
        }

        private static void LogEntity(string label, Transform root, Vector3 origin, Camera cam)
        {
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            int on = 0;
            string sample = "(no renderers)";
            foreach (var r in renderers)
            {
                if (r.enabled && r.gameObject.activeInHierarchy) on++;
                if (sample == "(no renderers)")
                {
                    string extra = r is SpriteRenderer sr ? $" sprite={(sr.sprite != null ? sr.sprite.name : "NULL")} a={sr.color.a:F2}" : "";
                    sample = $"{r.GetType().Name} en={r.enabled} act={r.gameObject.activeInHierarchy} layer='{r.sortingLayerName}' order={r.sortingOrder}{extra}";
                }
            }
            var vp = cam.WorldToViewportPoint(root.position);
            bool inView = vp.z > 0 && vp.x >= 0 && vp.x <= 1 && vp.y >= 0 && vp.y <= 1;
            Plugin.Log.LogInfo($"[Diag:Render] {label} act={root.gameObject.activeInHierarchy} drawing={on}/{renderers.Length} d={Vector2.Distance(origin, root.position):F0} inView={inView}  first: {sample}");
        }
    }
}
