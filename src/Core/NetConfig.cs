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

        public static ConfigEntry<string> ModManifestPolicy;
        public static ConfigEntry<float> EnemyHealthScalePerPlayer;
        public static ConfigEntry<bool> AutoUpdate;

        public static ConfigEntry<string> AutoStart;
        public static ConfigEntry<bool> AutoReady;
        public static ConfigEntry<bool> AutoLaunchRun;
        public static ConfigEntry<float> AutoFly;

        public static ConfigEntry<bool> TrackerNames;
        public static ConfigEntry<bool> TrackerArrows;
        public static ConfigEntry<bool> ShareMapExploration;
        public static ConfigEntry<bool> Scoreboard;

        public static ConfigEntry<float> StateHz;
        public static ConfigEntry<float> ShipStateHz;
        public static ConfigEntry<float> AuthorityRadius;
        public static ConfigEntry<float> TransferRadius;
        public static ConfigEntry<float> InterestRadius;

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
                "50 ms, and puppets buffer two intervals — raising this lowers their visual delay " +
                "at a bandwidth cost proportional to nearby entity count.");
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
        }
    }
}
