# T-125 - local dry run of the release packaging pipeline: one RID.
#
# This mirrors the per-leg steps of .github/workflows/release.yml so the
# acceptance agent can verify the same pipeline locally without pushing
# a tag: the version is parsed from the committed Directory.Build.props
# (the workflow's single version source), the matching CHANGELOG.md
# section must exist, one RID is published self-contained from the same
# project the workflow publishes, and the publish output is smoke
# checked the same way (app host present, the embedded version matches
# the props version). The script does NOT archive and does NOT need
# a tag; it PRINTS what it would archive.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\release-dry-run.ps1 -OutDir <dir>
#
#   optional: -Rid win-x64|osx-x64|osx-arm64|linux-x64 (default win-x64)
#
# -OutDir is caller-given by design (never a repo-relative or hardcoded
# path inside the script): the publish tree lands in <OutDir>\publish-<rid>.
# Exit code 0 = the dry run succeeded; any step failing throws and exits
# nonzero (a broken build fails this dry run loudly, as in CI).

[CmdletBinding()]
param(
    # Caller-given output directory for the publish tree.
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$OutDir,

    # One RID per run, mirroring one release.yml matrix leg.
    [ValidateSet('win-x64', 'osx-x64', 'osx-arm64', 'linux-x64')]
    [string]$Rid = 'win-x64',

    # The publish target, kept to the app host project for a bounded
    # dry-run time (identical to the project the workflow publishes).
    [ValidateNotNullOrEmpty()]
    [string]$Project = 'src/ZettaTrade.App'
)

$ErrorActionPreference = 'Stop'

# ----------------------------------------------------------------- setup
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot $Project
if (-not (Test-Path $projectPath)) { throw "Project not found: $projectPath" }

# Resolve the caller-given path against the caller's location BEFORE the
# script moves into the repository (works for relative and absolute
# paths alike, also when the directory does not exist yet).
$fullOutDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutDir)
if (-not (Test-Path $fullOutDir)) {
    New-Item -ItemType Directory -Path $fullOutDir -Force | Out-Null
}
$publishDir = Join-Path $fullOutDir "publish-$Rid"
if (Test-Path $publishDir) { throw "Output already exists: $publishDir - give a fresh output directory." }

# Archive extension map, identical to the workflow's matrix.
$archiveExt = @{
    'win-x64'   = '.zip'
    'osx-x64'   = '.zip'
    'osx-arm64' = '.zip'
    'linux-x64' = '.tar.gz'
}

# ------------------------------------------------- version (props parse)
$props = Get-Content -Raw (Join-Path $repoRoot 'Directory.Build.props')
$versionPrefix = [regex]::Match($props, '<VersionPrefix>([^<>]+)</VersionPrefix>').Groups[1].Value.Trim()
$versionSuffix = [regex]::Match($props, '<VersionSuffix>([^<>]+)</VersionSuffix>').Groups[1].Value.Trim()
if (-not $versionPrefix) { throw 'Directory.Build.props carries no <VersionPrefix>.' }
$version = $versionPrefix
if ($versionSuffix) { $version = "$versionPrefix-$versionSuffix" }
Write-Host "Props version: $version (prefix '$versionPrefix', suffix '$versionSuffix')"

# ----------------------------------------------- release notes (excerpt)
$changelogLines = @(Get-Content (Join-Path $repoRoot 'CHANGELOG.md'))
$inside = $false
$notesCount = 0
$notesHeader = $null
foreach ($line in $changelogLines) {
    if ($line -match '^## \[') {
        if ($inside) { break }
        if ($line.Contains("[$version]")) { $inside = $true; $notesCount++; $notesHeader = $line }
    }
    elseif ($inside) { $notesCount++ }
}
if (-not $inside) {
    throw "CHANGELOG.md carries no '## [$version]' section - in the real pipeline the tag commit must add it."
}
Write-Host "Release notes source: $notesHeader ($notesCount lines)"

# ------------------------------------------------------- publish (1 RID)
Write-Host "Publishing: dotnet publish $Project -c Release -r $Rid --self-contained true -o $publishDir"
$startedAt = Get-Date
& dotnet publish $projectPath -c Release -r $Rid --self-contained true -o $publishDir
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish exited $LASTEXITCODE - the dry run fails the way CI would fail."
}
$publishWall = [math]::Round(((Get-Date) - $startedAt).TotalSeconds, 1)
Write-Host "Publish OK in $publishWall s."

# --------------------------------------------------- smoke (as in CI)
$appHost = 'ZettaTrade.App'
if ($Rid -like 'win-*') { $appHost = 'ZettaTrade.App.exe' }
$appHostPath = Join-Path $publishDir $appHost
$managedDllPath = Join-Path $publishDir 'ZettaTrade.App.dll'
if (-not (Test-Path $appHostPath)) { throw "$appHost is missing from the publish output." }
if (-not (Test-Path $managedDllPath)) { throw 'ZettaTrade.App.dll is missing from the publish output.' }
# The embedded informational version is the props version with the baked
# source-revision tail ("0.1.0-alpha.77+<commit sha>"). The local probe
# (Windows PowerShell) reads it from the version resource; the CI leg
# reads the same string from the assembly's informational-version
# attribute (cross-OS). The ordinal prefix asserts the props-version
# identity in both paths.
$informational = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($managedDllPath).ProductVersion
if (-not $informational.StartsWith($version, [System.StringComparison]::Ordinal)) {
    throw "Embedded informational version '$informational' does not match the props version '$version'."
}
$files = (Get-ChildItem $publishDir -Recurse -File | Measure-Object).Count
Write-Host "Smoke OK: $appHost present, embedded version '$informational' matches '$version' ($files files published)."

# -------------------------------------------------------- what to ship
$archiveName = "zettatrade-$version-$Rid$($archiveExt[$Rid])"
Write-Host ''
Write-Host "Would archive: $archiveName"
Write-Host "  from: $publishDir"
Write-Host "Dry run OK."
exit 0
