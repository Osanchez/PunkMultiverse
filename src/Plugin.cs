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
            RuntimeInstrumentation.Initialize(Thread.CurrentThread);

            // Growth watchdog: register the bounded-in-steady-state collections whose unbounded
            // growth would signal a leak / stuck queue. Heap is registered by DiagWatch itself.
            Core.DiagWatch.RegisterDefaults = () =>
            {
                Core.DiagWatch.Register("outbox", () => Core.NetSession.Instance?.OutboxDepth ?? 0, floor: 64);
                Core.DiagWatch.Register("visualProjectiles", () => Sync.ProjectileSync.VisualProjectileCount, floor: 128);
                Core.DiagWatch.Register("liveReplicas", () => Sync.EnemySync.LiveEntityCount, floor: 400);
                Core.DiagWatch.Register("capturedLoot", () => Patches.LootDiag.CapturedLootCount, floor: 32);
            };

            _harmony = new Harmony(Guid);
            _harmony.PatchAll(typeof(Plugin).Assembly);

            _runtime = new GameObject("PunkMultiverse");
            Object.DontDestroyOnLoad(_runtime);
            _runtime.hideFlags = HideFlags.HideAndDontSave;
            _runtime.AddComponent<NetSession>();
            _runtime.AddComponent<ClockGuard>();
            _runtime.AddComponent<NetHud>();
            _runtime.AddComponent<LobbyScreen>();
            _runtime.AddComponent<MainMenuInjection>();
            _runtime.AddComponent<PlayerTracker>();
            _runtime.AddComponent<Scoreboard>();
            _runtime.AddComponent<SpectatorCam>();
            _runtime.AddComponent<Toast>();

            Log.LogInfo($"{Name} v{Version} loaded (transport: {NetConfig.Transport.Value}). F9 = net overlay, F10 = sync diagnostics. F8 (or the pause menu in a net run) sends this machine's log for the current run id.");
        }

        // Hot-reload teardown contract: kill the runtime object (stops the session + transport via
        // NetSession.OnDestroy) and remove every Harmony patch so a reload doesn't double-hook.
        private void OnDestroy()
        {
            if (_runtime != null) Object.Destroy(_runtime);
            try { _harmony?.UnpatchSelf(); } catch { }
            RuntimeInstrumentation.Shutdown();
            // Last: we own SteamAPI on direct launches, and an un-shut-down steamclient
            // intermittently deadlocks process exit (the windowless zombie Punk.exe).
            Transport.GameServerBootstrap.Shutdown();
            Transport.SteamBootstrap.Shutdown();
        }
    }
}
