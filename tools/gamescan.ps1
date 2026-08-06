<#
.SYNOPSIS
    Detect what a game update changed, and whether any of it can break the mod.

.DESCRIPTION
    Steam updates the base game underneath us. When something starts misbehaving after an
    update, the question is never "what changed in the game" (hundreds of things do) but
    "what changed that we depend on" — usually a dozen or fewer.

    This wraps tools/gamescan:
      * hashes every member of Punk.Main.dll (signature AND normalized IL body),
      * extracts the members the mod actually depends on from the COMPILED mod DLL,
      * diffs against the committed baseline and reports only what intersects.

    Committed:   gamescan/baseline.json, gamescan/contract.json, docs/api/**
    Local only:  gamescan/cache/** (decompiled game source), gamescan/reports/**
                 — the repo is public; game source is never committed.

.EXAMPLE
    tools\gamescan.ps1
    Scan the installed game against the baseline and write a report.

.EXAMPLE
    tools\gamescan.ps1 -Accept
    Same, then promote the new manifest to the baseline (do this once you have reviewed
    the report and updated the mod for anything it flagged).

.EXAMPLE
    tools\gamescan.ps1 -Index
    Regenerate the per-area API index under docs/api/.
#>
[CmdletBinding()]
param(
    # Promote the scanned manifest to gamescan/baseline.json after reporting.
    [switch]$Accept,

    # Regenerate docs/api/ from the current game assembly.
    [switch]$Index,

    # Regenerate src/Core/GameBaseline.g.cs, the compact baseline the mod checks at boot.
    [switch]$Guard,

    # Report identifiers the curated docs claim that the game assembly no longer declares.
    [switch]$DocCheck,

    # Skip the decompile step (faster; the report loses its ready-to-run git diff commands).
    [switch]$NoDecompile,

    # Game install to scan. Defaults to the install this repo lives inside.
    [string]$GameDir
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
if (-not $GameDir) { $GameDir = Split-Path -Parent $repo }

$gameDll  = Join-Path $GameDir 'Punk_Data\Managed\Punk.Main.dll'
$modDll   = Join-Path $repo 'bin\Release\PunkMultiverse.dll'
$toolProj = Join-Path $repo 'tools\gamescan\GameScan.csproj'
$toolExe  = Join-Path $repo 'tools\gamescan\bin\Release\net8.0\gamescan.exe'

$scanDir     = Join-Path $repo 'gamescan'
$baseline    = Join-Path $scanDir 'baseline.json'
$contract    = Join-Path $scanDir 'contract.json'
$cacheRoot   = Join-Path $scanDir 'cache'
$reportsRoot = Join-Path $scanDir 'reports'

if (-not (Test-Path $gameDll)) { throw "game assembly not found: $gameDll" }

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Warn($msg) { Write-Host "    $msg" -ForegroundColor Yellow }

# --- game build id ------------------------------------------------------------------------
# The assembly carries no game version (it is always 0.0.0.0) and the exe's ProductVersion is
# the Unity version, not the build. Steam's appmanifest buildid is the only stable identifier
# for "which build of the game is installed".
function Get-GameVersion {
    $steamApps = Split-Path -Parent (Split-Path -Parent $GameDir)
    $manifest = Join-Path $steamApps 'appmanifest_2850470.acf'
    if (Test-Path $manifest) {
        $m = Select-String -Path $manifest -Pattern '"buildid"\s+"(\d+)"' | Select-Object -First 1
        if ($m) { return "steam-$($m.Matches[0].Groups[1].Value)" }
    }
    $sha = (Get-FileHash -Path $gameDll -Algorithm SHA256).Hash.Substring(0, 12).ToLower()
    Write-Warn "no Steam appmanifest found; identifying this build by DLL hash instead"
    return "dll-$sha"
}

$version = Get-GameVersion
Write-Step "game build: $version"

# --- build the tool -----------------------------------------------------------------------
$needBuild = $true
if (Test-Path $toolExe) {
    $newestSource = Get-ChildItem (Join-Path $repo 'tools\gamescan') -Filter *.cs |
                    Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($newestSource -and $newestSource.LastWriteTime -lt (Get-Item $toolExe).LastWriteTime) {
        $needBuild = $false
    }
}
if ($needBuild) {
    Write-Step 'building tools/gamescan'
    dotnet build $toolProj -c Release -v q --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'gamescan tool failed to build' }
}

# --- decompile cache ----------------------------------------------------------------------
# Keeps a readable copy of each scanned build so the report can point at a real diff.
$cacheDir = Join-Path $cacheRoot $version
if (-not $NoDecompile -and -not (Test-Path (Join-Path $cacheDir 'Punk.Main.csproj'))) {
    $ilspy = $null
    $cmd = Get-Command ilspycmd -ErrorAction SilentlyContinue
    if ($cmd) { $ilspy = $cmd.Source }
    elseif (Test-Path "$env:USERPROFILE\.dotnet\tools\ilspycmd.exe") { $ilspy = "$env:USERPROFILE\.dotnet\tools\ilspycmd.exe" }

    if ($ilspy) {
        Write-Step "decompiling to gamescan/cache/$version"
        New-Item -ItemType Directory -Force -Path $cacheDir | Out-Null
        # -r is not optional. Without the Unity assemblies on the reference path ILSpy cannot
        # tell a class from a struct, and emits defensive casts plus "Unknown result type"
        # comments throughout. Those are decompiler artifacts, not code changes — but they
        # make a cache-to-cache diff unreadable, burying the one real change in twenty lines
        # of noise. Passing it explicitly also means a -GameDir pointing at a staged copy
        # still produces a comparable cache.
        $managed = Join-Path $GameDir 'Punk_Data\Managed'
        & $ilspy -p -o $cacheDir -r $managed $gameDll | Out-Null
        if ($LASTEXITCODE -ne 0) { Write-Warn 'ilspycmd reported a failure; the cache may be incomplete' }
    }
    else {
        Write-Warn 'ilspycmd not found (dotnet tool install -g ilspycmd) — skipping decompile'
    }
}

# --- manifest + contract ------------------------------------------------------------------
New-Item -ItemType Directory -Force -Path $scanDir, $reportsRoot | Out-Null
$scanned = Join-Path $reportsRoot "manifest-$version.json"

Write-Step 'hashing game assembly'
& $toolExe manifest --dll $gameDll --out $scanned --game-version $version
if ($LASTEXITCODE -ne 0) { throw 'manifest extraction failed' }

if (Test-Path $modDll) {
    Write-Step 'extracting mod contract surface'
    & $toolExe contract --mod $modDll --out $contract
    if ($LASTEXITCODE -ne 0) { throw 'contract extraction failed' }
}
elseif (-not (Test-Path $contract)) {
    throw "no mod DLL at $modDll and no committed contract.json — build the mod first (build.ps1)"
}
else {
    Write-Warn 'mod DLL not built; reusing the committed contract.json'
}

# --- API index ----------------------------------------------------------------------------
if ($Index) {
    Write-Step 'generating docs/api'
    & $toolExe index --manifest $scanned --out-dir (Join-Path $repo 'docs\api')
    if ($LASTEXITCODE -ne 0) { throw 'index generation failed' }
}

# --- documentation drift --------------------------------------------------------------------
if ($DocCheck) {
    Write-Step 'checking docs against the game assembly'
    & $toolExe doccheck --manifest $scanned --docs-dir (Join-Path $repo 'docs') --src-dir (Join-Path $repo 'src')
}

# --- runtime guard ------------------------------------------------------------------------
if ($Guard) {
    Write-Step 'generating src/Core/GameBaseline.g.cs'
    & $toolExe guard --manifest $scanned --contract $contract --out (Join-Path $repo 'src\Core\GameBaseline.g.cs')
    if ($LASTEXITCODE -ne 0) { throw 'guard generation failed' }
    Write-Warn 'rebuild the mod so the new baseline is compiled in (build.ps1)'
}

# --- first run: adopt as baseline ----------------------------------------------------------
if (-not (Test-Path $baseline)) {
    Write-Step 'no baseline yet — adopting this scan as the baseline'
    Copy-Item $scanned $baseline -Force
    Write-Host ''
    Write-Host "Baseline written to gamescan/baseline.json ($version). Commit it." -ForegroundColor Green
    return
}

# --- diff ---------------------------------------------------------------------------------
$reportMd   = Join-Path $reportsRoot "report-$version.md"
$reportJson = Join-Path $reportsRoot "report-$version.json"

# Deliberately NOT ConvertFrom-Json: it lower-cases nothing but compares keys case-insensitively,
# and the game genuinely declares members differing only in case (AIAgent.seeker / .Seeker), which
# makes it throw on DuplicateKeysInJsonString. Only one value is needed, and Assembly is written
# before Types, so it is always near the top.
$baseVersion = $null
foreach ($line in (Get-Content $baseline -TotalCount 40)) {
    if ($line -match '"GameVersion":\s*"([^"]+)"') { $baseVersion = $Matches[1]; break }
}
if (-not $baseVersion) { throw "could not read GameVersion from $baseline" }
$baseCache   = Join-Path $cacheRoot $baseVersion

Write-Step "diffing $baseVersion -> $version"
$diffArgs = @(
    'diff',
    '--before', $baseline,
    '--after', $scanned,
    '--contract', $contract,
    '--out-md', $reportMd,
    '--out-json', $reportJson
)
if ((Test-Path $baseCache) -and (Test-Path $cacheDir)) {
    $diffArgs += @('--cache-before', "gamescan/cache/$baseVersion", '--cache-after', "gamescan/cache/$version")
}

& $toolExe @diffArgs
$verdict = $LASTEXITCODE

Write-Host ''
switch ($verdict) {
    0 { Write-Host 'CLEAN — nothing the mod depends on changed.' -ForegroundColor Green }
    3 { Write-Host 'BEHAVIOUR CHANGED — same signatures, different code, in members the mod uses.' -ForegroundColor Yellow
        Write-Host 'Nothing will warn you at runtime. Read the report.' -ForegroundColor Yellow }
    4 { Write-Host 'BREAKING — members the mod patches changed shape. Harmony will throw at load.' -ForegroundColor Red }
    default { throw "diff failed (exit $verdict)" }
}
Write-Host "report: $reportMd"

if ($Accept) {
    Copy-Item $scanned $baseline -Force
    Write-Host "baseline promoted to $version — commit gamescan/baseline.json" -ForegroundColor Green
}
elseif ($verdict -ne 0) {
    Write-Host 'once the mod is updated for the above, re-run with -Accept to move the baseline.'
}

exit $verdict
