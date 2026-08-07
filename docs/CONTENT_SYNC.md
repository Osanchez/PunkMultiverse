# Content sync

**What it is:** the host's custom content — WeaponForge weapons, sprites, sounds — is transferred
to everyone who joins, so a session runs on one content set rather than on whatever each player
happens to have installed.

**Why it exists:** see the last section. The failure it prevents is silent, which is the reason
it is worth this much machinery.

---

## The two halves

| | |
|---|---|
| **The barrier** | *Detects* divergence and refuses the run. Ships in `LevelReadyMsg.ModuleRegistryDigest`; see [`WEAPONFORGE.md`](WEAPONFORGE.md). |
| **The transfer** | *Prevents* divergence by giving the joiner the host's content. This document. |

The barrier is the floor and it stays even when the transfer works — it is the thing that catches
a mod nobody wrote a transfer for.

---

## Configuration

| Key | Default | Meaning |
|---|---|---|
| `ContentRoot` | `""` | Folder the **host** serves. Relative resolves under the mod folder. Empty = serve nothing, and the whole feature is inert. |
| `ContentRateKBps` | `4096` | Per-peer send budget, **bytes per second**. Floor 16. |
| `ContentCacheMaxMB` | `512` | LRU budget for the cache. The active set is never evicted. |
| `ContentMaxFileMB` | `16` | Largest single file published. Guards a `ContentRoot` pointed at something enormous by accident. |

A client sets nothing. `ContentRoot` on a client is ignored for receiving — it only governs what
that machine would serve if *it* hosted.

**Nobody is gated by a host with an empty `ContentRoot`.** That is the common case and it must
stay free.

---

## Shape

```
<mod folder>/content/
  cas/<aa>/<hex32>.bin      committed blob — the NAME is its digest
  cas/<aa>/<hex32>.part     resumable partial
  sets/<hex32>.set          manifest of a COMPLETE, VERIFIED set
  active/<hex32>/...        the materialised tree, laid out as the host had it
```

### The one invariant

> If `sets/<hash>.set` exists, every blob it names exists and is byte-correct.

Everything else is built on it — most of all the cache fast path, which does nothing but check
that file's existence. Two rules hold it up:

1. **A blob's filename is its commit record.** Written to `.tmp`, flushed, digest-verified, then
   `File.Move`d into place. A torn write can never leave a file whose name claims a digest the
   contents don't have. That file would otherwise be trusted forever, by every future launch.
2. **One method writes a `.set`,** and only after every blob it names has committed. There is
   exactly one place to review for rule 1's converse.

`BootSweep` clears `.tmp` and expired `.part` debris at startup, before anything can read it.

**A corrupt cache is never fatal, only slow** — every corruption path degrades to "download it
again". That is a design goal, not an accident: a cache that can wedge a player out of the game
is worse than no cache.

---

## Hashing — SHA-256, deliberately

The repo's idiom is FNV-1a 64 (`DamageSync.HashName`). Content sync **does not follow it**, and
this is not an oversight to tidy up:

The claim being made is *"these files are byte-identical"*. A 64-bit non-cryptographic digest
cannot back that claim. A collision doesn't mean a retry — it means a client silently plays a
whole session with different weapon data, which is precisely the outcome the feature exists to
stop. SHA-256, truncated to 16 bytes.

FNV stays for the short display ids in log lines, where a collision costs a confusing log entry.

### Canonical paths

Relative to the root, `\` → `/`, Unicode Form C, **case preserved**, sorted by **UTF-8 bytes**.

- Sorted by bytes rather than `StringComparer.Ordinal`, because Ordinal compares UTF-16 code
  units and the two disagree across the surrogate range. Two machines must produce the same set
  digest for the same files or clients re-download forever.
- Case is **not** folded: a Linux client has to open the exact name it was handed.

Instead, the **host drops what it cannot publish** and names each one in the log — case-only
duplicates, reserved Windows device names (`CON`, `NUL`, `COM1`…), traversal segments, control
characters, absolute paths.

*Drops*, not "refuses the set". A client refuses a bad set **in full**, because the set hash
covers every file and a client does not get to pick the parts it likes. So if the host merely
warned and served anyway, one stray file in `ContentRoot` would take the whole session down with
an error about that file. `ContentHash.Publishable` is the host-side filter;
`ContentHash.Validate` is the receive-side check, and it is defence in depth against a
hand-crafted offer rather than the primary gate.

### Executables are never transferred

`.dll`, `.exe`, `.ps1`, `.sh`, `.jar`, `.lnk` and friends are refused on **both** sides. Two
independent reasons, either sufficient:

- A content channel that carries executables is a code-delivery channel. Joining a stranger's
  server must not be a way to receive a DLL. Nothing loads out of the materialised tree today
  except WeaponForge's own JSON/PNG/WAV readers — but that is a property of today's code, and
  this feature deliberately points another plugin's loader at that tree.
- The obvious thing for a host to set `ContentRoot` to is WeaponForge's own plugin folder, which
  contains `WeaponForge.dll`. Publishing it would redistribute a third-party binary carrying no
  licence that grants it, to every player who joins.

It is a denylist, not an allowlist: an allowlist would have to guess which formats a content mod
we don't control considers valid, and would refuse legitimate content the first time one added a
format. A denylist only has to know what is dangerous, which is a smaller and far more stable set.

The receiver also re-validates paths: it writes host-chosen filenames to disk, so
`Path.GetFullPath` must stay under `active/<hash>`. That check is security-relevant and lives in
one function.

---

## Transfer

Messages `101–105` (`ContentOffer` / `Need` / `Chunk` / `Done` / `Status`), all on
**`NetChannel.Events`**.

- **Events, never Control.** A multi-megabyte stream must not be able to queue in front of a
  `Welcome`.
- **`Transport.Send` directly, never `NetSession.SendReliable`.** SendReliable copies every
  payload and caps its outbox at 8192 with drop-oldest — a GC storm plus silent loss at a few
  MB/s. Backpressure comes off `Send`'s bool return, the same way `WorldSync` streams terrain.
- **Budget is bytes per second, not per frame.** `ServerFrameRateCap` deliberately caps a
  dedicated server's frame rate; a per-frame budget would quietly halve its throughput.

### Sequence

```
host   BeginHosting()        hashes ContentRoot once, when hosting starts — before anyone joins,
                             so the first joiner never waits on the host's disk
join   Welcome  ->  Offer    immediately after the Welcome: the transfer runs while the player
                             reads the lobby, not after they press START
client compares by digest    cache hit -> Done, zero chunks
                             otherwise -> Need(only what it lacks, + .part length to resume)
host   Chunk...              rate-budgeted, dedup'd: one blob, one download
client verify -> install     into active/<setHash>/, verified DURING the copy
client Done(ok)  ->  host
```

**The client recomputes the set hash from the file list it receives and compares it to the one
the host named, before a single content byte moves.** So both machines' hash implementations are
validated against each other on every single join — the property `tools/content-hash-test.ps1`
checks offline is also checked continuously in production.

### Threading

`NetWriter`, `NetReader` and `NetSession` are main-thread only. Hashing, disk IO, verification,
materialisation and eviction all run on one worker thread; the hand-off is `byte[]` through a
`ConcurrentQueue`, drained per frame under a bounded budget.

The drain uses **ReceivePump's termination rule** — process the backlog present at entry, never
chase the live tail. That rule exists because "drain until empty" was a 55-second freeze on this
project once already.

---

## Gating

One line, host-side, in `HandleSetLobbyPrefs`:

```csharp
player.Ready = prefs.Ready && Content.ContentSync.Satisfied(player.Slot);
```

`AllReady` and the START button already follow from `Ready`, so nothing else needed changing.
Host-side is the point: a modded client cannot report itself ready past the gate.

`Satisfied` **fails closed** — an unknown peer is `Idle`, which is not `Satisfied` — with two
deliberate exceptions that both mean *there is nothing to be out of sync with*: the host hasn't
finished hashing yet, or the host serves nothing at all.

---

## What the player sees

`UI/ContentDownloadScreen.cs` — a modal with a progress bar, a percentage, the byte counts, and
one button: **CANCEL AND LEAVE**.

**When it appears.** Whenever this machine is downloading or installing — which is from the moment
it *joins*, not from the moment the host presses START. The transfer is kicked off straight after
the `Welcome` so it runs while the player reads the lobby, so on a small pack or a warm cache it
finishes before anyone could have pressed anything and **the screen never appears at all**. When
it does appear, it is because there was genuinely something to wait for.

Either way it is always gone before ship selection in co-op and before drop selection in Battle
Royale — not by a check in either screen, but because neither can begin until the run starts and
the run cannot start while this is up. The gate is the mechanism; the modal only explains it.

**Why a modal at all.** The go-live gate already refuses to start a run for anyone still syncing.
Without this screen that refusal is *silent*, and a silent refusal reads as a broken lobby with a
greyed-out START button.

**Progress is measured in bytes, not files.** A ten-file set where one is 2 MB and the rest are
2 KB would make a file-counting bar sit at 90% for the whole download and then jump. The
denominator is what *this machine still needs*, not what the set weighs, so a player who already
has nine of ten files sees a bar that fills rather than one that stops at 10%. Resume subtracts
what the `.part` already holds. The bar caps at 99% while installing — 100% belongs to "you can
play".

Clients report progress to the host every 0.5s (`ContentStatusMsg`), so the lobby can show a
per-player figure instead of a binary not-ready.

**Cancel leaves.** Not "cancel and keep waiting" — once you have refused the content there is
nothing to wait for: the run cannot start without you, and you cannot play with a weapon set that
differs from everyone else's. So the button says what it does. Escape does the same, because a
modal you cannot dismiss with Escape *is* a stuck lobby.

Cancelling must **not** open the gate. The host marks that slot failed and START stays refused —
removing the player being waited on is not the same as satisfying them, and getting this backwards
would start a run with someone on different content, which is the exact divergence the feature
exists to prevent. Partial downloads stay on disk as digest-keyed `.part` files, so coming back
later resumes.

---

## Applying it — the swap

Having the bytes on disk is not the same as playing with them. `ForgeContentSwap` points
WeaponForge at the materialised set, on the host and on every client, and puts the player's own
content back when the session ends.

This was expected to need an upstream change or a transpiler into WeaponForge's `LoadAll` bodies.
It does not. All three of its content roots are `public static string` methods —
`ForgeRegistry.WeaponsFolder`, `ForgeSpriteLibrary.SpritesFolder`, `ForgeSoundLibrary.SoundsFolder`
— and each has exactly **one** call site, inside its own loader. Three Harmony postfixes redirect
every read.

Two things in the reload sequence are load-bearing, and both were found by reading their code:

1. **Drop the old forge modules from `ModuleRegistry.itemList` first.** Their `RegisterInto`
   *skips* ids already present, so without this the old and new sets coexist and the module
   digest can never match the host's.
2. **Reset the sprite and sound `_loaded` latches.** Both loaders return immediately once loaded,
   so a swap that skips this loads the new *weapons* against the *old* sprites and sounds. That is
   the failure that looks like it worked.

Outgoing Unity objects are deliberately **not** destroyed. Destroying them is what turns a stale
reference into a crash, and a session performs at most two swaps — a few MB held until the process
exits is the cheaper mistake. For the same reason the swap is lobby-only: a rebuild replaces
objects that installed `Module`s hold.

Every failure path leaves the player's own content in place, never a half-applied set. If the swap
cannot run at all, the go-live digest still refuses a divergent run — so a broken swap costs a
session its custom weapons, not its integrity.

**Still owed upstream.** Postfixing another mod's methods and writing its private statics works,
but it is a contract nobody promised us and their next release can end it silently. The
`ForgeInterop` hook is still worth asking Sugarheady for, and WeaponForge is worth tracking in
gamescan — that is now the early warning for a swap that has quietly stopped working.

---

## Verification

| | |
|---|---|
| [`tools/content-hash-test.ps1`](../tools/content-hash-test.ps1) | Two installs hash the same fixture; the **full** output must match — set digest and every per-file digest. No session, no network, seconds to run. **Lead with this**: separators, case, ordering and encoding are what differ across platforms, and this catches all four. Run it against the Wine server image too. |
| [`tools/content-test.ps1`](../tools/content-test.ps1) | End to end: `coldsync`, `gate`, `warmsync`, `nocontent`. |

`content-test.ps1` verifies the installed tree with `Get-FileHash` against the fixture — byte
identity proven **outside** the code under test. Every other assertion in the suite is the mod
agreeing with itself, and a bug in `ContentHash` would keep them all green.

The gate phase throttles the transfer on purpose so "START was refused mid-download" is an
observation rather than a race the harness happened to win.

---

## Why any of this

`Modes/BattleRoyaleLootTables.cs` builds its drop pools from
`ModuleRegistry.AllItems.OrderBy(Id, Ordinal)` and picks `pool[rnd.Next(pool.Count)]` — where
`rnd` is seeded per entity and rolled **independently on every machine**. BR's contested-loot
identity is `(Group, Ordinal)`: parallel deterministic rolls, host arbitrates the claims.

So a host running a content mod alongside a client who isn't doesn't merely miss custom items.
The extra registry entries **shift every index**, and the entire drop table desyncs. Nothing about
it looks wrong on either screen.

That is the class of bug this feature is paying for: not "the weapon is missing" but "both players
are looking at a different world and neither can tell".
