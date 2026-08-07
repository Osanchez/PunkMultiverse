# CONTENT SYNC END TO END: a joiner with none of the host's content ends up holding all of it.
#
# tools/content-hash-test.ps1 proves the two machines AGREE about a hash. This proves the bytes
# actually move, land correctly, gate the run while they are in flight, and cost nothing the
# second time. Those are four separate claims and each has its own way of passing by accident:
#
#   coldsync   the client had none of it, asked for what it lacked, and installed a set whose
#              hash equals the host's. Verified twice: by the mod's own digest, and by
#              Get-FileHash over active/ against the fixture -- byte-identity proven OUTSIDE our
#              own hash implementation, which is the only check that survives a bug in it.
#   gate       START is refused while a client is still downloading. This is the claim with real
#              consequences: going live with divergent content desyncs the BR drop table
#              silently. Asserted by making the transfer SLOW on purpose, not by timing luck.
#   warmsync   a restarted client with a warm cache logs a cache hit and requests zero blobs.
#              This is the whole "rejoin re-downloads nothing" requirement.
#   progress   the modal's numbers are REAL: the client reports intermediate percentages that
#              climb, the host receives them, and CANCEL leaves the gate shut rather than letting
#              the run start without that player. The modal's appearance needs a windowed game and
#              a human; everything driving it is asserted here.
#   resume     a client killed MID-DOWNLOAD picks up where it left off instead of starting over
#              -- and, far more importantly, is not wedged forever by the partial file it left
#              behind. This is not hypothetical: the host used to discard the resume offset
#              entirely, so the client asked to continue from N, the host sent from 0, and the
#              client rejected every chunk for the rest of time. That player could never become
#              ready in ANY future session until they deleted the cache by hand.
#   nocontent  a host serving nothing must not gate anybody. The feature has to be invisible
#              when it is not in use -- otherwise every vanilla session pays for it.
#
# The fixture is ~8 MB from a fixed seed, big enough that the transfer is observable and spans
# many chunks, and regenerated identically on every run so a failure is reproducible.
#
# DEV installs only. ASCII only. BOM-free configs.
param(
    [ValidateSet("all", "coldsync", "gate", "warmsync", "nocontent", "progress", "resume")]
    [string]$Phase = "all",
    [int]$RateKBps = 256
)
$ErrorActionPreference = "Stop"
$CoordDir = "C:\Program Files (x86)\Steam\steamapps\common\PUNK Playtest - OD Test2"
$BotDirs  = @(
    "C:\Program Files (x86)\Steam\steamapps\common\PUNK Playtest - OD Dev3",
    "C:\Program Files (x86)\Steam\steamapps\common\PUNK Playtest - OD Dev4"
)
$CoordPlug = Join-Path $CoordDir "BepInEx\plugins\PunkMultiverse"
$CoordLog  = Join-Path $CoordDir "BepInEx\LogOutput.log"
$Fixture   = "synccontent"          # deliberately NOT servecontent: the hash test owns that one

function CountIn($p,$pat){ if(-not(Test-Path $p)){return 0}; return @(Select-String -Path $p -Pattern $pat -AllMatches -EA SilentlyContinue).Count }
function Lines($p,$pat){ if(-not(Test-Path $p)){return @()}; return @(Select-String -Path $p -Pattern $pat -AllMatches -EA SilentlyContinue) }
function Cmd($plug,$txt){ Add-Content -Path (Join-Path $plug "devcmd.txt") -Value $txt -Encoding Ascii }
function Line($label,$text){ Write-Host ("  {0,-26} {1}" -f $label, $text) }
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

# The fixture is generated, not committed: ~8 MB of random bytes would be absurd in a repo, and a
# fixed seed makes it reproducible anyway. Byte content is deliberately incompressible-ish and
# file-unique so a transfer that mixes two blobs up cannot pass.
function BuildFixture([string]$root) {
    if (Test-Path $root) { Remove-Item -Recurse -Force $root }
    $spec = @(
        @{ p="weapons/mv_lance.json";      kb=2    },
        @{ p="weapons/mv_arc.json";        kb=3    },
        @{ p="weapons/mv_scatter.json";    kb=2    },
        @{ p="sprites/mv_lance.png";       kb=1400 },
        @{ p="sprites/mv_arc.png";         kb=1800 },
        @{ p="sprites/mv_scatter.png";     kb=1100 },
        @{ p="sprites/sub/mv_muzzle.png";  kb=900  },   # nested: relative paths must survive
        @{ p="sounds/mv_shot.wav";         kb=1500 },
        @{ p="sounds/mv_loop.wav";         kb=1200 },
        @{ p="readme.txt";                 kb=1    },
        @{ p="WeaponForge.dll";            kb=64   }   # MUST be refused -- see the assertion below
    )
    $total = 0
    foreach ($f in $spec) {
        $dest = Join-Path $root ($f.p -replace '/', '\')
        $dir = Split-Path $dest -Parent
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
        # Seed off the path so every file differs and the whole fixture is a pure function of the
        # spec above -- rerunning the test regenerates byte-identical content.
        $seed = 0
        foreach ($c in $f.p.ToCharArray()) { $seed = ($seed * 131 + [int]$c) % 2147483647 }
        $rnd = New-Object System.Random($seed)
        $bytes = New-Object byte[] ($f.kb * 1024)
        $rnd.NextBytes($bytes)
        [System.IO.File]::WriteAllBytes($dest, $bytes)
        $total += $bytes.Length
    }
    return $total
}

# Clear a client's content cache so "cold" genuinely means cold. Anything left from a previous
# run would turn coldsync into an accidental warmsync and it would still pass.
function ClearCache([string]$plug) {
    $c = Join-Path $plug "content"
    if (Test-Path $c) { Remove-Item -Recurse -Force $c }
}

$devRoots = @($CoordDir) + $BotDirs
if (Get-Process Punk -EA SilentlyContinue | Where-Object { $devRoots -contains (Split-Path $_.Path -Parent) }) {
    "ABORT: a DEV-install Punk.exe is already running."; exit 2
}

$script:ok = $true
$pids = @()
$BotPlugs = @($BotDirs | ForEach-Object { Join-Path $_ "BepInEx\plugins\PunkMultiverse" })
$BotLogs  = @($BotDirs | ForEach-Object { Join-Path $_ "BepInEx\LogOutput.log" })

function CleanLogs() {
    Remove-Item -Force -EA SilentlyContinue (Join-Path $CoordPlug "devcmd.txt"), $CoordLog, (Join-Path $CoordPlug "devout.txt")
    for ($i = 0; $i -lt $BotPlugs.Count; $i++) {
        Remove-Item -Force -EA SilentlyContinue (Join-Path $BotPlugs[$i] "devcmd.txt"), $BotLogs[$i], (Join-Path $BotPlugs[$i] "devout.txt")
    }
}
function StopAll($ids) { foreach ($p in $ids) { Stop-Process -Id $p -Force -EA SilentlyContinue }; Start-Sleep 4 }

try {
    $fixtureRoot = Join-Path $CoordPlug $Fixture
    $bytes = BuildFixture $fixtureRoot
    Line "fixture" ("{0} files ({1} servable + 1 refusable), {2:N0} bytes at {3}" -f 11, 10, $bytes, $Fixture)

    $hostContent = $(if ($Phase -eq "nocontent") { "" } else { $Fixture })
    SetCfg (Join-Path $CoordPlug "config.cfg") @{
        "Transport"="Udp"; "UdpPort"="7793"; "CommandFile"="devcmd.txt"; "AutoLaunchRun"="false";
        "LogLevel"="Verbose"; "PreGenerateWorld"="true"; "BrMinPlayers"="1"; "GameMode"="BattleRoyale";
        "ContentRoot"=$hostContent; "ContentRateKBps"="$RateKBps"
    }
    foreach ($plug in $BotPlugs) {
        SetCfg (Join-Path $plug "config.cfg") @{
            "Transport"="Udp"; "UdpAddress"="127.0.0.1"; "UdpPort"="7793"; "AutoStart"="Join";
            "AutoReady"="true"; "CommandFile"="devcmd.txt"; "LogLevel"="Verbose"; "AutoLaunchRun"="false";
            "ContentRoot"=""            # a client publishes nothing; it only receives
        }
    }

    # ---------------------------------------------------------------------------------------
    # COLD SYNC + GATE. One session: they are the same transfer observed at two moments, and
    # splitting them would mean paying for a second world pre-gen to learn nothing new.
    # ---------------------------------------------------------------------------------------
    if ($Phase -eq "all" -or $Phase -eq "coldsync" -or $Phase -eq "gate" -or $Phase -eq "nocontent") {
        Write-Host "--- $(if ($Phase -eq 'nocontent') { 'NO CONTENT' } else { 'COLD SYNC + GATE' }) ---"
        CleanLogs
        foreach ($plug in $BotPlugs) { ClearCache $plug }

        $pids += StartGame $CoordDir $true
        if (-not (WaitFor $CoordLog "\[Udp\] hosting" 120 "coordinator hosting")) { throw "no host" }
        if (-not (WaitFor $CoordLog "\[PreGen\] world ready" 220 "pre-build")) { throw "no pre-build" }

        if ($Phase -ne "nocontent") {
            # The host hashes its root when it begins hosting, before anyone can join -- so a
            # joiner never waits on the host's disk.
            if (-not (WaitFor $CoordLog "\[Content\] serving \d+ file" 60 "host hashed its content")) {
                Fail "the host never published its content set"
            }
            $sv = @(Lines $CoordLog "\[Content\] serving (\d+) file\(s\), set ([0-9a-f]+)")
            if ($sv.Count -gt 0) {
                $hostFiles = $sv[0].Matches[0].Groups[1].Value
                $script:hostSet = $sv[0].Matches[0].Groups[2].Value
                Line "host set" "$hostFiles files, set $script:hostSet"
                if ([int]$hostFiles -ne 10) { Fail "host published $hostFiles files, expected 10" }
            }
            # The DLL must be dropped from the set, BY NAME, and the other ten must still serve.
            $skipped = @(Lines $CoordLog "\[Content\]   (.+\.dll: .+)$")
            if ($skipped.Count -lt 1) {
                Fail "the host served an executable -- a content channel must not carry code"
            } else { Line "executable refused" $skipped[0].Matches[0].Groups[1].Value }
        }

        foreach ($d in $BotDirs) { $pids += StartGame $d $false; Start-Sleep 2 }
        if (-not (WaitFor $CoordLog "joined" 150 "bot joins" $BotDirs.Count)) { throw "bots did not all join" }

        if ($Phase -eq "nocontent") {
            # A host with no content must gate nobody: START has to work immediately, and no
            # client may sit waiting on a transfer that is never coming.
            Start-Sleep 6
            Cmd $BotPlugs[0] "start"
            if (-not (WaitFor $CoordLog "GO LIVE" 200 "go-live with no content")) {
                Fail "an empty ContentRoot blocked the run -- the feature is not invisible when unused"
            } else { Line "no-content go-live" "OK (nobody was gated)" }
            foreach ($lg in $BotLogs) {
                if ((CountIn $lg "\[Content\]") -gt 0) { Fail "a client did content work with no content on offer" }
            }
            StopAll $pids; $pids = @()
        }
        else {
            # THE GATE. The transfer is deliberately throttled so this is a real observation and
            # not a race the harness happens to win: at RateKBps the ~8 MB set cannot possibly be
            # done yet. If START is honoured here, a run can go live with divergent content.
            Start-Sleep 3
            Cmd $BotPlugs[0] "start"
            Start-Sleep 5
            $ignored = CountIn $CoordLog "\[Admin\] start ignored .*allReady=False"
            $tooEarly = CountIn $CoordLog "GO LIVE"
            if ($tooEarly -gt 0) { Fail "the run went live while a client was still downloading" }
            elseif ($ignored -lt 1) { Fail "START was neither refused nor honoured -- the gate is not observable" }
            else { Line "gate while syncing" "START refused (allReady=False)" }

            # --- the numbers behind the modal ---------------------------------------------
            # Sampled WHILE the throttle is still in effect, so these are genuine mid-transfer
            # readings. A bar that only ever shows 0 and 100 is not a progress bar, and blob
            # counting (rather than bytes) is the usual reason one behaves that way.
            $seen = @()
            for ($t = 0; $t -lt 8; $t++) {
                foreach ($p in $BotPlugs) { Cmd $p "contentstat" }
                Cmd $CoordPlug "contentstat"     # the HOST's view, sampled mid-transfer too
                Start-Sleep 3
            }
            foreach ($plug in $BotPlugs) {
                foreach ($m in (Lines (Join-Path $plug "devout.txt") "contentstat: local=(\w+) pct=(\d+) bytes=(\d+)/(\d+)")) {
                    $seen += [int]$m.Matches[0].Groups[2].Value
                }
            }
            # THE ROSTER VIEW. The host knowing a percentage is not the feature -- every OTHER
            # client has to know it too, or a downloading player still reads as somebody idling in
            # a slot. bot0 must see bot1 mid-transfer, from the roster alone.
            $peerSeen = @()
            for ($b = 0; $b -lt $BotPlugs.Count; $b++) {
                foreach ($m in (Lines (Join-Path $BotPlugs[$b] "devout.txt") "contentstat: roster P(\d) (\w+) (\d+)%$")) {
                    $peerSeen += [int]$m.Matches[0].Groups[3].Value      # rows WITHOUT "(me)"
                }
            }
            $peerMid = @($peerSeen | Where-Object { $_ -gt 0 -and $_ -lt 100 } | Select-Object -Unique)
            if ($peerMid.Count -lt 1) {
                Fail "no client ever saw ANOTHER player mid-sync on the roster - the lobby would still say NOT READY"
            } else { Line "peer sees peer" "$($peerMid.Count) distinct mid-sync value(s) on the roster" }

            $mid = @($seen | Where-Object { $_ -gt 0 -and $_ -lt 100 } | Select-Object -Unique | Sort-Object)
            Line "progress samples" (($seen | Select-Object -Unique | Sort-Object) -join ", ")
            if ($mid.Count -lt 2) {
                Fail "the client never reported two distinct mid-transfer percentages - the bar would jump 0 to 100"
            } else { Line "progress climbs" "$($mid.Count) distinct mid-transfer values" }

            # And the host must RECEIVE them, or the lobby cannot show a per-player figure. Read
            # from the samples taken during the loop above: a reading taken after the transfer
            # finished would say 100% and prove nothing, which is what the first version of this
            # assertion did.
            $peerRows = @(Lines (Join-Path $CoordPlug "devout.txt") "contentstat:   P(\d) (\w+) (\d+)%")
            if ($peerRows.Count -lt 1) { Fail "the host has no per-peer content state to show in the lobby" }
            else {
                $hostPct = @($peerRows | ForEach-Object { [int]$_.Matches[0].Groups[3].Value })
                $hostMid = @($hostPct | Where-Object { $_ -gt 0 -and $_ -lt 100 } | Select-Object -Unique)
                Line "host peer view" (($hostPct | Select-Object -Unique | Sort-Object) -join ", ")
                if ($hostMid.Count -lt 1) {
                    Fail "the host only ever saw 0% or 100% - ContentStatus is not reaching it, so a lobby percentage would be fiction"
                } else { Line "host mid-transfer" "$($hostMid.Count) distinct value(s) received" }
            }

            # ...and it must not be a permanent block. Once the content lands the same command works.
            foreach ($lg in $BotLogs) {
                if (-not (WaitFor $lg "\[Content\] set [0-9a-f]+ installed at" 300 "client install")) {
                    Fail "a client never finished installing the content"
                }
            }
            Start-Sleep 6
            Cmd $BotPlugs[0] "start"
            if (-not (WaitFor $CoordLog "GO LIVE" 240 "go-live after sync")) {
                Fail "the run never went live even after every client was satisfied -- the gate does not release"
            } else { Line "gate after sync" "START honoured, run went live" }

            # --- what actually arrived ---------------------------------------------------------
            for ($i = 0; $i -lt $BotLogs.Count; $i++) {
                $need = @(Lines $BotLogs[$i] "\[Content\] need (\d+)/(\d+) blob\(s\)")
                $inst = @(Lines $BotLogs[$i] "\[Content\] set ([0-9a-f]+) installed at (.+)$")
                if ($need.Count -lt 1) { Fail "bot$i never requested any blobs -- its cache was not cold" }
                else { Line "bot$i requested" ("{0}/{1} blobs" -f $need[0].Matches[0].Groups[1].Value, $need[0].Matches[0].Groups[2].Value) }
                if ($inst.Count -lt 1) { Fail "bot$i never installed a set" ; continue }
                $got = $inst[0].Matches[0].Groups[1].Value
                $path = $inst[0].Matches[0].Groups[2].Value.Trim()
                Line "bot$i installed" "$got"
                if ($script:hostSet -and $got -ne $script:hostSet) {
                    Fail "bot$i installed set $got but the host served $script:hostSet"
                }
                if ((CountIn $BotLogs[$i] "\[Content\] failed:") -gt 0) { Fail "bot$i logged a content failure" }
                if ((CountIn $BotLogs[$i] "blob failed verification") -gt 0) {
                    Fail "bot$i had to re-request a blob that failed verification"
                }

                # THE INDEPENDENT CHECK. Everything above is the mod agreeing with itself; a bug in
                # ContentHash would keep every one of those assertions green. Get-FileHash over the
                # materialised tree against the fixture is the one comparison that does not use a
                # single line of the code under test.
                if (Test-Path $path) {
                    $bad = 0; $n = 0
                    # The refused executable is deliberately absent from the installed tree, so
                    # it is not part of this comparison -- its own assertion is above.
                    foreach ($src in Get-ChildItem -Recurse -File $fixtureRoot | Where-Object { $_.Extension -ne ".dll" }) {
                        $rel = $src.FullName.Substring($fixtureRoot.Length).TrimStart('\')
                        $dst = Join-Path $path $rel
                        $n++
                        if (-not (Test-Path $dst)) { Write-Host "        missing: $rel"; $bad++; continue }
                        $a = (Get-FileHash -Algorithm SHA256 $src.FullName).Hash
                        $b = (Get-FileHash -Algorithm SHA256 $dst).Hash
                        if ($a -ne $b) { Write-Host "        differs: $rel"; $bad++ }
                    }
                    if ($bad -gt 0) { Fail "bot$i's installed tree differs from the fixture in $bad/$n file(s)" }
                    else { Line "bot$i bytes on disk" "$n/$n identical to the fixture (Get-FileHash)" }
                } else { Fail "bot$i reported installing to '$path', which does not exist" }
            }
            if ((CountIn $CoordLog "\[Content\] P\d has the content") -lt $BotDirs.Count) {
                Fail "the host was not told by every client that the content landed"
            } else { Line "host acks" "$($BotDirs.Count)/$($BotDirs.Count) clients confirmed" }

            StopAll $pids; $pids = @()
        }
    }

    # ---------------------------------------------------------------------------------------
    # CANCEL. The modal's button, driven through the same method it calls. What must NOT happen
    # is the run starting without that player: they would be holding a different weapon set, which
    # is the exact divergence this whole feature exists to prevent. Cancelling has to leave the
    # gate SHUT, not open it by removing the thing being waited on.
    # ---------------------------------------------------------------------------------------
    if ($Phase -eq "progress") {
        Write-Host "--- CANCEL ---"
        CleanLogs
        foreach ($plug in $BotPlugs) { ClearCache $plug }

        $pids += StartGame $CoordDir $true
        if (-not (WaitFor $CoordLog "\[Udp\] hosting" 120 "coordinator hosting")) { throw "no host" }
        if (-not (WaitFor $CoordLog "\[PreGen\] world ready" 220 "pre-build")) { throw "no pre-build" }
        foreach ($d in $BotDirs) { $pids += StartGame $d $false; Start-Sleep 2 }
        if (-not (WaitFor $CoordLog "joined" 150 "bot joins" $BotDirs.Count)) { throw "bots did not all join" }

        # Cancel bot1 mid-transfer; bot0 is left to finish so the ONLY reason the run cannot start
        # is the player who refused.
        if (-not (WaitFor $BotLogs[1] "\[Content\] need \d+/\d+ blob" 90 "bot1 download start")) {
            Fail "bot1 never started downloading, so there was nothing to cancel"
        }
        Cmd $BotPlugs[1] "contentcancel"
        Start-Sleep 6
        $c = @(Lines $BotLogs[1] "\[Content\] cancelled by the player at (\d+)%")
        if ($c.Count -lt 1) { Fail "bot1 did not register the cancel" }
        else { Line "bot1 cancelled" ("at {0}%" -f $c[0].Matches[0].Groups[1].Value) }

        if ((CountIn $CoordLog "could not install the content: cancelled by the player") -lt 1) {
            Fail "the host was never told the player cancelled"
        } else { Line "host told" "cancel reported" }

        # The gate must stay shut. This is the assertion with teeth.
        Cmd $BotPlugs[0] "start"
        Start-Sleep 8
        if ((CountIn $CoordLog "GO LIVE") -gt 0) {
            Fail "the run went live after a player cancelled - they would be on different content"
        } else { Line "gate after cancel" "START still refused" }

        StopAll $pids; $pids = @()
    }

    # ---------------------------------------------------------------------------------------
    # RESUME. Kill a client mid-transfer, restart it, and require that the partial file it left
    # is an ASSET rather than a trap.
    # ---------------------------------------------------------------------------------------
    if ($Phase -eq "resume") {
        Write-Host "--- RESUME ---"
        CleanLogs
        ClearCache $BotPlugs[0]

        $pids += StartGame $CoordDir $true
        if (-not (WaitFor $CoordLog "\[Udp\] hosting" 120 "coordinator hosting")) { throw "no host" }
        if (-not (WaitFor $CoordLog "\[PreGen\] world ready" 220 "pre-build")) { throw "no pre-build" }

        $botPid = StartGame $BotDirs[0] $false
        $pids += $botPid
        if (-not (WaitFor $BotLogs[0] "\[Content\] need \d+/\d+ blob" 150 "download start")) {
            Fail "bot0 never started downloading"
        }
        Start-Sleep 10                       # throttled, so this is a genuinely partial transfer
        Stop-Process -Id $botPid -Force -EA SilentlyContinue
        Start-Sleep 4

        $parts = @(Get-ChildItem -Recurse -File (Join-Path $BotPlugs[0] "content") -Filter *.part -EA SilentlyContinue)
        Line "partials left" ("{0} .part file(s)" -f $parts.Count)
        if ($parts.Count -lt 1) {
            Fail "no partial file survived the kill - this phase cannot test what it exists to test"
        }
        $partBytes = ($parts | Measure-Object -Property Length -Sum).Sum

        # Back it comes, with that partial still on disk.
        $pids += StartGame $BotDirs[0] $false
        if (-not (WaitFor $BotLogs[0] "\[Content\] set [0-9a-f]+ installed at" 300 "resumed install")) {
            Fail "bot0 never finished after resuming - a partial download wedged it"
        } else { Line "resumed install" "completed" }

        # The host must have been TOLD to resume, or it is silently re-sending from zero and the
        # only reason this passes is that the client tolerates it.
        $res = @(Lines $CoordLog "\[Content\] peer \d+ needs \d+ blob\(s\) \((\d+) resuming\)")
        if ($res.Count -lt 1) {
            Fail "the host was never asked to resume - the offset is being dropped again"
        } else { Line "host resumed" ("{0} blob(s), {1:N0} bytes already held" -f $res[-1].Matches[0].Groups[1].Value, $partBytes) }

        # And no rejection storm. One rejected chunk is a self-heal; hundreds is the old wedge.
        $rej = CountIn $BotLogs[0] "chunk rejected"
        Line "chunks rejected" "$rej"
        if ($rej -gt 4) { Fail "$rej chunks rejected - the resume offset is not being honoured" }

        StopAll $pids; $pids = @()
    }

    # ---------------------------------------------------------------------------------------
    # WARM SYNC. Same fixture, same host, clients restarted with their cache INTACT. The whole
    # point of the content-addressed store: a rejoin costs one file-exists check.
    # ---------------------------------------------------------------------------------------
    if ($Phase -eq "all" -or $Phase -eq "warmsync") {
        Write-Host "--- WARM SYNC (cache intact) ---"
        # Logs only -- deliberately NOT the cache. If this phase runs standalone the cache may be
        # cold and the assertions below will say so rather than passing quietly.
        CleanLogs

        $pids += StartGame $CoordDir $true
        if (-not (WaitFor $CoordLog "\[Udp\] hosting" 120 "coordinator hosting")) { throw "no host" }
        if (-not (WaitFor $CoordLog "\[PreGen\] world ready" 220 "pre-build")) { throw "no pre-build" }
        foreach ($d in $BotDirs) { $pids += StartGame $d $false; Start-Sleep 2 }
        if (-not (WaitFor $CoordLog "joined" 150 "bot joins" $BotDirs.Count)) { throw "bots did not all join" }

        for ($i = 0; $i -lt $BotLogs.Count; $i++) {
            if (-not (WaitFor $BotLogs[$i] "\[Content\] cache hit for set" 90 "bot$i cache hit")) {
                Fail "bot$i did not hit its cache -- a rejoin is re-downloading content it already has"
            } else { Line "bot$i" "cache hit" }
            if ((CountIn $BotLogs[$i] "\[Content\] need \d+/\d+ blob") -gt 0) {
                Fail "bot$i requested blobs despite a warm cache"
            }
        }
        if ((CountIn $CoordLog "\[Content\] peer \d+ needs") -gt 0) {
            Fail "the host queued blobs for a client that already had the set"
        } else { Line "host sent" "zero blobs" }

        # And the cache hit must still end in a satisfied, ungated client.
        foreach ($lg in $BotLogs) {
            if (-not (WaitFor $lg "\[Content\] set [0-9a-f]+ installed at" 90 "bot install from cache")) {
                Fail "a cache hit never produced an installed set"
            }
        }
        Start-Sleep 4
        Cmd $BotPlugs[0] "start"
        if (-not (WaitFor $CoordLog "GO LIVE" 240 "warm go-live")) {
            Fail "the run never went live from a warm cache"
        } else { Line "warm go-live" "OK" }

        StopAll $pids; $pids = @()
    }

    # A background thread that does not stop is an already-observed class of bug on this project.
    foreach ($lg in @($CoordLog) + $BotLogs) {
        if ((CountIn $lg "\[Content\] worker did not stop") -gt 0) { Fail "the content worker failed to stop in $lg" }
    }
}
finally {
    foreach ($p in $pids) { Stop-Process -Id $p -Force -EA SilentlyContinue }
    RestoreCfgKeys
}

Write-Host "====================================================="
Write-Host $(if ($script:ok) { "CONTENT SYNC: PASS" } else { "CONTENT SYNC: PROBLEMS ABOVE" })
if (-not $script:ok) { exit 1 }
