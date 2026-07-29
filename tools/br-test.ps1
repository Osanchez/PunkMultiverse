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
    # lifecycle | ring | sync | pvp | bars | loot | all. Comma-separated.
    [string]$Phases = "all",
    # Fire simprof at the coordinator while the ring is mid-closure and print the attribution.
    # The ring phase measures what PAINTING costs; this answers what the painted WORLD costs,
    # which turned out to be a far bigger number (2026-07-28: the host fell from 120fps to 0.2fps
    # during a match while paint itself stayed under 50ms per 10s).
    [switch]$ProfileRing
)
$ErrorActionPreference = "Stop"
$CoordDir = "C:\Program Files (x86)\Steam\steamapps\common\PUNK Playtest - OD Test2"
$BotDirs  = @(@(
    "C:\Program Files (x86)\Steam\steamapps\common\PUNK Playtest - OD Dev3",
    "C:\Program Files (x86)\Steam\steamapps\common\PUNK Playtest - OD Dev4"
) | Select-Object -First $Bots)
$CoordPlug = Join-Path $CoordDir "BepInEx\plugins\PunkMultiverse"
$CoordLog  = Join-Path $CoordDir "BepInEx\LogOutput.log"

$Want = @($Phases -split "," | ForEach-Object { $_.Trim().ToLower() })
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
    $psi.EnvironmentVariables["PUNKMV_BR_CHOOSE_SPAWN"]="0"
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
        "EnableGameModes"="true"; "GameMode"="BattleRoyale"; "BrMatchMinutes"="6"; "BrRingStartMinutes"="1";
        "BrRingStages"="4"; "BrRingCloseSeconds"="20"; "BrCarePackageMinutes"="2"; "BrMinPlayers"="1"
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
    $probed = $false
    if ($BotDirs.Count -ge 2 -and ((Phase "sync") -or (Phase "pvp") -or (Phase "bars") -or (Phase "loot"))) {
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
    $deadline = (Get-Date).AddSeconds($WatchSeconds)
    while ((Get-Date) -lt $deadline) {
        if ((CountIn $CoordLog "\[BR\] WINNER") -ge 1) { Write-Host "match resolved early"; break }
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

        $rate = @(Lines $CoordLog "each closing (\d+)u over (\d+)s = ([0-9.]+) u/s")
        if ($rate.Count -ge 1) { Line "closure rate" ("{0} u/s" -f $rate[0].Matches[0].Groups[3].Value) }
        else { Line "closure rate" "MISSING"; $ok = $false }

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
        $burn = @($BotLogs | ForEach-Object { Select-String -Path $_ -Pattern "\[BR\] in the zone \((\d+) > (\d+)\).{0,4}stage (\d+), x([0-9.]+) damage, ~(\d+)s" -AllMatches -EA SilentlyContinue })
        if ($burn.Count -ge 1) {
            $g = $burn[-1].Matches[0].Groups
            Line "zone damage" ("stage {0}, x{1} damage, ~{2}s to kill from full" -f $g[3].Value, $g[4].Value, $g[5].Value)
        } else { Line "zone damage" "no bot was ever caught outside (not a failure)" }

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
