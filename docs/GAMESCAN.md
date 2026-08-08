# gamescan — knowing what a game update changed

Steam updates PUNK underneath this mod without asking. When something starts misbehaving
afterwards, the useful question is never *"what changed in the game"* — hundreds of things do —
but *"what changed that we depend on"*, which is usually fewer than a dozen.

`gamescan` answers exactly that question.

```
tools\gamescan.ps1
```

```
==> game build: steam-24507299
==> diffing steam-24501188 -> steam-24507299
gamescan: 0 breaking, 1 behavioral, 412 unused

BEHAVIOR CHANGED — same signatures, different code, in members the mod uses.
Nothing will warn you at runtime. Read the report.
report: gamescan/reports/report-steam-24507299.md
```

---

## Why not just hash the code

Three decisions make the difference between a useful report and a wall of noise.

**It hashes IL, not decompiled text.** Decompiler output changes when the decompiler changes,
and it changes when the *resolution context* changes — decompiling `Punk.Main.dll` without the
Unity assemblies beside it produces different casts and extra comments for identical IL. A
text hash reports those as changes. Hashing the normalized instruction stream sees through
them. (Branch targets are recorded as instruction *indexes*, not byte offsets, so an edit
early in a method does not cascade into every branch after it.)

**It records two hashes per member, and the second one is the point.**

| Hash | Catches | Would you find out otherwise? |
|---|---|---|
| signature | renamed / removed / re-typed members | Yes — Harmony throws at load |
| **IL body** | **same signature, different behavior** | **No. Nothing reports this.** |

The second row is the failure mode this project keeps paying for. A method whose body changed
while its signature held still will load cleanly, patch cleanly, and behave differently.

The signature hash also folds in the **constant value of literal fields**, which is what makes a
silently renumbered enum (`CellType.Hazard` 5 → 6) visible. Nothing else would catch it.

**It cross-references against what the mod actually uses.** This is what turns 412 changes into
one. The dependency set is extracted from the *compiled mod assembly's IL*, not from a regex
over `src/` — so it includes every ordinary call and field access, not just the Harmony
attributes:

| Discovered via | Count |
|---|---:|
| direct calls into game code | 692 |
| direct field access | 387 |
| `[HarmonyPatch]` string targets | 165 |
| type references | 158 |
| `AccessTools` string lookups | 92 |

Scanning only for `[HarmonyPatch]` and `AccessTools` would have found 257 of those and missed
1,079 real dependencies.

---

## What it produces

| Path | Committed? | What it is |
|---|---|---|
| `gamescan/baseline.json` | **yes** | Hashes for every type and member of the game build the mod is currently built against |
| `gamescan/contract.json` | **yes** | The game members the mod depends on, with `src/File.cs:line` for each use |
| `src/Core/GameBaseline.g.cs` | **yes** | Compact baseline compiled into the mod for the boot-time check |
| `docs/api/**` | **yes** | Mechanically generated API index |
| `gamescan/cache/<build>/` | no | Decompiled game source, kept so the report can show you the actual change |
| `gamescan/reports/` | no | Manifests and reports per scanned build |

**This repo is public.** Game source is never committed — only derived hashes. That is why the
cache is gitignored and why the report points at local files rather than embedding them.

---

## Reading a report

Findings are tiered, most-actionable first:

- 🔴 **Breaking** — signature changed or member removed, *and the mod uses it*. Harmony will
  throw at load. Fix before shipping.
- 🟠 **Behavioral** — body changed, signature stable, *and the mod uses it*. Nothing warns
  you. Read the diff and decide whether your assumptions still hold.
- ⚪ **Unused** — changed, but nothing in the mod references it. Collapsed by default.

Each finding lists every place in `src/` that touches it, and a ready-to-run command:

```
git diff --no-index gamescan/cache/steam-24501188/ResourceBar.cs \
                    gamescan/cache/steam-24507299/ResourceBar.cs
```

Exit codes carry the verdict for CI: `0` clean, `3` behavioral only, `4` breaking.

---

## The workflow

```powershell
# after Steam updates the game, or when something inexplicable starts happening
tools\gamescan.ps1

# ...read gamescan/reports/report-<build>.md, fix what it flags, then:
tools\gamescan.ps1 -Accept        # promote the new build to the baseline
tools\gamescan.ps1 -Guard         # refresh the mod's compiled-in baseline
tools\gamescan.ps1 -Index         # refresh docs/api/
tools\gamescan.ps1 -DocCheck      # find prose that now describes a type the game removed
.\build.ps1                       # rebuild so the new baseline ships
```

`-DocCheck` reports identifiers the curated docs mention that the game assembly does not
declare. It is a lead generator, not a gate: the docs legitimately name prefab ids, config keys,
the mod's own protocol vocabulary and types from other assemblies, none of which are in
`Punk.Main`. Read it after an update and follow up on anything that used to resolve.

`-Accept` is deliberately a separate step. Moving the baseline is a statement that you have
looked at the changes and the mod is correct against the new build.

Regenerate `contract.json` whenever the mod's dependencies change (it is refreshed from
`bin/Release/PunkMultiverse.dll` on every run, so just build first).

---

## The boot-time check

The mod carries a compact copy of its dependency surface and checks it at startup. Output is
tagged `[GameScan]` and it is **log-only** — a base-game update is never a reason to refuse to
load.

```
[GameScan] game matches baseline (steam-24507299).
```

or, after an update:

```
[GameScan] the base game has CHANGED since this mod was built
           (baseline steam-24507299, module 982c5693 -> 1a4f77b2).
[GameScan] 0 type(s) and 3 member(s) the mod depends on are GONE.
[GameScan]   missing member LootDropper.DropLoot/2
```

**What it can and cannot see.** It verifies that the types and members the mod depends on still
exist with the same arity — enough to catch the renames and removals that make Harmony throw.
It deliberately does **not** try to detect body changes: hashing IL at load would cost real
startup time, and comparing Cecil's type names against reflection's would fire constantly on
generics that never changed. The log says as much rather than implying a stronger guarantee.
For body changes, run the offline scan.

---

## Rebuilding from scratch

Delete `gamescan/baseline.json` and run `tools\gamescan.ps1`; the first run with no baseline
adopts the installed build. `-NoDecompile` skips the decompile step (faster, but the report
loses its ready-to-run diff commands). `ilspycmd` is required for the cache:

```
dotnet tool install -g ilspycmd
```

The manifest carries a `FormatVersion`. If the hashing scheme ever changes, the differ refuses
to compare across versions rather than producing a meaningless diff — regenerate the baseline.

---

## See also

- [`VANILLA_GOTCHAS.md`](VANILLA_GOTCHAS.md) — read before changing anything touching game code
- [`api/`](api/) — the generated API index this tool also produces
