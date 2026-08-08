# BEAM VISUAL: is the beam actually DRAWN on the other player's screen?
#
# This is the one claim no assertion in this repo can make. The weapon matrix measures health, so
# a beam that damages correctly and renders as nothing at all passes it perfectly -- which is
# exactly the state Omar reported: "the ship gives an animation like its shooting, but there is no
# line". Health was never going to see that.
#
# So: two WINDOWED clients (a renderer is the whole point), the shooter holds a beam on the target
# with infinite resource, and BOTH machines photograph themselves while it is held. The shooter's
# frame proves the beam exists at all; the OBSERVER'S frame is the actual question, because a beam
# that draws locally and not remotely is a replication bug and looks identical to a working weapon
# from the shooter's seat.
#
# Capture is the game's own ScreenCapture (the `screenshot` devcmd), never a screen grab: a screen
# region grab photographs whatever else is on the desktop, and it once captured a video call.
#
# DEV installs only. ASCII only. BOM-free configs.
#
# CmdletBinding so an unknown switch is an ERROR. Without it PowerShell drops unbound arguments
# into $args silently -- `-Solo` was accepted, ignored, and the two-client path ran instead,
# producing a "control" that was nothing of the kind and would have been read as a result.
[CmdletBinding()]
param(
    [string]$Weapon = "FORGE-MVARCBEAM",
    [int]$HoldSeconds = 40,
    [int]$ShotCount = 5,
    [int]$Range = 14,
    [string]$Coordinator = "PUNK Playtest - OD Dev5",
    [string]$Shooter = "PUNK Playtest - OD Dev3",
    [string]$Observer = "PUNK Playtest - OD Dev4",
    [string]$OutDir = "$env:TEMP\beamshots",
    # CONTROL: one windowed client, Standard mode, friendly fire off -- the state in which this
    # mod's PvP patches are inert. If the beam draws HERE but not in a session, the missing visual
    # is OURS: the cast-origin push is the suspect, since a beam's visual is drawn from where its
    # cast starts. If it draws in neither, the bug predates this session and belongs to WeaponForge.
    [switch]$Solo,
    # Serve NOTHING, so no client swaps its WeaponForge content. Only valid when every install
    # already holds an identical set -- otherwise the module digests differ and go-live is refused.
    # This isolates the CONTENT SWAP from everything else: the swap tears down WeaponForge's
    # sprite library (_sprites/_anims/_sheets and the _loaded latch) and rebuilds it, and a beam
    # whose visual asset did not come back would draw no line while still dealing damage.
    [switch]$NoServe,
    # Which mode the SOLO control runs in. Standard leaves the PvP patches inert; BattleRoyale
    # turns them on while keeping the player a HOST and alone. Running both splits two variables
    # that were otherwise conflated -- "client vs host" and "Standard vs BR" -- and only one of
    # them can be the reason the beam draws solo and not in a session.
    [string]$SoloGameMode = "Standard"
)
$ErrorActionPreference = "Stop"
$Common = "C:\Program Files (x86)\Steam\steamapps\common"
$CoordDir = Join-Path $Common $Coordinator
$ShootDir = Join-Path $Common $Shooter
$ObsDir   = Join-Path $Common $Observer
$CoordPlug = Join-Path $CoordDir "BepInEx\plugins\PunkMultiverse"
$CoordLog  = Join-Path $CoordDir "BepInEx\LogOutput.log"
$ShootPlug = Join-Path $ShootDir "BepInEx\plugins\PunkMultiverse"
$ObsPlug   = Join-Path $ObsDir   "BepInEx\plugins\PunkMultiverse"
$ShootLog  = Join-Path $ShootDir "BepInEx\LogOutput.log"
$ObsLog    = Join-Path $ObsDir   "BepInEx\LogOutput.log"

function CountIn($p,$pat){ if(-not(Test-Path $p)){return 0}; return @(Select-String -Path $p -Pattern $pat -AllMatches -EA SilentlyContinue).Count }
function Lines($p,$pat){ if(-not(Test-Path $p)){return @()}; return @(Select-String -Path $p -Pattern $pat -AllMatches -EA SilentlyContinue) }
function Cmd($plug,$txt){ Add-Content -Path (Join-Path $plug "devcmd.txt") -Value $txt -Encoding Ascii }
function Line($label,$text){ Write-Host ("  {0,-22} {1}" -f $label, $text) }
function WaitFor($p,$pat,$to,$what,$min=1){
    $d=(Get-Date).AddSeconds($to)
    while((Get-Date) -lt $d){ if((CountIn $p $pat) -ge $min){ return $true }; Start-Sleep 2 }
    Write-Host "  TIMEOUT $what"; return $false
}

$script:CfgBackups = @()
function SetCfg([string]$path, [hashtable]$kv, [string]$section = "Session") {
    $cfg = Get-Content -Raw $path
    foreach ($k in $kv.Keys) {
        $pat = "(?m)^{0}\s*=.*$" -f [regex]::Escape($k)
        $m = [regex]::Match($cfg, $pat)
        $script:CfgBackups += @{ Path=$path; Key=$k; Line=$(if($m.Success){$m.Value}else{$null}); Existed=$m.Success }
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
function RestoreCfg() {
    foreach ($b in $script:CfgBackups) {
        if (-not (Test-Path $b.Path)) { continue }
        $cfg = Get-Content -Raw $b.Path
        $pat = "(?m)^{0}\s*=.*$" -f [regex]::Escape($b.Key)
        if ($b.Existed) { $cfg = [regex]::Replace($cfg, $pat, $b.Line) } else { $cfg = [regex]::Replace($cfg, $pat, "") }
        [System.IO.File]::WriteAllText($b.Path, $cfg)
    }
    if ($script:CfgBackups.Count -gt 0) { Write-Host "restored $($script:CfgBackups.Count) config key(s)"; $script:CfgBackups = @() }
}

function Launch($dir, $coord, $windowed) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = Join-Path $dir "Punk.exe"
    # WINDOWED for the two clients: a headless instance renders nothing, which is the entire
    # reason every previous test was blind to this.
    $psi.Arguments = if ($windowed) { "-screen-fullscreen 0 -screen-width 1280 -screen-height 720" } else { "-batchmode -nographics" }
    $psi.WorkingDirectory = $dir; $psi.UseShellExecute = $false
    if ($coord) { $psi.EnvironmentVariables["PUNKMV_COORDINATOR"]="1"; $psi.EnvironmentVariables["PUNKMV_TRANSPORT"]="Udp" }
    $psi.EnvironmentVariables["PUNKMV_BR_CHOOSE_SPAWN"] = "0"
    foreach($k in @($psi.EnvironmentVariables.Keys | Where-Object {$_ -like "DOORSTOP*"})){ $psi.EnvironmentVariables.Remove($k) }
    return [System.Diagnostics.Process]::Start($psi)
}

# Ask the GAME to photograph itself and wait for the file. ScreenCapture writes at end of frame.
function Shoot($plug, $name, $destDir, $tag) {
    Cmd $plug "screenshot $name"
    $src = Join-Path $plug "screenshots\$name.png"
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline -and -not (Test-Path $src)) { Start-Sleep -Milliseconds 500 }
    if (-not (Test-Path $src)) { Write-Host "    skip: $tag/$name never appeared"; return $null }
    Start-Sleep 1
    $dst = Join-Path $destDir "$tag-$name.png"
    Copy-Item $src $dst -Force
    return $dst
}

$devRoots = @($CoordDir, $ShootDir, $ObsDir)
. (Join-Path $PSScriptRoot "lib-preflight.ps1")
Assert-GameBuild -Installs $devRoots
$busy = @(Get-Process Punk -EA SilentlyContinue |
          Where-Object { $devRoots -contains (Split-Path $_.Path -Parent) } |
          ForEach-Object { Split-Path (Split-Path $_.Path -Parent) -Leaf } | Select-Object -Unique)
if ($busy.Count -gt 0) { "ABORT: these installs are in use: $($busy -join ', ')"; exit 2 }

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
Get-ChildItem $OutDir -Filter *.png -EA SilentlyContinue | Remove-Item -Force
$procs = @()
$frames = @()
try {
    # The shooter serves its own content so all three machines agree on the module set.
    $served = Join-Path $CoordPlug "beamcontent"
    if (Test-Path $served) { Remove-Item -Recurse -Force $served }
    New-Item -ItemType Directory -Force -Path $served | Out-Null
    $copied = 0
    if (-not $NoServe) {
        foreach ($d in @("weapons","sprites","sounds")) {
            $src = Join-Path (Join-Path $ShootDir "BepInEx\plugins") $d
            if (-not (Test-Path $src)) { continue }
            Copy-Item -Recurse -Path $src -Destination (Join-Path $served $d)
            $copied += @(Get-ChildItem -Recurse -File $src).Count
        }
        if ($copied -eq 0) { throw "the shooter has no WeaponForge content to serve" }
        Line "serving" "$copied file(s)"
    } else {
        Line "serving" "NOTHING - no content swap will happen (installs must already match)"
    }

    if ($Solo) {
        # One machine hosting itself, Standard, friendly fire off. No coordinator, no second
        # client, no PvP patches.
        SetCfg (Join-Path $ShootPlug "config.cfg") @{
            "Transport"="Udp"; "UdpPort"="7805"; "CommandFile"="devcmd.txt"; "AutoStart"="Host";
            "AutoReady"="true"; "AutoLaunchRun"="true"; "LogLevel"="Info";
            "GameMode"=$SoloGameMode; "FriendlyFire"="false"; "ContentRoot"=""
            "BrMinPlayers"="1"; "BrMatchMinutes"="30"; "BrRingStages"="4"
        }
        Remove-Item -Force -EA SilentlyContinue $ShootLog, (Join-Path $ShootPlug "devcmd.txt")
        Line "mode" "SOLO ($SoloGameMode, friendly fire off, hosting alone)"
        $procs += Launch $ShootDir $false $true
        if (-not (WaitFor $ShootLog "GO LIVE" 300 "run start")) { throw "the solo run never started" }
        Start-Sleep 12

        # Assert the control really is one, rather than trusting the config.
        $active = (CountIn $ShootLog "\[BR\] hitscan weapon widened") + (CountIn $ShootLog "\[BR\] hitscan origin pushed")
        if ($SoloGameMode -eq "Standard" -and $active -gt 0) {
            throw "PvP patches were ACTIVE in a Standard control - it proves nothing about attribution"
        }
        Line "pvp patches" $(if ($active -gt 0) { "ACTIVE ($active diagnostic line(s)) - expected in BattleRoyale" } else { "inert" })

        Cmd $ShootPlug "god"; Start-Sleep 2
        Cmd $ShootPlug "tpshop"; Start-Sleep 3
        Cmd $ShootPlug "clearmobs 250"; Start-Sleep 3
        Cmd $ShootPlug "autofly 0"; Start-Sleep 2
        Cmd $ShootPlug "equip $Weapon"; Start-Sleep 6
        if ((CountIn $ShootLog ("equip: .*" + [regex]::Escape($Weapon))) -lt 1) { throw "could not equip $Weapon" }

        $frames += Shoot $ShootPlug "before" $OutDir "solo"
        Cmd $ShootPlug ("fire {0}" -f $HoldSeconds)
        Line "firing" "$HoldSeconds s into open space"
        Start-Sleep 4
        for ($i = 1; $i -le $ShotCount; $i++) {
            $frames += Shoot $ShootPlug "hold$i" $OutDir "solo"
            Start-Sleep 4
        }
        $localShots = CountIn $ShootLog "\[ForgeDiag\] shot LOCAL"
        Line "forge trace" "solo LOCAL=$localShots"
        if ($localShots -lt 1) { throw "the beam never fired in the control - the frames show nothing" }
        return
    }

    SetCfg (Join-Path $CoordPlug "config.cfg") @{
        "Transport"="Udp"; "UdpPort"="7803"; "CommandFile"="devcmd.txt"; "AutoLaunchRun"="false";
        "LogLevel"="Info"; "PreGenerateWorld"="true"; "BrMinPlayers"="1"; "GameMode"="BattleRoyale";
        "BrMatchMinutes"="30"; "BrRingStages"="4"
        "ContentRoot"=$(if ($NoServe) { "" } else { "beamcontent" })
    }
    foreach ($plug in @($ShootPlug, $ObsPlug)) {
        SetCfg (Join-Path $plug "config.cfg") @{
            "Transport"="Udp"; "UdpAddress"="127.0.0.1"; "UdpPort"="7803"; "AutoStart"="Join";
            "AutoReady"="true"; "CommandFile"="devcmd.txt"; "LogLevel"="Info"; "AutoLaunchRun"="false"
        }
    }
    Remove-Item -Force -EA SilentlyContinue $CoordLog, $ShootLog, $ObsLog,
        (Join-Path $ShootPlug "devcmd.txt"), (Join-Path $ObsPlug "devcmd.txt"), (Join-Path $CoordPlug "devcmd.txt")

    Line "starting" "coordinator (headless) + 2 WINDOWED clients"
    $procs += Launch $CoordDir $true $false
    if (-not (WaitFor $CoordLog "\[Udp\] hosting" 120 "hosting")) { throw "no host" }
    if (-not (WaitFor $CoordLog "\[PreGen\] world ready" 240 "pre-gen")) { throw "no pregen" }
    $procs += Launch $ShootDir $false $true
    Start-Sleep 3
    $procs += Launch $ObsDir $false $true
    if (-not (WaitFor $CoordLog "joined" 200 "clients join" 2)) { throw "clients did not join" }

    if (-not $NoServe) {
        foreach ($lg in @($ShootLog, $ObsLog)) {
            if (-not (WaitFor $lg "\[Content\] set [0-9a-f]+ installed at" 300 "content install")) { throw "content never installed" }
        }
    }

    Cmd $ShootPlug "start"
    if (-not (WaitFor $CoordLog "GO LIVE" 240 "go-live")) { throw "never went live" }
    Start-Sleep 12
    Line "match" "live"

    $slots = @{}
    foreach ($pair in @(@("shooter",$ShootLog), @("observer",$ObsLog))) {
        $m = @(Lines $pair[1] "welcomed as slot (\d+)")
        if ($m.Count -eq 0) { throw "could not read $($pair[0]) slot" }
        $slots[$pair[0]] = [int]$m[0].Matches[0].Groups[1].Value
    }
    Line "slots" ("shooter P{0}, observer P{1}" -f ($slots["shooter"]+1), ($slots["observer"]+1))

    # GOD ON BOTH, IMMEDIATELY -- before any staging. This test measures RENDERING, not damage,
    # so invulnerability costs nothing here and removes the entire class of failure that has
    # dogged every run: the shooter died to a station Turret Laser, then to a Floater, and this
    # time to a CellType Hazard at its own spawn -- before staging had even begun. `equip` then
    # succeeded against a stale reference and `fire` reported "no local ship", so the beam never
    # fired and twelve screenshots recorded nothing at all.
    foreach ($p in @($ShootPlug, $ObsPlug)) { Cmd $p "god" }
    Start-Sleep 3
    Line "invulnerable" "both ships (this test measures pixels, not damage)"

    # Stage clear of station turrets and hostiles -- both have killed a shooter mid-setup before.
    $stations = @(Lines $CoordLog "\[BR\] spawn slot \d -> station #\d+ at \((-?[0-9.]+),(-?[0-9.]+)\)" |
                  ForEach-Object { [pscustomobject]@{ X=[double]$_.Matches[0].Groups[1].Value; Y=[double]$_.Matches[0].Groups[2].Value } })
    # Land on a shop and nuke around it, rather than trusting arithmetic to find empty ground.
    Cmd $ObsPlug "tpshop"; Start-Sleep 3
    Cmd $ObsPlug "clearmobs 250"; Start-Sleep 3
    foreach ($p in @($ShootPlug, $ObsPlug)) { Cmd $p "autofly 0" }
    Start-Sleep 2
    Cmd $ObsPlug "pvpstage 40 20"; Start-Sleep 2
    Cmd $ShootPlug ("tpplayer {0} {1}" -f $slots["observer"], $Range)
    Start-Sleep 2
    Cmd $ShootPlug "pvpstage 40"; Start-Sleep 2
    Cmd $ShootPlug "clearmobs 250"; Start-Sleep 3

    # Unlimited resource so the beam can be HELD -- a beam that stutters out of ammo is
    # indistinguishable in a still frame from one that never drew.
    Cmd $ShootPlug "equip $Weapon"
    Start-Sleep 6
    if ((CountIn $ShootLog ("equip: .*" + [regex]::Escape($Weapon))) -lt 1) { throw "could not equip $Weapon" }
    Line "shooter" "$Weapon equipped (god already on)"

    # And confirm the ship is actually THERE before firing at it. "fire: no local ship" is what a
    # dead shooter looks like, and it is silent unless someone checks.
    if ((CountIn $ShootLog "local ship died") -gt 0) {
        $why = @(Lines $ShootLog "local ship died . broadcast \(killed by ([^)]+)\)")
        $by = if ($why.Count -gt 0) { $why[-1].Matches[0].Groups[1].Value } else { "unknown" }
        throw "the shooter died before firing (killed by $by) - the frames would show nothing"
    }

    # A baseline frame from each machine BEFORE firing. Without it there is nothing to compare a
    # "beam" against -- the world is full of bright things.
    $frames += Shoot $ShootPlug "before" $OutDir "shooter"
    $frames += Shoot $ObsPlug   "before" $OutDir "observer"

    Cmd $ShootPlug ("fire {0} player {1}" -f $HoldSeconds, $slots["observer"])
    Line "firing" "$HoldSeconds s held at the observer"
    Start-Sleep 4

    for ($i = 1; $i -le $ShotCount; $i++) {
        $frames += Shoot $ShootPlug "hold$i" $OutDir "shooter"
        $frames += Shoot $ObsPlug   "hold$i" $OutDir "observer"
        Start-Sleep 4
    }

    $localShots  = CountIn $ShootLog "\[ForgeDiag\] shot LOCAL"
    $replayShots = CountIn $ObsLog   "\[ForgeDiag\] shot REPLAYED"
    Line "forge trace" "shooter LOCAL=$localShots, observer REPLAYED=$replayShots"
    if ($replayShots -lt 1) { Write-Host "  NOTE: the observer never logged a replayed shot - a missing beam there is a REPLICATION problem, not a rendering one" -ForegroundColor Yellow }
}
finally {
    foreach ($p in $procs) { try { $p.Kill() } catch {} }
    Start-Sleep 3
    RestoreCfg
}

$frames = @($frames | Where-Object { $_ })
Write-Host "====================================================="
Write-Host "captured $($frames.Count) frame(s) in $OutDir"
$frames | ForEach-Object { Write-Host ("  " + (Split-Path $_ -Leaf)) }
Write-Host "Compare observer-before against observer-hold*: the beam is either drawn there or it is not."
