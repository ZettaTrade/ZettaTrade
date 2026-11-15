# T-257 - Linux AppImage staging/build smoke for the release.yml linux leg.
#
# This verifies what is verifiable on this machine, mirroring the linux
# leg of .github/workflows/release.yml:
#
#   1. the version is parsed from Directory.Build.props exactly the way the
#      release pipeline parses it (the pipeline's single version source);
#   2. the AppDir is staged from the caller-given publish tree:
#      usr/bin/ carries the whole self-contained publish output plus the
#      zettatrade launcher wrapper, AppRun is the entry point, the
#      zettatrade.desktop entry and the committed mark PNG
#      (scripts/make-packaging-icons.ps1) ride the AppDir root - the
#      plain AppImageKit layout appimagetool packs as-is;
#   3. the AppDir invariants are linted: the .desktop entry text (the
#      Exec/Icon/Type/Terminal/StartupWMClass pins), the wrapper and
#      AppRun exec targets, the PNG icon facts;
#   4. IF appimagetool is found: the AppImage is built (the release
#      naming family zettatrade-<version>-<rid>.AppImage), asserted
#      non-empty and carrying the AppImage type-2 magic (AI 0x02 at
#      offset 8). FUSE-less environments first get the plain tool call,
#      then the --appimage-extract-and-run retry (the CI form). IF
#      appimagetool is NOT found (a Windows dev box): STAGE-ONLY - it
#      says so plainly and exits 0; the build is asserted in CI, never
#      claimed here.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\linux-appimage.ps1 -OutDir <dir> -PublishDir <dir>
#
# -OutDir is caller-given by design (never a repo-relative or hardcoded
# path inside the script): the AppDir lands in <OutDir>\AppDir and the
# AppImage in <OutDir> itself. Exit code 0 = the verifiable part passed;
# any step failing throws and exits nonzero.

[CmdletBinding()]
param(
    # Caller-given output directory for the staged AppDir and the AppImage.
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$OutDir,

    # The already-published self-contained linux-x64 tree (in CI the leg's
    # publish step; locally a release-dry-run.ps1 publish-linux-x64 output).
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$PublishDir
)

$ErrorActionPreference = 'Stop'

# ----------------------------------------------------------------- setup
$repoRoot = Split-Path -Parent $PSScriptRoot
$propsPath = Join-Path $repoRoot 'Directory.Build.props'
$pngAsset = Join-Path $PSScriptRoot 'assets/zettatrade-256.png'
if (-not (Test-Path $propsPath)) { throw "Directory.Build.props not found: $propsPath" }
if (-not (Test-Path $pngAsset)) { throw "The packaging PNG asset is missing: $pngAsset (scripts/make-packaging-icons.ps1 generates it)." }

# Resolve the caller-given paths against the caller's location BEFORE the
# script moves into the repository (works for relative and absolute paths
# alike, also when the directory does not exist yet).
$fullOutDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutDir)
$fullPublishDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($PublishDir)
if (-not (Test-Path $fullPublishDir)) { throw "Publish tree not found: $fullPublishDir" }
$appDir = Join-Path $fullOutDir 'AppDir'
if (Test-Path $fullOutDir) { throw "Output already exists: $fullOutDir - give a fresh output directory." }
New-Item -ItemType Directory -Path (Join-Path $appDir 'usr/bin') | Out-Null

# ------------------------------------------------- version (props parse)
# Identical to the release pipeline's workflow-level parse.
$props = Get-Content -Raw $propsPath
$versionPrefix = [regex]::Match($props, '<VersionPrefix>([^<>]+)</VersionPrefix>').Groups[1].Value.Trim()
$versionSuffix = [regex]::Match($props, '<VersionSuffix>([^<>]+)</VersionSuffix>').Groups[1].Value.Trim()
if (-not $versionPrefix) { throw 'Directory.Build.props carries no <VersionPrefix>.' }
$version = $versionPrefix
if ($versionSuffix) { $version = "$versionPrefix-$versionSuffix" }
Write-Host "Props version: $version (prefix '$versionPrefix', suffix '$versionSuffix')"

# ------------------------------------------------------------- stage the AppDir
$binDir = Join-Path $appDir 'usr/bin'
Copy-Item -Path (Join-Path $fullPublishDir '*') -Destination $binDir -Recurse -Force

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
function Write-LfText([string]$Path, [string]$Text) {
    # Generated Unix text carries LF line endings only - CRLF wrappers or
    # desktop entries would break the target OS outright.
    [System.IO.File]::WriteAllText($Path, ($Text -replace "`r`n", "`n"), $utf8NoBom)
}

# The launcher wrapper next to the host: resolves itself through the
# symlink-safe readlink chain and execs the publish host.
$wrapperText = @'
#!/bin/sh
# T-257 - the ZettaTrade launcher inside the AppDir: exec the app host
# that ships next to this wrapper in usr/bin.
HERE=$(dirname "$(readlink -f "$0")")
exec "$HERE/ZettaTrade.App" "$@"
'@
Write-LfText (Join-Path $binDir 'zettatrade') $wrapperText

# The AppImage entry point: appimagetool uses the AppDir's own AppRun
# when present, so the exec chain is pinned instead of inherited from the
# stock template.
$appRunText = @'
#!/bin/sh
# T-257 - the AppImage entry point: run the launcher wrapper that execs
# the app host.
HERE=$(dirname "$(readlink -f "$0")")
exec "$HERE/usr/bin/zettatrade" "$@"
'@
Write-LfText (Join-Path $appDir 'AppRun') $appRunText

# The desktop entry: Exec names the wrapper (the stock resolution inside
# the AppImage), Icon names the AppDir-root PNG.
$desktopText = @'
[Desktop Entry]
Type=Application
Name=ZettaTrade
GenericName=Trading terminal
Comment=Multi-exchange desktop trading terminal
Exec=zettatrade
Icon=zettatrade
Terminal=false
StartupNotify=false
StartupWMClass=ZettaTrade.App
Categories=Office;Finance;
Keywords=trading;crypto;stocks;terminal;
'@
Write-LfText (Join-Path $appDir 'zettatrade.desktop') $desktopText

# The committed mark asset rides the AppDir root, named for the Icon value.
Copy-Item -Path $pngAsset -Destination (Join-Path $appDir 'zettatrade-256.png') -Force

# ---------------------------------------------------------------- lint
function Test-AppImageStagingLint([string]$AppDir) {
    $problems = [System.Collections.Generic.List[string]]::new()

    $layoutPins = @(
        @{ Path = 'usr/bin/ZettaTrade.App'; What = 'the app host' },
        @{ Path = 'usr/bin/ZettaTrade.App.dll'; What = 'the managed assembly next to the host' },
        @{ Path = 'usr/bin/zettatrade'; What = 'the launcher wrapper' },
        @{ Path = 'AppRun'; What = 'the AppImage entry point' },
        @{ Path = 'zettatrade.desktop'; What = 'the desktop entry' },
        @{ Path = 'zettatrade-256.png'; What = 'the desktop icon' }
    )
    foreach ($pin in $layoutPins) {
        if (-not (Test-Path (Join-Path $AppDir $pin['Path']))) {
            $problems.Add("missing from the AppDir: $($pin['What']) ($($pin['Path'])).")
        }
    }

    $wrapperLines = @([System.IO.File]::ReadAllLines((Join-Path $AppDir 'usr/bin/zettatrade')))
    if (@($wrapperLines).Count -lt 1 -or $wrapperLines[0] -ne '#!/bin/sh') {
        $problems.Add('usr/bin/zettatrade does not start with the #!/bin/sh shebang.')
    }
    if ($null -eq ($wrapperLines | Where-Object { $_ -eq 'exec "$HERE/ZettaTrade.App" "$@"' })) {
        $problems.Add('usr/bin/zettatrade does not exec the publish host (ZettaTrade.App).')
    }

    $appRunLines = @([System.IO.File]::ReadAllLines((Join-Path $AppDir 'AppRun')))
    if (@($appRunLines).Count -lt 1 -or $appRunLines[0] -ne '#!/bin/sh') {
        $problems.Add('AppRun does not start with the #!/bin/sh shebang.')
    }
    if ($null -eq ($appRunLines | Where-Object { $_ -eq 'exec "$HERE/usr/bin/zettatrade" "$@"' })) {
        $problems.Add('AppRun does not exec the launcher wrapper (usr/bin/zettatrade).')
    }

    $desktopLines = @([System.IO.File]::ReadAllLines((Join-Path $AppDir 'zettatrade.desktop')))
    if ($desktopLines[0] -ne '[Desktop Entry]') {
        $problems.Add('zettatrade.desktop does not start with the [Desktop Entry] header.')
    }
    $desktopPins = @(
        @{ Text = 'Type=Application'; What = 'the entry type' },
        @{ Text = 'Name=ZettaTrade'; What = 'the entry name' },
        @{ Text = 'Exec=zettatrade'; What = 'the Exec target (the launcher wrapper)' },
        @{ Text = 'Icon=zettatrade'; What = 'the Icon target (the AppDir-root PNG)' },
        @{ Text = 'Terminal=false'; What = 'the Terminal flag' },
        @{ Text = 'StartupWMClass=ZettaTrade.App'; What = 'the StartupWMClass hint' },
        @{ Text = 'Categories=Office;Finance;'; What = 'the menu categories' }
    )
    foreach ($pin in $desktopPins) {
        if ($null -eq ($desktopLines | Where-Object { $_ -eq $pin['Text'] })) {
            $problems.Add("zettatrade.desktop is missing: $($pin['What']).")
        }
    }

    # The icon: the PNG signature plus the 256x256 IHDR facts.
    $pngBytes = [System.IO.File]::ReadAllBytes((Join-Path $AppDir 'zettatrade-256.png'))
    if ([System.Text.Encoding]::ASCII.GetString($pngBytes, 1, 3) -ne 'PNG') {
        $problems.Add('zettatrade-256.png lacks the PNG signature.')
    }
    $ihdrWidth = ([int]$pngBytes[16] -shl 24) -bor ([int]$pngBytes[17] -shl 16) -bor ([int]$pngBytes[18] -shl 8) -bor [int]$pngBytes[19]
    $ihdrHeight = ([int]$pngBytes[20] -shl 24) -bor ([int]$pngBytes[21] -shl 16) -bor ([int]$pngBytes[22] -shl 8) -bor [int]$pngBytes[23]
    if ($ihdrWidth -ne 256 -or $ihdrHeight -ne 256) {
        $problems.Add("zettatrade-256.png is ${ihdrWidth}x${ihdrHeight}, not 256x256.")
    }

    return $problems
}

$lintProblems = @(Test-AppImageStagingLint $appDir)
if ($lintProblems.Count -gt 0) {
    throw "The staged AppDir lint failed:`n    " + ($lintProblems -join "`n    ")
}
Write-Host "AppDir lint OK: the usr/bin host + wrapper, the AppRun exec chain, the desktop entry pins and the PNG icon facts all hold."

# ----------------------------------------------- appimagetool probe + build
$toolPath = Get-Command appimagetool -ErrorAction SilentlyContinue
$appImageName = "zettatrade-$version-linux-x64.AppImage"
$appImagePath = Join-Path $fullOutDir $appImageName

if (-not $toolPath) {
    Write-Host ''
    Write-Host 'STAGE-ONLY SMOKE OK.'
    Write-Host 'appimagetool was not found on this machine - the AppImage CANNOT be built locally.'
    Write-Host 'The build is carried (and asserted) by the release.yml linux leg in CI (the pinned'
    Write-Host 'AppImage/appimagetool download); this session records the limitation instead of'
    Write-Host 'claiming the acceptance.'
    Write-Host "Staged: $appDir"
    Write-Host "Would archive: $appImageName from: $appDir"
    exit 0
}

Write-Host "Building: appimagetool $appDir $appImagePath"
& $toolPath.Source $appDir $appImagePath
if (-not (Test-Path $appImagePath)) {
    # The FUSE-less environment case (the CI form): retry through the
    # runtime's extract-and-run shim, which needs no FUSE at all.
    Write-Host 'The plain call produced nothing - retrying with --appimage-extract-and-run (the FUSE-less form).'
    & $toolPath.Source --appimage-extract-and-run $appDir $appImagePath
}
if ($LASTEXITCODE -ne 0) { throw "appimagetool exited $LASTEXITCODE - the AppImage build failed." }

if (-not (Test-Path $appImagePath)) { throw "The AppImage was not produced: $appImagePath" }
$appImageFile = Get-Item $appImagePath
if ($appImageFile.Length -le 0) { throw "The AppImage is empty: $appImagePath" }

# The type-2 AppImage magic: the runtime shim sits at offset 0 and the
# 'AI' marker plus the 0x02 type byte sit at offset 8.
$appImageBytes = [System.IO.File]::ReadAllBytes($appImagePath)
if ($appImageBytes.Length -lt 12) { throw "The AppImage is too small to carry the type-2 magic: $appImagePath" }
if ($appImageBytes[8] -ne 0x41 -or $appImageBytes[9] -ne 0x49 -or $appImageBytes[10] -ne 0x02) {
    throw "The AppImage lacks the type-2 magic (AI 0x02 at offset 8): $appImagePath"
}

Write-Host "AppImage OK: $appImageName ($([math]::Round($appImageFile.Length / 1MB, 2)) MiB), the type-2 magic is present."
Write-Host ''
Write-Host 'Linux AppImage smoke OK.'
exit 0
