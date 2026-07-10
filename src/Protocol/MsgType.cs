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

        // ---- Channel 1: unreliable state ----
        Ping = 20,
        Pong = 21,
        ShipState = 22,     // 20 Hz per-player ship snapshot
        EntityState = 23,   // 12 Hz batched entity snapshots (authority -> all)
        FireEvent = 24,     // ship weapon fired (visual replay on peers)
        EntityFire = 25,    // enemy/minion/boss weapon fired (visual replay on peers)
        EntitySpawned = 26, // (ch2) generic runtime entity spawn (boss adds, spawner enemies)
        ShipDash = 27,      // ship dashed (visual/audio replay on peers)

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
    }
}
