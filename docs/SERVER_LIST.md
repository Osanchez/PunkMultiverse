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

`mods` is the local BepInEx plugin GUIDs, without versions, capped at 12
(`ModManifest.BrowserList`). The browser filters on mod *identity*; the HELLO handshake is what
actually enforces version agreement, and Steam caps how much metadata a lobby may carry.

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

## Not built yet

PunkMultiverse publishes; it does not browse. There is no in-game server list — joining a listed
session means going through PUNK Nexus or an invite. Adding `RequestLobbyList` to the PLAY ONLINE
screen would reuse everything above unchanged.
