using System.Collections.Generic;
using PunkMultiverse.Sync;
using UnityEngine;

namespace PunkMultiverse.Core
{
    /// <summary>
    /// One-shot render-state dump (F11, alongside the ownership dump). Built to diagnose the
    /// "host-owned boss / minions / projectiles deal damage but don't render on the client" bug:
    /// the GameObjects exist and collide, so the question is purely why their renderers don't draw.
    /// Logs, for the local ship (a known-visible baseline) and every nearby puppet, whether each
    /// renderer is enabled/active, its sprite, alpha, sorting layer/order, and whether it's inside
    /// the camera frustum — plus a per-sorting-layer histogram of nearby SpriteRenderers to catch a
    /// whole-layer hide. Compare the puppets against the LOCAL SHIP line: whatever differs is the lead.
    /// </summary>
    internal static class RenderDiag
    {
        public static void DumpNearby(float radius = 60f)
        {
            try
            {
                var ship = ShipSync.LocalShip;
                Vector3 origin = ship != null ? ship.transform.position : Vector3.zero;
                var cam = Camera.main;
                Plugin.Log.LogInfo($"[Diag:Render] === nearby render dump (origin {origin.x:F0},{origin.y:F0}, r={radius}, cam={(cam != null ? cam.name : "null")}) ===");

                if (ship != null) LogEntity("LOCAL SHIP (baseline)", ship.transform, origin, cam);

                var puppets = Object.FindObjectsByType<RemoteEntityPuppet>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                int shown = 0;
                foreach (var p in puppets)
                {
                    if (p == null || Vector2.Distance(origin, p.transform.position) > radius) continue;
                    LogEntity($"puppet netId={p.NetId} '{p.name}'", p.transform, origin, cam);
                    if (++shown >= 40) { Plugin.Log.LogInfo("[Diag:Render] …(more puppets truncated)"); break; }
                }
                if (shown == 0) Plugin.Log.LogInfo("[Diag:Render] no puppets within radius (host-owned bodies not spawned here?)");

                // Per-sorting-layer histogram of nearby SpriteRenderers — a whole layer showing many
                // 'off' (or a layer the ship isn't on) points at a layer/culling hide rather than a
                // per-entity one. Projectiles that are sprites show up here even though they're not puppets.
                var srs = Object.FindObjectsByType<SpriteRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                var byLayer = new Dictionary<string, int[]>(); // [on, off, alpha0]
                foreach (var sr in srs)
                {
                    if (sr == null || Vector2.Distance(origin, sr.transform.position) > radius) continue;
                    if (!byLayer.TryGetValue(sr.sortingLayerName, out var t)) { t = new int[3]; byLayer[sr.sortingLayerName] = t; }
                    if (sr.enabled && sr.gameObject.activeInHierarchy) t[0]++; else t[1]++;
                    if (sr.color.a <= 0.01f) t[2]++;
                }
                foreach (var kv in byLayer)
                    Plugin.Log.LogInfo($"[Diag:Render] SpriteRenderer layer '{kv.Key}': {kv.Value[0]} drawing, {kv.Value[1]} off, {kv.Value[2]} alpha≈0");
                Plugin.Log.LogInfo("[Diag:Render] === end ===");
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"[Diag:Render] dump failed: {e}");
            }
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
            string onCam = "";
            if (cam != null)
            {
                var vp = cam.WorldToViewportPoint(root.position);
                onCam = $" inView={(vp.z > 0 && vp.x >= 0 && vp.x <= 1 && vp.y >= 0 && vp.y <= 1)}";
            }
            Plugin.Log.LogInfo($"[Diag:Render] {label} act={root.gameObject.activeInHierarchy} drawing={on}/{renderers.Length} d={Vector2.Distance(origin, root.position):F0}{onCam}  first: {sample}");
        }
    }
}
