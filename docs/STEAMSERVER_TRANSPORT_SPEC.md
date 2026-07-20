# SteamServerTransport — spec (v1)

`Transport = SteamServer`: the coordinator holds an **anonymous game-server Steam identity**
and remote players connect to it over Steam networking — SDR NAT traversal, encryption, and
IP privacy for free, no port forwarding. This is the friends-facing deployment of the server
sidecar. Proven end-to-end by spike A (`SteamServerSpike.cs`, commit bdbadc9): logon grants a
universe-90 server SteamID on this appid, and a user client's `ConnectP2P` to that identity
round-trips messages over SDR.

Everything above the transport line (session, leases, sequencer, budgets) is untouched — this
is the third `ITransport` implementation, selected by the existing single-config resolver
(`NetSession.ResolvedTransport`).

## 1. Facts the design rests on (verified against the bundled Steamworks.NET)

- `GameServer.Init(uint ip, ushort gamePort, ushort queryPort, EServerMode, string version)`
  — appid resolves ONLY via `SteamAppId`/`SteamGameId` env vars (no steam_appid.txt in this
  install); set them in-process before Init (`SteamBootstrap` precedent).
- The callback dispatcher exists only AFTER `GameServer.Init` — register `CreateGameServer`
  callbacks after Init or `RunCallbacks` self-poisons ("Callback dispatcher is not
  initialized", spike attempt 1).
- There is **no `SteamGameServerNetworkingMessages`** — the server side is connection-oriented
  `SteamGameServerNetworkingSockets` only. Client side uses user `SteamNetworkingSockets`.
- Poll groups exist on both contexts (`CreatePollGroup` / `SetConnectionPollGroup` /
  `ReceiveMessagesOnPollGroup`) — use them for O(1) receive calls server-side.
- `SteamGameServerNetworkingUtils` exists — apply the same raised send limits as
  `SteamBootstrap.RaiseSendLimits` does for the user context.
- Send flags are the same constants the Messages transport already uses
  (`k_nSteamNetworkingSend_Reliable`=8, `ReliableNoNagle`=9, `UnreliableNoNagle`=1).
- **Lanes are OFF the table**: `ConfigureConnectionLanes` is bound with `out int/out ushort`
  where the native API takes arrays — unusable beyond one lane without unsafe marshaling.
  Head-of-line isolation comes from dual connections instead (§3).
- Listen-server coexistence (client SteamAPI + GameServer API in one process) is proven —
  the spike ran both in the coordinator.

## 2. Architecture

**One class, `src/Transport/SteamServerTransport.cs`, both roles** (mirrors
`LoopbackUdpTransport`): `StartHost()` = game-server role; `StartClient(address)` = user role,
`address` = the server's SteamID64 as a decimal string.

**Process-level `GameServerBootstrap`** (static, mirrors `SteamBootstrap`): owns
`GameServer.Init` + `LogOnAnonymous` + the logon callbacks + `LogOff/Shutdown` on quit.
Init-once semantics: a stopped/restarted session REUSES the logged-on server identity
(GameServer.Init cannot be cleanly re-initialized in-process). Exposes
`ServerSteamId` (0 until logged on) and `LoggedOn`. The transport's `StartHost` blocks on
neither: it starts the bootstrap if needed and reports `IsRunning=false` until logon; the
session's existing connect/retry flows tolerate that (sidecar join already retries 90s).

**Identity plumbing** (how joiners learn the server id):
- On logon, the coordinator writes the id to `<plugin>/coordinator-steamid.txt` AND logs it.
- The hosting player's sidecar join flow (`JoinSidecarWhenUp`) reads that file (retry until
  present) instead of the loopback address when the sidecar transport is SteamServer.
- Remote friends: `JoinByCode` already falls through to "address" semantics — a pasted
  17-digit SteamID64 is the join code for v1. The lobby screen shows it as the SERVER CODE
  when the local session is a SteamServer session. (Steam-lobby metadata discovery = later.)

**Config/env plumbing**:
- `Transport` AcceptableValueList grows: `Steam | Loopback | SteamServer`.
- `SidecarLauncher` passes `PUNKMV_TRANSPORT=SteamServer` to the child when the player's own
  `Transport=Steam` (Steam-capable machine), else `PUNKMV_TRANSPORT=Loopback` (today's local
  behavior). `NetSession.ResolvedTransport` becomes:
  - coordinator: `PUNKMV_TRANSPORT` env value, default `Loopback`;
  - `_sidecarSession`: the value the launcher chose (stored on spawn);
  - otherwise: config.
- Everyone in a SteamServer session uses SteamServer — including the hosting player on the
  same machine (Steam routes same-host efficiently; no split-transport sessions in v1).

## 3. Wire design — dual connections per peer

The Combat channel exists to never queue behind a terrain burst. A single reliable Sockets
stream would head-of-line block Combat behind Events (up to the 512KB send buffer ≈ seconds).
Therefore **two connections per peer**, by virtual port:

| vport | name | carries | why together |
|---|---|---|---|
| 0 | bulk | Control, Events | both bulk-tolerant reliable; merging them only STRENGTHENS ordering |
| 1 | fast | Combat, State | Combat = reliable-no-nagle on its own uncongested stream; State = unreliable (never blocks anything, rides the low-latency path) |

- **Framing**: 1-byte channel prefix + payload (the LoopbackUdpTransport pattern), so 4
  logical channels survive on 2 connections. `MaxMessagesPerPoll`-style pumping reuses the
  Messages transport's marshal/Release pattern.
- **Flags per channel**: Control/Events → Reliable(8); Combat → ReliableNoNagle(9);
  State → UnreliableNoNagle(1). Same mapping as today.
- **Ordering contract**: WS8.2 barriers need per-channel reliable ordering. Each reliable
  channel lives inside exactly one ordered stream → holds (Control+Events sharing a stream is
  strictly stronger than today's independent channels — safe). `[Seq]=0` in tests is the
  proof gate.
- **Server side**: two listen sockets (vport 0, 1); ONE poll group for all connections
  (the channel byte + per-connection peer mapping disambiguate). Map
  `HSteamNetConnection -> (peerSteamId, vport)` on accept; `peerId -> {bulk, fast}` for send.
- **Client side**: two `ConnectP2P(identity, vport)` calls; receive via
  `ReceiveMessagesOnConnection` on both (two conns — poll group optional client-side).
- **PeerConnected** fires when BOTH connections of a pair reach Connected;
  **PeerDisconnected** when either drops (then close both). A pair that half-connects and
  stalls >15s is torn down.

## 4. Send / receive semantics (ITransport contract)

- `Send(peer, channel, data, reliable)`: pick conn by channel's vport; frame = [channel][data];
  `SendMessageToConnection` with the channel's flags. Return false on
  `k_EResultLimitExceeded` / invalid conn (backpressure contract — the outbox and terrain
  streamer depend on it). Ignore the `reliable` arg mismatch the same way Messages does
  (flags derive from channel).
- `Poll()`: pump `GameServer.RunCallbacks()` (server role) — the user context is pumped by
  the game/SteamBootstrap already; drain the poll group / connections; dispatch via
  `DataReceived(peer, channel, segment)` after stripping the channel byte. Inline receive in
  v1 (no ThreadedReceive parity yet — note it; ReceiveBudgetMs drain still bounds dispatch).
- `Stop()`: close connections (with linger flag 0), close listen sockets/poll group, clear
  maps. Does NOT log off the game server (process-level bootstrap owns that; quit hook does).
- `NetSeq`/`NetStats` hooks: call `NetStats.AddOut(channel, …)` + `NetSeq.NoteSent` on
  accepted sends, exactly like both existing transports (WS8.2 counting is transport-level).

## 5. Failure modes

- **Logon failure / timeout** (30s): StartHost marks the transport failed → session `Fail` →
  sidecar path falls back to classic in-process hosting (existing fallback in `HostOnline`).
- **Mid-session logon drop** (`SteamServersDisconnected_t`): log loudly; keep connections
  (SDR sessions generally survive brief backend blips); if connections start dying, the
  normal peer-timeout / client-side host-loss machinery takes over (migration among clients
  — already exists and was the pre-sidecar behavior; nothing new to build).
- **AllowPeer**: v1 accepts all incoming connections (playtest posture; the session-layer
  HELLO + mod-manifest check still gates actual joins). Follow-up: allowlist by lobby/friends.
- **Two coordinators on one Steam client**: unsupported; second `GameServer.Init` in another
  process on the same machine may fail → clean StartHost failure path above.

## 6. Out of scope (v1)

Server browser/A2S responses, VAC (`eServerModeAuthentication` without Secure), Steam-lobby
discovery for server sessions, ThreadedReceive parity, allowlists, Docker (that's the
LiteNetLib "Udp" transport, planned separately per the A-then-C decision).

## 7. Test plan

- **T1 — same-machine 3-proc, all-SteamServer** (extends scenario #29): player game
  (`Transport=Steam`, `HostViaSidecar=true`) spawns sidecar with `PUNKMV_TRANSPORT=SteamServer`;
  player joins via coordinator-steamid.txt; OD Test2 joins via `steamconnect`-style code
  (config `Transport=SteamServer`, join code = server id). Gates: 3-way GO LIVE + checksum
  parity; **[Seq]=0** (ordering contract on the new wire); `terrainMismatch=0`;
  kill-the-player-game survival (scenario #29's headline gate); zero exceptions.
- **T2 — Combat-vs-bulk isolation**: during the rejoin catch-up burst (the heaviest Events
  load we can generate), `[SnapshotLatency] relayAvg/relayMax` and damage round-trips must not
  spike with the burst (that's the dual-connection design earning its keep). Compare against
  a Loopback run of the same phase.
- **T3 — field**: a real remote friend joins via server code. First genuinely-remote sidecar
  session. (Needs a second machine/person; not automatable here.)
- Soak bot runs unchanged on Loopback — SteamServer soak variant only after T1/T2 pass.

## 8. Implementation checklist (ordered)

1. `GameServerBootstrap` (extract/replace the server half of `SteamServerSpike`): env appid,
   Init-once, logon callbacks, `ServerSteamId`, id-file write, quit LogOff, send limits via
   `SteamGameServerNetworkingUtils`.
2. `SteamServerTransport` skeleton: role split, maps, events, Stop.
3. Server accept path: listen sockets ×2, poll group, conn→(peer,vport) on accept, pair
   tracking → PeerConnected/Disconnected.
4. Client connect path: dual ConnectP2P, pair tracking, host peer id = server id.
5. Send path: framing + flags + backpressure + NetStats/NetSeq hooks.
6. Receive path: poll-group drain (server) / per-conn drain (client), channel strip, dispatch.
7. Wiring: config list value, `ResolvedTransport` env/sidecar plumbing, `SidecarLauncher`
   PUNKMV_TRANSPORT, `JoinSidecarWhenUp` id-file read, lobby-screen SERVER CODE display.
8. T1 → T2, fix, then commit. Keep `SteamServerSpike` (env-gated) until T1 passes, then
   delete it — the bootstrap supersedes it.

Reference code: `SteamServerSpike.cs` (working both-ends skeleton), `SteamMessagesTransport`
(marshal/Release, flags, backpressure, AllowPeer patterns), `LoopbackUdpTransport` (channel
framing, role split, paced sends).

## 9. Risks

- **SDR quota/appid policy**: anonymous servers on a playtest appid worked today; Valve-side
  policy could change. Cheap detection: the logon callbacks; clean fallback: classic hosting.
- **Same-machine SDR loopback latency** for the hosting player (+ a few ms vs raw loopback):
  measure in T1 (`RttMs` on the roster); if it matters, split-transport sessions are the
  v2 escape hatch (deliberately out of v1).
- **Binding gaps**: everything used is verified present in the bundled dll (§1); no new
  native marshaling beyond patterns already shipped in `SteamMessagesTransport`.
