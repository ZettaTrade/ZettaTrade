#Requires -Version 7
# Objective "done" check for every agent. Run from any worktree: pwsh scripts/gate.ps1
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path -Parent $PSScriptRoot)

function Step([string]$Name, [scriptblock]$Command) {
    Write-Host "== $Name" -ForegroundColor Cyan
    & $Command
    if ($LASTEXITCODE -ne 0) {
        Write-Host "GATE FAILED: $Name" -ForegroundColor Red
        exit 1
    }
}

Step 'build'  { dotnet build -nodeReuse:false -warnaserror --nologo -v q }
Step 'test'   { dotnet test --no-build --nologo -v q }
Step 'format' { dotnet format --verify-no-changes --no-restore -v q }

# Text rules the analyzers cannot express
$rules = @(
    @{ Pattern = 'CommunityToolkit\.Mvvm';                  Message = 'Use ReactiveUI, not CommunityToolkit.Mvvm' },
    @{ Pattern = '\[(ObservableProperty|RelayCommand)\]';   Message = 'Use RaiseAndSetIfChanged / ReactiveCommand' },
    @{ Pattern = 'GetAwaiter\(\)\s*\.GetResult\(\)';        Message = 'No blocking on tasks' },
    @{ Pattern = '\block\s*\(';                             Message = 'Use SemaphoreSlim instead of lock in async code' },
    @{ Pattern = '^\s*_\s*=\s*.*Async\(';                   Message = 'No fire-and-forget' }
)

$files = Get-ChildItem src, tests -Recurse -Filter *.cs -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '[\\/](obj|bin)[\\/]' }

$failed = $false
foreach ($rule in $rules) {
    $hits = $files | Select-String -Pattern $rule.Pattern
    if ($hits) {
        $failed = $true
        Write-Host "RULE: $($rule.Message)" -ForegroundColor Red
        $hits | ForEach-Object { "  $($_.Path):$($_.LineNumber)" }
    }
}
if ($failed) { Write-Host 'GATE FAILED: rules' -ForegroundColor Red; exit 1 }

Write-Host 'GATE PASSED' -ForegroundColor Green
