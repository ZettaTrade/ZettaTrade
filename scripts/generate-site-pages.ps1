<#
.SYNOPSIS
Render the site's changelog and user-manual pages (T-259) from their markdown
sources: CHANGELOG.md -> docs-site/public/changelog/index.html and
docs/USER-MANUAL.md -> docs-site/public/manual/index.html.

.DESCRIPTION
The pages are generated, never hand-edited. The renderer is a house subset of
GitHub-flavored markdown - ATX headings (h1-h4), paragraphs, flat lists (with
wrapped continuations), tables (pipe rows at column 0), fenced code, hr,
blockquotes, bold, inline code and links - because the two sources carry
exactly that and the renderer refuses anything it cannot render honestly.
Everything is HTML-escaped first, so text is text and code is code; links to
sibling .md files are rewritten to GitHub blob URLs on the default branch and
in-page #anchors are kept, with every anchor verified against the rendered
slugs (the manual's own table of contents must resolve or the run fails).

Deterministic by design: no clock stamp - the page header links the rendered
source on the default branch, which dates it better than a timestamp. The
failure policy is refused-not-defaulted (docs/ALPHA-PROGRAM.md): a missing
source, an unterminated fence, a table without a separator row, an image or
an unresolvable relative link fails the run loudly.

.PARAMETER Repo
The OWNER/REPOSITORY slug the blob links point at. Defaults to the origin
remote's slug.

.PARAMETER SiteRoot
The docs-site root. Defaults to <repo root>/docs-site.

.EXAMPLE
pwsh scripts/generate-site-pages.ps1
#>
[CmdletBinding()]
param(
    [string]$Repo = '',
    [string]$SiteRoot = ''
)

$ErrorActionPreference = 'Stop'

$root = git rev-parse --show-toplevel
if (-not $Repo) {
    $origin = git remote get-url origin
    if ($origin -notmatch '[:/]([^/]+/[^/.]+?)(?:\.git)?$') {
        throw "Cannot derive the repository slug from the origin remote: $origin"
    }
    $Repo = $Matches[1]
}
if (-not $SiteRoot) { $SiteRoot = Join-Path $root 'docs-site' }
$blobBase = "https://github.com/$Repo/blob/main"

# The escaping happens FIRST for every source line - text is text, code is
# code, and no source byte can reach the page as markup.
function ConvertTo-HtmlText([string]$text) {
    [System.Net.WebUtility]::HtmlEncode($text)
}

# The GitHub heading slug: lowercase; letters, digits, spaces, underscores and
# hyphens kept; everything else dropped; spaces become hyphens; duplicates get
# GitHub's own -2, -3, ... suffixes.
function New-GitHubSlug([string]$heading, [object]$slugTable) {
    $base = $heading.ToLowerInvariant()
    $base = [regex]::Replace($base, '[^\p{L}\p{N} _-]', '')
    $base = $base.Replace(' ', '-')
    $count = [int]$slugTable[$base]
    $slugTable[$base] = $count + 1
    if ($count -gt 0) { return "$base-$count" }
    return $base
}

# The inline pass runs on ALREADY-ESCAPED text. Order: protect code spans
# first, then links (so a bold-wrapped link still resolves), then bold, then
# restore the code spans untouched - no inline construct nests into a code
# span, exactly like the GitHub rendering.
function ConvertFrom-Inline([string]$escaped, [string]$sourceDirRel, [object]$linkLog) {
    $codes = [System.Collections.Generic.List[string]]::new()
    $result = [regex]::Replace($escaped, '`([^`]+)`', {
        param($m)
        $codes.Add($m.Groups[1].Value) | Out-Null
        [string][char]1 + ($codes.Count - 1) + [string][char]1
    })

    $result = [regex]::Replace($result, '\[([^\]]+)\]\(([^)\s]+)\)', {
        param($m)
        $text = $m.Groups[1].Value
        $target = $m.Groups[2].Value
        $href = Resolve-LinkTarget $target $sourceDirRel $linkLog
        "<a href=`"$href`">$text</a>"
    })

    $result = [regex]::Replace($result, '\*\*([^*]+?)\*\*', '<strong>$1</strong>')

    $result = [regex]::Replace($result, [string][char]1 + '(\d+)' + [string][char]1, {
        param($m)
        '<code>' + $codes[[int]$m.Groups[1].Value] + '</code>'
    })
    return $result
}

# The link policy: http(s) and pure in-page anchors ride as-is; a relative .md
# link becomes a blob URL on the default branch (resolved against the source
# file's own directory); anything else is refused - never guessed.
function Resolve-LinkTarget([string]$target, [string]$sourceDirRel, [object]$linkLog) {
    if ($target -match '^https?://') { return $target }
    if ($target.StartsWith('#')) {
        $linkLog.Add($target.Substring(1)) | Out-Null
        return $target
    }
    $anchor = ''
    if ($target -match '^([^#]+\.md)(#.*)?$') {
        $path = $Matches[1]
        if ($Matches.Count -gt 2 -and $Matches[2]) { $anchor = $Matches[2] }
        $combined = ($sourceDirRel + '/' + $path).Replace('\', '/')
        $parts = [System.Collections.Generic.List[string]]::new()
        foreach ($part in $combined.Split('/')) {
            if ($part -eq '' -or $part -eq '.') { continue }
            if ($part -eq '..') {
                if ($parts.Count -eq 0) { throw "The relative link escapes the repository: $target" }
                $parts.RemoveAt($parts.Count - 1)
                continue
            }
            $parts.Add($part) | Out-Null
        }
        $repoPath = ($parts -join '/')
        $linkLog.Add($null) | Out-Null
        return "$blobBase/$repoPath$anchor"
    }
    throw "The renderer's subset does not carry the relative link '$target' (from $sourceDirRel) - extend the renderer instead of guessing."
}

# The block parser: one pass, line-oriented, exactly the shapes the two
# sources carry.
function ConvertFrom-Markdown([string[]]$lines, [string]$sourceDirRel, [string]$pageSlugScope) {
    $slugTable = @{}
    $linkLog = [System.Collections.Generic.List[string]]::new()
    $out = [System.Collections.Generic.List[string]]::new()
    $inFence = $false
    $i = 0
    while ($i -lt $lines.Count) {
        $line = $lines[$i]

        if ($inFence) {
            if ($line -match '^```\s*$') {
                $inFence = $false
                $out.Add('</code></pre>') | Out-Null
            }
            else {
                $out.Add((ConvertTo-HtmlText $line)) | Out-Null
            }
            $i++
            continue
        }

        if ($line -match '^```(\S*)\s*$') {
            $lang = $Matches[1]
            $inFence = $true
            if ($lang) {
                $out.Add('<pre><code class="language-' + (ConvertTo-HtmlText $lang) + '">') | Out-Null
            }
            else {
                $out.Add('<pre><code>') | Out-Null
            }
            $i++
            continue
        }

        if ($line -match '^(#{1,4}) (.+)$') {
            $level = $Matches[1].Length
            $text = $Matches[2]
            $slug = New-GitHubSlug $text $slugTable
            $out.Add("<h$level id=`"$slug`">" + (ConvertFrom-Inline (ConvertTo-HtmlText $text) $sourceDirRel $linkLog) + "</h$level>") | Out-Null
            $i++
            continue
        }

        if ($line -match '^---+\s*$') {
            $out.Add('<hr>') | Out-Null
            $i++
            continue
        }

        if ($line -match '^> ?') {
            $quote = [System.Collections.Generic.List[string]]::new()
            while ($i -lt $lines.Count -and $lines[$i] -match '^> ?') {
                $quote.Add(($lines[$i] -replace '^> ?', '')) | Out-Null
                $i++
            }
            $inner = ($quote | Where-Object { $_.Trim() -ne '' }) -join ' '
            if ($inner.Trim() -ne '') {
                $out.Add('<blockquote><p>' + (ConvertFrom-Inline (ConvertTo-HtmlText $inner) $sourceDirRel $linkLog) + '</p></blockquote>') | Out-Null
            }
            continue
        }

        if ($line -match '^[-*] ' -or $line -match '^\d+\. ') {
            $ordered = $line -match '^\d+\. '
            $marker = if ($ordered) { '^\d+\. ' } else { '^[-*] ' }
            $items = [System.Collections.Generic.List[string]]::new()
            while ($i -lt $lines.Count) {
                if ($lines[$i] -match $marker) {
                    $items.Add(($lines[$i] -replace $marker, '')) | Out-Null
                    $i++
                }
                elseif ($lines[$i] -match '^\s{2,}\S' -and $items.Count -gt 0) {
                    $items[$items.Count - 1] += ' ' + $lines[$i].Trim()
                    $i++
                }
                elseif ($lines[$i].Trim() -eq '' -and $items.Count -gt 0) {
                    # A blank line closes the block only when the next
                    # non-blank line is dedented; an indented continuation
                    # after the blank still belongs to the current item.
                    $peek = $i + 1
                    while ($peek -lt $lines.Count -and $lines[$peek].Trim() -eq '') { $peek++ }
                    if ($peek -lt $lines.Count -and ($lines[$peek] -match $marker -or $lines[$peek] -match '^\s{2,}\S')) {
                        $i++
                        continue
                    }
                    break
                }
                else { break }
            }
            $tag = if ($ordered) { 'ol' } else { 'ul' }
            $out.Add("<$tag>") | Out-Null
            foreach ($item in $items) {
                $rendered = ConvertFrom-Inline (ConvertTo-HtmlText $item) $sourceDirRel $linkLog
                $out.Add("<li>$rendered</li>") | Out-Null
            }
            $out.Add("</$tag>") | Out-Null
            continue
        }

        if ($line -match '^\|') {
            $rows = [System.Collections.Generic.List[string]]::new()
            while ($i -lt $lines.Count -and $lines[$i] -match '^\|') {
                $rows.Add($lines[$i]) | Out-Null
                $i++
            }
            if ($rows.Count -lt 2) { throw "A table without a separator row near line: $line" }
            $separator = $rows[1]
            if ($separator -notmatch '^\|(\s*:?-{3,}:?\s*\|)+\s*$') {
                throw "A table's second row is not a separator row: $separator"
            }
            $out.Add('<table>') | Out-Null
            $out.Add('<thead>') | Out-Null
            $out.Add((ConvertFrom-TableRow $rows[0] 'th' $sourceDirRel $linkLog)) | Out-Null
            $out.Add('</thead>') | Out-Null
            if ($rows.Count -gt 2) {
                $out.Add('<tbody>') | Out-Null
                for ($r = 2; $r -lt $rows.Count; $r++) {
                    $out.Add((ConvertFrom-TableRow $rows[$r] 'td' $sourceDirRel $linkLog)) | Out-Null
                }
                $out.Add('</tbody>') | Out-Null
            }
            $out.Add('</table>') | Out-Null
            continue
        }

        if ($line -match '!\[') {
            throw "The renderer's subset carries no images - an image appeared near: $line"
        }

        if ($line.Trim() -ne '') {
            $paragraph = [System.Collections.Generic.List[string]]::new()
            while ($i -lt $lines.Count -and $lines[$i].Trim() -ne '' -and
                   $lines[$i] -notmatch '^(#{1,4}) ' -and $lines[$i] -notmatch '^```' -and
                   $lines[$i] -notmatch '^---+\s*$' -and $lines[$i] -notmatch '^> ?' -and
                   $lines[$i] -notmatch '^[-*] ' -and $lines[$i] -notmatch '^\d+\. ' -and
                   $lines[$i] -notmatch '^\|') {
                $paragraph.Add($lines[$i].Trim()) | Out-Null
                $i++
            }
            $joined = $paragraph -join ' '
            $out.Add('<p>' + (ConvertFrom-Inline (ConvertTo-HtmlText $joined) $sourceDirRel $linkLog) + '</p>') | Out-Null
            continue
        }

        $i++
    }

    if ($inFence) { throw 'The source ends inside a fenced code block - refusing to render.' }

    # The anchor self-check (the manual's table of contents must resolve):
    # every in-page anchor the page links must be a slug the renderer itself
    # generated.
    $missing = @($linkLog | Where-Object { $_ -and -not $slugTable.ContainsKey($_) })
    if ($missing.Count -gt 0) {
        throw ("Unresolved in-page anchors in $pageSlugScope : " + (($missing | Select-Object -Unique) -join ', '))
    }

    return ($out -join "`n")
}

function ConvertFrom-TableRow([string]$row, [string]$cellTag, [string]$sourceDirRel, [object]$linkLog) {
    $inner = $row.Trim()
    if ($inner.StartsWith('|')) { $inner = $inner.Substring(1) }
    if ($inner.EndsWith('|')) { $inner = $inner.Substring(0, $inner.Length - 1) }
    $cells = $inner.Split('|') | ForEach-Object { $_.Trim() }
    $cellsHtml = @()
    foreach ($cell in $cells) {
        $cellsHtml += "<$cellTag>" + (ConvertFrom-Inline (ConvertTo-HtmlText $cell) $sourceDirRel $linkLog) + "</$cellTag>"
    }
    return '<tr>' + ($cellsHtml -join '') + '</tr>'
}

function New-GeneratedPage([string]$title, [string]$description, [string]$pageUrl, [string]$sourceRel, [string]$bodyHtml) {
    $sourceUrl = "$blobBase/$sourceRel"
    return @"
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <meta name="color-scheme" content="light dark" />
  <meta name="description" content="$description" />
  <title>$title</title>
  <meta property="og:site_name" content="ZettaTrade" />
  <meta property="og:type" content="article" />
  <meta property="og:title" content="$title" />
  <meta property="og:description" content="$description" />
  <meta property="og:url" content="$pageUrl" />
  <link rel="icon" href="/ZettaTrade/favicon.ico" />
  <link rel="icon" type="image/svg+xml" href="/ZettaTrade/zettatrade-mark.svg" />
  <style>
    :root { color-scheme: light dark; }
    body { font: 16px/1.6 system-ui, sans-serif; margin: 0 auto; max-width: 60rem; padding: 2rem 1.25rem 4rem; }
    h1, h2 { margin: 1.5rem 0 .5rem; }
    h3, h4 { margin: 1.25rem 0 .4rem; }
    .stamp { color: #5f6672; margin: 0 0 2rem; font-size: .9rem; }
    table { border-collapse: collapse; width: 100%; margin: 1rem 0; }
    th, td { border: 1px solid #8884; padding: .5rem .75rem; text-align: left; vertical-align: top; }
    code { background: #8882; padding: .1rem .3rem; border-radius: 3px; overflow-wrap: anywhere; }
    pre { background: #8882; padding: .75rem 1rem; border-radius: 6px; overflow-x: auto; }
    pre code { background: none; padding: 0; }
    blockquote { border-left: 3px solid #8884; margin: 1rem 0; padding: .25rem 1rem; color: #5f6672; }
    a { overflow-wrap: anywhere; }
    hr { border: none; border-top: 1px solid #8884; margin: 2rem 0; }
    li { margin: .25rem 0; }
    footer { color: #5f6672; margin-top: 2.5rem; border-top: 1px solid #8884; padding-top: 1rem; }
  </style>
</head>
<body>
$bodyHtml
  <footer>Rendered from <a href="$sourceUrl">$sourceRel</a> (the default branch) by <a href="$blobBase/scripts/generate-site-pages.ps1">scripts/generate-site-pages.ps1</a> - generated, never hand-edited.</footer>
</body>
</html>
"@
}

function Write-SitePage([string]$outPath, [string]$html) {
    $outDir = Split-Path -Parent $outPath
    if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }
    [System.IO.File]::WriteAllText($outPath, $html, (New-Object System.Text.UTF8Encoding($false)))
    Write-Output "site page written: $outPath"
}

# --- The two pages ------------------------------------------------------------

$changelogSource = Join-Path $root 'CHANGELOG.md'
$manualSource = Join-Path $root 'docs/USER-MANUAL.md'
foreach ($source in @($changelogSource, $manualSource)) {
    if (-not (Test-Path $source)) { throw "The source file is missing: $source" }
}

$changelogLines = [System.IO.File]::ReadAllLines($changelogSource)
$changelogBody = ConvertFrom-Markdown $changelogLines '' 'CHANGELOG.md'
$changelogHtml = New-GeneratedPage 'ZettaTrade - changelog' `
    'What every ZettaTrade version carried, rendered from the repository changelog.' `
    'https://zettatrade.github.io/ZettaTrade/changelog/' `
    'CHANGELOG.md' ("<p class=`"stamp`">The newest version first; every section is the verbatim repository changelog.</p>`n<h1>ZettaTrade - changelog</h1>`n" + $changelogBody)
Write-SitePage (Join-Path $SiteRoot 'public/changelog/index.html') $changelogHtml

$manualLines = [System.IO.File]::ReadAllLines($manualSource)
$manualBody = ConvertFrom-Markdown $manualLines 'docs' 'docs/USER-MANUAL.md'
$manualHtml = New-GeneratedPage 'ZettaTrade - user manual' `
    'The user manual of the ZettaTrade desktop trading terminal: installation, first run, concepts, the keyboard map and the safety notes.' `
    'https://zettatrade.github.io/ZettaTrade/manual/' `
    'docs/USER-MANUAL.md' ("<p class=`"stamp`">Rendered verbatim from the repository manual - the shipped code of the current build is what it documents.</p>`n" + $manualBody)
Write-SitePage (Join-Path $SiteRoot 'public/manual/index.html') $manualHtml
