# T-256 - lint/build smoke for the Windows installer script.
#
# This verifies what is verifiable on this machine, mirroring the win-x64
# leg of .github/workflows/release.yml:
#
#   1. the version is parsed from Directory.Build.props exactly the way the
#      release pipeline parses it (the pipeline's single version source);
#   2. one win-x64 self-contained publish from the same project the
#      workflow publishes, smoke-checked the same way (app host present,
#      the embedded version matches the props version);
#   3. a static lint of scripts/windows-installer.iss pinning the
#      invariants that matter: the dual install mode, the data-safety
#      guarantee (no [UninstallDelete]/[InstallDelete]/[UninstallRun] and
#      no reference to the profile paths anywhere outside comments), the
#      never-elevated app launch and the portable (machine-locally path
#      free) source layout;
#   4. IF Inno Setup's ISCC.exe is found (on PATH, in the standard install
#      locations or given via -ISCC): the actual compile, followed by the
#      CI-side smoke - the setup exe exists, is non-empty and carries the
#      props version in its version resource. IF ISCC is NOT found, the
#      smoke stays LINT-ONLY: it says so plainly and exits 0 - the compile
#      is then asserted in CI, never claimed here.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\windows-installer-smoke.ps1 -OutDir <dir>
#
#   optional: -ISCC <path to ISCC.exe>   (skip the standard-location probe)
#
# -OutDir is caller-given by design (never a repo-relative or hardcoded
# path inside the script): the publish tree lands in <OutDir>\publish-win-x64
# and the compiled setup exe in <OutDir> itself. Exit code 0 = the
# verifiable part passed; any step failing throws and exits nonzero.

[CmdletBinding()]
param(
    # Caller-given output directory for the publish tree and the setup exe.
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$OutDir,

    # Explicit path to ISCC.exe; when empty, PATH and the standard Inno
    # Setup install locations are probed.
    [ValidateNotNullOrEmpty()]
    [string]$ISCC = ''
)

$ErrorActionPreference = 'Stop'

# ----------------------------------------------------------------- setup
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'src/ZettaTrade.App'
$issPath = Join-Path $PSScriptRoot 'windows-installer.iss'
if (-not (Test-Path $projectPath)) { throw "Project not found: $projectPath" }
if (-not (Test-Path $issPath)) { throw "Installer script not found: $issPath" }

# Resolve the caller-given path against the caller's location BEFORE the
# script moves into the repository (works for relative and absolute paths
# alike, also when the directory does not exist yet).
$fullOutDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutDir)
if (-not (Test-Path $fullOutDir)) {
    New-Item -ItemType Directory -Path $fullOutDir -Force | Out-Null
}
$publishDir = Join-Path $fullOutDir 'publish-win-x64'
if (Test-Path $publishDir) { throw "Output already exists: $publishDir - give a fresh output directory." }

# ------------------------------------------------- version (props parse)
# Identical to the release pipeline's workflow-level parse.
$props = Get-Content -Raw (Join-Path $repoRoot 'Directory.Build.props')
$versionPrefix = [regex]::Match($props, '<VersionPrefix>([^<>]+)</VersionPrefix>').Groups[1].Value.Trim()
$versionSuffix = [regex]::Match($props, '<VersionSuffix>([^<>]+)</VersionSuffix>').Groups[1].Value.Trim()
if (-not $versionPrefix) { throw 'Directory.Build.props carries no <VersionPrefix>.' }
$version = $versionPrefix
if ($versionSuffix) { $version = "$versionPrefix-$versionSuffix" }
Write-Host "Props version: $version (prefix '$versionPrefix', suffix '$versionSuffix')"

# ------------------------------------------------------- publish (win-x64)
Write-Host "Publishing: dotnet publish src/ZettaTrade.App -c Release -r win-x64 --self-contained true -o $publishDir"
$startedAt = Get-Date
& dotnet publish $projectPath -c Release -r win-x64 --self-contained true -o $publishDir
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish exited $LASTEXITCODE - the smoke fails the way CI would fail."
}
$publishWall = [math]::Round(((Get-Date) - $startedAt).TotalSeconds, 1)
Write-Host "Publish OK in $publishWall s."

# --------------------------------------------------- publish smoke (as in CI)
$appHostPath = Join-Path $publishDir 'ZettaTrade.App.exe'
$managedDllPath = Join-Path $publishDir 'ZettaTrade.App.dll'
if (-not (Test-Path $appHostPath)) { throw 'ZettaTrade.App.exe is missing from the publish output.' }
if (-not (Test-Path $managedDllPath)) { throw 'ZettaTrade.App.dll is missing from the publish output.' }
$informational = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($managedDllPath).ProductVersion
if (-not $informational.StartsWith($version, [System.StringComparison]::Ordinal)) {
    throw "Embedded informational version '$informational' does not match the props version '$version'."
}
$files = (Get-ChildItem $publishDir -Recurse -File | Measure-Object).Count
Write-Host "Publish smoke OK: app host present, embedded version '$informational' matches '$version' ($files files published)."

# -------------------------------------------------------------- iss lint
# Static pins over the script text. Comment lines (';') are exempt from
# the path/appdata rules - the header documents the invariants there.
function Test-InstallerLint([string]$Path) {
    $problems = [System.Collections.Generic.List[string]]::new()
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if (@($bytes | Where-Object { $_ -gt 127 }).Count -gt 0) {
        $problems.Add('the script carries non-ASCII bytes (keep it ASCII-only: the PS 5.1 lint reads BOM-less files as ANSI).')
    }
    $raw = [System.IO.File]::ReadAllText($Path)
    $lines = @([System.IO.File]::ReadAllLines($Path))
    $nonComment = @($lines | Where-Object { $_.TrimStart() -notmatch '^;' -and $_.Trim() -ne '' })

    foreach ($required in @('[Setup]', '[Tasks]', '[Files]', '[Icons]', '[Run]')) {
        if ($nonComment -notcontains $required) { $problems.Add("missing section: $required.") }
    }
    foreach ($forbidden in @('[UninstallDelete]', '[InstallDelete]', '[UninstallRun]')) {
        if ($nonComment -contains $forbidden) {
            $problems.Add("FORBIDDEN section present: $forbidden - the uninstaller must never delete beyond {app} (user-data safety).")
        }
    }

    $setupChecks = @(
        @{ Pattern = '^AppId=\{\{[0-9A-F\-]+\}\}$'; What = 'AppId (stable GUID)' },
        @{ Pattern = '^AppName={#AppName}$'; What = 'AppName from the define' },
        @{ Pattern = '^AppVersion={#AppVersion}$'; What = 'AppVersion from the /D define' },
        @{ Pattern = '^DefaultDirName=\{autopf\}\\'; What = 'DefaultDirName={autopf}\ (per-mode resolution)' },
        @{ Pattern = '^PrivilegesRequired=lowest$'; What = 'per-user default (no UAC)' },
        @{ Pattern = '^PrivilegesRequiredOverridesAllowed=dialog commandline$'; What = 'per-user AND per-machine modes' },
        @{ Pattern = '^ArchitecturesAllowed=x64compatible$'; What = 'x64-compatible guard' },
        @{ Pattern = '^UninstallDisplayIcon=\{app\}'; What = 'uninstall display icon' },
        @{ Pattern = '^OutputBaseFilename=zettatrade-\{#AppVersion\}-win-x64-setup$'; What = 'artifact name in the release naming family' }
    )
    foreach ($check in $setupChecks) {
        if (@($nonComment | Where-Object { $_ -match $check.Pattern }).Count -ne 1) {
            $problems.Add("[Setup] missing or duplicated: $($check.What).")
        }
    }

    if ($raw -notmatch '#ifndef AppVersion' -or $raw -notmatch '#error "Pass /DAppVersion') {
        $problems.Add('the /DAppVersion guard is missing (the version must come from the props parse, never a baked default).')
    }
    if ($raw -notmatch '#ifndef PublishDir' -or $raw -notmatch '#error "Pass /DPublishDir') {
        $problems.Add('the /DPublishDir guard is missing.')
    }

    $filesEntries = @($nonComment | Where-Object { $_ -match '^Source: "\{#PublishDir\}\\\*"' })
    if ($filesEntries.Count -ne 1) {
        $problems.Add('[Files] must carry exactly one publish-tree entry: Source: "{#PublishDir}\*".')
    }
    else {
        if ($filesEntries[0] -notmatch 'DestDir: "\{app\}"') { $problems.Add('[Files] the publish tree must land in {app} only.') }
        if ($filesEntries[0] -notmatch 'Flags:.*ignoreversion') { $problems.Add('[Files] missing ignoreversion flag.') }
        if ($filesEntries[0] -notmatch 'Flags:.*recursesubdirs') { $problems.Add('[Files] missing recursesubdirs flag.') }
    }

    if (@($nonComment | Where-Object { $_ -match '^Name: "\{group\}\\' }).Count -ne 1) {
        $problems.Add('[Icons] missing the per-mode Start-menu entry ({group}).')
    }
    if (@($nonComment | Where-Object { $_ -match '^Name: "\{autodesktop\}\\\{#AppName\}".*Tasks: desktopicon$' }).Count -ne 1) {
        $problems.Add('[Icons] missing the desktop entry tied to the desktopicon task ({autodesktop}).')
    }
    if (@($nonComment | Where-Object { $_ -match '^Name: "desktopicon"' }).Count -ne 1) {
        $problems.Add('[Tasks] missing the desktopicon task.')
    }

    $runEntries = @($nonComment | Where-Object { $_ -match '^Filename: "\{app\}\\\{#AppExe\}"' })
    if ($runEntries.Count -ne 1) {
        $problems.Add('[Run] must carry exactly one launch entry for {app}\{#AppExe}.')
    }
    else {
        if ($runEntries[0] -notmatch 'Flags:.*runasoriginaluser') {
            $problems.Add('[Run] the launch entry must carry runasoriginaluser - the app must never start elevated.')
        }
        if ($runEntries[0] -notmatch 'Flags:.*skipifsilent') {
            $problems.Add('[Run] the launch entry must carry skipifsilent (no forced launch in silent installs).')
        }
    }

    # The profile directories must never be scripted: any non-comment
    # mention of APPDATA/LOCALAPPDATA paths is a data-safety violation.
    foreach ($line in $nonComment) {
        if ($line -match '(?i)appdata') {
            $problems.Add("non-comment line references an appdata path - the profile is out of the script's reach: $line")
        }
        if ($line -match '(?i)\b[a-z]:[\\/]') {
            $problems.Add("non-comment line carries a drive-letter path (source layout must stay portable): $line")
        }
    }

    return $problems
}

$lintProblems = @(Test-InstallerLint $issPath)
if ($lintProblems.Count -gt 0) {
    throw "windows-installer.iss lint failed:`n    " + ($lintProblems -join "`n    ")
}
Write-Host "Lint OK: sections, data-safety pins, dual mode, launch flags and portable paths all hold."

# --------------------------------------------------- ISCC probe + compile
$isccPath = $null
if ($ISCC) {
    if (-not (Test-Path $ISCC)) { throw "Given ISCC.exe not found: $ISCC" }
    $isccPath = (Resolve-Path $ISCC).Path
}
else {
    # The ProgramFiles(x86) variable is absent on 32-bit systems: probe
    # only the roots that exist.
    $roots = @(${env:ProgramFiles(x86)}, $env:ProgramFiles)
    $folders = @('Inno Setup 6', 'Inno Setup 7')
    foreach ($root in $roots) {
        if (-not $root) { continue }
        foreach ($folder in $folders) {
            $candidate = Join-Path $root (Join-Path $folder 'ISCC.exe')
            if (Test-Path $candidate) { $isccPath = $candidate; break }
        }
        if ($isccPath) { break }
    }
    if (-not $isccPath) {
        $onPath = Get-Command iscc -ErrorAction SilentlyContinue
        if ($onPath) { $isccPath = $onPath.Source }
    }
}

$setupName = "zettatrade-$version-win-x64-setup.exe"
$setupPath = Join-Path $fullOutDir $setupName

if (-not $isccPath) {
    Write-Host ''
    Write-Host 'LINT-ONLY SMOKE OK.'
    Write-Host 'ISCC.exe was not found on this machine - the installer compile CANNOT be verified locally.'
    Write-Host 'The compile is carried (and asserted) by the release.yml Windows leg in CI; this session'
    Write-Host 'records the limitation instead of claiming the acceptance.'
    exit 0
}

Write-Host "Compiling: $isccPath /DAppVersion=$version /DInstallerOutDir=$fullOutDir /DPublishDir=$publishDir"
& $isccPath "/DAppVersion=$version" "/DInstallerOutDir=$fullOutDir" "/DPublishDir=$publishDir" $issPath
if ($LASTEXITCODE -ne 0) {
    throw "ISCC exited $LASTEXITCODE - the installer compile failed."
}

if (-not (Test-Path $setupPath)) { throw "The setup exe was not produced: $setupPath" }
$setupFile = Get-Item $setupPath
if ($setupFile.Length -le 0) { throw "The setup exe is empty: $setupPath" }
$setupVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($setupPath.FullName).FileVersion
if (-not $setupVersion.StartsWith($version, [System.StringComparison]::Ordinal)) {
    throw "Setup file version '$setupVersion' does not match the props version '$version'."
}
Write-Host "Compile OK: $setupName ($([math]::Round($setupFile.Length / 1MB, 2)) MiB), file version '$setupVersion' matches '$version'."

Write-Host ''
Write-Host 'Installer smoke OK.'
exit 0
