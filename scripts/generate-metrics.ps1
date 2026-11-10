<#
.SYNOPSIS
Generate the public metrics page (T-129) from the public GitHub REST API:
releases and their download counts, stars/forks/watchers, contributors and
resolved issues - every number rendered with the link that re-derives it.

.DESCRIPTION
The page is generated, never hand-edited (docs/ALPHA-PROGRAM.md is the canon).
The generator is static and CI-able: it needs no auth for a public repository;
pass $env:GITHUB_TOKEN (or -Token) to raise the rate limit in CI. The failure
policy is refused-not-defaulted: any API failure fails the run loudly and the
previous page stays standing - no number is fabricated and no stale content is
carried silently (the page stamps its generation time).

The counted numbers, and only these (the re-derivability gate): releases and
total release downloads (the same per-asset download_count the Releases page
shows), stars/forks/watchers (the repository page), contributors (the
contributors graph), resolved issues (the cited GitHub search URL).

.PARAMETER Repo
The OWNER/REPOSITORY slug. Defaults to the origin remote's slug.

.PARAMETER OutFile
The rendered page path. Defaults to
docs-site/public/metrics/index.html relative to the repository root.

.PARAMETER Token
A GitHub token (raises the rate limit). Defaults to $env:GITHUB_TOKEN.

.EXAMPLE
pwsh scripts/generate-metrics.ps1
#>
[CmdletBinding()]
param(
    [string]$Repo = '',
    [string]$OutFile = '',
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

if (-not $OutFile) {
    $root = git rev-parse --show-toplevel
    $OutFile = Join-Path $root 'docs-site/public/metrics/index.html'
}

$headers = @{
    'User-Agent' = 'zettatrade-metrics-generator'
    'Accept'     = 'application/vnd.github+json'
}
if (-not $Token -and $env:GITHUB_TOKEN) { $Token = $env:GITHUB_TOKEN }
if ($Token) { $headers['Authorization'] = "Bearer $Token" }

function Invoke-GitHubApi([string]$path) {
    $uri = "https://api.github.com$path"
    try {
        return Invoke-RestMethod -Uri $uri -Headers $headers
    }
    catch {
        throw "The GitHub API call failed for $path : $($_.Exception.Message)"
    }
}

function ConvertTo-HtmlText([string]$text) {
    [System.Net.WebUtility]::HtmlEncode($text)
}

# --- The public numbers ------------------------------------------------------

$pending = $false
try {
    $repoInfo = Invoke-GitHubApi "/repos/$Repo"
}
catch [System.Management.Automation.RuntimeException] {
    # A 404 from the UNAUTHENTICATED public API means the repository is not
    # public (yet) - the honest page state is "pending", never a fabricated
    # zero. Any other failure stays loud.
    if ("$($_.Exception.Message)" -match '\(404\)') {
        $pending = $true
    }
    else {
        throw
    }
}

$generated = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss')

if ($pending) {
    $html = @"
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>ZettaTrade - public metrics (pending)</title>
  <style>
    :root { color-scheme: light dark; }
    body { font: 16px/1.6 system-ui, sans-serif; margin: 0 auto; max-width: 60rem; padding: 2rem 1.25rem 4rem; }
    h1 { margin: 0 0 .25rem; }
    .stamp { color: #5f6672; margin: 0 0 2rem; }
    footer { color: #5f6672; margin-top: 2.5rem; }
    code { background: #8882; padding: .1rem .3rem; border-radius: 3px; }
  </style>
</head>
<body>
  <h1>ZettaTrade - public metrics</h1>
  <p class="stamp">Generated $generated UTC.</p>
  <p>The repository is not visible to the public GitHub API yet, so there are no public numbers to show - this page carries none rather than fabricating any. Once the repository is public, the <code>metrics</code> workflow (and <code>scripts/generate-metrics.ps1</code> locally) fills it with the re-derivable numbers described in <a href="https://github.com/ZettaTrade/ZettaTrade/blob/main/docs/ALPHA-PROGRAM.md">docs/ALPHA-PROGRAM.md</a>.</p>
  <footer>The app is local-first and telemetry-free: nothing in the binary phones home - this page consumes public GitHub data only.</footer>
</body>
</html>
"@

    $outDir = Split-Path -Parent $OutFile
    if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }
    [System.IO.File]::WriteAllText($OutFile, $html, (New-Object System.Text.UTF8Encoding($false)))
    Write-Output "metrics page written (pending state - the repository is not public yet): $OutFile"
    return
}

$stars = [int]$repoInfo.stargazers_count
$forks = [int]$repoInfo.forks_count
$watchers = [int]$repoInfo.subscribers_count
$repoUrl = $repoInfo.html_url

# Releases: public list; downloads are the sum of every asset's public
# download_count (the same number the Releases page shows).
$releases = Invoke-GitHubApi "/repos/$Repo/releases?per_page=100"
$releaseCount = @($releases).Count
$downloads = 0
foreach ($release in $releases) {
    foreach ($asset in @($release.assets)) {
        $downloads += [int]$asset.download_count
    }
}
$releasesUrl = "$repoUrl/releases"

# Contributors: the public graph's count (paginate the public endpoint).
$contributorCount = 0
$page = 1
while ($true) {
    $batch = Invoke-GitHubApi "/repos/$Repo/contributors?per_page=100&page=$page"
    $contributorCount += @($batch).Count
    if (@($batch).Count -lt 100) { break }
    $page++
}
$contributorsUrl = "$repoUrl/graphs/contributors"

# Resolved issues: the public search, cited verbatim.
$resolvedUrl = "https://github.com/search?q=repo%3A$Repo+is%3Aissue+is%3Aclosed&type=issues"
$search = Invoke-RestMethod -Uri "https://api.github.com/search/issues?q=repo%3A$Repo+is%3Aissue+is%3Aclosed&per_page=1" -Headers $headers
$resolved = [int]$search.total_count

# --- The page ----------------------------------------------------------------

$metrics = @(
    @{ value = "$releaseCount"; label = 'Releases published'; href = $releasesUrl; note = 'every alpha and future tag' },
    @{ value = "$downloads"; label = 'Release downloads'; href = $releasesUrl; note = 'the sum of the per-asset counts the Releases page shows' },
    @{ value = "$stars"; label = 'Stars'; href = $repoUrl; note = 'the repository page' },
    @{ value = "$forks"; label = 'Forks'; href = $repoUrl; note = 'the repository page' },
    @{ value = "$watchers"; label = 'Watchers'; href = $repoUrl; note = 'the repository page' },
    @{ value = "$contributorCount"; label = 'Contributors'; href = $contributorsUrl; note = 'the contributors graph' },
    @{ value = "$resolved"; label = 'Issues resolved'; href = $resolvedUrl; note = 'the cited GitHub search, re-derivable by anyone' }
)

$rows = ''
foreach ($metric in $metrics) {
    $value = ConvertTo-HtmlText $metric.value
    $label = ConvertTo-HtmlText $metric.label
    $href = ConvertTo-HtmlText $metric.href
    $note = ConvertTo-HtmlText $metric.note
    $rows += "      <tr><th scope=`"row`">$label</th><td class=`"num`">$value</td><td><a href=`"$href`">$href</a></td><td>$note</td></tr>`n"
}

$html = @"
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>ZettaTrade - public metrics</title>
  <style>
    :root { color-scheme: light dark; }
    body { font: 16px/1.6 system-ui, sans-serif; margin: 0 auto; max-width: 60rem; padding: 2rem 1.25rem 4rem; }
    h1 { margin: 0 0 .25rem; }
    .stamp { color: #5f6672; margin: 0 0 2rem; }
    table { border-collapse: collapse; width: 100%; }
    th, td { border: 1px solid #8884; padding: .5rem .75rem; text-align: left; vertical-align: top; }
    td.num { font-variant-numeric: tabular-nums; font-weight: 600; text-align: right; }
    a { overflow-wrap: anywhere; }
    footer { color: #5f6672; margin-top: 2.5rem; }
    code { background: #8882; padding: .1rem .3rem; border-radius: 3px; }
  </style>
</head>
<body>
  <h1>ZettaTrade - public metrics</h1>
  <p class="stamp">Generated $generated UTC. Every number carries the link that re-derives it (the alpha program's gate, <code>docs/ALPHA-PROGRAM.md</code>); the page is generated by <code>scripts/generate-metrics.ps1</code> and never hand-edited.</p>
  <table>
    <thead>
      <tr><th scope="col">Metric</th><th scope="col">Value</th><th scope="col">Source (re-derivation link)</th><th scope="col">What it counts</th></tr>
    </thead>
    <tbody>
$rows    </tbody>
  </table>
  <footer>The app is local-first and telemetry-free: nothing in the binary phones home - this page consumes public GitHub data only.</footer>
</body>
</html>
"@

$outDir = Split-Path -Parent $OutFile
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }
[System.IO.File]::WriteAllText($OutFile, $html, (New-Object System.Text.UTF8Encoding($false)))

Write-Output "metrics page written: $OutFile"
Write-Output "releases=$releaseCount downloads=$downloads stars=$stars forks=$forks watchers=$watchers contributors=$contributorCount resolved=$resolved"
