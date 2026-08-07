using System.IO;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.Mono;
using HarmonyLib;
// The game ships its own global-namespace ConfigFile type which shadows BepInEx's.
using BepConfigFile = BepInEx.Configuration.ConfigFile;
using PunkMultiverse.Core;
using PunkMultiverse.UI;
using UnityEngine;

namespace PunkMultiverse
{
    /// <summary>
    /// Punk Multiverse — online co-op for PUNK, inspired by Noita Entangled Worlds.
    /// Steam lobbies (clipboard code / friend invite), 4 players, proximity entity authority,
    /// per-player loot, shared map progression. Everything in-game; no companion app.
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.osanchez.punkmultiverse";
        public const string Name = "Punk Multiverse";
        // Baked from the csproj <Version> at build time (GeneratePluginVersion target).
        public const string Version = PluginVersionInfo.Version;

        internal static ManualLogSource Log;
        internal static Plugin Instance;

        private Harmony _harmony;
        private GameObject _runtime;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            var cfg = new BepConfigFile(Path.Combine(ModFolder.Dir, "config.cfg"), saveOnInit: true);
            NetConfig.Init(cfg);
            // Straight after Init, never before: the audit's whole method is reading what BepInEx
            // did NOT hand to a Bind call, which is only meaningful once every Bind has run.
            ConfigAudit.Run(cfg);
            // Before the first patch is applied: if Steam has moved the base game underneath us,
            // say so by name now rather than letting it surface later as unexplained behaviour.
            GameGuard.Run();
            RuntimeInstrumentation.Initialize(Thread.CurrentThread);

            // Growth watchdog: register the bounded-in-steady-state collections whose unbounded
            // growth would signal a leak / stuck queue. Heap is registered by DiagWatch itself.
            Core.DiagWatch.RegisterDefaults = () =>
            {
                Core.DiagWatch.Register("outbox", () => Core.NetSession.Instance?.OutboxDepth ?? 0, floor: 64);
                Core.DiagWatch.Register("visualProjectiles", () => Sync.ProjectileSync.VisualProjectileCount, floor: 128);
                Core.DiagWatch.Register("liveReplicas", () => Sync.EnemySync.LiveEntityCount, floor: 400);
                Core.DiagWatch.Register("capturedLoot", () => Patches.LootDiag.CapturedLootCount, floor: 32);
                // Long-running-server guards: per-entity send bookkeeping is pruned on stream-out,
                // so these plateau near the live-entity count; the segment cache plateaus near the
                // manifest size. Sustained climb = pruning regressed (a marathon session would
                // otherwise grow them forever, one entry per runtime spawn ever).
                Core.DiagWatch.Register("sendState", () => Sync.EnemySync.SendStateCount, floor: 512);
                Core.DiagWatch.Register("sendPriority", () => Sync.EnemySync.SendPriorityCount, floor: 512);
                Core.DiagWatch.Register("segmentCache", () => Core.AuthorityManager.EntitySegmentCacheCount, floor: 12000);
            };

            // Before anything else can fail: quitting must always end the process, even when a
            // native library deadlocks the shutdown. See Core/ExitWatchdog.cs.
            Core.ExitWatchdog.Install();
            UnityEngine.Application.quitting += Content.ContentSync.StopWorker;

            _harmony = new Harmony(Guid);
            _harmony.PatchAll(typeof(Plugin).Assembly);
            // Kept out of PatchAll on purpose — see BattleRoyaleLoot.ApplyGenericPatches.
            Modes.BattleRoyaleLoot.ApplyGenericPatches(_harmony);
            // Same generic-method corner, same hand-application: a pickup may never outlive its
            // own grant (Patches/PickupGrantGuard.cs — the infinite-item faucet).
            Patches.PickupGrantGuard.ApplyGenericPatches(_harmony);
            // Third-party target, so hand-applied for the same reason: PatchAll would throw at
            // chainload on a machine where WeaponForge is absent or has been refactored, and take
            // the whole mod down with it. Absent is the common case.
            Content.ForgeBridge.ApplySuppressionPatch(_harmony);
            // Clear crash debris from the content cache before anything can read it. Listings
            // only, no hashing, so it is cheap enough to run unconditionally.
            Content.ContentStore.BootSweep();

            _runtime = new GameObject("PunkMultiverse");
            Object.DontDestroyOnLoad(_runtime);
            _runtime.hideFlags = HideFlags.HideAndDontSave;
            _runtime.AddComponent<NetSession>();
            _runtime.AddComponent<ClockGuard>();
            _runtime.AddComponent<NetHud>();
            _runtime.AddComponent<LobbyScreen>();
            _runtime.AddComponent<MainMenuInjection>();
            _runtime.AddComponent<PlayerTracker>();
            _runtime.AddComponent<RingLavaVisual>();
            _runtime.AddComponent<RingWorldMapOverlay>();
            _runtime.AddComponent<CarePackageArrow>(); // gold edge arrow + crate glyph to BR drops
            _runtime.AddComponent<Scoreboard>();
            _runtime.AddComponent<ShipStatusBars>();
            _runtime.AddComponent<SpectatorCam>();
            _runtime.AddComponent<Toast>();
            _runtime.AddComponent<KillFeed>();   // deaths get their own top-right channel
            _runtime.AddComponent<HitMarker>();  // attacker-side hit confirmation

            // LiteNetLib is merged into this assembly (ILRepack, internalized) as of v0.1.164 —
            // delete the stale standalone DLL an older install left. Safe at this point: the
            // merged types mean nothing ever loads that file again; boot is also the one moment
            // it's guaranteed unmapped (self-update can't delete it mid-session).
            try
            {
                var stale = System.IO.Path.Combine(ModFolder.Dir, "LiteNetLib.dll");
                if (System.IO.File.Exists(stale))
                {
                    System.IO.File.Delete(stale);
                    Log.LogInfo("[Update] removed stale LiteNetLib.dll (merged into the mod since v0.1.164)");
                }
            }
            catch { }

            if (NetConfig.IsCoordinator)
            {
                // Dedicated server: no player, no monitor, no hotkeys — client video features
                // stand down (the fps-limit fallback used to log a meaningless "MAX (240)" here,
                // Omar 2026-07-24). What a headless server DOES need is a frame cap: uncapped
                // null-gfx Unity idles at 1000+ fps of pure CPU waste.
                int cap = NetConfig.ServerFrameRateCap.Value;
                if (cap > 0) UnityEngine.Application.targetFrameRate = cap;
                // The mode belongs in the boot banner: this is the mod's OWN reading of the config,
                // so it stays true even when the egg's env-var banner and the injected config.cfg
                // disagree — which is exactly the failure that silently hosts the wrong ruleset.
                Log.LogInfo($"{Name} v{Version} — dedicated coordinator (transport: {NetConfig.Transport.Value}, " +
                    $"game mode: {NetConfig.ConfiguredMode}, frame cap {(cap > 0 ? cap + " fps" : "none")}). " +
                    "Admin via devcmd.txt (tools/srv.ps1 cmd); " +
                    "`uploadlogs` sends this server's log for the current run id.");
            }
            else
            {
                // Video QoL: fps cap (defaults to the monitor's refresh) + resizable window. The
                // handle lookup no longer depends on the window being focused (that was the cause
                // of "resizing works sometimes" — see VideoTweaks.FindProcessWindow), and the
                // Ticker below re-asserts it for windows that appear or get rebuilt later.
                UI.VideoTweaks.CaptureWindowHandle();
                UI.VideoTweaks.ApplyFpsLimit();
                UI.VideoTweaks.ApplyResizableWindow();
                // ...and keep re-asserting it: the window may not exist or be focused yet at load,
                // and Alt+Enter rebuilds the window style without raising the settings event.
                _runtime.AddComponent<UI.VideoTweaks.Ticker>();

                Log.LogInfo($"{Name} v{Version} loaded (transport: {NetConfig.Transport.Value}). F9 = net overlay, F10 = sync diagnostics. F8 (or the pause menu in a net run) sends this machine's log for the current run id.");
            }
        }

        // Hot-reload teardown contract: kill the runtime object (stops the session + transport via
        // NetSession.OnDestroy) and remove every Harmony patch so a reload doesn't double-hook.
        private void OnDestroy()
        {
            if (_runtime != null) Object.Destroy(_runtime);
            try { _harmony?.UnpatchSelf(); } catch { }
            RuntimeInstrumentation.Shutdown();
            // Backstop: if this teardown ran without Application.quitting (or that event never
            // fires on this exit path), the guarantee still applies from here.
            ExitWatchdog.Arm();
            // Last: we own SteamAPI on direct launches, and an un-shut-down steamclient
            // intermittently deadlocks process exit (the windowless zombie Punk.exe).
            Transport.GameServerBootstrap.Shutdown();
            Transport.SteamBootstrap.Shutdown();
        }
    }
}
