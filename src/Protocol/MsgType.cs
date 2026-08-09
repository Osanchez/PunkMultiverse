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
        DirectRoutePulse = 29,  // viewer -> host: direct owner snapshot route is alive

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
        SegmentLeaseAck = 61,  // RETIRED — nothing ever sent it; commits ride RuntimeBaselineAck
        KillLedger = 62,       // host -> all: periodic canonical entity-death reconciliation
        EntityBaseline = 63,   // host -> client: canonical generation-time entity positions
        TerrainDigest = 64,    // host -> clients: canonical terrain-ledger revision/hash
        TerrainRepairRequest = 65,
        TerrainRepairChunk = 66,
        EntityStarvedRequest = 67, // viewer -> host: visible puppet has no servicing simulator
        EntityAuthorityPrepare = 68, // host -> candidate: prove exact netId is still instantiated
        EntityAuthorityAck = 69,     // candidate -> host: exact netId is ready for simulation
        PlantFruitKilled = 70,       // stable (plant netId, generated fruit id) child destruction
        RuntimeBaselineRequest = 71, // host -> current owner: full state for interest/handoff
        RuntimeBaseline = 72,        // owner -> target (host-routed): reliable full segment state
        RuntimeBaselineAck = 73,     // target -> host: baseline installed; deltas/commit may begin
        DirectRoute = 74,            // host -> owner: add/remove direct owner->viewer state route
        EntityBoundaryHandoff = 75,  // reliable final state when a mobile entity crosses lease cells
        DamageUnservable = 76,       // owner -> host: I own this target but have no live object;
                                     // queue the claim and hand the segment to the attacker
        ResidencyReport = 77,        // peer -> host: full set of segments my game is streaming
        SegmentDormancyCommit = 78,  // owner -> host -> all: final entity states for a segment
                                     // the owner is unloading (the release edge, I-10)
        DormantState = 79,           // host -> rejoiner: canonical state cache replay chunks
        SegmentRosterAudit = 80,     // owner -> host -> all: periodic identity roster for an
                                     // owned segment; receivers detect and heal world-database
                                     // divergence (entity data missing behind a live identity)
        ProjectileDetonate = 81,     // owner -> all: a real projectile exploded (identity + pos);
                                     // peers consume their visual copy so it can't fly through a
                                     // host-cleared block and detonate a second time downrange
        ProjectileState = 82,        // owner -> all: heavy-ordnance (rocket/mine/bomb) flight state
                                     // (identity + pos + vel); peers snap their visual copy to the
                                     // authority's path and dead-reckon between updates (WS1.1)
        LinkHealth = 83,             // viewer -> host -> all: 1-byte receive-quality score; owners
                                     // map it to that viewer's presentation byte budget (WS7.2)
        SegmentStateSummary = 84,    // owner -> all: cheap per-segment identity hash at 0.2 Hz;
                                     // a viewer echoes it back on confirmed mismatch and the owner
                                     // answers with a targeted roster audit (WS9.1 Merkle-lite)
        EventSeqCheckpoint = 85,     // host -> client, per reliable channel: "N messages preceded
                                     // this one" — ordered delivery makes it a barrier; a deficit
                                     // at arrival is silent loss (outbox drop, migration gap) (WS8.2)
        EventGapReport = 86,         // client -> host: checkpoint deficit detected; host answers
                                     // with the idempotent SendEventCatchUp state replay (WS8.2)
        PartyLeaderSettings = 87,    // party leader -> coordinator (in lobby): the run seed +
                                     // friendly-fire + hp-scaling the hosting player chose, so a
                                     // shipless coordinator hosts the world THEY picked (sidecar parity)
        LobbyMembers = 88,           // party leader -> coordinator: the SteamID64 set of the discovery
                                     // lobby's current members. A shipless coordinator can't see the
                                     // Steam lobby, so the leader relays membership; the coordinator
                                     // gates incoming HELLOs against it (sidecar lobby-gated joins #2)
        AdminGrant = 89,             // coordinator -> one player: your private capability token. The
                                     // first real player to connect becomes session admin; the token
                                     // authorizes host-like commands over an untrusted transport, so it
                                     // works for the standalone server (no reliance on Steam identity)
        AdminCommand = 90,           // admin player -> coordinator: token + command (start run / kick).
                                     // The server performs it only if the token matches the grant, so a
                                     // modded client that force-enables the buttons is still refused

        // ---- Channel 0 (control): Battle Royale (docs/BATTLE_ROYALE.md) ----
        Announce = 91,       // host -> all: show this text as a toast. The mod had no way for a
                             // server to speak to every client; BR needs it for ring warnings and
                             // elimination callouts, and it generalizes (MOTD, admin notices)
        RingState = 92,      // host -> all: lava-ring center/radius/stage + next-shrink time. Drives
                             // the ring HUD and each client's own out-of-zone burn damage (ship HP
                             // is owner-authoritative, so the victim applies it locally)
        Placement = 93,      // host -> all: a player's final placement on elimination, plus how many
                             // remain — elimination toasts and the "YOU PLACED #N" screen
        CarePackage = 94,    // host -> all: a supply drop spawned at x,y with this netId — arrow
                             // target for everyone; whoever destroys it gets the loot
        LootClaim = 95,      // client -> host: I am picking up the loot identified by (group,
                             // ordinal). BR loot is CONTESTED — one pile, one taker — so a pickup
                             // is a request, not a fact (docs/BATTLE_ROYALE.md)
        LootClaimed = 96,    // host -> all: that loot went to this slot. First claim wins; everyone
                             // else destroys their copy of it

        // ---- Channel 0 (control): Battle Royale drop selection (pre-go-live) ----
        SpawnChoice = 97,    // client -> host: I am dropping in this biome
        SpawnTally = 98,     // host -> all: how many players have picked each biome so far. Drives
                             // the heat colouring; deliberately a COUNT the UI turns into
                             // green/amber/red rather than a number shown to players
        // (99 was SpawnAssign — retired. The host no longer assigns stations: players may share a
        //  pad, so there is nothing to agree on, and a player deploys the instant they pick rather
        //  than waiting for the host to answer.)
        SpawnClear = 100,    // deployer -> host -> all: the exact netIds this player silently
                             // removed from their landing pad at deploy. The go-live sweep can't
                             // cover this (a drop lands up to 30s later, and enemies wander back),
                             // and a deploy is one machine's moment — so unlike the go-live clear
                             // the SET must travel, or the removals become ghosts everywhere else

        // ---- Channel 0 (control): forensics ----
        DiagMark = 30,       // any peer -> all: "I just lost sight of this netId; print what YOU
                             // see for it, right now." One machine's view of a vanished enemy is
                             // an absence; the pair of views is a diagnosis (Sync/EntityForensics)

        // ---- Channel 0 (control): entity identity reconciliation ----
        IdResolveRequest = 57, // client -> host: netIds my manifest couldn't match
        IdResolveReply = 58,   // host -> client: their entity type + position, for
                               // type+nearest-position matching against local orphans

        // ---- Channel 2 (events): host-served content ----
        // Bulk on purpose. Control must stay clear of anything a transfer can queue behind — a
        // content message must never be able to delay a Welcome or a StartRun — and Events is
        // the lane already documented as reliable and bulk-tolerant, whose backpressure contract
        // (Send returning false) the terrain streamer already relies on.
        ContentOffer = 101,  // host -> client: set hash + the file list, chunked
        ContentNeed = 102,   // client -> host: the digests I lack, with resume offsets, chunked
        ContentChunk = 103,  // host -> client: (digest, offset, bytes, last)
        ContentDone = 104,   // client -> host: installed and verified, or failed with a reason
        ContentStatus = 105, // client -> host: progress, for the host's lobby roster

        // A HELD weapon's trigger state. FireEvent replicates discrete SHOTS, which is right for a
        // projectile -- the replay spawns one and it flies on carrying its own visual. A beam has
        // no such object: HitscanWeapon.OnBarrelMoved draws it every frame ONLY while
        // IsTriggerPulled, and a puppet's trigger is never pulled, so the else branch actively
        // switches the beam off. The owner fired 196 casts and the observer replayed 152 of them
        // and drew nothing at all, which is not a delivery problem -- it is the wrong thing being
        // delivered.
        HeldFire = 106,      // owner -> peers: trigger down/up plus aim, for continuous weapons
    }
}
