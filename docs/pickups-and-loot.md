# Pickups & loot

## Anatomy

```
InteractiblePickup<TData> : SavableComponent<TData>, LootDropper.IDroppedLoot   (abstract)
├── ConsumablePickup : InteractiblePickup<Data>   Data: Consumable consumable
└── IngredientPickup : InteractiblePickup<Data>   Data: Ingredient ingredient

Loot pipeline (on the DROPPER, e.g. enemies/containers/fruits):
LootDropper   — lootSelectionMethod, dropTable (DropTable) or fixed loot (DroppabbleItem),
                dropForce/dropAngle/spawnOffset
LootSelector  — rolls the table        LootFactory — instantiates the drop
LootCollector — pickup magnet/collection on the ship side
ConsumablePickupFactory / IngredientPickupFactory — runtime pickup creation
```

Two spawn origins matter for sync:
1. **Generated pickups** — placed at level gen, in the netId manifest (**DET**).
2. **Runtime drops** — spawned on death/kill of enemies, containers, fruits.

## The instanced-loot rule (deliberate design)

**Loot is per-player, progression is shared.** Runtime drops are NOT synced entities:
each machine spawns its OWN copy of a kill's drops (`KillInstance` replays `spawnOnDeath`
locally; fruit loot dedups via `DroppedPlantFruitLoot` per machine). Every player collects
their own instance — no loot stealing, no pickup races to arbitrate.

Consequences to keep in mind:
- Drop ROLLS can differ per machine (different loot from the same crate is by design —
  unless the drop table roll uses a synced seed; verify if identical drops matter).
- A pickup floating on your screen doesn't exist on your teammate's.
- Economy: each player pays their own costs; `EconomyStash` handles the shared-vs-personal
  resource policy the mod adds.

## Sync status

| Aspect | Status | Mechanism |
|---|---|---|
| Generated pickups (existence) | **DET** | manifest netIds; collection removes locally — **GAP?**: does picking up a GENERATED pickup replicate? If not, both players can grab the same placed pickup. `LootDiag` exists for drop diagnostics — verify with a generated consumable. |
| Runtime drops | **LOCAL** | instanced per player, by design |
| Pickup physics while lying around | **LOCAL** | each machine's copies are its own |
| Consumable USE effects | per-player | consumables act on the user's ship; module/stat changes ride ModuleGridSync where relevant |
| Shared resource tanks | **EVENT/STATE** | `RunData.SharedResourceTanks` added to every ship's Unit — kept coherent via the mod's economy/stash sync |

**The one to test first:** the generated-pickup double-collect (two players grabbing the
same placed ingredient). If it reproduces, route it like progression: collect event keyed
by netId with a ledger, applied on Data.
