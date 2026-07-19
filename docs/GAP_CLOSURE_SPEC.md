# Gap-Closure Spec — remaining work to harden co-op sync

Status: **proposal / roadmap** (nothing here implemented). Baseline: v0.1.109.
Companion docs: `ENTITY_SYNC_ARCHITECTURE.md` (the model), `DESYNC_CONVERGENCE_PLAN.md`
(RC1/RC2, shipped in 0.1.109), `test-scenarios.md` (verification catalog).

## 0. Where we stand

v0.1.109 closed the catastrophic class: the host freeze (dormant-claim drain), the
convergence storms (RC1 interest gating + RC2 lease hysteresis — authChurn 5-20/s → 0,
coordinator-cache fallbacks 101 → 0), distant loot/gold starvation, double detonations,
shop deaths, drone HP display, rejoin lockout, pause lockup. What remains is the
**residual artifact class inherent to distributed-authority P2P**: brief ghosts at
transition edges, projectile flight divergence, articulated-body wobble, and a handful of
diagnosed-but-unfixed correctness holes. This spec enumerates every known remaining item,
with design, acceptance criteria, and effort.

### Reality check vs. Noita Entangled Worlds (NEW)

NEW (IntQuant/noita_entangled_worlds) validates the architecture family we chose:
per-entity/area authority delegated between peers (`give_authority_to: PeerId`),
host/client world-update split, chunked pixel-world sync, item "globalization" with
peer authority — structurally the same shape as our segment leases + residency +
host-ledger terrain. Two honest observations from studying it:

- **They have not eliminated this artifact class either.** Community reports the same
  enemy jitter/desync family. Their README documents *what* syncs, not a solved-desync
  story. The remaining gap is not "NEW knows a trick we don't."
- **Where we are ahead:** deterministic generation with a verify-or-abort go-live barrier
  (4-part checksums — NEW has no equivalent gate), dormancy as a first-class protocol
  state with commit/grace semantics, an instrumented convergence loop (residency-gated
  interest, event-driven wakes), a main-thread hitch watchdog, and a scripted two-instance
  harness with a PASS-criteria scenario catalog. NEW's architectural edge on us: the
  **proxy split** (netcode runs in a separate Rust process, isolating transport from the
  game's frame loop) — our equivalent risk is mitigated differently (threaded receive +
  budgeted drain), and a process split is not worth the migration for what remains.

Conclusion: we close the gap by finishing our own diagnosed list, not by imitation.

---

## WS1 — Projectile flight coherence (the biggest *visible* residual)

**Problem.** Projectiles replicate by re-simulation (FireEvent → local replay). Copies
diverge in flight: interpolated muzzle origin, terrain differences mid-flight, timing.
0.1.109 pinned the *endpoint* for explosives (authoritative detonate + dedup); flight
in between is still N independent guesses, and non-explosive projectiles have no
authoritative endpoint at all (victim-side hit detection covers damage, not visuals).

**1.1 Heavy-ordnance flight sync (state, not re-sim).** For slow/long-lived projectiles
(rockets, mines, bombs — anything with lifetime > ~1.5s or speed < ~25 u/s, detected at
`StampProjectile`), the owner streams position/velocity at CombatStateHz on the State
channel, keyed by the existing `NetworkProjectileIdentity`. Peers snap their visual copy
to the stream (dead-reckon between updates) instead of free-flying it. Fast bullets stay
re-simulated (invisible divergence at their speeds; per-bullet streaming is the bandwidth
cliff we must not walk off).
- Files: `ProjectileSync.cs` (new `ProjectileStateMsg`, owner tick, peer apply),
  `Messages.cs`, `MsgType.cs`.
- Accept: harness — rocket fired past a peer lands within 1u of the owner's blast point
  on both screens (`[Diag:Detonate] applied` position delta logged < 1u); no bandwidth
  regression in `[Counts]` at 4 players (< +2 KB/s per active shooter).
- Effort: M. Risk: low (additive channel; visual copies already exist).

**1.2 Visual-projectile TTL GC.** A peer's visual copy whose detonate event is lost (or
whose owner despawned it silently by range) currently lives until vanilla lifetime.
Add a TTL: if a stamped visual projectile outlives its weapon's expected lifetime +2s,
destroy it quietly. Closes the "stray phantom bullet keeps flying" tail.
- Files: `ProjectileSync.cs` (sweep in the existing tick).
- Accept: soak scenario — `VisualProjectileCount` returns to ~0 within 5s of a cease-fire.
- Effort: S.

**1.3 Beam/contact attack visual replication (RC4 completion).** Fire events are
unreliable-broadcast; under load the victim can take routed damage with no visible
attack. Send the enemy `EntityFireMsg` **reliably to the targeted victim only**
(`TargetSlot != 255`), unreliable to everyone else. Deferred from 0.1.109 to avoid
re-inflating the message storm — now safe post-RC1/RC2 (churn ≈ 0).
- Files: `ProjectileSync.cs:~548`.
- Accept: harness — victim under artillery fire logs a replay for ≥95% of `[CombatHit]`
  entries attributable to entity shots (correlate shot ids over a 60s window).
- Effort: S. Risk: low.

## WS2 — Correctness holes (diagnosed, unfixed)

**2.1 Terrain silent-divergence backstop (RC-B).** The 10s terrain digest hashes only
the *ledger* (cells changed from baseline). A reactive cascade that mutates cells while
`_applying` is true (`WorldSync.CaptureCellChanges` early-drop, `WorldSync.cs:142`) never
enters the ledger — both peers' ledgers agree while the arrays differ, forever invisible.
Fix in two layers: (a) every ~30s (and on any digest mismatch) fold a full-array FNV
checksum (reuse `RunStarter.ChecksumLevel`) into `TerrainDigestMsg`; full-array mismatch
with agreeing ledger → existing `TerrainRepair` path; (b) extend the
`SuppressNetCellCascade` skip-list to every reactive terrain system found mutating cells
during apply, so the blind spot stops being written to.
- Files: `WorldSync.cs` (Tick/ApplyDigest), `Messages.cs` (one uint field).
- Accept: existing scenario baselines stay `terrainMismatch=0`; new fault-injection test
  (dev-poke a cell during apply) heals within one digest interval.
- Effort: M.

**2.2 Dispatch sub-phase marker (watchdog blind spot).** The watchdog cannot capture a
managed stack on this Mono (proven twice); a future freeze will again say only
`phase=Transport.Poll`. Publish the current `MsgType` + a dispatch counter from
`NetSession.Dispatch`; watchdog prints `handler=DamageRequest disp#=N`. The next hang
names its handler in the first log line.
- Files: `NetSession.cs:~1584`, `RuntimeInstrumentation.cs`, `MainThreadWatchdog.cs`.
- Accept: `stall`-based test shows the marker in `[Hitch]` output.
- Effort: S. Do this **first** — it is insurance for everything else in this spec.

**2.3 Bound `NetIds.ApplyChunk`.** Client-side `while (LastManifest.Count <= netId)`
(`NetIds.cs:212`) trusts a wire value; a corrupt/hostile `StartIndex` inflates the list
unboundedly (latent freeze/DoS). Clamp to a sane maximum (e.g. 65k) and reject the chunk
with a logged warning.
- Effort: S.

**2.4 Inactive-replica bind window.** `SpawnReplica`'s NRE-retry path re-instantiates
ACTIVE, leaving a one-frame window where a prefab-active shooter can fire unsynced.
Pre-disable known offender components (Shield et al.) before activation, or mute in
`Awake` via a temporary flag.
- Files: `MinionSync.cs:248-277`.
- Accept: zero `inactive replica bind failed` warnings across the entity-sweep zoo (#17).
- Effort: S-M.

**2.5 Shop-shield scope decision.** Current gate = `ShipMenuToggler.isOpen`, which also
covers the plain ship menu — a player can pop inventory mid-fight to facetank (vanilla
parity, but new leverage in co-op). Option: also require the private `currentStation !=
null` so only station shops shield. **Product call for Omar** — spec recommends
narrowing to stations; one reflection field away.
- Effort: S.
- **DECIDED + DONE (2026-07-19, Omar): station shops only.** `LocalShopMenuOpen()` now
  requires `isOpen && currentStation != null` (fake-null safe; `currentStation` is
  reassigned on every menu Open, so the pair is race-free). Popping inventory mid-fight
  no longer shields; docking at a station shop still does. The `shop on` harness override
  is unchanged (it simulates the shielded state directly, scenario #21).

## WS3 — Combat feel (jitter class)

**3.1 Articulated sub-part correction.** Multi-part bodies sync the root; children are
local physics. On snapshot *correction* (teleport-class, > ~3u), also
`TeleportWithChildren` the articulated chain (the machinery exists from the
interpolation audit) instead of letting tails/limbs spring across the map. Between
corrections, leave local physics alone (cheap, looks organic).
- Files: `RemoteEntityPuppet.cs` (apply path), reuse ShipSync child-carry logic.
- Accept: articulated-teleport scenario (#6) extended: child-root distance after a
  forced 50u correction stays < 5u on the viewer.
- Effort: M.

**3.2 Handoff aggro memory.** On lease handoff the new simulator's AI starts cold
(aggro amnesia — documented GAP in scenarios #8). Piggyback the current target slot on
the handoff baseline (1 byte per entity) and apply it via the existing `NoteAggro`
machinery on commit.
- Accept: #8 extended — enemy re-engages the same player within 1s of `handoff=` commit
  without being re-hit.
- Effort: M.

**3.3 On-screen combat rate tier.** `CombatStateHz` (30) already exists; add an
"on-screen of any viewer" tier at 45-60Hz for the ≤8 entities actually in a firefight
with a remote viewer (interest routes already know who watches what). Pure smoothness;
do **last** — it helps feel, fixes nothing.
- Effort: S-M. Watch `[Counts]` bandwidth.

## WS4 — Session lifecycle hardening

**4.1 Rejoin ship-reclaim (ghost-ship elimination).** Today: disconnect destroys the
puppet everywhere and rejoin replays the run from seed + catch-up. Two artifacts remain:
(a) the stale-frozen puppet statue during the disconnect-detection window; (b) rejoin
replay cost grows with run length. Spec: on disconnect, immediately **hide** (not
destroy) the puppet on peers (visible=false, colliders off) for a 60s reclaim window;
a rejoin within it re-binds the ship state (position/HP/fuel from the host's last
snapshot) instead of full replay; after 60s, despawn as today. Removes the "his ship is
still there" confusion and makes short reconnects near-instant.
- Files: `ShipSync.cs` (`RemoveRemoteShip` → `SuspendRemoteShip`), `NetSession.cs`
  (HandleRejoin fast path), `RemotePuppet.cs` (stale-freeze → hide).
- Accept: scenario #12 extended — kill/relaunch client < 60s: rejoiner is live in < 10s,
  `disconnectDespawns=0` for the window, no statue visible on the host (screenshot).
- Effort: L. Highest-value lifecycle item.

**4.2 Host-migration entity continuity.** Migration reattaches players (verified #13),
but entity authority for the dead host's segments resolves via grace fallbacks (slow,
lossy). On `promoted to host (migration)`: new host immediately dormant-commits all
ex-host-owned segments from its own last-received state, then normal wake claims them.
- Accept: new scenario — kill host process mid-fight; surviving peer's enemies resume
  simulating within 5s, zero zombies in `entities` after 10s.
- Effort: M-L.

## WS5 — Economy completeness

**5.1 Consumable/Module drops in the distant-loot payload.** The `EntityKilledMsg.Loot`
payload covers Ingredients + shared currency. Consumable and Module drops from a distant
kill still vanish for far players. Extend `LootEntry` with a type byte; grant via
`Vault.Add(Consumable,int)` / module store; same dedup latch.
- Accept: #18 extended with a consumable-dropping entity.
- Effort: M (needs one decompile pass for the consumable/module registry lookups —
  same shape as the IngredientRegistry work).

## WS6 — Verification infrastructure (pays for everything above)

**6.1 Fix the `fuel` devcmd puppet lookup** (use `ShipSync.ShipsBySlot` instead of
`FindObjectsOfType` — the reason #24 is only partially automated).
**6.2 Screenshot assertions.** `shot` exists; add it to the pause (#overlay), RC5
warning-laser, and reclaim-window scenarios so "visual" items get artifact evidence in
the report, even if judged by eye.
**6.3 Soak bot.** A 10-minute scripted 2-instance soak (autofly loops through fresh
territory + periodic combat + one stall + one rejoin) run before each release; PASS =
the #19 steady-state metrics + zero ongoing hitches + zero exceptions. This is the
regression net for every WS above.
- Effort: S each.

---

---

# Scale plane — N players, any bandwidth (review addendum)

The workstreams above fix defects at 2-4 players on good links. This section makes the
system **scale with player count and degrade gracefully with bandwidth** instead of
assuming both. Governing principle, stated once and enforced everywhere:

> **Two-plane rule.** Split all traffic into a CORRECTNESS plane (leases, kills, damage,
> terrain diffs, progression — small, reliable, sequenced; the world is WRONG without it)
> and a PRESENTATION plane (pose/vitals snapshots, fire visuals — large, unreliable,
> re-sendable; the world is only CHOPPY without it). The correctness plane gets a hard
> bandwidth floor (~10 kbps target, measurable from [Counts] commit/kill/diff rates) and
> never degrades. The presentation plane is fully elastic per link. Under this rule, a
> starved link produces a *choppy* world, never a *divergent* one — "any bandwidth"
> becomes a guarantee about correctness, not smoothness.

We already have the physical split (reliable Control/Events/Combat vs unreliable State);
the work is enforcing the discipline and making the elastic half actually elastic.

## WS7 — Elastic presentation plane (priority-scheduled replication)

**7.1 Per-(entity,viewer) priority accumulator — replace fixed Hz with byte budgets.**
The proven model (Tribes networking, Halo/Unreal prioritized replication): every entity
accrues priority per viewer each tick — weighted by distance, velocity, fire state,
staleness since last send, and on-screen-ness — and each send tick the owner fills that
viewer's **byte budget** with the highest-priority entities, resetting their accumulators.
Consequences: any budget produces the best-possible subset (a 5 KB/s link gets the
firefight at full rate and the background at 1Hz, automatically); StateHz/CombatStateHz/
DistantStateHz collapse into weights instead of three hard tiers; WS3.3 (on-screen tier)
is **subsumed** — on-screen-ness is just a weight. `EntityStateBundle` + interest routes
already shape the send path; this replaces its selection loop, not its transport.
- Files: `EnemySync.cs` (Collect/SelectInterestedGroups), `AdaptiveSnapshotTiming.cs`.
- Accept: harness A/B at equal bandwidth — combat entities' snapshot age p95 drops ≥2x
  while total bytes stay flat; background entities age more (by design).
- Effort: L. The single highest-leverage scale item.

**7.2 Receiver-driven budget (per-link congestion signal).** Each peer already measures
what it needs: receive-queue backlog (`ReceivePump`), interpolation jitter, and gap rate.
Piggyback a 2-byte "link health" score on the existing `Ping`/`ResidencyReport` cadence;
owners map it to that viewer's byte budget (7.1) with slow-start-style probing (grow on
clean, halve on backlog). This is TCP-friendly rate control for the presentation plane —
a congested viewer automatically receives less, *chosen by priority*, instead of
stalling everything equally.
- Accept: throttle-injection test (loopback + artificial 20 KB/s cap on one peer): capped
  peer stays in-game, correctness plane unaffected (`kills/leases` deltas identical on
  all peers), only its snapshot ages rise; UNCAPPED peers see zero change.
- Effort: M (given 7.1).

**7.3 Degrade ladder, not cliffs.** Formalize per-viewer LOD: ring 0 (on-screen combat)
full state; ring 1 pose+vitals; ring 2 pose-only quantized; ring 3 existence-only (map
dot — new, tiny message, lets far players keep world awareness with near-zero cost).
Budget exhaustion demotes rings smoothly. A peer below the correctness floor for >30s is
moved to catch-up-spectator (rejoin machinery already provides this state) rather than
silently rotting — an explicit, visible state instead of mystery desync.
- Effort: M.

## WS8 — Topology: unhost the state plane

**8.1 Owner→viewer direct state fanout.** Today the host relays most presentation
traffic: host uplink is O(N × activity) — the 8+ player wall. `DirectRoute` already
exists for owner→viewer snapshots; make it the DEFAULT for the state plane (host relay
becomes the fallback for NAT-blocked pairs — Steam Datagram Relay handles most). Host
keeps the correctness plane (sequencing needs a single point anyway). Host uplink drops
to O(N) control traffic; presentation bandwidth becomes each owner's own problem, sized
by their own budgets (7.2).
- Accept: 3-instance loopback (needs harness support for a 3rd install) — host `[Counts]`
  relay bytes flat while two clients exchange combat.
- Effort: M-L (mostly making DirectRoute first-class + fallback detection).

**8.2 Host as explicit sequencer (gap-detected event log).** The Events/Combat planes
already flow through the host; stamp a per-run monotonic sequence number on every
world-mutating event it relays. Clients detect sequence holes (channel-crossing races,
migration gaps) and request targeted replay via the existing `SendEventCatchUp` machinery
instead of trusting per-channel ordering end-to-end. This turns the correctness plane
into a replicated log with gap repair — the strongest cheap guarantee available to us.
- Effort: M.

## WS9 — Guaranteed convergence (bounded time-to-heal)

**9.1 Periodic state summaries (Merkle-lite).** Generalize the terrain digest pattern
(hash + compare + targeted repair — already proven) to ENTITIES: owners publish a per-
segment hash over (netId, lifetime, killed, coarse pose bucket, hp bucket) at ~0.2 Hz;
viewers hash their puppet set; mismatch → request that segment's roster diff (the
existing RosterAudit/divergence-heal machinery becomes the repair arm). Cost is
O(active segments) per cycle — independent of entity count and player count. Result:
**every** desync class — ghost, zombie, missed kill, lost spawn — has a bounded
detection time (≤ one summary cycle) instead of "whenever an audit notices."
- Accept: fault-injection — dev-command deletes a puppet / suppresses a kill on one
  peer; world self-repairs within 10s with a `[Heal]` log naming the cause.
- Effort: M-L. This is the "self-correcting regardless of conditions" capstone; after
  it, lost messages are a latency problem, not a correctness problem.

**9.2 Correctness-floor accounting.** Add a `[Counts]` line splitting bytes by plane.
CI/soak asserts the correctness plane stays under the floor (~10 kbps) at 4 players
under combat. Keeps the two-plane rule honest as features land.
- Effort: S.

## WS10 — Heterogeneous peers (capability-weighted authority)

**10.1 Load-aware lease placement.** `SelectOwner` picks the closest resident; a potato
PC surrounded by action becomes everyone's bottleneck. Piggyback a load factor (frame-
time headroom + send-budget utilization, 1 byte) on `ResidencyReport`; use it as the
tiebreaker among resident candidates and as a per-peer cap on owned segments (overflow →
next-closest resident, then host). Sticky-grab and "leases only within residency"
invariants unchanged — this only re-ranks eligible candidates.
- Accept: soak with one artificially slowed instance (`stall` bursts): its owned-segment
  count stays capped; enemy snapshot ages for ITS segments stay bounded.
- Effort: M.

**10.2 Slowest-peer decoupling audit.** Inventory every barrier that can gate the party
on one peer (go-live joins are already exempt by design — the no-terrain-gating
invariant). Remaining: handoff baseline acks (UnreadyUntil bounds it — verify the bound),
migration reattach, module-grid rebroadcast waits. Rule: every wait gets a deadline +
degrade path (pin/heal/spectate), never an unbounded stall.
- Effort: S-M (mostly audit + a few timeouts).

## Sequencing (revised)

| Phase | Items | Rationale | Status (2026-07-19) |
|---|---|---|---|
| 1 (insurance) | 2.2, 2.3, 6.1, 9.2 | Cheap; incidents self-identify; plane accounting from day one | **DONE** (harness-verified) |
| 2 (visible wins) | 1.1, 1.2, 1.3, 3.1 | Projectile + jitter class — the things testers *see* | **DONE** (harness-verified) |
| 3 (lifecycle) | 4.1, then 4.2 | Ghost ships + reconnect feel | **DONE** (built; needs manual kill/rejoin + host-kill verification) |
| 4 (correctness tail) | 2.1, 2.4, 5.1, 3.2 | Silent/rare classes | **DONE** (2.4 verified live; 5.1 modules stay resident-only — physical pickups, no inventory API) |
| 5 (scale core) | 7.1 → 7.2 → 9.1 | Budgeted replication, per-link elasticity, bounded heal | **DONE** (accumulator stage A + per-viewer byte-budget stage B; link-health slow-start; data-level summaries → targeted audits) |
| 6 (scale topology) | 8.1, 8.2, 10.1, 10.2, 7.3 | Unhost the state plane; heterogeneous peers | **DONE** — 8.1 verified already-implemented (direct default + 2.5s pulse fallback); 8.2 checkpoint sequencer + gap catch-up; 10.1 load tiebreak + soft cap; 10.2 audit closed (outbox drop was the one hole — now detected by 8.2); 7.3 delivered as budget floor + degrade toast (ring-3 map dots deferred) |
| 7 (polish) | 2.5, 6.2, 6.3 | Product calls + release net (3.3 subsumed by 7.1) | 2.5 **DONE** (Omar: station shops only — inventory no longer shields); 6.2/6.3 release tooling open |

Phases 2-4 are worth shipping to playtesters before starting phase 5; phases 5-6 are the
"any number of players, any bandwidth" investment and pay off most beyond 4 players.

### Implementation notes (phases 5-6, for future maintainers)

- **7.1** — stage A: `TryCollectEntry`'s three Hz tiers became weights feeding a per-entity
  accumulator (`SendPriority`); same rates at defaults, but staleness accrues and factors
  compose. Stage B: `ApplyViewerBudget` fills each viewer's byte budget highest-priority
  first (staleness × proximity-to-viewer × fire; fixed-owner minions always kept),
  preserving (segment, epoch) group structure; dropped entries win later by staleness.
  Applied on the host fanout AND client direct sends; the client→host feed stays complete
  (canonical relay source). Default budget 12 KB/tick — keyframes align on the 0.5s cadence
  into ~6 KB single-tick bursts, so 3 KB dropped entries in QUIET sessions (measured).
- **7.2** — `LinkHealthMsg` (2s, client→host→all): underruns + chunk gaps + jitter → score;
  owners halve the budget at score ≥48, grow ×1.25 below 16. Devcmds `linkhealth <n|auto>`
  (force advertised score) and `netbudget <bytes|auto>` (force budgets) drive throttle tests.
- **9.1** — `SegmentStateSummaryMsg` at 0.2 Hz per owned segment; hash is PURE data-level
  (netId+lifetime, killed excluded, wire-rounded positions, 2u boundary band). Shipped as
  **detection telemetry** (`summaries=tx/chk/miss` on [BytePlanes]); the echo→targeted-audit
  repair arm is implemented but gated behind `[Diag] SummaryHeal` (default OFF). Why: three
  successive live runs showed position-based segment membership cannot reach parity for
  entities OUTSIDE a viewer's interest radius — their viewer-side data.position freezes at
  last-streamed, so fringe segments and wandered enemies false-positive forever (measured:
  live-object clause → 0-vs-2 loops; raw float pos → boundary pair splits; band+rounding →
  wander-class repeats). **v2 design**: viewer-targeted summaries — the owner hashes, per
  viewer, only entities within THAT viewer's interest (it knows every ship position), or
  position-independent invariant buckets (netId-range hashes over lifetime+killed only) with
  a reconcile-style repair. Until then the round-robin roster audit remains the repair arm,
  and the miss counter gives the bounded-time visibility.
- **8.2** — counts at transport accept (`NetSeq`), per (peer, channel); reliable channels are
  ordered so `EventSeqCheckpoint` is a barrier; deficit → `EventGapReport` → idempotent
  `SendEventCatchUp`. Catches outbox-overflow drops and migration gaps. Counters reset per
  connection (peer-id reuse on loopback).
- **10.1** — load byte on `ResidencyReport` (frame-time EMA; a load-bucket change triggers a
  report). `SelectOwner`: load is a tiebreak at equal distance; overloaded (≥160 ≈ 25 fps)
  peers at ≥6 owned segments are skipped ONLY when another resident candidate exists —
  never creates artificial dormancy, sticky-grab and residency invariants untouched.

## Non-goals (accepted limitations of the architecture)

- **Per-bullet flight streaming** — bandwidth cliff; fast bullets stay re-simulated.
- **A dedicated authoritative server** — the game engine only exists inside clients.
- **Lockstep determinism of the live sim** — vanilla Punk is frame-rate-dependent and
  RNG-everywhere; we gate *generation* deterministically and heal the rest.
- **Sub-100ms artifact-free handoffs under packet loss** — transitions are distributed;
  we minimize their frequency and duration, we cannot make them atomic.
