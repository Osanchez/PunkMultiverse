using HarmonyLib;
using PunkMultiverse.Core;

namespace PunkMultiverse.Patches
{
    /// <summary>
    /// Stop the dedicated coordinator computing things nobody can see or hear.
    ///
    /// A headless server runs the whole game, including systems whose only product is a picture or
    /// a sound. `simprof` measured them on the live server (30s window, 627 frames, 5690ms of
    /// vanilla per-frame time total):
    ///
    ///     LightmapGenerator.Update            656ms
    ///     StationLightSource.Update           534ms   (28215 calls — one per station, per frame)
    ///     PauseScreen.Update                  352ms
    ///     ShipGroundParticle.Update           350ms
    ///     ShipEngineSound.Update              274ms
    ///     UnityTilemapRenderer.Update          88ms
    ///     StatusEffectParticleManager.Update   69ms
    ///
    /// That is ~2.3 of 5.7 seconds — about 40% of the server's vanilla per-frame cost — spent on
    /// lighting, particles, audio and tile rendering for an audience of zero. Prefixes below skip
    /// them on a coordinator only; a player's client is untouched.
    ///
    /// Deliberately NOT trimmed, and why (each looks like render work and is not):
    ///   * LevelChangeBuffer.Update — the single biggest consumer at 1080ms, but it is the terrain
    ///     change pipeline. Terrain diffs are replicated state; cutting it would desync the world.
    ///   * FogManager — fog here is a host-authoritative GAS SIMULATION, not a visual effect
    ///     (docs/ENTITY_SYNC_ARCHITECTURE.md). It affects gameplay and must keep running.
    ///   * MapDrawer.Update — drains the segment-reveal queue that MapShareSync fills from remote
    ///     players' movement. Skipping it would leak that queue on a long-running server.
    ///   * SavableEntity / StationConnection — core entity and station wiring, not presentation.
    ///
    /// This does not touch determinism: none of it participates in world generation, which is
    /// finished long before these run, and the coordinator already contributes a data-only
    /// fingerprint (it reports VisualVariantCount 0 and sits out the visual audit).
    /// </summary>
    internal static class HeadlessTrim
    {
        /// <summary>MEASURED REGRESSION — this is OFF by default and must stay that way until the
        /// culprit is identified. Enabling the whole set on the live server took it from 55-66fps
        /// (avg 15-18ms, ZERO frames over 250ms) to 15-17fps (avg 59-65ms, SIX frames over 250ms)
        /// at matched uptime. Cutting presentation work made the server four times SLOWER, which
        /// means at least one of these Updates is draining a queue that something else then walks —
        /// the same shape as MapDrawer's reveal queue, which is why that one was already excluded.
        /// `UnityTilemapRenderer` is the prime suspect: terrain edits (the BR ring paints thousands
        /// of cells) plausibly enqueue dirty tiles that only its Update consumes.
        ///
        /// So the set is now per-system and runtime-switchable via the `htrim` devcmd, because
        /// bisecting this one release at a time is not affordable — a dedicated server needs a
        /// build, a release and a restart per guess.
        ///     htrim off                     everything vanilla (default)
        ///     htrim lightmap,enginesound     enable just those two
        ///     htrim all                      the full set that regressed
        ///     htrim                          report what is active
        /// </summary>
        private static readonly System.Collections.Generic.HashSet<string> Enabled =
            new System.Collections.Generic.HashSet<string>();

        internal static readonly string[] All =
            { "lightmap", "stationlight", "groundparticle", "enginesound", "statusparticle",
              "tilemap", "pausescreen", "minimap" };

        internal static string Configure(string arg)
        {
            Enabled.Clear();
            if (!string.IsNullOrEmpty(arg))
            {
                if (arg.Equals("all", System.StringComparison.OrdinalIgnoreCase))
                    foreach (var n in All) Enabled.Add(n);
                else if (!arg.Equals("off", System.StringComparison.OrdinalIgnoreCase))
                    foreach (var n in arg.Split(','))
                    {
                        string trimmed = n.Trim().ToLowerInvariant();
                        if (System.Array.IndexOf(All, trimmed) >= 0) Enabled.Add(trimmed);
                    }
            }
            return Enabled.Count == 0 ? "off (all vanilla systems running)" : string.Join(",", Enabled);
        }

        /// <summary>A dedicated coordinator has no camera, audio device or viewer — but see the
        /// regression note above: "nobody can see it" does not prove "nothing depends on it".</summary>
        private static bool Skip(string name) => NetConfig.IsCoordinator && Enabled.Contains(name);

        [HarmonyPatch(typeof(LightmapGenerator), "Update")]
        internal static class NoLightmaps
        {
            private static bool Prefix() => !Skip("lightmap");
        }

        [HarmonyPatch(typeof(StationLightSource), "Update")]
        internal static class NoStationLights
        {
            private static bool Prefix() => !Skip("stationlight");
        }

        [HarmonyPatch(typeof(ShipGroundParticle), "Update")]
        internal static class NoGroundParticles
        {
            private static bool Prefix() => !Skip("groundparticle");
        }

        [HarmonyPatch(typeof(ShipEngineSound), "Update")]
        internal static class NoEngineSound
        {
            private static bool Prefix() => !Skip("enginesound");
        }

        [HarmonyPatch(typeof(StatusEffectParticleManager), "Update")]
        internal static class NoStatusParticles
        {
            private static bool Prefix() => !Skip("statusparticle");
        }

        [HarmonyPatch(typeof(UnityTilemapRenderer), "Update")]
        internal static class NoTilemapRender
        {
            private static bool Prefix() => !Skip("tilemap");
        }

        [HarmonyPatch(typeof(PauseScreen), "Update")]
        internal static class NoPauseScreen
        {
            private static bool Prefix() => !Skip("pausescreen");
        }

        // The minimap rebuilds a texture on a timer and uploads it — pure presentation, and on a
        // shipless coordinator its ship-centering path has nothing to centre on.
        [HarmonyPatch(typeof(Minimap), "Update")]
        internal static class NoMinimap
        {
            private static bool Prefix() => !Skip("minimap");
        }
    }
}
