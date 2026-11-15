# T-257 - macOS .dmg staging/build smoke for the release.yml osx legs.
#
# This verifies what is verifiable on this machine, mirroring the osx legs
# of .github/workflows/release.yml:
#
#   1. the version is parsed from Directory.Build.props exactly the way the
#      release pipeline parses it (the pipeline's single version source);
#   2. the app bundle ZettaTrade.app is staged from the caller-given
#      publish tree: Contents/MacOS/ carries the whole self-contained
#      publish output, Contents/Info.plist is generated (the keys pinned
#      below) and Contents/Resources/ZettaTrade.icns is the committed
#      packaging mark asset (scripts/make-packaging-icons.ps1);
#   3. the bundle layout and the Info.plist invariants are linted: the
#      host names, the bundle identifier, the version strings (the props
#      version, verbatim), the system floor and the icon container facts;
#   4. IF hdiutil is found (a macOS runner): the executable bit is pinned
#      on the app host, the /Applications drag target symlink is added
#      next to the bundle and the .dmg is built (UDZO, the release
#      naming family zettatrade-<version>-<rid>.dmg), asserted non-empty
#      and carrying the UDIF 'koly' trailer. IF hdiutil is NOT found (a
#      Windows dev box): STAGE-ONLY - it says so plainly and exits 0;
#      the build is asserted in CI, never claimed here.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\macos-dmg.ps1 -OutDir <dir> -PublishDir <dir>
#
#   optional: -Rid osx-x64|osx-arm64 (default osx-arm64; the artifact name only)
#
# -OutDir is caller-given by design (never a repo-relative or hardcoded
# path inside the script): the bundle lands in <OutDir>\dmg-root\ZettaTrade.app
# and the .dmg in <OutDir> itself. Exit code 0 = the verifiable part
# passed; any step failing throws and exits nonzero.

[CmdletBinding()]
param(
    # Caller-given output directory for the staged bundle and the .dmg.
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$OutDir,

    # The already-published self-contained osx RID tree (in CI the leg's
    # publish step; locally a release-dry-run.ps1 publish-<rid> output).
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$PublishDir,

    # The RID in the artifact name, matching the release.yml matrix leg.
    [ValidateSet('osx-x64', 'osx-arm64')]
    [string]$Rid = 'osx-arm64'
)

$ErrorActionPreference = 'Stop'

# ----------------------------------------------------------------- setup
$repoRoot = Split-Path -Parent $PSScriptRoot
$propsPath = Join-Path $repoRoot 'Directory.Build.props'
$icnsAsset = Join-Path $PSScriptRoot 'assets/zettatrade.icns'
if (-not (Test-Path $propsPath)) { throw "Directory.Build.props not found: $propsPath" }
if (-not (Test-Path $icnsAsset)) { throw "The packaging icns asset is missing: $icnsAsset (scripts/make-packaging-icons.ps1 generates it)." }

# Resolve the caller-given paths against the caller's location BEFORE the
# script moves into the repository (works for relative and absolute paths
# alike, also when the directory does not exist yet).
$fullOutDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutDir)
$fullPublishDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($PublishDir)
if (-not (Test-Path $fullPublishDir)) { throw "Publish tree not found: $fullPublishDir" }
$dmgRoot = Join-Path $fullOutDir 'dmg-root'
$bundleRoot = Join-Path $dmgRoot 'ZettaTrade.app'
if (Test-Path $fullOutDir) { throw "Output already exists: $fullOutDir - give a fresh output directory." }
New-Item -ItemType Directory -Path $bundleRoot | Out-Null

# ------------------------------------------------- version (props parse)
# Identical to the release pipeline's workflow-level parse.
$props = Get-Content -Raw $propsPath
$versionPrefix = [regex]::Match($props, '<VersionPrefix>([^<>]+)</VersionPrefix>').Groups[1].Value.Trim()
$versionSuffix = [regex]::Match($props, '<VersionSuffix>([^<>]+)</VersionSuffix>').Groups[1].Value.Trim()
if (-not $versionPrefix) { throw 'Directory.Build.props carries no <VersionPrefix>.' }
$version = $versionPrefix
if ($versionSuffix) { $version = "$versionPrefix-$versionSuffix" }
Write-Host "Props version: $version (prefix '$versionPrefix', suffix '$versionSuffix')"

# ------------------------------------------------------------ stage the bundle
$macosDir = Join-Path $bundleRoot 'Contents/MacOS'
$resourcesDir = Join-Path $bundleRoot 'Contents/Resources'
New-Item -ItemType Directory -Path $macosDir | Out-Null
New-Item -ItemType Directory -Path $resourcesDir | Out-Null

# The whole self-contained publish output rides Contents/MacOS: the app
# host keeps its name (it must stay adjacent to the managed ZettaTrade.App.dll
# it resolves), the bundled runtime and every asset come along.
Copy-Item -Path (Join-Path $fullPublishDir '*') -Destination $macosDir -Recurse -Force

# The Info.plist is generated with the props version verbatim. The
# placeholder form keeps the template free of string interpolation.
$plistTemplate = @'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key>
  <string>ZettaTrade</string>
  <key>CFBundleDisplayName</key>
  <string>ZettaTrade</string>
  <key>CFBundleIdentifier</key>
  <string>io.github.zettatrade.ZettaTrade</string>
  <key>CFBundleExecutable</key>
  <string>ZettaTrade.App</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleInfoDictionaryVersion</key>
  <string>6.0</string>
  <key>CFBundleShortVersionString</key>
  <string>@@VERSION@@</string>
  <key>CFBundleVersion</key>
  <string>@@VERSION@@</string>
  <key>CFBundleIconFile</key>
  <string>ZettaTrade</string>
  <key>LSMinimumSystemVersion</key>
  <string>12.0</string>
  <key>LSApplicationCategoryType</key>
  <string>public.app-category.finance</string>
  <key>NSHighResolutionCapable</key>
  <true/>
</dict>
</plist>
'@
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$plistPath = Join-Path $bundleRoot 'Contents/Info.plist'
[System.IO.File]::WriteAllText($plistPath, ($plistTemplate.Replace('@@VERSION@@', $version)), $utf8NoBom)

# The committed mark asset rides Contents/Resources, named for the
# CFBundleIconFile value (Finder resolves ZettaTrade -> ZettaTrade.icns).
Copy-Item -Path $icnsAsset -Destination (Join-Path $resourcesDir 'ZettaTrade.icns') -Force

# ---------------------------------------------------------------- lint
function Test-DmgStagingLint([string]$BundleRoot, [string]$Version) {
    $problems = [System.Collections.Generic.List[string]]::new()

    $layoutPins = @(
        @{ Path = 'Contents/MacOS/ZettaTrade.App'; What = 'the app host' },
        @{ Path = 'Contents/MacOS/ZettaTrade.App.dll'; What = 'the managed assembly next to the host' },
        @{ Path = 'Contents/Info.plist'; What = 'the bundle manifest' },
        @{ Path = 'Contents/Resources/ZettaTrade.icns'; What = 'the bundle icon' }
    )
    foreach ($pin in $layoutPins) {
        if (-not (Test-Path (Join-Path $BundleRoot $pin['Path']))) {
            $problems.Add("missing from the bundle: $($pin['What']) ($($pin['Path'])).")
        }
    }

    $plistText = [System.IO.File]::ReadAllText((Join-Path $BundleRoot 'Contents/Info.plist'))
    try {
        $plistXml = [xml]$plistText
    }
    catch {
        $problems.Add('Contents/Info.plist is not valid XML.')
    }
    # ASCII-only keeps every reader (plutil on macOS, the lint here) on
    # the same page.
    if ($plistText -match '[^\x00-\x7F]') {
        $problems.Add('Contents/Info.plist carries non-ASCII bytes.')
    }
    $plistLines = @([System.IO.File]::ReadAllLines((Join-Path $BundleRoot 'Contents/Info.plist')))
    $plistPins = @(
        @{ Text = '<key>CFBundleExecutable</key>'; What = 'CFBundleExecutable key' },
        @{ Text = '<string>ZettaTrade.App</string>'; What = 'CFBundleExecutable value (the publish host name)' },
        @{ Text = '<string>io.github.zettatrade.ZettaTrade</string>'; What = 'CFBundleIdentifier value' },
        @{ Text = '<string>APPL</string>'; What = 'CFBundlePackageType value' },
        @{ Text = '<key>CFBundleIconFile</key>'; What = 'CFBundleIconFile key' },
        @{ Text = '<string>12.0</string>'; What = 'LSMinimumSystemVersion value (the .NET 10 macOS floor)' },
        @{ Text = '<string>public.app-category.finance</string>'; What = 'LSApplicationCategoryType value' },
        @{ Text = '<key>NSHighResolutionCapable</key>'; What = 'NSHighResolutionCapable key' }
    )
    foreach ($pin in $plistPins) {
        if (@($plistLines | Where-Object { $_.Trim() -eq $pin['Text'] }).Count -lt 1) {
            $problems.Add("Info.plist is missing: $($pin['What']).")
        }
    }
    foreach ($key in @('CFBundleShortVersionString', 'CFBundleVersion')) {
        $block = "<key>$key</key>"
        $valueLine = "<string>$Version</string>"
        if (-not $plistText.Contains($block) -or -not $plistText.Contains($valueLine)) {
            $problems.Add("Info.plist is missing the $key value carrying the props version ($Version).")
        }
    }

    # The icon container: the modern PNG-frame icns facts.
    $icnsStaged = Join-Path $BundleRoot 'Contents/Resources/ZettaTrade.icns'
    if (Test-Path $icnsStaged) {
        $icnsBytes = [System.IO.File]::ReadAllBytes($icnsStaged)
        if ([System.Text.Encoding]::ASCII.GetString($icnsBytes, 0, 4) -ne 'icns') {
            $problems.Add('ZettaTrade.icns lacks the icns magic.')
        }
        $icnsTotal = [int](([uint32]$icnsBytes[4] -shl 24) -bor ([uint32]$icnsBytes[5] -shl 16) -bor ([uint32]$icnsBytes[6] -shl 8) -bor [uint32]$icnsBytes[7])
        if ($icnsTotal -ne $icnsBytes.Length) {
            $problems.Add("ZettaTrade.icns total length $icnsTotal does not match the file length $($icnsBytes.Length).")
        }
        $offset = 8
        $types = @()
        while ($offset -lt $icnsBytes.Length) {
            $types += [System.Text.Encoding]::ASCII.GetString($icnsBytes, $offset, 4)
            $entryLength = [int](([uint32]$icnsBytes[$offset + 4] -shl 24) -bor ([uint32]$icnsBytes[$offset + 5] -shl 16) -bor ([uint32]$icnsBytes[$offset + 6] -shl 8) -bor [uint32]$icnsBytes[$offset + 7])
            if ($entryLength -lt 8) { $problems.Add("ZettaTrade.icns entry carries an impossible length: $entryLength."); break }
            $offset += $entryLength
        }
        foreach ($required in @('ic07', 'ic08')) {
            if ($types -notcontains $required) { $problems.Add("ZettaTrade.icns is missing the $required frame.") }
        }
    }

    return $problems
}

$lintProblems = @(Test-DmgStagingLint $bundleRoot $version)
if ($lintProblems.Count -gt 0) {
    throw "The staged bundle lint failed:`n    " + ($lintProblems -join "`n    ")
}
Write-Host "Bundle lint OK: Contents/MacOS host + managed assembly, Info.plist pins, the icns container facts all hold."

# --------------------------------------------------- hdiutil probe + build
$hdiutilPath = Get-Command hdiutil -ErrorAction SilentlyContinue
$dmgName = "zettatrade-$version-$Rid.dmg"
$dmgPath = Join-Path $fullOutDir $dmgName

if (-not $hdiutilPath) {
    Write-Host ''
    Write-Host 'STAGE-ONLY SMOKE OK.'
    Write-Host 'hdiutil was not found on this machine (a macOS-only tool) - the .dmg CANNOT be built locally.'
    Write-Host 'The build is carried (and asserted) by the release.yml osx legs in CI; this session'
    Write-Host 'records the limitation instead of claiming the acceptance.'
    Write-Host "Staged: $bundleRoot"
    Write-Host "Would archive: $dmgName from: $dmgRoot"
    exit 0
}

# The macOS-only finishing: the executable bit on the app host and the
# drag-to-Applications target next to the bundle.
$hostPath = Join-Path $macosDir 'ZettaTrade.App'
& chmod 755 $hostPath
if ($LASTEXITCODE -ne 0) { throw "chmod exited $LASTEXITCODE - the app host executable bit could not be pinned." }
$applicationsLink = Join-Path $dmgRoot 'Applications'
if (-not (Test-Path $applicationsLink)) {
    New-Item -ItemType SymbolicLink -Path $applicationsLink -Target '/Applications' | Out-Null
}
if (-not (Test-Path $applicationsLink)) { throw 'The /Applications drag target symlink is missing from the dmg root.' }

Write-Host "Building: hdiutil create -volname ZettaTrade -srcfolder $dmgRoot -ov -format UDZO $dmgPath"
& $hdiutilPath.Source create -volname ZettaTrade -srcfolder $dmgRoot -ov -format UDZO $dmgPath
if ($LASTEXITCODE -ne 0) { throw "hdiutil exited $LASTEXITCODE - the .dmg build failed." }

if (-not (Test-Path $dmgPath)) { throw "The .dmg was not produced: $dmgPath" }
$dmgFile = Get-Item $dmgPath
if ($dmgFile.Length -le 0) { throw "The .dmg is empty: $dmgPath" }

# The UDIF trailer: every hdiutil image ends with a 512-byte block that
# starts with the 'koly' magic - a format-level presence assert.
$dmgBytes = [System.IO.File]::ReadAllBytes($dmgPath)
if ($dmgBytes.Length -lt 512) { throw "The .dmg is too small to carry the UDIF trailer: $dmgPath" }
$tailStart = $dmgBytes.Length - 512
$tailMagic = [System.Text.Encoding]::ASCII.GetString($dmgBytes, $tailStart, 4)
if ($tailMagic -ne 'koly') { throw "The .dmg lacks the UDIF 'koly' trailer (the tail reads '$tailMagic'): $dmgPath" }

Write-Host "Dmg OK: $dmgName ($([math]::Round($dmgFile.Length / 1MB, 2)) MiB), the UDIF trailer is present."
Write-Host ''
Write-Host 'macOS .dmg smoke OK.'
exit 0
