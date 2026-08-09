<#
.SYNOPSIS
  Build Punk Multiverse and deploy it to the game's BepInEx plugins folder.
.EXAMPLE
  powershell -File build.ps1              # Release build + deploy
  powershell -File build.ps1 -Debug       # Debug build (with pdb) + deploy
  powershell -File build.ps1 -Zip         # Release build + deploy + dist zip
  powershell -File build.ps1 -GameDir "D:\PunkCopy"   # deploy to a copied game install
#>
param(
    [switch]$Debug,
    [switch]$Zip,
    [string]$GameDir = ""
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
if (-not $GameDir) { $GameDir = (Resolve-Path (Join-Path $root "..")).Path }

# Wire up the repo's git hooks (version auto-bump on commit) — safe no-op outside a clone.
try { git -C $root config core.hooksPath .githooks 2>$null | Out-Null } catch { }

$config = if ($Debug) { "Debug" } else { "Release" }
$csproj = Join-Path $root "PunkMultiverse.csproj"

Write-Host "Building $config against game at: $GameDir"
dotnet build $csproj -c $config -p:GameDir=$GameDir --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$outDir = Join-Path $root "bin\$config"
$pluginDir = Join-Path $GameDir "BepInEx\plugins\PunkMultiverse"
New-Item -ItemType Directory -Force $pluginDir | Out-Null

Copy-Item (Join-Path $outDir "merged\PunkMultiverse.dll") $pluginDir -Force
# mod.json is the distribution manifest PUNK Nexus reads out of the install to know what version
# is on disk. It has to travel with the DLL, locally and in the zip alike.
$modJson = Join-Path $root "mod.json"
if (Test-Path $modJson) { Copy-Item $modJson $pluginDir -Force }
# LiteNetLib is MERGED into PunkMultiverse.dll (ILRepack) - remove any stale standalone copy.
Remove-Item (Join-Path $pluginDir "LiteNetLib.dll") -Force -ErrorAction SilentlyContinue
if ($Debug) {
    Copy-Item (Join-Path $outDir "PunkMultiverse.pdb") $pluginDir -Force
} else {
    Remove-Item (Join-Path $pluginDir "PunkMultiverse.pdb") -Force -ErrorAction SilentlyContinue
}
Write-Host "Deployed to $pluginDir"

if ($Zip) {
    [xml]$proj = Get-Content $csproj
    $version = $proj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    $dist = Join-Path $root "dist"
    New-Item -ItemType Directory -Force $dist | Out-Null
    $staging = Join-Path $dist "staging\BepInEx\plugins\PunkMultiverse"
    New-Item -ItemType Directory -Force $staging | Out-Null
    Copy-Item (Join-Path $outDir "merged\PunkMultiverse.dll") $staging -Force  # LiteNetLib merged in

    # The published manifest and the built version must agree, or the client offers an update that
    # installs the same build (or worse, hides one that exists). The pre-commit hook keeps these in
    # step; this catches the case where it did not run.
    if (-not (Test-Path $modJson)) { throw "mod.json is missing - PUNK Nexus cannot list this mod without it." }
    $mj = Get-Content $modJson -Raw | ConvertFrom-Json
    if ($mj.version -ne $version) {
        throw "mod.json version '$($mj.version)' does not match the csproj <Version> '$version'. Update mod.json (or run the pre-commit hook) before packaging."
    }
    if (-not $mj.gameVersion) { throw "mod.json has no 'gameVersion'; PUNK Nexus gates installs on it." }

    # And it must be the game we actually built against. PUNK Nexus gates installs on an EXACT
    # string match, so a stale gameVersion does not degrade gracefully -- it makes the mod
    # uninstallable for everyone, with a message blaming the author for not publishing a build.
    # That is what happened when the game went 0.12.10 -> 0.12.11: every mod in the catalog went
    # dark at once, this one included, and nothing in the build said a word.
    #
    # Warn rather than throw when the version cannot be read (CI builds against a reference bundle,
    # not an install, and must not fail for that) -- but a MISMATCH is always fatal, because that is
    # the case that ships a dead listing.
    # Read it the SAME way PUNK Nexus does, or the two can disagree and this check would pass while
    # the gate still refuses. Not Punk.exe's ProductVersion -- that is the Unity version
    # ("6000.3.4f1"), not the game's. Unity bakes bundleVersion into Punk_Data/globalgamemanagers as
    # a length-prefixed string; take the dotted-numeric one with the most components.
    $gameVersion = $null
    $ggm = Join-Path $GameDir "Punk_Data\globalgamemanagers"
    if (Test-Path $ggm) {
        $bytes = [System.IO.File]::ReadAllBytes($ggm)
        # PlayerSettings sits near the front (bundleVersion is at ~offset 1964 here), so a 64KB
        # window is generous and keeps this off the build's critical path.
        $scanEnd = [Math]::Min($bytes.Length, 65536)
        $bestParts = -1
        for ($i = 0; $i -lt $scanEnd - 4; $i++) {
            $len = [BitConverter]::ToInt32($bytes, $i)
            if ($len -lt 1 -or $len -gt 64 -or ($i + 4 + $len) -gt $bytes.Length) { continue }
            $ok = $true
            for ($j = 0; $j -lt $len; $j++) {
                $c = $bytes[$i + 4 + $j]
                # digits (48-57) or '.' (46). Testing "$c -lt 48" first would reject the dot.
                if (-not ((($c -ge 48) -and ($c -le 57)) -or ($c -eq 46))) { $ok = $false; break }
            }
            if (-not $ok) { continue }
            $s = [System.Text.Encoding]::ASCII.GetString($bytes, $i + 4, $len)
            $parts = ($s -split '\.').Count
            if ($parts -ge 2 -and $parts -gt $bestParts) { $bestParts = $parts; $gameVersion = $s }
        }
    }
    if (-not $gameVersion) {
        Write-Host "  note: no game version readable under '$GameDir' - cannot verify mod.json gameVersion '$($mj.gameVersion)'"
    }
    elseif ($mj.gameVersion -ne $gameVersion) {
        throw "mod.json gameVersion '$($mj.gameVersion)' but the game here is '$gameVersion'. PUNK Nexus matches this EXACTLY, so publishing would make the mod uninstallable. Update mod.json (and re-check the mod still works on $gameVersion)."
    }
    else {
        Write-Host "  mod.json gameVersion $gameVersion matches the game built against"
    }
    Copy-Item $modJson $staging -Force

    $zipPath = Join-Path $dist "PunkMultiverse-v$version.zip"
    Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
    Compress-Archive -Path (Join-Path $dist "staging\BepInEx") -DestinationPath $zipPath
    Remove-Item (Join-Path $dist "staging") -Recurse -Force
    Write-Host "Packaged $zipPath"
}
