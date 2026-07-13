namespace PunkMultiverse.Protocol
{
    /// <summary>Leading byte of every payload. Grouped by the channel they normally travel on.</summary>
    public enum MsgType : byte
    {
        None = 0,

        // ---- Channel 0: reliable control ----
        Hello = 1,          // client -> host: version handshake
        Welcome = 2,        // host -> client: accepted, slot assignment + roster
        Reject = 3,         // host -> client: refused (version mismatch, full, ...)
        LobbyState = 4,     // host -> all: full roster snapshot (names, colors, ready)
        PlayerJoined = 5,
        PlayerLeft = 6,
        StartRun = 7,       // host -> all: seed + options
        LevelReady = 8,     // client -> host: level generated + checksum
        Manifest = 9,       // host -> all: netId assignment chunks
        GoLive = 10,        // host -> all: everyone verified, start playing
        RejoinState = 11,   // host -> rejoiner: cell ledger, upgrades, kills, grids
        SetLobbyPrefs = 12, // client -> host: my color / ready state
        SessionEnded = 13,  // host -> all: deliberate session end (not a connection loss)
        Kicked = 14,        // host -> one client: removed from the lobby by the host
        RunEnded = 15,      // host -> all: party wiped — back to the lobby, session intact

        // ---- Channel 1: unreliable state ----
        Ping = 20,
        Pong = 21,
        ShipState = 22,     // 20 Hz per-player ship snapshot
        EntityState = 23,   // legacy single-segment entity snapshots
        FireEvent = 24,     // ship weapon fired (visual replay on peers)
        EntityFire = 25,    // enemy/minion/boss weapon fired (visual replay on peers)
        EntitySpawned = 26, // (ch2) generic runtime entity spawn (boss adds, spawner enemies)
        ShipDash = 27,      // ship dashed (visual/audio replay on peers)
        EntityStateBundle = 28, // coalesced multi-segment snapshots, interest-routed by host

        // ---- Channel 2: reliable events ----
        DamageRequest = 40, // non-authority -> authority (routed via host)
        EntityKilled = 41,
        ShipDied = 42,
        ShipResurrected = 43,
        CellDiff = 44,      // destructible terrain changes
        AuthAssign = 45,    // host -> all: entity authority assignment
        AuthRelease = 46,   // client -> host: giving up authority
        MinionSpawned = 47,
        ModuleGridState = 48,
        StationUpgrade = 49,
        ScannerUsed = 50,
        GameOver = 51,
        GameWon = 52,
        FogDiff = 53,       // shared map exploration (fogLevels runs)
        InstrumentUsed = 54,
        HookState = 55,     // grappling hook attach/detach (visual)
        TerrainSync = 56,   // host -> one client: terrain chunk stream begin/end markers
                            // (the chunks themselves travel as ordinary CellDiff messages)

        MapDiscovered = 59, // any player permanently revealed a station/POI on the map (overdrawn icon)

        SegmentLease = 60,     // host -> all: prepare/commit a segment simulator + epoch
        SegmentLeaseAck = 61,  // selected simulator -> host: prepare is installed
        KillLedger = 62,       // host -> all: periodic canonical entity-death reconciliation
        EntityBaseline = 63,   // host -> client: canonical generation-time entity positions
        TerrainDigest = 64,    // host -> clients: canonical terrain-ledger revision/hash
        TerrainRepairRequest = 65,
        TerrainRepairChunk = 66,

        // ---- Channel 0 (control): entity identity reconciliation ----
        IdResolveRequest = 57, // client -> host: netIds my manifest couldn't match
        IdResolveReply = 58,   // host -> client: their entity type + position, for
                               // type+nearest-position matching against local orphans
    }
}
