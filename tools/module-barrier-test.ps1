# MODULE BARRIER: prove a run is REFUSED when the installed module set differs between machines.
#
# The positive case (everyone matches, the run goes live) is already covered by br-test.ps1 --
# every match it runs is one. This script covers the case that actually matters and that nothing
# else can see: two machines whose module sets DIVERGE must not be allowed to start a run.
#
# Why it matters more than "someone is missing a weapon": Modes/BattleRoyaleLootTables builds its
# drop pool from the module registry ordered by id, then picks pool[rnd.Next(pool.Count)] with a
# per-entity seed rolled independently on every machine. One extra module on one machine shifts
# every index, and BR's contested-loot identity is (Group, Ordinal) -- so the machines disagree
# about what every ordinal names. Silent, total drop-table divergence. This is the guard.
#
# The divergence is staged with the `modulefake` devcmd, which registers a module on one machine
# only -- exactly what a content mod (WeaponForge and friends) does when its weapon set differs.
# No content mod needs to be installed to run this.
#
# DEV installs only (OD Test2 coordinator, OD Dev3 bot). ASCII only. BOM-free configs.
param(
    [int]$TimeoutSeconds = 240
)
$ErrorActionPreference = "Stop"
$CoordDir = "C:\Program Files (x86)\Steam\steamapps\common\PUNK Playtest - OD Test2"
$BotDir   = "C:\Program Files (x86)\Steam\steamapps\common\PUNK Playtest - OD Dev3"
$CoordPlug = Join-Path $CoordDir "BepInEx\plugins\PunkMultiverse"
$BotPlug   = Join-Path $BotDir   "BepInEx\plugins\PunkMultiverse"
$CoordLog  = Join-Path $CoordDir "BepInEx\LogOutput.log"
$BotLog    = Join-Path $BotDir   "BepInEx\LogOutput.log"

function CountIn($p,$pat){ if(-not(Test-Path $p)){return 0}; return @(Select-String -Path $p -Pattern $pat -AllMatches -EA SilentlyContinue).Count }
function WaitFor($p,$pat,$to,$what,$min=1){ $d=(Get-Date).AddSeconds($to); while((Get-Date)-lt $d){ if((CountIn $p $pat)-ge $min){return $true}; Start-Sleep 2 }; Write-Host "TIMEOUT $what"; return $false }
function Cmd($plug,$txt){ Add-Content -Path (Join-Path $plug "devcmd.txt") -Value $txt -Encoding Ascii }
function Lines($p,$pat){ if(-not(Test-Path $p)){return @()}; return @(Select-String -Path $p -Pattern $pat -AllMatches -EA SilentlyContinue) }

# config.cfg PERSISTS and these are installs that get played on -- put every key back afterwards.
$script:CfgBackups = @()
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
        else { $cfg = [regex]::Replace($cfg, $pat, "") }
        [System.IO.File]::WriteAllText($b.Path, $cfg)
    }
    if ($script:CfgBackups.Count -gt 0) {
        Write-Host "restored $($script:CfgBackups.Count) config key(s) to their pre-test values"
        $script:CfgBackups = @()
    }
}
function SetCfg([string]$path, [hashtable]$kv, [string]$section = "Session") {
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
    $psi.EnvironmentVariables["PUNKMV_BR_CHOOSE_SPAWN"] = "0"
    foreach($k in @($psi.EnvironmentVariables.Keys | Where-Object {$_ -like "DOORSTOP*"})){ $psi.EnvironmentVariables.Remove($k) }
    return [System.Diagnostics.Process]::Start($psi).Id
}

$devRoots = @($CoordDir, $BotDir)
if (Get-Process Punk -EA SilentlyContinue | Where-Object { $devRoots -contains (Split-Path $_.Path -Parent) }) {
    "ABORT: a DEV-install Punk.exe is already running."; exit 2
}

$ok = $true
$pids = @()
try {
    SetCfg (Join-Path $CoordPlug "config.cfg") @{
        "Transport"="Udp"; "UdpPort"="7789"; "CommandFile"="devcmd.txt"; "AutoLaunchRun"="false";
        "LogLevel"="Verbose"; "PreGenerateWorld"="true"; "BrMinPlayers"="1"
    }
    SetCfg (Join-Path $BotPlug "config.cfg") @{
        "Transport"="Udp"; "UdpAddress"="127.0.0.1"; "UdpPort"="7789"; "AutoStart"="Join";
        "AutoReady"="true"; "CommandFile"="devcmd.txt"; "LogLevel"="Verbose"; "AutoLaunchRun"="false"
    }
    Remove-Item -Force -EA SilentlyContinue `
        (Join-Path $CoordPlug "devcmd.txt"), $CoordLog, (Join-Path $CoordPlug "devout.txt"), `
        (Join-Path $BotPlug   "devcmd.txt"), $BotLog,   (Join-Path $BotPlug   "devout.txt")

    $pids += StartGame $CoordDir $true
    if (-not (WaitFor $CoordLog "\[Udp\] hosting" 120 "coordinator hosting")) { throw "no host" }
    # Wait for the pre-built world like br-test does: issuing `start` mid-pre-generation is a
    # different, unrelated race and would muddy whatever this test reports.
    if (-not (WaitFor $CoordLog "\[PreGen\] world ready" 180 "pre-build")) { throw "no pre-build" }
    $pids += StartGame $BotDir $false
    if (-not (WaitFor $CoordLog "joined" 150 "bot join")) { throw "bot did not join" }
    Start-Sleep 5
    Write-Host "coordinator + bot up"

    # --- baseline: both machines agree before we touch anything -------------------------------
    Cmd $CoordPlug "moduledigest"
    Cmd $BotPlug   "moduledigest"
    Start-Sleep 4
    $cBase = @(Lines (Join-Path $CoordPlug "devout.txt") "moduledigest: modules=(\d+) digest=([0-9A-F]+)")
    $bBase = @(Lines (Join-Path $BotPlug   "devout.txt") "moduledigest: modules=(\d+) digest=([0-9A-F]+)")
    if ($cBase.Count -lt 1 -or $bBase.Count -lt 1) { throw "no baseline moduledigest from one or both machines" }
    $cd = $cBase[0].Matches[0].Groups[2].Value
    $bd = $bBase[0].Matches[0].Groups[2].Value
    Write-Host ("  baseline   coordinator={0} bot={1}" -f $cd, $bd)
    if ($cd -ne $bd) {
        Write-Host "  FAIL: the two installs disagreed BEFORE the test injected anything."
        Write-Host "        They must have identical mods for this test to mean anything."
        $ok = $false
    } else { Write-Host "  baseline   MATCH (as required)" }

    # --- stage the divergence on the bot only --------------------------------------------------
    Cmd $BotPlug "modulefake barrier.test.weapon"
    Start-Sleep 4
    $fake = @(Lines (Join-Path $BotPlug "devout.txt") "modulefake: added .* digest=([0-9A-F]+)")
    if ($fake.Count -lt 1) { throw "modulefake produced no output -- is the devcmd present in this build?" }
    $bAfter = $fake[0].Matches[0].Groups[1].Value
    Write-Host ("  divergence bot={0} (was {1})" -f $bAfter, $bd)
    if ($bAfter -eq $bd) { Write-Host "  FAIL: injecting a module did not change the digest"; $ok = $false }

    # --- the run must now be refused ------------------------------------------------------------
    Cmd $BotPlug "start"
    $refused = WaitFor $CoordLog "GENERATION MISMATCH" $TimeoutSeconds "mismatch refusal"
    if (-not $refused) {
        Write-Host "  FAIL: the run was NOT refused -- divergent module sets went live."
        $ok = $false
    }
    else {
        $line = @(Lines $CoordLog "GENERATION MISMATCH.*modules=(\w+) world=(\w+)")
        if ($line.Count -lt 1) { Write-Host "  FAIL: refusal did not report which dimension diverged"; $ok = $false }
        else {
            $mod = $line[0].Matches[0].Groups[1].Value
            $wld = $line[0].Matches[0].Groups[2].Value
            Write-Host ("  refusal    modules={0} world={1}" -f $mod, $wld)
            if ($mod -ne "True")  { Write-Host "  FAIL: refusal did not attribute the divergence to modules"; $ok = $false }
            if ($wld -ne "False") { Write-Host "  FAIL: world generation also diverged -- test is not isolating modules"; $ok = $false }
        }
        # The player-facing reason must send them after the right problem.
        if ((CountIn $BotLog "weapon/module content differs") -lt 1) {
            Write-Host "  FAIL: the client was not told this was a weapon/module content mismatch"
            $ok = $false
        } else { Write-Host "  reason     client told it is a weapon/module content mismatch" }
    }
    if ((CountIn $CoordLog "\[BR\] MATCH START") -gt 0) {
        Write-Host "  FAIL: a match started despite divergent module sets"; $ok = $false
    }
}
finally {
    foreach ($p in $pids) { Stop-Process -Id $p -Force -EA SilentlyContinue }
    Start-Sleep 2
    RestoreCfgKeys
}

Write-Host "====================================================="
Write-Host $(if ($ok) { "MODULE BARRIER: PASS" } else { "MODULE BARRIER: PROBLEMS ABOVE" })
if (-not $ok) { exit 1 }
