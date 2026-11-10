<#
.SYNOPSIS
Sync the repository's issue labels to the alpha program's canonical set
(T-129). Idempotent: missing labels are created, existing ones are updated to
the canon's color and description, anything outside the canon is left alone
(other workflows may own their own labels).

.DESCRIPTION
The canonical list lives in this script (it mirrors the table in
docs/ALPHA-PROGRAM.md). Requires the GitHub CLI authenticated against the
repository (gh auth login). Never deletes labels.

.PARAMETER Repo
The OWNER/REPOSITORY slug. Defaults to the origin remote's slug.

.EXAMPLE
pwsh scripts/labels-sync.ps1
#>
[CmdletBinding()]
param(
    [string]$Repo = ''
)

$ErrorActionPreference = 'Stop'

if (-not $Repo) {
    $origin = git remote get-url origin
    if ($origin -notmatch '[:/]([^/]+/[^/.]+?)(?:\.git)?$') {
        throw "Cannot derive the repository slug from the origin remote: $origin"
    }
    $Repo = $Matches[1]
}

# The canon (docs/ALPHA-PROGRAM.md, the triage pipeline section): name, color
# (no leading hash, per the GitHub API), description.
$labels = @(
    @{ name = 'bug';              color = 'd73a4a'; description = 'Something is broken' },
    @{ name = 'feedback';         color = '0e8a16'; description = 'An opinion, friction report or idea from the alpha testers' },
    @{ name = 'triage';           color = 'fbca04'; description = 'New intake, not yet walked (applied by the issue templates)' },
    @{ name = 'board';            color = '1d76db'; description = 'Promoted onto docs/TASKS.md as a T-0NN row' },
    @{ name = 'good first issue'; color = '7057ff'; description = 'Small, scoped and welcoming for a first contribution' },
    @{ name = 'help wanted';      color = '008672'; description = 'Real work a contributor can take now' }
)

$existing = gh label list --repo $Repo --limit 200 --json name | ConvertFrom-Json
$existingNames = @{}
foreach ($item in $existing) { $existingNames[$item.name] = $true }

foreach ($label in $labels) {
    if ($existingNames.ContainsKey($label.name)) {
        gh label edit $label.name --repo $Repo `
            --color $label.color --description $label.description | Out-Null
        Write-Output "updated: $($label.name)"
    }
    else {
        gh label create $label.name --repo $Repo `
            --color $label.color --description $label.description | Out-Null
        Write-Output "created: $($label.name)"
    }
}

Write-Output "label sync complete: $($labels.Count) canonical labels against $Repo"
