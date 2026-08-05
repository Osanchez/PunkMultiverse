# PUBLIC PATH SMOKE TEST — join the live server the way a tester will, over the internet.
#
# Everything else we run talks to the server on the LAN or the tailnet. Neither proves the thing a
# playtest depends on: that the playit.gg UDP tunnel is up and forwarding. The tunnel agent logs
# only errors, so "quiet" is indistinguishable from "wedged" — the only honest check is to dial the
# public address with the exact build a tester runs and see a join land.
#
#   ./public-join-test.ps1
#   ./public-join-test.ps1 -Address 192.168.1.226:7778     # compare against the direct path
#
# Uses the OD Dev3 install as the client. ASCII only.
param(
    [string]$Address  = "punk-mv.playit.game:17201",
    [string]$ClientDir = "C:\Program Files (x86)\Steam\steamapps\common\PUNK Playtest - OD Dev3",
    [int]$TimeoutSeconds = 90
)
$ErrorActionPreference = "Stop"
$Plug = Join-Path $ClientDir "BepInEx\plugins\PunkMultiverse"
$Log  = Join-Path $ClientDir "BepInEx\LogOutput.log"

function CountIn($p, $pat) {
    if (-not (Test-Path $p)) { return 0 }
    return @(Select-String -Path $p -Pattern $pat -AllMatches -EA SilentlyContinue).Count
}
function Lines($p, $pat) {
    if (-not (Test-Path $p)) { return @() }
    return @(Select-String -Path $p -Pattern $pat -AllMatches -EA SilentlyContinue)
}

if (Get-Process Punk -EA SilentlyContinue | Where-Object { (Split-Path $_.Path -Parent) -eq $ClientDir }) {
    "ABORT: that install is already running."; exit 2
}

# Transport/address only. AutoStart=Join makes it dial on its own, so no UI driving is needed.
$cfgPath = Join-Path $Plug "config.cfg"
$backup  = Get-Content -Raw $cfgPath
try {
    $host_, $port = $Address -split ":", 2
    $cfg = $backup
    foreach ($kv in @(@{k="Transport";v="Udp"}, @{k="UdpAddress";v=$host_}, @{k="UdpPort";v=$port},
                      @{k="AutoStart";v="Join"}, @{k="AutoReady";v="true"}, @{k="LogLevel";v="Verbose"},
                      @{k="CommandFile";v="devcmd.txt"})) {
        $pat = "(?m)^{0}\s*=.*$" -f [regex]::Escape($kv.k)
        $cfg = $cfg -replace $pat, ("{0} = {1}" -f $kv.k, $kv.v)
    }
    [System.IO.File]::WriteAllText($cfgPath, $cfg)
    Remove-Item -Force -EA SilentlyContinue $Log, (Join-Path $Plug "devcmd.txt")

    Write-Host "dialing $Address with $(Split-Path $ClientDir -Leaf) ..."
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = Join-Path $ClientDir "Punk.exe"; $psi.Arguments = "-batchmode -nographics"
    $psi.WorkingDirectory = $ClientDir; $psi.UseShellExecute = $false
    $psi.EnvironmentVariables["PUNKMV_BR_CHOOSE_SPAWN"] = "0"
    foreach ($k in @($psi.EnvironmentVariables.Keys | Where-Object { $_ -like "DOORSTOP*" })) {
        $psi.EnvironmentVariables.Remove($k)
    }
    $proc = [System.Diagnostics.Process]::Start($psi)

    # "welcomed as slot N" is the handshake completing: the packet went out over the tunnel, the
    # server answered, and the version check passed. Anything less is not a join.
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $joined = $false
    while ((Get-Date) -lt $deadline) {
        if ((CountIn $Log "welcomed as slot") -ge 1) { $joined = $true; break }
        if ((CountIn $Log "Version mismatch") -ge 1) { break }
        Start-Sleep 3
    }

    Write-Host ""
    Write-Host "=============== PUBLIC PATH ==============="
    Write-Host ("  address              {0}" -f $Address)
    if ($joined) {
        $w = Lines $Log "welcomed as slot (\d+)"
        Write-Host ("  join                 OK - {0}" -f ($w[0].Line -replace '.*Punk Multiverse\] ',''))
        $v = Lines $Log "Punk Multiverse v([0-9.]+)"
        if ($v.Count -ge 1) { Write-Host ("  client build         v{0}" -f $v[0].Matches[0].Groups[1].Value) }
        $rtt = Lines $Log "\[Udp\].*rtt[= ]([0-9]+)"
        if ($rtt.Count -ge 1) { Write-Host ("  rtt                  {0} ms" -f $rtt[-1].Matches[0].Groups[1].Value) }
        Write-Host "PUBLIC JOIN: PASS - the tunnel is forwarding and testers can connect"
    } else {
        $mismatch = Lines $Log "Version mismatch.*"
        if ($mismatch.Count -ge 1) {
            Write-Host ("  join                 REJECTED - {0}" -f ($mismatch[0].Line -replace '.*Punk Multiverse\] ',''))
            Write-Host "PUBLIC JOIN: FAIL - the tunnel works, the BUILD does not match"
        } else {
            Write-Host "  join                 TIMED OUT - no welcome inside $TimeoutSeconds s"
            Write-Host "PUBLIC JOIN: FAIL - nothing came back through the tunnel"
        }
    }
    Write-Host "==========================================="
}
finally {
    Get-Process Punk -EA SilentlyContinue |
        Where-Object { (Split-Path $_.Path -Parent) -eq $ClientDir } |
        ForEach-Object { $_.Kill() }
    # config.cfg PERSISTS and these installs get played on; never let a test outlive itself.
    [System.IO.File]::WriteAllText($cfgPath, $backup)
    Write-Host "client config restored"
}
