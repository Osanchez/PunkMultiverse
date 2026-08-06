# Shops, economy & progression

Two currencies, one run-scoped shop list, a vault that persists within a run, and a small
amount of meta-progression that survives it.

Exact signatures: [`api/shops-and-economy.md`](api/shops-and-economy.md).

## Currencies

`Price` is a struct with exactly two currency types:

```csharp
public enum CurrencyType { Ingredient, Resource }
```

| Currency | Held in | Checked against |
|---|---|---|
| `Ingredient` | the `Vault` | `vault.AmountOf(ingredient)` |
| `Resource` | the ship's `Unit` tanks | `unit.GetResource(resource)` |

`amount` is a **float**, but every transaction uses `AmountFloored` (`Mathf.FloorToInt`). A
price of `9.9` costs 9. Do not display the raw float as the price.

An item's `price` is a `List<Price>` — a single item can cost several currencies at once, and
all of them must be affordable.

## Buying

```csharp
public bool Purchase(ShopItem shopItem)
{
    if (!CanPurchase(shopItem)) return false;

    if (!runData.AllShopItemsAreFree)
        foreach (Price item in shopItem.price)
        {
            if (item.currencyType == Price.CurrencyType.Ingredient)
                vault.Remove(item.ingredient, item.AmountFloored);
            if (item.currencyType == Price.CurrencyType.Resource)
                ship.Unit.GetTank(item.resource).Value -= item.AmountFloored;   // (!)
        }

    if (shopItem.RepeatInShop) { shopItem.IncreasePrice(...); shopItem.CreateNewItem(); }
    else                        ShopItemList.Remove(shopItem);

    runData.RegisterModuleDropped(shopItem.Module.Data);
    ...
}
```

**The marked line goes through the resource-tank setter**, which means it inherits that setter's
behaviour: an `isInfinite` tank silently refuses to decrease. With vanilla's unlimited-resources
option on, every resource-priced item is affordable *and* costs nothing. This is the same
mechanism that makes ships unkillable — see
[`VANILLA_GOTCHAS.md`](VANILLA_GOTCHAS.md#an-infinite-tank-silently-refuses-to-decrease).

`runData.AllShopItemsAreFree` short-circuits both the affordability check and the deduction.

## Repeat items re-roll, and get more expensive

If `ModuleData.repeatInShop` is set, buying does not remove the entry. It does two things:

```csharp
public void CreateNewItem()
{
    Module = moduleTemplate.DeepCopy();
    Module.Level = level;
    Module.RandomizeConnections();     // <-- different connections every time
}
```

So the *second* purchase of a repeatable module is **not the same module**. Its
`North/East/South/West` connection flags are re-randomised, which changes how it wires into a
cluster. Players noticing "the same module didn't fit this time" are seeing this, not a bug.

Price escalation:

```csharp
public void IncreasePrice(float incrementMultiplier)
{
    foreach (Price increment in priceIncrement)
    {
        int i = price.FindIndex(p => p.HasSameCurrency(increment));
        if (i != -1) { var v = price[i]; v.amount += increment.amount * incrementMultiplier; price[i] = v; }
        else          price.Add(increment);          // <-- can introduce a NEW currency
    }
}
```

Two things worth knowing: increments are matched **by currency**, and if the increment names a
currency the price does not yet have, it is *appended*. A repeatedly-bought item can therefore
start costing something it never cost before.

**Co-op multiplies the increment**:

```csharp
float incrementMultiplier = GameScene.arguments.isCoop ? coopPriceIncrementMultiplayer : 1f;
```

Prices escalate faster in co-op by design. Price parity between clients matters — a client
computing this differently sees different prices.

## The shop list is run-scoped, not per-station

```csharp
public ShopItemList ShopItemList => runData.GeneralShopItemList;
```

`Shop` is a `MonoBehaviour` sitting on a station, but its inventory comes from `RunData`. Every
shop in a run shares one list. Buying at one station changes what is on offer at the next, and
a repeat item's escalated price follows the player around.

## The Vault

Run-scoped storage, `IMementoOriginator`:

| Holds | As |
|---|---|
| Modules | `List<Module>` + a `newModules` `HashSet` driving the "new!" badge |
| Consumables | `List<ConsumableWithAmount>` |
| Ingredients | `Dictionary<Ingredient, int>` |

Events: `ConsumableAmountChanged`, `IngredientAmountChanged`, `NewModuleSeen`.

## Station upgrades

`StationUpgrade` is a separate, simpler track from the module shop: a single `cost` in one
`resourceUsed`, plus a `PriceIncreaseMode` and `priceIncreaseAmount`. It carries its own
`activatedObject`, animation trigger and map icon, so an upgrade is a visible change to the
station.

## Meta-progression

`MetaProgressManager` is the only thing that survives a run, and it is deliberately tiny:

```csharp
META_UNLOCKED_LOADOUTS
META_TOTAL_DEATH_COUNT
```

`UnlockLoadout(LoadoutTemplate)`, `GetUnlockedLoadouts()`, `RegisterDeath()`,
`GetTotalDeathCount()`, and a static `ResetUnlockedLoadouts()`. There is no currency, level or
unlock tree behind it.

## Multiplayer notes

- **Shop unlocks replicate as events.** They must be broadcast *after* the `InGame` state flip:
  `ProgressionSync` dropped all 44 unlock broadcasts once because the unlock ran before it, and
  every shop appeared locked.
- **Price parity** is required — both sides must agree on the co-op multiplier and the current
  escalated price, or purchases desync.
- Nothing grants starting money; the run begins at zero (confirmed by decompile).
- Module **trading** between players was considered and rejected.

## See also

- [`modules-and-ship-building.md`](modules-and-ship-building.md) — what you are buying
- [`pickups-and-loot.md`](pickups-and-loot.md) — where ingredients come from
- [`VANILLA_GOTCHAS.md`](VANILLA_GOTCHAS.md)
