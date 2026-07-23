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

Copy-Item (Join-Path $outDir "PunkMultiverse.dll") $pluginDir -Force
# Dependency DLLs (LiteNetLib for the Udp transport) ride alongside the plugin.
Copy-Item (Join-Path $outDir "LiteNetLib.dll") $pluginDir -Force -ErrorAction SilentlyContinue
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
    Copy-Item (Join-Path $outDir "PunkMultiverse.dll") $staging -Force
    Copy-Item (Join-Path $outDir "LiteNetLib.dll") $staging -Force -ErrorAction SilentlyContinue
    $zipPath = Join-Path $dist "PunkMultiverse-v$version.zip"
    Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
    Compress-Archive -Path (Join-Path $dist "staging\BepInEx") -DestinationPath $zipPath
    Remove-Item (Join-Path $dist "staging") -Recurse -Force
    Write-Host "Packaged $zipPath"
}
