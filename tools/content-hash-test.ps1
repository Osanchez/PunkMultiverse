# CONTENT HASH AGREEMENT: two independent installs must hash the same files identically.
#
# This is the cheapest and most valuable check in the content feature, and it needs no session,
# no lobby and no network. If two machines can disagree about the hash of identical bytes, clients
# re-download forever; if they can agree about the hash of DIFFERENT bytes, the module registry
# diverges silently and BR's drop table goes with it. Everything else assumes this holds.
#
# The comparison is deliberately over the FULL output — set digest AND every per-file digest — so
# a disagreement names the file rather than just failing.
#
# Run the same fixture on the Wine/Linux dedicated server too when one is available; the rules
# most likely to differ across platforms (separators, case, ordering, encoding) are exactly the
# ones this catches.
param(
    [string]$Content = "servecontent"
)
$ErrorActionPreference = "Stop"
$A = "C:\Program Files (x86)\Steam\steamapps\common\PUNK Playtest - OD Dev3"
$B = "C:\Program Files (x86)\Steam\steamapps\common\PUNK Playtest - OD Dev4"

function PlugOf($d) { return Join-Path $d "BepInEx\plugins\PunkMultiverse" }
function Cmd($plug,$txt){ Add-Content -Path (Join-Path $plug "devcmd.txt") -Value $txt -Encoding Ascii }
function StartGame($dir) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = Join-Path $dir "Punk.exe"; $psi.Arguments = "-batchmode -nographics"
    $psi.WorkingDirectory = $dir; $psi.UseShellExecute = $false
    foreach($k in @($psi.EnvironmentVariables.Keys | Where-Object {$_ -like "DOORSTOP*"})){ $psi.EnvironmentVariables.Remove($k) }
    return [System.Diagnostics.Process]::Start($psi).Id
}

$devRoots = @($A, $B)
if (Get-Process Punk -EA SilentlyContinue | Where-Object { $devRoots -contains (Split-Path $_.Path -Parent) }) {
    "ABORT: a DEV-install Punk.exe is already running."; exit 2
}

$pids = @()
$ok = $true
try {
    foreach ($d in $devRoots) {
        $plug = PlugOf $d
        Remove-Item -Force -EA SilentlyContinue (Join-Path $plug "devcmd.txt"), (Join-Path $plug "devout.txt")
        $pids += StartGame $d
    }
    Start-Sleep 40
    foreach ($d in $devRoots) { Cmd (PlugOf $d) "contenthash $Content" }
    Start-Sleep 12

    $outA = @(Get-Content (Join-Path (PlugOf $A) "devout.txt") -EA SilentlyContinue | Where-Object { $_ -match "contenthash:" })
    $outB = @(Get-Content (Join-Path (PlugOf $B) "devout.txt") -EA SilentlyContinue | Where-Object { $_ -match "contenthash:" })

    # Strip the timestamp prefix each Out() line carries; only the content is being compared.
    $normA = @($outA | ForEach-Object { ($_ -replace '^\[[0-9.]+\]\s*', '') })
    $normB = @($outB | ForEach-Object { ($_ -replace '^\[[0-9.]+\]\s*', '') })

    if ($normA.Count -eq 0) { Write-Host "  FAIL: install A produced no contenthash output"; $ok = $false }
    if ($normB.Count -eq 0) { Write-Host "  FAIL: install B produced no contenthash output"; $ok = $false }

    if ($ok) {
        $setA = ($normA | Where-Object { $_ -match "files=(\d+) set=([0-9a-f]+)" })[0]
        $setB = ($normB | Where-Object { $_ -match "files=(\d+) set=([0-9a-f]+)" })[0]
        Write-Host "  A  $setA"
        Write-Host "  B  $setB"
        if ($setA -ne $setB) { Write-Host "  FAIL: the two installs disagree on the SET digest"; $ok = $false }
        else { Write-Host "  set digest              MATCH" }

        if ($normA.Count -ne $normB.Count) {
            Write-Host "  FAIL: different number of lines ($($normA.Count) vs $($normB.Count))"
            $ok = $false
        } else {
            $diff = 0
            for ($i = 0; $i -lt $normA.Count; $i++) {
                if ($normA[$i] -ne $normB[$i]) {
                    if ($diff -lt 5) { Write-Host "  FAIL: line $i differs`n        A: $($normA[$i])`n        B: $($normB[$i])" }
                    $diff++
                }
            }
            if ($diff -gt 0) { Write-Host "  FAIL: $diff line(s) differ"; $ok = $false }
            else { Write-Host "  per-file digests        MATCH ($($normA.Count) lines identical)" }
        }
        if ($normA -match "UNPUBLISHABLE") { Write-Host "  FAIL: the fixture is not publishable"; $ok = $false }
    }
}
finally {
    foreach ($p in $pids) { Stop-Process -Id $p -Force -EA SilentlyContinue }
}
Write-Host "====================================================="
Write-Host $(if ($ok) { "CONTENT HASH: PASS" } else { "CONTENT HASH: PROBLEMS ABOVE" })
if (-not $ok) { exit 1 }
