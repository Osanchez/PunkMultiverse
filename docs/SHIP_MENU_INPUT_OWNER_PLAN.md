# Ship-menu input owner — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: use `superpowers:subagent-driven-development` (recommended) or
> `superpowers:executing-plans` to implement this task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** A client can always leave a station shop, and can always buy in it, without the player
doing anything special — and the log names which mechanism was about to trap them.

**Architecture:** Four narrow Harmony guards make `ShipMenuToggler`'s own state self-consistent in a
net run (it was written for exactly one `PlayerInput` and one `Ship`), plus one watcher that
evaluates a small invariant table each frame, logs violations, and — only after the violation has
persisted — escapes through **vanilla's own** `Close()`. No new owner of the action maps: every
guard either normalises an argument vanilla already takes, or calls a vanilla method.

**Tech stack:** BepInEx 6 / HarmonyX, Unity Input System, C# — same as the rest of the mod.
Verification is the two-instance loopback harness (`docs/harness.md`, `.claude/skills/mp-test`),
not a unit-test framework: this code only exists inside a running game.

## Global constraints

- **Net runs only.** Every patch returns early unless `NetSession.Active`. Single-player behaviour
  must be bit-identical to vanilla.
- **Never reimplement game logic** (`CLAUDE.md`). Guards normalise inputs and call vanilla methods;
  no copy of vanilla's `OnActionTriggered` branch, no hand-rolled close.
- **Branch, not `main`.** Work on `fix/ship-menu-input-owner`. A commit to `main` is a release.
- Logging tag: `[MenuState]` for the watcher, `[ShipMenu]` for the guards. `Plugin.Log.LogInfo`.
- New config keys are bound in `NetConfig.Init`; deleting one later requires a line in
  `ConfigAudit.Retired`.
- PowerShell here is 5.1: no `&&`, no ternary, no `??`.

## Root-cause status

Not proven — three live candidates, all real defects in a net run, all cheap to close:

1. **Re-entrant `Open()`** — `ShipMenuToggler.Open` has no `isOpen` guard (the same defect already
   fixed for `PauseScreen` in `GuardPatches.NetRunPauseButtons`). With the ship map still live at a
   station, `Interactor.OnUseActivated → Station.OnUseActivated → Shop.StartShopping → OpenShop`
   can re-open the menu with the very press meant to leave it.
2. **Owner mismatch** — `OnActionTriggered` accepts input only when
   `FirstOrDefault(p => p.actions.Contains(action)) == playerInputInControl`. `playerInputs` is
   built once at `GameStarted` from `gameController.Ships`, which in a net run contains puppets
   (`ShipSync.SpawnPuppets` appends them to `ShipManager.ships`). A mismatch silently kills close,
   back, tab switching and `activeTab.OnInputActionPerformed` — i.e. exactly "can't exit, can't buy".
3. **`Open()` aborted mid-body** — `ShowTab` (→ `ModuleGridScreen.OnOpened`) runs *before* the
   `SwitchCurrentActionMap("MapControl")` loop and `DisableShipControl()`. An exception there leaves
   `isOpen = true`, the canvas up, the ship map live and the "Shop" map unregistered.

Task 1 makes the failure reproducible and observable; Tasks 3–6 close all three; Task 2 and Task 7
report which one was actually firing in the wild. If the friend's log later shows a fourth
mechanism, the watcher will have named it.

## File structure

| Path | Responsibility |
|---|---|
| `src/Patches/ShipMenuGuards.cs` | **New.** The four guards (Tasks 3–6). One file: they all patch `ShipMenuToggler` and share the "who is the local PlayerInput" helper. |
| `src/Core/MenuStateWatch.cs` | **New.** Invariant table, violation logging, debounced escape (Tasks 2, 7). |
| `src/Core/DevTools.cs` | Two devcmds: `shopopen`, `menustate` (Task 1). |
| `src/Core/NetConfig.cs` | One key: `MenuStateRepair` (Task 7). |
| `src/Plugin.cs` | Register the watcher on the runtime object (Task 2). |
| `docs/test-scenarios.md` | Scenario `shop-menu-exit` with PASS criteria (Task 1). |
| `docs/VANILLA_GOTCHAS.md` | What vanilla does here and why it cannot survive a second ship (Task 8). |
| `docs/ui-and-screens.md` | Cross-reference from the screens doc (Task 8). |

---

### Task 1: Make the bug drivable and observable in the harness

Nothing can be graded until a script can open a station shop the *real* way and dump the state.
There is no interact devcmd today (`shop` fakes the damage shield; `shopstate` reports stock).

**Files:**
- Modify: `src/Core/DevTools.cs` (command switch, near `case "shopstate":`)
- Modify: `docs/test-scenarios.md` (new scenario entry)

**Interfaces:**
- Produces: devcmd `shopopen` (opens the nearest unlocked station's shop through
  `Station.OnUseActivated`), devcmd `menustate` (one-line dump, harness-parsable, prefix
  `menustate:`), scenario name `shop-menu-exit`.

- [ ] **Step 1: Write the assertion first — add the scenario to `docs/test-scenarios.md`**

```markdown
### shop-menu-exit

Client can leave a station shop and buy in it. Two instances, loopback.

Drive (CLIENT instance only):
  unlockstation            # nearest station becomes a real shop (checkpoint + broadcast)
  tp <station x> <station y>
  shopopen
  menustate                # expect: open=True owner=local map=MapControl shopmap=True
  nav cancel               # vanilla back/close path
  menustate                # expect: open=False shipcontrol=True

PASS:
- both `menustate` lines match the expectations above
- no `[MenuState] broken` line in either log during the scenario
- no `Exception` from `ShipMenuToggler.Open` / `ModuleGridScreen.OnOpened` in the client log
```

- [ ] **Step 2: Run the scenario before writing any code — it must fail**

Use the `mp-test` skill with `shop-menu-exit`. Expected failure: `Unknown command: shopopen`.
This confirms the harness path, the two installs and the deploy are healthy before we trust any
later verdict.

- [ ] **Step 3: Implement `shopopen`**

Real vanilla path only — find the nearest unlocked station and activate it through the ship's own
`Interactor`, exactly as a keypress would:

```csharp
case "shopopen":
{
    // Drive the REAL interact path (Interactor -> Station.OnUseActivated -> Shop.StartShopping),
    // not ShipMenuToggler.OpenShop directly: the bug under test lives in who owns the input
    // afterwards, and only the real path reproduces the ownership the game assigns.
    var ship = ShipSync.LocalShip;
    if (ship == null) { Out("shopopen: no local ship"); return; }
    var interactor = ship.GetComponentInChildren<Interactor>(true);
    if (interactor == null) { Out("shopopen: ship has no Interactor"); return; }
    var em = ServiceLocator.Get<EntityManager>();
    Station nearest = null; float best = float.MaxValue;
    foreach (var station in UnityEngine.Object.FindObjectsOfType<Station>())
    {
        if (station.ComponentData == null || !station.ComponentData.IsUnlocked) continue;
        float d = Vector2.Distance(ship.transform.position, station.transform.position);
        if (d < best) { best = d; nearest = station; }
    }
    if (nearest == null) { Out("shopopen: no unlocked station in the scene"); return; }
    nearest.OnUseActivated(interactor);
    Out($"shopopen: activated station at dist={best:0.0}");
    return;
}
```

- [ ] **Step 4: Implement `menustate`**

One line, all four invariant inputs, so the harness greps instead of inferring:

```csharp
case "menustate":
{
    var t = ServiceLocator.Get<ShipMenuToggler>();
    if (t == null) { Out("menustate: no ShipMenuToggler"); return; }
    var tv = Traverse.Create(t);
    bool open = tv.Field("isOpen").GetValue<bool>();
    var owner = tv.Field("playerInputInControl").GetValue<PlayerInput>();
    var station = tv.Field("currentStation").GetValue<Station>();
    var localInput = ShipSync.LocalShip != null ? ShipSync.LocalShip.shipInput : null;
    var localPi = localInput != null ? localInput.PlayerInput : null;
    string map = (localPi != null && localPi.currentActionMap != null) ? localPi.currentActionMap.name : "none";
    bool shopMap = false;
    try { shopMap = localPi != null && localPi.actions.FindActionMap("Shop").enabled; } catch { }
    bool shipControl = localInput != null && localInput.ShipControlActionMap != null
                       && localInput.ShipControlActionMap.Enabled;
    Out($"menustate: open={open} owner={(owner == localPi ? "local" : (owner == null ? "null" : "other"))} " +
        $"map={map} shopmap={shopMap} shipcontrol={shipControl} station={(station != null)}");
    return;
}
```

- [ ] **Step 5: Build, deploy to both installs, run `shop-menu-exit` again**

`powershell -File build.ps1` then the same with `-GameDir "<client install>"`. Record the two
`menustate:` lines verbatim — **this is the evidence that decides which root cause is real.**
Do not skip forward on a guess: if `owner=other`, cause 2 is confirmed; if `map=ShipControl` with
`shopmap=False`, cause 3; if the log shows two `OpenShop` in one press, cause 1.

- [ ] **Step 6: Commit**

```bash
git checkout -b fix/ship-menu-input-owner
git add src/Core/DevTools.cs docs/test-scenarios.md docs/SHIP_MENU_INPUT_OWNER_PLAN.md
git commit -m "Harness: drive and dump the ship-menu input state"
```

---

### Task 2: The watcher — invariants and logging, no repair yet

**Files:**
- Create: `src/Core/MenuStateWatch.cs`
- Modify: `src/Plugin.cs` (add the component to `_runtime`, beside the other runtime components)

**Interfaces:**
- Consumes: `ShipSync.LocalShip`, `NetSession.Active`.
- Produces: `MenuStateWatch.LastViolation` (string, empty when clean) for Task 7 and for `menustate`.

The invariant table — the contract this mod holds for the *ship menu* (the pause overlay
deliberately deviates and is out of scope here, see `GuardPatches.KeepShipControllableWhilePaused`):

| # | While | Must hold |
|---|---|---|
| I1 | menu open | `playerInputInControl` is the local ship's `PlayerInput` |
| I2 | menu open | local `PlayerInput.currentActionMap.name == "MapControl"` |
| I3 | menu open at a station | the local asset's `"Shop"` action map is enabled |
| I4 | menu closed | local `ShipControlActionMap.Enabled` |

- [ ] **Step 1: Write the failing assertion**

Add to the `shop-menu-exit` scenario: with the menu open, `menustate` must report
`violation=none`. Run it — `menustate` has no `violation` field yet, so it fails.

- [ ] **Step 2: Implement the watcher**

```csharp
// Evaluates the ship-menu invariant table each frame and reports the FIRST violation that
// survives DebounceFrames. Debounce exists because vanilla is legitimately inconsistent for a
// few frames: UIScreen.CloseCoroutine yields, AnimatedScreen animates, ShowTab re-opens a tab.
internal sealed class MenuStateWatch : MonoBehaviour
{
    private const int DebounceFrames = 30;   // ~0.5s at 60fps

    internal static string LastViolation = "";

    private string _pending = "";
    private int _pendingFrames;

    private void Update()
    {
        if (!NetSession.Active) { Reset(); return; }
        string violation = Evaluate();
        if (violation.Length == 0) { Reset(); return; }
        if (violation != _pending) { _pending = violation; _pendingFrames = 0; return; }
        _pendingFrames++;
        if (_pendingFrames != DebounceFrames) return;      // fire exactly once per episode
        LastViolation = violation;
        Plugin.Log.LogWarning($"[MenuState] broken {violation}");
    }

    private void Reset()
    {
        if (LastViolation.Length != 0) Plugin.Log.LogInfo("[MenuState] cleared");
        _pending = ""; _pendingFrames = 0; LastViolation = "";
    }

    /// <summary>"" when every invariant holds, else "I2: map=ShipControl open=True station=True".</summary>
    private string Evaluate() { /* read the same fields menustate reads; return the first break */ }
}
```

`Evaluate` reads exactly what `menustate` reads (Task 1, Step 4) — factor that read into a
`MenuStateWatch.Snapshot()` struct and have the devcmd call it, so the two can never disagree.

- [ ] **Step 3: Extend `menustate` with `violation=<LastViolation or none>`**

- [ ] **Step 4: Run `shop-menu-exit`; record whether a violation appears and which**

- [ ] **Step 5: Commit**

```bash
git add src/Core/MenuStateWatch.cs src/Core/DevTools.cs src/Plugin.cs
git commit -m "Diagnostics: name the broken ship-menu invariant instead of guessing"
```

---

### Task 3: Guard 1 — the owner is always the local player

Closes root cause 2. Two halves, both normalising state vanilla already keeps, so vanilla's own
equality check starts passing instead of being bypassed.

**Files:**
- Create: `src/Patches/ShipMenuGuards.cs`

- [ ] **Step 1: Assertion first** — `shop-menu-exit` must report `owner=local` while open. Run it;
      on a machine where cause 2 is live this is the failing test. (If it already passes, keep the
      guard: it is the difference between "works today" and "cannot break".)

- [ ] **Step 2: Rebuild `playerInputs` from the local ship**

```csharp
// Vanilla builds playerInputs once, from gameController.Ships — which in a net run holds this
// client's puppets of everybody else. Every consumer of that list is wrong with a puppet in it:
// the owner lookup can resolve to one, and Open/Close switch action maps on all of them.
[HarmonyPatch(typeof(ShipMenuToggler), "OnGameStarted")]
internal static class LocalPlayerInputOnly
{
    private static void Postfix(ShipMenuToggler __instance)
    {
        if (!NetSession.Active) return;
        var local = LocalPlayerInput();
        if (local == null) return;
        var list = Traverse.Create(__instance).Field("playerInputs").GetValue<List<PlayerInput>>();
        // Vanilla subscribed OnActionTriggered on every ship; unsubscribe the puppets. A fresh
        // delegate compares equal to the original (same target + method), so -= removes it.
        var handler = (Action<InputAction.CallbackContext>)AccessTools
            .Method(typeof(ShipMenuToggler), "OnActionTriggered")
            .CreateDelegate(typeof(Action<InputAction.CallbackContext>), __instance);
        int dropped = 0;
        foreach (var pi in list)
        {
            if (pi == null || pi == local) continue;
            pi.onActionTriggered -= handler;
            dropped++;
        }
        list.Clear();
        list.Add(local);
        Plugin.Log.LogInfo($"[ShipMenu] playerInputs pinned to the local ship (dropped {dropped} puppet inputs)");
    }
}
```

- [ ] **Step 3: Normalise the `Open` argument**

```csharp
// OpenShop passes the INTERACTING ship's PlayerInput, the Tab path passes whatever the owner
// lookup resolved. Both must name the same object or the menu becomes input-orphaned.
[HarmonyPatch(typeof(ShipMenuToggler), "Open")]
internal static class OwnerIsAlwaysLocal
{
    private static void Prefix(ref PlayerInput __0)
    {
        if (!NetSession.Active) return;
        var local = LocalPlayerInput();
        if (local == null || __0 == local) return;
        Plugin.Log.LogInfo("[ShipMenu] open requested with a non-local PlayerInput — retargeted to the local ship");
        __0 = local;
    }
}
```

- [ ] **Step 4: The shared helper**

```csharp
private static PlayerInput LocalPlayerInput()
{
    var ship = ShipSync.LocalShip;
    if (ship == null || ship.shipInput == null) return null;
    return ship.shipInput.PlayerInput;
}
```

- [ ] **Step 5: Build, deploy both, run `shop-menu-exit`** — expect `owner=local`, close works.

- [ ] **Step 6: Commit** — `git commit -m "Ship menu: the local player owns it, not a puppet"`

---

### Task 4: Guard 2 — a re-entrant open cannot re-arm the menu

Closes root cause 1. Mirrors `GuardPatches.NetRunPauseButtons`, including its lesson: a redundant
open that came from the player's own key is *closed*, not dropped, or the button stops working.

**Files:** Modify `src/Patches/ShipMenuGuards.cs`

- [ ] **Step 1: Assertion first** — scenario step: `shopopen` twice in a row, then `menustate`.
      Expect one open, `open=True owner=local`, and a `[ShipMenu] suppressed` line — not two
      `Open` bodies. Run; it fails (vanilla re-runs the whole body).

- [ ] **Step 2: Implement**

```csharp
// Vanilla Open() has no isOpen guard. Re-running the body while open re-pauses, re-switches
// action maps and re-points the owner — and at a station the ship map is live, so the Use press
// meant to LEAVE the shop re-opens it. Re-entry is allowed to change the TAB (that is how the
// game switches map<->grid), but must not re-run the input contract.
[HarmonyPatch(typeof(ShipMenuToggler), "Open")]
internal static class NoReentrantOpen
{
    private static bool Prefix(ShipMenuToggler __instance, int __1, Station __2)
    {
        if (!NetSession.Active) return true;
        if (!Traverse.Create(__instance).Field("isOpen").GetValue<bool>()) return true;
        int current = Traverse.Create(__instance).Field("currentTabIndex").GetValue<int>();
        if (current != __1)
        {
            Traverse.Create(__instance).Field("currentStation").SetValue(__2);
            __instance.ShowTab(__1);
            Plugin.Log.LogDebug($"[ShipMenu] re-entrant open -> tab switch only ({current} -> {__1})");
        }
        else Plugin.Log.LogDebug("[ShipMenu] suppressed redundant open");
        return false;
    }
}
```

Patch order note: this prefix and `OwnerIsAlwaysLocal` both patch `Open`. Harmony runs all
prefixes; `OwnerIsAlwaysLocal` only rewrites an argument, so order does not matter — but if
`NoReentrantOpen` returns false the body is skipped, which is the intent in both cases.

- [ ] **Step 3: Run the scenario** — expect exactly one open and a working close.

- [ ] **Step 4: Commit** — `git commit -m "Ship menu: a second open cannot re-arm an open menu"`

---

### Task 5: Guard 3 — the exit press cannot re-enter the station

Closes the other half of root cause 1: the interact action lives on the ship map, which is alive
in a net run, so `Close()` and a fresh `Station.OnUseActivated` can land on the same press.
Vanilla already has this concept — `Ship.LastTimeExitShipMenu` + `minDelayAfterLeavingShipMenu`
guard module activation the same way.

**Files:** Modify `src/Patches/ShipMenuGuards.cs`

- [ ] **Step 1: Assertion first** — scenario: `shopopen`, `nav cancel`, `menustate` twice, 1s apart.
      Expect `open=False` on both. Failing today if the re-open race is live.

- [ ] **Step 2: Implement**

```csharp
private const float ReopenBlockSeconds = 0.35f;
private static float _lastCloseAt = -99f;

[HarmonyPatch(typeof(ShipMenuToggler), "Close")]
internal static class StampClose
{
    private static void Postfix() { if (NetSession.Active) _lastCloseAt = Time.unscaledTime; }
}

// Station interaction is how the shop opens; refuse the one that arrives in the same breath as
// the close that just happened. Vanilla is protected by the ship map being OFF while the menu is
// open — a protection this mod deliberately removes in a live co-op world.
[HarmonyPatch(typeof(Station), "OnUseActivated")]
internal static class NoInstantReopen
{
    private static bool Prefix()
    {
        if (!NetSession.Active) return true;
        if (Time.unscaledTime - _lastCloseAt >= ReopenBlockSeconds) return true;
        Plugin.Log.LogDebug("[ShipMenu] station use suppressed — the menu just closed");
        return false;
    }
}
```

Note the blast radius: this also suppresses a station *unlock* press in that 0.35s window. That is
acceptable (the player presses again) and must be stated in the doc update in Task 8.

- [ ] **Step 3: Run the scenario.** Also re-run `unlockstation`-based scenarios in the smoke set to
      prove the unlock path still works.

- [ ] **Step 4: Commit** — `git commit -m "Ship menu: leaving a shop cannot immediately re-enter it"`

---

### Task 6: Guard 4 — a throwing tab cannot strand the menu

Closes root cause 3. `ShowTab` runs before `Open`'s input contract; contain it there so `Open`
always finishes, even if the tab's contents are broken.

**Files:** Modify `src/Patches/ShipMenuGuards.cs`

- [ ] **Step 1: Assertion first** — no direct trigger exists, so assert on the log: after a forced
      throw (temporarily add `throw new Exception("probe")` at the top of a `ShowTab` postfix in a
      scratch build), `menustate` must still report `map=MapControl`, and the menu must close.
      Remove the scratch throw before committing.

- [ ] **Step 2: Implement**

```csharp
// ShipMenuToggler.Open calls ShowTab BEFORE it switches action maps and disables ship control.
// A tab that throws (ModuleGridScreen.OnOpened touches Ship, Station, Shop and every HUD) leaves
// isOpen=true, the canvas up, the ship map live and the Shop map unregistered — the exact shape
// of the field report. Contain the tab; let Open finish its input contract.
[HarmonyPatch(typeof(ShipMenuToggler), "ShowTab")]
internal static class ContainThrowingTab
{
    private static Exception Finalizer(Exception __exception, int index)
    {
        if (__exception == null) return null;
        if (!NetSession.Active) return __exception;   // single-player keeps vanilla behaviour
        Plugin.Log.LogError($"[ShipMenu] tab {index} threw, containing so the menu stays closable: {__exception}");
        return null;
    }
}
```

- [ ] **Step 3: Run the scenario with and without the scratch throw**

- [ ] **Step 4: Commit** — `git commit -m "Ship menu: a throwing tab no longer strands the screen"`

---

### Task 7: The backstop — escape through vanilla's own Close

Only now, with the known mechanisms closed, add repair. It does exactly one thing, through the
vanilla path, and says so in the log.

**Files:**
- Modify: `src/Core/MenuStateWatch.cs`
- Modify: `src/Core/NetConfig.cs` (bind `MenuStateRepair`, default `true`, section `[Debug]`)

- [ ] **Step 1: Assertion first** — scenario `shop-menu-exit-backstop`: with a scratch build that
      forces `playerInputInControl = null` after open, expect `[MenuState] broken I1`, then
      `[MenuState] repaired: closed the ship menu`, then `menustate: open=False shipcontrol=True`.

- [ ] **Step 2: Implement**

```csharp
// RepairFrames is deliberately far past DebounceFrames: the invariant has to be broken for two
// full seconds before we act. Anything faster fights animations and coroutines — and this mod
// has already paid for one bug where two owners fought over the same action maps.
private const int RepairFrames = 120;

// ... inside Update, after the violation has been logged:
if (_pendingFrames < RepairFrames || !NetConfig.MenuStateRepair.Value) return;
_pendingFrames = 0;
var toggler = ServiceLocator.Get<ShipMenuToggler>();
if (toggler == null) return;
Plugin.Log.LogWarning($"[MenuState] repaired: closing the ship menu ({LastViolation})");
toggler.Close();     // vanilla's own exit: restores ship control and the action maps
```

`Close()` is vanilla and re-enables ship control and the maps itself — that is the whole reason to
use it rather than poking action maps directly.

- [ ] **Step 3: Run both scenarios; confirm the backstop never fires in the clean one**

A backstop that fires during a healthy run is a bug in the backstop.

- [ ] **Step 4: Commit** — `git commit -m "Ship menu: a stranded menu escapes itself"`

---

### Task 8: Write down what vanilla does, and ship the branch

**Files:**
- Modify: `docs/VANILLA_GOTCHAS.md`, `docs/ui-and-screens.md`, `docs/test-scenarios.md`

- [ ] **Step 1: `VANILLA_GOTCHAS.md` — a section per mechanism**, in the house voice: what vanilla
      does, why it is fine in single-player, what a second `Ship` does to it. Include the
      `Ship.LastTimeExitShipMenu` precedent for the re-open block.
- [ ] **Step 2: `ui-and-screens.md`** — under "Multiplayer notes", one line per guard with a
      pointer to `ShipMenuGuards.cs`.
- [ ] **Step 3: Run the full smoke suite** (`mp-test smoke`) — these guards touch the screen every
      run start goes through; a regression here is a run-ender.
- [ ] **Step 4: Commit docs, push the branch, open a PR against `Osanchez/PunkMultiverse`**

```bash
git add docs/
git commit -m "Docs: the ship menu was written for exactly one ship"
git push fork fix/ship-menu-input-owner
```

Do not merge to `main` without a human pass with the friend on real Steam — the harness proves the
mechanism, a playtest proves the fix.

---

## Self-review notes

- **Spec coverage:** guards for causes 1 (Tasks 4, 5), 2 (Task 3), 3 (Task 6); checker (Task 2);
  auto-repair with no player input (Task 7); repro + evidence (Task 1); docs (Task 8).
- **Ordering:** Task 1 before every fix on purpose — it is the only step that can still tell us
  which cause was real. If it is skipped, the guards will mask the evidence.
- **Known gap:** `Evaluate()` in Task 2 is specified by its inputs and return contract, not by a
  finished body; it reads the same fields as `menustate` (Task 1, Step 4) through the shared
  `Snapshot()`. Implementers must write it once and call it from both.
- **Blast radius to watch:** Task 5 suppresses *all* station interaction for 0.35s after a close,
  including unlock. Task 6 swallows tab exceptions in net runs — the log line is the only trace,
  so it is `LogError`, not `LogDebug`.
