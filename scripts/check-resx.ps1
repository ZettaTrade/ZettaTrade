#Requires -Version 5
# Localization invariant check: every user-facing string lives in resx and
# every key exists in BOTH cultures (en base file + ru file). Run from any
# worktree: pwsh scripts/check-resx.ps1 (exit code 0 = in sync).
# Wired into scripts/gate.ps1 as the 'resx' step; costs well under 2 s.
#
# Module prefix rule (AGENTS.md): every key is prefixed by the module that
# owns it, so files always conflict per module, not globally. The prefixes
# known to this repo are listed below - add one when a module ships its
# first localized strings (e.g. Exchange.* when the exchanges module lands).
# A key without a known prefix fails exactly like a missing key.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

$ModulePrefixes = @(
    'Menu.',
    'Settings.',
    'Order.',
    'Status.',
    'AI.',
    'Chart.',
    'Chrome.',
    'Trading.',
    'NewOrder.',
    'Positions.',
    'History.',
    'Stats.',
    'Fees.',
    'OrderBook.',
    'Tape.',
    'Palette.',
    'Notif.',
    'Wizard.',
    'Port.',
    'Signal.',
    'Backtest.',
    'Alert.',
    'Log.'
)

function Get-ResxKeys([string]$Path) {
    [xml]$content = Get-Content -LiteralPath $Path -Raw -Encoding UTF8
    # Designer-only entries ($this.*, >>Form) are not localized strings.
    return @($content.root.data |
        Where-Object { $_.name -and $_.name -notmatch '^(\$this\.|>>)' } |
        ForEach-Object { [string]$_.name })
}

$failures = New-Object System.Collections.Generic.List[string]
$baseFiles = Get-ChildItem (Join-Path $root 'src') -Recurse -Filter *.resx |
    Where-Object { $_.Name -notlike '*.ru.resx' }

foreach ($base in $baseFiles) {
    $ruCultureFile = Join-Path $base.DirectoryName ($base.BaseName + '.ru.resx')
    $enKeys = Get-ResxKeys $base.FullName
    $ruKeys = Get-ResxKeys $ruCultureFile
    "$($base.BaseName): $($enKeys.Count) en keys, $($ruKeys.Count) ru keys"

    foreach ($difference in (Compare-Object -CaseSensitive -ReferenceObject $enKeys -DifferenceObject $ruKeys)) {
        if ($difference.SideIndicator -eq '<=') {
            $failures.Add("$($base.Name): key '$($difference.InputObject)' is missing in the ru file")
        } else {
            $failures.Add("$($base.Name): key '$($difference.InputObject)' is missing in the en file")
        }
    }

    foreach ($key in ($enKeys + $ruKeys | Sort-Object -CaseSensitive -Unique)) {
        $dot = $key.IndexOf('.')
        $prefix = if ($dot -gt 0) { $key.Substring(0, $dot + 1) } else { '' }
        if ($ModulePrefixes -cnotcontains $prefix) {
            $failures.Add("$($base.Name): key '$key' has no module prefix (known: $($ModulePrefixes -join ' '))")
        }
    }
}

if ($failures.Count -gt 0) {
    Write-Host "RESX FAILED: $($failures.Count) problem(s)" -ForegroundColor Red
    foreach ($failure in $failures) {
        Write-Host "  $failure" -ForegroundColor Red
    }
    exit 1
}

exit 0
