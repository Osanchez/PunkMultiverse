using System;
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
        public static ConfigEntry<string> UdpAddress;
        public static ConfigEntry<int> UdpPort;
        public static ConfigEntry<bool> PumpSteamCallbacks;
        public static ConfigEntry<int> SteamAppId;
        public static ConfigEntry<bool> AcceptAnySteamSession;
        public static ConfigEntry<bool> ThreadedReceive;
        public static ConfigEntry<float> ReceiveBudgetMs;

        public static ConfigEntry<string> ModManifestPolicy;
        public static ConfigEntry<float> EnemyHealthScalePerPlayer;
        public static ConfigEntry<float> CoinDespawnSeconds;
        public static ConfigEntry<float> JitterFloorUnitsPerSec;
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
        public static ConfigEntry<bool> ShipStatusBars;

        public static ConfigEntry<float> StateHz;
        public static ConfigEntry<float> CombatStateHz;
        public static ConfigEntry<float> DistantStateHz;
        public static ConfigEntry<float> ShipStateHz;
        public static ConfigEntry<float> AuthorityRadius;
        public static ConfigEntry<float> TransferRadius;
        public static ConfigEntry<float> InterestRadius;
        public static ConfigEntry<float> ResidencyGraceSeconds;

        public static ConfigEntry<bool> SyncDiagnostics;
        public static ConfigEntry<string> LogLevel;

        /// <summary>Full-rate instrumentation (periodic blocks at ProfileReportInterval, every SPIKE line).</summary>
        public static bool VerboseLogs =>
            string.Equals(LogLevel?.Value, "Verbose", StringComparison.OrdinalIgnoreCase);
        /// <summary>Warnings and one-shot events only — no periodic instrumentation blocks.</summary>
        public static bool QuietLogs =>
            string.Equals(LogLevel?.Value, "Quiet", StringComparison.OrdinalIgnoreCase);
        public static ConfigEntry<string> LogUploadEndpoint;
        public static ConfigEntry<bool> SummaryHeal;
        public static ConfigEntry<bool> ClockGuardEnabled;
        public static ConfigEntry<bool> CoordinatorMode;
        public static ConfigEntry<float> EmptyServerResetSeconds;
        public static ConfigEntry<int> ServerFrameRateCap;
        public static ConfigEntry<bool> PreGenerateWorld;
        public static ConfigEntry<bool> EnableGameModes;
        public static ConfigEntry<string> GameMode;
        public static ConfigEntry<int> BrMatchMinutes;
        public static ConfigEntry<int> BrRingStartMinutes;
        public static ConfigEntry<int> BrRingStages;
        public static ConfigEntry<float> BrRingCloseSeconds;
        public static ConfigEntry<float> BrRingHoldMinutes;
        public static ConfigEntry<float> BrNextRingWarningSeconds;
        public static ConfigEntry<float> BrStationUnlockDelaySeconds;
        public static ConfigEntry<bool> BrSpawnAtStationDirectly;
        public static ConfigEntry<bool> BrChooseSpawn;
        public static ConfigEntry<float> BrChooseSpawnSeconds;
        public static ConfigEntry<float> BrSpawnProtectionSeconds;
        public static ConfigEntry<bool> ShowZoneVisual;
        public static ConfigEntry<float> BrZoneKillSeconds;
        public static ConfigEntry<float> BrZoneDamageStageScale;
        public static ConfigEntry<bool> SegmentChangeRouting;
        public static ConfigEntry<bool> TrimTerrainPresentation;
        public static ConfigEntry<int> BrCarePackageMinutes;
        public static ConfigEntry<int> BrCarePackagesPerWave;
        public static ConfigEntry<float> BrRingFirstHoldMinutes;
        public static ConfigEntry<float> PvPDamageScale;
        public static ConfigEntry<float> BrEnemyHpScale;
        public static ConfigEntry<int> BrMinPlayers;
        public static ConfigEntry<float> BrSpawnClearRadius;
        public static ConfigEntry<float> BrStationHazardClearRadius;
        public static ConfigEntry<float> BrWinnerSelfDestructSeconds;
        public static ConfigEntry<string> InstallId;

        /// <summary>Master switch for alternate game modes. While it is off, nothing can select
        /// anything but Standard: the GAME MODE row does not appear when self-hosting and a
        /// dedicated server ignores its GameMode value. Governs what THIS machine may host — a
        /// client joining a server that is running Battle Royale still plays it, because the run's
        /// ruleset is the host's to decide.</summary>
        public static bool GameModesEnabled => EnableGameModes != null && EnableGameModes.Value;

        /// <summary>Dedicated-server ruleset for every run it hosts (restart to change), or
        /// Standard while game modes are switched off. A self-hosting player picks the mode on the
        /// GAME SETTINGS screen instead; this value is only the server's default.
        /// See docs/BATTLE_ROYALE.md.</summary>
        public static Protocol.GameMode ConfiguredMode =>
            GameModesEnabled
            && GameMode != null && GameMode.Value != null
            && GameMode.Value.Trim().Replace("_", "").Equals("BattleRoyale", StringComparison.OrdinalIgnoreCase)
                ? Protocol.GameMode.BattleRoyale
                : Protocol.GameMode.Standard;

        /// <summary>Non-null when a mode was configured but the master switch will quietly veto it —
        /// the one way a dedicated server hosts the wrong ruleset for a whole session with nothing
        /// in the log to explain it. (An unrecognized mode NAME cannot be detected here: GameMode
        /// binds with an AcceptableValueList, so BepInEx rewrites a typo to the default before this
        /// ever reads it. That check lives in pelican_egg/start-server.sh, which sees the raw
        /// panel value.)</summary>
        public static string ModeWarning()
        {
            string raw = (GameMode?.Value ?? "").Trim();
            if (raw.Length == 0 || raw.Equals("Standard", StringComparison.OrdinalIgnoreCase)) return null;
            return GameModesEnabled ? null
                : $"GameMode is '{raw}' but EnableGameModes is false — hosting Standard. " +
                  "Set EnableGameModes=1 (panel: Enable Game Modes) and restart to allow it.";
        }
        public static ConfigEntry<int> FpsLimit;
        public static ConfigEntry<bool> ResizableWindow;
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

        /// <summary>Transport a spawned coordinator should use, from PUNKMV_TRANSPORT (the launcher
        /// sets it to match the hosting player's own capability: SteamServer on a Steam machine,
        /// Loopback for local-only). Default Loopback — the safe local behavior.</summary>
        internal static string EnvCoordinatorTransport =>
            System.Environment.GetEnvironmentVariable("PUNKMV_TRANSPORT") is string t
            && !string.IsNullOrWhiteSpace(t) ? t : "Loopback";
        public static ConfigEntry<bool> ProfileFrames;
        public static ConfigEntry<bool> HitchWatchdog;
        public static ConfigEntry<int> HitchThresholdMs;
        public static ConfigEntry<int> HitchRepeatMs;
        public static ConfigEntry<float> ProfileReportInterval;
        public static ConfigEntry<float> ProfileObjectScanInterval;
        public static ConfigEntry<bool> CaptureHitchStack;
        public static ConfigEntry<float> DiagOwnershipDumpInterval;

        public static void Init(BepConfigFile cfg)
        {
            Transport = cfg.Bind("Transport", "Transport", "Steam",
                new ConfigDescription(
                    "LEAVE THIS ON Steam - normal players never change it. Hosting and joining " +
                    "friends works on Steam, and joining a dedicated server is done in-game (PLAY " +
                    "ONLINE -> DIRECT CONNECT for an IP server, or accepting a Steam invite), which " +
                    "auto-selects the right connection. This value is only the DEFAULT for the " +
                    "clipboard Join/Host buttons. Advanced/server-only: Loopback (dev/LAN UDP); " +
                    "SteamServer (dedicated Steam game-server identity - what the host-your-own " +
                    "sidecar uses); Udp (direct UDP by host:port - what Docker/no-Steam servers use).",
                    new AcceptableValueList<string>("Steam", "Loopback", "SteamServer", "Udp")));
            UdpAddress = cfg.Bind("Transport", "UdpAddress", "127.0.0.1",
                "Udp transport: server address to join when no explicit address is given " +
                "(a `join host:port` devcmd or lobby code overrides this).");
            UdpPort = cfg.Bind("Transport", "UdpPort", 7778,
                "Udp transport: port the server listens on / clients connect to. Distinct from " +
                "LoopbackPort so a dev loopback session and a Udp server can coexist on one machine.");
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
                "GAME SETTINGS screen: Base Health * (1 + 0.25 * (players - 1)), counted when the " +
                "game starts — a solo player is the unscaled vanilla baseline. The host's value " +
                "applies to the whole session.");

            CoinDespawnSeconds = cfg.Bind("Session", "CoinDespawnSeconds", 45f,
                "Co-op only: currency (gold) pickups auto-despawn this many seconds after dropping " +
                "if nobody collects them — the base game has no such timer, so uncollected coins " +
                "would pile up forever. Only shared-currency ResourcePickups are affected; module/" +
                "ingredient/consumable pickups are left to persist. 0 disables (coins never despawn).");

            InstallId = cfg.Bind("Session", "InstallId", "",
                "Auto-generated once and then left alone: a random id that tells a server which " +
                "copy of the game you are, so a rejoin finds your old slot. Only used by the " +
                "non-Steam transports (Udp/Loopback), which carry no account identity. Blank = " +
                "generate on the next connect. Clear it if you copied a whole game folder to " +
                "another machine and the two now collide.");

            JitterFloorUnitsPerSec = cfg.Bind("Diag", "JitterFloorUnitsPerSec", 10f,
                "Enemy-jitter detector threshold: a remote enemy whose interpolation racks up this " +
                "many units/second of in-place (goes-nowhere) motion is reported as vibrating in the " +
                "[Jitter] line. Normal erratic flyers wobble ~6-10u/s; the pathological 'insane " +
                "jitter' is far higher. Lower to catch subtler cases, raise to only flag the worst.");

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

            FpsLimit = cfg.Bind("Video", "FpsLimit", 0,
                "Frame-rate cap. 0 = MAX (your monitor's refresh rate — the default); any other " +
                "value caps rendering at that many fps (minimum 60). Adjustable in-game on the " +
                "VIDEO options tab (new FPS LIMIT row). With VSYNC ON the display sync governs " +
                "and this cap is inert — that's Unity semantics, not a bug.");
            ResizableWindow = cfg.Bind("Video", "ResizableWindow", true,
                "Windowed mode only: make the game window freely resizable (drag edges, maximize " +
                "button). The game ships with a fixed-border window; this restores the standard " +
                "Windows frame. No effect in Borderless.");
            TrackerNames = cfg.Bind("Tracker", "Names", true,
                "Name label in the player's color above remote players' ships.");
            TrackerArrows = cfg.Bind("Tracker", "Arrows", true,
                "Screen-edge arrows in the player's color with name+distance while they're offscreen; hidden when visible.");
            Scoreboard = cfg.Bind("Tracker", "Scoreboard", true,
                "Hold Tab during a net run for the party scoreboard (HP, kills, deaths, distance).");
            ShipStatusBars = cfg.Bind("UI", "ShipStatusBars", true,
                "Show a small health (red) and fuel (blue) bar above other players' ships, so you " +
                "can read their condition in a fight. Fixed size — upgrades change how full the " +
                "bars are, not how big. Only drawn for ships on screen.");
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

            // Fresh key ON PURPOSE (was [Diag] LogUploadEndpoint, default empty): every existing
            // install has the empty value WRITTEN, and a file value beats a new bind default —
            // renaming the key is the only way the now-default endpoint reaches the fleet, so
            // testers' SEND LOGS actually lands in S3 instead of quietly saving locally. The
            // endpoint is hardened for public exposure: presigned single-object PUTs only,
            // 10 MiB cap, per-run stable keys (re-sends overwrite), client cooldowns, reserved
            // concurrency, and a budget kill-switch. The orphaned old key line is inert.
            LogUploadEndpoint = cfg.Bind("Diag", "LogUploadUrl",
                "https://57mjrwp6bts74pm7hsbv6rlgq40eekkq.lambda-url.us-east-1.on.aws/",
                "Signer endpoint for SEND LOGS / the `uploadlogs` devcmd (a Lambda Function URL; " +
                "see infra/diagnostics-s3-setup.ps1). The mod asks it for a short-lived presigned " +
                "S3 PUT URL for one exact object, then uploads — no AWS credentials in the mod and " +
                "no anonymous access on the bucket. Empty = collect only: `uploadlogs` still " +
                "gzips the log to <plugin>/diagnostics/<runId>/ and prints the path to send manually.");
            LogLevel = cfg.Bind("Diag", "LogLevel", "Normal", new ConfigDescription(
                "How chatty the log is. Normal = every warning and one-shot event, but the periodic " +
                "instrumentation blocks ([Frame]/[Counts]/[Population]/…) slow to every 30s and " +
                "[Profile]/[PatchProfile] SPIKE lines are rate-limited. Verbose = full-rate " +
                "instrumentation (set this when reporting a bug so the log carries fine-grained " +
                "data). Quiet = warnings and events only, no periodic blocks. Live-switchable with " +
                "the `loglevel <Normal|Verbose|Quiet>` devcmd.",
                new AcceptableValueList<string>("Normal", "Verbose", "Quiet")));
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
            PreGenerateWorld = cfg.Bind("Session", "PreGenerateWorld", true,
                "Dedicated coordinator only: build the next run's world while the lobby is idle " +
                "instead of when someone presses START. A Wine server generates in ~26s versus a " +
                "player's ~6s, and START waits for the slowest participant — pre-building moves " +
                "the server's cost to when nobody is waiting, so START only costs the players' " +
                "own ~6s. Legal because a dedicated server owns the seed (DIRECT CONNECT clients " +
                "never send one); if a party leader supplies a different seed, the pre-built " +
                "world is discarded and generation runs at START as before.");
            EnableGameModes = cfg.Bind("Session", "EnableGameModes", false,
                "Master switch for alternate game modes (currently Battle Royale). OFF means every " +
                "run is the normal co-op game: the GAME MODE row is hidden when you host, and a " +
                "dedicated server ignores its GameMode setting. Turn this on to make mode " +
                "selection available. Joining someone else's Battle Royale server still works " +
                "either way — the host decides the ruleset for its own runs.");
            GameMode = cfg.Bind("Session", "GameMode", "Standard",
                new ConfigDescription(
                    "Ruleset for runs this DEDICATED SERVER hosts (takes effect on restart). " +
                    "Standard = the normal co-op game. BattleRoyale = last player standing: " +
                    "scattered spawns at pre-opened stations, PvP on with reduced damage, a lava " +
                    "ring closing over the match, care packages, and placement screens (see " +
                    "docs/BATTLE_ROYALE.md). Players hosting their own game choose the mode on the " +
                    "GAME SETTINGS screen instead — this setting does not affect them.",
                    new AcceptableValueList<string>("Standard", "BattleRoyale")));
            BrMatchMinutes = cfg.Bind("Session", "BrMatchMinutes", 18,
                "DEPRECATED and no longer used for the ring schedule. Match length is now DERIVED " +
                "from BrRingStartMinutes + BrRingStages x (BrRingHoldMinutes + BrRingCloseSeconds) " +
                "and logged at match start — set the pacing you want and read the total, rather " +
                "than setting a total and discovering the pacing.");
            BrRingStartMinutes = cfg.Bind("Session", "BrRingStartMinutes", 2,
                "Battle Royale: minutes of grace before the ring starts closing (the first warning).");
            BrRingStages = cfg.Bind("Session", "BrRingStages", 6,
                "Battle Royale: how many closures the ring makes. The ring HALVES rather than " +
                "stepping evenly — from a start radius of 100 with 6 closures the ladder is " +
                "100, 80, 40, 20, 10, 5, 0 — so the late game accelerates on its own. Equal radius " +
                "steps feel slower and slower as the circle shrinks; halving keeps the pressure " +
                "proportional to how much room is left.");
            BrNextRingWarningSeconds = cfg.Bind("Session", "BrNextRingWarningSeconds", 60f,
                "Battle Royale: how long before a closure the NEXT zone (the amber circle) appears " +
                "on the map. It stays up through the closure and disappears once the real zone has " +
                "caught up to it, so between closures the map shows only where you actually are — " +
                "the amber circle means 'move', and it should not be permanent wallpaper that stops " +
                "meaning anything.");
            BrRingHoldMinutes = cfg.Bind("Session", "BrRingHoldMinutes", 5f,
                "Battle Royale: minutes the ring sits still between closures. THIS is the pacing " +
                "knob — total match length is derived from it (grace + closures x (hold + close)) " +
                "and logged at match start, rather than being configured and leaving the hold to " +
                "fall out of a division nobody can predict. BrMatchMinutes is no longer used for " +
                "the schedule.");
            BrRingCloseSeconds = cfg.Bind("Session", "BrRingCloseSeconds", 45f,
                "Battle Royale: how long ONE ring closure takes. The zone holds still between " +
                "closures and then draws in over this many seconds, evenly, so the pace is " +
                "predictable and the terrain painting stays light. Automatically shortened if a " +
                "stage is too brief to fit it (short test matches).");
            TrimTerrainPresentation = cfg.Bind("Perf", "TrimTerrainPresentation", true,
                "Skip tile/lightmap refresh for cells nobody is looking at. A coordinator renders " +
                "nothing, so it does none of it; a player's machine refreshes only cells the " +
                "tilemap renderer reports VISIBLE, because off-screen cells are already refreshed " +
                "by CellBecameVisible when they scroll in. Measured on a BR coordinator: " +
                "GroundTilemapUpdater.OnCellsChanged was 94% of all frame time (~0.6ms per changed " +
                "cell, 15k cells per 10s) and drove the host to 0.1fps as the ring closed. Turn " +
                "off only to prove a terrain-rendering bug is or is not this patch.");
            SegmentChangeRouting = cfg.Bind("Perf", "SegmentChangeRouting", true,
                "Hand each level segment only the terrain changes inside it, instead of vanilla's " +
                "every-segment-scans-every-change (which is O(segments x changes) per frame). " +
                "Measured on a Battle Royale coordinator: LevelChangeBuffer.Update was 99.3% of " +
                "all frame time at 979ms per call while the ring closed. Behaviour is identical - " +
                "the discarded changes failed the segment's own rect test anyway. Turn off only to " +
                "prove a terrain bug is or is not this patch.");
            BrChooseSpawn = cfg.Bind("Session", "BrChooseSpawn", true,
                "Battle Royale: let players pick which BIOME to drop into before the match places " +
                "them, with a live heat map of where everyone else is heading. Runs inside the " +
                "go-live barrier — the only moment the world exists and no ship does — so players " +
                "land on their chosen station with no teleport. Off spawns everyone by the " +
                "farthest-point scatter as before. A dedicated server/sidecar never chooses; its " +
                "clients still do.");
            BrChooseSpawnSeconds = cfg.Bind("Session", "BrChooseSpawnSeconds", 30f,
                "Battle Royale: seconds players get to choose a drop region. When it expires " +
                "anyone who has not chosen is given a random region — the timer is a decision, not " +
                "a punishment. The match cannot start until this resolves, so one idle player can " +
                "hold the lobby for this long.");
            BrSpawnProtectionSeconds = cfg.Bind("Session", "BrSpawnProtectionSeconds", 4f,
                "Battle Royale: seconds of invulnerability AFTER dropping in. You are already " +
                "untouchable for the whole time the drop screen is up — a player reading a menu " +
                "cannot defend themselves — and this extends it past the landing, because you " +
                "arrive somewhere you have never seen with no idea what is beside you. 0 disables " +
                "the post-landing grace; the while-choosing protection is not optional.");
            BrSpawnAtStationDirectly = cfg.Bind("Session", "BrSpawnAtStationDirectly", true,
                "Battle Royale: place each ship ON its own spawn station from the first frame, and " +
                "point the opening cinematic at that station, instead of spawning everyone on the " +
                "shared start pad and teleporting them apart a few seconds later. Off falls back to " +
                "the teleport (which also happens automatically if the direct placement fails).");
            BrStationUnlockDelaySeconds = cfg.Bind("Session", "BrStationUnlockDelaySeconds", 8f,
                "Battle Royale: seconds after go-live before every station is unlocked. NOT " +
                "cosmetic — the vanilla start cinematic identifies the station to pan to as 'the " +
                "one with an installed upgrade', so unlocking them all at go-live sends a client's " +
                "camera to a random station and then hangs the cinematic waiting for a GameObject " +
                "that is not streamed in, leaving the ship with no controls. This waits until every " +
                "machine has picked its start station. Lower it only if you enjoy that bug.");
            ShowZoneVisual = cfg.Bind("UI", "ShowZoneVisual", true,
                "Battle Royale: draw the closing zone as molten ground (UI/RingLavaVisual.cs). The " +
                "zone is RENDERED, not built out of terrain — it has no collider and deals no " +
                "contact damage, so you can always fly through it. Turning this off leaves the " +
                "damage exactly as it is and simply makes the zone invisible; the ring outlines on " +
                "the minimap and map screen are separate.");
            BrZoneKillSeconds = cfg.Bind("Session", "BrZoneKillSeconds", 60f,
                "Battle Royale: seconds to die from FULL health while caught in the zone during " +
                "the FIRST ring. The zone is not solid — you can always fly through it, which is " +
                "how a player avoids being walled in — so this is the price of a crossing, not a " +
                "death sentence. Scaled by max health, so an upgraded hull buys proportionally " +
                "more time. Raised by BrZoneDamageStageScale as the ring closes.");
            BrZoneDamageStageScale = cfg.Bind("Session", "BrZoneDamageStageScale", 0.75f,
                "Battle Royale: how much harder the zone bites per completed shrink stage. Damage " +
                "is multiplied by (1 + stage * this), so at the default 0.75 the opening zone takes " +
                "BrZoneKillSeconds to kill and the 8th ring takes about an eighth of that. The " +
                "early zones are meant to be survivable and the late ones are not.");
            BrCarePackageMinutes = cfg.Bind("Session", "BrCarePackageMinutes", 3,
                "Battle Royale: minutes between care-package waves (0 disables them). The FIRST " +
                "wave lands at half this, so a short match still sees one. Each package is " +
                "destructible; only the player who destroys it gets the loot.");
            BrCarePackagesPerWave = cfg.Bind("Session", "BrCarePackagesPerWave", 2,
                "Battle Royale: how many packages drop in each wave, scattered independently " +
                "across the safe zone (Omar, 2026-07-29: 'I would like more air drops throughout " +
                "the world').");
            BrRingFirstHoldMinutes = cfg.Bind("Session", "BrRingFirstHoldMinutes", 2f,
                "Battle Royale: minutes the ring holds before its FIRST closure (later closures " +
                "use BrRingHoldMinutes). The opening hold plays differently from the rest — " +
                "everyone is looting, nobody is contesting ground — and a full-length first hold " +
                "read as 'the ring never closes' in short matches. Clamped to BrRingHoldMinutes.");
            PvPDamageScale = cfg.Bind("Session", "PvPDamageScale", 0.25f,
                "Battle Royale: multiplier on player-vs-player damage. Late-game weapons would " +
                "otherwise one-shot other players; damage to ENEMIES is unaffected.");
            BrEnemyHpScale = cfg.Bind("Session", "BrEnemyHpScale", 0.5f,
                "Battle Royale: enemy max-health multiplier. 0.5 makes players effectively deal " +
                "double damage to enemies, so kills and gold come twice as fast.");
            BrStationHazardClearRadius = cfg.Bind("Session", "BrStationHazardClearRadius", 14f,
                "Battle Royale: world units of DAMAGING TERRAIN (lava, gas, anything with contact " +
                "damage) scrubbed off the ground around every shop at match start. World generation " +
                "will happily put lava against a station — fine in co-op where you arrive on your " +
                "own terms, unfair when that station is a SPAWN. Deliberately small: a landing pad, " +
                "not a cleared arena, and every cleared cell is a replicated terrain diff. 0 " +
                "disables it and leaves the world exactly as generated.");
            BrSpawnClearRadius = cfg.Bind("Session", "BrSpawnClearRadius", 60f,
                "Battle Royale: world units of clear ground around every player's spawn station. " +
                "Enemies inside that circle are removed at match start so nobody opens the match " +
                "already being shot at. 0 disables the clear.");
            BrWinnerSelfDestructSeconds = cfg.Bind("Session", "BrWinnerSelfDestructSeconds", 10f,
                "Battle Royale: seconds the winner gets to enjoy the victory before their ship " +
                "self-destructs and the run ends. Stops a won match from becoming a private " +
                "sandbox nobody else can leave. 0 = no self-destruct (the run still ends).");
            BrMinPlayers = cfg.Bind("Session", "BrMinPlayers", 2,
                "Battle Royale: players required to START a match. 1 is allowed for testing " +
                "(logged as a warning) — a solo match ends as soon as it begins.");
            ServerFrameRateCap = cfg.Bind("Session", "ServerFrameRateCap", 120,
                "Dedicated coordinator only: cap the server's frame rate. Headless Unity runs " +
                "UNCAPPED otherwise (1000+ fps idling in the lobby, pure wasted CPU). 120 keeps " +
                "the 20Hz state pipeline and transport drain responsive (<=8ms poll latency) at a " +
                "fraction of the cost. 0 = uncapped.");
            EmptyServerResetSeconds = cfg.Bind("Session", "EmptyServerResetSeconds", 120f,
                "Dedicated server only: if a run is in progress and NO players have been connected " +
                "for this many seconds, end the abandoned run and return the server to a fresh " +
                "lobby so the next joiner can START a new game. The grace window leaves room for a " +
                "crashed player to rejoin-in-place first. 0 disables (an abandoned run then " +
                "simulates forever). Party wipes need no timer — all players dead ends the run " +
                "within seconds via the normal wipe path.");
            CoordinatorMode = cfg.Bind("Session", "CoordinatorMode", false,
                "EXPERIMENTAL (server sidecar): run this process as a dedicated shipless coordinator " +
                "— it hosts and runs the correctness plane but plays nobody and simulates nothing. " +
                "Implies AutoStart=Host/AutoReady/AutoLaunchRun (waits for at least one real player). " +
                "Intended for headless use (-batchmode -nographics); can also be forced with the " +
                "PUNKMV_COORDINATOR=1 environment variable.");
            ClockGuardEnabled = cfg.Bind("Sync", "ClockGuard", true,
                "While a net session is active and this game window is UNFOCUSED, temporarily " +
                "swap vsync for a frame-rate cap at your display's own refresh rate, restoring " +
                "your exact settings the moment you tab back in. WHY: with vsync on, an " +
                "unfocused window's game clock advances a fixed 1/refresh per frame regardless " +
                "of real frame time — under load the whole simulation runs slow (measured 0.4x " +
                "real time on a 240Hz display), its snapshots fall behind, and every OTHER " +
                "player sees enemies vibrate/stutter (chronic interpolation underruns). The " +
                "swap keeps the clock honest at any refresh rate and is invisible: it only ever " +
                "applies while you are not looking at the game. Disable ONLY if it misbehaves " +
                "with your driver/display setup — a [Clock] warning in your log plus teammates " +
                "reporting stutter while you were tabbed out means this instance is the cause.");

            // [Sync] section + fresh key ON PURPOSE (was [Diag] SummaryHeal): the v1 entry
            // shipped default-false, so every existing install has "SummaryHeal = false"
            // WRITTEN in its config, and a file value beats a new bind default. Renaming the
            // key is the only way "on by default" actually reaches the existing fleet; the
            // orphaned [Diag] line is inert. Keep this key as the emergency kill-switch.
            SummaryHeal = cfg.Bind("Sync", "SummaryHeal", true,
                "WS9.1 v3: segment identity-summary mismatches trigger targeted roster audits " +
                "(echo + repair), so silent world divergence — an enemy existing on one screen " +
                "but not another — self-heals in bounded time. Membership is ASSIGNMENT-based " +
                "(the owner's own segment assignment, echoed to viewers as the snapshot group " +
                "key), which eliminated every position-inference false-positive class the v1/v2 " +
                "predicates hit (fringe staleness, boundary-band skew, idle-at-threshold). " +
                "Repairs both directions: missing entities materialize from the owner's roster; " +
                "ghosts (live here, absent from the owner) are removed after 3 consecutive " +
                "audits inside this viewer's fresh zone. Off = detection-only telemetry " +
                "(summaries=tx/chk/miss on [BytePlanes]) with no repair traffic.");
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
        }
    }
}
