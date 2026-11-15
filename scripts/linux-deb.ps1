# T-257 - Linux .deb staging/build smoke for the release.yml linux leg.
#
# This verifies what is verifiable on this machine, mirroring the linux
# leg of .github/workflows/release.yml:
#
#   1. the version is parsed from Directory.Build.props exactly the way the
#      release pipeline parses it (the pipeline's single version source);
#      the deb Version field additionally pins the plain three-part form
#      (the Debian version grammar rides the props being plain 0.2.N);
#   2. the package tree is staged from the caller-given publish tree, on
#      the fixed contract layout: usr/lib/zettatrade/ carries the whole
#      self-contained publish output, usr/bin/zettatrade is the launcher
#      wrapper, usr/share/applications/zettatrade.desktop the menu entry,
#      usr/share/icons/hicolor/256x256/apps/zettatrade.png the icon and
#      DEBIAN/control the package manifest (generated, the fields pinned
#      below);
#   3. the control and the data-tree invariants are linted: the package
#      name, the props version, the amd64 architecture, the maintainer
#      form, the dependency floor (the self-contained runtime needs
#      glibc, fontconfig, X11 core and TLS; the desktop environment
#      carries the rest - documented in docs/SECURITY.md), the wrapper
#      exec target, the desktop entry and the PNG icon facts;
#   4. IF dpkg-deb is found: the .deb is built (--root-owner-group, the
#      release naming family zettatrade-<version>-<rid>.deb), asserted
#      non-empty and carrying the ar 'debian-binary' first member. IF
#      dpkg-deb is NOT found (a Windows dev box): STAGE-ONLY - it says
#      so plainly and exits 0; the build (plus the CI install smoke on
#      the runner) is asserted in CI, never claimed here.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\linux-deb.ps1 -OutDir <dir> -PublishDir <dir>
#
# -OutDir is caller-given by design (never a repo-relative or hardcoded
# path inside the script): the package tree lands in <OutDir>\deb-staging
# and the .deb in <OutDir> itself. Exit code 0 = the verifiable part
# passed; any step failing throws and exits nonzero.

[CmdletBinding()]
param(
    # Caller-given output directory for the staged package tree and the .deb.
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
$stagingDir = Join-Path $fullOutDir 'deb-staging'
if (Test-Path $fullOutDir) { throw "Output already exists: $fullOutDir - give a fresh output directory." }
New-Item -ItemType Directory -Path (Join-Path $stagingDir 'DEBIAN') | Out-Null

# ------------------------------------------------- version (props parse)
# Identical to the release pipeline's workflow-level parse, plus the
# plain three-part pin: the deb Version field must stay the 0.2.N form.
$props = Get-Content -Raw $propsPath
$versionPrefix = [regex]::Match($props, '<VersionPrefix>([^<>]+)</VersionPrefix>').Groups[1].Value.Trim()
$versionSuffix = [regex]::Match($props, '<VersionSuffix>([^<>]+)</VersionSuffix>').Groups[1].Value.Trim()
if (-not $versionPrefix) { throw 'Directory.Build.props carries no <VersionPrefix>.' }
$version = $versionPrefix
if ($versionSuffix) { $version = "$versionPrefix-$versionSuffix" }
if ($version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') {
    throw "The deb Version field needs the plain three-part props version, got '$version' (the props rule: plain 0.2.N, no prerelease suffix)."
}
Write-Host "Props version: $version (prefix '$versionPrefix', suffix '$versionSuffix')"

# ------------------------------------------------------------ stage the tree
$libDir = Join-Path $stagingDir 'usr/lib/zettatrade'
New-Item -ItemType Directory -Path $libDir | Out-Null
New-Item -ItemType Directory -Path (Join-Path $stagingDir 'usr/bin') | Out-Null
New-Item -ItemType Directory -Path (Join-Path $stagingDir 'usr/share/applications') | Out-Null
New-Item -ItemType Directory -Path (Join-Path $stagingDir 'usr/share/icons/hicolor/256x256/apps') | Out-Null

# The whole self-contained publish output rides the package lib dir; the
# app host keeps its name (it must stay adjacent to the managed
# ZettaTrade.App.dll it resolves).
Copy-Item -Path (Join-Path $fullPublishDir '*') -Destination $libDir -Recurse -Force

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
function Write-LfText([string]$Path, [string]$Text) {
    # Generated Unix text carries LF line endings only - CRLF wrappers or
    # control files would break the target OS outright.
    [System.IO.File]::WriteAllText($Path, ($Text -replace "`r`n", "`n"), $utf8NoBom)
}

# The launcher wrapper on the contract path: /usr/bin is the package's
# public door, the publish tree sits in the fixed package lib dir.
$wrapperText = @'
#!/bin/sh
# T-257 - the installed package launcher: exec the app host from the
# fixed package layout (usr/lib/zettatrade).
exec /usr/lib/zettatrade/ZettaTrade.App "$@"
'@
Write-LfText (Join-Path $stagingDir 'usr/bin/zettatrade') $wrapperText

# The menu entry points at the public /usr/bin door.
$desktopText = @'
[Desktop Entry]
Type=Application
Name=ZettaTrade
GenericName=Trading terminal
Comment=Multi-exchange desktop trading terminal
Exec=/usr/bin/zettatrade
Icon=zettatrade
Terminal=false
StartupNotify=false
StartupWMClass=ZettaTrade.App
Categories=Office;Finance;
Keywords=trading;crypto;stocks;terminal;
'@
Write-LfText (Join-Path $stagingDir 'usr/share/applications/zettatrade.desktop') $desktopText

# The icon on the hicolor 256x256 slot, named for the Icon value.
Copy-Item -Path $pngAsset -Destination (Join-Path $stagingDir 'usr/share/icons/hicolor/256x256/apps/zettatrade.png') -Force

# The Installed-Size field: the staged data tree in KiB, computed - never
# a guessed constant.
$installedSize = [math]::Round(((Get-ChildItem $stagingDir -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1024), 0)

# The control file: the dependency floor is what the self-contained
# runtime genuinely links (glibc, fontconfig, X11 core, TLS); the
# libssl3t64 | libssl3 alternatives chain covers the pre- and post-time64
# transition naming. The desktop environment carries the rest of the GUI
# surface (documented in docs/SECURITY.md).
$controlTemplate = @'
Package: zettatrade
Version: @@VERSION@@
Architecture: amd64
Maintainer: ZettaTrade maintainers <zettatrade@users.noreply.github.com>
Installed-Size: @@INSTALLED_SIZE@@
Depends: libc6 (>= 2.27), libfontconfig1, libx11-6, libssl3t64 | libssl3
Section: utils
Priority: optional
Homepage: https://github.com/zettatrade/zettatrade
Description: Multi-exchange desktop trading terminal
 A local-first crypto and stock trading terminal with charts, order
 books, portfolio tracking and an MCP-connected AI assistant.
 Trading safety: every order passes the risk manager; testnet and
 paper lanes by default, live venues behind the confirmation gates.
'@
$controlText = $controlTemplate.Replace('@@VERSION@@', $version).Replace('@@INSTALLED_SIZE@@', "$installedSize")
Write-LfText (Join-Path $stagingDir 'DEBIAN/control') $controlText

# ---------------------------------------------------------------- lint
function Test-DebStagingLint([string]$StagingDir, [string]$Version, [int]$ExpectedInstalledSize) {
    $problems = [System.Collections.Generic.List[string]]::new()

    $layoutPins = @(
        @{ Path = 'DEBIAN/control'; What = 'the package manifest' },
        @{ Path = 'usr/bin/zettatrade'; What = 'the launcher wrapper' },
        @{ Path = 'usr/lib/zettatrade/ZettaTrade.App'; What = 'the app host' },
        @{ Path = 'usr/lib/zettatrade/ZettaTrade.App.dll'; What = 'the managed assembly next to the host' },
        @{ Path = 'usr/share/applications/zettatrade.desktop'; What = 'the menu entry' },
        @{ Path = 'usr/share/icons/hicolor/256x256/apps/zettatrade.png'; What = 'the hicolor icon' }
    )
    foreach ($pin in $layoutPins) {
        if (-not (Test-Path (Join-Path $StagingDir $pin['Path']))) {
            $problems.Add("missing from the package tree: $($pin['What']) ($($pin['Path'])).")
        }
    }

    $controlLines = @([System.IO.File]::ReadAllLines((Join-Path $StagingDir 'DEBIAN/control')))
    $controlPins = @(
        @{ Text = 'Package: zettatrade'; What = 'the package name' },
        @{ Text = "Version: $Version"; What = 'the props version' },
        @{ Text = 'Architecture: amd64'; What = 'the amd64 architecture' },
        @{ Text = 'Maintainer: ZettaTrade maintainers <zettatrade@users.noreply.github.com>'; What = 'the maintainer form' },
        @{ Text = "Installed-Size: $ExpectedInstalledSize"; What = 'the computed installed size' },
        @{ Text = 'Section: utils'; What = 'the section' },
        @{ Text = 'Priority: optional'; What = 'the priority' },
        @{ Text = 'Homepage: https://github.com/zettatrade/zettatrade'; What = 'the homepage' },
        @{ Text = 'Description: Multi-exchange desktop trading terminal'; What = 'the description' },
        @{ Text = 'Depends: libc6 (>= 2.27), libfontconfig1, libx11-6, libssl3t64 | libssl3'; What = 'the dependency floor' }
    )
    foreach ($pin in $controlPins) {
        if ($null -eq ($controlLines | Where-Object { $_ -eq $pin['Text'] })) {
            $problems.Add("DEBIAN/control is missing: $($pin['What']).")
        }
    }
    # The description continuation lines must carry a leading space (the
    # Debian control grammar) and no line may start with a dot.
    $descriptionIndex = [array]::IndexOf($controlLines, 'Description: Multi-exchange desktop trading terminal')
    if ($descriptionIndex -lt 0) {
        $problems.Add('DEBIAN/control has no Description field.')
    }
    else {
        for ($i = $descriptionIndex + 1; $i -lt $controlLines.Count; $i++) {
            if ($controlLines[$i] -notmatch '^ ' -and $controlLines[$i] -ne '') {
                $problems.Add("DEBIAN/control continuation line is not space-prefixed: $($controlLines[$i])")
            }
            if ($controlLines[$i].StartsWith('.')) {
                $problems.Add("DEBIAN/control continuation line starts with a dot: $($controlLines[$i])")
            }
        }
    }

    $wrapperLines = @([System.IO.File]::ReadAllLines((Join-Path $StagingDir 'usr/bin/zettatrade')))
    if (@($wrapperLines).Count -lt 1 -or $wrapperLines[0] -ne '#!/bin/sh') {
        $problems.Add('usr/bin/zettatrade does not start with the #!/bin/sh shebang.')
    }
    if ($null -eq ($wrapperLines | Where-Object { $_ -eq 'exec /usr/lib/zettatrade/ZettaTrade.App "$@"' })) {
        $problems.Add('usr/bin/zettatrade does not exec the publish host on the contract path.')
    }

    $desktopLines = @([System.IO.File]::ReadAllLines((Join-Path $StagingDir 'usr/share/applications/zettatrade.desktop')))
    if ($desktopLines[0] -ne '[Desktop Entry]') {
        $problems.Add('the menu entry does not start with the [Desktop Entry] header.')
    }
    $desktopPins = @(
        @{ Text = 'Type=Application'; What = 'the entry type' },
        @{ Text = 'Name=ZettaTrade'; What = 'the entry name' },
        @{ Text = 'Exec=/usr/bin/zettatrade'; What = 'the Exec target (the public door)' },
        @{ Text = 'Icon=zettatrade'; What = 'the Icon target (the hicolor PNG)' },
        @{ Text = 'Terminal=false'; What = 'the Terminal flag' },
        @{ Text = 'StartupWMClass=ZettaTrade.App'; What = 'the StartupWMClass hint' }
    )
    foreach ($pin in $desktopPins) {
        if ($null -eq ($desktopLines | Where-Object { $_ -eq $pin['Text'] })) {
            $problems.Add("the menu entry is missing: $($pin['What']).")
        }
    }

    # The icon: the PNG signature plus the 256x256 IHDR facts.
    $pngBytes = [System.IO.File]::ReadAllBytes((Join-Path $StagingDir 'usr/share/icons/hicolor/256x256/apps/zettatrade.png'))
    if ([System.Text.Encoding]::ASCII.GetString($pngBytes, 1, 3) -ne 'PNG') {
        $problems.Add('the hicolor icon lacks the PNG signature.')
    }
    $ihdrWidth = ([int]$pngBytes[16] -shl 24) -bor ([int]$pngBytes[17] -shl 16) -bor ([int]$pngBytes[18] -shl 8) -bor [int]$pngBytes[19]
    $ihdrHeight = ([int]$pngBytes[20] -shl 24) -bor ([int]$pngBytes[21] -shl 16) -bor ([int]$pngBytes[22] -shl 8) -bor [int]$pngBytes[23]
    if ($ihdrWidth -ne 256 -or $ihdrHeight -ne 256) {
        $problems.Add("the hicolor icon is ${ihdrWidth}x${ihdrHeight}, not 256x256.")
    }

    return $problems
}

$lintProblems = @(Test-DebStagingLint $stagingDir $version $installedSize)
if ($lintProblems.Count -gt 0) {
    throw "The staged package tree lint failed:`n    " + ($lintProblems -join "`n    ")
}
Write-Host "Deb lint OK: the control fields, the contract layout, the wrapper exec target, the menu entry and the PNG icon facts all hold."

# ------------------------------------------------- dpkg-deb probe + build
$toolPath = Get-Command dpkg-deb -ErrorAction SilentlyContinue
$debName = "zettatrade-$version-linux-x64.deb"
$debPath = Join-Path $fullOutDir $debName

if (-not $toolPath) {
    Write-Host ''
    Write-Host 'STAGE-ONLY SMOKE OK.'
    Write-Host 'dpkg-deb was not found on this machine (a Linux-only tool) - the .deb CANNOT be built locally.'
    Write-Host 'The build is carried (and asserted) by the release.yml linux leg in CI, together with'
    Write-Host 'the runner install smoke; this session records the limitation instead of claiming'
    Write-Host 'the acceptance.'
    Write-Host "Staged: $stagingDir"
    Write-Host "Would archive: $debName from: $stagingDir"
    exit 0
}

Write-Host "Building: dpkg-deb --root-owner-group --build $stagingDir $debPath"
& $toolPath.Source --root-owner-group --build $stagingDir $debPath
if ($LASTEXITCODE -ne 0) { throw "dpkg-deb exited $LASTEXITCODE - the .deb build failed." }

if (-not (Test-Path $debPath)) { throw "The .deb was not produced: $debPath" }
$debFile = Get-Item $debPath
if ($debFile.Length -le 0) { throw "The .deb is empty: $debPath" }

# The deb container is an ar archive: the !<arch> magic, then the
# debian-binary first member name.
$debBytes = [System.IO.File]::ReadAllBytes($debPath)
if ($debBytes.Length -lt 24) { throw "The .deb is too small to carry the ar layout: $debPath" }
if ([System.Text.Encoding]::ASCII.GetString($debBytes, 0, 8) -ne "!<arch>`n") {
    throw "The .deb lacks the ar !<arch> magic: $debPath"
}
if ([System.Text.Encoding]::ASCII.GetString($debBytes, 8, 13) -ne 'debian-binary') {
    throw "The .deb first ar member is not debian-binary: $debPath"
}

Write-Host "Deb OK: $debName ($([math]::Round($debFile.Length / 1MB, 2)) MiB), the ar layout is present."
Write-Host ''
Write-Host 'Linux .deb smoke OK.'
exit 0
