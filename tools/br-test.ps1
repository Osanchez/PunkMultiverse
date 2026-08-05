# BATTLE ROYALE HARNESS: run a whole compressed match against local bots and assert both the
# LIFECYCLE (from the coordinator log) and the live BEHAVIOUR the mode depends on - ring paint
# cost, player ship sync, player-to-player damage registration, health/fuel bar data, and
# contested loot arbitration. Shortened timers so a full match fits in one run.
#
# The behaviour probes run as a scripted block right after go-live, BEFORE the timeline is left
# to play out: every one of them needs the two ships in a known relationship (adjacent, one
# shooting the other), which a free-running match will never hand you.
#
# DEV installs only (OD Test2 coordinator, OD Dev3/Dev4 bots). ASCII only. BOM-free configs.
param(
    [int]$Bots = 2,
    [int]$WatchSeconds = 420,
    # lifecycle | ring | sync | pvp | bars | loot | fire | all. Comma-separated.
    [string]$Phases = "all",
    # Fire simprof at the coordinator while the ring is mid-closure and print the attribution.
    # The ring phase measures what PAINTING costs; this answers what the painted WORLD costs,
    # which turned out to be a far bigger number (2026-07-28: the host fell from 120fps to 0.2fps
    # during a match while paint itself stayed under 50ms per 10s).
    [switch]$ProfileRing,
    # Run the bots WITH the drop screen and drive it via the `drop` devcmd. The drop path was
    # manual-test-only, which is how "deploy drops you through terrain that has not streamed in"
    # reached a live match: no automated run could reach Deploy at all. Implies its own assertions
    # and skips the other probes, which all need ships already placed.
    [switch]$DropScreen
)
$ErrorActionPreference = "Stop"
$CoordDir = "C:\Program Files (x86)\Steam\steamapps\common\PUNK Playtest - OD Test2"
$BotDirs  = @(@(
    "C:\Program Files (x86)\Steam\steamapps\common\PUNK Playtest - OD Dev3",
    "C:\Program Files (x86)\Steam\steamapps\common\PUNK Playtest - OD Dev4"
) | Select-Object -First $Bots)
$CoordPlug = Join-Path $CoordDir "BepInEx\plugins\PunkMultiverse"
$CoordLog  = Join-Path $CoordDir "BepInEx\LogOutput.log"

# Split on commas AND whitespace. `-Phases lifecycle,ring` without quotes is parsed by PowerShell
# as an ARRAY, which coerces into the single string "lifecycle ring"; splitting on "," alone then
# produced one bogus phase name, every Phase() test returned false, and the run happily printed
# "BR SMOKE: PASS" having asserted nothing at all. A harness that can pass without testing is worse
# than no harness, so unknown names are now a hard error rather than a silent skip.
$KnownPhases = @("all","lifecycle","ring","sync","pvp","bars","loot","fire")
$Want = @($Phases -split '[,\s]+' | ForEach-Object { $_.Trim().ToLower() } | Where-Object { $_ })
$bogus = @($Want | Where-Object { $KnownPhases -notcontains $_ })
if ($bogus.Count -gt 0) {
    "ABORT: unknown phase(s): $($bogus -join ', '). Valid: $($KnownPhases -join ', ')"
    exit 2
}
if ($Want.Count -eq 0) { "ABORT: -Phases resolved to nothing"; exit 2 }
Write-Host "phases: $($Want -join ', ')"
function Phase($name) { return ($Want -contains "all") -or ($Want -contains $name) }

function CountIn($p,$pat){ if(-not(Test-Path $p)){return 0}; return @(Select-String -Path $p -Pattern $pat -AllMatches -EA SilentlyContinue).Count }
function WaitFor($p,$pat,$to,$what,$min=1){ $d=(Get-Date).AddSeconds($to); while((Get-Date)-lt $d){ if((CountIn $p $pat)-ge $min){return $true}; Start-Sleep 3 }; Write-Host "TIMEOUT $what"; return $false }
function Cmd($plug,$txt){ Add-Content -Path (Join-Path $plug "devcmd.txt") -Value $txt -Encoding Ascii }
function Lines($p,$pat){ if(-not(Test-Path $p)){return @()}; return @(Select-String -Path $p -Pattern $pat -AllMatches -EA SilentlyContinue) }
# Every key this run overwrites, so the finally block can put it back. config.cfg PERSISTS, and
# these are the SAME installs Omar plays on: the harness's BrChooseSpawn=false silently disabled
# the drop screen for his second player until he reported it as "the second player is still auto
# spawning" (2026-07-29). A test harness must not be able to change how the game plays afterwards.
$script:CfgBackups = @()   # each: @{ Path=...; Key=...; Line=...; Existed=$bool }

function BackupCfgKeys([string]$path, [string[]]$keys) {
    if (-not (Test-Path $path)) { return }
    $cfg = Get-Content -Raw $path
    foreach ($k in $keys) {
        $pat = "(?m)^{0}\s*=.*$" -f [regex]::Escape($k)
        $m = [regex]::Match($cfg, $pat)
        $script:CfgBackups += @{ Path = $path; Key = $k; Line = $(if ($m.Success) { $m.Value } else { $null }); Existed = $m.Success }
    }
}

function RestoreCfgKeys() {
    foreach ($b in $script:CfgBackups) {
        if (-not (Test-Path $b.Path)) { continue }
        $cfg = Get-Content -Raw $b.Path
        $pat = "(?m)^{0}\s*=.*$" -f [regex]::Escape($b.Key)
        if ($b.Existed) { $cfg = [regex]::Replace($cfg, $pat, $b.Line) }
        else { $cfg = [regex]::Replace($cfg, $pat, "") }   # we introduced it; take it back out
        [System.IO.File]::WriteAllText($b.Path, $cfg)
    }
    if ($script:CfgBackups.Count -gt 0) {
        Write-Host "restored $($script:CfgBackups.Count) config key(s) to their pre-test values"
        $script:CfgBackups = @()
    }
}

function SetCfg([string]$path, [hashtable]$kv, [string]$section = "Session") {
    # Replace the key if present; INSERT it under the section header if not. A plain replace
    # silently no-ops for a key the installed build has never written yet, and the game then
    # overwrites the file with defaults - which is exactly how the first BR run came up Standard.
    BackupCfgKeys $path @($kv.Keys)
    $cfg = Get-Content -Raw $path
    foreach ($k in $kv.Keys) {
        $pat = "(?m)^{0}\s*=.*$" -f [regex]::Escape($k)
        $line = "{0} = {1}" -f $k, $kv[$k]
        if ($cfg -match $pat) { $cfg = $cfg -replace $pat, $line }
        else {
            $hdr = "(?m)^\[{0}\]" -f [regex]::Escape($section)
            if ($cfg -match $hdr) { $cfg = $cfg -replace $hdr, ("[{0}]`r`n{1}" -f $section, $line) }
            else { $cfg = $cfg.TrimEnd() + "`r`n`r`n[$section]`r`n$line`r`n" }
        }
    }
    [System.IO.File]::WriteAllText($path, $cfg)
}
function StartGame($dir, $coord) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = Join-Path $dir "Punk.exe"; $psi.Arguments = "-batchmode -nographics"
    $psi.WorkingDirectory = $dir; $psi.UseShellExecute = $false
    if ($coord) { $psi.EnvironmentVariables["PUNKMV_COORDINATOR"]="1"; $psi.EnvironmentVariables["PUNKMV_TRANSPORT"]="Udp" }
    # Drop screen OFF for every harness process, via the ENVIRONMENT rather than config.cfg. A bot
    # cannot click a drop screen, but config.cfg persists and these installs are played on — writing
    # the key there disabled the drop screen for Omar's second player long after the test ended
    # (2026-07-29). An env var dies with the process; that is the whole point.
    $psi.EnvironmentVariables["PUNKMV_BR_CHOOSE_SPAWN"] = $(if ($DropScreen -and -not $coord) { "1" } else { "0" })
    foreach($k in @($psi.EnvironmentVariables.Keys | Where-Object {$_ -like "DOORSTOP*"})){ $psi.EnvironmentVariables.Remove($k) }
    return [System.Diagnostics.Process]::Start($psi).Id
}
function Show($label, $pat) {
    $hits = @(Select-String -Path $CoordLog -Pattern $pat -EA SilentlyContinue)
    if ($hits.Count -eq 0) { Write-Host ("  {0,-22} MISSING" -f $label); return $false }
    Write-Host ("  {0,-22} {1}" -f $label, (($hits[0].Line -replace '.*Punk Multiverse\] ','')))
    return $true
}
function Line($label, $text) { Write-Host ("  {0,-22} {1}" -f $label, $text) }

$devRoots = @($CoordDir) + $BotDirs
if (Get-Process Punk -EA SilentlyContinue | Where-Object { $devRoots -contains (Split-Path $_.Path -Parent) }) {
    "ABORT: a DEV-install Punk.exe is already running."; exit 2
}

$pids = @()
try {
    SetCfg (Join-Path $CoordPlug "config.cfg") @{
        "Transport"="Udp"; "UdpPort"="7787"; "CommandFile"="devcmd.txt"; "AutoLaunchRun"="false";
        "LogLevel"="Verbose"; "PreGenerateWorld"="true"; "EmptyServerResetSeconds"="600";
        # BrMatchMinutes + BrRingStages are the WHOLE ring schedule now: the per-zone wait and
        # closure times are derived from them on a curve, and there is no per-zone knob left to
        # set. 6 min / 4 zones gives waits of 107/39/7/0s and closures of 74/59/44/29s.
        "EnableGameModes"="true"; "GameMode"="BattleRoyale"; "BrMatchMinutes"="6";
        "BrRingStages"="4"; "BrCarePackageMinutes"="2"; "BrMinPlayers"="1"
    }
    Remove-Item -Force -EA SilentlyContinue (Join-Path $CoordPlug "devcmd.txt"), $CoordLog
    foreach ($d in $BotDirs) {
        $plug = Join-Path $d "BepInEx\plugins\PunkMultiverse"
        SetCfg (Join-Path $plug "config.cfg") @{
            "Transport"="Udp"; "UdpAddress"="127.0.0.1"; "UdpPort"="7787"; "AutoStart"="Join";
            "AutoReady"="true"; "CommandFile"="devcmd.txt"; "LogLevel"="Normal"; "AutoLaunchRun"="false"
        }
        Remove-Item -Force -EA SilentlyContinue (Join-Path $plug "devcmd.txt"), (Join-Path $d "BepInEx\LogOutput.log"), (Join-Path $plug "devout.txt")
    }

    $pids += StartGame $CoordDir $true
    if (-not (WaitFor $CoordLog "\[Udp\] hosting" 120 "coordinator hosting")) { throw "no host" }
    if (-not (WaitFor $CoordLog "\[PreGen\] world ready" 180 "pre-build")) { throw "no pre-build" }
    Write-Host "coordinator up, world pre-built"

    foreach ($d in $BotDirs) { $pids += StartGame $d $false; Start-Sleep 2 }
    if (-not (WaitFor $CoordLog "joined" 150 "bot joins" $BotDirs.Count)) { throw "bots did not all join" }
    Write-Host "$($BotDirs.Count) bots joined"
    Start-Sleep 5

    $BotPlugs = @($BotDirs | ForEach-Object { Join-Path $_ "BepInEx\plugins\PunkMultiverse" })
    $BotLogs  = @($BotDirs | ForEach-Object { Join-Path $_ "BepInEx\LogOutput.log" })

    Cmd $BotPlugs[0] "start"
    if (-not (WaitFor $CoordLog "GO LIVE" 180 "go-live")) { throw "never went live" }
    # Bots fly blind into hazards and die in seconds, which ends the match before the ring or a
    # care package ever appears. God-mode keeps them alive so the whole timeline is observable;
    # the elimination path is proven separately by ungodding one at the end.
    Start-Sleep 5
    foreach ($p in $BotPlugs) { Cmd $p "god" }
    Write-Host "MATCH LIVE (bots godded)"

    # Each bot's SLOT, straight from its own welcome. Slots are not 0/1 in a dedicated run - the
    # coordinator occupies one - and every probe below addresses a player by slot.
    $BotSlots = @()
    foreach ($lg in $BotLogs) {
        $m = @(Lines $lg "welcomed as slot (\d+)")
        if ($m.Count -eq 0) { throw "could not read a bot's slot from its log" }
        $BotSlots += [int]$m[0].Matches[0].Groups[1].Value
    }
    Write-Host ("bot slots: " + ($BotSlots -join ", "))

    # ============================ BEHAVIOUR PROBES ============================
    # Every probe below needs the ships adjacent. BR scatters spawns ~1600 units apart on purpose,
    # so none of this is observable until that distance is collapsed.
    if ($DropScreen) {
        Write-Host "probe: drop screen -> deploy -> settle"
        foreach ($i in 0..($BotDirs.Count-1)) {
            if (-not (WaitFor $BotLogs[$i] "drop window open" 120 "bot$i drop window")) { $ok = $false; continue }
        }
        Start-Sleep 4                      # let the input-arm delay pass
        foreach ($p in $BotPlugs) { Cmd $p "drop" }
        Start-Sleep 25                     # deploy + settle + protection window
    }

    $probed = $false
    if ($BotDirs.Count -ge 2 -and ((Phase "sync") -or (Phase "pvp") -or (Phase "bars") -or (Phase "loot") -or (Phase "fire"))) {
        $probed = $true
        Cmd $BotPlugs[0] ("tpplayer {0}" -f $BotSlots[1])
        Start-Sleep 5
    }

    if ($probed -and (Phase "sync")) {
        # Ship sync: bot1 flies a full-throttle circle (a held heading extrapolates perfectly and
        # would hide the very defect we are measuring), bot0 samples the DRAWN pose of bot1's ship
        # every render frame.
        Write-Host "probe: ship sync (28s)"
        Cmd $BotPlugs[1] "orbit 26 4"
        Start-Sleep 2
        Cmd $BotPlugs[0] ("shipsmooth {0} 20" -f $BotSlots[1])
        Start-Sleep 28
    }

    if ($probed -and ((Phase "pvp") -or (Phase "bars"))) {
        # Player-to-player damage. God mode does NOT shield a ship from a ROUTED damage request
        # (ApplyDamageRequest runs with _applyingRemote set, which the god gate sits behind), so
        # the bots stay alive against the ring while PvP still lands and can be measured.
        Write-Host "probe: player-vs-player damage (22s)"
        # Both ships sit on a station, embedded in ground. Clearing terrain alone was not enough:
        # a ship FALLS after a teleport, so a pocket cleared on arrival is 30 units above it by the
        # time it shoots (measured 2026-07-29: cleared at y=490, fired from y=475, buried again,
        # `player bullet HIT layer=10(Ground)` at the shooter's own position). `pvpstage` clears the
        # pocket AND pins gravity to zero, so the two ships stay in the empty room they were given.
        # Order matters: the target is staged first, then the shooter teleports beside it and stages
        # its own pocket around the position it will actually fire from.
        # The target is lifted clear of the station FIRST (a station's Hatch and Platform are prefab
        # colliders on the Ground layer that `clearterrain` cannot delete - they blocked the line at
        # 6 units while the target sat at 9.2), then the shooter teleports beside it up there and
        # stages its own pocket. Both then hang in open air with nothing in between.
        # Autofly OFF first, or the two ships simply fly apart: the probe caught them 38 units
        # apart moments after being staged 8 units apart. Re-armed after the burst so the rest of
        # the match still plays out.
        foreach ($p in $BotPlugs) { Cmd $p "autofly 0" }
        Start-Sleep 2
        Cmd $BotPlugs[1] "pvpstage 30 45"
        Start-Sleep 2
        Cmd $BotPlugs[0] ("tpplayer {0} 5" -f $BotSlots[1])
        Start-Sleep 1
        Cmd $BotPlugs[0] "pvpstage 30"
        Start-Sleep 2
        Cmd $BotPlugs[0] "shipbars"
        Cmd $BotPlugs[1] "shipbars"
        Start-Sleep 1
        # One shot first, so the probe below reads the mask off a REAL bullet rather than the
        # physics matrix, then the physical verdict, then the measured burst.
        Cmd $BotPlugs[0] ("fire 1 player {0}" -f $BotSlots[1])
        Start-Sleep 2
        Cmd $BotPlugs[0] "pvpprobe"
        Start-Sleep 2
        # Re-anchor before each short burst rather than trusting one teleport to hold for twelve
        # seconds. The target kept drifting out to 20-38 units between staging and firing (its own
        # machine keeps simulating it, and the puppet the shooter aims at lags behind by the interp
        # delay), which made the whole probe a coin flip: identical code measured hitAnotherShip=4
        # on one run and 0 on the next. Four short cycles put the two ships back at 5 units apart
        # immediately before every burst.
        for ($cyc = 0; $cyc -lt 4; $cyc++) {
            Cmd $BotPlugs[0] ("tpplayer {0} 5" -f $BotSlots[1])
            Start-Sleep 1
            Cmd $BotPlugs[0] "pvpprobe"
            Cmd $BotPlugs[0] ("fire 3 player {0}" -f $BotSlots[1])
            Start-Sleep 3
            # Sample the bars WHILE the victim is hurt. The bots are godded so the run survives the
            # ring, and god heals them back to full within a second or two - so a sample taken after
            # the burst finished always read a healthy ship and the bar check failed on a ship that
            # had genuinely just been shot (PvP damage is scaled x0.25, so the dip is small and
            # short-lived).
            Cmd $BotPlugs[0] "shipbars"
            Cmd $BotPlugs[1] "shipbars"
            Start-Sleep 2
        }
        Cmd $BotPlugs[0] "shipbars"
        Cmd $BotPlugs[1] "shipbars"
        Start-Sleep 3
        foreach ($p in $BotPlugs) { Cmd $p "autofly 600" }
    }

    if ($probed -and (Phase "fire")) {
        # Fire vs the damage shields. Burn is ticked straight out of DamagableResource.Update via a
        # private Damage(float), so it never passes TakeDamage - which is where god mode, shop
        # invulnerability and Battle Royale spawn protection are all enforced. A/B in one run:
        # UNSHIELDED must burn and lose health, SHIELDED must not, and the shielded ship must not
        # still be alight afterwards (the whole point - you cannot walk out of spawn protection on
        # fire and immediately start dying to it).
        # BR loot tables FIRST, while the bot is still godded and the match safely alive:
        # destroy a container next to bot0 and watch what falls out of it.
        Write-Host "probe: crate loot"
        Cmd $BotPlugs[0] "spawn CrateTech"
        Start-Sleep 3
        Cmd $BotPlugs[0] "fire 6 dir 1 0"
        Start-Sleep 9

        Write-Host "probe: fire vs shields (22s)"
        Cmd $BotPlugs[0] "god off"
        Start-Sleep 1
        Cmd $BotPlugs[0] "burn 100"
        Start-Sleep 4
        Cmd $BotPlugs[0] "burn"          # read-only: unshielded state
        Start-Sleep 2
        Cmd $BotPlugs[0] "god"           # shield back on before the ring finishes the job
        Start-Sleep 1
        Cmd $BotPlugs[0] "burn 100"
        Start-Sleep 5
        Cmd $BotPlugs[0] "burn"          # read-only: shielded state
        Start-Sleep 2
        # ENEMY DAMAGE SCALE. Spawn a shooter next to an un-godded bot: the spawn areas are cleared
        # and the bots are godded all match, so a normal run never lands a single enemy hit and the
        # scale went three runs unmeasured.
        # Several types, because the first attempt spawned a Unit_Floater_Soldier whose projectiles
        # log amount=0 - it hit the bot four times and there was nothing to scale. A probe enemy has
        # to actually deal damage or it measures nothing.
        Cmd $BotPlugs[0] "god off"
        foreach ($e in @("Enemy_Turret_Worm","Unit_FlyAlfa","Enemy_Raven","Unit_Floater_SoldierPurple")) {
            Cmd $BotPlugs[0] ("spawn {0}" -f $e)
        }
        Start-Sleep 22
        Cmd $BotPlugs[0] "god"
        Start-Sleep 2
    }

    if ($probed -and (Phase "loot")) {
        # Contested loot: both ships in the same place, both mining. Terrain drops are keyed by
        # cell, so the two machines roll the same pile and exactly one may claim each item.
        Write-Host "probe: contested loot (27s)"
        Cmd $BotPlugs[1] ("tpplayer {0} 3" -f $BotSlots[0])
        Start-Sleep 2
        Cmd $BotPlugs[0] "fire 20 dir 0 -1"
        Cmd $BotPlugs[1] "fire 20 dir 0 -1"
        Start-Sleep 27
    }

    if ($probed) { Write-Host "probes done - letting the match play out" }

    # Let the match run: ring stages, care packages, eliminations.
    $profiled = $false
    $zoneProbed = $false
    $deadline = (Get-Date).AddSeconds($WatchSeconds)
    while ((Get-Date) -lt $deadline) {
        if ((CountIn $CoordLog "\[BR\] WINNER") -ge 1) { Write-Host "match resolved early"; break }

        # ZONE DAMAGE, mid-match. The endgame ungod below already proves the FINAL ring kills, but
        # the final ring closes to radius zero — everyone is outside it, so it cannot distinguish
        # "the zone burns you" from "the match ended". This probe puts a bot outside a real,
        # mid-game circle on ground that exists, and asserts it starts burning there.
        # Runs once closure 3 has begun, i.e. the ring is sitting on zone 2's radius and centre,
        # which the match-start ladder already told us exactly.
        if (-not $zoneProbed -and (CountIn $CoordLog "THE LAVA RING IS CLOSING \(3/") -ge 1) {
            $zoneProbed = $true
            $zones = @(Lines $CoordLog "zone (\d+)/(\d+): wait \d+s, close \d+s, r (\d+) -> (\d+), center \((-?\d+),(-?\d+)\)")
            if ($zones.Count -ge 2) {
                $g = $zones[1].Matches[0].Groups     # zone 2: its END radius + END centre
                $r  = [double]$g[4].Value
                $cx = [double]$g[5].Value; $cy = [double]$g[6].Value
                # 90 units beyond the boundary: unambiguously outside, still nowhere near the void
                # border on a ~1000-unit map once the ring has closed twice.
                $tx = [int]($cx + $r + 90); $ty = [int]$cy
                Write-Host "probe: zone damage - bot0 -> ($tx,$ty), outside r=$r at ($cx,$cy)"
                Cmd $BotPlugs[0] "god off"
                Cmd $BotPlugs[0] "tp $tx $ty"
                Start-Sleep 12
                $hit = CountIn $BotLogs[0] "\[BR\] (ENTERED THE ZONE|in the zone)"
                Write-Host "  burn reports while outside: $hit"
                # Put it back and re-arm god, so ONE probe doesn't decide the match and the
                # remaining closures still get watched with two bots alive.
                Cmd $BotPlugs[0] "tp $([int]$cx) $([int]$cy)"
                Cmd $BotPlugs[0] "god"
            } else { Write-Host "probe: zone damage SKIPPED - could not parse the zone ladder" }
        }
        # Profile once the ring has had time to convert real ground - the cost being chased only
        # exists after a large area has been painted, so profiling at match start measures nothing.
        if ($ProfileRing -and -not $profiled -and (CountIn $CoordLog "\[BR\] ring paint") -ge 6) {
            $profiled = $true
            Write-Host "firing simprof + cellfanout at the coordinator (mid-closure)"
            # simprof blames the publisher (LevelChangeBuffer.Update); cellfanout says WHICH of its
            # eight subscribers is responsible. Both, or the answer is "a method whose entire body
            # is one Invoke", which explains nothing.
            Cmd $CoordPlug "cellfanout on"
            Cmd $CoordPlug "simprof 20"
        }
        Start-Sleep 10
    }

    # Prove the endgame too: drop god on EVERY bot and let the ring finish them. God must come off
    # the eventual winner as well — the winner's self-destruct damages its own ship, and
    # IsGodShieldedLocalShip blocks exactly that, so leaving one godded made the self-destruct
    # assertion pass or fail on which bot happened to survive.
    foreach ($p in $BotPlugs) { Cmd $p "god off" }
    WaitFor $CoordLog "\[BR\] WINNER" 240 "winner" | Out-Null
    # The winner's self-destruct is on a countdown (BrWinnerSelfDestructSeconds, default 10s) and
    # the host holds the run open for it. Assert AFTER it has had time to fire, or the check races
    # the countdown and reports MISSING on a perfectly good run.
    Start-Sleep 18

    Write-Host ""
    Write-Host "=============== BATTLE ROYALE RESULTS ==============="
    $ok = $true

    if (Phase "lifecycle") {
        Write-Host "--- lifecycle ---"
        $ok = (Show "match start"      "\[BR\] MATCH START") -and $ok
        $ok = (Show "ring center"      "\[BR\] ring center") -and $ok
        $ok = (Show "playable map"     "\[BR\] playable map") -and $ok
        # (No "ring material" check: the zone is rendered, not made of terrain, so there is no
        # cell type to resolve. Its absence is asserted positively in the ring phase instead.)
        $ok = (Show "stations opened"  "\[BR\] opened \d+ stations") -and $ok
        $ok = (Show "ring closing"     "RING IS CLOSING") -and $ok
        Show "care package"            "\[BR\] care package" | Out-Null
        Show "elimination"             "\[BR\] P\d+ .*placed" | Out-Null
        Show "winner"                  "\[BR\] WINNER" | Out-Null

        # Distinct spawn stations - the guarantee, asserted. Logged by the BOTS: a coordinator is
        # shipless, so it never computes a scatter for itself.
        $scatterHits = @($BotLogs | ForEach-Object { Select-String -Path $_ -Pattern "\[BR\] scattered to station" -EA SilentlyContinue })
        Line "spawn scatter" ("{0} bots teleported to their own station" -f $scatterHits.Count)
        if ($scatterHits.Count -lt $BotDirs.Count) { $ok = $false }
        $spawns = @($BotLogs | ForEach-Object { Select-String -Path $_ -Pattern "\[BR\] spawn slot (\d+) -> station #(\d+)" -AllMatches -EA SilentlyContinue })
        $bySlot = @{}
        $disagree = 0
        foreach ($m in $spawns) {
            $slot = $m.Matches[0].Groups[1].Value; $st = $m.Matches[0].Groups[2].Value
            if ($bySlot.ContainsKey($slot)) { if ($bySlot[$slot] -ne $st) { $disagree++ } }
            else { $bySlot[$slot] = $st }
        }
        $stationIds = @($bySlot.Values)
        $dupes = @($stationIds | Group-Object | Where-Object { $_.Count -gt 1 })
        Line "distinct stations" ("{0} slots -> {1} stations, {2} shared, {3} cross-machine disagreements" -f `
            $bySlot.Count, ($stationIds | Select-Object -Unique).Count, $dupes.Count, $disagree)
        if ($dupes.Count -gt 0) { $ok = $false; Write-Host "  FAIL: two players shared a station" }
        if ($disagree -gt 0) { $ok = $false; Write-Host "  FAIL: machines disagreed on the assignment" }

        # The host opening 44 stations means nothing if the unlocks never REACH the clients. The
        # first live match had "opened 44 stations" in the coordinator log and zero broadcasts,
        # because BeginMatch ran one line before SetState(InGame) and ProgressionSync's capture
        # ignores installs outside InGame - every client sat at a locked shop. Assert BOTH halves.
        $broadcast = CountIn $CoordLog "\[Progress\] station upgrade .* broadcast"
        $applied = @($BotLogs | ForEach-Object { Select-String -Path $_ -Pattern "applied remote station upgrade" -AllMatches -EA SilentlyContinue }).Count
        Line "station unlocks" ("{0} broadcast by host, {1} applied on bots" -f $broadcast, $applied)
        if ($broadcast -lt 1 -or $applied -lt 1) { $ok = $false; Write-Host "  FAIL: station unlocks did not replicate" }

        # Spawn areas cleared of enemies. Derived identically on every machine and sent over no
        # wire, so every bot must report its own clear.
        $cleared = @($BotLogs | ForEach-Object { Select-String -Path $_ -Pattern "\[BR\] spawn clear: removed" -EA SilentlyContinue }).Count
        Line "spawn clear" ("{0}/{1} bots cleared their spawn areas" -f $cleared, $BotDirs.Count)
        if ($cleared -lt $BotDirs.Count) { $ok = $false; Write-Host "  FAIL: spawn clear did not run on every machine" }

        # A won match must not leave the winner alone in the world. Either the winner scuttled, or
        # they were already dead when the countdown finished - both satisfy the actual requirement,
        # and demanding only the first made this assertion depend on whether the ring happened to
        # finish the winner off first.
        $selfDestruct = @($BotLogs | ForEach-Object { Select-String -Path $_ -Pattern "\[BR\] winner self-destructed" -EA SilentlyContinue }).Count
        $alreadyDead = @($BotLogs | ForEach-Object { Select-String -Path $_ -Pattern "\[BR\] winner self-destruct skipped" -EA SilentlyContinue }).Count
        $armed = @($BotLogs | ForEach-Object { Select-String -Path $_ -Pattern "\[BR\] winner self-destruct armed" -EA SilentlyContinue }).Count
        if ($selfDestruct -ge 1) { Line "winner self-destruct" "fired" }
        elseif ($alreadyDead -ge 1) { Line "winner self-destruct" "not needed - the winner was already dead" }
        elseif ($armed -ge 1) {
            Line "winner self-destruct" "ARMED BUT NEVER COMPLETED - the run ended mid-countdown"
            $ok = $false
        }
        else { Line "winner self-destruct" "MISSING - never even armed"; $ok = $false }

        $stages = CountIn $CoordLog "\[BR\] announce: (THE RING IS CLOSING|FINAL RING|THE LAVA RING)"
        $drops  = CountIn $CoordLog "\[BR\] care package"
        Line "timeline" ("{0} ring announcements, {1} care packages" -f $stages, $drops)
        if ($stages -lt 2) { $ok = $false }
    }

    if (Phase "ring") {
        Write-Host "--- ring geometry + paint cost ---"
        # The ring must be sized to the PLAYABLE DISC, not the cell array. A start radius wider
        # than mapRadius * 1.2 means the corner-distance bug is back and a third of the schedule
        # is spent closing through the void border.
        $disc = @(Lines $CoordLog "playable map = disc centre \((\d+),(\d+)\) r=(\d+)")
        $start = @(Lines $CoordLog "ring start radius (\d+) .*centre offset (\d+)")
        if ($disc.Count -ge 1 -and $start.Count -ge 1) {
            $mapR = [double]$disc[0].Matches[0].Groups[3].Value
            $startR = [double]$start[0].Matches[0].Groups[1].Value
            $offset = [double]$start[0].Matches[0].Groups[2].Value
            $ratio = $startR / [Math]::Max(1.0, $mapR)
            Line "ring fit" ("map r={0:0} start r={1:0} (offset {2:0}) ratio={3:0.00}" -f $mapR, $startR, $offset, $ratio)
            if ($ratio -gt 1.2) { $ok = $false; Write-Host "  FAIL: ring starts well outside the playable map" }
        } else { Line "ring fit" "MISSING (no playable-map / start-radius line)"; $ok = $false }

        # THE LADDER. Every zone has its own wait, closure and centre now, so the assertions are
        # about the SHAPE: radii strictly shrink, the last one reaches zero, and the safe window
        # tightens from first zone to last (a constant hold is the defect this replaced).
        $zones = @(Lines $CoordLog "zone (\d+)/(\d+): wait (\d+)s, close (\d+)s, r (\d+) -> (\d+), center \((-?\d+),(-?\d+)\), drift (\d+)")
        if ($zones.Count -ge 2) {
            $radii = @(); $waits = @(); $drifts = @()
            foreach ($z in $zones) {
                $g = $z.Matches[0].Groups
                $waits += [int]$g[3].Value; $radii += [int]$g[6].Value; $drifts += [int]$g[9].Value
            }
            $shrinks = $true
            for ($i = 1; $i -lt $radii.Count; $i++) { if ($radii[$i] -ge $radii[$i-1]) { $shrinks = $false } }
            Line "ring ladder" ("{0} zones, radii {1} -> 0" -f $zones.Count, ($radii[0]))
            if (-not $shrinks) { $ok = $false; Write-Host "  FAIL: radii do not strictly decrease" }
            if ($radii[-1] -ne 0) { $ok = $false; Write-Host "  FAIL: final zone does not close to 0 (r=$($radii[-1]))" }
            # The whole point of the rewrite: the match must TIGHTEN.
            Line "pacing tightens" ("first wait {0}s -> last wait {1}s" -f $waits[0], $waits[-1])
            if ($waits[0] -le $waits[-1]) { $ok = $false; Write-Host "  FAIL: the safe window does not shrink - the ring is not tightening" }
            Line "zone drift" ("per closure: {0}" -f ($drifts -join ", "))
            if (($drifts | Measure-Object -Sum).Sum -le 0) { $ok = $false; Write-Host "  FAIL: the zone never moves - drift is dead" }
        } else { Line "ring ladder" "MISSING (no per-zone ladder logged)"; $ok = $false }

        # Containment + bounds: the two ways a drifting zone goes wrong. Both are asserted by the
        # mod itself every match; here we just insist it stayed quiet and reported.
        $bug = (CountIn $CoordLog "RING PATH BUG") + (CountIn $CoordLog "RING BOUNDS BUG")
        if ($bug -eq 0) { Line "zone containment" "every zone inside its predecessor, every centre on real ground" }
        else { $ok = $false; Line "zone containment" "$bug RING PATH/BOUNDS BUG report(s) - see the coordinator log" }

        $bounds = @(Lines $CoordLog "ring bounds: .*fully inside the playable disc from closure (\d+) onward.*final anchor sits (\d+) units")
        if ($bounds.Count -ge 1) {
            $g = $bounds[0].Matches[0].Groups
            Line "closes inside the map" ("fully inside the disc from closure {0}; anchor {1}u from map centre" -f $g[1].Value, $g[2].Value)
        } else { Line "closes inside the map" "MISSING (no ring-bounds line)"; $ok = $false }

        $anchor = @(Lines $CoordLog "final zone anchored on a shop at \((-?\d+),(-?\d+)\) openness=(\d+)")
        if ($anchor.Count -ge 1) {
            $g = $anchor[0].Matches[0].Groups
            Line "endgame anchor" ("shop at ({0},{1}), {2}% open ground" -f $g[1].Value, $g[2].Value, $g[3].Value)
        } else { Line "endgame anchor" "no central shop found - fell back to the opening centre" }

        # The zone is RENDERED, not built. It must not paint a single cell: that is the whole
        # reason it stopped costing 9-second frames, and the regression it must never make quietly.
        $paint = @(Lines $CoordLog "ring paint r=(\d+) ")
        if ($paint.Count -eq 0) {
            Line "zone is rendered" "0 terrain cells painted by the ring (correct)"
        } else {
            $ok = $false
            Line "zone is rendered" ("{0} [BR] ring paint reports - THE RING IS PAINTING TERRAIN AGAIN" -f $paint.Count)
            Write-Host "  FAIL: the zone is supposed to be a rendered surface plus a radius check."
        }

        # And it must still HURT. The damage is a radius check each client applies to its own ship,
        # so the evidence is on the bots, not the host.
        # NOTE the log line joins with an em dash; match loosely so an ASCII-only script never
        # depends on the encoding surviving a round trip through the log file.
        $burn = @($BotLogs | ForEach-Object { Select-String -Path $_ -Pattern "\[BR\] (?:ENTERED THE ZONE|in the zone) \((\d+) > (\d+)\).*?stage (\d+), x([0-9.]+) damage, ~(\d+)s from full, hp ([0-9.]+)/([0-9.]+)" -AllMatches -EA SilentlyContinue })
        # STRICT now, not advisory. The mid-match probe parks a bot outside a real circle and the
        # endgame ungod leaves both of them outside a ring closing to zero, so a run with no burn
        # report at all means the zone stopped hurting — the failure that would make the whole
        # mode cosmetic while every other assertion still passed.
        if ($burn.Count -ge 1) {
            $g = $burn[-1].Matches[0].Groups
            Line "zone damage" ("{0} burn reports; last: stage {1}, x{2} damage, ~{3}s to kill from full" -f
                $burn.Count, $g[3].Value, $g[4].Value, $g[5].Value)
        } else {
            $ok = $false
            Line "zone damage" "NO burn reported by any bot - the zone is not damaging players"
        }

        # And it must actually KILL: burning that never empties the tank is still a cosmetic zone.
        # The endgame ring closes to radius 0 with both bots ungodded, so someone has to die of it.
        $zoneKills = @($BotLogs | ForEach-Object { Select-String -Path $_ -Pattern "\[BR\] .*(eliminated|YOU PLACED)" -AllMatches -EA SilentlyContinue }).Count
        $winner = CountIn $CoordLog "\[BR\] WINNER"
        if ($winner -ge 1) { Line "ring resolves the match" "WINNER declared after the final closure" }
        else { $ok = $false; Line "ring resolves the match" "no WINNER - the closing ring never finished anyone" }

        Line "host hitches" (CountIn $CoordLog "\[Hitch\]")

        # Frame health across the whole match. The ring is the only thing that changes the world at
        # scale, so a host that starts healthy and ends unplayable indicts what the ring LEAVES
        # BEHIND, not the painting - which is measured separately above and is small.
        # Assert on the WORST sample, never first-vs-last: the host recovers the moment the ring
        # stops painting, so a run that spent four minutes at 0.2fps still ends at 118fps and a
        # start/end comparison calls it healthy.
        $frames = @(Lines $CoordLog "\[Frame\] mono=([0-9.]+)s .*avg=([0-9.]+)ms.* fps=([0-9.]+)")
        if ($frames.Count -ge 2) {
            $worstMs = 0.0; $worstAt = 0; $best = 0.0
            foreach ($f in $frames) {
                $g = $f.Matches[0].Groups
                $ms = [double]$g[2].Value
                if ($ms -gt $worstMs) { $worstMs = $ms; $worstAt = [int][double]$g[1].Value }
                if ([double]$g[3].Value -gt $best) { $best = [double]$g[3].Value }
            }
            $bad = @($frames | Where-Object { [double]$_.Matches[0].Groups[3].Value -lt 10.0 }).Count
            Line "host frame time" ("best {0}fps, WORST {1:0}ms at {2}s, {3}/{4} windows under 10fps" -f `
                $best, $worstMs, $worstAt, $bad, $frames.Count)
            if ($worstMs -gt 250.0) {
                $ok = $false
                Write-Host "  FAIL: the host degraded to an unplayable frame rate during the match."
                Write-Host "        Ring PAINT cost is reported above and is NOT it. Re-run with"
                Write-Host "        -ProfileRing and read [SimProf]: LevelChangeBuffer.Update at"
                Write-Host "        ~99% of frame time means every segment is scanning every cell"
                Write-Host "        change again (see Patches/SegmentChangeRouting.cs)."
            }
        } else { Line "host frame time" "no [Frame] samples" }

        if ($ProfileRing) {
            $prof = @(Lines $CoordLog "\[SimProf\] (\S+)\s+total=\s*([0-9.]+)ms calls=\s*(\d+) avg=\s*([0-9.]+)ms")
            if ($prof.Count -ge 1) {
                Write-Host "  simprof (top 8 by total, mid-closure):"
                foreach ($p in ($prof | Select-Object -First 8)) {
                    $g = $p.Matches[0].Groups
                    Write-Host ("    {0,-44} total={1,8}ms calls={2,7} avg={3}ms" -f $g[1].Value, $g[2].Value, $g[3].Value, $g[4].Value)
                }
            } else { Line "simprof" "no [SimProf] attribution in the log" }

            # The line that actually names a culprit.
            $fan = @(Lines $CoordLog "\[CellFanout\] (\S+)\s+total=\s*([0-9.]+)ms calls=\s*(\d+) avg=\s*([0-9.]+)ms worst=\s*([0-9.]+)ms")
            if ($fan.Count -ge 1) {
                Write-Host "  cell-change fanout (worst 10s window, by handler):"
                $byHandler = @{}
                foreach ($f in $fan) {
                    $g = $f.Matches[0].Groups
                    $name = $g[1].Value
                    $ms = [double]$g[2].Value
                    if (-not $byHandler.ContainsKey($name) -or $byHandler[$name] -lt $ms) { $byHandler[$name] = $ms }
                }
                foreach ($k in ($byHandler.Keys | Sort-Object { -$byHandler[$_] } | Select-Object -First 6)) {
                    Write-Host ("    {0,-34} {1,9:0.0}ms in a 10s window" -f $k, $byHandler[$k])
                }
            } else { Line "cell fanout" "no [CellFanout] breakdown in the log" }
        }
    }

    if ($probed -and (Phase "sync")) {
        Write-Host "--- player ship sync ---"
        # rendersmooth reports the DRAWN pose of the observed ship: CV is the shape of the streamed
        # motion, stall% is how often it froze between snapshots. Fixed-step metrics see neither -
        # which is exactly what hid the MoveTo sawtooth for weeks.
        $sm = @(Lines $BotLogs[0] "rendersmooth slot \d+: .*mean=([0-9.]+) max=([0-9.]+) u/s CV=([0-9.]+) \| stall%=([0-9.]+)")
        if ($sm.Count -ge 1) {
            $g = $sm[-1].Matches[0].Groups
            $mean = [double]$g[1].Value; $cv = [double]$g[3].Value; $stall = [double]$g[4].Value
            Line "puppet motion" ("mean={0} u/s max={1} CV={2} stall%={3}" -f $g[1].Value, $g[2].Value, $g[3].Value, $g[4].Value)
            if ($mean -lt 1.0) { Line "  note" "target barely moved - orbit did not take; CV/stall are not meaningful" }
            elseif ($cv -gt 1.5 -or $stall -gt 15.0) { $ok = $false; Write-Host "  FAIL: puppet motion is jittery (CV>1.5 or stall%>15)" }
        } else { Line "puppet motion" "MISSING (shipsmooth produced no report)"; $ok = $false }

        $lat = @(Lines $BotLogs[0] "\[ShipLatency\].*saturated=([0-9.]+)%.*underruns=(\d+) \(([0-9.]+)/s\)")
        if ($lat.Count -ge 1) {
            $g = $lat[-1].Matches[0].Groups
            Line "ship latency" ("saturated={0}% underruns={1}/s" -f $g[1].Value, $g[3].Value)
            if ([double]$g[1].Value -gt 50.0) { Write-Host "  WARN: playout buffer saturated - the sender is behind, not the netcode" }
        } else { Line "ship latency" "no [ShipLatency] samples" }
    }

    if ($probed -and (Phase "pvp")) {
        Write-Host "--- player-to-player damage ---"
        # The victim logs every routed request it applies, with the hp it moved. That single line
        # covers the whole PvP chain: the projectile actually collided (Patches/BattleRoyalePvP.cs),
        # the local hit was routed to the owner (DamageSync.SendDamageRequest), and the owner
        # applied it through the vanilla pipeline.
        $hits = @(Lines $BotLogs[1] "\[CombatHit\] remote-request=\d+ attacker=P(\d+) .*applied=True hp=([0-9.]+)->([0-9.]+)")
        Line "damage registered" ("{0} routed PvP hits applied on the victim" -f $hits.Count)
        # The gate ladder and the physical probe, echoed on every run - a bare FAIL sent this
        # investigation after three different imaginary defects. These say WHERE it died.
        $ladder = @(Lines $BotLogs[0] "\[PvPDiag\] playerProjTicks=.*")
        if ($ladder.Count -ge 1) { Line "gate ladder" $ladder[-1].Matches[0].Value }
        foreach ($v in @(Lines $BotLogs[0] "(VERDICT:.*|\*\*\* NOT IN MASK.*|ship layers=.*|castable colliders in mask:.*)")) {
            Write-Host ("        probe: " + $v.Matches[0].Value.Trim())
        }
        if ($hits.Count -lt 1) {
            $ok = $false
            Write-Host "  FAIL: not one shot landed on the other player."
            Write-Host "        Read the probe lines above BEFORE changing any code: they say whether"
            Write-Host "        the target was physically reachable. 'something is in the way' is a rig"
            Write-Host "        failure, not a PvP failure, and no netcode change will fix it."
        } else {
            $first = [double]$hits[0].Matches[0].Groups[2].Value
            $last  = [double]$hits[-1].Matches[0].Groups[3].Value
            Line "victim hp" ("{0:0.#} -> {1:0.#} over the burst" -f $first, $last)
            if ($last -ge $first) { $ok = $false; Write-Host "  FAIL: hits applied but hp never moved" }
        }
    }

    if ($DropScreen) {
        Write-Host "--- drop screen: deploy + settle ---"
        foreach ($i in 0..($BotDirs.Count-1)) {
            $dep = @(Lines $BotLogs[$i] "\[BRDrop\] DEPLOYED to (\S+) at \((-?[0-9.]+),(-?[0-9.]+)\)")
            $set = @(Lines $BotLogs[$i] "\[BRDrop\] (settled|settle TIMED OUT) at \((-?[0-9.]+),(-?[0-9.]+)\)")
            if ($dep.Count -lt 1) { Line "bot$i deploy" "MISSING - never deployed"; $ok = $false; continue }
            $g = $dep[-1].Matches[0].Groups
            Line "bot$i deploy" ("{0} at ({1},{2})" -f $g[1].Value,$g[2].Value,$g[3].Value)
            if ($set.Count -lt 1) { Line "bot$i settle" "MISSING - physics released with no settle"; $ok = $false; continue }
            $sg = $set[-1].Matches[0].Groups
            Line "bot$i settle" ("{0} at ({1},{2})" -f $sg[1].Value,$sg[2].Value,$sg[3].Value)
            if ($sg[1].Value -ne "settled") {
                Write-Host "  WARN: settle timed out - no ground under that pad within 10 units."
            }
            # THE regression this exists for: the ship must not end up far BELOW the pad it chose.
            $padY = [double]$g[3].Value
            $fell = @(Lines $BotLogs[$i] "\[CombatHit\] contact=CellType Hazard .*applied=True at \((-?[0-9.]+),(-?[0-9.]+)\)")
            if ($fell.Count -ge 1) {
                $hy = [double]$fell[-1].Matches[0].Groups[2].Value
                if ($padY - $hy -gt 8.0) {
                    $ok = $false
                    Write-Host ("  FAIL: took hazard damage {0:N0} units BELOW the pad - the ship fell through" -f ($padY - $hy))
                    Write-Host "        unstreamed terrain again (Modes/BattleRoyaleSpawnSelect.TickSettle)."
                } else { Line "bot$i hazard" ("contact at y={0}, pad y={1} - within tolerance" -f $hy, $padY) }
            } else { Line "bot$i hazard" "none - clean arrival" }
        }
    }

    if ($probed -and (Phase "fire")) {
        Write-Host "--- fire vs damage shields ---"
        $burns = @(Lines $BotLogs[0] "\[Dev\] burn: BurnLevel=([0-9.]+) onFire=(\w+) hp=([0-9.-]+) shielded=(\w+)")
        if ($burns.Count -ge 4) {
            $u = $burns[1].Matches[0].Groups   # read-only sample, god OFF
            $s = $burns[3].Matches[0].Groups   # read-only sample, god ON
            Line "unshielded" ("burn={0} onFire={1} hp={2} shielded={3}" -f $u[1].Value,$u[2].Value,$u[3].Value,$u[4].Value)
            Line "shielded"   ("burn={0} onFire={1} hp={2} shielded={3}" -f $s[1].Value,$s[2].Value,$s[3].Value,$s[4].Value)
            if ([double]$s[1].Value -gt 0.0 -or $s[2].Value -eq "True") {
                $ok = $false
                Write-Host "  FAIL: a SHIELDED ship is still burning - fire still bypasses the shield,"
                Write-Host "        so spawn protection / god / shop invulnerability can be burned through."
            }
            $hpStart = [double]$burns[0].Matches[0].Groups[3].Value
            $hpUnshielded = [double]$u[3].Value
            if ($hpUnshielded -ge $hpStart) {
                Write-Host "  WARN: unshielded ship did not lose health to fire - the control half of"
                Write-Host "        this probe proved nothing (too short a window, or it healed)."
            }
        } else { Line "fire probe" ("only {0} burn sample(s) - need 4" -f $burns.Count); $ok = $false }

        $scaled = @(Lines $BotLogs[0] "\[Damage\] enemy damage scaled for this player: ([0-9.]+) -> ([0-9.]+)")
        if ($scaled.Count -ge 1) {
            $sg = $scaled[0].Matches[0].Groups
            Line "enemy dmg scale" ("{0} -> {1} (after armour)" -f $sg[1].Value, $sg[2].Value)
            if ([double]$sg[2].Value -ge [double]$sg[1].Value) { $ok = $false; Write-Host "  FAIL: enemy damage was not reduced" }
        } else { Line "enemy dmg scale" "NOT OBSERVED - no enemy hit landed on the un-godded bot" }

        Write-Host "--- BR loot tables ---"
        foreach ($i in 0..($BotDirs.Count-1)) {
            $gen = @(Lines $BotLogs[$i] "\[BRLoot\] generation: \+(\d+) container")
            if ($gen.Count -ge 1) { Line "bot$i gen crates" ("+{0} extra containers" -f $gen[-1].Matches[0].Groups[1].Value) }
            else { Line "bot$i gen crates" "MISSING - generation pass never ran"; $ok = $false }
        }
        # Generation determinism is proven by the match going live at all (hash barrier), but say it:
        if (@(Lines $CoordLog "GENERATION MISMATCH").Count -gt 0) {
            $ok = $false; Write-Host "  FAIL: GENERATION MISMATCH - the extra-crate pass diverged between machines"
        }
        $drops = @(Lines $BotLogs[0] "\[BRLoot\] (container|enemy|miniboss|boss) '([^']+)' dropped (WHITE|COLOURED) weapon '([^']+)'|\[BRLoot\].*dropped consumable '([^']+)'")
        Line "augmented drops" ("{0} observed on bot0" -f $drops.Count)
        foreach ($d in ($drops | Select-Object -First 4)) { Write-Host ("        " + $d.Matches[0].Value) }
        $pool = @(Lines $BotLogs[0] "\[BRLoot\] weapon pools: (\d+) white, (\d+) coloured")
        if ($pool.Count -ge 1) { Line "weapon pools" $pool[-1].Matches[0].Value.Replace("[BRLoot] weapon pools: ","") }
    }

    if ($probed -and (Phase "bars")) {
        Write-Host "--- health / fuel bar data ---"
        # The bars are UI and a bot runs -nographics, so what is asserted is the DATA they bind to:
        # the remote ship's tanks as read by the observer, through the same accessors
        # UI/ShipStatusBars uses. A puppet whose tanks stop being fed shows a full bar on a ship
        # that is nearly dead - which is the failure that actually happens.
        $victim = "P{0}" -f ($BotSlots[1] + 1)
        $obs = @(Lines $BotLogs[0] ("\[Bars\] {0} hp=([0-9.]+)/([0-9.]+) fuel=([0-9.]+)/([0-9.]+)" -f $victim))
        if ($obs.Count -ge 2) {
            $b = $obs[0].Matches[0].Groups; $a = $obs[-1].Matches[0].Groups
            Line "observed remote" ("hp {0}/{1} -> {2}/{3}, fuel {4} -> {5}" -f `
                $b[1].Value, $b[2].Value, $a[1].Value, $a[2].Value, $b[3].Value, $a[3].Value)
            if ([double]$b[2].Value -le 0) { $ok = $false; Write-Host "  FAIL: remote health capacity is zero - the bar would bind an empty tank" }
            if ([double]$a[1].Value -ge [double]$b[1].Value) {
                $ok = $false
                Write-Host "  FAIL: the observer's copy of the remote ship's health never moved while"
                Write-Host "        it was being shot - the bars would show a full, healthy ship."
            }
        } else { Line "observed remote" ("only {0} sample(s) - need 2" -f $obs.Count); $ok = $false }

        # Owner-side truth, to tell "the bar is stale" apart from "the ship never took damage".
        $own = @(Lines $BotLogs[1] ("\[Bars\] {0} \(local\) hp=([0-9.]+)/([0-9.]+) fuel=([0-9.]+)/([0-9.]+)" -f $victim))
        if ($own.Count -ge 2) {
            $b = $own[0].Matches[0].Groups; $a = $own[-1].Matches[0].Groups
            Line "victim's own view" ("hp {0} -> {1}, fuel {2} -> {3}" -f $b[1].Value, $a[1].Value, $b[3].Value, $a[3].Value)
        } else { Line "victim's own view" ("only {0} sample(s)" -f $own.Count) }
    }

    if ($probed -and (Phase "loot")) {
        Write-Host "--- contested loot ---"
        # Every award is arbitrated by the host and broadcast once. Two machines claiming the same
        # (group, ordinal) must resolve to ONE slot - that is the entire contract.
        $awards = @(Lines $CoordLog "\[BRLoot\] loot #(-?\d+)\.(\d+) -> P(\d+)")
        $keys = @{}
        $conflicts = 0
        foreach ($a in $awards) {
            $k = "{0}.{1}" -f $a.Matches[0].Groups[1].Value, $a.Matches[0].Groups[2].Value
            $w = $a.Matches[0].Groups[3].Value
            if ($keys.ContainsKey($k)) { if ($keys[$k] -ne $w) { $conflicts++ } } else { $keys[$k] = $w }
        }
        Line "loot awards" ("{0} awards over {1} distinct drops, {2} awarded to two different players" -f `
            $awards.Count, $keys.Count, $conflicts)
        if ($conflicts -gt 0) { $ok = $false; Write-Host "  FAIL: the same drop went to two players" }
        if ($awards.Count -lt 1) { Line "  note" "nothing was claimed - the bots may not have collected; not a failure" }

        # And the loser really loses it: BR must never hand a distant player a private copy.
        $granted = @($BotLogs | ForEach-Object { Select-String -Path $_ -Pattern "\[Loot\] materialized" -EA SilentlyContinue }).Count
        Line "distant grants" ("{0} (must be 0 in BR - loot is contested at the site)" -f $granted)
        if ($granted -gt 0) { $ok = $false; Write-Host "  FAIL: BR granted loot remotely" }
    }

    Write-Host "--- health ---"
    $mismatch = CountIn $CoordLog "GENERATION MISMATCH"
    $errors   = CountIn $CoordLog "\[BR\].*failed"
    $lootErr  = @($BotLogs | ForEach-Object { Select-String -Path $_ -Pattern "\[BRLoot\].*could not gate" -EA SilentlyContinue }).Count
    Line "health" ("mismatches={0} br-errors={1} loot-patch-failures={2}" -f $mismatch, $errors, $lootErr)
    if ($mismatch -gt 0 -or $errors -gt 0 -or $lootErr -gt 0) { $ok = $false }
    Write-Host "====================================================="
    Write-Host $(if ($ok) { "BR SMOKE: PASS" } else { "BR SMOKE: PROBLEMS ABOVE" })
}
finally {
    foreach ($id in $pids) { Stop-Process -Id $id -Force -EA SilentlyContinue }
    Write-Host "all processes stopped"
    # Runs even on a throw or Ctrl-C: these installs are played on, not just tested on.
    RestoreCfgKeys
}
