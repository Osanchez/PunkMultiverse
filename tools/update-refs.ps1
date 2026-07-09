# tools/update-refs.ps1
# Refresh the CI reference bundle (after a game update or when the csproj gains a reference):
# regenerates punkmultiverse-refs.zip from the local install and uploads it to the private
# Osanchez/PunkMods-refs 'refs' release (same release the Mods repo uses; different asset name).
#
#   powershell -ExecutionPolicy Bypass -File tools\update-refs.ps1
#
# Requires the GitHub CLI (gh) authenticated with write access to the refs repo (native or WSL).
param(
    [string]$GameDir,
    [string]$RefsRepo = 'Osanchez/PunkMods-refs'
)
$ErrorActionPreference = 'Stop'
$Root = Split-Path $PSScriptRoot -Parent

& (Join-Path $PSScriptRoot 'make-refs.ps1') -GameDir $GameDir
$zip = Join-Path $Root 'punkmultiverse-refs.zip'
if (-not (Test-Path $zip)) { throw "punkmultiverse-refs.zip was not produced" }

Write-Host "`nUploading punkmultiverse-refs.zip to $RefsRepo (refs release)..." -ForegroundColor Cyan
if (Get-Command gh -ErrorAction SilentlyContinue) {
    gh release upload refs "$zip" --repo $RefsRepo --clobber
}
elseif (Get-Command wsl -ErrorAction SilentlyContinue) {
    $wslZip = (wsl wslpath -a "$zip").Trim()
    wsl gh release upload refs "$wslZip" --repo $RefsRepo --clobber
}
else {
    throw "gh not found (native or WSL). Upload manually: gh release upload refs `"$zip`" --repo $RefsRepo --clobber"
}
if ($LASTEXITCODE -ne 0) { throw "Upload failed (exit $LASTEXITCODE)." }
Write-Host "Done. CI will use the new bundle on the next push." -ForegroundColor Green
