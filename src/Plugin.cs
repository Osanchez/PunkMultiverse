using System.IO;
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

            _harmony = new Harmony(Guid);
            _harmony.PatchAll(typeof(Plugin).Assembly);

            _runtime = new GameObject("PunkMultiverse");
            Object.DontDestroyOnLoad(_runtime);
            _runtime.hideFlags = HideFlags.HideAndDontSave;
            _runtime.AddComponent<NetSession>();
            _runtime.AddComponent<NetHud>();
            _runtime.AddComponent<LobbyScreen>();
            _runtime.AddComponent<MainMenuInjection>();
            _runtime.AddComponent<PlayerTracker>();
            _runtime.AddComponent<Scoreboard>();
            _runtime.AddComponent<SpectatorCam>();
            _runtime.AddComponent<Toast>();
            _runtime.AddComponent<LogUploader>();

            Log.LogInfo($"{Name} v{Version} loaded (transport: {NetConfig.Transport.Value}). F9 = net overlay, F10 = sync diagnostics. Logs auto-send to the webhook on game close (if [Diag] LogWebhookUrl is set); F8 sends now.");
        }

        // Hot-reload teardown contract: kill the runtime object (stops the session + transport via
        // NetSession.OnDestroy) and remove every Harmony patch so a reload doesn't double-hook.
        private void OnDestroy()
        {
            if (_runtime != null) Object.Destroy(_runtime);
            try { _harmony?.UnpatchSelf(); } catch { }
        }
    }
}
