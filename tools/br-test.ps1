# BATTLE ROYALE HARNESS: run a whole compressed match against local bots and assert the
# lifecycle from the coordinator log. Shortened timers (6 min match, ring at 1 min, care
# package every 2 min) so a full match fits in one test run.
# DEV installs only (OD Test2 coordinator, OD Dev3/Dev4 bots). ASCII only. BOM-free configs.
param([int]$Bots = 2, [int]$WatchSeconds = 420)
$ErrorActionPreference = "Stop"
$CoordDir = "C:\Program Files (x86)\Steam\steamapps\common\PUNK Playtest - OD Test2"
$BotDirs  = @(@(
    "C:\Program Files (x86)\Steam\steamapps\common\PUNK Playtest - OD Dev3",
    "C:\Program Files (x86)\Steam\steamapps\common\PUNK Playtest - OD Dev4"
) | Select-Object -First $Bots)
$CoordPlug = Join-Path $CoordDir "BepInEx\plugins\PunkMultiverse"
$CoordLog  = Join-Path $CoordDir "BepInEx\LogOutput.log"

function CountIn($p,$pat){ if(-not(Test-Path $p)){return 0}; return @(Select-String -Path $p -Pattern $pat -AllMatches -EA SilentlyContinue).Count }
function WaitFor($p,$pat,$to,$what,$min=1){ $d=(Get-Date).AddSeconds($to); while((Get-Date)-lt $d){ if((CountIn $p $pat)-ge $min){return $true}; Start-Sleep 3 }; Write-Host "TIMEOUT $what"; return $false }
function Cmd($plug,$txt){ Add-Content -Path (Join-Path $plug "devcmd.txt") -Value $txt -Encoding Ascii }
function SetCfg([string]$path, [hashtable]$kv, [string]$section = "Session") {
    # Replace the key if present; INSERT it under the section header if not. A plain replace
    # silently no-ops for a key the installed build has never written yet, and the game then
    # overwrites the file with defaults - which is exactly how the first BR run came up Standard.
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
    foreach($k in @($psi.EnvironmentVariables.Keys | Where-Object {$_ -like "DOORSTOP*"})){ $psi.EnvironmentVariables.Remove($k) }
    return [System.Diagnostics.Process]::Start($psi).Id
}
function Show($label, $pat) {
    $hits = @(Select-String -Path $CoordLog -Pattern $pat -EA SilentlyContinue)
    if ($hits.Count -eq 0) { Write-Host ("  {0,-22} MISSING" -f $label); return $false }
    Write-Host ("  {0,-22} {1}" -f $label, (($hits[0].Line -replace '.*Punk Multiverse\] ','')))
    return $true
}

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
        "BrRingStages"="4"; "BrCarePackageMinutes"="2"; "BrMinPlayers"="1"
    }
    Remove-Item -Force -EA SilentlyContinue (Join-Path $CoordPlug "devcmd.txt"), $CoordLog
    foreach ($d in $BotDirs) {
        $plug = Join-Path $d "BepInEx\plugins\PunkMultiverse"
        SetCfg (Join-Path $plug "config.cfg") @{
            "Transport"="Udp"; "UdpAddress"="127.0.0.1"; "UdpPort"="7787"; "AutoStart"="Join";
            "AutoReady"="true"; "CommandFile"="devcmd.txt"; "LogLevel"="Normal"; "AutoLaunchRun"="false"
        }
        Remove-Item -Force -EA SilentlyContinue (Join-Path $plug "devcmd.txt"), (Join-Path $d "BepInEx\LogOutput.log")
    }

    $pids += StartGame $CoordDir $true
    if (-not (WaitFor $CoordLog "\[Udp\] hosting" 120 "coordinator hosting")) { throw "no host" }
    if (-not (WaitFor $CoordLog "\[PreGen\] world ready" 180 "pre-build")) { throw "no pre-build" }
    Write-Host "coordinator up, world pre-built"

    foreach ($d in $BotDirs) { $pids += StartGame $d $false; Start-Sleep 2 }
    if (-not (WaitFor $CoordLog "joined" 150 "bot joins" $BotDirs.Count)) { throw "bots did not all join" }
    Write-Host "$($BotDirs.Count) bots joined"
    Start-Sleep 5

    Cmd (Join-Path $BotDirs[0] "BepInEx\plugins\PunkMultiverse") "start"
    if (-not (WaitFor $CoordLog "GO LIVE" 180 "go-live")) { throw "never went live" }
    # Bots fly blind into hazards and die in seconds, which ends the match before the ring or a
    # care package ever appears. God-mode keeps them alive so the whole timeline is observable;
    # the elimination path is proven separately by ungodding one at the end.
    Start-Sleep 5
    foreach ($d in $BotDirs) { Cmd (Join-Path $d "BepInEx\plugins\PunkMultiverse") "god" }
    Write-Host "MATCH LIVE (bots godded) - watching for ${WatchSeconds}s"

    # Let the match run: ring stages, care packages, eliminations.
    $deadline = (Get-Date).AddSeconds($WatchSeconds)
    while ((Get-Date) -lt $deadline) {
        if ((CountIn $CoordLog "\[BR\] WINNER") -ge 1) { Write-Host "match resolved early"; break }
        Start-Sleep 10
    }

    # Prove the endgame too: drop god on one bot and let the ring finish it.
    Cmd (Join-Path $BotDirs[0] "BepInEx\plugins\PunkMultiverse") "god off"
    WaitFor $CoordLog "\[BR\] WINNER" 240 "winner" | Out-Null

    Write-Host ""
    Write-Host "=============== BATTLE ROYALE RESULTS ==============="
    $ok = $true
    $ok = (Show "match start"      "\[BR\] MATCH START") -and $ok
    $ok = (Show "ring center"      "\[BR\] ring center") -and $ok
    $ok = (Show "ring material"    "\[BR\] ring material") -and $ok
    $ok = (Show "stations opened"  "\[BR\] opened \d+ stations") -and $ok
    $ok = (Show "ring closing"     "RING IS CLOSING") -and $ok
    Show "care package"            "\[BR\] care package" | Out-Null
    Show "elimination"             "\[BR\] P\d+ .*placed" | Out-Null
    Show "winner"                  "\[BR\] WINNER" | Out-Null

    # Distinct spawn stations - the guarantee, asserted. Logged by the BOTS: a coordinator is
    # shipless, so it never computes a scatter for itself.
    $BotLogs = @($BotDirs | ForEach-Object { Join-Path $_ "BepInEx\LogOutput.log" } | Where-Object { Test-Path $_ })
    $scatterHits = @($BotLogs | ForEach-Object { Select-String -Path $_ -Pattern "\[BR\] scattered to station" -EA SilentlyContinue })
    Write-Host ("  {0,-22} {1} bots teleported to their own station" -f "spawn scatter", $scatterHits.Count)
    if ($scatterHits.Count -lt $BotDirs.Count) { $ok = $false }
    $spawns = @($BotLogs | ForEach-Object { Select-String -Path $_ -Pattern "\[BR\] spawn slot (\d+) -> station #(\d+)" -AllMatches })
    $bySlot = @{}
    $disagree = 0
    foreach ($m in $spawns) {
        $slot = $m.Matches[0].Groups[1].Value; $st = $m.Matches[0].Groups[2].Value
        if ($bySlot.ContainsKey($slot)) { if ($bySlot[$slot] -ne $st) { $disagree++ } }
        else { $bySlot[$slot] = $st }
    }
    $stationIds = @($bySlot.Values)
    $dupes = @($stationIds | Group-Object | Where-Object { $_.Count -gt 1 })
    Write-Host ("  {0,-22} {1} slots -> {2} stations, {3} shared, {4} cross-machine disagreements" -f `
        "distinct stations", $bySlot.Count, ($stationIds | Select-Object -Unique).Count, $dupes.Count, $disagree)
    if ($dupes.Count -gt 0) { $ok = $false; Write-Host "  FAIL: two players shared a station" }
    if ($disagree -gt 0) { $ok = $false; Write-Host "  FAIL: machines disagreed on the assignment" }

    $stages = CountIn $CoordLog "\[BR\] announce: (THE RING IS CLOSING|FINAL RING|THE LAVA RING)"
    $drops  = CountIn $CoordLog "\[BR\] care package"
    Write-Host ("  {0,-22} {1} ring announcements, {2} care packages" -f "timeline", $stages, $drops)
    if ($stages -lt 2) { $ok = $false }
    $mismatch = CountIn $CoordLog "GENERATION MISMATCH"
    $errors   = CountIn $CoordLog "\[BR\].*failed"
    Write-Host ("  {0,-22} mismatches={1} br-errors={2}" -f "health", $mismatch, $errors)
    if ($mismatch -gt 0 -or $errors -gt 0) { $ok = $false }
    Write-Host "====================================================="
    Write-Host $(if ($ok) { "BR SMOKE: PASS" } else { "BR SMOKE: PROBLEMS ABOVE" })
}
finally {
    foreach ($id in $pids) { Stop-Process -Id $id -Force -EA SilentlyContinue }
    Write-Host "all processes stopped"
}
