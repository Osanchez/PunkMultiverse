# CONTENT SWAP: a player who does not have the host's weapons ends up able to FIRE them.
#
# tools/content-test.ps1 proves the bytes arrive. That is not the same claim as this one. Its
# fixture is synthetic bytes nothing ever loads, so it cannot tell the difference between content
# that landed and content that landed AND got loaded into the game. This is the test that can.
#
# The setup is the whole point: bot1's WeaponForge folders are EMPTIED before it starts, so it
# boots with none of the host's weapons and must obtain every one of them over the wire. Then it
# is the machine told to equip and fire one. If the swap only half worked, this is where it shows:
#
#   registered   bot1 logs the reload and ends up with the same module digest as everyone else.
#                A digest match is what the go-live barrier gates on, so without it nothing runs.
#   sprites      the sprite and sound loaders latch on `_loaded` and return immediately once
#                loaded. A swap that forgets to drop that latch loads the new WEAPONS against the
#                OLD sprites and sounds -- and looks completely fine until you fire.
#   fires        bot1 shoots the custom weapon and bot0 logs the SAME weapon id replayed, plus
#                damage. This is the chain that matters to a player.
#   restored     after the session, bot1 has its own (empty) set back. A restore that is assumed
#                rather than measured is exactly the kind of probe that lies.
#
# bot1's real WeaponForge content is moved aside and put back in the finally block. If this script
# is killed mid-run, the folders are at <name>.swapbak next to the originals.
#
# DEV installs only. ASCII only. BOM-free configs.
param(
    [string]$Weapon = "FORGE-MVPLASMALANCE",
    [int]$FireSeconds = 8
)
$ErrorActionPreference = "Stop"
$CoordDir = "C:\Program Files (x86)\Steam\steamapps\common\PUNK Playtest - OD Test2"
$BotDirs  = @(
    "C:\Program Files (x86)\Steam\steamapps\common\PUNK Playtest - OD Dev3",   # keeps its content
    "C:\Program Files (x86)\Steam\steamapps\common\PUNK Playtest - OD Dev4"    # STRIPPED
)
$CoordPlug = Join-Path $CoordDir "BepInEx\plugins\PunkMultiverse"
$CoordLog  = Join-Path $CoordDir "BepInEx\LogOutput.log"
$Fixture   = "forgecontent"
$ForgeDirs = @("weapons", "sprites", "sounds")

function CountIn($p,$pat){ if(-not(Test-Path $p)){return 0}; return @(Select-String -Path $p -Pattern $pat -AllMatches -EA SilentlyContinue).Count }
function Lines($p,$pat){ if(-not(Test-Path $p)){return @()}; return @(Select-String -Path $p -Pattern $pat -AllMatches -EA SilentlyContinue) }
function Cmd($plug,$txt){ Add-Content -Path (Join-Path $plug "devcmd.txt") -Value $txt -Encoding Ascii }
function Line($label,$text){ Write-Host ("  {0,-24} {1}" -f $label, $text) }
function Fail($msg){ Write-Host "  FAIL: $msg"; $script:ok = $false }
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

# Move the victim's real content aside. A rename rather than a delete: this is a player's install
# and the content is not reproducible from this repo.
$script:Stripped = @()
function StripForgeContent([string]$pluginRoot) {
    foreach ($d in $ForgeDirs) {
        $src = Join-Path $pluginRoot $d
        if (-not (Test-Path $src)) { continue }
        $bak = "$src.swapbak"
        if (Test-Path $bak) { throw "a previous run left $bak behind - restore it by hand before re-running" }
        Move-Item -Path $src -Destination $bak
        $script:Stripped += @{ Live=$src; Bak=$bak }
    }
}
function RestoreForgeContent() {
    foreach ($s in $script:Stripped) {
        if (-not (Test-Path $s.Bak)) { continue }
        if (Test-Path $s.Live) { Remove-Item -Recurse -Force $s.Live }
        Move-Item -Path $s.Bak -Destination $s.Live
    }
    if ($script:Stripped.Count -gt 0) {
        Write-Host "restored $($script:Stripped.Count) WeaponForge folder(s) to $($BotDirs[1])"
        $script:Stripped = @()
    }
}

$devRoots = @($CoordDir) + $BotDirs

# A test against the wrong game build produces evidence about a game nobody plays. The dev
# installs are copies and only Steam's is patched -- that gap reached five weeks once.
. (Join-Path $PSScriptRoot "lib-preflight.ps1")
Assert-GameBuild -Installs $devRoots
if (Get-Process Punk -EA SilentlyContinue | Where-Object { $devRoots -contains (Split-Path $_.Path -Parent) }) {
    "ABORT: a DEV-install Punk.exe is already running."; exit 2
}

$script:ok = $true
$pids = @()
$BotPlugs = @($BotDirs | ForEach-Object { Join-Path $_ "BepInEx\plugins\PunkMultiverse" })
$BotLogs  = @($BotDirs | ForEach-Object { Join-Path $_ "BepInEx\LogOutput.log" })
# WeaponForge's own folder, not the shared plugins folder. If a legacy flat copy of weapons/ is
# ever left behind in plugins/, a forked WeaponForge falls back to it and this strip silently does
# nothing -- the victim boots WITH content and the test passes without testing anything.
$VictimPlugins = Join-Path $BotDirs[1] "BepInEx\plugins\WeaponForge"

try {
    # --- what the host will serve: its own WeaponForge content, copied into a served folder ----
    # Copied rather than served in place, because ContentRoot pointed at the plugins folder would
    # try to publish WeaponForge.dll -- which the host refuses, correctly, but noisily.
    $fixtureRoot = Join-Path $CoordPlug $Fixture
    if (Test-Path $fixtureRoot) { Remove-Item -Recurse -Force $fixtureRoot }
    New-Item -ItemType Directory -Force -Path $fixtureRoot | Out-Null
    $served = 0
    foreach ($d in $ForgeDirs) {
        $src = Join-Path (Join-Path $CoordDir "BepInEx\plugins\WeaponForge") $d
        if (-not (Test-Path $src)) { continue }
        Copy-Item -Recurse -Path $src -Destination (Join-Path $fixtureRoot $d)
        $served += @(Get-ChildItem -Recurse -File $src).Count
    }
    if ($served -eq 0) { throw "the host has no WeaponForge content to serve" }
    Line "host serves" "$served file(s) from $Fixture"

    # --- strip the victim ---------------------------------------------------------------------
    StripForgeContent $VictimPlugins
    Line "bot1 stripped" "its weapons, sprites and sounds are moved aside"

    SetCfg (Join-Path $CoordPlug "config.cfg") @{
        "Transport"="Udp"; "UdpPort"="7795"; "CommandFile"="devcmd.txt"; "AutoLaunchRun"="false";
        "LogLevel"="Verbose"; "PreGenerateWorld"="true"; "BrMinPlayers"="1"; "GameMode"="BattleRoyale";
        "BrMatchMinutes"="12"; "BrRingStages"="4"; "ContentRoot"=$Fixture
    }
    Remove-Item -Force -EA SilentlyContinue (Join-Path $CoordPlug "devcmd.txt"), $CoordLog, (Join-Path $CoordPlug "devout.txt")
    for ($i = 0; $i -lt $BotPlugs.Count; $i++) {
        SetCfg (Join-Path $BotPlugs[$i] "config.cfg") @{
            "Transport"="Udp"; "UdpAddress"="127.0.0.1"; "UdpPort"="7795"; "AutoStart"="Join";
            "AutoReady"="true"; "CommandFile"="devcmd.txt"; "LogLevel"="Verbose"; "AutoLaunchRun"="false";
            "ContentRoot"=""
        }
        Remove-Item -Force -EA SilentlyContinue (Join-Path $BotPlugs[$i] "devcmd.txt"), $BotLogs[$i], (Join-Path $BotPlugs[$i] "devout.txt")
        # Cold every time: a warm cache would still exercise the swap, but it would stop this
        # test from also proving the transfer that feeds it.
        $c = Join-Path $BotPlugs[$i] "content"
        if (Test-Path $c) { Remove-Item -Recurse -Force $c }
    }

    $pids += StartGame $CoordDir $true
    if (-not (WaitFor $CoordLog "\[Udp\] hosting" 120 "coordinator hosting")) { throw "no host" }
    if (-not (WaitFor $CoordLog "\[PreGen\] world ready" 220 "pre-build")) { throw "no pre-build" }
    foreach ($d in $BotDirs) { $pids += StartGame $d $false; Start-Sleep 2 }
    if (-not (WaitFor $CoordLog "joined" 150 "bot joins" $BotDirs.Count)) { throw "bots did not all join" }

    # --- the swap ------------------------------------------------------------------------------
    Write-Host "--- swap ---"
    foreach ($i in 0,1) {
        if (-not (WaitFor $BotLogs[$i] "\[Content\] set [0-9a-f]+ installed at" 300 "bot$i install")) {
            Fail "bot$i never installed the host's content"
        }
    }
    if (-not (WaitFor $BotLogs[1] "\[Forge\] reloaded for host content" 60 "bot1 reload")) {
        Fail "bot1 never reloaded WeaponForge against the host's content"
    }
    foreach ($i in 0,1) {
        $r = @(Lines $BotLogs[$i] "\[Forge\] reloaded for host content: dropped (\d+), registered (\d+)")
        if ($r.Count -gt 0) {
            $reg = [int]$r[0].Matches[0].Groups[2].Value
            Line "bot$i reload" ("dropped {0}, registered {1}" -f $r[0].Matches[0].Groups[1].Value, $reg)
            if ($reg -lt 1) { Fail "bot$i registered no custom modules after the swap" }
        }
    }

    # A digest match is the claim with teeth: it is what the go-live barrier gates on, and bot1
    # only reaches it by having genuinely loaded content it did not possess at boot.
    Cmd $CoordPlug "moduledigest"; foreach ($p in $BotPlugs) { Cmd $p "moduledigest" }
    Start-Sleep 5
    $digests = @()
    foreach ($p in @($CoordPlug) + $BotPlugs) {
        $m = @(Lines (Join-Path $p "devout.txt") "moduledigest: modules=(\d+) digest=([0-9A-F]+)")
        if ($m.Count -gt 0) { $digests += $m[0].Matches[0].Groups[2].Value }
    }
    Line "module digests" (($digests | Select-Object -Unique) -join ", ")
    if ($digests.Count -ne 3) { Fail "only $($digests.Count)/3 machines reported a module digest" }
    elseif (($digests | Select-Object -Unique).Count -ne 1) {
        Fail "the machines disagree on the module set after the swap"
    } else { Line "digest agreement" "all 3 machines identical" }

    # --- and it actually fires ------------------------------------------------------------------
    Cmd $BotPlugs[0] "start"
    if (-not (WaitFor $CoordLog "GO LIVE" 220 "go-live")) { throw "never went live" }
    Start-Sleep 5
    foreach ($p in $BotPlugs) { Cmd $p "god" }

    $BotSlots = @()
    foreach ($lg in $BotLogs) {
        $m = @(Lines $lg "welcomed as slot (\d+)")
        if ($m.Count -eq 0) { throw "could not read a bot's slot" }
        $BotSlots += [int]$m[0].Matches[0].Groups[1].Value
    }

    # bot1 -- the machine that had NOTHING -- is the shooter. That is the whole point.
    Cmd $BotPlugs[1] "equip $Weapon"
    Start-Sleep 10
    # The parentheses are load-bearing. In ARGUMENT position PowerShell does not evaluate
    # [regex]::Escape($Weapon) as an expression -- it passes it as a literal string, so the search
    # is for a pattern that matches nothing and the assertion false-FAILs while the feature works
    # perfectly. That is exactly what this test did on its first green run.
    if ((CountIn $BotLogs[1] ([regex]::Escape($Weapon))) -lt 1) {
        Fail "bot1 could not equip $Weapon - the downloaded content did not reach the registry"
    } else { Line "bot1 equipped" $Weapon }

    # Staging lifted from forge-sync-test.ps1; every step there exists because of a measured
    # failure, and the argument order really is `fire <secs> player <slot>`.
    foreach ($p in $BotPlugs) { Cmd $p "autofly 0" }
    Start-Sleep 2
    Cmd $BotPlugs[0] "pvpstage 30 45"
    Start-Sleep 2
    Cmd $BotPlugs[1] ("tpplayer {0} 5" -f $BotSlots[0])
    Start-Sleep 1
    Cmd $BotPlugs[1] "pvpstage 30"
    Start-Sleep 2
    Cmd $BotPlugs[1] ("fire {0} player {1}" -f $FireSeconds, $BotSlots[0])
    Start-Sleep ($FireSeconds + 10)

    Write-Host "--- fires ---"
    $armed = CountIn $BotLogs[1] "fire: [0-9.]+s.*via weapon trigger"
    Line "fire armed" $(if ($armed -gt 0) { "yes" } else { "NO - the fire command never armed" })
    $local  = @(Lines $BotLogs[1] "\[ForgeDiag\] shot LOCAL '([^']+)'")
    $replay = @(Lines $BotLogs[0] "\[ForgeDiag\] shot REPLAYED '([^']+)'")
    Line "bot1 shot LOCAL" "$($local.Count)"
    Line "bot0 shot REPLAYED" "$($replay.Count)"
    if ($local.Count -lt 1) { Fail "bot1 never fired the downloaded weapon" }
    if ($replay.Count -lt 1) { Fail "bot0 never saw the downloaded weapon fire" }
    if ($local.Count -gt 0 -and $replay.Count -gt 0) {
        $a = $local[0].Matches[0].Groups[1].Value
        $b = $replay[0].Matches[0].Groups[1].Value
        Line "weapon id" "$a -> $b"
        if ($a -ne $b) { Fail "the peer replayed a DIFFERENT weapon ($a fired, $b replayed)" }
        if ($a -ne $Weapon) { Fail "bot1 fired '$a', expected '$Weapon'" }
    }
    if ((CountIn $BotLogs[0] "\[ForgeDiag\] damage .* from '$([regex]::Escape($Weapon))'") -lt 1) {
        Fail "bot0 registered no damage from the downloaded weapon"
    } else { Line "damage" "bot0 took damage from $Weapon" }

    # --- and gives it back -----------------------------------------------------------------------
    Write-Host "--- restore ---"
    Cmd $BotPlugs[1] "mainmenu"
    Start-Sleep 12
    if ((CountIn $BotLogs[1] "\[Forge\] the player's own weapons are back") -lt 1) {
        Fail "bot1 never restored its own content when the session ended"
    } else { Line "bot1 restored" "its own (empty) content is back" }
}
finally {
    foreach ($p in $pids) { Stop-Process -Id $p -Force -EA SilentlyContinue }
    Start-Sleep 3
    RestoreForgeContent
    RestoreCfgKeys
}

Write-Host "====================================================="
Write-Host $(if ($script:ok) { "FORGE SWAP: PASS" } else { "FORGE SWAP: PROBLEMS ABOVE" })
if (-not $script:ok) { exit 1 }
