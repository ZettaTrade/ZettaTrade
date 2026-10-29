#Requires -Version 5
# Usage: pwsh scripts/new-worktree.ps1 -Name chart   (creates ..\zt-chart on branch feat/chart)
param(
    [Parameter(Mandatory)][string]$Name,
    [string]$Base = 'main'
)
$root = Split-Path -Parent $PSScriptRoot
$path = Join-Path (Split-Path $root -Parent) "zt-$Name"
git -C $root worktree add $path -b "feat/$Name" $Base
Write-Host "Worktree ready: $path (branch feat/$Name). Start OpenCode there."
# Cleanup after merge: git worktree remove ..\zt-<name>; git branch -d feat/<name>
