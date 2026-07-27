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
        /// <summary>Single gate for every prefix here. A dedicated coordinator has no camera, no
        /// audio device and no viewer; a listen-host still renders for its own player.</summary>
        private static bool SkipOnServer => NetConfig.IsCoordinator;

        [HarmonyPatch(typeof(LightmapGenerator), "Update")]
        internal static class NoLightmaps
        {
            private static bool Prefix() => !SkipOnServer;
        }

        [HarmonyPatch(typeof(StationLightSource), "Update")]
        internal static class NoStationLights
        {
            private static bool Prefix() => !SkipOnServer;
        }

        [HarmonyPatch(typeof(ShipGroundParticle), "Update")]
        internal static class NoGroundParticles
        {
            private static bool Prefix() => !SkipOnServer;
        }

        [HarmonyPatch(typeof(ShipEngineSound), "Update")]
        internal static class NoEngineSound
        {
            private static bool Prefix() => !SkipOnServer;
        }

        [HarmonyPatch(typeof(StatusEffectParticleManager), "Update")]
        internal static class NoStatusParticles
        {
            private static bool Prefix() => !SkipOnServer;
        }

        [HarmonyPatch(typeof(UnityTilemapRenderer), "Update")]
        internal static class NoTilemapRender
        {
            private static bool Prefix() => !SkipOnServer;
        }

        [HarmonyPatch(typeof(PauseScreen), "Update")]
        internal static class NoPauseScreen
        {
            private static bool Prefix() => !SkipOnServer;
        }

        // The minimap rebuilds a texture on a timer and uploads it — pure presentation, and on a
        // shipless coordinator its ship-centering path has nothing to centre on.
        [HarmonyPatch(typeof(Minimap), "Update")]
        internal static class NoMinimap
        {
            private static bool Prefix() => !SkipOnServer;
        }
    }
}
