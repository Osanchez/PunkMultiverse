---
name: mp-gamescan
description: Find out what a PUNK game update changed and fix what it broke in the mod. Runs tools/gamescan.ps1 (IL-level diff of Punk.Main.dll cross-referenced against the mod's real dependency surface), then triages each finding to its use sites in src/ and repairs them. Use when the base game has updated, when the mod stopped working or behaves oddly after a Steam update, when a Harmony patch throws at load, when a `[GameScan]` warning appears in the log, or when asked what changed in the game / whether an update broke us. Args: optional focus, e.g. "breaking", "behavioural", "accept", "docs".
---

# Game update: what changed, and what it broke

Steam updates PUNK underneath the mod without asking. The useful question is never *"what
changed in the game"* — hundreds of things do — but ***"what changed that we depend on"***,
which is usually fewer than a dozen.

`tools/gamescan.ps1` answers that. This skill runs it and then does the part the tool cannot:
read the actual code change, find every place in `src/` that assumed the old behaviour, and fix
it.

Reference: `docs/GAMESCAN.md`. Read `docs/VANILLA_GOTCHAS.md` before changing anything that
touches game code.

---

## 1. Run it

```powershell
.\tools\gamescan.ps1
```

Do **not** pipe it through `2>&1` — Windows PowerShell 5.1 wraps a native exe's stderr in
`NativeCommandError` records and fails the call even on exit 0. Just run it.

The exit code is the verdict:

| Code | Meaning | What to do |
|---|---|---|
| `0` | Nothing the mod depends on changed | Say so and stop. The update is very unlikely to be the cause. |
| `3` | **Behavioural** — bodies changed under stable signatures | Triage. Nothing warns you at runtime; this is the dangerous one. |
| `4` | **Breaking** — signatures changed or members removed | Triage first. Harmony will throw at load. |

The report lands in `gamescan/reports/report-<build>.md`. **Read it** — it names every use site
in `src/` with `file:line`.

### Build the mod first if `bin/Release/PunkMultiverse.dll` is missing

The contract surface is extracted from the compiled mod, so a build must exist. A **stale**
build is fine and is in fact the right input: the question is whether the update breaks the mod
*as it currently is*.

If `.\build.ps1` itself now fails to compile, that is not a problem to route around — it is the
loudest possible confirmation of a breaking change. Read the compiler errors; they name the
members directly. Then continue with the triage below.

---

## 2. Triage

Work strictly in tier order. Fixing a 🔴 often makes a 🟠 moot.

### 🔴 Breaking — signature changed or member removed

Harmony throws at load, so these are visible. For each finding the report gives the member and
every `src/` use site. Read the new shape from the decompiled cache and update the patch or call
to match.

### 🟠 Behavioural — same signature, different code

**This is the tier that matters.** Nothing will report it: the mod loads, patches apply, and
behaviour differs. The report tells you *that* a body changed and by how many IL instructions —
you have to read *what* changed.

Each finding comes with a ready-to-run command:

```bash
git diff --no-index gamescan/cache/<old-build>/ResourceBar.cs \
                    gamescan/cache/<new-build>/ResourceBar.cs
```

Expect that diff to be **small** — usually a line or two. Both caches are decompiled with the
Unity assemblies on ILSpy's reference path, so what you see is the real change, not decompiler
noise. If a diff comes back with dozens of changed casts and `Unknown result type` comments, the
cache was built without `-r` and you are reading artifacts: delete that cache directory and
re-run the scan.

Then, for each use site the report lists, ask the only question that counts: **does our code
still hold, given the new body?** A changed body is not automatically a problem. Judge it, do
not reflexively patch.

A worked example, from a real scan:

```
🟠 body — ResourceBar.CheckCapacityChanged()   130 IL → 132 IL
   used via call at src/UI/ShipStatusBars.cs:232, :245, :247
```

The diff showed:

```csharp
- int num = Mathf.RoundToInt(resourceTank.Capacity);
+ int num = Math.Max(0, Mathf.RoundToInt(resourceTank.Capacity));
```

A game-side clamp for negative capacity. Verdict: benign, no mod change needed — but only
knowable by reading it.

### If the decompiled cache for the OLD build is missing

You get hashes and no readable diff. `gamescan/cache/` is gitignored, so a fresh clone has no
history, and a build that was never scanned was never cached.

Fall back to:
- the API index (`docs/api/`) and the current source in `gamescan/cache/<new-build>/`,
- the mod's use sites, reasoning about what the current code does,
- `git log` on the relevant `src/` files for why the code assumed what it did.

Never delete `gamescan/cache/` to save space. It is the only record of what the game used to
look like.

---

## 3. Common shapes

**A patch target vanished.** The method was renamed or its parameters changed. Find the new one
in `docs/api/` or the cache, update the `[HarmonyPatch]` / `AccessTools` call. If it was reached
via `AccessTools.TypeByName("X")` the failure is a silent null at load, not an exception — check
`[GameScan]` in the log.

**An enum was renumbered.** The signature hash folds in literal field constants specifically to
catch this. Nothing else would. Any code storing or wire-encoding that enum's numeric value
needs re-checking, including saved data and protocol messages.

**A method the mod prefixes now early-returns differently.** Re-read the guard clauses. A prefix
that returns `false` to skip the original is sensitive to the original's own control flow.

**Behaviour moved between methods.** Body shrank in one place and grew in another. The report
shows both if both are in the contract surface — and shows only one if the other is a member the
mod does not touch, which is worth checking in the ⚪ section.

---

## 4. After fixing

Only once the mod is correct against the new build:

```powershell
.\build.ps1                       # must compile cleanly first
.\tools\gamescan.ps1 -Accept      # promote the new build to the baseline
.\tools\gamescan.ps1 -Guard       # refresh src/Core/GameBaseline.g.cs
.\tools\gamescan.ps1 -Index       # refresh docs/api/
.\tools\gamescan.ps1 -DocCheck    # find prose describing types the game removed
.\build.ps1                       # rebuild so the new baseline ships
```

`-Accept` is deliberately separate. Moving the baseline is a statement that you looked at the
changes and the mod is correct — not a step to run reflexively at the start.

`-DocCheck` is a lead generator, not a gate: the docs legitimately name prefab ids, config keys,
the mod's own protocol vocabulary and other assemblies' types. Follow up on anything that *used
to* resolve.

If a finding taught you something about how the base game behaves, add it to
`docs/VANILLA_GOTCHAS.md`. That file is the highest-value artifact in the repo for anyone — or
any model — working on this later.

---

## 5. Rules that bite

- **Do not commit or push unless asked.** Every push to `main` publishes a release.
- **`gamescan/cache/` and `gamescan/reports/` must never be committed.** The repo is public and
  the cache is decompiled game source. They are gitignored; keep it that way. Only
  `baseline.json` and `contract.json` (derived hashes) go in.
- **A version mismatch of the form `0.12.x` is the base game, not the mod.** On the dedicated
  server the game lives on the volume and is copied only on first boot, so rebuilding the image
  alone changes nothing.
- **`ConvertFrom-Json` cannot read the manifest.** PowerShell 5.1 compares keys
  case-insensitively and the game declares `AIAgent.seeker` and `AIAgent.Seeker`, so it throws
  `DuplicateKeysInJsonString`. Use the tool, or regex the one value you need.
- `ilspycmd` is required for the cache (`dotnet tool install -g ilspycmd`). `-NoDecompile` skips
  it and is faster, but the report loses its diff commands — only use it when you already have
  the cache.

---

## 6. Reporting back

Lead with the verdict, not the process:

- **Clean** — say the update did not touch anything the mod depends on, and that the problem is
  therefore probably elsewhere. Do not pad it.
- **Findings** — for each one: the member, what actually changed in the code, whether our
  assumption still holds, and what you did about it. Distinguish "changed but benign" from
  "changed and broke us" explicitly; a body-changed finding that needed no action is a useful
  result, not a non-answer.
- **Numbers** — quote the tier counts (`N breaking, M behavioural, K unused`) so the scale is
  clear.
- Flag anything you could not read (missing old cache) rather than guessing at it.
