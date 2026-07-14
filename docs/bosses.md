# Bosses

## How the game models a boss

There is **no Boss class** — a boss is a normal enemy entity (see [enemies.md](enemies.md))
with one extra MonoBehaviour and some UI/music wiring:

```
BossStateActivator : MonoBehaviour      — on the boss prefab; calls into BossStateManager
BossStateManager                        — global service; CurrentBoss (SavableEntity),
                                          EnteredBossState / ExitedBossState events
BossHealthbar / BossHealthbarRow        — UI bound to CurrentBoss's DamagableResource
InGameMusicController                   — listens for boss state (also for station unlocks)
```

`BossStateActivator.EnterBossState(savableEntity)` / `ExitBossState` toggle the global
boss state — the healthbar appears and music changes while `CurrentBoss` is set.

Everything else about a boss — HP tank, shields, AI states, weapons, movement, loot — is
the standard enemy composition, just with bigger numbers and richer State graphs.

## Miniboss variants observed

Elite/miniboss-tier prefabs in the roster (by naming + encounter behavior):
`Unit_FlyDad`, `Unit_FlyAlfa`, `Unit_Cross_Alpha`, `Unit_Cross_Jock*` (weapon-specialist
elites), `Enemy_Turret_Worm` (the stationary "worm turret" fought in the frozen-boss
incident). Whether each carries a `BossStateActivator` is prefab data — verify in-game by
whether the boss healthbar appears.

## Sync status

| Aspect | Status | Notes |
|---|---|---|
| Boss entity itself | **STATE/EVENT** | identical to any enemy — snapshots, kill ledger, HP scaling |
| Boss state enter/exit (healthbar, music) | **GAP?** | `BossStateActivator` triggers locally. If its trigger is proximity/aggro-based it fires on whatever machine's conditions are met — a non-owner may get no healthbar/music while fighting the puppet, or get them while the owner disagrees. **Verify: fight a boss as the non-owner and check whether the healthbar appears.** |
| Boss on a dormant segment | fixed-ish | the "mini boss not shooting" incident: a dormant-owned boss is a mannequin until claimed. 0.1.85 claims nearest-first with a 16/scan allowance; turret-type bosses still engage at long range — watch the `[Availability] lease flush woke N` line timing vs first player shot. |
| Boss kill rewards | **LOCAL** per player | loot is instanced per player (see pickups-and-loot.md) |

**Sync design note:** if the healthbar gap is real, the cheapest fix mirrors the mod's
progression events: patch `BossStateManager.EnterBossState/ExitBossState`, broadcast the
boss netId, and drive the manager remotely (dedup by netId, `_applyingRemote` guard —
same shape as `ProgressionSync.CaptureUpgrade`).
