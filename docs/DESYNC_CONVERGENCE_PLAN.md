# Enemy-Desync Convergence Fix — Design / Plan (for review)

Status: **proposal, not implemented.** Nothing in the sync core is touched yet.
Companion to `ENTITY_SYNC_ARCHITECTURE.md`. Diagnosis source: two host logs
(`LogOutput.log` 4p, `LogOutput_Tikken.log` 2p+freeze) + code trace.

## 1. Problem

The residency/authority architecture is implemented and correct — these are **convergence
failures at the residency edges**, not the old "host fakes ownership" flaw. Two roots:

- **RC1 — interest baselines never converge.** The host decides a peer is "interested" in a
  segment by *ship distance* (`IsGroupInterestingTo`, ~95u, `EnemySync.cs:1462-1481`), which
  *leads* the game's radius-3 streamer. It requests an enemy baseline for a segment the peer
  hasn't activated → peer NACKs `segment-inactive` (`EnemySync.cs:1952`), a code deliberately
  excluded from escalation (`:1968`) → blind 1.5s retry **not gated on residency**
  (`:1508`) → route never `Ready` → owner's snapshots for that enemy are dropped for that peer
  (`SelectInterestedGroups`, `:1443-1452`) → enemy frozen/absent on that peer. This is the
  641× `BaselineRoster not ready` storm.
- **RC2 — lease ping-pong.** `SelectOwner` is correctly sticky-while-resident and otherwise
  "closest resident" (`AuthorityManager.cs:502-521`) — the rule is right. The problem is
  *residency itself flickers* on boundary segments (game destroys GameObjects past radius-3;
  residency reports are debounced) → the lease flips 5–20×/s → each flip resets puppets
  (`RemoteEntityPuppet.ResetSnapshots`) and floods reliable messages.

Cascade: RC1/RC2 → phantom attacks (RC4), artillery telegraph loss (RC5), and the reliable-message
flood that contributed to the host freeze (RC6 — the freeze *loop* is already fixed).

## 2. Invariants this MUST NOT break

1. **Ownership = host-authoritative + sticky client-grab.** Never re-introduce "closest player
   owns it" re-optimization. `SelectOwner` already honors this; do not change the rule, only the
   residency signal feeding it.
2. **Leases only within reported residency; Dormant is first-class; never fabricate baselines.**
3. **No terrain/state gating; joins never block or diverge.** Interest changes must *reduce
   redundant requests*, never *withhold* state a resident peer needs to converge.
4. **Identity keyed on instanceId, not position.**

## 3. Change 1 — RC1: residency-gate the interest baseline (core)

Mirror what leases already do (`UnreadyUntil`, cleared on `ResidencyReport`,
`AuthorityManager.cs:255`) for interest routes.

- Expose `AuthorityManager.IsResident(slot, key)` (currently private, `:273`) — a clean read.
- In `EnsureInterestRoute` (`EnemySync.cs:1483-1512`): only (re-)issue `BeginRuntimeBaseline(Interest)`
  when the target **is resident** in the segment, OR its residency is unknown (correctness-over-
  filtering, matching `IsGroupInterestingTo:1465`). A not-yet-resident target *cannot bind the
  baseline anyway*, so this withholds nothing usable — it just stops the wasted 1.5s re-request loop.
- Event-driven wake: when a `ResidencyReport` marks the target newly resident in a segment that has
  a pending (un-`Ready`) interest route, trigger the interest baseline immediately (analogous to the
  `UnreadyUntil.Remove` hook at `AuthorityManager.cs:255`). Add a small callback/queue so
  `AuthorityManager` can nudge `EnemySync` on that transition.
- Do **not** drop the owner's snapshot group for a target that *is* resident and merely mid-
  materialization — only for genuinely non-interested/non-resident targets. (Guards invariant 3.)

Risk: low. The gate applies only to targets that can't use the baseline yet; convergence once
resident is unchanged (actually faster — the event wake beats the 1.5s poll).

## 4. Change 2 — RC2: lease-flip hysteresis on the residency signal (core)

Keep the ownership *rule*; damp the *flicker*.

- **Minimum dwell.** After a lease commits to an owner, don't flip for ~1–2s unless the owner is
  truly gone (peer lost / explicit dormancy commit). A per-segment `lastCommittedAt` checked in the
  scan (`Tick:390-479`) before adding to `_claims`.
- **Residency grace.** Treat an owner as still-resident for ~1–1.5s after its residency report last
  dropped the segment, *if* it was resident very recently — so a one-frame flicker doesn't hand off.
  A `(slot,segment) -> recentlyResidentUntil` map updated on `ResidencyReport`; `SelectOwner`'s
  `IsResident(current.Owner, key)` becomes `IsResidentOrGrace(...)`. **Only** for keeping the
  *current* owner — never for granting a new lease (invariant 2).
- `OnPeerLost` (`AuthorityManager.cs:289`) still clears immediately, so grace never holds a lease
  for a peer that actually left the session.

Risk: a legitimate handoff can lag by up to the grace window (~1s) — acceptable vs. 20 flips/s.
Both durations should be `NetConfig` knobs so they're tunable without a rebuild.

## 5. Change 3 — RC4/RC5 visuals (dependent; after RC1 lands)

- **RC4 phantom-attack visual.** Enemy `FireEventMsg` is unreliable (`ProjectileSync.cs:548`).
  Once RC1 restores snapshot flow, optionally send it **reliably to the specific interested/damaged
  peer** — *not* reliable-to-all, which would re-inflate the storm. Defer until RC1 cuts baseline
  traffic.
- **RC5 artillery telegraph.** Replicate the warmup as a state (extend the 0/1/2 fire byte with a
  warmup value) and draw the aim-laser during warmup in `DriveBeam` (`RemoteEntityPuppet.cs:200-230`),
  driven by the already-synced aim vector. Small protocol addition; only helps once snapshots flow.

## 6. Change 4 — explosives authoritative detonation (independent)

Add `ProjectileDetonateMsg` (owner broadcasts detonate + world pos keyed by the existing
`NetworkProjectileIdentity`; peers snap the cosmetic boom there and destroy their copy; peer
projectiles stop self-detonating, with a short TTL GC fallback). **VFX/lifecycle only** — damage and
terrain stay single-sourced (owner `DamageSync` + authoritative `CellDiff`), so no double-count.
Independent of RC1/RC2; can land anytime. (Full detail in the explosives investigation.)

## 7. Rollout & verification

- Land **RC1 + RC2 together** (they interact). Put dwell/grace/interest durations behind `NetConfig`.
- Verify with `mp-test` (two-instance loopback): drive ships apart via `autofly`, watch F10 NetDiag —
  `authChurn` should fall from 5–20 flips/s to ~0–2; the `BaselineRoster not ready` storm should
  clear; enemies should stay materialized on both peers across segment boundaries; no phantom hits.
- Success metric = interest-request rate, `authChurn`, and `materialized/missing` all trend down.

## 8. Open decisions

1. Dwell (~1–2s) and residency-grace (~1–1.5s) durations — expose as `NetConfig` knobs? (recommend yes)
2. RC5 telegraph replication — worth a protocol field now, or leave artillery telegraph owner-only
   for a first pass?
3. Order: RC1+RC2 (core) first → verify → then explosives detonation → then RC4/RC5 visuals. OK?
