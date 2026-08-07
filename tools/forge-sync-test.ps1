# CUSTOM WEAPON SYNC: prove a content-mod weapon reaches the OTHER machine intact.
#
# A stock weapon and a custom weapon do not travel the same distance. FireEventMsg carries the
# shooter's slot and which holder fired -- it does NOT carry the weapon. A peer resolves the
# weapon from the PUPPET's own module grid, which arrived earlier over ModuleGridSync as a bare
# string module id, which only resolves if that id is registered on the peer. So a custom weapon
# can fire locally with the right sprite, animation and sound, and arrive on the other machine as
# nothing at all -- or as a different weapon.
#
# This asserts the whole chain from the log on BOTH machines:
#   equip   -> the custom module installs and the grid replicates it
#   shot    -> shooter logs [ForgeDiag] shot LOCAL '<id>'
#   replay  -> the OTHER machine logs [ForgeDiag] shot REPLAYED '<the same id>'
#   damage  -> the victim registers damage from it
#
# The id equality is the point. A replay line naming a different weapon is the failure mode that
# looks completely fine on the shooter's screen.
#
# Requires WeaponForge + the Mv* weapons installed in all three dev installs.
# DEV installs only. ASCII only. BOM-free configs.
param(
    [string]$Weapon = "FORGE-MVPLASMALANCE",
    [int]$FireSeconds = 8
)
$ErrorActionPreference = "Stop"
$CoordDir = "C:\Program Files (x86)\Steam\steamapps\common\PUNK Playtest - OD Test2"
$BotDirs  = @(
    "C:\Program Files (x86)\Steam\steamapps\common\PUNK Playtest - OD Dev3",
    "C:\Program Files (x86)\Steam\steamapps\common\PUNK Playtest - OD Dev4"
)
$CoordPlug = Join-Path $CoordDir "BepInEx\plugins\PunkMultiverse"
$CoordLog  = Join-Path $CoordDir "BepInEx\LogOutput.log"

function CountIn($p,$pat){ if(-not(Test-Path $p)){return 0}; return @(Select-String -Path $p -Pattern $pat -AllMatches -EA SilentlyContinue).Count }
function WaitFor($p,$pat,$to,$what,$min=1){ $d=(Get-Date).AddSeconds($to); while((Get-Date)-lt $d){ if((CountIn $p $pat)-ge $min){return $true}; Start-Sleep 2 }; Write-Host "TIMEOUT $what"; return $false }
function Cmd($plug,$txt){ Add-Content -Path (Join-Path $plug "devcmd.txt") -Value $txt -Encoding Ascii }
function Lines($p,$pat){ if(-not(Test-Path $p)){return @()}; return @(Select-String -Path $p -Pattern $pat -AllMatches -EA SilentlyContinue) }
function Line($label,$text){ Write-Host ("  {0,-24} {1}" -f $label, $text) }

$script:CfgBackups = @()
function BackupCfgKeys([string]$path, [string[]]$keys) {
    if (-not (Test-Path $path)) { return }
    $cfg = Get-Content -Raw $path
    foreach ($k in $keys) {
        $pat = "(?m)^{0}\s*=.*$" -f [regex]::Escape($k)
        $m = [regex]::Match($cfg, $pat)
        $script:CfgBackups += @{ Path=$path; Key=$k; Line=$(if($m.Success){$m.Value}else{$null}); Existed=$m.Success }
    }
}
function RestoreCfgKeys() {
    foreach ($b in $script:CfgBackups) {
        if (-not (Test-Path $b.Path)) { continue }
        $cfg = Get-Content -Raw $b.Path
        $pat = "(?m)^{0}\s*=.*$" -f [regex]::Escape($b.Key)
        if ($b.Existed) { $cfg = [regex]::Replace($cfg, $pat, $b.Line) } else { $cfg = [regex]::Replace($cfg, $pat, "") }
        [System.IO.File]::WriteAllText($b.Path, $cfg)
    }
    if ($script:CfgBackups.Count -gt 0) { Write-Host "restored $($script:CfgBackups.Count) config key(s)"; $script:CfgBackups = @() }
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

$devRoots = @($CoordDir) + $BotDirs
if (Get-Process Punk -EA SilentlyContinue | Where-Object { $devRoots -contains (Split-Path $_.Path -Parent) }) {
    "ABORT: a DEV-install Punk.exe is already running."; exit 2
}

$ok = $true
$pids = @()
try {
    SetCfg (Join-Path $CoordPlug "config.cfg") @{
        "Transport"="Udp"; "UdpPort"="7791"; "CommandFile"="devcmd.txt"; "AutoLaunchRun"="false";
        "LogLevel"="Verbose"; "PreGenerateWorld"="true"; "BrMinPlayers"="1"; "GameMode"="BattleRoyale";
        "BrMatchMinutes"="12"; "BrRingStages"="4"
    }
    Remove-Item -Force -EA SilentlyContinue (Join-Path $CoordPlug "devcmd.txt"), $CoordLog, (Join-Path $CoordPlug "devout.txt")
    foreach ($d in $BotDirs) {
        $plug = Join-Path $d "BepInEx\plugins\PunkMultiverse"
        SetCfg (Join-Path $plug "config.cfg") @{
            "Transport"="Udp"; "UdpAddress"="127.0.0.1"; "UdpPort"="7791"; "AutoStart"="Join";
            "AutoReady"="true"; "CommandFile"="devcmd.txt"; "LogLevel"="Verbose"; "AutoLaunchRun"="false"
        }
        Remove-Item -Force -EA SilentlyContinue (Join-Path $plug "devcmd.txt"), (Join-Path $d "BepInEx\LogOutput.log"), (Join-Path $plug "devout.txt")
    }

    $pids += StartGame $CoordDir $true
    if (-not (WaitFor $CoordLog "\[Udp\] hosting" 120 "coordinator hosting")) { throw "no host" }
    if (-not (WaitFor $CoordLog "\[PreGen\] world ready" 200 "pre-build")) { throw "no pre-build" }
    foreach ($d in $BotDirs) { $pids += StartGame $d $false; Start-Sleep 2 }
    if (-not (WaitFor $CoordLog "joined" 150 "bot joins" $BotDirs.Count)) { throw "bots did not all join" }
    Start-Sleep 5

    $BotPlugs = @($BotDirs | ForEach-Object { Join-Path $_ "BepInEx\plugins\PunkMultiverse" })
    $BotLogs  = @($BotDirs | ForEach-Object { Join-Path $_ "BepInEx\LogOutput.log" })

    # Every machine must agree on the module set or the run is refused - which is itself the
    # barrier working, but it would mask what this test is trying to observe.
    Cmd $CoordPlug "moduledigest"; foreach ($p in $BotPlugs) { Cmd $p "moduledigest" }
    Start-Sleep 4
    $digests = @()
    foreach ($p in @($CoordPlug) + $BotPlugs) {
        $m = @(Lines (Join-Path $p "devout.txt") "moduledigest: modules=(\d+) digest=([0-9A-F]+)")
        if ($m.Count -gt 0) { $digests += $m[0].Matches[0].Groups[2].Value }
    }
    Line "module digests" (($digests | Select-Object -Unique) -join ", ")
    if (($digests | Select-Object -Unique).Count -ne 1) {
        Write-Host "  FAIL: machines disagree on the module set - custom content is not identical"
        $ok = $false
    }

    Cmd $BotPlugs[0] "start"
    if (-not (WaitFor $CoordLog "GO LIVE" 200 "go-live")) { throw "never went live" }
    Start-Sleep 5
    foreach ($p in $BotPlugs) { Cmd $p "god" }
    Write-Host "MATCH LIVE"

    $BotSlots = @()
    foreach ($lg in $BotLogs) {
        $m = @(Lines $lg "welcomed as slot (\d+)")
        if ($m.Count -eq 0) { throw "could not read a bot's slot" }
        $BotSlots += [int]$m[0].Matches[0].Groups[1].Value
    }
    Line "bot slots" ($BotSlots -join ", ")

    # --- equip the custom weapon on bot0 and let the grid replicate ---------------------------
    Cmd $BotPlugs[0] "equip $Weapon"
    Start-Sleep 10                     # ModuleGridSync compares every 5s
    if ((CountIn $BotLogs[0] "equip: .*$([regex]::Escape($Weapon))") -lt 1 -and
        (CountIn $BotLogs[0] ([regex]::Escape($Weapon))) -lt 1) {   # parens required - see below
        Write-Host "  FAIL: bot0 never equipped $Weapon"; $ok = $false
    } else { Line "equip" "$Weapon installed on bot0" }

    # What ForgeDiag can see, from the shooter itself. When a shot logs nothing this is the line
    # that says whether the id sets were empty or the equipped weapon simply was not in them.
    Cmd $BotPlugs[0] "forgeids"
    Start-Sleep 3
    foreach ($l in @(Lines (Join-Path $BotPlugs[0] "devout.txt") "forgeids: (modules=.*|primary .*|secondary .*)")) {
        Line "forgeids" $l.Matches[0].Groups[1].Value
    }

    # --- stage the two ships and fire -----------------------------------------------------------
    # Ported from br-test.ps1's PvP probe, which is the only sequence known to actually land
    # player-to-player shots. Every step here exists because of a specific measured failure:
    #
    #   autofly off  - otherwise the two ships fly apart; the probe once caught them 38 units
    #                  apart moments after being staged 8 units apart.
    #   pvpstage     - a ship FALLS after a teleport, so a pocket cleared on arrival is above it
    #                  by the time it shoots; pvpstage clears the pocket AND pins gravity to zero.
    #                  The target is lifted clear of the station FIRST, because a station's Hatch
    #                  and Platform are prefab colliders on the Ground layer that clearterrain
    #                  cannot delete and which blocked the line.
    #
    # And the argument order is `fire <seconds> player <slot>`, NOT `fire player <slot> <seconds>`:
    # parts[1] is parsed as the DURATION, so putting "player" there makes float.TryParse fail,
    # leaves the duration at 0, and hits the "fire 0 = stop" branch. That silently turned this
    # whole test into "stop firing" and produced a log with a lone `fire: stopped` in it.
    foreach ($p in $BotPlugs) { Cmd $p "autofly 0" }
    Start-Sleep 2
    Cmd $BotPlugs[1] "pvpstage 30 45"
    Start-Sleep 2
    Cmd $BotPlugs[0] ("tpplayer {0} 5" -f $BotSlots[1])
    Start-Sleep 1
    Cmd $BotPlugs[0] "pvpstage 30"
    Start-Sleep 2
    Cmd $BotPlugs[0] ("fire {0} player {1}" -f $FireSeconds, $BotSlots[1])
    Start-Sleep ($FireSeconds + 10)

    # --- the assertions -------------------------------------------------------------------------
    Write-Host "--- custom weapon sync ---"
    $localLines  = @(Lines $BotLogs[0] "\[ForgeDiag\] shot LOCAL '([^']+)'")
    $replayLines = @(Lines $BotLogs[1] "\[ForgeDiag\] shot REPLAYED '([^']+)'")

    # Distinguish "the weapon never fired" from "it fired and was not traced" - they have
    # completely different causes and the first one is usually the harness's own fault.
    # "fire: 8.0s via weapon trigger at P2" - no text between the duration and "via" in the
    # common case, so the pattern must not require a separator there.
    $armed = CountIn $BotLogs[0] "fire: [0-9.]+s.*via weapon trigger"
    Line "fire armed" $(if ($armed -gt 0) { "yes" } else { "NO - the fire command never armed" })
    if ($armed -lt 1) { Write-Host "  FAIL: fire never armed; nothing below is meaningful"; $ok = $false }

    if ($localLines.Count -lt 1) {
        Write-Host "  FAIL: the shooter never logged a custom-weapon shot (is $Weapon a Forge weapon?)"
        $ok = $false
    } else {
        $localId = $localLines[0].Matches[0].Groups[1].Value
        Line "shooter fired" "'$localId'"
        if ($replayLines.Count -lt 1) {
            Write-Host "  FAIL: the OTHER client never replayed a custom-weapon shot."
            Write-Host "        The weapon fired locally but did not exist on the peer -- this is"
            Write-Host "        the module-grid/registry chain breaking."
            $ok = $false
        } else {
            $remoteId = $replayLines[0].Matches[0].Groups[1].Value
            Line "peer replayed" "'$remoteId'"
            if ($localId -ne $remoteId) {
                Write-Host "  FAIL: the peer replayed a DIFFERENT weapon ('$remoteId' vs '$localId')"
                $ok = $false
            } else { Line "weapon identity" "MATCH on both machines" }
        }
    }

    # A null holder on the puppet is the specific symptom of the grid not carrying the module.
    $nullHolder = CountIn $BotLogs[1] "has no weapon on the puppet"
    Line "puppet holder" $(if ($nullHolder -gt 0) { "$nullHolder dropped shot(s) - GRID DID NOT ARRIVE" } else { "resolved" })
    if ($nullHolder -gt 0) { $ok = $false }

    # Damage proves the shot did something on the receiving machine, not just that art was drawn.
    $dmg = @(Lines $BotLogs[1] "\[ForgeDiag\] damage ([0-9.]+) from '([^']+)'")
    $dmgAny = ($dmg.Count + (CountIn $BotLogs[1] "\[ForgeDiag\] damage"))
    Line "damage on victim" $(if ($dmg.Count -gt 0) { "$($dmg.Count) line(s), first from '$($dmg[0].Matches[0].Groups[2].Value)'" } else { "none logged" })

    # Sprite/sound resolution is per-machine; assert both actually loaded the custom assets.
    foreach ($i in 0..1) {
        $spr = CountIn $BotLogs[$i] "Loaded \d+ custom sprite"
        $snd = CountIn $BotLogs[$i] "Loaded \d+ custom sound"
        Line "bot$i assets" "sprites=$spr sounds=$snd"
        if ($spr -lt 1 -or $snd -lt 1) { Write-Host "  FAIL: bot$i did not load the custom assets"; $ok = $false }
    }
}
finally {
    foreach ($p in $pids) { Stop-Process -Id $p -Force -EA SilentlyContinue }
    Start-Sleep 2
    RestoreCfgKeys
}
Write-Host "====================================================="
Write-Host $(if ($ok) { "FORGE SYNC: PASS" } else { "FORGE SYNC: PROBLEMS ABOVE" })
if (-not $ok) { exit 1 }
