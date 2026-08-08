# Refuse to build a DLL that speaks a protocol its version string does not account for.
#
# The failure this prevents: protocol 23 and protocol 24 both shipping as "0.1.244", so a
# rejected player is told the two mod versions are identical and reasonably concludes there is
# nothing to update. See src/Protocol/protocol.lock for the full story.
#
# Runs from the MSBuild CheckProtocolLock target on every build, so `dotnet build` is covered
# and not just build.ps1. Exit 1 fails the build.
#
# ASCII only. PowerShell 5.1: no ternary, no ??, no &&.
[CmdletBinding()]
param(
    # The csproj <Version>, passed in by MSBuild so there is one source of truth for it.
    [Parameter(Mandatory = $true)][string]$Version
)
$ErrorActionPreference = "Stop"

$root     = Split-Path $PSScriptRoot -Parent
$lockPath = Join-Path $root "src\Protocol\protocol.lock"
$srcPath  = Join-Path $root "src\Core\NetSession.cs"

function Die($lines) {
    Write-Host ""
    Write-Host "PROTOCOL LOCK: build refused" -ForegroundColor Red
    foreach ($l in $lines) { Write-Host "  $l" }
    Write-Host ""
    exit 1
}

if (-not (Test-Path $lockPath)) { Die @("missing $lockPath") }
if (-not (Test-Path $srcPath))  { Die @("missing $srcPath") }

# --- what the code actually speaks -----------------------------------------------------------
# Anchored on `const int` so the ProtocolVersion assignments in the message builders (which read
# `ProtocolVersion = ProtocolVersion`) cannot be mistaken for the declaration.
$src = Get-Content -Raw $srcPath
$m = [regex]::Match($src, 'const\s+int\s+ProtocolVersion\s*=\s*(\d+)')
if (-not $m.Success) {
    Die @("could not find 'const int ProtocolVersion = N' in src/Core/NetSession.cs.",
          "If it was renamed or moved, update this script -- do not delete the check.")
}
$code = [int]$m.Groups[1].Value

# --- what the lock records -------------------------------------------------------------------
$entries = @()
$lineNo = 0
foreach ($line in (Get-Content $lockPath)) {
    $lineNo++
    $t = $line.Trim()
    if ($t -eq "" -or $t.StartsWith("#")) { continue }
    $e = [regex]::Match($t, '^(\d+)\s*=\s*([0-9]+(?:\.[0-9]+)*)$')
    if (-not $e.Success) { Die @("src/Protocol/protocol.lock line ${lineNo}: expected '<protocol> = <version>', got '$t'") }
    $entries += @{ Proto = [int]$e.Groups[1].Value; Ver = [version]$e.Groups[2].Value; Raw = $t }
}
if ($entries.Count -eq 0) { Die @("src/Protocol/protocol.lock has no entries") }

# --- the file must be a monotonic history ------------------------------------------------------
# Both columns strictly increase, which is what makes "you cannot reuse the previous version"
# checkable without consulting git.
for ($i = 1; $i -lt $entries.Count; $i++) {
    $prev = $entries[$i - 1]; $cur = $entries[$i]
    if ($cur.Proto -le $prev.Proto) {
        Die @("protocol numbers must increase: '$($cur.Raw)' does not come after '$($prev.Raw)'.")
    }
    if ($cur.Ver -le $prev.Ver) {
        Die @("protocol $($cur.Proto) claims version $($cur.Ver), which is not newer than protocol",
              "$($prev.Proto)'s $($prev.Ver). A protocol bump MUST move the version -- otherwise two",
              "builds speaking different protocols both introduce themselves as $($cur.Ver), which is",
              "exactly the confusion this file exists to prevent.",
              "",
              "Bump <Version> in PunkMultiverse.csproj, then set this line to that version.")
    }
}

$last = $entries[$entries.Count - 1]

# --- the code and the lock must agree ----------------------------------------------------------
if ($code -ne $last.Proto) {
    if ($code -lt $last.Proto) {
        Die @("NetSession.ProtocolVersion is $code but the lock's newest entry is $($last.Proto).",
              "Going BACKWARDS is almost certainly a bad merge. Reconcile by hand.")
    }
    $next = [version]"$($last.Ver.Major).$($last.Ver.Minor).$($last.Ver.Build + 1)"
    Die @("NetSession.ProtocolVersion is $code, but src/Protocol/protocol.lock stops at $($last.Proto).",
          "",
          "A protocol bump changes what is on the wire, so it needs a version players can see.",
          "Two builds at the same version speaking different protocols report a mismatch whose",
          "own text says the versions are identical -- the player is told to do nothing.",
          "",
          "To fix, in this order:",
          "  1. set <Version> in PunkMultiverse.csproj to $next or later",
          "     (do it by hand: the pre-commit hook only bumps on main, not on feature branches)",
          "  2. append to src/Protocol/protocol.lock:  $code = <that version>")
}

# --- the shipped version must be at or past the protocol's version ------------------------------
# Later commits move <Version> ahead of the lock, which is fine and expected. Behind it is not:
# that means the version was rolled back after the protocol moved.
$csproj = [version]$Version
if ($csproj -lt $last.Ver) {
    Die @("<Version> is $csproj but protocol $($last.Proto) was introduced at $($last.Ver).",
          "Shipping $csproj would hand players a version string OLDER than a build that already",
          "spoke this protocol, so the two are indistinguishable in the wrong direction.",
          "Set <Version> to $($last.Ver) or later.")
}

Write-Host "protocol lock OK: protocol $code, introduced at $($last.Ver), building $csproj"
exit 0
