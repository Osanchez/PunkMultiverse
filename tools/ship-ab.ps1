# SHIP MOVEMENT STREAMING A/B — two bots on the REAL server, teleported together, orbiting at
# FULL THROTTLE around each other and firing, measuring how that movement arrives on the other
# client. Runs every ceiling in -Ceilings within ONE session (no restarts) so the comparison is
# against identical network conditions. Fast by design: ~40s per condition.
#
#   [ShipLatency]  buffer health  (delay vs what the formula WANTED, saturation, underruns)
#   shipsmooth     drawn motion   (CV + stall% of the remote ship's rendered path)
# ASCII only. BOM-free configs.
param(
    [int]$SampleSeconds = 40,
    [string]$Server = "100.110.40.88",
    [int]$Port = 7778,
    [string[]]$Ceilings = @("auto","200","300")
)
$ErrorActionPreference = "Stop"
$BotDirs = @(
    "C:\Program Files (x86)\Steam\steamapps\common\PUNK Playtest - OD Dev3",
    "C:\Program Files (x86)\Steam\steamapps\common\PUNK Playtest - OD Dev4")

function CountIn($p,$pat){ if(-not(Test-Path $p)){return 0}; return @(Select-String -Path $p -Pattern $pat -AllMatches -EA SilentlyContinue).Count }
function WaitFor($p,$pat,$to,$what,$min=1){ $d=(Get-Date).AddSeconds($to); while((Get-Date)-lt $d){ if((CountIn $p $pat)-ge $min){return $true}; Start-Sleep 2 }; Write-Host "TIMEOUT $what"; return $false }
function Cmd($dir,$txt){ Add-Content -Path (Join-Path $dir "BepInEx\plugins\PunkMultiverse\devcmd.txt") -Value $txt -Encoding Ascii }
function SetCfg([string]$path,[hashtable]$kv){
    $cfg = Get-Content -Raw $path
    foreach ($k in $kv.Keys) { $cfg = $cfg -replace ("(?m)^{0}\s*=.*$" -f [regex]::Escape($k)), ("{0} = {1}" -f $k,$kv[$k]) }
    [System.IO.File]::WriteAllText($path,$cfg)
}
function LogOf($d){ Join-Path $d "BepInEx\LogOutput.log" }

$clash = Get-Process Punk -EA SilentlyContinue | Where-Object { $BotDirs -contains (Split-Path $_.Path -Parent) }
if ($clash) { "ABORT: a bot-install Punk.exe is already running."; exit 2 }

$pids = @()
try {
    foreach ($d in $BotDirs) {
        $plug = Join-Path $d "BepInEx\plugins\PunkMultiverse"
        SetCfg (Join-Path $plug "config.cfg") @{ "Transport"="Udp"; "UdpAddress"=$Server; "UdpPort"="$Port"; "AutoStart"="Join"; "AutoReady"="true"; "CommandFile"="devcmd.txt"; "LogLevel"="Verbose"; "AutoLaunchRun"="false" }
        Remove-Item -Force -EA SilentlyContinue (Join-Path $plug "devcmd.txt"), (Join-Path $plug "devcmd.txt.consuming"), (LogOf $d), (Join-Path $plug "lastsession.txt")
    }
    foreach ($d in $BotDirs) {
        $bp = New-Object System.Diagnostics.ProcessStartInfo
        $bp.FileName = Join-Path $d "Punk.exe"; $bp.Arguments = "-batchmode -nographics"; $bp.WorkingDirectory = $d; $bp.UseShellExecute = $false
        foreach($k in @($bp.EnvironmentVariables.Keys | Where-Object {$_ -like "DOORSTOP*"})){ $bp.EnvironmentVariables.Remove($k) }
        $pids += [System.Diagnostics.Process]::Start($bp).Id
        if (-not (WaitFor (LogOf $d) "welcomed as slot" 120 "join")) { throw "a bot never joined" }
    }
    # Real slot numbers, not assumed - shipsmooth/tpplayer both key off them.
    $slots = @()
    foreach ($d in $BotDirs) {
        $m = Select-String -Path (LogOf $d) -Pattern "welcomed as slot (\d+)" | Select-Object -First 1
        $slots += [int]$m.Matches[0].Groups[1].Value
    }
    Write-Host "bots joined as slots $($slots -join ', ')"

    Start-Sleep 4
    Cmd $BotDirs[0] "start"
    if (-not (WaitFor (LogOf $BotDirs[0]) "GO LIVE" 300 "go-live")) { throw "run never went live" }
    Write-Host "RUN LIVE"
    Start-Sleep 4
    foreach ($d in $BotDirs) { Cmd $d "god" }
    # Collapse BR's ~1600-unit spawn scatter: staleness is only visible on a target you track.
    Cmd $BotDirs[1] ("tpplayer {0} 12" -f $slots[0])
    Start-Sleep 3

    foreach ($ceil in $Ceilings) {
        Write-Host "--- condition: shipdelay $ceil ---"
        foreach ($d in $BotDirs) { Cmd $d "shipdelay $ceil"; Cmd $d "say PHASE_$ceil" }
        Start-Sleep 2
        # Re-close the gap each round (orbiting drifts them apart), then full-throttle circles
        # while shooting at each other.
        Cmd $BotDirs[1] ("tpplayer {0} 12" -f $slots[0])
        Start-Sleep 1
        for ($i = 0; $i -lt $BotDirs.Count; $i++) {
            Cmd $BotDirs[$i] "orbit $SampleSeconds 4"
            Cmd $BotDirs[$i] "fire $SampleSeconds"
        }
        Start-Sleep 6   # let the buffer settle into the new ceiling before sampling
        for ($i = 0; $i -lt $BotDirs.Count; $i++) {
            $other = $slots[1 - $i]
            Cmd $BotDirs[$i] ("shipsmooth {0} {1}" -f $other, ($SampleSeconds - 12))
        }
        Start-Sleep ($SampleSeconds + 6)
    }

    Write-Host ""
    Write-Host "================= SHIP MOVEMENT STREAMING: A/B vs the LIVE server ================="
    for ($i = 0; $i -lt $BotDirs.Count; $i++) {
        $log = LogOf $BotDirs[$i]
        Write-Host ("bot$($i+1) (slot $($slots[$i])) watching slot $($slots[1-$i]):")
        $lines = Get-Content $log
        $phase = "(pre)"
        $acc = @{}
        $smooth = @{}
        foreach ($ln in $lines) {
            if ($ln -match "PHASE_(\S+)") { $phase = $Matches[1]; continue }
            if ($ln -match "\[ShipLatency\].*snapshots=([0-9.]+)/s delayAvg=([0-9.]+)ms wantedAvg=([0-9.]+)ms delayMax=([0-9.]+)ms jitterAvg=([0-9.]+)ms saturated=([0-9.]+)% underruns=([0-9]+) \(([0-9.]+)/s\)") {
                if (-not $acc.ContainsKey($phase)) { $acc[$phase] = @() }
                $acc[$phase] += [pscustomobject]@{ rate=[double]$Matches[1]; delay=[double]$Matches[2]; wanted=[double]$Matches[3]; jitter=[double]$Matches[5]; sat=[double]$Matches[6]; upsRate=[double]$Matches[8] }
            }
            if ($ln -match "rendersmooth slot \d+: .*CV=([0-9.]+) \| stall%=([0-9.]+) \| rotWasted=([0-9.]+)") {
                if (-not $smooth.ContainsKey($phase)) { $smooth[$phase] = @() }
                $smooth[$phase] += [pscustomobject]@{ cv=[double]$Matches[1]; stall=[double]$Matches[2]; rot=[double]$Matches[3] }
            }
        }
        foreach ($ph in $Ceilings) {
            if (-not $acc.ContainsKey($ph)) { Write-Host ("   {0,-6} no samples" -f $ph); continue }
            $s = $acc[$ph]
            $line = "   {0,-6} delay={1,5:0.0}ms wanted={2,5:0.0}ms short={3,5:0.0}ms sat={4,5:0.#}% jitter={5,4:0.0}ms underruns={6,5:0.0}/s rate={7:0.#}/s  [{8} win]" -f `
                $ph, ($s.delay|Measure-Object -Average).Average, ($s.wanted|Measure-Object -Average).Average, `
                (($s.wanted|Measure-Object -Average).Average - ($s.delay|Measure-Object -Average).Average), `
                ($s.sat|Measure-Object -Average).Average, ($s.jitter|Measure-Object -Average).Average, `
                ($s.upsRate|Measure-Object -Average).Average, ($s.rate|Measure-Object -Average).Average, $s.Count
            Write-Host $line
            if ($smooth.ContainsKey($ph)) {
                $m = $smooth[$ph]
                Write-Host ("          drawn motion: CV={0:0.00} stall%={1:0.0} rotWasted={2:0.0}deg/s" -f `
                    ($m.cv|Measure-Object -Average).Average, ($m.stall|Measure-Object -Average).Average, ($m.rot|Measure-Object -Average).Average)
            }
        }
    }
    Write-Host "==================================================================================="
    Write-Host "short = how much MORE delay the playout formula wanted than the ceiling allowed."
    Write-Host "Target: sat -> low, underruns/s -> near 0, stall% -> low, at the SMALLEST delay that gets there."
}
finally {
    foreach ($id in $pids) { Stop-Process -Id $id -Force -EA SilentlyContinue }
    Write-Host "bots stopped"
}
