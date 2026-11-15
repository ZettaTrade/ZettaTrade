<#
.SYNOPSIS
Stamp the landing pages' direct download links from the latest release's own
asset list (T-259).

.DESCRIPTION
The landing pages carry a marked download list (data-zt-dl="...") whose
committed state points at the Releases page - release-agnostic source. This
script, run AFTER the site build against the dist/ output, resolves the
latest release through GET /repos/{owner}/{repo}/releases/latest and stamps
every marked link - href AND visible text - with the exact artifact the
release carries: the four per-OS archives (zettatrade-<version>-win-x64.zip,
-osx-x64.zip, -osx-arm64.zip, -linux-x64.tar.gz) and the release's own
zettatrade-<version>-sha256.txt, plus the version into the
data-zt-release-version span. Every stamped URL is HEAD-verified to answer
200 before the run succeeds - the "every download link lands on the matching
latest artifact" gate, self-checked.

No release yet (the API answers 404 - the honest pending state, mirroring
generate-metrics.ps1): the pages keep their committed Releases-page links
and the run succeeds with a note. Any other API failure, a release missing
one of the five artifacts, a failed HEAD check or a missing landing file
fails the run loudly - nothing is guessed, nothing half-stamped.

.PARAMETER DistDir
The built site root (the vite dist/). Defaults to <repo root>/docs-site/dist.

.PARAMETER Repo
The OWNER/REPOSITORY slug. Defaults to the origin remote's slug.

.PARAMETER Token
A GitHub token (raises the rate limit). Defaults to $env:GITHUB_TOKEN.

.EXAMPLE
pwsh scripts/stamp-landing-downloads.ps1 -DistDir docs-site/dist
#>
[CmdletBinding()]
param(
    [string]$DistDir = '',
    [string]$Repo = '',
    [string]$Token = ''
)

$ErrorActionPreference = 'Stop'

if (-not $Repo) {
    $origin = git remote get-url origin
    if ($origin -notmatch '[:/]([^/]+/[^/.]+?)(?:\.git)?$') {
        throw "Cannot derive the repository slug from the origin remote: $origin"
    }
    $Repo = $Matches[1]
}
if (-not $DistDir) {
    $root = git rev-parse --show-toplevel
    $DistDir = Join-Path $root 'docs-site/dist'
}

$headers = @{
    'User-Agent' = 'zettatrade-landing-stamp'
    'Accept'     = 'application/vnd.github+json'
}
if (-not $Token -and $env:GITHUB_TOKEN) { $Token = $env:GITHUB_TOKEN }
if ($Token) { $headers['Authorization'] = "Bearer $Token" }

$landingFiles = @(
    (Join-Path $DistDir 'landing/index.html'),
    (Join-Path $DistDir 'landing/index.ru.html')
)
foreach ($file in $landingFiles) {
    if (-not (Test-Path $file)) { throw "The built landing page is missing: $file (run the site build first)" }
}

# The latest release: drafts and pre-releases are never "latest" (the API's
# own rule), so the landing always stamps the newest stable artifact set.
$release = $null
try {
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/latest" -Headers $headers
}
catch {
    $message = "$($_.Exception.Message)"
    if ($message -match '\(404\)') {
        Write-Output "No release published yet (the API answered 404): the landing keeps its committed Releases-page links - the honest pending state."
        return
    }
    throw "The GitHub API call for the latest release failed: $message"
}

$version = $release.tag_name
if ($version.StartsWith('v')) { $version = $version.Substring(1) }

# The T-125/T-256 delivery pipeline's own names, by RID:
$ridAssetName = @{
    'win-x64'   = "zettatrade-$version-win-x64.zip"
    'osx-x64'   = "zettatrade-$version-osx-x64.zip"
    'osx-arm64' = "zettatrade-$version-osx-arm64.zip"
    'linux-x64' = "zettatrade-$version-linux-x64.tar.gz"
    'sums'      = "zettatrade-$version-sha256.txt"
}

$assets = @($release.assets)
$stampPlan = @()
foreach ($slot in $ridAssetName.Keys) {
    $name = $ridAssetName[$slot]
    $asset = $assets | Where-Object { $_.name -eq $name } | Select-Object -First 1
    if (-not $asset) {
        throw "The latest release ($($release.tag_name)) carries no '$name' asset - refusing to half-stamp the landing."
    }
    $stampPlan += @{ Slot = $slot; Name = $name; Url = $asset.browser_download_url }
}

# The gate: every stamped URL must answer HEAD 200 before anything is
# written (GitHub serves release assets through a redirect to the download
# CDN; the HEAD check follows it like a browser would).
foreach ($entry in $stampPlan) {
    try {
        $head = Invoke-WebRequest -Uri $entry.Url -Method Head -Headers $headers -UseBasicParsing
        if ($head.StatusCode -ne 200) {
            throw "The stamped URL answered HTTP $($head.StatusCode): $($entry.Url)"
        }
    }
    catch {
        throw "The HEAD check failed for '$($entry.Name)': $($_.Exception.Message)"
    }
}
Write-Output "Latest release $($release.tag_name): all $($stampPlan.Count) stamped URLs answer HEAD 200."

foreach ($file in $landingFiles) {
    $html = [System.IO.File]::ReadAllText($file)
    foreach ($entry in $stampPlan) {
        # The marked link: href and visible text both become the artifact -
        # a link that says one file name and downloads another is a lie.
        $pattern = '<a data-zt-dl="' + [regex]::Escape($entry.Slot) + '"[^>]*>[^<]*</a>'
        $replacement = '<a data-zt-dl="' + $entry.Slot + '" href="' + $entry.Url + '">' + $entry.Name + '</a>'
        $patched = [regex]::Replace($html, $pattern, $replacement)
        if ($patched -eq $html) {
            throw "The marked download link '$($entry.Slot)' was not found in $file - the landing source and the stamp script disagree."
        }
        $html = $patched
    }
    $html = [regex]::Replace($html,
        '<span data-zt-release-version>[^<]*</span>',
        '<span data-zt-release-version>' + $version + '</span>')

    # Self-check: every stamped URL is present in the written page.
    foreach ($entry in $stampPlan) {
        if (-not $html.Contains('href="' + $entry.Url + '"')) {
            throw "The stamped URL for '$($entry.Slot)' is missing from the patched page: $file"
        }
    }

    [System.IO.File]::WriteAllText($file, $html, (New-Object System.Text.UTF8Encoding($false)))
    Write-Output "Stamped: $file (release $version, $($stampPlan.Count) artifact links)"
}

Write-Output "landing stamped: release=$version files=$($landingFiles.Count) links=$($stampPlan.Count * $landingFiles.Count)"
