# tools/make-refs.ps1
# Builds punkmultiverse-refs.zip: the game/BepInEx DLLs this project references, laid out
# gamedir-shaped (BepInEx\core + Punk_Data\Managed) so CI can build without a game install.
# The DLL list is discovered from PunkMultiverse.csproj HintPaths — add a <Reference>, rerun this.
param(
    [string]$GameDir
)
$ErrorActionPreference = 'Stop'
$Root = Split-Path $PSScriptRoot -Parent
if (-not $GameDir) { $GameDir = (Resolve-Path (Join-Path $Root '..')).Path }

$csproj = Get-Content (Join-Path $Root 'PunkMultiverse.csproj') -Raw
$managed = [System.Collections.Generic.HashSet[string]]::new()
$core = [System.Collections.Generic.HashSet[string]]::new()
foreach ($m in [regex]::Matches($csproj, '<HintPath>\$\(ManagedDir\)\\(.+?\.dll)</HintPath>'))  { [void]$managed.Add($m.Groups[1].Value) }
foreach ($m in [regex]::Matches($csproj, '<HintPath>\$\(BepInExCore\)\\(.+?\.dll)</HintPath>')) { [void]$core.Add($m.Groups[1].Value) }

$staging = Join-Path $Root 'refs-staging'
if (Test-Path $staging) { [System.IO.Directory]::Delete($staging, $true) }
$managedDst = Join-Path $staging 'Punk_Data\Managed'
$coreDst = Join-Path $staging 'BepInEx\core'
New-Item -ItemType Directory -Force $managedDst, $coreDst | Out-Null

foreach ($dll in $managed) {
    $src = Join-Path $GameDir "Punk_Data\Managed\$dll"
    if (-not (Test-Path $src)) { throw "Missing $src" }
    Copy-Item $src $managedDst
}
foreach ($dll in $core) {
    $src = Join-Path $GameDir "BepInEx\core\$dll"
    if (-not (Test-Path $src)) { throw "Missing $src" }
    Copy-Item $src $coreDst
}

$zip = Join-Path $Root 'punkmultiverse-refs.zip'
Remove-Item $zip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip
[System.IO.Directory]::Delete($staging, $true)
Write-Host "Bundled $($managed.Count) managed + $($core.Count) core DLLs -> $zip"
