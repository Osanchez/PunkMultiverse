# Entity Synchronization Architecture — Investigation & Design

Status: **IMPLEMENTED — all phases.** Phases 0–1 in v0.1.81, Phases 2–5 in v0.1.82 (see §13
status notes for what shipped and the two documented deviations: PropRest deferred, starvation
backstop retained).
Scope: distributed entity authority, residency, materialization, dormancy, recovery, host
migration, and the container/prop model for PunkMultiverse.

---

## 1. Root-cause analysis

### 1.1 The one-sentence version

**Authority and residency are two independent systems that were never tied together**: segment
leases are assigned by Chebyshev distance between ships and segments, while the game only keeps
entity GameObjects alive within a *different* (and smaller) distance of the local player — and
`BuildRuntimeBaselineRoster`'s dormant-data fallback lets handoffs and interest routes *commit
anyway* by fabricating a default state, converting a silent geometry mismatch into confirmed,
acknowledged authority over entities nobody simulates.

### 1.2 The geometry, from decompiled game code

`LevelSegmentComponentManager.OnCurrentSegmentChanged` (game, Punk.Main.dll):

```csharp
segmentsToGenerate.RemoveAll(s => currentSegment.MaxDistance(s) > 3f);
foreach (var item in segments.Keys.ToList())
    if (!(currentSegment.MaxDistance(item) <= 3f)) Destroy(item);   // immediate unload
foreach (var n in level.GetNeighbourSegments(currentSegment, 3)) ...  // queue builds
```

- The game streams entity GameObjects for segments within **Chebyshev 3** of the local player's
  segment and **destroys them immediately** beyond that. Builds are amortized **one segment per
  frame** (`Update` pops `segmentsToGenerate[0]`).
- `EntityGameObjectManager.InstantiateGameObjects` spawns objects from
  `EntityManager.GetEntitiesInSegment` — the same data-side spatial bucket that
  `BuildRuntimeBaselineRoster` walks. **EntityData exists for every entity at all times; a
  GameObject exists only inside the streaming radius.**

`AuthorityManager` (mod):

- `AcquireRadiusSegments = 3`, `KeepRadiusSegments = 4` (`src/Core/AuthorityManager.cs:41-42`).
- `SelectOwner` keeps a lease sticky while the current owner is within **Chebyshev 4**
  (`AuthorityManager.cs:283-285`).
- Every unleased segment defaults to the **host** (`OwnerOf`, `AuthorityManager.cs:99-119`) —
  regardless of whether the host has anything instantiated there.
- Leases are only returned to the host when **no player** retains the segment
  (`!Retained.Contains(key)`, `AuthorityManager.cs:263-270`) — the *observing* player's presence
  keeps a segment "retained" for an absent owner.

So there is a **deterministic, one-segment-wide band (Chebyshev distance exactly 4)** around every
player where that peer holds leases for segments whose GameObjects its own game has already
destroyed. And there is an unbounded region where the *host* nominally owns segments it has never
streamed. Both produce the same invariant violation:

> A peer is authoritative for entities it has no live simulator for.

### 1.3 The enabling flaw: fabricated baseline entries

`BuildRuntimeBaselineRoster` (`src/Sync/EnemySync.cs:1075-1138`) walks
`EntityManager.GetEntitiesInSegment` — dormant EntityData included — and when the source cannot
collect real state:

```csharp
if (egm == null || OwnerOf(netId) != session.LocalSlot
    || !TryCollectEntry(egm, netId, out state, forceFull: true))
{
    state = new EntityStateEntry {
        ... Pos = data.position, Vel = Vector2.zero,
        State = byte.MaxValue, HpFraction = 1f, ...   // manufactured
    };
}
```

Consequences:

1. **Identity existence is treated as proof of a simulator.** A handoff PREPARE from a
   non-resident source produces a full roster of manufactured entries; the target materializes
   them (`ApplyRuntimeBaseline`, `EnemySync.cs:1277-1412`), ACKs `Installed=true`, and the host
   **commits the lease** (`CompleteBaselineHandoff` → `Commit`). The transaction "succeeds";
   nothing in it ever verified that anyone can actually stream these entities afterwards.
2. **State is corrupted, not just missing.** The fabricated entry claims
   `Fields.Full` with `HpFraction = 1f` — a previously damaged entity that goes through a
   dormant-source handoff is silently healed on the target, and `FullState` (the host's canonical
   cache, also the lost-owner fallback) is poisoned with the fabrication.
3. **Interest routes make the same category error** in the other direction: `route.Ready` is set
   when the *target installed the roster*, not when the *source can produce snapshots*.

### 1.4 The four concrete failure mechanisms

**(A) Host-owned starving puppets on the client** (the ~12 enemy puppets that never received a
snapshot). The client streams a segment within its radius 3; the segment's lease is host-default
or host-sticky at Chebyshev 4; the host has no GameObjects there. The client's own EGM spawns the
objects, `ApplyOwnership` sees `OwnerOf == host` and attaches muted `RemoteEntityPuppet`s. The
host's `Tick` iterates only `LiveEntities` (`SpawnedUnitCandidates`, `EnemySync.cs:1898-1920`) —
it has none there — so it produces **no state groups, therefore no interest route, therefore no
baseline, therefore no snapshots, ever**. The puppet freezes at generation pose.

**(B) Client-owned starving entities on the host** (Crate2 #3310, Crate #3116, Box_Matchbox #860,
Box_Health #558, Unit_Grunt #3641, Unit_Fly #1191). A client acquires leases while nearby, then
moves to Chebyshev 4 (or the handoff-return scan never fires because the host player keeps the
segment `Retained`). The client's game destroys the GameObjects (radius 3); the lease sticks
(radius 4). The host walks in, streams the objects, sees `OwnerOf == client`, puppets them — and
the client, having no live objects, streams nothing. **No runtime spawn messages** for these
entities is exactly what the manifest/world origin predicts.

**(C) Damage to dormant-owned entities is silently dropped on clients.**
`DamageSync.ApplyDamageRequest` (`src/Sync/DamageSync.cs:387-399`): when the target isn't spawned
on the owner, only the **host** queues the damage and forces a lease transfer
(`OnDormantHit`); a **client** owner hits `if (session.IsHost) {...}` and falls through to
`return` — the damage vanishes. This is "fightable on only one peer": the host empties magazines
into a frozen `Unit_Grunt #3641` whose client owner discards every request.

**(D) Physics props are not in the authority system at all.** `ApplyOwnership` early-outs for
non-`Unit` entities (`EnemySync.cs:625`), so containers never get puppets; every machine that
streams them runs **independent local physics**. The owner-side snapshot path for props requires
the owner to have a live object (`TryCollectEntry`, `EnemySync.cs:1934-1944`) — a non-resident
owner streams nothing, and non-owners never stream. Result: "falling containers visible on only
the host": the host's local crate obeys gravity; the client's copy is dormant data at the
generation pose. The baseline roster additionally **excludes** non-Unit entities entirely
(`isUnit` check, `EnemySync.cs:1095-1103`), so no baseline can ever repair them.

**(E — aggravator) Recovery correctly refuses to promote, so the failure is permanent.**
`IsStableAvailabilityCandidate` (`EnemySync.cs:1693-1742`) rejects zero-snapshot replicas
("no-authoritative-pose") — the right call given that promotion would canonize a generation pose —
but since mechanisms A/B guarantee no snapshot will ever arrive, starvation detection loops
forever: request → refuse → cooldown → request.

### 1.5 Why the counters looked clean

Chunk loss 0, stale lifetimes 0, unauthorized snapshots 0, digests matched — all consistent:
nothing was ever *sent* for these entities, so no receive-side counter could fire. The
instrumentation measures the health of traffic that exists; it has no gauge for **traffic that is
owed but absent** (first-snapshot deadlines, owner-without-simulator). Section 10 fixes this.

### 1.6 Log evidence (most recent session pair, v0.1.80, ~32 min, 2026-07-13 19:35)

Both peers' logs exist locally (two-instance loopback): host
`PUNK Playtest\BepInEx\LogOutput.log` (7,603 lines), client
`PUNK Playtest - OD Test2\BepInEx\LogOutput.log` (7,354 lines). Note: the mod's LogUploader
webhooks logs off-box; it does not store received client logs, so cross-peer analysis depends on
both installs being local — a gap §10 addresses (lease-table digests would have proven the
split-brain from one log).

**The split-brain, in two lines.** Same entity, same segment, `age=never` on both machines, each
naming the *other* as owner:

```
HOST   mono=151.4s  [EntityAudit] #1191/Unit_Fly@(39,36) owner=P2 age=never dist=inf gate=no-authoritative-pose
CLIENT mono= 51.5s  [EntityAudit] #1191/Unit_Fly@(39,36) owner=P1 age=never dist=inf gate=no-authoritative-pose
```

**Timeline (segments (39,36) and (42,36); all six reported entities):**

1. `t≈50s` — client is near, host is far. Segment unleased → defaults to host. Client's audit
   shows 27 puppets `owner=P1 age=never` — **mechanism A** live: the "owner" (host) has no
   objects, so nothing ever streams.
2. `t≈62s` — host's authority scan hands the segments to the client:
   `[Lease] segment (39,36) P1->P2 epoch=3`, `[Lease] segment (42,36) P1->P2 epoch=6`.
   The commit path requires a baseline ACK (`SegmentLeaseAckMsg` has **no sender anywhere in the
   codebase** — `CompleteBaselineHandoff` is the only commit trigger), and the host was
   non-resident — so the committed handoff **necessarily rode fabricated roster entries**
   (mechanism §1.3). The transaction "succeeded" while transferring authority over entities the
   source never simulated.
3. `t≈62–151s` — the client moves off; its game destroys the objects (radius 3); the lease sticks
   with P2 (radius 4 / `Retained` by the host player's own presence — §1.2).
4. `t=151.4s` — host streams the segment in and audits:
   `puppets=13 … neverSnapshot=6 … #3310/Crate2@(42,36) owner=P2 age=never, #860/Box_Matchbox,
   #3641/Unit_Grunt, #558/Box_Health, #1191/Unit_Fly, #3116/Crate — all owner=P2 age=never
   gate=no-authoritative-pose`. **`neverSnapshot` stays pinned at 6 from t=151s to t≈1860s** —
   thirty minutes of owed-but-absent snapshots.
5. `t≈1920s` (teardown reclaim) — the host cannot take the segments back:
   `[BaselineRoster] incomplete … segment=(39,36) target=P1 materialized=0/3
   missing=#558:data,#1191:data,#3641:data` → `[Lease] segment (39,36) remains P2` (same for
   (42,36)/#3310). The lease is stuck with a peer that has nothing, because the reclaim baseline
   *also* has no honest source.

**Counter evidence (session end):**

| Counter | Host | Client | Reading |
|---|---|---|---|
| `starvedPuppetFrames` | 381,013 | 148,703 | both machines spent the match holding non-updating puppets |
| `availabilityDeferrals` | 17,813 | 20,393 | recovery detected starvation ~38k times… |
| `availabilityPromotions` / `starvedRequests` | 0 / 0 | 0 / 0 | …and never escalated once (aggravator E: `no-authoritative-pose` gate can never pass when no snapshot can ever arrive) |
| `baselines req/apply` | 10,821 / 8 | 0 / 33 | the host's route/baseline retry loop spun ~10k times without converging — silent churn with no alarm |
| `roster materialized` | 0 | 11 (missing 6) | the split segments never materialized a simulator anywhere |
| `unauthorized`, `duplicateNetIds` | 0 | 0 | **not** a dual-simulation conflict — a zero-simulation condition |

One more instructive detail: `#3641 Unit_Grunt` appears in a client-side boundary handoff at
`t≈53s` (`[Diag:Handoff] #3641 crossed (39,36)->(39,37); P2->P1 reliable final state`) — the
per-entity boundary machinery ran and produced an owner *label* without ever producing a
*simulator*, confirming that every transfer path (segment lease, boundary handoff, interest
route) shares the same missing residency check.

---

## 2. The fundamental invariant, and the concept that enforces it

### 2.1 Residency–Authority Contract

> **I-0 (fundamental):** At any instant, every synced entity identity is in exactly one global
> phase: **Live(owner P, epoch E)** — exactly one connected peer P holds authority AND has a
> live, simulation-capable object for it — or **Dormant(rev R)** — no peer simulates it and its
> canonical state is a durable, explicitly-committed record — or **Destroyed(rev R)** — a durable
> tombstone. No observer may present an entity in a state that is neither authoritative-live nor
> canonical-dormant.

The single biggest change this document proposes: **"the host owns everything by default" is
replaced by "nothing is owned by default."** Dormant is a first-class, *honest* state — the
current system fakes Live for the entire world and fabricates state whenever the fakery is
audited. The host remains the **coordinator** of leases, ledgers, and canonical dormant state; it
stops being the fictional default *simulator*.

### 2.2 Full invariant set

| # | Invariant |
|---|---|
| I-1 **Identity** | Every synced entity has exactly one netId, assigned deterministically (manifest) or by slot-partitioned runtime allocation. A netId never aliases two entities (existing `NetIds.RegisterRuntime` guard). Identity proves *existence*, never *state* and never *residency*. |
| I-2 **Lifetime** | A (netId, lifetime) pair names one logical spawn generation. All state/mutation/kill traffic carries it; mismatches drop (existing). Lifetime bumps only on authoritative reuse, never on stream-out/in. |
| I-3 **Ownership** | Ownership is host-granted (lease or fixed assignment), epoch-scoped, and exclusive. A peer simulates an entity iff: it is the committed owner, the epoch is current, its object is canonical (`LiveEntities`), and no puppet/quarantine component is present (existing `CanSimulate`, kept). |
| I-4 **Segment lease** | A lease may only be granted to, and only remain with, a peer whose **reported residency set** contains the segment. A lease held by a peer that loses residency MUST transition (handoff or dormancy) — enforced at the owner (on unload) and audited at the host (on residency report). |
| I-5 **Source residency** | A handoff/interest baseline entry is either **Live** (source has a canonical simulating object; carries `LastSimTick`) or **Dormant** (state comes from the canonical store, marked with its origin and revision). A source never fabricates an entry, and a Live entry from a non-resident source is a protocol violation the host rejects. |
| I-6 **Target residency** | Authority commits only after the target proves materialization: its EGM has the segment active and every roster entity bound to a live object (existing ACK discipline, kept and extended with a `NotResident` reason). A target that cannot materialize within its deadline causes the segment to fall to **Dormant**, never back to a non-resident owner. |
| I-7 **Initial state** | An entity becomes visible/interactive on a machine only with either (a) live authoritative state (snapshot/baseline from the current owner-epoch), or (b) canonical dormant state (generation state ⊕ replayed mutation ledger ⊕ last dormancy commit). Fabricated defaults are removed from the protocol. |
| I-8 **Visibility** | A remote replica renders frozen at its last valid state and is muted (existing puppet). Interacting with a Dormant entity triggers claim/activation; the interaction is queued durably (never silently dropped) until an owner exists. |
| I-9 **Destruction** | Destruction is a durable mutation: (netId, lifetime, revision) tombstone, monotonic per entity, replayed by ledger to every peer and to late joiners (existing kill ledger, kept). Applying a kill is idempotent; death effects run at most once per machine (existing `DeathEffectsDone`/loot dedup, kept). |
| I-10 **Unloading** | An owner must not destroy its last live object for an entity while holding authority. Unload = commit final state to the canonical store (dormancy commit) *first*, then release. The hook point (`EntityGameObjectManager.DestroyGameObjects` prefix) makes this atomic with the game's own unload. |
| I-11 **Disconnects** | When a peer disconnects, every lease and fixed assignment it held resolves immediately: segments resident on a surviving peer are re-granted (activation from canonical store + host's last received live state); all others become Dormant. Pending handoffs to/from it cancel by epoch. No fabricated state is minted. |
| I-12 **Host migration** | Every input the coordinator needs (kill ledger, mutation revisions, dormancy commits, lease table, manifest) is replicated to all peers continuously, so any survivor can be promoted and reconstruct the same canonical store. Fixed-owner reassignment stays deterministic (existing `ReassignFixedOwners`). |

---

## 3. Authority model decision

### 3.1 Options evaluated

| Model | Correctness | Latency | Bandwidth | CPU | Complexity | Failure recovery | Verdict |
|---|---|---|---|---|---|---|---|
| **Host simulates everything** | Strong (single writer) | Bad for clients (every enemy reaction 1 RTT; interception feel) | High from host, low from clients | Host scales with players×area — the design constraint forbids it; host must also stream *every* combat area, which the game's radius-3 streamer physically won't do without deep engine surgery | Low | Trivial (host is the state) | Rejected as permanent model; **retained as degraded mode** for pathological segments (flag-controlled) |
| **Distributed segment authority (today's model, geometry-fixed)** | Good *if* leases ⊆ residency | Good (combat is local to the nearest player) | Good (interest-routed) | Distributed | Medium (exists) | Epochs + two-phase (exists) | **Core of recommendation** |
| **Per-entity authority for everything** | Good | Good | Per-entity lease/ack traffic ≫ segment batching; thousands of manifest entities | Bookkeeping heavy | High | Per-entity timeouts everywhere | Rejected as the general rule; **kept for exceptions** (minions, starvation grabs — existing `FixedOwners`) |
| **Simulation islands (interaction-graph clusters)** | Best isolation in theory | Good | Similar to segments | Cluster maintenance every frame | Very high; dynamic cluster identity is hard to make idempotent | Complex | Rejected now; segments already approximate it at radius scale |
| **Deterministic lockstep** | Perfect if achievable | Input latency; stalls on loss | Minimal | Full sim on every peer | Requires deterministic physics/RNG/iteration order — Unity PhysX 2D is not deterministic across machines, and the game consumes RNG in callback/streaming order (§8) | Rebuild-the-game | Rejected |
| **Hybrid: host-coordinated, residency-constrained segment leases + Dormant phase + per-entity exceptions** | Strong (I-0 enforceable) | Good | Good (dormant = zero streaming) | Distributed; dormant = zero cost | Medium — mostly *removes* code paths (fallbacks, starvation loops) | Explicit per-transition rules (§7) | **RECOMMENDED** |

### 3.2 The recommended model in one paragraph

Keep host-arbitrated, epoch-scoped segment leases and the two-phase baseline/ACK handoff — that
machinery is sound. Constrain it with residency: peers continuously report their EGM-active
segment set; the host grants leases only within it; unleased segments are **Dormant, not
host-owned**. Wire the owner's unload path so that losing residency *is* the release edge
(dormancy commit → host stores canonical state → broadcast). Wire stream-in so that gaining
residency over a Dormant segment *is* the activation edge (host grants lease with a canonical
baseline — never a fabricated one). Keep sticky ownership (no closest-player re-optimization —
leases move only on residency loss, interaction claims, or disconnect) and keep per-entity fixed
ownership for minions and proven-concrete starvation grabs.

---

## 4. Entity lifecycle state machine

### 4.1 Global phase (agreed via coordinator; the source of truth for I-0)

```
                      +--------------------------------------+
                      |                                      v
 GENERATED (manifest) ──► DORMANT(rev) ──activation──► LIVE(owner,epoch)
                      ▲       ▲  │                        │   │
        runtime spawn─┘       │  └──interaction claim──►──┘   │
                              │                               │
                              └────────dormancy commit────────┤
                                                              │
                    DESTROYED(tombstone) ◄────kill/destroy────┘
                              ▲
                              └───(dormant destroy: mutation on data)───
```

- `DORMANT(rev)`: canonical state = generation state ⊕ mutation ledger ⊕ latest dormancy commit;
  `rev` is monotonic (reuses the mutation-revision machinery). Nobody streams; nobody simulates.
- `LIVE(owner, epoch)`: exactly one simulator; snapshots flow; all durable events must come from
  `owner` at `epoch` (existing `IsStateAuthority` check, kept).
- `DESTROYED`: terminal; enforced by kill ledger replay + spawn-hook re-kill (existing).

### 4.2 Per-machine object state (what a single peer holds for one netId)

| State | Meaning | Entry | Exit |
|---|---|---|---|
| `IdentityOnly` | netId↔instanceId known; EntityData exists; no GameObject | manifest / runtime registration | stream-in, destroy |
| `MaterializingTarget` | roster received; spawning + binding objects; **not visible/simulating** | baseline apply | ACK complete → `ReplicaLive` or `SimulatorArming`; failure → `IdentityOnly` + NACK |
| `ReplicaDormant` | frozen at canonical pose, muted, damage-forwarding queued as claim | stream-in of a Dormant-phase entity | activation commit (→`ReplicaLive` or `Simulator`), unload |
| `ReplicaLive` | muted puppet interpolating owner snapshots (existing `RemoteEntityPuppet`) | first snapshot/baseline from current epoch | authority change, owner dormancy, unload, kill |
| `Simulator` | canonical local object, full AI/physics, streams snapshots (existing `CanSimulate` gate) | lease/assign commit for my slot | handoff prepare, unload (→ dormancy commit), kill |
| `HandoffFrozen` | simulator paused during PREPARE; puppet w/ final pose (existing freeze) | prepare sent/received | commit (→`ReplicaLive`) or cancel (→`Simulator`) |
| `Quarantined` | superseded duplicate lifetime (existing `DuplicateEntityInert`) | overlap detected | retirement destroy |
| `Tombstoned` | killed; object removed; data destroyed; re-kill on re-stream (existing) | kill applied | never |
| `OrphanRecovering` | starving replica or ownerless live object; recovery protocol running (§7.5) | starvation deadline / owner loss | promotion, activation, or dormancy |

Rules preserved from today (they are correct): newest EGM object is canonical; superseded
lifetimes quarantine, never fight the loader; puppets mute before first activation; kills replay
on stream-in.

---

## 5. Message flows

Notation: `H` = host/coordinator, `A` = current owner, `B` = target/observer, `⊳` reliable,
`⇢` unreliable. All new messages are idempotent by (segment, epoch) or (netId, lifetime, rev).

### 5.1 Interest materialization (observer approaches a LIVE segment owned elsewhere)

```
B: EGM stream-in → replicas spawn muted+hidden-state (ReplicaDormant pose from canonical store)
B ⊳ H : ResidencyReport(+segment S)
H     : S is Live(A,E); create route (B,S)
H ⊳ A : RuntimeBaselineRequest(S, E, purpose=Interest)
A ⊳ H ⊳ B : RuntimeBaseline(S, E, entries: Live[LastSimTick]… + Dormant[rev]…, digest)
B     : bind/materialize, apply live entries to puppets, dormant entries to frozen poses
B ⊳ H : RuntimeBaselineAck(installed, counts, missing)
H     : route.Ready → A's snapshot groups for S now relay to B (or direct route)
A ⇢ B : EntityStateBundle…            ── first-snapshot deadline clock runs from Ack
```

Key differences from today: the route begins from **B's residency**, not from A happening to emit
a state group; dormant entries are honest (origin-tagged, no HP fabrication); `route.Ready` gates
only *live* entries' streaming expectations.

### 5.2 Segment handoff (A resident → B resident; e.g. B is closer and A is leaving)

```
H     : decides transfer (A lost/losing residency, or claim) — never mere distance re-optimization
H ⊳ all: SegmentLease(S, B, E+1, PREPARE)
H ⊳ A : RuntimeBaselineRequest(S, E→E+1, purpose=Handoff)
A     : freeze simulators in S (existing freeze), collect LIVE entries only
A ⊳ H : RuntimeBaseline(S, …, sourceLiveCount, dormantCount, digest)
H     : verify: every Live entry ⊆ A's residency claim; merge Dormant entries from canonical store
H ⊳ B : RuntimeBaseline(S, E+1, purpose=Handoff)
B ⊳ H : RuntimeBaselineAck(complete | missing+reason)
  complete → H ⊳ all: SegmentLease(S, B, E+1, COMMIT); B arms simulators; A demotes to replicas
  missing  → H ⊳ all: SegmentLease(S, A, E+1, CANCEL); A unfreezes (existing path)
             + if A is actually non-resident: go to §5.3 dormancy instead of cancel-to-A
```

### 5.3 Owner unloading (A streams the segment out — the Chebyshev-4 band, fixed)

```
A     : game calls EGM.DestroyGameObjects(S)  [Harmony prefix, BEFORE objects die]
A     : collect final EntityStateEntry for every canonical simulator in S
A ⊳ H : SegmentDormancyCommit(S, E, entries, digest)     ── the release edge
A     : objects destroyed by game; A stops streaming S (lease locally invalidated)
H     : canonical store ← entries (rev++); phase(S) = Dormant
H ⊳ all: SegmentLease(S, none/DORMANT, E+1, COMMIT) + DormancyDigest(S, rev)
observers of S: puppets freeze at final committed pose (they already hold it from snapshots)
  — if another peer B is resident in S: H immediately runs §5.4 activation for B
```

Timeout: if `SegmentDormancyCommit` is lost (crash mid-unload), H detects via ResidencyReport
(A no longer resident) and falls back to **its own snapshot cache** (`FullState`) as the dormancy
source, marked `origin=CoordinatorCache` — the honest version of today's
`BuildCachedBaselineRoster`.

Three refinements added 2026-07-15 after the fallback fired 88× in one playtest with zero crashes:

- **Deferred-commit latch** (`AuthorityManager.DeferredDormancyCommits`): when a commit arrives
  while another peer looks resident, `OnDormancyCommit` defers to a possible handoff — but the
  received commit stays first-class state. If the prospective target unloads before the scan
  hands off, the segment goes Dormant from the latched commit (quiet), never via the crash grace.
  The latch invalidates when the committing owner becomes resident again, when the lease commits
  to a new owner, and on any dormant transition.
- **Lease decline** (`EnemySync.PendingLeaseAcceptance`): a grant can race the target's unload —
  the destroy-prefix commit never fires because the owner's lease replica lagged (`OwnerOf(key)`
  wasn't "me" yet at `DestroyGameObjects` time), or the grant landed after the segment was gone.
  Every grant to the local slot is checked ~1.25 s later: still owned + still not streaming →
  send the dormancy commit then (the decline). Timeouts are for crashes only; every graceful
  path gets an explicit message.
- **Disconnect cleanup logs as itself**: `OnPeerLost` resolutions print one summary line
  ("resolved N owned segment(s) to dormant"), not N per-segment "no commit received" warnings.
  After these three, any remaining `coordinator-cache fallback` warning is a real anomaly worth
  chasing (verified: 0 across a full co-flight harness run, `dormantTransitions=213(cache0)`).

### 5.4 Activation (Dormant segment gains a resident peer B — stream-in or interaction claim)

```
B ⊳ H : ResidencyReport(+S)      (or DamageRequest on a ReplicaDormant → claim)
H     : phase(S)=Dormant → select owner=B (resident; claims win ties)
H ⊳ all: SegmentLease(S, B, E+1, PREPARE)
H ⊳ B : RuntimeBaseline(S, E+1, purpose=Activation, entries from canonical store [origin,rev])
B ⊳ H : RuntimeBaselineAck(complete)
H ⊳ all: SegmentLease(S, B, E+1, COMMIT)
B     : arms simulators; queued dormant interactions (damage claims) replay to B (existing
        PendingDormantDamage generalized: queue lives on H, drains on commit — for ALL owners)
```

This replaces both silent divergence paths: vanilla-like behavior returns (a player walking into
a sleeping room wakes it up), but *globally agreed* and with canonical state.

### 5.5 Owner disconnect

```
H     : transport loss of A
H     : for each lease of A: cancel pending epochs (existing OnPeerLost)
        for each such segment S:
          resident survivor B exists → §5.4 activation for B, baseline origin=CoordinatorCache
          nobody resident          → phase(S)=Dormant from CoordinatorCache
H     : fixed owners of A → deterministic fallback slot (existing ReassignFixedOwners),
        but ONLY if fallback peer proves concrete residency; else Dormant
```

If the **host** dies: migration (existing election) → promoted peer already holds ledgers +
dormancy commits (I-12) → rebuilds lease table from scratch: all segments Dormant, then §5.4
activation storms resolve from each peer's residency reports. Deliberately simple: post-migration
the world re-activates from canonical state instead of trying to reconcile stale leases.

### 5.6 Late join / rejoin

```
J ⊳ H : Hello… (existing) → regenerates world from seed → LEVEL_READY (digest barrier, existing)
H ⊳ J : Manifest + EntityBaseline (existing)
H ⊳ J : Ledger replay (existing kills/mutations/upgrades) + NEW: DormancyDigest per touched
        segment + dormancy entries (chunked, vicinity-first like terrain)
H ⊳ J : GoLive
J     : streams world; every segment J enters is Dormant-or-Live like any other peer:
        Live → interest route (§5.1); Dormant → activation (§5.4) if J is the only resident
```

No terrain gating; big state arrives in player-vicinity chunks (existing WorldSync pattern
reused for the dormancy ledger).

### 5.7 Shared container: fall, land, open, destroy (see §6 for the model)

```
Generation: crate at rest pose P0 on all peers (deterministic, digest-verified)
B streams segment in (Dormant) → §5.4 → B owns; crate unfrozen on B; local physics runs on B ONLY
B ⇢ all-interested : prop snapshots while moving (existing prop path, now owner-gated)
crate sleeps (rb.IsSleeping ∧ Δpos<ε for T) → B ⊳ H: PropRest(netId, pose, rev)  [micro-dormancy]
   → canonical pose updated; observers freeze at P1; streaming stops
Any peer opens/damages it: puppet/dormant → DamageRequest/claim → owner applies → durable
   mutation (rev++) broadcast; destruction → EntityKilled tombstone (existing, replayable)
Loot: container destruction rolls ONCE on the owner → contents announced in the kill/mutation
   event; each machine then spawns its own per-player pickup copies locally (existing instanced
   model, unchanged: the CONTAINER is shared, the PICKUPS are per-player and excluded from
   generic replication — LootFactory gate, `MinionSync.MarkLootSpawns`)
```

---

## 6. Containers and physics props

Props are today's second unhandled class (root cause D). Design:

1. **Props join the ownership system** with a cheap profile: no AI mute list, just physics
   ownership. Non-owner residents attach a `PropPuppet`-style kinematic hold at canonical pose
   (today's PropPuppet already interpolates and self-expires; it gains a "frozen at canonical, not
   local-sim" default instead of falling back to local physics).
2. **Rest-state micro-dormancy**: an owned prop that sleeps for T (~1s) commits its pose
   (`PropRest`) and stops costing bandwidth; this is the prop-scale analogue of §5.3 and the
   canonical answer for "falling containers": the fall is simulated once, by one owner, and the
   landing pose becomes durable truth for observers and late joiners.
3. **Activation**: same as entities — dormant prop + resident peer → owner; dormant prop + damage
   → claim.
4. **Opening/damage/destruction** are durable mutations with revisions (existing machinery).
   Container *contents* decided once on the owner at destruction time, announced in the event —
   never re-rolled per machine. (Loot the game rolls at *generation* time stays seed-derived.)
5. **Per-player vs shared, explicitly**: the shared object is the container entity (netId,
   canonical pose, HP, destruction tombstone). The intentionally per-player objects are the
   pickups it spawns (`*_pickup`, LootFactory call sites) — local-only, never in rosters, never
   in the authority pool (existing and correct; keep the call-site gate, it's more reliable than
   id-suffix matching).
6. **Roster inclusion**: baselines/rosters include props (drop the Unit-only filter) with an
   entity-class flag so the target knows to bind a prop hold instead of an AI mute.

---

## 7. Failure & timeout behavior (every transition)

Principle: **prefer invariant-preserving fallbacks (Dormant) over heuristics (timeouts that guess
an owner)**. Timeouts exist only to detect *absence of progress*, and their outcome is always a
transition to an honest state, never a fabricated one.

| Transition | Failure | Detection | Outcome |
|---|---|---|---|
| PREPARE sent | target gone / no ACK | existing PrepareRetry (2s) ×3 | CANCEL epoch; if source still resident → stays Live(A); else → Dormant |
| Handoff baseline (source) | source can't produce (crashed, non-resident) | request retry ×2 then residency check | H substitutes CoordinatorCache dormancy entries; target becomes Activation instead of Handoff |
| Baseline apply (target) | materialization failures (`data/type/segment-inactive/spawn/not-unit`) | NACK w/ reason + missing list (existing) | retry after target reports residency for S (event-driven, replaces blind 3s `UnreadyUntil`); ×3 → Dormant |
| Baseline apply (target) | PERMANENT failure: no entity data behind a live identity (world-database divergence) | target-side: `data`/`identity` failure code; host-side: `PermanentNetIds` in the ack (2026-07-15) | target self-heals by respawning from the baseline entry (`TryRespawnFromBaseline`, once per lifetime); if 3 identical-permanent-missing NACKs still accrue, host PINS the entities to the source as explicit owners — rosters exclude fixed owners, so the next retry completes and the segment stops being hostage. Transient codes (`segment-inactive`, `spawn:*`, `egm`) never count toward the pin |
| Entity data divergence at rest | a peer's EntityManager lost data another peer still simulates (not detectable at handoff edges alone) | **SegmentRosterAudit** (2026-07-15): owners broadcast identity rosters (netId, lifetime, type, pos) for owned+streamed segments ~4/s round-robin | receivers heal missing data via `TryRespawnFromBaseline`; reverse case (local live entity absent from owner's roster) logs on the 2nd consecutive audit; counters `rosterAudits=tx/rx divergence=found/healed/pinned` in `[Residency]` |
| Availability promotion | candidate has a GameObject but no EntityManager data (can't actually simulate) | `HasSimulableEntityData` gate on both the local-commit and remote-prepare paths (2026-07-15) | promotion declined (`no-entity-data` gate) — the starved puppet stays a puppet until the audit/baseline heal restores its data |
| COMMIT broadcast | peer missed COMMIT | reliable channel + lease snapshot replay on reconnect (existing `Snapshot()`) | idempotent re-apply |
| First snapshot after commit | owner never streams | **FirstSnapshotDeadline** (new, e.g. 2s per committed lease) | host-side alarm counter + forced audit: owner resident? no → dormancy path; yes → log + re-request; never silent |
| Dormancy commit | lost mid-unload | ResidencyReport shows owner left w/o commit | CoordinatorCache fallback, flagged `origin=CoordinatorCache` counter |
| ResidencyReport | lost/duplicated | monotonic report revision per peer | full-set resync request (reports carry set-hash; mismatch → full retransmit) |
| Activation | target fails to materialize | NACK ×3 | remains Dormant; interaction claims stay queued with TTL (drop + counter after e.g. 30s) |
| Dormant damage claim | no resident peer can own | queue TTL (15s) | drop with `DormantClaimDropped` counter. Decision (2026-07-13): a claim can only originate from an attacker who had a local collider at fire time, so an unownable claim is a sub-second unload/disconnect race — not worth an in-absentia HP ledger. The normal case (attacker still resident) resolves via forced lease + replay |
| Owner disconnect mid-handoff | pending epoch to/from lost peer | existing OnPeerLost cancel | epoch cancelled; resolve by §5.5 |
| Host disconnect mid-anything | migration | election (existing) | all leases void; world Dormant; re-activation storm from residency reports (§5.5) |
| Starved puppet (legacy path) | live entity, owner streams nothing | existing detector | now only possible transiently; recovery = host audit (owner resident?) instead of per-entity promotion loop; per-entity promotion (FixedOwner grab) kept only for entities with valid canonical state (always true now) |
| Conflicting live copies | two peers both simulate | epoch mismatch drops the loser's durable events (existing) + new `DualSimulatorDetected` counter via snapshot sender audit | loser demotes on next lease apply; log fingerprint |

---

## 8. Determinism budget

What may rely on the seed (verified by the existing four-digest barrier — keep it):

- World/terrain generation, entity identity (instanceId order), placement, plant/fruit structure,
  tile visual variants. These are proven deterministic today (`DeterminismAudit`, barrier abort on
  mismatch) — this is the right scope.
- Pure functions of (runSeed, stable ids): e.g. generation-time loot tables, `DeterministicGeneration.Mix` scopes.

What must NEVER rely on the seed (always explicit events):

- Anything after go-live that touches: Unity physics (positions, falls, contacts), RNG consumed in
  callback/streaming order (`Rnd()` parameterless, distribution draws outside generation scopes),
  AI decisions, timing-dependent spawns, damage/death, container contents decided at
  open/destroy time, pickup rolls.
- Rationale: consumption-order divergence is unbounded even with identical seeds — the existing
  code already treats runtime as explicit (spawn/kill/mutation events); this design extends that
  to *prop physics* and *dormancy poses*, the two places where "same seed + local physics" was
  still being trusted implicitly.

---

## 9. Lessons from noita_entangled_worlds (NEW, v1.6.3, verified in source)

NEW's entity layer ("DES — Distributed Entity Sync", `ewext/src/modules/entity_sync*`,
`noita_proxy/src/net/des.rs`) independently converged on the same shape this document proposes,
which is strong evidence the shape is right. Its design statement: *"we completely disregard the
normal saving system for entities we sync. Each entity gets an owner. Each peer broadcasts an
'Interest' zone."*

### 9.1 What transfers directly (and what it validates here)

| NEW mechanism | What it validates in this design |
|---|---|
| Host proxy keeps a registry of **all** tracked entities; an R-tree spatial index holds only **unowned** ones, with `FullEntityData` (pos, hp, wand bytes, phys bodies) | **Dormant as first-class phase + canonical store (§2, §5.3/5.4).** "Unowned + stored state" is the default; nothing fabricates state, ever |
| `ReleaseAuthority(gid)` = upload full state to host **then** kill the local instance; beyond-radius entities are *stored, simulated by nobody* | **Dormancy commit as the release edge (I-10)** and **frozen-at-distance is an acceptable gameplay tradeoff** — a shipped, popular mod proves players tolerate it |
| Claims: every 7 frames each peer requests authority for unowned entities within 512 px of its camera; host `drain_within_distance` grants with full stored state | **Activation-on-residency (§5.4)** — grant is always accompanied by canonical state, target-side |
| Radius hysteresis: request 512 / hold 640 / transfer 512 / global 768–896 — and, critically, the *mod itself* controls entity lifetime, so authority range and simulation range **cannot** diverge | The precise inverse of PUNK's bug: NEW never has an authority/residency gap because one system owns both. PUNK's mod rides the game's segment streamer, so it must *observe* residency (ResidencyReport) instead of owning it — the central adaptation |
| Interest zones (1024 px enter / 1536 px exit): full `EntityInit` snapshot on enter, **delete all that peer's puppets** on exit; diffs flow only to interested peers | Interest baselines (§5.1) + a cleaner alternative to puppet starvation: replicas *of a live remote owner* are deleted when interest lapses, not kept frozen indefinitely |
| Deaths to out-of-interest peers ride a `SpawnOnce` buffer (corpse spawned + insta-killed when the peer later approaches) | PUNK's kill ledger + re-kill-on-stream-in is the same idea and already works; keep it |
| Chest loot: open request → owner rolls **once**, broadcasts result; gold/hearts/perks are per-player by explicit exclusion list | §6 container model (contents decided once, in the event) and the existing per-player pickup exclusion (LootFactory gate) |
| Kill puppets by inflicting lethal damage attributed to the responsible peer, so death scripts/loot run natively per machine | PUNK's `KillInstance` → `Die()` + per-machine instanced loot dedup — same pattern, already present |
| Deterministic worldgen + host-arbitrated dedup (`uniq_flags`: first requester wins the spawn, everyone else kills their copy) | Determinism used for *existence*, arbitration for *events* (§8). PUNK's manifest (deterministic instanceIds → netIds) is strictly stronger for identity; keep it |
| Time-budgeted, cursor-resumed processing everywhere (1–5 ms/frame budgets for diff capture, apply, authority spawning) | Materialization/baseline work in PUNK should adopt the same budget discipline (EGM builds are already 1 segment/frame — the protocol must tolerate that, hence `NotResidentYet`) |
| Per-peer FPS exchanged; velocities scaled by FPS ratio | Already covered by `AdaptiveSnapshotTiming` + velocity extrapolation |

### 9.2 Where this design deliberately differs

- **Per-entity vs per-segment authority.** NEW claims individual entities (R-tree, 64-bit random
  Gids); PUNK batches by segment. Segment batching fits PUNK better: the game's own
  streaming/spatial model is segment-based, entity density per segment is high, and the existing
  two-phase lease machinery is per-segment. NEW pays for per-entity with unvalidated claims
  (their code: `// TODO maybe check that authorities are correct?`) and accepted Gid-collision
  probability; PUNK's epoch-scoped leases + deterministic manifest are stronger on both counts.
  Per-entity is retained only for exceptions (minions, grabs) — same as NEW's "global entities"
  special class (bosses never released to storage, only transferred).
- **Host migration.** NEW has none: host disconnect ends the session (host-side persisted
  `SaveState` covers resume-later instead). PUNK requires live migration, hence I-12's replicated
  ledgers/commits — a place this design must go beyond the reference.
- **Residency source of truth.** NEW's mod kills game-resurrected copies on sight (`ew_des` tag)
  and spawns replicas itself — it *owns* residency. PUNK cannot (EGM re-instantiates from
  EntityData on every segment build), so residency is *reported and audited*, and the unload path
  is hooked rather than replaced.

### 9.3 Noita-specific pieces that do not apply

Pixel/chunk world sync (PUNK's terrain is already a host-authoritative cell ledger with
vicinity-first streaming); opaque native entity serialization blobs (Unity side has no
engine-native serializer — prefab-id + explicit state fields is the PUNK equivalent, and the
type-guarded identity already rejects mismatches); camera-centric interest (PUNK uses ship
positions); polymorph identity smuggling; shared-health arbitration; wand-RNG cast replay
(PUNK's `ProjectileSync` visual replay already fills that role).

---

## 10. Instrumentation (proof obligations)

Gauges/counters to add (extending `InstrumentationCounters` + F11 overlay + `[Profile]` lines).
Each maps to an invariant; several are release criteria for the migration phases.

| Metric | Kind | Invariant | Alarm condition |
|---|---|---|---|
| `OwnerWithoutSimulator` | per-peer gauge: leased segments ∉ residency set | I-4 | > 0 for > 1 report interval |
| `LiveEntityNoCanonicalObject` | gauge: netIds owned-by-me ∉ LiveEntities | I-3 | > 0 sustained |
| `BaselineSourceLive/DormantCounts` | fields on RuntimeBaseline + Ack | I-5 | Live entry from non-resident source (reject + count) |
| `FabricatedEntryRejected` | counter (transition guard) | I-5/I-7 | any |
| `FirstSnapshotDeadlineMissed` | histogram: commit→first snapshot per lease; misses | I-0 | any miss |
| `VisiblePuppetNoState` | gauge: active puppets with 0 snapshots ∧ no canonical rev | I-7 | any |
| `AuthorityHeldDuringUnload` | counter: DestroyGameObjects with owned entities, no dormancy commit sent | I-10 | any |
| `HandoffDuration{result,reason}` | histogram + labeled failures (prepare-timeout, nack-type, source-lost…) | §7 | p99 > 2s |
| `MaterializationFailureByType` | counter labeled entityId × reason | I-6 | novel reason |
| `SegmentRosterDigest` | periodic host↔owner digest audit per live segment | I-0 | mismatch |
| `DataObjectDisagreement` | scan: EntityData present ∧ (canonical object expected by phase) mismatch | I-0 | growth |
| `ContainerPoseDivergence` | on prop rest commit: |canonical−local| per observer | §6 | > 0.5u |
| `SnapshotProdBySegment` / `ConsBySegment` | per-peer rate map | I-0 | owner produces 0 for a Live segment |
| `InterestRouteRejection{reason}` | counter | §5.1 | growth |
| `DormantClaimQueueDepth/TTLDrops` | gauge/counter | §7 | TTL drops |
| `LeaseTableDigest` | periodic host↔peer lease-table hash exchange (+ client-side lease apply logging) | I-4 | mismatch — would have proven the observed split-brain from a single log |
| `BaselineReqApplyRatio` | rolling req vs apply per (source,segment) | §5.1 | requests ≥5× applies over 30s (the observed 10,821/8 storm) |
| `ActivationLatency` | histogram: residency/claim → commit | §5.4 | p99 > 1.5s |
| `CoordinatorCacheFallbackUsed` | counter (lost-owner dormancy source) | §5.3 | growth without disconnects |
| `DualSimulatorDetected` | counter | I-3 | any |

All exposed in the periodic `[Profile]`/instrumentation dump and the auto-sent logs, so any future
divergence names an entity, owner, segment, epoch, transition, and reason.

---

## 11. Host coordinator vs simulation owner

| Responsibility | Coordinator (host) | Simulation owner (any peer incl. host) |
|---|---|---|
| Lease grant/cancel/commit, epochs | ✔ (sole writer) | — |
| Residency truth | audits reports | reports adds/removes (reliable, revisioned) |
| Canonical dormant store | ✔ (writes on commits; broadcasts) | produces dormancy commits on unload/rest |
| Kill/mutation ledgers | ✔ periodic reconciliation (exists) | originates durable events for owned entities only |
| Interest routing / relay | ✔ (exists; direct routes optional, exist) | streams snapshots for owned live entities only |
| Claim queues (dormant damage) | ✔ holds + drains on commit | applies after activation |
| Late-join catch-up | ✔ (manifest, ledgers, dormancy, terrain — exists + dormancy) | — |
| Migration inputs | replicates ledgers/commits to all (I-12) | retains replicas; candidate for promotion |
| Simulation of the world | **only where the host player is resident** | wherever resident + leased |

---

## 12. Protocol changes

New messages (channel 2, reliable, idempotent):

| Msg | Fields | Notes |
|---|---|---|
| `ResidencyReport` | peer slot, report rev, added[], removed[], setHash | sent on EGM segment build/destroy; full resync on hash mismatch |
| `SegmentDormancyCommit` | segment, epoch, entries[], digest | owner → host on unload (I-10) |
| `PropRest` | netId, lifetime, pose, rev | micro-dormancy (§6); may batch |
| `DormancyDigest` | segment, rev, hash | host → all; late-join + audit anchor |

Extended messages (additive):

| Msg | Change |
|---|---|
| `RuntimeBaselineMsg` entry | per-entry flags byte: `origin {Live, DormantCommit, CoordinatorCache, Generation}`, `entityClass {Unit, Prop}`; varint `LastSimTick`; varint `CanonicalRev` |
| `RuntimeBaselineMsg` header | `SourceLiveCount`, `DormantCount` (audit fields) |
| `RuntimeBaselineAckMsg` | `ReasonCode {Complete, NotResidentYet, SpawnFailure, TypeMismatch, DataMissing}` per missing id (replaces string details) |
| `RuntimeBaselinePurpose` | + `Activation`, + `Dormancy` (distinct from Interest/Handoff; I-5) |
| `SegmentLeaseMsg` | owner value `none/DORMANT` legalized (today host is implicit default); phase `DORMANT-COMMIT` |
| `DamageRequestMsg` handling | dormant target ⇒ forward to host claim queue (never dropped) — behavioral, no wire change |

Removed behavior (no wire change): fabricated default entries in `BuildRuntimeBaselineRoster`;
`route.Ready` implying stream expectations for dormant entries; Unit-only roster filter; the dead
`SegmentLeaseAck` receive path (no sender exists in the codebase — remove or implement, never
leave a second commit trigger half-wired).

One data-hygiene fix independent of the rest: `ApplyRuntimeBaseline` writes `FullState[netId]`
**before** validating the entry can be bound (`EnemySync.cs:1320-1321`), so fabricated entries
poison the host's canonical cache — the same cache used as the lost-owner fallback. Under the new
model the store only accepts origin-tagged writes.

Compatibility: all changes are additive or behavioral; a protocol version bump gates mixed-version
lobbies (mod already rejects version mismatch at join).

---

## 13. Phased migration plan

Each phase is independently shippable and reversible; each has release criteria measured by §10.

**Phase 0 — Measure (no behavior change).** ✅ **Implemented in v0.1.81.**
Shipped: `[Residency]` report line (every ProfileReportInterval) with `ownerNoSimSegments`
(leases owned by me ∉ my EGM activeSegments — on the host this counts exactly the mechanism-A
segments), `fixedOwnedNotLive`, per-origin baseline entry counts, first-snapshot
observed/missed/max (2 s deadline per committed lease, judged only when this machine holds a
live object the owner should service), handoff duration in the `[Lease]` commit line +
`handoffRejects`. `VisiblePuppetNoState` remains covered by the existing `[EntityAudit]`
neverSnapshot gauge.
Criteria: metrics reproduce the reported failures on demand (park-at-band test, §14).

**Phase 1 — Stop lying (small, surgical).** ✅ **Implemented in v0.1.81.**
1. ✅ `BuildRuntimeBaselineRoster` tags every entry with `BaselineEntryOrigin`
   (Live/LastKnown/Generation/CoordinatorCache, folded into the roster digest); the fallback
   chain is now TryCollectEntry → lifetime-checked `FullState` cache → generation data flagged
   `Generation`. Targets apply Generation entries as position-only (no snapshot push, no vitals
   writes — HasSnapshot stays false so starvation stays honest), and `FullState` is written only
   for entries that actually bound, with provenance tracked (`FullStateOrigins`) so the
   lost-owner cache can never launder a guess into "last known simulator state".
2. ✅ Client-side dormant drop fixed: owners bounce unservable claims to the host
   (`MsgType.DamageUnservable` carrying the full original request); the host queues + forces the
   lease toward the attacker for ANY owner. Also fixed en route: dormant-claim replays to remote
   owners were dead on arrival (every peer had already recorded the RequestId as seen) — replays
   now carry a `Replay` flag that bypasses the dedup. Claims expire after 15 s (§7 decision).
3. ◐ Props are now in rosters (class-flagged, bindable on targets — positions repair at
   baseline time). Frozen holds on non-owners are **deferred to Phase 4**: freezing prop physics
   before residency-constrained leases guarantee a streaming owner would leave crates hanging
   mid-air; under today's geometry local physics is the less-wrong default.
4. ➕ (pulled forward from Phase 3 for field relief) Silent-owner promotion: the availability
   gate now allows promotion of a concrete replica after >10 s of total owner silence
   (never-snapshotted or long-stale). The observed 30-minute frozen statues self-heal in ~10 s
   through the existing two-phase prepare/ACK; the 5–10 s stale band still defers so transient
   hiccups don't churn authority.
Criteria: `baselineOrigins` gen-count visible and shrinking phase-over-phase; unbreakable-entity
reports stop; full-HP-heal-on-handoff stops.

**Phase 2 — Residency truth.** ✅ **Implemented in v0.1.82.**
`ResidencyReportMsg` (full-set, rev-ordered, debounced 0.25 s, hash-gated — hook-free: polls the
EGM's activeSegments each entity tick); host lease assignment constrained to resident peers
(`SelectOwner` = forced-claim → sticky-while-resident → closest resident → Dormant); unleased
segments and lease defaults are **`DormantOwner` (255)** — the host-default is gone, and with it
the Chebyshev acquire/keep radii and the Retained-check bug; `DestroyGameObjects` prefix →
`SegmentDormancyCommit` with the owner's final origin-tagged states (I-10), even when empty so
the host transitions immediately; commit-less owner departures (crash) fall to the coordinator
cache after a 2 s grace; `OnPeerLost` transitions the lost peer's leases to Dormant from cache.
Criteria: `ownerNoSimSegments`≈0 sustained in 2-player soak; band test passes.

**Phase 3 — Dormancy store + activation.** ✅ **Implemented in v0.1.82.**
Activation IS the residency-constrained grant: a Dormant segment gaining a resident peer rides
the ordinary PREPARE/baseline/ACK path with source=DormantOwner, which resolves to the
coordinator's state cache (the Phase-1 lost-owner fallback, now fed by dormancy commits).
Dormancy commits are relayed to every peer (I-12) so any survivor can be promoted with the
store intact. Dormant damage: host queues claims for owner-255 targets (including its own
shots) and forces the lease to the attacker; late join replays the canonical cache
(`DormantStateMsg` chunks, origin-tagged) after the event catch-up. The starvation detector
skips dormant-owned entities (activation is already in flight — we hold the object, so our
residency report is driving a grant).
Criteria: activation latency within lease-scan cadence (+~100 ms handoff); `VisiblePuppetNoState`
transient-only; late joiner sees committed vitals/poses.

**Phase 4 — Props fully owned.** ✅ **Implemented in v0.1.82 (rest micro-dormancy deferred).**
Non-owner props are **held** (`PropPuppet.Hold`: kinematic freeze at canonical pose, no
self-expiry, interpolates while the owner streams, restored to local physics when ownership
arrives); prop damage routes to the simulator through all three damage chokepoints (fixes
per-machine crate HP divergence); prop states ride rosters and dormancy commits. Loot contents
are now **deterministic per death**: `DropLoot` runs inside a `DeterministicGeneration` scope
seeded on (runSeed, netId/fruit, mutationRevision), so every machine's per-player copy rolls the
same contents with zero protocol traffic — chosen over contents-in-event (no LootDropper
surgery, same guarantee). Deferred: `PropRest` micro-dormancy — purely a bandwidth optimization
(stationary-prop heartbeats are 0.75 s and cheap; durable rest poses already come from dormancy
commits); revisit if prop heartbeats ever profile hot. Non-owners can no longer push
remote-owned props (physics ownership is exclusive by design, §6); if playtests want
push-to-claim, wire collision impulses into the claim path later.
Criteria: `ContainerPoseDivergence` p99 < 0.5u; two-instance container scenario passes.

**Phase 5 — Migration hardening + cleanup.** ✅ **Implemented in v0.1.82 (one deviation).**
Promoted hosts continue the epoch sequence (`OnPromotedToHost` — restarting at 1 would lose
every PREPARE to the higher epochs peers already hold; a latent pre-existing bug); dormancy
commits + kill/mutation ledgers are replicated to all peers, so a promoted host's canonical
store is intact; the dead `SegmentLeaseAck` receive path (no sender ever existed) is removed;
readiness backoff (`UnreadyUntil`) clears event-driven when the target's residency report shows
the segment active. **Deviation:** the per-entity starvation/promotion machinery is NOT deleted
— it is demoted to a rarely-firing backstop. Deleting a working recovery path requires soak
evidence across many host-death cycles that a single automated session cannot provide; remove
it only after `starvedRequests` stays 0 across several real playtests.
Criteria: post-migration world equivalence; zero fabrication counters across soak suite.

---

## 14. Test scenarios (two-instance loopback harness exists: AutoStart/AutoReady/AutoFly)

Automated pass/fail = instrumentation dump assertions at run end (logs are already auto-sent).

| # | Scenario | Steps | Pass | Fail |
|---|---|---|---|---|
| T1 | **Band starvation (repro of this bug)** | Host parks; scripted client flies to Chebyshev-4 from an enemy segment, host approaches it | every visible unit puppet gets snapshot ≤2s **or** is Dormant-frozen with `CanonicalRev>0`; `OwnerWithoutSimulator`=0 | any `FirstSnapshotDeadlineMissed` / `VisiblePuppetNoState` |
| T2 | Interest materialization | client owns combat area; host flies in | route ready ≤1s; first snapshot ≤500ms after Ack; 0 fabricated entries | deadline miss |
| T3 | Owner unload | client owns segment w/ enemies, flies away | `SegmentDormancyCommit` precedes object destruction; host store rev++; observers freeze at final pose (≤0.5u of owner's last sim pose) | `AuthorityHeldDuringUnload`>0 |
| T4 | Dormant damage claim | host shoots frozen crate/enemy owned by nobody | activation ≤1.5s; damage applied exactly once (dedup counters); entity killable | claim TTL drop; HP mismatch between peers >1% |
| T5 | Owner disconnect | kill client process mid-combat in its owned segment | host re-activates ≤2s from CoordinatorCache; no full-HP resets (HP within last-snapshot ±ε); no dual simulators | fabrication counter; `DualSimulatorDetected` |
| T6 | Disconnect mid-handoff | kill client between PREPARE and ACK | epoch cancelled; segment Live(host) or Dormant; no stuck `Pending` after 5s | pending lease alive >5s |
| T7 | Container fall | crate ledge segment; client activates; both watch | one simulator; observer interpolates; rest pose equal ≤0.1u on both; late-join sees rest pose not generation pose | `ContainerPoseDivergence` |
| T8 | Late join | 2p run 10 min (kills, terrain, containers), third instance joins | digests match; joiner's kill/mutation/dormancy replay complete; no zombie entities; joins never blocked on world state | roster digest mismatch |
| T9 | Host migration | host killed mid-run | election; world re-activates; ledger/dormancy equivalence digest across survivors | store divergence |
| T10 | Kill/zombie regression (existing bugs stay fixed) | re-stream killed entities, loot dedup, plant fruits | 0 duplicate drops; 0 death-loop repeats | `DropLoot` repeat stack |
| T11 | Churn soak | 2 players orbiting each other across segment boundaries 10 min | `AuthFlips` bounded; no oscillating handoffs of same segment >1/min; frame p99 within budget | lease ping-pong |

---

## 15. Expected costs

| Resource | Delta vs today | Reasoning |
|---|---|---|
| **CPU (host)** | ↓ slightly | stops fake-owning the world (it never simulated it anyway); lease scan driven by report events instead of 0.5s distance scans; canonical store writes are event-driven and O(entities-in-segment) on unload only |
| **CPU (clients)** | ~flat to ↓ | starvation detector loop removed for the steady state; puppet count unchanged; props frozen instead of locally simulated (fewer physics bodies awake) |
| **Bandwidth** | ↓ in steady state, small ↑ at edges | new: residency reports (~10–40 B per segment change), dormancy commits (~26–30 B/entity, only on unload edges), PropRest (~15 B/prop, once per landing). Removed: perpetual starvation requests/promotion retries; snapshot expectations for dormant entities; prop divergence repair never needed. Dormant segments cost **zero** streaming (today host-"owned" dormant areas cost retry chatter) |
| **Memory** | +canonical store | ≈ `FullState` (already exists) + dormancy revs: ~40–60 B/entity touched; 6.5k entities ⇒ ≲400 KB worst case, replicated to peers for I-12 |
| **Latency** | activation adds ~RTT + EGM build (1 segment/frame) on first contact with a Dormant segment | masked in practice: activation begins at *stream-in* (radius 3 ≈ 75 u ahead of the player), and combat claims reuse the existing forced-lease path; handoffs unchanged (two-phase, ~1–2 RTT) |

---

## 16. Risks & open questions

1. **Hook fragility**: `DestroyGameObjects` prefix must run before object teardown; verify the
   game never unloads via other paths (e.g. `UnloadEntity` on `EntityMovedToNewSegment` — needs
   the same capture for single-entity unloads).
2. **Residency report freshness**: EGM builds one segment/frame; a fast-moving player's reports
   lag actual residency. Mitigation: `NotResidentYet` NACK + event-driven retry (already
   designed), and lease grants may precede full build only in PREPARE (never COMMIT).
3. **Canonical-store replication cost under churn**: broadcasting every dormancy commit to all
   peers is the simplest I-12; if bandwidth profiling objects, fall back to host-only store +
   periodic checkpoint (weaker migration guarantee, bounded staleness).
4. **Prop feel**: frozen-at-canonical props may look "nailed down" if activation is slow;
   activation-on-residency should make this invisible, but needs playtesting (T7).
5. **Non-unloadable / bespoke entities** (stations, ships, minions, plants): stay on their
   bespoke sync paths (ShipSync, MinionSync fixed owners, plant fruit tombstones). The roster
   builder must keep excluding them from segment rosters *explicitly* (flag, not heuristics).
6. **Two-simulator race during migration re-activation storm**: mitigated by epochs (all leases
   void → new epochs from the promoted host), but the storm itself needs the T9 soak to size
   PREPARE budgets.
7. **Unknown entity classes** (future game updates): type-guarded identity (existing `IdKey`
   entityId fold) + `MaterializationFailureByType` will surface new types; default posture for
   unknown = Dormant-frozen, never local-sim.
8. **Open**: should *enemy AI targets* transfer in handoff baselines (currently aim/state bytes
   only)? Deferred — puppet re-acquisition on the new owner has been acceptable.
9. **Resolved (2026-07-13)**: claim-queue TTL, not direct-apply. Damage requests can only
   originate from an attacker with a concrete local object, so an unownable claim is a
   sub-second unload/disconnect race; a 15 s TTL with a drop counter covers it without an
   in-absentia HP ledger or its farming edge cases.

---

## Appendix A — Where today's code maps onto the design

| Existing | Fate |
|---|---|
| `AuthorityManager` leases/epochs/two-phase | kept; owner selection replaced (residency-driven), default-host removed |
| `EnemySync` freeze/baseline/ack plumbing | kept; roster builder rewritten (origin-tagged, no fabrication, props included) |
| `BuildCachedBaselineRoster` | becomes the CoordinatorCache dormancy source (explicit, counted) |
| Starved-puppet detector + two-phase promotion | demoted to audit + rare per-entity grab; cooldown loop removed |
| `RemoteEntityPuppet` / `PropPuppet` / `DuplicateEntityInert` | kept as-is (they are the per-machine state machine §4.2) |
| `NetIds` manifest/lifetimes/orphans | kept unchanged (I-1/I-2 already hold) |
| Kill ledger / mutation revisions / loot dedup | kept; dormancy revs reuse the same monotonic pattern |
| `DamageSync` dormant queue (host-only) | generalized to coordinator claim queue for all owners |
| `OnDormantHit` forced leases | becomes the interaction-claim path of §5.4 |
| Interest routes / direct routes | kept; readiness semantics corrected (per-class) |
