using PunkMultiverse.Core;
using UnityEngine;

namespace PunkMultiverse.UI
{
    /// <summary>
    /// The Battle Royale zone, drawn instead of built.
    ///
    /// This replaces ~2.9 MILLION painted lava cells per match. The terrain version converted the
    /// whole playable disc through <c>Level.SetCell</c>, and every one of those cells was an event
    /// through <c>LevelChangeBuffer</c> to eight subscribers plus a replicated terrain diff to
    /// every client — measured at 9-second frames on the host and the lag spikes players reported.
    /// Fortnite's storm and PUBG's blue zone are not level geometry either: they are a rendered
    /// surface plus a distance check. This is that, and it costs one mesh and one draw call.
    ///
    /// WHAT IT IS NOT: it has no collider and deals no contact damage. Being caught in the zone
    /// hurts through the radius check in <c>BattleRoyale.LocalTick</c>, and a player can always fly
    /// straight through — the whole point, so the closing ring can never wall someone in.
    ///
    /// The mesh is an ANNULUS regenerated when the radius moves: transparent hole over the safe
    /// zone, lava everywhere outside, out to well past the map edge so there is no visible seam at
    /// the border. Regenerating 128 segments of triangle strip a few times a second is free; the
    /// alternative (one big quad with a scaled radial-alpha texture) cannot keep the hole aligned
    /// to the safe radius as the ring shrinks.
    ///
    /// The material is built from a Unity built-in shader — a mod cannot compile its own, because
    /// shader compilation is an editor/build-time step — and the lava look comes from a
    /// procedurally generated texture scrolled and pulsed over time. Point-filtered and low
    /// resolution on purpose: it has to sit in a game whose whole art direction is 8-bit.
    /// </summary>
    internal sealed class RingLavaVisual : MonoBehaviour
    {
        private const int Segments = 128;      // smooth enough that the hole reads as a circle
        private const float OuterOvershoot = 1.6f; // how far past the map edge the lava extends

        private GameObject _go;
        private MeshFilter _filter;
        private MeshRenderer _renderer;
        private Mesh _mesh;
        private Material _material;
        private Vector3[] _vertices;
        private Vector2[] _uvs;

        private float _builtRadius = -1f;
        private Vector2 _builtCenter;
        private float _outerRadius = 4000f;

        private void LateUpdate()
        {
            if (_failed) return;
            // RingPersists, not Active: the zone stays on screen after the match is decided, right
            // through the victory callout, instead of blinking out at the moment players are
            // looking at where it caught them.
            if (!Modes.BattleRoyale.RingPersists || !NetConfig.ShowZoneVisual.Value)
            {
                if (_go != null) _go.SetActive(false);
                return;
            }
            // A dedicated coordinator has no camera and draws nothing; building a mesh for it is
            // the same class of waste the terrain version was made of.
            if (NetConfig.IsCoordinator) return;

            // The zone must never be the thing that breaks a match — but a swallowed exception here
            // means the zone is silently invisible, and "invisible killing ground" is worse than a
            // caught error nobody logged. Say it once, then stay quiet.
            try { Draw(); }
            catch (System.Exception e)
            {
                if (!_failed)
                {
                    _failed = true;
                    Plugin.Log.LogWarning($"[BR] zone visual failed and is now off: {e.Message}");
                }
                if (_go != null) _go.SetActive(false);
            }
        }

        private bool _failed;

        private void Draw()
        {
            var ring = Modes.BattleRoyale.Ring;
            var center = new Vector2(ring.CenterX, ring.CenterY);
            float safe = Mathf.Max(0f, ring.SafeRadius);

            Ensure();
            _go.SetActive(true);

            // Rebuild only when the hole has actually moved. The ring creeps a few units a second,
            // so this lands a handful of times per second at most.
            if (Mathf.Abs(safe - _builtRadius) > 0.25f || (center - _builtCenter).sqrMagnitude > 0.05f)
            {
                BuildAnnulus(safe);
                _builtRadius = safe;
                _builtCenter = center;
            }

            // Sit the quad at the ring centre, in front of the terrain but behind nothing that
            // matters — the ships are at z=0 and the camera looks down -z.
            _go.transform.position = new Vector3(center.x, center.y, -0.5f);

            // Animate: scroll the lava and pulse its intensity. Both are material properties, so
            // this costs nothing per frame regardless of how large the zone is.
            float t = Time.unscaledTime;
            _material.mainTextureOffset = new Vector2(t * 0.03f, t * 0.017f);
            // Brighter and more opaque as the ring gets deadlier, so the zone LOOKS like what it
            // now does — the damage multiplier is the same number driving the colour.
            float bite = Mathf.Clamp01((Modes.BattleRoyale.ZoneDamageMultiplier - 1f) / 5f);
            float pulse = 0.82f + 0.06f * Mathf.Sin(t * 2.1f);
            _material.color = new Color(
                Mathf.Lerp(1f, 1f, bite),
                Mathf.Lerp(0.42f, 0.16f, bite),
                Mathf.Lerp(0.12f, 0.06f, bite),
                Mathf.Lerp(0.42f, 0.62f, bite) * pulse);
        }

        // ---------------------------------------------------------------- mesh

        /// <summary>Two concentric rings of vertices joined into a triangle strip: the inner ring on
        /// the safe boundary, the outer one past the map edge. The hole is real geometry, not an
        /// alpha cutout, so it stays exactly on the radius the damage check uses at any zoom.</summary>
        private void BuildAnnulus(float innerRadius)
        {
            int ringVerts = Segments + 1;
            if (_vertices == null || _vertices.Length != ringVerts * 2)
            {
                _vertices = new Vector3[ringVerts * 2];
                _uvs = new Vector2[ringVerts * 2];
                var triangles = new int[Segments * 6];
                for (int i = 0; i < Segments; i++)
                {
                    int inner = i * 2, outer = inner + 1, nextInner = inner + 2, nextOuter = inner + 3;
                    int t = i * 6;
                    triangles[t + 0] = inner; triangles[t + 1] = outer; triangles[t + 2] = nextOuter;
                    triangles[t + 3] = inner; triangles[t + 4] = nextOuter; triangles[t + 5] = nextInner;
                }
                _mesh.Clear();
                _mesh.vertices = new Vector3[ringVerts * 2];
                _mesh.triangles = triangles;
            }

            float outer2 = Mathf.Max(innerRadius + 1f, _outerRadius);
            for (int i = 0; i <= Segments; i++)
            {
                float a = i / (float)Segments * Mathf.PI * 2f;
                float cos = Mathf.Cos(a), sin = Mathf.Sin(a);
                int vi = i * 2;
                _vertices[vi] = new Vector3(cos * innerRadius, sin * innerRadius, 0f);
                _vertices[vi + 1] = new Vector3(cos * outer2, sin * outer2, 0f);
                // World-scale UVs so the lava texture keeps a constant size on screen instead of
                // stretching as the ring shrinks.
                _uvs[vi] = new Vector2(_vertices[vi].x, _vertices[vi].y) / 24f;
                _uvs[vi + 1] = new Vector2(_vertices[vi + 1].x, _vertices[vi + 1].y) / 24f;
            }
            _mesh.vertices = _vertices;
            _mesh.uv = _uvs;
            _mesh.RecalculateBounds();
        }

        private void Ensure()
        {
            if (_go != null) return;

            _outerRadius = 4000f;
            try
            {
                var level = ServiceLocator.Get<Level>();
                if (level != null) _outerRadius = Mathf.Max(level.Width, level.Height) * OuterOvershoot;
            }
            catch { }

            _go = new GameObject("PunkMV_ZoneLava");
            _go.transform.SetParent(transform, worldPositionStays: false);
            _mesh = new Mesh { name = "PunkMV_ZoneLava" };
            _mesh.MarkDynamic();
            _filter = _go.AddComponent<MeshFilter>();
            _filter.sharedMesh = _mesh;
            _renderer = _go.AddComponent<MeshRenderer>();
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            _material = BuildMaterial();
            _renderer.sharedMaterial = _material;
            SortAboveTerrain();
            _builtRadius = -1f;
        }

        /// <summary>Draw over the ground but under the ships. Anchored to the GROUND TILEMAP rather
        /// than to a ship: molten ground belongs immediately above the terrain it covers, and
        /// anchoring to the ship would only say "one below the ship", which is a different and much
        /// less reliable thing when the ship sits on another sorting layer entirely. The chosen
        /// layer is logged because this is the one part of the zone a headless test cannot check —
        /// if the lava renders under the terrain or over the ships, that log line says why.</summary>
        private void SortAboveTerrain()
        {
            try
            {
                // Matched by type NAME, not by type: TilemapRenderer lives in
                // UnityEngine.TilemapModule, which this assembly does not reference and does not
                // need to for one sorting lookup.
                Renderer ground = null;
                foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
                {
                    if (r == null || r.GetType().Name != "TilemapRenderer") continue;
                    // Highest-sorted tilemap = the topmost terrain layer; sit just above it.
                    if (ground == null || r.sortingOrder > ground.sortingOrder) ground = r;
                }
                if (ground != null)
                {
                    _renderer.sortingLayerID = ground.sortingLayerID;
                    _renderer.sortingOrder = ground.sortingOrder + 1;
                    Plugin.Log.LogInfo($"[BR] zone visual sorts on layer " +
                        $"'{SortingLayer.IDToName(_renderer.sortingLayerID)}' order {_renderer.sortingOrder} " +
                        $"(above '{ground.name}' at {ground.sortingOrder})");
                    return;
                }
            }
            catch (System.Exception e) { Plugin.Log.LogWarning($"[BR] zone sorting probe failed: {e.Message}"); }
            _renderer.sortingOrder = 100;
            Plugin.Log.LogInfo("[BR] zone visual sorts at fallback order 100 (no tilemap found)");
        }

        private static Material BuildMaterial()
        {
            // A mod cannot ship a compiled shader, so this uses one the game already has loaded.
            // Sprites/Default is the right pick: unlit, vertex-coloured, alpha-blended, and present
            // in every 2D Unity build.
            var shader = Shader.Find("Sprites/Default")
                         ?? Shader.Find("Unlit/Transparent")
                         ?? Shader.Find("UI/Default");
            var material = new Material(shader) { name = "PunkMV_ZoneLava" };
            material.mainTexture = BuildLavaTexture();
            material.mainTexture.wrapMode = TextureWrapMode.Repeat;
            material.renderQueue = 3000; // transparent
            return material;
        }

        /// <summary>A seamless molten texture, generated rather than imported. Two octaves of
        /// tiling Perlin noise quantised into a handful of bands: the quantisation is what makes it
        /// read as 8-bit lava rather than a smooth gradient, and it tiles because the noise is
        /// sampled on a torus.</summary>
        private static Texture2D BuildLavaTexture()
        {
            const int size = 64;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Repeat,
                name = "PunkMV_ZoneLavaTex",
            };
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float n = TilingNoise(x, y, size, 4f) * 0.65f + TilingNoise(x, y, size, 9f) * 0.35f;
                    // Quantise to 5 bands — hard steps, no smooth ramp.
                    float band = Mathf.Floor(Mathf.Clamp01(n) * 5f) / 4f;
                    var c = Color.Lerp(new Color(0.55f, 0.06f, 0.02f), new Color(1f, 0.85f, 0.30f), band);
                    // The brightest band is the most opaque: molten veins read through the haze.
                    byte alpha = (byte)Mathf.Lerp(150f, 255f, band);
                    pixels[y * size + x] = new Color32(
                        (byte)(c.r * 255f), (byte)(c.g * 255f), (byte)(c.b * 255f), alpha);
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply();
            return tex;
        }

        /// <summary>Perlin sampled around two circles instead of across a plane, which is what
        /// makes the result tile seamlessly in both axes.</summary>
        private static float TilingNoise(int x, int y, int size, float frequency)
        {
            float u = x / (float)size * Mathf.PI * 2f;
            float v = y / (float)size * Mathf.PI * 2f;
            float nx = Mathf.Cos(u) * frequency + frequency;
            float ny = Mathf.Cos(v) * frequency + frequency;
            float nz = Mathf.Sin(u) * frequency + frequency;
            float nw = Mathf.Sin(v) * frequency + frequency;
            return (Mathf.PerlinNoise(nx, ny) + Mathf.PerlinNoise(nz, nw)) * 0.5f;
        }

        private void OnDestroy()
        {
            if (_mesh != null) Destroy(_mesh);
            if (_material != null)
            {
                if (_material.mainTexture != null) Destroy(_material.mainTexture);
                Destroy(_material);
            }
            if (_go != null) Destroy(_go);
        }
    }
}
