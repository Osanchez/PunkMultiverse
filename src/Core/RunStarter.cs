using System;
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

        /// <summary>Battle Royale skipped the selector, so there is nothing to auto-pick past.</summary>
        public static bool SelectorSkipped { get; private set; }

        public static void LaunchRun(int seed)
        {
            PendingSeed = seed;
            SelectorSkipped = false;

            // Battle Royale has no class selection: everyone flies the Gunner, so the selector
            // would be a screen with one legal answer. Skip straight to the game scene — the
            // selector is only a producer of RunArguments, and GameScene.GoToGameScene takes them
            // directly (the game's own Continue/QuickLoad flows do exactly this).
            var session = NetSession.Instance;
            if (session != null && session.LobbyMode == Protocol.GameMode.BattleRoyale)
            {
                // startingLoadout is deliberately left null here. The loadout assets live in the
                // LoadoutSelector scene's bundle and are NOT resident while we sit in the lobby —
                // measured, not assumed: every machine in the harness logged "assets are not loaded
                // yet". ForceBattleRoyaleLoadout below stamps the Gunner in once the Game scene is
                // up and that bundle has loaded with it.
                SelectorSkipped = true;
                Plugin.Log.LogInfo($"[Run] net run starting: seed={seed} — BR, no class selection");
                GameScene.GoToGameScene(RunArguments.NewRun(false)); // InjectSeed stamps the synced seed
                return;
            }

            Plugin.Log.LogInfo($"[Run] net run starting: seed={seed} — opening loadout selector");
            RunSetupScene.GoToLoadoutSelector(false, false);
        }

        /// <summary>The Battle Royale ship for everyone: the Gunner.
        ///
        /// Matched by identity, never by list position — LoadoutPool.loadouts is a hand-ordered
        /// serialized list (its real order is 4,2,1,5,3,6), so `loadouts[0]` happening to be the
        /// Gunner today is luck, not a contract. displayName is the game's player-facing label
        /// ("GUNNER"); the asset name is what the game's own unlock system keys on, so both are
        /// checked before settling for anything else.</summary>
        internal static int LastCandidateCount;

        internal static LoadoutTemplate FindBattleRoyaleLoadout()
        {
            var pool = Resources.FindObjectsOfTypeAll<LoadoutPool>().FirstOrDefault();
            var loadouts = pool != null
                ? Traverse.Create(pool).Field("loadouts").GetValue() as System.Collections.Generic.List<LoadoutTemplate>
                : null;
            var candidates = (loadouts != null && loadouts.Count > 0)
                ? loadouts.Where(l => l != null)
                : Resources.FindObjectsOfTypeAll<LoadoutTemplate>().Where(l => l != null);
            var all = candidates.ToList();
            LastCandidateCount = all.Count; // logged: seeing 1 of 6 means the bundle is only half up
            if (all.Count == 0) return null;

            return all.FirstOrDefault(l => string.Equals(l.displayName, "GUNNER", StringComparison.OrdinalIgnoreCase))
                ?? all.FirstOrDefault(l => string.Equals(l.name, "Starter_Popper", StringComparison.OrdinalIgnoreCase))
                ?? all.OrderBy(l => l.name, StringComparer.Ordinal).First(); // deterministic on every machine
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
                // PreGenLoading: a coordinator building the next world during lobby idle is a run
                // launch in every way except session state — the bypass and seed injection must
                // fire for it too. (Gating on Loading alone silently generated pre-built worlds
                // with a VANILLA seed: right world shape, wrong world — every reuse then tripped
                // the determinism barrier and looked like "drift".)
                if (session == null || !NetSession.Active
                    || (session.State != SessionState.Loading && !session.PreGenLoading)) return true;
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
                if (session == null || !NetSession.Active
                    || (session.State != SessionState.Loading && !session.PreGenLoading)) return; // PreGenLoading: see BypassStartInterceptors
                __0.seed = PendingSeed;
                __0.isCoop = false;
                __0.isContinue = false;
                // Battle Royale starts everyone on the Gunner — what you build from there is earned
                // in the match, not chosen at the menu. Runs on every machine, so no agreement has
                // to be negotiated. This also covers the selector fallback path and the game's own
                // Restart(), both of which re-enter here with whatever the player last picked.
                if (session.LobbyMode == Protocol.GameMode.BattleRoyale)
                {
                    var gunner = FindBattleRoyaleLoadout();
                    if (gunner != null)
                    {
                        if (__0.startingLoadout != gunner)
                            Plugin.Log.LogInfo($"[BR] forced loadout '{gunner.name}' ({gunner.displayName})");
                        __0.startingLoadout = gunner;
                    }
                    else Plugin.Log.LogWarning("[BR] no loadout assets found — the run keeps the game's default ship");
                }
                CurrentLoadout = __0.startingLoadout;
                Plugin.Log.LogInfo($"[Run] seed {PendingSeed} injected, loadout={CurrentLoadout?.name ?? "null"}");
            }
        }

        /// <summary>The loadout a puppet ship should be built from, resolved LATE and on demand.
        ///
        /// <see cref="CurrentLoadout"/> is captured at seed injection — which on a dedicated
        /// coordinator happens during the PRE-GENERATION scene load, before the loadout bundle is
        /// resident, so it captures null ("[Run] seed N injected, loadout=null"). The pre-built
        /// world is then REUSED at START with no scene reload, so neither the injection postfix nor
        /// GameController.Awake ever fires again to correct it: CurrentLoadout stayed null for the
        /// whole run, every SpawnPuppet bailed with "missing prefab/loadout", and the coordinator
        /// ran the entire match with ZERO ship puppets (remoteShips=0). That one null is why a
        /// dedicated Battle Royale could only be won by disconnect (no puppet -> ApplyShipState and
        /// ApplyLifeEvent both early-return -> DeadBySlot never written -> IsSlotDead always false)
        /// and why enemies never got an owner assigned (authority scan saw no simulators).
        ///
        /// By go-live the assets ARE resident, so resolving here succeeds where the early capture
        /// could not. Only consulted when the captured value is missing, so a normal run — where
        /// injection ran against a real scene — keeps using exactly what the player launched with.
        /// </summary>
        internal static LoadoutTemplate ResolveLoadout()
        {
            if (CurrentLoadout != null) return CurrentLoadout;
            var found = FindBattleRoyaleLoadout(); // Gunner-preferred, else deterministic first
            if (found == null) return null;
            CurrentLoadout = found;
            Plugin.Log.LogInfo($"[Run] loadout resolved late: '{found.name}' ({found.displayName}) " +
                $"from {LastCandidateCount} candidates — the injection pass ran during " +
                "pre-generation, before the loadout bundle was resident");
            return found;
        }

        /// <summary>Stamp the Gunner into the run the moment the Game scene exists.
        ///
        /// This is the only place the forcing can be GUARANTEED to work: GameController.Awake is
        /// the first point at which the loadout bundle is resident (the Game scene references the
        /// pool through LoadoutUnlocker), and it runs before BuildLevel reads startingLoadout. It
        /// also catches every entry path at once — skipped selector, a selector run, and the game's
        /// own Restart() — so no launch route can sneak a different ship into a BR match.
        /// </summary>
        [HarmonyPatch(typeof(GameController), "Awake")]
        internal static class ForceBattleRoyaleLoadout
        {
            private static void Postfix(GameController __instance)
            {
                var session = NetSession.Instance;
                if (session == null || !NetSession.Active
                    || session.CurrentMode != Protocol.GameMode.BattleRoyale) return;
                try
                {
                    var gunner = FindBattleRoyaleLoadout();
                    if (gunner == null) { Plugin.Log.LogWarning("[BR] no loadout assets in the game scene — keeping the default ship"); return; }
                    var field = Traverse.Create(__instance).Field("runArguments");
                    var args = field.GetValue<RunArguments>(); // struct: read, modify, write back
                    args.startingLoadout = gunner;
                    field.SetValue(args);
                    CurrentLoadout = gunner;
                    Plugin.Log.LogInfo($"[BR] every ship is the {gunner.displayName} ('{gunner.name}') " +
                        $"— chosen from {LastCandidateCount} loadouts, no class selection");
                }
                catch (Exception e) { Plugin.Log.LogWarning($"[BR] could not force the loadout: {e.Message}"); }
            }
        }

        /// <summary>DEV: pick the first loadout programmatically (for clickless two-instance tests).</summary>
        private static float _nextAutoPickDiagAt;

        /// <summary>Why the retry loop is still retrying (throttled) — a silent forever-retry here
        /// leaves the coordinator parked in the loadout selector with no clue in the log.</summary>
        private static void AutoPickDiag(string why)
        {
            if (Time.unscaledTime < _nextAutoPickDiagAt) return;
            _nextAutoPickDiagAt = Time.unscaledTime + 5f;
            Plugin.Log.LogWarning($"[Run] auto-pick waiting: {why} " +
                $"(active scene '{UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}')");
        }

        public static bool TryAutoPickLoadout()
        {
            // BR never opened a selector; without this the harness/coordinator retry loop would
            // warn "no RunSetupScreen" once a second for the whole run.
            if (SelectorSkipped) return true;
            var screen = UnityEngine.Object.FindFirstObjectByType<RunSetupScreen>();
            if (screen == null) { AutoPickDiag("no RunSetupScreen in the loaded scene"); return false; }
            var pool = Resources.FindObjectsOfTypeAll<LoadoutPool>().FirstOrDefault();
            var loadouts = pool != null
                ? Traverse.Create(pool).Field("loadouts").GetValue() as System.Collections.Generic.List<LoadoutTemplate>
                : null;
            var pick = loadouts?.FirstOrDefault() ?? Resources.FindObjectsOfTypeAll<LoadoutTemplate>().OrderBy(t => t.name).FirstOrDefault();
            if (pick == null) { AutoPickDiag("RunSetupScreen present but no LoadoutTemplate loaded yet"); return false; }
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
