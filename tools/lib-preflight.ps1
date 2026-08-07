# PREFLIGHT: refuse to run a test against the wrong game build.
#
# Dot-source this and call Assert-GameBuild with every install the test will launch.
#
# WHY THIS EXISTS. The dev installs are COPIES, made once and never updated; only the
# Steam-managed install gets patched. On 2026-08-07 that gap had reached five weeks, and a whole
# day of content-sync testing ran against a game build nobody was playing any more. Nothing
# looked wrong: the harnesses passed, the mod's own [GameScan] line was in a log nobody grepped,
# and every result described a game that was no longer current.
#
# A test that quietly measures the wrong build is worse than one that fails, because it produces
# evidence. So this aborts rather than warns.
#
# The reference is the STEAM-MANAGED install (the parent of this repo), because that is the one
# Steam keeps correct. gamescan/baseline.json is checked too when it is present: it catches the
# other direction, where Steam has moved ahead of a baseline nobody has re-accepted, which is a
# real state and means the mod's contract has not been re-verified against what is installed.

function Get-GameBuildHash([string]$installDir) {
    $dll = Join-Path $installDir "Punk_Data\Managed\Punk.Main.dll"
    if (-not (Test-Path $dll)) { return $null }
    return (Get-FileHash -Algorithm SHA256 $dll).Hash.Substring(0, 12).ToLower()
}

function Get-SteamBuildId() {
    # steamapps/common/<install>/.. -> steamapps/appmanifest_2850470.acf
    $manifest = "C:\Program Files (x86)\Steam\steamapps\appmanifest_2850470.acf"
    if (-not (Test-Path $manifest)) { return $null }
    $m = Select-String -Path $manifest -Pattern '"buildid"\s+"(\d+)"' | Select-Object -First 1
    if ($m) { return $m.Matches[0].Groups[1].Value }
    return $null
}

<#
.SYNOPSIS
    Abort unless every install is on the same game build as the Steam-managed one.
.PARAMETER Installs
    Every install the test will launch, including the coordinator.
.PARAMETER Reference
    The install to trust. Defaults to the Steam-managed "PUNK Playtest".
.PARAMETER WarnOnly
    Report and continue instead of exiting. For a run that DELIBERATELY targets an old build.
#>
function Assert-GameBuild {
    param(
        [Parameter(Mandatory = $true)][string[]]$Installs,
        [string]$Reference = "C:\Program Files (x86)\Steam\steamapps\common\PUNK Playtest",
        [switch]$WarnOnly
    )

    $refHash = Get-GameBuildHash $Reference
    if (-not $refHash) {
        Write-Host "PREFLIGHT: cannot read the reference game build at $Reference" -ForegroundColor Red
        if (-not $WarnOnly) { exit 3 }
        return
    }

    $bad = @()
    foreach ($i in ($Installs | Select-Object -Unique)) {
        $h = Get-GameBuildHash $i
        if (-not $h) { $bad += "$i : no Punk.Main.dll"; continue }
        if ($h -ne $refHash) { $bad += ("{0} : {1} (reference {2})" -f (Split-Path -Leaf $i), $h, $refHash) }
    }

    $buildId = Get-SteamBuildId
    $label = if ($buildId) { "steam-$buildId / $refHash" } else { $refHash }

    if ($bad.Count -eq 0) {
        Write-Host "preflight: game build $label on all $($Installs.Count) install(s)"
    }
    else {
        Write-Host ""
        Write-Host "PREFLIGHT FAILED - these installs are NOT on the current game build:" -ForegroundColor Red
        foreach ($b in $bad) { Write-Host "  $b" -ForegroundColor Red }
        Write-Host ""
        Write-Host "The dev installs are copies; only the Steam one is patched. Refresh them with:" -ForegroundColor Yellow
        Write-Host "  robocopy `"$Reference`" `"<install>`" /MIR /XD `"$Reference\BepInEx`"" -ForegroundColor Yellow
        Write-Host "(/XD BepInEx keeps each install's mod, config, WeaponForge and content.)" -ForegroundColor Yellow
        Write-Host ""
        Write-Host "Running anyway would produce results about a build nobody plays. Pass -WarnOnly" -ForegroundColor Yellow
        Write-Host "to the harness only when testing an old build is the actual point." -ForegroundColor Yellow
        if (-not $WarnOnly) { exit 3 }
    }

    # The other direction: Steam has moved and the mod's baseline has not been re-accepted, so
    # nobody has checked whether the update touched anything the mod depends on.
    $baseline = Join-Path (Split-Path -Parent $PSScriptRoot) "gamescan\baseline.json"
    if ($buildId -and (Test-Path $baseline)) {
        $m = Select-String -Path $baseline -Pattern '"GameVersion"\s*:\s*"steam-(\d+)"' | Select-Object -First 1
        if ($m -and $m.Matches[0].Groups[1].Value -ne $buildId) {
            Write-Host ("preflight: WARNING - gamescan baseline is steam-{0} but Steam has {1}. " -f
                $m.Matches[0].Groups[1].Value, $buildId) -ForegroundColor Yellow
            Write-Host "           Run tools\gamescan.ps1 to see whether the update touched anything the mod uses." -ForegroundColor Yellow
        }
    }
}
