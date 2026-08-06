# Dedicated-server admin over Tailscale SSH.
#
# The server is a plain `docker run` container named `punkmv` on Omar's old desktop
# (osanchez-dt), reached over Tailscale SSH. There is no control panel and no SFTP: everything
# here is `ssh <host> docker ...`, which means the only credential involved is the Tailscale SSH
# key already in ~/.ssh/config.
#
#   ./srv.ps1 state                     # container status + the mod's resolved config banner
#   ./srv.ps1 log [-Lines 200]          # recent container log
#   ./srv.ps1 log -Follow               # live tail (Ctrl-C to stop)
#   ./srv.ps1 cmd "simprof 30"          # queue a devcmd (the mod polls devcmd.txt twice a second)
#   ./srv.ps1 get <remote> [local]      # copy a file OUT of the container (paths are container-side)
#   ./srv.ps1 put <local> <remote>      # copy a file IN, CRLF-stripped (scp from Windows adds them)
#   ./srv.ps1 config                    # show the operator overrides file that actually wins
#   ./srv.ps1 restart | stop | start
#   ./srv.ps1 recreate                  # pull the latest image and rebuild the container
#
# ASCII only.
param(
    [Parameter(Position = 0)][string]$Action = "state",
    [Parameter(Position = 1, ValueFromRemainingArguments = $true)][string[]]$Rest,
    [int]$Lines = 120,
    [switch]$Follow,
    [string]$SshHost = "punkdt",          # ~/.ssh/config alias for osanchez-dt over Tailscale
    [string]$Container = "punkmv"
)
# NOT "Stop". Docker and the container both write benign text to stderr — image-pull progress, and
# Xvfb's "X connection to :0 broken" as an OLD instance shuts down — and with Stop set, PowerShell
# promotes any of it to a terminating error and the command dies half-done. `srv.ps1 state` failed
# exactly this way while the server it was reporting on was perfectly healthy.
$ErrorActionPreference = "Continue"

$PluginDir = "/home/container/BepInEx/plugins/PunkMultiverse"
$Overrides = "/home/container/server.cfg"

# Tailscale SSH prints a post-quantum advisory on every connection; it is noise, not an error.
function RemoteRun([string]$cmd) {
    # ssh.exe, not ssh: PowerShell matches command names case-insensitively, so a function
    # named Ssh would resolve `ssh` back to itself and recurse until the call stack blows.
    $out = & ssh.exe $SshHost $cmd 2>&1
    $out | Where-Object { $_ -notmatch "post-quantum|store now, decrypt later|openssh\.com/pq|^\s*\*\*" }
}

switch ($Action) {
    "state" {
        # JSON, never a --format template: the remote shell is cmd.exe, which splits Go templates
        # on their '|' and mangles the braces. Ask for raw JSON and parse it on THIS side.
        $json = (RemoteRun "docker inspect $Container") -join "`n"
        if (-not $json.Trim().StartsWith("[")) { Write-Host "no container '$Container':`n$json"; exit 1 }
        $c = ($json | ConvertFrom-Json)[0]
        $ports = ($c.NetworkSettings.Ports.PSObject.Properties |
                  ForEach-Object { "$($_.Name) -> $($_.Value.HostPort -join ',')" }) -join "  "
        Write-Host ("{0} | {1} | {2} | {3}" -f $c.Name.TrimStart('/'), $c.Config.Image, $c.State.Status, $ports)
        Write-Host "`n--- resolved config (what the mod actually read) ---"
        # Filter locally too. The mod prints this block on every boot: settings differing from
        # defaults, then a warning for every key on disk it no longer reads. Trust it over the
        # start-server.sh banner, which echoes its own shell variables and cannot see the
        # overrides file that is applied after it.
        RemoteRun "docker logs --tail 600 $Container" |
            Select-String -Pattern '\[Config\]|game mode:' |
            Select-Object -Last 30 | ForEach-Object { $_.Line }
    }
    "log" {
        if ($Follow) { & ssh.exe $SshHost "docker logs -f --tail $Lines $Container" }
        else { RemoteRun "docker logs --tail $Lines $Container" }
    }
    "cmd" {
        $line = ($Rest -join " ").Trim()
        if (-not $line) { Write-Host "usage: srv.ps1 cmd `"<devcmd>`""; exit 2 }
        # Append via `tee -a` over STDIN rather than a `>>` redirect. The remote shell is cmd.exe
        # and PowerShell drops the embedded quotes on the way, so cmd would grab the `>>` for
        # itself and fail with "The system cannot find the path specified". Piping needs no quoting
        # at all. Append, never overwrite: the mod truncates the file once it runs the batch.
        $line | & ssh.exe $SshHost "docker exec -i $Container tee -a $PluginDir/devcmd.txt" |
            Out-Null
        Write-Host "sent: $line"
    }
    "get" {
        if (-not $Rest -or $Rest.Count -lt 1) { Write-Host "usage: srv.ps1 get <remote> [local]"; exit 2 }
        $remote = $Rest[0]
        $local = if ($Rest.Count -ge 2) { $Rest[1] } else { Split-Path -Leaf $remote }
        $staging = "C:/Users/omar/_srvget_" + (Split-Path -Leaf $remote)
        RemoteRun "docker cp ${Container}:$remote `"$staging`""
        & scp.exe "${SshHost}:$staging" $local 2>&1 |
            Where-Object { $_ -notmatch "post-quantum|store now|openssh\.com/pq|^\s*\*\*" }
        RemoteRun "cmd /c del `"$($staging -replace '/','\')`"" | Out-Null
        Write-Host "got: $remote -> $local"
    }
    "put" {
        if (-not $Rest -or $Rest.Count -lt 2) { Write-Host "usage: srv.ps1 put <local> <remote>"; exit 2 }
        $local = $Rest[0]; $remote = $Rest[1]
        if (-not (Test-Path $local)) { Write-Host "no such file: $local"; exit 2 }
        $staging = "C:/Users/omar/_srvput_" + (Split-Path -Leaf $local)
        & scp.exe $local "${SshHost}:$staging" 2>&1 |
            Where-Object { $_ -notmatch "post-quantum|store now|openssh\.com/pq|^\s*\*\*" }
        RemoteRun "docker cp `"$staging`" ${Container}:$remote"
        # scp from Windows carries CRLF into a Linux container; the shell reads a trailing \r as
        # part of the value and silently sets nonsense. Strip it on arrival, every time.
        # Unquoted on purpose — the sed expression contains no spaces, so it survives cmd.exe
        # intact, whereas a quoted `sh -c '...'` loses its quotes in transit.
        RemoteRun "docker exec $Container sed -i s/\r`$// $remote"
        RemoteRun "cmd /c del `"$($staging -replace '/','\')`"" | Out-Null
        Write-Host "put: $local -> $remote (CRLF stripped)"
    }
    "config" {
        # THE file that decides the server's settings. start-server.sh applies it LAST, so it beats
        # both that script's defaults and any -e variable on the container.
        RemoteRun "docker exec $Container cat $Overrides"
    }
    { $_ -in "restart", "stop", "start" } {
        RemoteRun "docker $Action $Container"
        Write-Host "$Action`: $Container"
    }
    "recreate" {
        # The full run line. Only these five -e vars are ours; everything else in `docker inspect`
        # (PATH, WINEPREFIX, DISPLAY, HOME, ...) comes from the image and must NOT be re-passed.
        # Deliberately no BR_* variables: match tuning lives in server.cfg, and a second source of
        # truth for it silently loses to that file.
        RemoteRun "docker pull osanchezdev/punk-punkmultiverse:latest"
        RemoteRun "docker rm -f $Container"
        RemoteRun ("docker run -d --name $Container --restart unless-stopped -p 7778:7778/udp " +
             "-v punkmv-data:/home/container " +
             "-e ENABLE_ADMIN_COMMANDS=1 -e SERVER_PORT=7778 -e SERVER_ADDRESS=192.168.1.226 " +
             "-e GAME_MODE=BattleRoyale -e LOG_LEVEL=Verbose " +
             "osanchezdev/punk-punkmultiverse:latest")
        Write-Host "recreated $Container on the latest image"
    }
    default { Write-Host "usage: srv.ps1 state|log|cmd|get|put|config|restart|stop|start|recreate" }
}
