# WEAPON MATRIX: fire every weapon at another player and measure who actually bled.
#
# Two questions per weapon, and they are different questions:
#
#   HITS    the other player's health went DOWN. Nothing else counts -- not a log line saying a
#           shot was fired, not a projectile being visible. Omar's shard weapon looked perfect on
#           both screens and passed straight through the target.
#   SAFE    the SHOOTER'S health did not. A beam that stops on its own hull damages its owner and
#           draws nothing, and both of those read as "the weapon is broken" without saying why.
#
# Measured by HEALTH DELTA, not by parsing damage logs. Log lines differ per weapon path
# (ForgeDiag traces custom weapons, CombatHit traces routed damage, vanilla hitscan traces
# neither), so a log-shaped assertion silently covers one path and not the others -- which is
# exactly how two of the three weapon paths shipped broken. A health delta is the same measurement
# for every weapon in the game.
#
# SELF-DAMAGE IS NOT ALWAYS A BUG. A point-blank explosive should hurt its owner. So the expected
# answer is per weapon, via -SelfDamageOk, and a weapon that self-harms when it should not is a
# FAIL while one that does it by design is reported and passed.
#
# DEV installs only. ASCII only. BOM-free configs.
param(
    # Weapons to test, by module id. Default: every custom weapon the host has.
    [string[]]$Weapons,
    # Weapons where hurting the shooter is CORRECT (explosives, contact weapons).
    [string[]]$SelfDamageOk = @(),
    [int]$FireSeconds = 6,
    # Distance between the two ships when firing. Far enough that a lobbed shot has to travel.
    [int]$Range = 30,
    # Which installs to drive. Parameterised so a run can avoid installs a HUMAN is playing on --
    # this harness refuses to start when a dev install is busy, and the answer to that should be
    # "use different ones", never "close the game someone is using".
    [string]$Coordinator = "PUNK Playtest - OD Dev5",
    [string[]]$Bots = @("PUNK Playtest - OD Dev3", "PUNK Playtest - OD Dev4")
)
$ErrorActionPreference = "Stop"
$Common   = "C:\Program Files (x86)\Steam\steamapps\common"
$CoordDir = Join-Path $Common $Coordinator
$BotDirs  = @($Bots | ForEach-Object { Join-Path $Common $_ })   # [0] SHOOTER, [1] TARGET
$CoordPlug = Join-Path $CoordDir "BepInEx\plugins\PunkMultiverse"
$CoordLog  = Join-Path $CoordDir "BepInEx\LogOutput.log"

function CountIn($p,$pat){ if(-not(Test-Path $p)){return 0}; return @(Select-String -Path $p -Pattern $pat -AllMatches -EA SilentlyContinue).Count }
function Lines($p,$pat){ if(-not(Test-Path $p)){return @()}; return @(Select-String -Path $p -Pattern $pat -AllMatches -EA SilentlyContinue) }
function Cmd($plug,$txt){ Add-Content -Path (Join-Path $plug "devcmd.txt") -Value $txt -Encoding Ascii }
function Line($label,$text){ Write-Host ("  {0,-24} {1}" -f $label, $text) }
function Fail($msg){ Write-Host "  FAIL: $msg" -ForegroundColor Red; $script:ok = $false }
function WaitFor($p,$pat,$to,$what,$min=1){
    $d=(Get-Date).AddSeconds($to)
    while((Get-Date) -lt $d){ if((CountIn $p $pat) -ge $min){ return $true }; Start-Sleep 2 }
    Write-Host "  TIMEOUT $what"; return $false
}

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

# Health of a machine's OWN ship. Only the local line is authoritative -- a puppet's tank is
# whatever the last snapshot carried, which lags behind the damage being measured.
# Wait until a ship's health STOPS changing. Several weapons apply damage over time -- burn,
# electric -- and a fixed sleep after firing measures a number that is still moving. That is not a
# small inaccuracy: it credits weapon N's lingering burn to weapon N+1, so the harness reported
# the beam as safe and blamed the NEXT weapon for its self-damage.
function SettledHp($plug, [int]$maxWaitSec = 40) {
    $last = $null; $stable = 0
    $deadline = (Get-Date).AddSeconds($maxWaitSec)
    while ((Get-Date) -lt $deadline) {
        $hp = LocalHp $plug
        if ($null -eq $hp) { return $null }
        if ($null -ne $last -and [math]::Abs($hp - $last) -lt 0.001) {
            $stable++
            if ($stable -ge 2) { return $hp }      # unchanged across three consecutive reads
        } else { $stable = 0 }
        $last = $hp
        Start-Sleep 3
    }
    return $last
}

# Heal, and CONFIRM it took. A ship still burning will not reach full, and starting a weapon from
# a wound the previous weapon left makes the next number a lie.
function HealToFull($plug, [int]$maxWaitSec = 45) {
    $deadline = (Get-Date).AddSeconds($maxWaitSec)
    $out = Join-Path $plug "devout.txt"
    while ((Get-Date) -lt $deadline) {
        $seen = (CountIn $out "hpfull: P")
        Cmd $plug "hpfull"
        # Wait for a NEW line. Reading the last one matched a stale reply from the previous
        # weapon and declared a wounded ship healthy -- the beam was measured against a target
        # sitting on 1 HP.
        $w2 = (Get-Date).AddSeconds(12)
        while ((Get-Date) -lt $w2 -and (CountIn $out "hpfull: P") -le $seen) { Start-Sleep -Milliseconds 700 }
        $m = @(Lines $out "hpfull: P(\d) hp=([0-9.]+)/([0-9.]+)")
        if ($m.Count -gt 0) {
            $v = [double]$m[-1].Matches[0].Groups[2].Value
            $c = [double]$m[-1].Matches[0].Groups[3].Value
            if ([math]::Abs($v - $c) -lt 0.001) { return $true }
        }
    }
    return $false
}

function LocalHp($plug) {
    $out = Join-Path $plug "devout.txt"
    $before = (CountIn $out "hpsnap:")
    Cmd $plug "hpsnap"
    $deadline = (Get-Date).AddSeconds(12)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 700
        if ((CountIn $out "hpsnap:") -gt $before) { break }
    }
    $m = @(Lines $out "hpsnap: P(\d) hp=([0-9.]+)/([0-9.]+) local")
    if ($m.Count -lt 1) { return $null }
    return [double]$m[-1].Matches[0].Groups[2].Value
}

$devRoots = @($CoordDir) + $BotDirs

# A test against the wrong game build produces evidence about a game nobody plays.
. (Join-Path $PSScriptRoot "lib-preflight.ps1")
Assert-GameBuild -Installs $devRoots
$busy = @(Get-Process Punk -EA SilentlyContinue |
          Where-Object { $devRoots -contains (Split-Path $_.Path -Parent) } |
          ForEach-Object { Split-Path (Split-Path $_.Path -Parent) -Leaf } | Select-Object -Unique)
if ($busy.Count -gt 0) {
    "ABORT: these installs are in use: $($busy -join ', ')"
    "       Pass -Coordinator / -Bots to drive different ones, or close those clients."
    exit 2
}

$script:ok = $true
$pids = @()
$BotPlugs = @($BotDirs | ForEach-Object { Join-Path $_ "BepInEx\plugins\PunkMultiverse" })
$BotLogs  = @($BotDirs | ForEach-Object { Join-Path $_ "BepInEx\LogOutput.log" })
$results = @()
$script:deathsSeen = 0
$script:dumpedColliders = $false

try {
    # THE MACHINES MUST HOLD THE SAME MODULES OR THE RUN IS REFUSED, and that is the go-live
    # barrier working rather than something to route around. The first version of this harness set
    # ContentRoot="" and the coordinator install had no custom weapons -- so WeaponForge wrote its
    # SEVEN starter examples into the empty folder while the bots carried three, and the run
    # aborted with a module mismatch before a single shot was fired.
    #
    # So converge them the way production does: the coordinator SERVES the shooter's content, and
    # every machine swaps to that set. It also means `weaponlist forge` returns the same weapons on
    # every machine, which is what the matrix iterates.
    $served = Join-Path $CoordPlug "matrixcontent"
    if (Test-Path $served) { Remove-Item -Recurse -Force $served }
    New-Item -ItemType Directory -Force -Path $served | Out-Null
    $copied = 0
    foreach ($d in @("weapons","sprites","sounds")) {
        $src = Join-Path (Join-Path $BotDirs[0] "BepInEx\plugins") $d
        if (-not (Test-Path $src)) { continue }
        Copy-Item -Recurse -Path $src -Destination (Join-Path $served $d)
        $copied += @(Get-ChildItem -Recurse -File $src).Count
    }
    if ($copied -eq 0) { throw "the shooter install has no WeaponForge content to serve" }
    Line "serving" "$copied file(s) so every machine holds the same modules"

    # Battle Royale, because that is what makes players shootable at all. BrMinPlayers=1 so the
    # match can start with two bots and no waiting.
    SetCfg (Join-Path $CoordPlug "config.cfg") @{
        "Transport"="Udp"; "UdpPort"="7799"; "CommandFile"="devcmd.txt"; "AutoLaunchRun"="false";
        "LogLevel"="Verbose"; "PreGenerateWorld"="true"; "BrMinPlayers"="1"; "GameMode"="BattleRoyale";
        "BrMatchMinutes"="30"; "BrRingStages"="4"; "ContentRoot"="matrixcontent"
    }
    Remove-Item -Force -EA SilentlyContinue (Join-Path $CoordPlug "devcmd.txt"), $CoordLog, (Join-Path $CoordPlug "devout.txt")
    for ($i = 0; $i -lt $BotPlugs.Count; $i++) {
        SetCfg (Join-Path $BotPlugs[$i] "config.cfg") @{
            "Transport"="Udp"; "UdpAddress"="127.0.0.1"; "UdpPort"="7799"; "AutoStart"="Join";
            "AutoReady"="true"; "CommandFile"="devcmd.txt"; "LogLevel"="Verbose"; "AutoLaunchRun"="false"
        }
        Remove-Item -Force -EA SilentlyContinue (Join-Path $BotPlugs[$i] "devcmd.txt"), $BotLogs[$i], (Join-Path $BotPlugs[$i] "devout.txt")
    }

    $pids += StartGame $CoordDir $true
    if (-not (WaitFor $CoordLog "\[Udp\] hosting" 120 "coordinator hosting")) { throw "no host" }
    if (-not (WaitFor $CoordLog "\[PreGen\] world ready" 220 "pre-build")) { throw "no pre-build" }
    foreach ($d in $BotDirs) { $pids += StartGame $d $false; Start-Sleep 2 }
    if (-not (WaitFor $CoordLog "joined" 150 "bot joins" $BotDirs.Count)) { throw "bots did not all join" }

    foreach ($i in 0,1) {
        if (-not (WaitFor $BotLogs[$i] "\[Content\] set [0-9a-f]+ installed at" 300 "bot$i content install")) {
            throw "a bot never installed the served content - the module sets cannot converge"
        }
    }
    Start-Sleep 6

    Cmd $BotPlugs[0] "start"
    if (-not (WaitFor $CoordLog "GO LIVE" 220 "go-live")) {
        # Name the real reason. A module mismatch here is the BARRIER working, and reads as a
        # harness hang unless it is called out.
        if ((CountIn $CoordLog "GENERATION MISMATCH") -gt 0) {
            $mm = @(Lines $CoordLog "GENERATION MISMATCH.*")
            Write-Host "  the go-live barrier refused the run:" -ForegroundColor Yellow
            Write-Host ("  " + $mm[-1].Matches[0].Value.Substring(0, [Math]::Min(320, $mm[-1].Matches[0].Value.Length))) -ForegroundColor Yellow
        }
        throw "never went live"
    }
    Start-Sleep 8
    Write-Host "MATCH LIVE"

    $BotSlots = @()
    foreach ($lg in $BotLogs) {
        $m = @(Lines $lg "welcomed as slot (\d+)")
        if ($m.Count -eq 0) { throw "could not read a bot's slot" }
        $BotSlots += [int]$m[0].Matches[0].Groups[1].Value
    }
    Line "shooter / target" ("P{0} -> P{1}" -f ($BotSlots[0]+1), ($BotSlots[1]+1))

    # STAGE AWAY FROM STATIONS. Battle Royale scatters players to spawn stations, and a station
    # has a defensive Turret Laser. The previous staging teleported the shooter beside the target
    # -- which was sitting AT a station -- and the turret killed it during setup, before a shot was
    # fired. Every self-damage number this harness produced up to that point was measuring a
    # turret, not a weapon (`local ship died - broadcast (killed by Turret Laser)`).
    #
    # The lift argument to pvpstage was meant to cover this and does not: 45 units up is still
    # inside turret range. So both ships move to open ground first, using the station positions the
    # coordinator prints at go-live.
    $stations = @(Lines $CoordLog "\[BR\] spawn slot \d -> station #\d+ at \((-?[0-9.]+),(-?[0-9.]+)\)" |
                  ForEach-Object { [pscustomobject]@{ X=[double]$_.Matches[0].Groups[1].Value; Y=[double]$_.Matches[0].Groups[2].Value } })
    if ($stations.Count -lt 2) { throw "could not read the spawn stations - cannot stage clear of their turrets" }
    $midX = [math]::Round((($stations | Measure-Object -Property X -Average).Average), 1)
    $midY = [math]::Round((($stations | Measure-Object -Property Y -Average).Average), 1)
    $clearance = [math]::Round((($stations | ForEach-Object { [math]::Sqrt([math]::Pow($_.X-$midX,2) + [math]::Pow($_.Y-$midY,2)) } | Measure-Object -Minimum).Minimum), 0)
    Line "staging ground" ("({0},{1}) - {2}u from the nearest station" -f $midX, $midY, $clearance)
    if ($clearance -lt 150) { Fail "the staging ground is only ${clearance}u from a station; its turret may reach it" }

    # NO god mode on either ship. God blocks damage at the routing chokepoint, which would make
    # every weapon read as safe AND as harmless -- the two things being measured.
    if (-not $Weapons -or $Weapons.Count -eq 0) {
        Cmd $BotPlugs[0] "weaponlist forge"
        Start-Sleep 6
        $Weapons = @(Lines (Join-Path $BotPlugs[0] "devout.txt") "weaponlist:   \* (\S+) \|" |
                     ForEach-Object { $_.Matches[0].Groups[1].Value } | Select-Object -Unique)
        if ($Weapons.Count -eq 0) { throw "no custom weapons found - pass -Weapons explicitly" }
    }
    Line "weapons" ($Weapons -join ", ")
    Write-Host ""

    foreach ($w in $Weapons) {
        Write-Host "--- $w ---"

        # Death check FIRST. A dead shooter despawns both ships, so equip fails for a reason that
        # has nothing to do with this weapon -- which is how one failure became three.
        if ((CountIn $BotLogs[0] "local ship died") -gt $script:deathsSeen) {
            $script:deathsSeen = (CountIn $BotLogs[0] "local ship died")
            Fail "$w : the shooter died during an earlier weapon - this one is unmeasurable"
            $results += [pscustomobject]@{ Weapon=$w; Target="-"; Self="-"; Verdict="SHOOTER DEAD" }
            continue
        }

        # Both ships to full AND CONFIRMED full. A ship still burning from the previous weapon
        # cannot be healed to full, and starting from a wound makes this weapon's number a lie.
        $healed = $true
        foreach ($p in $BotPlugs) { if (-not (HealToFull $p)) { $healed = $false } }
        if (-not $healed) {
            Fail "$w : could not restore both ships to full - damage over time from the previous weapon is still running"
            $results += [pscustomobject]@{ Weapon=$w; Target="-"; Self="-"; Verdict="NOT HEALED" }
            continue
        }

        # Stage. Every step here is load-bearing and each exists because of a measured failure:
        # ships drift apart without autofly off; a teleported ship FALLS, so the pocket cleared on
        # arrival is above it by the time it shoots (pvpstage clears AND pins gravity); the target
        # is lifted clear of station colliders that clearterrain cannot delete.
        foreach ($p in $BotPlugs) { Cmd $p "autofly 0" }
        Start-Sleep 2
        # Out to open ground first, THEN close the distance. Doing it the other way round is what
        # parked both ships under a station turret.
        Cmd $BotPlugs[1] ("tp {0} {1}" -f $midX, $midY)
        Start-Sleep 2
        Cmd $BotPlugs[1] "pvpstage $Range 20"
        Start-Sleep 2
        Cmd $BotPlugs[1] "clearmobs 200"
        Start-Sleep 3
        Cmd $BotPlugs[0] ("tpplayer {0} 5" -f $BotSlots[1])
        Start-Sleep 1
        Cmd $BotPlugs[0] "pvpstage $Range"
        Start-Sleep 2
        Cmd $BotPlugs[0] "clearmobs 200"
        Start-Sleep 3

        # And confirm nothing shot them while we were arranging things.
        if ((CountIn $BotLogs[0] "local ship died") -gt $script:deathsSeen) {
            $script:deathsSeen = (CountIn $BotLogs[0] "local ship died")
            $why = @(Lines $BotLogs[0] "local ship died . broadcast \(killed by ([^)]+)\)")
            $by = if ($why.Count -gt 0) { $why[-1].Matches[0].Groups[1].Value } else { "unknown" }
            Fail "$w : the shooter died DURING STAGING (killed by $by) - not a weapon result"
            $results += [pscustomobject]@{ Weapon=$w; Target="-"; Self="-"; Verdict="DIED STAGING ($by)" }
            continue
        }

        if (-not $script:dumpedColliders) {
            $script:dumpedColliders = $true
            Cmd $BotPlugs[0] "shipcolliders"
            Start-Sleep 5
            foreach ($l in (Lines (Join-Path $BotPlugs[0] "devout.txt") "shipcolliders:.*")) {
                Write-Host ("        " + $l.Matches[0].Value)
            }
        }

        Cmd $BotPlugs[0] "equip $w"
        Start-Sleep 6
        if ((CountIn $BotLogs[0] ("equip: .*" + [regex]::Escape($w))) -lt 1) {
            Fail "$w : could not be equipped"
            $results += [pscustomobject]@{ Weapon=$w; Target="-"; Self="-"; Verdict="NO EQUIP" }
            continue
        }

        if ((CountIn $BotLogs[0] "local ship died") -gt $script:deathsSeen) {
            $script:deathsSeen = (CountIn $BotLogs[0] "local ship died")
            Fail "$w : the shooter is DEAD before firing - every later weapon in this run is unmeasurable"
            $results += [pscustomobject]@{ Weapon=$w; Target="-"; Self="FATAL"; Verdict="DIED IN SETUP" }
            continue
        }

        $shooterBefore = LocalHp $BotPlugs[0]
        $targetBefore  = LocalHp $BotPlugs[1]
        if ($null -eq $shooterBefore -or $null -eq $targetBefore) {
            Fail "$w : could not read health before firing"
            $results += [pscustomobject]@{ Weapon=$w; Target="-"; Self="-"; Verdict="NO HP" }
            continue
        }

        # `fire <seconds> player <slot>` -- parts[1] is parsed as the DURATION, so the other
        # argument order silently becomes "fire 0", i.e. stop.
        Cmd $BotPlugs[0] ("fire {0} player {1}" -f $FireSeconds, $BotSlots[1])
        Start-Sleep ($FireSeconds + 4)

        # SETTLE, do not just sleep. Burn keeps ticking after the trigger releases; reading here
        # without waiting for the number to stop moving is what produced the false "safe".
        $shooterAfter = SettledHp $BotPlugs[0]
        $targetAfter  = SettledHp $BotPlugs[1]
        if ($null -eq $shooterAfter -or $null -eq $targetAfter) {
            Fail "$w : could not read health after firing"
            $results += [pscustomobject]@{ Weapon=$w; Target="-"; Self="-"; Verdict="NO HP" }
            continue
        }

        $dealt = [math]::Round($targetBefore - $targetAfter, 3)
        $self  = [math]::Round($shooterBefore - $shooterAfter, 3)
        $armed = CountIn $BotLogs[0] "fire: [0-9.]+s.*via weapon trigger"

        Line "target hp" ("{0} -> {1}  (dealt {2})" -f $targetBefore, $targetAfter, $dealt)
        Line "shooter hp" ("{0} -> {1}  (self {2})" -f $shooterBefore, $shooterAfter, $self)

        $verdict = @()
        if ($dealt -le 0) {
            # Distinguish "the weapon does nothing" from "the harness never fired", which have
            # completely different causes and only one of them is a product bug.
            if ($armed -lt 1) { Fail "$w : the fire command never armed - harness problem, not a weapon result" ; $verdict += "NOT FIRED" }
            else { Fail "$w : dealt NO damage to the other player" ; $verdict += "NO DAMAGE" }
        } else { $verdict += "hits" }

        if ($self -gt 0) {
            if ($SelfDamageOk -contains $w) { $verdict += "self-damage (expected)" }
            else { Fail "$w : damaged its OWN shooter ($self)" ; $verdict += "SELF-DAMAGE" }
        } else { $verdict += "safe" }

        $results += [pscustomobject]@{ Weapon=$w; Target=$dealt; Self=$self; Verdict=($verdict -join " / ") }
        Write-Host ""
    }
}
finally {
    foreach ($p in $pids) { Stop-Process -Id $p -Force -EA SilentlyContinue }
    Start-Sleep 3
    RestoreCfgKeys
}

Write-Host "====================================================="
if ($results.Count -gt 0) {
    $results | Format-Table -AutoSize | Out-String | Write-Host
}
Write-Host $(if ($script:ok) { "WEAPON MATRIX: PASS" } else { "WEAPON MATRIX: PROBLEMS ABOVE" })
if (-not $script:ok) { exit 1 }
