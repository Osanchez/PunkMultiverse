using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace PunkMultiverse.Core
{
    /// <summary>
    /// Launches a synchronized run through the game's own loadout selector: BeginRun opens the
    /// vanilla LoadoutSelector scene (each player picks their own loadout, exactly like vanilla),
    /// and when the selector calls GameScene.GoToGameScene we swap in the host's seed. The loadout
    /// assets only exist once that scene loads, so this is also the only reliable way to get one.
    /// </summary>
    internal static class RunStarter
    {
        /// <summary>Seed injected into the next GoToGameScene while a net run is loading.</summary>
        public static int PendingSeed;

        /// <summary>The local player's picked loadout — puppets are placed with this template too
        /// (their real builds arrive via module-grid sync).</summary>
        public static LoadoutTemplate CurrentLoadout { get; private set; }

        public static void LaunchRun(int seed)
        {
            PendingSeed = seed;
            Plugin.Log.LogInfo($"[Run] net run starting: seed={seed} — opening loadout selector");
            RunSetupScene.GoToLoadoutSelector(false, false);
        }

        // During a net run the seed screen of PunkSeedPicker (or any other StartGame interceptor)
        // must not block the flow — the host's seed is authoritative. Highest-priority prefix:
        // returning false skips the original AND all lower-priority prefixes (SeedPicker's).
        [HarmonyPatch(typeof(RunSetupScreen), "StartGame")]
        internal static class BypassStartInterceptors
        {
            private static readonly System.Reflection.FieldInfo ArgsF = AccessTools.Field(typeof(RunSetupScreen), "arguments");

            [HarmonyPriority(HarmonyLib.Priority.First)]
            private static bool Prefix(RunSetupScreen __instance)
            {
                var session = NetSession.Instance;
                if (session == null || !NetSession.Active || session.State != SessionState.Loading) return true;
                var args = (RunArguments)ArgsF.GetValue(__instance);
                Plugin.Log.LogInfo("[Run] bypassing run-setup interceptors (net run) — going to game scene");
                GameScene.GoToGameScene(args); // InjectSeed below stamps the synced seed
                return false;
            }
        }

        // The vanilla selector funnels here on pick; swap in the synced seed.
        [HarmonyPatch(typeof(GameScene), nameof(GameScene.GoToGameScene))]
        internal static class InjectSeed
        {
            private static void Prefix(ref RunArguments __0)
            {
                var session = NetSession.Instance;
                if (session == null || !NetSession.Active || session.State != SessionState.Loading) return;
                __0.seed = PendingSeed;
                __0.isCoop = false;
                __0.isContinue = false;
                CurrentLoadout = __0.startingLoadout;
                Plugin.Log.LogInfo($"[Run] seed {PendingSeed} injected, loadout={CurrentLoadout?.name ?? "null"}");
            }
        }

        /// <summary>DEV: pick the first loadout programmatically (for clickless two-instance tests).</summary>
        public static bool TryAutoPickLoadout()
        {
            var screen = Object.FindFirstObjectByType<RunSetupScreen>();
            if (screen == null) return false;
            var pool = Resources.FindObjectsOfTypeAll<LoadoutPool>().FirstOrDefault();
            var loadouts = pool != null
                ? Traverse.Create(pool).Field("loadouts").GetValue() as System.Collections.Generic.List<LoadoutTemplate>
                : null;
            var pick = loadouts?.FirstOrDefault() ?? Resources.FindObjectsOfTypeAll<LoadoutTemplate>().OrderBy(t => t.name).FirstOrDefault();
            if (pick == null) return false;
            var m = AccessTools.Method(typeof(RunSetupScreen), "OnLoadoutSelected");
            if (m == null)
            {
                Plugin.Log.LogWarning("[Run] RunSetupScreen.OnLoadoutSelected not found");
                return false;
            }
            Plugin.Log.LogInfo($"[Run] DEV auto-picking loadout '{pick.name}'");
            m.Invoke(screen, new object[] { pick });
            return true;
        }

        /// <summary>FNV-1a 64 over the generated terrain — cheap cross-client divergence detector.</summary>
        public static ulong ChecksumLevel(Level level)
        {
            try
            {
                var cells = Traverse.Create(level).Field("cellTypes").GetValue();
                if (cells is Unity.Collections.NativeArray<byte> native && native.IsCreated)
                {
                    var bytes = native.ToArray();
                    ulong hash = 14695981039346656037UL;
                    foreach (var b in bytes)
                    {
                        hash ^= b;
                        hash *= 1099511628211UL;
                    }
                    return hash;
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"[Run] level checksum failed: {e.Message}");
            }
            return 0;
        }
    }
}
