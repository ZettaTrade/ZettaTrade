<#
.SYNOPSIS
The site's link-check smoke (T-259): every link and asset reference in the
built site must resolve, or the deploy does not ship.

.DESCRIPTION
Scans every .html under the built site root (the vite dist/) for href="..."
and src="..." and rules them, per class:

  - In-page anchors (#x): the referenced id must exist in the SAME document -
    the rendered manual's table of contents and the landing's section nav are
    checked like any other link.
  - Relative references (./x, ../x, x): resolved against the page's own
    directory, the target must exist inside the built site (a directory
    target resolves to its index.html).
  - Absolute site references (/ZettaTrade/...): the base is the repository's
    documented site base (vite.config.ts); the stripped path must exist
    inside the built site. An absolute reference on a foreign base is a
    deploy bug and fails.
  - External http(s) references: warned and counted, never failed - the open
    web is not this build's gate. (The stamp script HEAD-verifies its own
    stamped download URLs.)
  - javascript: or any other unknown scheme: fails.

Local links are STRICT: a built site that links at a file it did not build
must not deploy. Exit code 1 on any strict failure.

.PARAMETER DistDir
The built site root. Defaults to <repo root>/docs-site/dist.

.PARAMETER SiteBase
The site's absolute base (vite.config.ts). Defaults to /ZettaTrade/.

.EXAMPLE
pwsh scripts/check-site-links.ps1 -DistDir docs-site/dist
#>
[CmdletBinding()]
param(
    [string]$DistDir = '',
    [string]$SiteBase = '/ZettaTrade/'
)

$ErrorActionPreference = 'Stop'

if (-not $DistDir) {
    $root = git rev-parse --show-toplevel
    $DistDir = Join-Path $root 'docs-site/dist'
}
if (-not (Test-Path $DistDir)) { throw "The built site root is missing: $DistDir (run the site build first)" }

$distRoot = (Get-Item $DistDir).FullName.TrimEnd('\', '/')
$pages = @(Get-ChildItem $DistDir -Recurse -Filter '*.html' -File)
if ($pages.Count -eq 0) { throw "No .html pages found under $DistDir" }

$warnings = [System.Collections.Generic.List[string]]::new()
$failures = [System.Collections.Generic.List[string]]::new()
$linkCount = 0

foreach ($page in $pages) {
    $pagePath = $page.FullName
    $relName = $pagePath.Substring($distRoot.Length + 1).Replace('\', '/')
    $html = [System.IO.File]::ReadAllText($pagePath)

    $ids = [System.Collections.Generic.HashSet[string]]::new()
    foreach ($m in [regex]::Matches($html, '\bid="([^"]+)"')) {
        [void]$ids.Add($m.Groups[1].Value)
    }

    foreach ($m in [regex]::Matches($html, '\b(?:href|src)="([^"]*)"')) {
        $raw = $m.Groups[1].Value
        if ($raw -eq '') {
            $failures.Add("${relName}: an empty href/src") | Out-Null
            continue
        }
        $linkCount++

        if ($raw -match '^https?://') {
            $warnings.Add("${relName}: external $raw") | Out-Null
            continue
        }

        if ($raw.StartsWith('#')) {
            if ($raw.Length -eq 1) {
                $failures.Add("${relName}: a bare '#' link") | Out-Null
                continue
            }
            $anchor = $raw.Substring(1)
            if (-not $ids.Contains($anchor)) {
                $failures.Add("${relName}: the in-page anchor '$raw' has no matching id") | Out-Null
            }
            continue
        }

        if ($raw -match '^[a-zA-Z][a-zA-Z0-9+.-]*:') {
            if ($raw -match '^(mailto|tel):') {
                $warnings.Add("${relName}: scheme link $raw") | Out-Null
                continue
            }
            $failures.Add("${relName}: the link scheme is not allowed on the site: $raw") | Out-Null
            continue
        }

        $target = $raw
        if ($target.StartsWith('/')) {
            # The site's own base (vite.config.ts), stripped; the base may or
            # may not end with a slash - normalize once.
            $basePrefix = $SiteBase.TrimEnd('/')
            if (-not $target.StartsWith("$basePrefix/")) {
                $failures.Add("${relName}: the absolute link rides a foreign base: $raw") | Out-Null
                continue
            }
            $target = $target.Substring($basePrefix.Length + 1)
        }

        # Strip the fragment; the target's own existence is the check.
        $fragment = ''
        $hash = $target.IndexOf('#')
        if ($hash -ge 0) {
            $fragment = $target.Substring($hash)
            $target = $target.Substring(0, $hash)
        }
        if ($target -eq '') { continue }

        # Relative targets resolve against the PAGE's own directory, not the
        # site root - exactly what a browser does; absolute targets already
        # carry the stripped base and resolve from the root.
        $pageDir = $null
        if (-not $raw.StartsWith('/')) { $pageDir = Split-Path -Parent $relName }
        $segments = [System.Collections.Generic.List[string]]::new()
        if ($pageDir) {
            foreach ($part in ($pageDir -split '/')) { if ($part) { $segments.Add($part) | Out-Null } }
        }
        foreach ($part in ($target -split '/')) {
            if ($part -eq '' -or $part -eq '.') { continue }
            if ($part -eq '..') {
                if ($segments.Count -eq 0) {
                    $failures.Add("${relName}: the link escapes the built site: $raw") | Out-Null
                    $segments = $null
                    break
                }
                $segments.RemoveAt($segments.Count - 1)
                continue
            }
            $segments.Add($part) | Out-Null
        }
        if ($null -eq $segments) { continue }

        $resolved = Join-Path $distRoot ($segments -join '/')
        if (Test-Path -LiteralPath $resolved -PathType Container) {
            $resolved = Join-Path $resolved 'index.html'
        }
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
            $failures.Add("${relName}: the link target does not exist in the build: $raw$fragment") | Out-Null
        }
    }
}

$externalCount = @($warnings | Where-Object { $_ -match 'external ' }).Count
foreach ($w in $warnings) { Write-Warning $w }

Write-Output "pages checked: $($pages.Count); references: $linkCount; external (warn-only): $externalCount; failures: $($failures.Count)"
if ($failures.Count -gt 0) {
    foreach ($f in $failures) { Write-Output "FAIL: $f" }
    throw "The site link check failed: $($failures.Count) unresolved local reference(s)."
}
Write-Output 'SITE LINK CHECK PASSED'
