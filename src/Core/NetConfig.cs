using BepInEx.Configuration;
// The game ships its own global-namespace ConfigFile type which shadows BepInEx's.
using BepConfigFile = BepInEx.Configuration.ConfigFile;

namespace PunkMultiverse
{
    /// <summary>All tunables, bound to BepInEx/plugins/PunkMultiverse/config.cfg.</summary>
    internal static class NetConfig
    {
        public static ConfigEntry<string> Transport;
        public static ConfigEntry<string> LoopbackHost;
        public static ConfigEntry<int> LoopbackPort;
        public static ConfigEntry<bool> PumpSteamCallbacks;
        public static ConfigEntry<int> SteamAppId;
        public static ConfigEntry<bool> AcceptAnySteamSession;
        public static ConfigEntry<bool> ThreadedReceive;
        public static ConfigEntry<float> ReceiveBudgetMs;

        public static ConfigEntry<string> ModManifestPolicy;
        public static ConfigEntry<float> EnemyHealthScalePerPlayer;
        public static ConfigEntry<bool> AutoUpdate;

        public static ConfigEntry<string> AutoStart;
        public static ConfigEntry<bool> AutoReady;
        public static ConfigEntry<bool> AutoLaunchRun;
        public static ConfigEntry<float> AutoFly;
        public static ConfigEntry<bool> DebugMenuKey;
        public static ConfigEntry<string> CommandFile;

        public static ConfigEntry<bool> TrackerNames;
        public static ConfigEntry<bool> TrackerArrows;
        public static ConfigEntry<bool> ShareMapExploration;
        public static ConfigEntry<bool> Scoreboard;

        public static ConfigEntry<float> StateHz;
        public static ConfigEntry<float> CombatStateHz;
        public static ConfigEntry<float> DistantStateHz;
        public static ConfigEntry<float> ShipStateHz;
        public static ConfigEntry<float> AuthorityRadius;
        public static ConfigEntry<float> TransferRadius;
        public static ConfigEntry<float> InterestRadius;
        public static ConfigEntry<float> ResidencyGraceSeconds;

        public static ConfigEntry<bool> SyncDiagnostics;
        public static ConfigEntry<bool> SummaryHeal;
        public static ConfigEntry<bool> CoordinatorMode;
        public static ConfigEntry<bool> HostViaSidecar;

        /// <summary>True when this process is a dedicated coordinator (a shipless host that plays
        /// nobody): hosts the session, runs the correctness plane (leases, sequencer, terrain, fog,
        /// canonical stores), owns no world simulation, and auto-drives the lobby with no UI. Set
        /// via config or the PUNKMV_COORDINATOR environment variable (how a spawned sidecar or a
        /// container enables it without touching config files).</summary>
        public static bool IsCoordinator =>
            (CoordinatorMode != null && CoordinatorMode.Value) || EnvCoordinator;
        internal static readonly bool EnvCoordinator =
            System.Environment.GetEnvironmentVariable("PUNKMV_COORDINATOR") is string v
            && (v == "1" || v.Equals("true", System.StringComparison.OrdinalIgnoreCase));
        public static ConfigEntry<bool> ProfileFrames;
        public static ConfigEntry<bool> HitchWatchdog;
        public static ConfigEntry<int> HitchThresholdMs;
        public static ConfigEntry<int> HitchRepeatMs;
        public static ConfigEntry<float> ProfileReportInterval;
        public static ConfigEntry<float> ProfileObjectScanInterval;
        public static ConfigEntry<bool> CaptureHitchStack;
        public static ConfigEntry<float> DiagOwnershipDumpInterval;
        public static ConfigEntry<string> LogWebhookUrl;

        public static void Init(BepConfigFile cfg)
        {
            Transport = cfg.Bind("Transport", "Transport", "Steam",
                new ConfigDescription("Which transport to use.", new AcceptableValueList<string>("Steam", "Loopback")));
            LoopbackHost = cfg.Bind("Transport", "LoopbackHost", "127.0.0.1",
                "Host address for the dev loopback transport.");
            LoopbackPort = cfg.Bind("Transport", "LoopbackPort", 7777,
                "UDP port for the dev loopback transport.");
            PumpSteamCallbacks = cfg.Bind("Transport", "PumpSteamCallbacks", false,
                "Run SteamAPI.RunCallbacks() ourselves. Leave off — the game's SteamManager already pumps.");
            AcceptAnySteamSession = cfg.Bind("Transport", "AcceptAnySteamSession", false,
                "DEV ONLY: accept P2P sessions from anyone, not just lobby members.");
            SteamAppId = cfg.Bind("Transport", "SteamAppId", 2850470,
                "Playtest appid, used only when the game didn't init Steam itself (direct Punk.exe launch).");
            ThreadedReceive = cfg.Bind("Transport", "ThreadedReceive", true,
                "Receive datagrams on a background thread (Steam AND loopback transports); the " +
                "main thread dispatches them within ReceiveBudgetMs per frame. Keeps inbound " +
                "bursts from spiking a single frame. Off = receive inline on the main thread " +
                "(pre-0.1.84 behavior).");
            ReceiveBudgetMs = cfg.Bind("Transport", "ReceiveBudgetMs", 8f,
                "Max milliseconds per frame spent dispatching received messages when " +
                "ThreadedReceive is on; the rest of the queue carries into the next frame. 0 = unlimited.");

            AutoUpdate = cfg.Bind("Update", "AutoUpdate", true,
                "Download new releases from GitHub at startup and stage them in place; the " +
                "update applies on the next launch (the running DLL is already loaded). The " +
                "replaced build is kept as PunkMultiverse.dll.bak for manual rollback. " +
                "Off = check only, update by hand from the releases page.");

            EnemyHealthScalePerPlayer = cfg.Bind("Session", "EnemyHealthScalePerPlayer", 0.25f,
                "Per-player enemy health scaling used when ENEMY HP SCALING is enabled on the " +
                "GAME SETTINGS screen: Base Health * (1 + (0.25 * number of players)), counted " +
                "when the game starts. The host's value applies to the whole session.");

            ModManifestPolicy = cfg.Bind("Session", "ModManifestPolicy", "Reject",
                new ConfigDescription(
                    "Host-side policy when a joiner's installed BepInEx mod set differs from the host's: " +
                    "Reject refuses the join (naming the difference); Warn lets them join with a [!] MODS " +
                    "marker on the roster; Ignore skips the check entirely.",
                    new AcceptableValueList<string>("Reject", "Warn", "Ignore")));

            AutoStart = cfg.Bind("Debug", "AutoStart", "None",
                new ConfigDescription("DEV ONLY: start a session automatically a few seconds after boot.",
                    new AcceptableValueList<string>("None", "Host", "Join")));
            AutoReady = cfg.Bind("Debug", "AutoReady", false,
                "DEV ONLY: auto-ready in the lobby (for scripted two-instance tests).");
            AutoLaunchRun = cfg.Bind("Debug", "AutoLaunchRun", false,
                "DEV ONLY: host auto-starts the run once everyone is ready.");
            AutoFly = cfg.Bind("Debug", "AutoFlySeconds", 0f,
                "DEV ONLY: after go-live, drive the local ship up-right for this many seconds (scripted tests).");
            DebugMenuKey = cfg.Bind("Debug", "DebugMenuKey", false,
                "DEV ONLY: F1 opens the game's built-in developer debug menu (spawn lists, noclip, " +
                "loadouts). Menu spawns replicate to every peer like any runtime spawn.");
            CommandFile = cfg.Bind("Debug", "CommandFile", "",
                "DEV ONLY: name of a command file in the plugin folder polled twice a second for " +
                "scripted test scenarios (spawn/tp/autofly/say). Empty = off. See docs/harness.md.");

            TrackerNames = cfg.Bind("Tracker", "Names", true,
                "Name label in the player's color above remote players' ships.");
            TrackerArrows = cfg.Bind("Tracker", "Arrows", true,
                "Screen-edge arrows in the player's color with name+distance while they're offscreen; hidden when visible.");
            Scoreboard = cfg.Bind("Tracker", "Scoreboard", true,
                "Hold Tab during a net run for the party scoreboard (HP, kills, deaths, distance).");
            ShareMapExploration = cfg.Bind("Tracker", "ShareMapExploration", true,
                "Merge explored map regions between players (fog-of-war sync).");

            StateHz = cfg.Bind("Sync", "StateHz", 20f,
                "Snapshot send rate for entities (enemies, props). 20 Hz = a fresh state every " +
                "50 ms; puppets adapt their interpolation delay to measured jitter. State is MTU-chunked and interest-filtered " +
                "per peer; raising this still increases apply cost proportional to nearby entities.");
            CombatStateHz = cfg.Bind("Sync", "CombatStateHz", 30f,
                "Snapshot rate for nearby or actively firing enemies. Adaptive interpolation uses " +
                "the measured cadence, so this reduces combat presentation latency without raising every entity.");
            DistantStateHz = cfg.Bind("Sync", "DistantStateHz", 10f,
                "Snapshot rate for enemies outside normal interest proximity but still retained by a simulator.");
            ShipStateHz = cfg.Bind("Sync", "ShipStateHz", 40f,
                "Snapshot send rate for player ships — the thing you watch most, and one tiny " +
                "message per player, so it runs hotter than entities. 40 Hz halves teammate " +
                "visual delay (~50 ms interpolation buffer) for ~2 KB/s per player.");
            AuthorityRadius = cfg.Bind("Authority", "AuthorityRadius", 60f,
                "Max distance (world units) at which a player can hold authority over an entity.");
            TransferRadius = cfg.Bind("Authority", "TransferRadius", 45f,
                "Beyond this distance authority may hand off to a closer player (25% hysteresis).");
            InterestRadius = cfg.Bind("Authority", "InterestRadius", 70f,
                "Entities farther than this from every player go dormant. Keep <= 75 (segment streaming radius).");
            ResidencyGraceSeconds = cfg.Bind("Authority", "ResidencyGraceSeconds", 1.0f,
                new ConfigDescription("Keep a segment's CURRENT owner considered resident for this " +
                    "many seconds after its residency report drops the segment, so one-frame " +
                    "streaming flicker at segment boundaries doesn't ping-pong the lease (the " +
                    "authChurn storm). Only ever retains the current owner — never grants a new " +
                    "lease. 0 disables the grace.",
                    new AcceptableValueRange<float>(0f, 5f)));

            SyncDiagnostics = cfg.Bind("Diag", "SyncDiagnostics", false,
                "Verbose sync/authority diagnostics: per-entity ownership assigns, releases, deny " +
                "windows, entity-state re-baselines, dual-ownership conflicts, and enemy fire " +
                "announce/replay — all tagged [Diag:<category>] for grepping. Off by default (it's " +
                "chatty); toggle live from the F11 overlay. Turn on to diagnose enemy behavior.");
            HostViaSidecar = cfg.Bind("Session", "HostViaSidecar", false,
                "EXPERIMENTAL (server sidecar, LOCAL/LAN only): hosting spawns a headless dedicated " +
                "coordinator process from this install and joins it as a regular player — your game " +
                "crashing or stalling no longer takes the session down. The sidecar is loopback-only " +
                "until the direct-UDP transport lands, so remote friends cannot join a sidecar " +
                "session yet. Your pre-lobby seed/settings choices do not reach the sidecar yet " +
                "(coordinator uses defaults).");
            CoordinatorMode = cfg.Bind("Session", "CoordinatorMode", false,
                "EXPERIMENTAL (server sidecar): run this process as a dedicated shipless coordinator " +
                "— it hosts and runs the correctness plane but plays nobody and simulates nothing. " +
                "Implies AutoStart=Host/AutoReady/AutoLaunchRun (waits for at least one real player). " +
                "Intended for headless use (-batchmode -nographics); can also be forced with the " +
                "PUNKMV_COORDINATOR=1 environment variable.");
            SummaryHeal = cfg.Bind("Diag", "SummaryHeal", false,
                "EXPERIMENTAL (WS9.1): let segment identity-summary mismatches actively trigger " +
                "targeted roster audits (echo + repair). Off = summaries still run as detection " +
                "telemetry (the summaries=tx/chk/miss counters on [BytePlanes]) but never generate " +
                "repair traffic. Keep off until the membership predicate is viewer-targeted: an " +
                "enemy that wanders outside a viewer's interest radius leaves stale data-side " +
                "positions behind, and position-based segment membership then false-positives " +
                "(measured: repeating un-healable mismatches on fringe + wander segments).");
            ProfileFrames = cfg.Bind("Diag", "ProfileFrames", true,
                "Per-frame profiler: times each of our subsystem ticks (ShipSync, WorldSync, " +
                "EnemySync, Authority, …) and every ~3s logs [Profile] avg/max ms per section plus " +
                "network + ownership-churn rates. Also fires a [Profile] SPIKE line naming the " +
                "dominant section on any frame our work exceeds ~20 ms. Cheap (a Stopwatch per " +
                "section); independent of SyncDiagnostics so you can profile without the chatty logs.");
            HitchWatchdog = cfg.Bind("Diag", "HitchWatchdog", true,
                "Watch the Unity main-thread heartbeat from a background thread and log stalls " +
                "even while the main thread cannot advance the normal frame loop.");
            HitchThresholdMs = cfg.Bind("Diag", "HitchThresholdMs", 250,
                new ConfigDescription("Main-thread heartbeat age that begins a hitch incident (ms).",
                    new AcceptableValueRange<int>(100, 5000)));
            HitchRepeatMs = cfg.Bind("Diag", "HitchRepeatMs", 2000,
                new ConfigDescription("Repeat-warning interval during one continuous stall (ms).",
                    new AcceptableValueRange<int>(500, 30000)));
            ProfileReportInterval = cfg.Bind("Diag", "ProfileReportInterval", 3f,
                new ConfigDescription("Seconds between aggregate frame, patch, GC, and count reports.",
                    new AcceptableValueRange<float>(1f, 30f)));
            ProfileObjectScanInterval = cfg.Bind("Diag", "ProfileObjectScanInterval", 15f,
                "Seconds between intrusive live-projectile/GameObject scans (0 disables them). " +
                "A scan over 20 ms disables further scans for that run.");
            CaptureHitchStack = cfg.Bind("Diag", "CaptureHitchStack", true,
                "Attempt a managed main-thread stack on a hitch when this Unity Mono runtime " +
                "supports cross-thread StackTrace capture; otherwise retain the phase marker.");
            DiagOwnershipDumpInterval = cfg.Bind("Diag", "OwnershipDumpInterval", 0f,
                "When SyncDiagnostics is on and this is > 0, log a full ownership table every N " +
                "seconds (0 = only on demand via the F11 overlay button).");
            LogWebhookUrl = cfg.Bind("Diag", "LogWebhookUrl", "",
                "Discord webhook URL for the F8 'send logs' key (uploads a gzipped BepInEx log). " +
                "Empty = F8 disabled. Keep this here, not in source — Discord auto-revokes webhook " +
                "URLs found in public repos.");
        }
    }
}
