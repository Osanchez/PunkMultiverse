# Plants

## Anatomy

Plants are one savable entity whose Data holds an entire procedural tree:

```
SavableEntity (root)
└── EntityPlant : SavableComponent<EntityPlant.Data>
      Data:
        rootBranch   : Branch        — recursive branch tree (the plant's shape)
        plantData    : EntityPlantData (ScriptableObject — species config)
        fruits       : List<Fruit>   — the interactive part
        branchEndPoints (float4 list, gen-time)
      runtime visuals:
        PlantBranchVisualBase / CurvedPlantBranchVisual / CurvedEntityPlantSegment
        EntityPlantFruit : MonoBehaviour (one per fruit)
            — IProjectileListener + IExplosionListener (shootable!)
            — Fruit { … } reference into Data.fruits
            — Killed event
```

Generation: `PlantGenerator` / `PlantGeneratorJob` / `EntityPlantGeneratorJob` at level
gen — fully deterministic (the mod checksums plant count+hash at run start:
`plants 146/5E2F92DC89F2EBB0`).

The interactive gameplay surface is the **fruit**: shooting/exploding a fruit kills it
(drops resources). Branches are cosmetic/structural.

## Sync status

| Aspect | Status | Mechanism |
|---|---|---|
| Plant existence/shape | **DET** | seed-generated; verified by the plants checksum at GO_LIVE |
| Fruit kills | **EVENT** | the mod's PlantLedger: killedFruits announced/applied per (plantNetId, fruitId); dedup via `KilledPlantFruits` |
| Fruit loot | **LOCAL** | instanced per player (`DroppedPlantFruitLoot` ledger prevents double drops per machine) |
| Fruit regrowth (if any) | **GAP?** | if fruits regrow over time, regrow timing is local — machines could disagree on a fruit being present. Verify whether `plantData` has regrow. |
| Branch damage | **LOCAL/n-a** | branches don't appear damageable (no DamagableResource on branch visuals) |
| Mutations | **EVENT** | `PlantFruitMutationRevisions` — revisioned mutation state per fruit rides the durable-mutation channel |

**Diagnostics:** `[PlantLedger] killedFruits=N announced=N applied=N missingLiveChild=N`
every report interval — `missingLiveChild` counts fruit-kill events that arrived for a
plant whose fruit child object wasn't found (ordering/stream-in gap indicator).
