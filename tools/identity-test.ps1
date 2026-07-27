# TWO CLIENTS, ONE SERVER: the lobby must seat them as two distinct players.
#
# Regression test for the 2026-07-26 field report ("both players entered the lobby, neither
# could see the other, both were admin"). Root cause: the non-Steam identity was a hash of the
# install PATH, which is the same string on any two machines with a default Steam library, so
# the host's one-identity-one-seat rule evicted whoever was already seated. The bot harness
# could never reproduce it -- its bots live in separate folders -- so phase 2 forces the
# collision by hand and asserts the displaced client is now TOLD, instead of silently sitting
# in a stale lobby.
#
# DEV installs only (OD Test2 coordinator, OD Dev3/Dev4 clients). ASCII only. BOM-free configs.
param([int]$WatchSeconds = 45)
$ErrorActionPreference = "Stop"
$Root      = Split-Path $PSScriptRoot -Parent
$Dll       = Join-Path $Root "bin\Release\merged\PunkMultiverse.dll"
$CoordDir  = "C:\Program Files (x86)\Steam\steamapps\common\PUNK Playtest - OD Test2"
$BotDirs   = @(
    "C:\Program Files (x86)\Steam\steamapps\common\PUNK Playtest - OD Dev3",
    "C:\Program Files (x86)\Steam\steamapps\common\PUNK Playtest - OD Dev4"
)
$CoordPlug = Join-Path $CoordDir "BepInEx\plugins\PunkMultiverse"
$CoordLog  = Join-Path $CoordDir "BepInEx\LogOutput.log"

function PlugOf($dir) { Join-Path $dir "BepInEx\plugins\PunkMultiverse" }
function LogOf($dir)  { Join-Path $dir "BepInEx\LogOutput.log" }
function CountIn($p,$pat){ if(-not(Test-Path $p)){return 0}; return @(Select-String -Path $p -Pattern $pat -AllMatches -EA SilentlyContinue).Count }
function WaitFor($p,$pat,$to,$what,$min=1){ $d=(Get-Date).AddSeconds($to); while((Get-Date)-lt $d){ if((CountIn $p $pat)-ge $min){return $true}; Start-Sleep 2 }; Write-Host "TIMEOUT $what"; return $false }
function Cmd($plug,$txt){ Add-Content -Path (Join-Path $plug "devcmd.txt") -Value $txt -Encoding Ascii }
function SetCfg([string]$path, [hashtable]$kv, [string]$section = "Session") {
    # Replace the key if present; INSERT it under the section header if not. A plain replace
    # silently no-ops for a key the installed build has never written, and the game then
    # overwrites the file with defaults.
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

$devRoots = @($CoordDir) + $BotDirs
if (Get-Process Punk -EA SilentlyContinue | Where-Object { $devRoots -contains (Split-Path $_.Path -Parent) }) {
    "ABORT: a DEV-install Punk.exe is already running."; exit 2
}
if (-not (Test-Path $Dll)) { "ABORT: build first - $Dll missing"; exit 2 }

# Deploy the build under test everywhere.
foreach ($d in $devRoots) { Copy-Item $Dll (Join-Path (PlugOf $d) "PunkMultiverse.dll") -Force }
Write-Host ("deployed " + ([Diagnostics.FileVersionInfo]::GetVersionInfo($Dll).FileVersion) + " to $($devRoots.Count) installs")

# ---------------------------------------------------------------- phase driver
# $forcedInstallId is deliberately UNTYPED: a [string] parameter coerces $null to "", which
# would blank the very value phase 2 exists to preserve.
function RunLobby([string]$phase, $forcedInstallId) {
    $pids = @()
    try {
        SetCfg (Join-Path $CoordPlug "config.cfg") @{
            "Transport"="Udp"; "UdpPort"="7789"; "CommandFile"="devcmd.txt"; "AutoLaunchRun"="false";
            "LogLevel"="Verbose"; "PreGenerateWorld"="false"; "EmptyServerResetSeconds"="600"
        }
        Remove-Item -Force -EA SilentlyContinue (Join-Path $CoordPlug "devcmd.txt"), $CoordLog
        foreach ($d in $BotDirs) {
            $kv = @{
                "Transport"="Udp"; "UdpAddress"="127.0.0.1"; "UdpPort"="7789"; "AutoStart"="Join";
                "AutoReady"="false"; "CommandFile"="devcmd.txt"; "LogLevel"="Normal"; "AutoLaunchRun"="false"
            }
            # $null = leave whatever the previous phase persisted (that is the point of phase 2).
            if ($null -ne $forcedInstallId) { $kv["InstallId"] = $forcedInstallId }
            SetCfg (Join-Path (PlugOf $d) "config.cfg") $kv
            Remove-Item -Force -EA SilentlyContinue (Join-Path (PlugOf $d) "devcmd.txt"), (LogOf $d)
        }

        $pids += StartGame $CoordDir $true
        if (-not (WaitFor $CoordLog "\[Udp\] hosting" 120 "coordinator hosting")) { throw "no host" }
        foreach ($d in $BotDirs) { $pids += StartGame $d $false; Start-Sleep 3 }
        WaitFor $CoordLog "joined" 120 "client joins" 2 | Out-Null
        Start-Sleep $WatchSeconds

        foreach ($d in $BotDirs) { Cmd (PlugOf $d) "status" }
        Start-Sleep 8

        Write-Host ""
        Write-Host "=============== $phase ==============="
        $seats = @()
        foreach ($d in $BotDirs) {
            $line = @(Select-String -Path (LogOf $d) -Pattern "status v.*state=" -EA SilentlyContinue | Select-Object -Last 1)
            $rejected = (CountIn (LogOf $d) "Rejected by host") -gt 0
            $name = Split-Path $d -Leaf
            if ($line.Count -eq 0) { Write-Host ("  {0,-14} NO STATUS (rejected={1})" -f $name, $rejected); $seats += ,@{Name=$name;Slot=$null;Admin=$null;Rejected=$rejected}; continue }
            $txt = $line[0].Line
            $slot  = ([regex]::Match($txt, "slot=(-?\d+)")).Groups[1].Value
            $admin = ([regex]::Match($txt, "admin=(\w+)")).Groups[1].Value
            $state = ([regex]::Match($txt, "state=(\w+)")).Groups[1].Value
            Write-Host ("  {0,-14} state={1} slot={2} admin={3} rejected={4}" -f $name, $state, $slot, $admin, $rejected)
            $seats += ,@{Name=$name;Slot=$slot;Admin=$admin;Rejected=$rejected}
        }
        $ids = @(Select-String -Path $CoordLog -Pattern "identity=([0-9A-F]+) joined" -AllMatches -EA SilentlyContinue |
                 ForEach-Object { $_.Matches[0].Groups[1].Value } | Select-Object -Unique)
        $evictions = CountIn $CoordLog "releasing the old seat"
        Write-Host ("  {0,-14} {1} distinct identities seen, {2} seat evictions" -f "server", $ids.Count, $evictions)
        return @{ Seats=$seats; Ids=$ids; Evictions=$evictions }
    }
    finally { foreach ($id in $pids) { Stop-Process -Id $id -Force -EA SilentlyContinue }; Start-Sleep 3 }
}

$ok = $true

# PHASE 1 - the reported scenario: two clients, blank InstallId (each generates its own).
$r1 = RunLobby "PHASE 1: two clients, generated identities" ""
$slots = @($r1.Seats | ForEach-Object { $_.Slot })
if ($r1.Ids.Count -lt 2)        { $ok=$false; Write-Host "  FAIL: clients presented the same identity" }
if ($r1.Evictions -gt 0)        { $ok=$false; Write-Host "  FAIL: a client was evicted from its seat" }
if (($slots | Select-Object -Unique).Count -lt 2) { $ok=$false; Write-Host "  FAIL: both clients hold the same slot" }
if (@($r1.Seats | Where-Object { $_.Admin -eq "True" }).Count -ne 1) { $ok=$false; Write-Host "  FAIL: expected exactly one admin" }
if (@($r1.Seats | Where-Object { $_.Rejected }).Count -ne 0)         { $ok=$false; Write-Host "  FAIL: a client was rejected" }
# The mechanism itself: a random id was generated AND persisted, so the identity no longer
# derives from the install path (which is what made two real machines collide).
$persisted = @($BotDirs | ForEach-Object {
    ([regex]::Match((Get-Content -Raw (Join-Path (PlugOf $_) "config.cfg")), "(?m)^InstallId\s*=\s*(\S+)")).Groups[1].Value })
Write-Host ("  {0,-14} {1}" -f "install ids", ($persisted -join ", "))
if (@($persisted | Where-Object { $_ }).Count -ne $BotDirs.Count) { $ok=$false; Write-Host "  FAIL: InstallId was not persisted to config" }
if (($persisted | Select-Object -Unique).Count -lt $BotDirs.Count) { $ok=$false; Write-Host "  FAIL: generated install ids collided" }

# PHASE 2 - the property a random id could plausibly have broken: identity must OUTLIVE the
# process, or every relaunch would look like a new player and a mid-run rejoin would never find
# its old seat. Same installs, no config touch - the server must see the very same identities.
$r2 = RunLobby "PHASE 2: identities survive a restart" $null
$same = @(Compare-Object $r1.Ids $r2.Ids).Count -eq 0
Write-Host ("  {0,-14} phase1={1} phase2={2}" -f "identities", ($r1.Ids -join ","), ($r2.Ids -join ","))
if (-not $same)          { $ok=$false; Write-Host "  FAIL: identities changed across a restart - rejoin would lose its seat" }
if ($r2.Evictions -gt 0) { $ok=$false; Write-Host "  FAIL: a client was evicted on the second run" }

Write-Host "====================================================="
Write-Host $(if ($ok) { "IDENTITY: PASS" } else { "IDENTITY: PROBLEMS ABOVE" })
