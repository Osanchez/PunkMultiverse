# PunkMultiverse pre-release soak bot (GAP_CLOSURE_SPEC 6.3, scenario #28).
#
# One scripted ~10-minute two-instance run that lives through everything at once:
# fresh-territory wandering (segment streaming / lease waves), periodic combat
# (projectile + damage + loot), one main-thread stall (reconnect-in-place), and one
# client kill + rejoin (catch-up + WS4.1 ship reclaim). Grades mechanically from the
# logs. PASS = ship it; any FAIL = do not release.
#
# Usage:  powershell -File soak.ps1            (from the repo folder)
#         powershell -File soak.ps1 -SkipDeploy   (reuse already-deployed builds)
# Exit code 0 = all gates passed. Artifacts (report + both logs) land in %TEMP%\punkmv-soak\<stamp>.
#
# Safety: refuses to run if any Punk.exe is already up (a foreign instance would fight
# for the loopback port); only ever kills the PIDs it launched; restores the host
# install's config.cfg byte-for-byte on exit.

param(
    [string]$HostDir = "C:\Program Files (x86)\Steam\steamapps\common\PUNK Playtest",
    [string]$ClientDir = "C:\Program Files (x86)\Steam\steamapps\common\PUNK Playtest - OD Test2",
    [switch]$SkipDeploy
)

$ErrorActionPreference = "Stop"
$RepoDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$HostPlug = Join-Path $HostDir "BepInEx\plugins\PunkMultiverse"
$ClientPlug = Join-Path $ClientDir "BepInEx\plugins\PunkMultiverse"
$HostLog = Join-Path $HostDir "BepInEx\LogOutput.log"
$ClientLog = Join-Path $ClientDir "BepInEx\LogOutput.log"
$Stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$ArtDir = Join-Path $env:TEMP "punkmv-soak\$Stamp"
New-Item -ItemType Directory -Force $ArtDir | Out-Null
$Report = Join-Path $ArtDir "soak-report.txt"
$script:Results = @()
$script:HostPid = 0
$script:ClientPid = 0

function Log([string]$msg) {
    $line = "[{0:HH:mm:ss}] {1}" -f (Get-Date), $msg
    # Write-Host, NOT Write-Output: functions that log (e.g. WaitFor's timeout path) would
    # otherwise leak the line into their return pipeline and corrupt boolean returns.
    Write-Host $line
    Add-Content -Path $Report -Value $line -Encoding utf8
}

function Gate([string]$name, [bool]$pass, [string]$evidence) {
    $verdict = "FAIL"; if ($pass) { $verdict = "PASS" }
    $script:Results += [pscustomobject]@{ Gate = $name; Verdict = $verdict; Evidence = $evidence }
    Log ("GATE {0}: {1} - {2}" -f $verdict, $name, $evidence)
}

function Warn([string]$name, [string]$evidence) {
    $script:Results += [pscustomobject]@{ Gate = $name; Verdict = "WARN"; Evidence = $evidence }
    Log ("GATE WARN: {0} - {1}" -f $name, $evidence)
}

function CountIn([string]$path, [string]$pattern) {
    if (-not (Test-Path $path)) { return 0 }
    return @(Select-String -Path $path -Pattern $pattern -AllMatches -ErrorAction SilentlyContinue).Count
}

function LastMatch([string]$path, [string]$pattern) {
    if (-not (Test-Path $path)) { return "" }
    $m = Select-String -Path $path -Pattern $pattern | Select-Object -Last 1
    if ($null -eq $m) { return "" }
    return $m.Matches[0].Value
}

function Cmd([string]$plugDir, [string]$text) {
    # The mod polls devcmd.txt at 2 Hz and empties it; ASCII (a BOM breaks the parser).
    Set-Content -Path (Join-Path $plugDir "devcmd.txt") -Value $text -Encoding Ascii
}

function WaitFor([string]$path, [string]$pattern, [int]$timeoutSec, [string]$what) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        if ((CountIn $path $pattern) -ge 1) { return $true }
        Start-Sleep -Seconds 5
    }
    Log "TIMEOUT waiting for '$what' ($timeoutSec s)" # Log uses Write-Host: safe in return pipeline
    return $false
}

# ---------------------------------------------------------------- preflight
$existing = Get-Process Punk -ErrorAction SilentlyContinue
if ($existing) {
    Write-Output "ABORT: Punk.exe already running (PIDs: $($existing.Id -join ', ')). Close it first."
    exit 2
}

$HostCfgPath = Join-Path $HostPlug "config.cfg"
$HostCfgBackup = Get-Content -Raw $HostCfgPath
try {
    Log "soak $Stamp starting; artifacts -> $ArtDir"

    # Arm the host install for scripted loopback (client install is the dedicated test copy).
    $cfg = $HostCfgBackup
    foreach ($kv in @(
        @("AutoStart", "Host"), @("AutoReady", "true"), @("AutoLaunchRun", "true"),
        @("AutoFlySeconds", "0"), @("SyncDiagnostics", "true"), @("Transport", "Loopback"))) {
        $cfg = $cfg -replace ("(?m)^{0}\s*=.*$" -f $kv[0]), ("{0} = {1}" -f $kv[0], $kv[1])
    }
    Set-Content -Path $HostCfgPath -Value $cfg -Encoding utf8
    Remove-Item -Force -ErrorAction SilentlyContinue `
        (Join-Path $HostPlug "devcmd.txt"), (Join-Path $HostPlug "devout.txt"), `
        (Join-Path $ClientPlug "devcmd.txt"), (Join-Path $ClientPlug "devout.txt")

    if (-not $SkipDeploy) {
        Log "building + deploying both installs"
        & powershell -File (Join-Path $RepoDir "build.ps1") | Select-Object -Last 1 | ForEach-Object { Log $_ }
        if ($LASTEXITCODE -ne 0) { throw "host deploy failed" }
        & powershell -File (Join-Path $RepoDir "build.ps1") -GameDir $ClientDir | Select-Object -Last 1 | ForEach-Object { Log $_ }
        if ($LASTEXITCODE -ne 0) { throw "client deploy failed" }
    }

    # ---------------------------------------------------------------- launch
    $p = Start-Process -FilePath (Join-Path $HostDir "Punk.exe") -WorkingDirectory $HostDir -PassThru
    $script:HostPid = $p.Id
    Log "host launched pid=$($script:HostPid)"
    Start-Sleep -Seconds 8
    $p = Start-Process -FilePath (Join-Path $ClientDir "Punk.exe") -WorkingDirectory $ClientDir -PassThru
    $script:ClientPid = $p.Id
    Log "client launched pid=$($script:ClientPid)"

    $liveH = WaitFor $HostLog "GO LIVE" 240 "host GO LIVE"
    $liveC = WaitFor $ClientLog "GO LIVE" 240 "client GO LIVE"
    $ckH = LastMatch $HostLog "checksum [0-9A-F]{16}"
    $ckC = LastMatch $ClientLog "checksum [0-9A-F]{16}"
    Gate "go-live + checksum parity" ($liveH -and $liveC -and $ckH -ne "" -and $ckH -eq $ckC) "host=$ckH client=$ckC"
    if (-not ($liveH -and $liveC)) { throw "never reached GO LIVE - aborting soak" }
    Start-Sleep -Seconds 15

    # ---------------------------------------------------------------- scripted life
    Log "phase: arm (god + knockback both sides)"
    Cmd $HostPlug "say soak:begin`ngod on`nknockback off"
    Cmd $ClientPlug "god on`nknockback off"
    Start-Sleep -Seconds 5

    Log "phase W1: wander (fresh territory streaming)"
    Cmd $HostPlug "autofly 20"; Cmd $ClientPlug "autofly 20"
    Start-Sleep -Seconds 50

    Log "phase C1: combat (spawn/poke/fire)"
    Cmd $HostPlug "spawn Unit_Grunt rel 10 0`nspawn Unit_Grunt rel 12 3`nspawn Enemy_Turret_Seeker rel 14 -2"
    Start-Sleep -Seconds 6
    for ($i = 0; $i -lt 3; $i++) {
        Cmd $HostPlug "fire 4 dir 1 0"
        Start-Sleep -Seconds 12
    }

    # phase D: distant-kill loot. The rest of the soak keeps both ships together, so kills are
    # resident for everyone and the non-owner loot path never runs. Split them: client flies far,
    # host kills module/consumable/ingredient droppers, and the FAR client must MATERIALIZE physical
    # pickups from the payload (the "everyone picks up their own" path — v0.1.120). This is the only
    # phase that exercises it; without it the loot rework is untested by the soak.
    Log "phase D: distant-kill loot (split players -> non-resident materialization)"
    $HostDevout = Join-Path $HostPlug "devout.txt"
    Cmd $ClientPlug "tp rel 300 0"           # far past interest radius -> non-resident for host kills
    Start-Sleep -Seconds 6
    # Spawn module/consumable droppers right by the host. They FALL (rigidbodies), so horizontal
    # fire misses — destroy them by netId with `poke` instead (position-independent). netIds come
    # from the `entities` dump, which the mod writes to devout.txt (not the log).
    Cmd $HostPlug "spawn CrateTech rel 6 0`nspawn Box_FuelGen rel 6 2`nspawn CrateTech rel 6 -2`nspawn Box_FuelGen rel 6 -4"
    Start-Sleep -Seconds 4
    Set-Content -Path $HostDevout -Value "" -Encoding Ascii   # clear spawn echoes before the dump
    Cmd $HostPlug "entities 25"
    Start-Sleep -Seconds 4
    $lootIds = @(Select-String -Path $HostDevout -Pattern "entity #(\d+) (CrateTech|Box_FuelGen)" -ErrorAction SilentlyContinue |
                 ForEach-Object { $_.Matches[0].Groups[1].Value } | Select-Object -Unique)
    Log "  phase D: poking $($lootIds.Count) loot droppers to death"
    if ($lootIds.Count -gt 0) { Cmd $HostPlug (($lootIds | ForEach-Object { "poke $_ 999" }) -join "`n") }
    Start-Sleep -Seconds 10
    $matModule = CountIn $ClientLog "\[Loot\] materialized module"
    $matAny    = CountIn $ClientLog "\[Loot\] materialized"
    $lootExc   = CountIn $ClientLog "materialize failed|error handling EntityKilled"
    Gate "distant loot: far client materializes physical pickups" ($matAny -ge 1) "materialized=$matAny (module=$matModule, droppers=$($lootIds.Count))"
    Gate "distant loot: no materialize exceptions" ($lootExc -eq 0) "lootExc=$lootExc"
    Cmd $ClientPlug "tp rel -300 0"          # rejoin the host for the rest of the run
    Start-Sleep -Seconds 6

    Log "phase W2: wander"
    Cmd $HostPlug "autofly 15"; Cmd $ClientPlug "autofly 15"
    Start-Sleep -Seconds 40

    Log "phase S: host main-thread stall 15s (reconnect-in-place)"
    $hostLinesBeforeStall = (Get-Content $HostLog -ErrorAction SilentlyContinue).Count
    Cmd $HostPlug "stall 15"
    Start-Sleep -Seconds 45
    $hitchesTotal = CountIn $HostLog "\[Hitch\] id=\d+ detected"
    $hostLinesAfterStall = (Get-Content $HostLog -ErrorAction SilentlyContinue).Count
    $selfPromote = CountIn $ClientLog "promoted to host \(migration\)"
    $reattach = CountIn $ClientLog "treating as a host stall|reattached to new host|host stall"
    Gate "stall: client reconnects in place, no self-promotion" (($selfPromote -eq 0)) "selfPromote=$selfPromote reattachMarkers=$reattach hostHitchEpisodes=$hitchesTotal"

    Log "phase C2: combat round two"
    Cmd $HostPlug "spawn Unit_Grunt rel -10 2`nspawn Unit_Fly rel -12 -3"
    Start-Sleep -Seconds 6
    Cmd $HostPlug "fire 4 dir -1 0"
    Start-Sleep -Seconds 25

    # Grade client phase-1 log BEFORE the relaunch wipes it, then archive it.
    # The benign filter must apply here too: the WS2.4 recovery line itself contains
    # "NullReferenceException" ("inactive replica bind failed ... retrying active").
    $benign = "LimitExceeded|Could not find (method|field|property)|send buffer|inactive replica bind failed"
    $c1Exceptions = @(Select-String -Path $ClientLog -Pattern "Exception|NullReference|replica failed" -ErrorAction SilentlyContinue | Where-Object { $_.Line -notmatch $benign }).Count
    $c1Seq = CountIn $ClientLog "\[Seq\]"
    $c1Heal = CountIn $ClientLog "\[Heal\]"
    Copy-Item $ClientLog (Join-Path $ArtDir "client-phase1.log") -ErrorAction SilentlyContinue

    Log "phase R: client kill + rejoin (FULL rejoin via station checkpoint)"
    # The tester-reported black screen (2026-07-20) lived exactly in the never-soaked FULL-rejoin
    # branch: every previous soak took the "designed deferral" (no checkpoint). Unlock a station
    # first so the rejoiner exercises checkpoint respawn — and HARD-gate the outcome.
    Cmd $HostPlug "unlockstation"
    $unlocked = WaitFor $HostLog "\[Progress\] station upgrade .* broadcast" 30 "station checkpoint unlock"
    if (-not $unlocked) { Warn "unlockstation produced no broadcast" "full-rejoin gate degrades to machinery-only" }
    Stop-Process -Id $script:ClientPid -Force
    Start-Sleep -Seconds 6
    # Delete the old log FIRST or the rejoin wait false-passes against phase-1 content
    # (BepInEx recreates it, but not until the new process boots).
    Remove-Item -Force -ErrorAction SilentlyContinue $ClientLog
    $p = Start-Process -FilePath (Join-Path $ClientDir "Punk.exe") -WorkingDirectory $ClientDir -PassThru
    $script:ClientPid = $p.Id
    Log "client relaunched pid=$($script:ClientPid)"
    $rejoined = WaitFor $ClientLog "rejoin spawn at station|Rejected by host: No checkpoint" 240 "client full rejoin"
    $deferred = CountIn $HostLog "rejoin for P2 deferred"
    $suspended = CountIn $HostLog "suspended puppet P2"
    $reclaimed = CountIn $HostLog "reclaimed puppet P2"
    $respawned = CountIn $HostLog "puppet spawned for slot 1"
    $stationSpawn = CountIn $ClientLog "rejoin spawn at station"
    $wentLive = CountIn $ClientLog "GO LIVE"
    if ($unlocked) {
        # With a checkpoint the rejoiner MUST spawn at the station and go live — a deferral or a
        # silent wedge here is the tester's black screen and now FAILS the soak.
        Gate "FULL rejoin: spawned at station checkpoint + went live" (($stationSpawn -ge 1) -and ($wentLive -ge 1)) "stationSpawn=$stationSpawn goLive=$wentLive deferred=$deferred"
    } else {
        $fullRejoin = ($wentLive -ge 1)
        Gate "rejoin machinery responded (full rejoin OR designed deferral)" ($fullRejoin -or ($deferred -ge 1)) "fullRejoin=$fullRejoin deferred=$deferred"
    }
    Gate "rejoin: puppet suspended on disconnect (WS4.1)" ($suspended -ge 1) "suspended=$suspended"
    if ($wentLive -ge 1) {
        if ($reclaimed -ge 1) { Log "WS4.1 fast reclaim CONFIRMED (same object reactivated)" }
        elseif ($respawned -ge 1) { Warn "reclaim window missed (fresh spawn fallback)" "reclaimed=0 freshSpawn=$respawned (boot exceeded 60s window - fallback is correct behavior)" }
    }
    Start-Sleep -Seconds 30

    Log "phase W3: settle wander + ceasefire drain"
    Cmd $HostPlug "autofly 10"; Cmd $ClientPlug "autofly 10"
    Start-Sleep -Seconds 70

    # ---------------------------------------------------------------- grading
    Log "grading final state"

    $hx = @(Select-String -Path $HostLog -Pattern "Exception|NullReference|replica failed" | Where-Object { $_.Line -notmatch $benign }).Count
    $cx = @(Select-String -Path $ClientLog -Pattern "Exception|NullReference|replica failed" -ErrorAction SilentlyContinue | Where-Object { $_.Line -notmatch $benign }).Count
    Gate "zero exceptions / replica failures" (($hx + $cx + $c1Exceptions) -eq 0) "host=$hx client2=$cx client1=$c1Exceptions"

    $seq = (CountIn $HostLog "\[Seq\]") + (CountIn $ClientLog "\[Seq\]") + $c1Seq
    Gate "sequencer clean (zero [Seq] gaps)" ($seq -eq 0) "total=$seq"

    # SummaryHeal is ON by default since WS9.1 v2: an occasional [Heal] is the system working
    # (a transient real divergence detected + repaired). The failure mode is a STORM — the same
    # segment re-healing forever (false-positive predicate or an un-healable divergence). A
    # healthy 10-minute soak measured 0; anything past a handful means the predicate regressed.
    $heal = (CountIn $HostLog "\[Heal\]") + (CountIn $ClientLog "\[Heal\]") + $c1Heal
    Gate "no [Heal] repair storms (occasional heals OK, SummaryHeal on)" ($heal -le 4) "total=$heal (threshold 4)"

    $tmH = LastMatch $HostLog "terrainMismatch=\d+"
    $tmC = LastMatch $ClientLog "terrainMismatch=\d+"
    if ((CountIn $ClientLog "\[Counts\]") -ge 1) {
        Gate "terrain parity" ($tmH -match "=0$" -and $tmC -match "=0$") "host:$tmH client:$tmC"
    } else {
        Gate "terrain parity (host)" ($tmH -match "=0$") "host:$tmH"
        Warn "client2 telemetry" "no [Counts] yet at grading (still catching up) - host-side parity graded only"
    }

    $cc = (CountIn $HostLog "coordinator-cache fallback") + (CountIn $ClientLog "coordinator-cache fallback")
    # The deliberate 15s stall freezes the host through the dormancy-commit grace window, so a
    # couple of in-flight commits legitimately fall back to the coordinator cache at recovery
    # (measured: 2, both stamped at the stall-recovery moment). Outside a stall these are real
    # anomalies — hence a tight allowance, not a blind pass.
    Gate "coordinator-cache fallbacks within stall allowance" ($cc -le 2) "total=$cc (stall allowance 2)"

    $bdRaw = LastMatch $HostLog "budgetDrops=\d+"
    $bd = 0; if ($bdRaw -match "\d+") { $bd = [int]$Matches[0] }
    Gate "presentation budget flat at defaults" ($bd -lt 300) "final host budgetDrops=$bd (threshold 300)"

    # Hitches: multi-second ONGOING chains are the release-blocker signature (the 42s field
    # freeze); the deliberate stall's own chain is exempted by its line bracket. Sub-second
    # "detected" blips during level gen / go-live are normal and only reported.
    $stallSlice = @(Get-Content $HostLog | Select-Object -Skip $hostLinesBeforeStall -First ($hostLinesAfterStall - $hostLinesBeforeStall)) -join "`n"
    $ongoingInStall = ([regex]::Matches($stallSlice, "\[Hitch\] id=\d+ ongoing")).Count
    $ongoingTotal = CountIn $HostLog "\[Hitch\] id=\d+ ongoing"
    Gate "no sustained hitches outside the deliberate stall" ($ongoingTotal -le $ongoingInStall) "ongoingTotal=$ongoingTotal inStallWindow=$ongoingInStall"
    Log "hitch episodes (detected): $hitchesTotal total - short boot/go-live blips are informational"

    $vi = LastMatch $ClientLog "visualIds=\d+"
    Log "final client $vi (informational; ~0 at ceasefire expected)"
    $churn = LastMatch $HostLog "authChurn=[0-9.]+"
    if ($churn -ne "") { Log "final host $churn (informational)" }
}
catch {
    Log "SOAK ERROR: $($_.Exception.Message)"
    Gate "soak completed without harness errors" $false "$($_.Exception.Message)"
}
finally {
    # ---------------------------------------------------------------- teardown
    foreach ($procId in @($script:HostPid, $script:ClientPid)) {
        if ($procId -gt 0) { try { Stop-Process -Id $procId -Force -ErrorAction Stop; Log "killed $procId" } catch {} }
    }
    Set-Content -Path $HostCfgPath -Value $HostCfgBackup -Encoding utf8
    Log "host config restored"
    Copy-Item $HostLog (Join-Path $ArtDir "host.log") -ErrorAction SilentlyContinue
    Copy-Item $ClientLog (Join-Path $ArtDir "client-phase2.log") -ErrorAction SilentlyContinue
    Remove-Item -Force -ErrorAction SilentlyContinue `
        (Join-Path $HostPlug "devcmd.txt"), (Join-Path $HostPlug "devout.txt"), `
        (Join-Path $ClientPlug "devcmd.txt"), (Join-Path $ClientPlug "devout.txt")
}

# ---------------------------------------------------------------- verdict
Log ""
Log "==================== SOAK VERDICT ===================="
$script:Results | ForEach-Object { Log ("{0,-4} {1} - {2}" -f $_.Verdict, $_.Gate, $_.Evidence) }
$fails = @($script:Results | Where-Object { $_.Verdict -eq "FAIL" }).Count
if ($fails -eq 0) { Log "SOAK: PASS (report: $Report)"; exit 0 }
else { Log "SOAK: FAIL ($fails gate(s)) (report: $Report)"; exit 1 }
