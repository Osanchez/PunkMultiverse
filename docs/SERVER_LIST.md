# Publishing a session to the server browser

This mod can advertise a session so strangers can find it, instead of only friends with an invite or
the lobby code. The browser that reads those listings is **PUNK Nexus**.

> The full discovery contract — both transports, the key table, and the unbuilt UDP relay design —
> lives in [`PunkNexus/docs/SERVER_LIST.md`](https://github.com/Osanchez/PunkNexus/blob/main/docs/SERVER_LIST.md).
> This page is only the mod's half.

## It is off by default, on purpose

Hosting normally creates a **friends-only** Steam lobby: reachable by invite, by the Steam overlay,
or by pasting the `PMV-…` code, and by nobody else. A co-op run with three friends must never appear
in a public list because someone shipped a default the other way.

```ini
[Session]
PublishServer = true             # the opt-in. Default false.
ServerName    = Neon Wasteland   # blank -> "<your persona>'s Co-op"
ServerRegion  = EU               # blank -> no region shown
```

With it on, the lobby is created **public** and stamped with browser metadata. Steam hosts the
listing, so there is nothing to port-forward and no address of yours is published — a joiner reaches
you over Steam's relay exactly as an invited friend would.

Only meaningful on the `Steam` and `SteamServer` transports. A `Udp` server has no Steam lobby at all
and is not discoverable this way; see Part 2 of the Nexus document.

## What gets published

Identity keys (`pmvver`, `gamebuild`, `host`, and for SteamServer sessions `xport` / `srvid`) are
stamped on every lobby and always have been — they are how joining works. The browser keys
(`listed`, `name`, `mode`, `region`, `maxp`, `np`, `mods`) are written **only** when `PublishServer`
is on. Their absence is what keeps a private lobby private, so nothing in that second group may ever
be stamped unconditionally.

`np` is the host's own count of occupied player slots. It is published rather than left to the
browser's `GetNumLobbyMembers`, which is only dependable for a lobby the caller has joined — and a
browser is not a member of anything it is listing.

`mods` is the **catalog id** of each installed mod, read from its `mod.json`, capped at 12
(`ModManifest.BrowserList`). Catalog ids rather than BepInPlugin GUIDs because the consumer is PUNK
Nexus, and a catalog id is what it can act on — look the mod up, show its name, install it for
someone joining a server. A GUID would leave the client guessing which listing it belongs to.
Folders with no `mod.json` fall back to their plugin GUID so a hand-built mod is still visible.

Versions are dropped: the browser filters on identity, the HELLO handshake is what actually
enforces version agreement, and Steam caps how much metadata a lobby may carry.

## Staleness, and why there is none

A Steam lobby is destroyed when its last member leaves. A host that quits, crashes, or drops its
connection takes the listing with it — there is no heartbeat to miss and no expiry to tune. The
browser reads the list live on every refresh, so there is no cached copy to be wrong either.

Three cases are handled explicitly in `SteamLobbyController` because each would otherwise leave a
wrong row in someone's browser:

| Case | Handling |
|---|---|
| Session fills up | `SetLobbyJoinable(false)`. Stays visible as full; the browser's slots-available filter drops it. |
| Publishing switched off mid-session | Clear `listed` **and** set the lobby type back to friends-only. Clearing the key alone leaves it reachable by id. |
| Host migration | Takeover clears the old host's listing. Lobby data outlives its author, so an inherited listing would advertise their name and player count forever. The new host's publish tick decides whether to relist. |

## Republish cadence

`NetSession.MaintainListing` ticks every 3 seconds, but `PublishListing` fingerprints the listing and
writes nothing when nothing a browsing player would notice has changed. Steam rate-limits
`SetLobbyData`; spending that budget on no-ops eventually costs a real update.

Only the lobby **owner** may write lobby metadata — Steam silently drops writes from anyone else.
Ownership is asked of Steam rather than remembered, so migration answers correctly.

## Launch arguments (auto-connect)

The game can be started straight into a session, which is how PUNK Nexus' **Play** button works and
what a "click to join" shortcut would use.

| Argument | Target | Read from |
|---|---|---|
| `+connect_lobby <SteamID64>` | A Steam lobby | Steam's own convention — the overlay and friends list pass this on a cold start. Not ours; read exactly as Steam writes it. |
| `+punkmv_connect <target>` | Anything the JOIN button accepts: `host:port`, a dedicated server's SteamID64, or a `PMV-…` code | Ours |
| `+connect <target>` | Same as above | Alias, because `+connect host:port` is the near-universal convention |

```
Punk.exe +connect_lobby 109775241234567890      # a friend's Steam session
Punk.exe +punkmv_connect 203.0.113.10:7777      # a dedicated UDP server
Punk.exe +punkmv_connect PMV-ABCDE-FGHJK-MNPQ   # a pasted lobby code
```

`+punkmv_connect` is deliberately **transport-agnostic**, and that is the whole point of it
existing: a UDP server has no Steam lobby to hand out, so before this it could not be auto-joined
at all. The value is not parsed by the launch code — it is handed to `JoinByCode`, which already
knows how to tell an address from a server id from a lobby code and overrides the transport to
match. A second parser would be a second thing to keep in agreement with the first.

That override is what makes this work for ordinary players: someone whose config says `Steam` still
lands on a UDP server correctly, with no config edit and no persisted change to their default.

`+connect_lobby` wins if both are passed. Both are read in `Core/LaunchArgs`; a flag with no value,
or a malformed id, is ignored rather than treated as an error. Unity ignores unknown arguments, so
passing these to a build without the mod installed is inert.

Steam must be running for the Steam forms — but not for `host:port`, which is the case that needs
no Steam at all.

## Not built yet

PunkMultiverse publishes; it does not browse. There is no in-game server list — joining a listed
session means going through PUNK Nexus or an invite. Adding `RequestLobbyList` to the PLAY ONLINE
screen would reuse everything above unchanged.
