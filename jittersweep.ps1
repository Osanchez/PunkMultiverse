# PunkMultiverse per-enemy-type jitter sweep (the harness Omar asked for after the
# clock-dilation root cause closed the crowd-jitter mystery).
#
# For EVERY unit type in the spawnable roster: spawn 3 host-owned near the co-located
# ships, poke them awake, let them behave for a dwell window, kill them, next type.
# The client's jitterstats accumulates per-TYPE puppet smoothness the whole run; one
# final dump yields the full ranking (wastedAvg/peak/jitter%/windows). With honest
# clocks (ClockGuard) a faithful type reads jitter% ~0 and wastedAvg near its owner
# baseline - so this doubles as a regression gate: any type above the bars is a sync
# problem for THAT type's motion/weapon kind, not general infrastructure.
#
# Usage:  powershell -File jittersweep.ps1            (from the repo folder)
#         powershell -File jittersweep.ps1 -SkipDeploy -MaxTypes 10 -DwellSec 15
# Exit 0 = no type exceeded the FAIL bar. Artifacts in %TEMP%\punkmv-jittersweep\<stamp>.
#
# Gates per type (from the final table): jitter% > 10 = FAIL (visibly vibrating),
# jitter% > 2 or wastedAvg > 3.0 = WARN. Types with < 30 windows are informational
# only (spawn failed / died instantly / never entered client interest).
#
# Safety: refuses to run if any Punk.exe is already up; only kills the PIDs it
# launched; restores the host install's config.cfg byte-for-byte on exit.

param(
    [string]$HostDir = "C:\Program Files (x86)\Steam\steamapps\common\PUNK Playtest",
    [string]$ClientDir = "C:\Program Files (x86)\Steam\steamapps\common\PUNK Playtest - OD Test2",
    [switch]$SkipDeploy,
    [int]$MaxTypes = 24,
    [int]$DwellSec = 15
)

$ErrorActionPreference = "Stop"
$RepoDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$HostPlug = Join-Path $HostDir "BepInEx\plugins\PunkMultiverse"
$ClientPlug = Join-Path $ClientDir "BepInEx\plugins\PunkMultiverse"
$HostLog = Join-Path $HostDir "BepInEx\LogOutput.log"
$ClientLog = Join-Path $ClientDir "BepInEx\LogOutput.log"
$Stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$ArtDir = Join-Path $env:TEMP "punkmv-jittersweep\$Stamp"
New-Item -ItemType Directory -Force $ArtDir | Out-Null
$Report = Join-Path $ArtDir "jittersweep-report.txt"
$script:HostPid = 0
$script:ClientPid = 0

function Log([string]$msg) {
    $line = "[{0:HH:mm:ss}] {1}" -f (Get-Date), $msg
    Write-Host $line
    Add-Content -Path $Report -Value $line -Encoding utf8
}

function CountIn([string]$path, [string]$pattern) {
    if (-not (Test-Path $path)) { return 0 }
    return @(Select-String -Path $path -Pattern $pattern -AllMatches -ErrorAction SilentlyContinue).Count
}

function Cmd([string]$plugDir, [string]$text) {
    Set-Content -Path (Join-Path $plugDir "devcmd.txt") -Value $text -Encoding Ascii
}

function ReadDevout([string]$plugDir) {
    $p = Join-Path $plugDir "devout.txt"
    if (-not (Test-Path $p)) { return @() }
    $lines = Get-Content $p
    Clear-Content $p
    return $lines
}

function WaitFor([string]$path, [string]$pattern, [int]$timeoutSec, [string]$what) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        if ((CountIn $path $pattern) -ge 1) { return $true }
        Start-Sleep -Seconds 5
    }
    Log "TIMEOUT waiting for '$what' ($timeoutSec s)"
    return $false
}

function HostShipPos() {
    Clear-Content (Join-Path $HostPlug "devout.txt") -ErrorAction SilentlyContinue
    Cmd $HostPlug "status"
    Start-Sleep -Seconds 2
    $line = (ReadDevout $HostPlug) | Select-String "ship=([-0-9.]+),([-0-9.]+)" | Select-Object -Last 1
    if ($null -eq $line) { return $null }
    return @([double]$line.Matches[0].Groups[1].Value, [double]$line.Matches[0].Groups[2].Value)
}

# ---------------------------------------------------------------- preflight
$existing = Get-Process Punk -ErrorAction SilentlyContinue
if ($existing) {
    Write-Output "ABORT: Punk.exe already running (PIDs: $($existing.Id -join ', ')). Close it first."
    exit 2
}

$HostCfgPath = Join-Path $HostPlug "config.cfg"
$HostCfgBackup = Get-Content -Raw $HostCfgPath
$fails = 0
try {
    Log "jittersweep $Stamp starting; artifacts -> $ArtDir"

    $cfg = $HostCfgBackup
    foreach ($kv in @(
        @("AutoStart", "Host"), @("AutoReady", "true"), @("AutoLaunchRun", "true"),
        @("AutoFlySeconds", "0"), @("Transport", "Loopback"))) {
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
    Start-Sleep -Seconds 8
    $p = Start-Process -FilePath (Join-Path $ClientDir "Punk.exe") -WorkingDirectory $ClientDir -PassThru
    $script:ClientPid = $p.Id
    Log "host pid=$($script:HostPid) client pid=$($script:ClientPid)"

    if (-not ((WaitFor $HostLog "GO LIVE" 240 "host GO LIVE") -and (WaitFor $ClientLog "GO LIVE" 240 "client GO LIVE"))) {
        throw "never reached GO LIVE"
    }
    Start-Sleep -Seconds 15

    # ---------------------------------------------------------------- arm
    Cmd $HostPlug "say jittersweep:begin`ngod on`nknockback off"
    Cmd $ClientPlug "god on`nknockback off"
    Start-Sleep -Seconds 3

    # ---------------------------------------------------------------- roster
    Clear-Content (Join-Path $HostPlug "devout.txt") -ErrorAction SilentlyContinue
    Cmd $HostPlug "roster unit"
    Start-Sleep -Seconds 3
    $rosterLines = ReadDevout $HostPlug
    $types = @()
    foreach ($line in $rosterLines) {
        # devout line: "[mono] roster <EntityId> unit=True body=... damageable=... shooter=... loot=..."
        # Units that can also be damaged: poke aggros them and the heavy poke can kill them.
        if ($line -match "roster (\S+) unit=True .*damageable=True") { $types += $Matches[1] }
    }
    # Real enemies first (Enemy_*/Unit_*) so a capped sweep spends its budget on the roster
    # that actually fights players; crates/drones/boxes fill whatever budget remains.
    $types = $types | Select-Object -Unique
    $types = @($types | Where-Object { $_ -match "^(Enemy_|Unit_)" }) +
             @($types | Where-Object { $_ -notmatch "^(Enemy_|Unit_)" })
    $types = $types | Select-Object -First $MaxTypes
    if ($types.Count -eq 0) { throw "roster produced no unit types (format change?)" }
    Log "sweeping $($types.Count) types: $($types -join ', ')"

    # Reset the client's per-type accumulation once; everything after this is THE sweep.
    Cmd $ClientPlug "jitterstats"
    Start-Sleep -Seconds 2

    # ---------------------------------------------------------------- sweep
    foreach ($type in $types) {
        $pos = HostShipPos
        if ($null -ne $pos) { Cmd $ClientPlug ("tp {0} {1}" -f ($pos[0] + 3), $pos[1]); Start-Sleep -Seconds 2 }
        Clear-Content (Join-Path $HostPlug "devout.txt") -ErrorAction SilentlyContinue
        Cmd $HostPlug "spawn $type rel 8 0`nspawn $type rel 10 4`nspawn $type rel 10 -4"
        Start-Sleep -Seconds 3
        Cmd $HostPlug "entities 40"
        Start-Sleep -Seconds 2
        $ids = @()
        foreach ($line in (ReadDevout $HostPlug)) {
            if ($line -match "entity #(\d+) $([regex]::Escape($type)) ") { $ids += $Matches[1] }
        }
        if ($ids.Count -eq 0) { Log "  ${type}: spawn produced no visible entities (skipped)"; continue }
        $pokes = ($ids | ForEach-Object { "poke $_ 1" }) -join "`n"
        Cmd $HostPlug $pokes
        Log "  ${type}: $($ids.Count) spawned + poked, dwelling ${DwellSec}s"
        Start-Sleep -Seconds $DwellSec
        # Kill: two heavy pokes each (some types shield/phase; leftovers noted by next census).
        $kills = ($ids | ForEach-Object { "poke $_ 999" }) -join "`n"
        Cmd $HostPlug $kills
        Start-Sleep -Seconds 2
        Cmd $HostPlug $kills
        Start-Sleep -Seconds 2
    }

    # ---------------------------------------------------------------- harvest
    Clear-Content (Join-Path $ClientPlug "devout.txt") -ErrorAction SilentlyContinue
    Cmd $ClientPlug "jitterstats keep"
    Start-Sleep -Seconds 3
    $table = ReadDevout $ClientPlug
    Log ""
    Log "==================== PER-TYPE RANKING (worst first) ===================="
    foreach ($line in $table) {
        if ($line -notmatch "jitterstats (\S+): avg=([0-9.]+) peak=([0-9.]+) jitter%=([0-9.]+) windows=(\d+)") {
            Log $line
            continue
        }
        $t = $Matches[1]; $avg = [double]$Matches[2]; $peak = [double]$Matches[3]
        $jit = [double]$Matches[4]; $win = [int]$Matches[5]
        $verdict = "PASS"
        if ($win -lt 30) { $verdict = "INFO(low samples)" }
        elseif ($jit -gt 10) { $verdict = "FAIL"; $fails++ }
        elseif ($jit -gt 2 -or $avg -gt 3.0) { $verdict = "WARN" }
        Log ("{0,-18} {1,-28} avg={2,-6} peak={3,-6} jitter%={4,-5} windows={5}" -f $verdict, $t, $avg, $peak, $jit, $win)
    }
    # A lone sub-0.9x [Clock] line paired with a >1x catch-up is a load hitch (the sweep's
    # spawn/kill churn), not vsync dilation. Only sustained dilation (<0.8x) poisons numbers.
    $clock = (CountIn $HostLog "\[Clock\].*at 0\.[0-7]") + (CountIn $ClientLog "\[Clock\].*at 0\.[0-7]")
    if ($clock -gt 0) { Log "WARNING: $clock sustained [Clock] dilation warnings - this sweep's numbers are suspect"; $fails++ }
    # 'bind failed ... retrying' is the documented self-healing inactive-instantiation path
    # (Enemy_Turret_Seeker class); only unhandled exceptions / hard replica failures count.
    $exc = (CountIn $HostLog "replica failed") + (CountIn $ClientLog "replica failed") `
         + (CountIn $HostLog "Exception") + (CountIn $ClientLog "Exception") `
         - (CountIn $HostLog "bind failed.*retrying") - (CountIn $ClientLog "bind failed.*retrying")
    if ($exc -gt 0) { Log "WARNING: $exc unhandled exceptions / hard replica failures"; $fails++ }
    Log "sustained clock warnings=$clock hard failures=$exc"
}
catch {
    Log "SWEEP ERROR: $($_.Exception.Message)"
    $fails++
}
finally {
    foreach ($procId in @($script:HostPid, $script:ClientPid)) {
        if ($procId -gt 0) { Stop-Process -Id $procId -Force -ErrorAction SilentlyContinue }
    }
    Set-Content -Path $HostCfgPath -Value $HostCfgBackup -Encoding utf8
    Copy-Item $HostLog (Join-Path $ArtDir "host.log") -ErrorAction SilentlyContinue
    Copy-Item $ClientLog (Join-Path $ArtDir "client.log") -ErrorAction SilentlyContinue
    Log "teardown complete; host config restored"
}

if ($fails -eq 0) { Log "JITTERSWEEP: PASS (report: $Report)"; exit 0 }
else { Log "JITTERSWEEP: FAIL ($fails) (report: $Report)"; exit 1 }
