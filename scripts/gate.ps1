#Requires -Version 5.1
<#
.SYNOPSIS
    ZettaTrade quality gate. Run it inside a feature worktree:
      pwsh scripts/gate.ps1
      powershell -File scripts/gate.ps1     (machines without PowerShell 7)

.DESCRIPTION
    1. Branch / protected files / scope check
    2. Static rules from AGENTS.md, applied to ADDED lines only (existing debt is ignored)
    3. .resx check via scripts/check-resx.ps1 (en/ru key sets in sync + module prefix rule)
    4. Commit message check
    5. dotnet build (warnings as errors), dotnet test, dotnet format --verify-no-changes
    Prints GATE PASSED only when everything is green, otherwise GATE FAILED.
    Output is deliberately short: it is meant to be read by an LLM (saves tokens).

    Scope is read from <git-dir>/agent-scope.txt (written by supervisor.ps1 'new').

    NOTE: keep this file ASCII-only. Windows PowerShell 5.1 reads a BOM-less file
    as ANSI and would mangle UTF-8 characters.
#>
[CmdletBinding()]
param(
    [string]$BaseBranch = 'main',
    [switch]$SkipTests,
    [switch]$AllowProtected
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$errors   = [System.Collections.Generic.List[string]]::new()
$warnings = [System.Collections.Generic.List[string]]::new()

function Say([string]$Text) { [Console]::Out.WriteLine($Text) }

# Always returns string[]. For a single value use: Invoke-Git ... | Select-Object -First 1
function Invoke-Git {
    # Windows PowerShell 5.1 turns native stderr output into a terminating error under 'Stop'
    # (git warns about CRLF, for example). Exit codes are checked explicitly instead.
    $ErrorActionPreference = 'Continue'
    $out = & git -c core.quotepath=false @args 2>&1
    if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') failed: $($out -join ' ')" }
    @($out | ForEach-Object { $_.ToString() })
}

# Runs a native tool and returns its exit code plus merged stdout/stderr.
function Invoke-Tool([string]$Exe, [string[]]$Arguments) {
    $ErrorActionPreference = 'Continue'   # same PowerShell 5.1 stderr issue as in Invoke-Git
    $out = & $Exe @Arguments 2>&1 | ForEach-Object { $_.ToString() }
    [pscustomobject]@{ Code = $LASTEXITCODE; Output = @($out) }
}

function Test-AnyLike([string]$Text, [string[]]$Patterns) {
    foreach ($p in $Patterns) { if ($Text -like $p) { return $true } }
    return $false
}

# Yields { File, Line, Text } for every added line in .cs / .axaml / .xaml files.
function Get-AddedLines([string]$Base) {
    $diff = Invoke-Git diff -U0 --no-color --diff-filter=AM $Base '--' '*.cs' '*.axaml' '*.xaml'
    $file = $null
    $line = 0
    foreach ($l in $diff) {
        if ($l -like '+++ b/*') { $file = $l.Substring(6); continue }
        if ($l -match '^@@ -\d+(?:,\d+)? \+(\d+)(?:,\d+)? @@') { $line = [int]$Matches[1]; continue }
        if ($l.StartsWith('+')) {
            [pscustomobject]@{ File = $file; Line = $line; Text = $l.Substring(1) }
            $line++
        }
    }
}

# Trims noisy tool output down to the lines that matter.
function Select-Problems([string[]]$Lines, [string]$Pattern, [int]$Max = 25) {
    $rootNative = [regex]::Escape((Resolve-Path $root).Path)
    $Lines |
        Where-Object { $_ -match $Pattern } |
        ForEach-Object { ($_ -replace $rootNative, '.') -replace '\s*\[[^\]]*\.(csproj|sln|slnx)\]\s*$', '' } |
        ForEach-Object { $_.Trim() } |
        Select-Object -Unique -First $Max
}

# Quiet VSTest summary lines end with "- <Some.Tests.dll (TFM)>": the FIRST number
# on such a line is the failure count in every console locale (the label differs,
# the position does not). ASCII-only so a BOM-less PS 5.1 ANSI read cannot mangle it.
function Get-FailedAssemblies([string[]]$Lines) {
    $names = [System.Collections.Generic.HashSet[string]]::new()
    foreach ($l in $Lines) {
        $m = [regex]::Match($l, '- ([^ ]+\.dll)')
        if (-not $m.Success) { continue }
        $n = [regex]::Match($l, '\d+')
        if ($n.Success -and [int]$n.Value -gt 0) { [void]$names.Add($m.Groups[1].Value) }
    }
    return @($names)
}

try {
    $root   = Invoke-Git rev-parse --show-toplevel | Select-Object -First 1
    Set-Location $root
    $gitDir = Invoke-Git rev-parse --absolute-git-dir | Select-Object -First 1
    $branch = Invoke-Git rev-parse --abbrev-ref HEAD | Select-Object -First 1
    $base   = Invoke-Git merge-base $BaseBranch HEAD | Select-Object -First 1

    # Make untracked (new) files visible to `git diff`. Ignored files stay ignored.
    Invoke-Git add -N . | Out-Null

    if ($branch -eq $BaseBranch -or $branch -eq 'HEAD') {
        $errors.Add("[BRANCH] Work on a feat/<name> branch, not '$branch'.")
    }

    $changed = @(Invoke-Git diff --name-only $base | Where-Object { $_ })

    # ---------------------------------------------------------------- scope
    $scopeFile = Join-Path $gitDir 'agent-scope.txt'
    $scope = @()
    if (Test-Path $scopeFile) {
        $scope = @(Get-Content $scopeFile |
            Where-Object { $_ -and -not $_.StartsWith('#') } |
            ForEach-Object { ($_.Trim() -replace '\\', '/') -replace '\*\*', '*' })
    }
    else {
        $warnings.Add('[SCOPE] No agent-scope.txt: scope is not enforced.')
    }

    # Shared files every feature may touch (see AGENTS.md "Shared files").
    $alwaysAllowed = @('*.resx', 'CompositionRoot.cs', '*/CompositionRoot.cs')
    $protectedPaths = @(
        'src/ZettaTrade.Core/Interfaces/*', 'Directory.Build.props', 'Directory.Packages.props',
        'AGENTS.md', 'scripts/*', '*.sln', '*.slnx', '.github/*'
    )
    $catchAllNames = @('Enums.cs', 'Models.cs', 'Utils.cs', 'Helpers.cs', 'Entities.cs', 'Common.cs')

    foreach ($f in $changed) {
        if (-not $AllowProtected -and (Test-AnyLike $f $protectedPaths)) {
            $errors.Add("[PROTECTED] $f is supervisor-only. Revert it and report the needed change.")
            continue
        }
        if ($scope.Count -gt 0 -and -not (Test-AnyLike $f ($scope + $alwaysAllowed))) {
            $errors.Add("[SCOPE] $f is outside the task scope. Revert it or report to the human.")
        }
        if ($catchAllNames -contains (Split-Path $f -Leaf)) {
            $errors.Add("[CATCH-ALL] ${f}: no catch-all files; one entity per file or a file named after the domain concept.")
        }
        if ($f -like '*.csproj') {
            $warnings.Add("[PACKAGES] $f changed: check any new PackageReference.")
        }
    }

    # ---------------------------------------------------------------- static rules (added lines only)
    $rules = @(
        @{ Id = 'MVVM-TOOLKIT'; Kind = 'cs'; Level = 'error'
           Pattern = 'CommunityToolkit\.Mvvm|\[ObservableProperty\]|\[RelayCommand\]|\bObservableObject\b'
           Msg = 'Use ReactiveUI: ReactiveObject, RaiseAndSetIfChanged, ReactiveCommand.' }
        @{ Id = 'RX-UNIT'; Kind = 'cs'; Level = 'error'
           Pattern = 'System\.Reactive\.Unit|\bUnit\.Default\b|Task<Unit>|ReactiveCommand<Unit\b|ReactiveCommand<[^>]*,\s*Unit>'
           Msg = 'Use ReactiveUI.RxVoid, never System.Reactive.Unit.' }
        @{ Id = 'UI-THREAD'; Kind = 'cs'; Level = 'error'
           Pattern = 'ObserveOn\(\s*RxApp\.MainThreadScheduler\s*\)'
           Msg = 'Use ObserveOnUiThread() (ZettaTrade.UI.Extensions).' }
        @{ Id = 'BLOCKING'; Kind = 'cs'; Level = 'error'; SkipTests = $true
           Pattern = '\.GetAwaiter\(\)\.GetResult\(\)|\.Wait\(\)|\.WaitAll\(|\.WaitAny\('
           Msg = 'No blocking on tasks. Use async/await; use InitializeAsync() instead of blocking in constructors.' }
        @{ Id = 'TASK-RESULT'; Kind = 'cs'; Level = 'warn'
           Pattern = '\.Result\b'
           Msg = 'If this is Task.Result: do not block, await it.' }
        @{ Id = 'LOCK'; Kind = 'cs'; Level = 'error'
           Pattern = '\block\s*\('
           Msg = 'No lock: use SemaphoreSlim, ConcurrentDictionary or Channel.' }
        @{ Id = 'FIRE-FORGET'; Kind = 'cs'; Level = 'error'
           Pattern = '^\s*_\s*=\s*[\w\.]+Async\s*\('
           Msg = 'No fire-and-forget: await it or handle errors (Subscribe(onError: ...)).' }
        @{ Id = 'STDOUT'; Kind = 'cs'; Level = 'error'; SkipTests = $true
           Pattern = '\bConsole\.(Write|WriteLine|Out|OpenStandardOutput|SetOut)\b|WriteTo\.Console\('
           Msg = 'Nothing may write to stdout (MCP protocol). Serilog logs to files only.' }
        @{ Id = 'HTML-ENTITY'; Kind = 'cs'; Level = 'error'; Comments = $true
           Pattern = '&(lt|gt|amp|quot|apos);'
           Msg = 'No HTML entities in C# sources.' }
        @{ Id = 'OLD-BASE'; Kind = 'cs'; Level = 'error'
           Pattern = '\bLocalizedViewModelBase\b'
           Msg = 'LocalizedViewModelBase is known debt: do not use or extend it. Use {loc:Localized Key} in XAML.' }
        @{ Id = 'SECRET'; Kind = 'cs'; Level = 'error'; Comments = $true
           Pattern = '(?i)(api[_-]?key|secret|token|password)\w*\s*=\s*"[A-Za-z0-9+/_\-]{16,}"'
           Msg = 'Possible hard-coded secret. Secrets only via ISecretStore; never real keys.' }
        @{ Id = 'TASK-FROMRESULT'; Kind = 'cs'; Level = 'warn'
           Pattern = 'Task\.FromResult'
           Msg = 'Do not wrap sync results in Task.FromResult unless an interface requires it.' }
        @{ Id = 'HARDCODED-UI'; Kind = 'xaml'; Level = 'warn'
           Pattern = '\b(Text|Content|Header|Title|Watermark)="(?!\{)(?=[^"]*[A-Za-z])[^"]*"'
           Msg = 'Possible hard-coded UI string. Use {loc:Localized Key}.' }
    )

    foreach ($a in @(Get-AddedLines -Base $base)) {
        $kind = if ([IO.Path]::GetExtension($a.File) -eq '.cs') { 'cs' } else { 'xaml' }
        $isTestFile = $a.File -like 'tests/*'
        $isComment = $a.Text.TrimStart().StartsWith('//')
        foreach ($r in $rules) {
            if ($r.Kind -ne $kind) { continue }
            if ($isTestFile -and $r.ContainsKey('SkipTests')) { continue }
            if ($isComment -and -not $r.ContainsKey('Comments')) { continue }
            if ([regex]::IsMatch($a.Text, $r.Pattern)) {
                $msg = "[$($r.Id)] $($a.File):$($a.Line) $($r.Msg)"
                if ($r.Level -eq 'error') { $errors.Add($msg) } else { $warnings.Add($msg) }
            }
        }
    }

    if (-not $SkipTests -and -not (Test-Path (Join-Path $root 'tests'))) {
        $warnings.Add('[TEST] No tests directory: dotnet test does not verify anything.')
    }

    # ---------------------------------------------------------------- resx (en/ru key sync + module prefix rule)
    # Whole-repo check, delegated to scripts/check-resx.ps1 and run with the same
    # interpreter the gate was started with (pwsh or Windows PowerShell 5.1).
    $resxScript = Join-Path $root 'scripts/check-resx.ps1'
    if (Test-Path $resxScript) {
        $psExe = (Get-Process -Id $PID).ProcessName
        $resx = Invoke-Tool $psExe @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $resxScript)
        if ($resx.Code -ne 0) {
            $probs = @(Select-Problems $resx.Output '\S' 15)
            $errors.Add("[RESX] check-resx.ps1 failed:`n    " + ($probs -join "`n    "))
        }
    }
    else {
        $errors.Add('[RESX] scripts/check-resx.ps1 not found.')
    }

    # ---------------------------------------------------------------- repo-hygiene lints (repair prevention)
    # a) Whole-repo scan for corruption-only markers of the double-encoding bug:
    #    UTF-8 bytes re-read as Windows-1251 produce these markers, and legit
    #    typography never uses them, so any hit is a tombstone of pasted garbage.
    # b) Whole-repo scan for machine-local absolute paths: branches, worktree
    #    names and base hashes are the portable facts, a drive-letter path
    #    rooted in programmer-typical directories (Users, Projects) is one
    #    machine's layout and rots everywhere else. The lookbehind keeps URL
    #    schemes (https://, avares://) from matching; generic roots such as
    #    C:Program-Files stay allowed. Files are read as UTF-8 explicitly:
    #    Windows PowerShell 5.1 reads BOM-less files as ANSI (CP1251) and
    #    would then turn every legit dash or quote into exactly the junk
    #    family lint a) hunts.
    $mojibakePattern = '[\u0402\u0406\u0459\u045A\u201A\u2020]'
    $machinePathPattern = '(?<![A-Za-z])[A-Za-z]:[\\/]+(Users|Projects)([\\/]|$)'
    $mojibakeExts = @('.cs', '.axaml', '.xaml', '.md', '.props', '.targets', '.csproj', '.sln', '.slnx', '.json', '.yml', '.yaml', '.txt', '.toml', '.ps1', '.cmd')
    $utf8Gated = New-Object System.Text.UTF8Encoding($false, $true)
    $mojibakeHits = [System.Collections.Generic.List[string]]::new()
    $machinePathHits = [System.Collections.Generic.List[string]]::new()
    $scanRoots = @(Get-ChildItem -LiteralPath $root -Recurse -File |
        Where-Object { ($mojibakeExts -contains $_.Extension.ToLowerInvariant()) -and
            ($_.FullName -notmatch '\\(\.git|bin|obj|node_modules)(\\|$)') })
    foreach ($scanFile in $scanRoots) {
        if ($scanFile.FullName -like "$gitDir*") { continue }
        $scanLines = $null
        try { $scanLines = [System.IO.File]::ReadAllLines($scanFile.FullName, $utf8Gated) } catch { continue }   # not repo text: skip
        for ($scanIndex = 0; $scanIndex -lt $scanLines.Count; $scanIndex++) {
            if ($scanLines[$scanIndex] -match $mojibakePattern) {
                $relative = $scanFile.FullName.Substring($root.Length + 1).Replace('\', '/')
                $mojibakeHits.Add("${relative}:$($scanIndex + 1)")
                if ($mojibakeHits.Count -ge 25) { break }
            }
            if ($scanLines[$scanIndex] -match $machinePathPattern) {
                $relative = $scanFile.FullName.Substring($root.Length + 1).Replace('\', '/')
                $machinePathHits.Add("${relative}:$($scanIndex + 1)")
                if ($machinePathHits.Count -ge 25) { break }
            }
        }
        if (($mojibakeHits.Count + $machinePathHits.Count) -ge 25) { break }
    }
    if ($mojibakeHits.Count -gt 0) {
        $errors.Add("[MOJIBAKE] baked double-encoded characters (UTF-8 re-read as CP1251), repair before commit:`n    " + ($mojibakeHits -join "`n    "))
    }
    if ($machinePathHits.Count -gt 0) {
        $errors.Add("[MACHINE-PATH] machine-local absolute paths baked into repository text; name branches, worktrees and base hashes instead:`n    " + ($machinePathHits -join "`n    "))
    }

    # ---------------------------------------------------------------- commit messages
    $subjects = @(Invoke-Git log "$base..HEAD" --format=%s | Where-Object { $_ })
    foreach ($s in $subjects) {
        if ($s -like 'Merge *') { continue }   # merge commits are branch-model artifacts, not feature commits
        if ($s -notmatch '^(feat|fix|refactor|docs|test|chore|perf|build|ci)(\([\w\-\.]+\))?!?: .{3,}') {
            $errors.Add("[COMMIT] '$s' is not an English conventional commit (feat:, fix:, refactor:, docs:).")
        }
    }
    $dirty = @(Invoke-Git status --porcelain | Where-Object { $_ })
    if ($dirty.Count -gt 0) {
        $warnings.Add("[COMMIT] $($dirty.Count) uncommitted change(s). Commit with a conventional-commit message when done.")
    }

    # ---------------------------------------------------------------- gitleaks secret scan (T-117, optional)
    # Same scanner CI enforces (gitleaks.toml). Guarded: skipped when the
    # binary is absent so the gate never breaks on machines without it;
    # CI (security.yml) always runs the scan.
    $gitleaksTool = Get-Command gitleaks -ErrorAction SilentlyContinue
    if ($null -eq $gitleaksTool) {
        $warnings.Add('[GITLEAKS] skipped: gitleaks not on PATH (optional locally; CI always scans).')
    }
    else {
        $scanConfig = Join-Path $root 'gitleaks.toml'
        if (-not (Test-Path $scanConfig)) {
            $errors.Add("[GITLEAKS] $scanConfig not found: the scanner CI config must exist.")
        }
        else {
            # Branch-local scan (the commits this branch adds above the merge
            # base), mirroring the CI push/PR range scans: sibling agents'
            # un-landed branches and pre-base history belong to the CI
            # nightly full-history scan and the docs/SECURITY.md audit.
            $scanReport = Join-Path $gitDir 'gate-gitleaks-report.json'
            $scan = Invoke-Tool $gitleaksTool.Source @(
                'git', $root,
                '--config', $scanConfig,
                '--log-opts', "--no-merges --first-parent $base..HEAD",
                '--report-format', 'json',
                '--report-path', $scanReport,
                '--no-banner'
            )
            if ($scan.Code -eq 0) {
                Remove-Item -LiteralPath $scanReport -ErrorAction SilentlyContinue
            }
            else {
                # Exit code 1 with a non-empty report = findings; anything else is a scanner or config error.
                $hasFindings = ((Test-Path -LiteralPath $scanReport -PathType Leaf) -and
                    ((Get-Item -LiteralPath $scanReport).Length -gt 2))
                if ($hasFindings) {
                    $errors.Add("[GITLEAKS] secret scan found findings in this branch's commits above the merge base. Report: $scanReport. Rotate-and-report per docs/SECURITY.md; never rewrite history outside an explicit supervisor decision.")
                }
                else {
                    $probs = @(Select-Problems $scan.Output '\S' 5)
                    $errors.Add("[GITLEAKS] secret scan failed (exit $($scan.Code)):`n    " + ($probs -join "`n    "))
                }
            }
        }
    }

    # ---------------------------------------------------------------- high-risk routing flag
    # Not a failure: tells the supervisor this diff needs a strong cloud reviewer plus a human look.
    $riskPatterns = @('*Risk*', '*Order*', '*Secret*', '*Mcp*', '*Trading*', '*Confirmation*', '*ExchangeAdapter*')
    $highRisk = @()
    foreach ($f in $changed) {
        if ($f -like 'tests/*') { continue }
        if (Test-AnyLike $f $riskPatterns) { $highRisk += $f }
    }

    # ---------------------------------------------------------------- build, test, format (only if static checks are clean)
    if ($errors.Count -eq 0) {
        $sln = @(Get-ChildItem -LiteralPath $root -File | Where-Object { $_.Extension -in '.sln', '.slnx' })
        if ($sln.Count -eq 0) {
            $errors.Add('[BUILD] No .sln/.slnx in the repository root.')
        }
        else {
            $logFile = Join-Path $gitDir 'gate-last.log'
            $build = Invoke-Tool 'dotnet' @('build', $sln[0].FullName, '-nodeReuse:false', '-warnaserror', '--nologo', '-v', 'q', '-clp:NoSummary')
            $build.Output | Set-Content $logFile
            if ($build.Code -ne 0) {
                $probs = @(Select-Problems $build.Output ': error ')
                if ($probs.Count -eq 0) { $probs = @($build.Output | Select-Object -Last 15) }
                $errors.Add("[BUILD] failed (full log: $logFile):`n    " + ($probs -join "`n    "))
            }
            else {
                if (-not $SkipTests) {
                    $testArgs = @('test', $sln[0].FullName, '--no-build', '--nologo', '-v', 'q')
                    $test = Invoke-Tool 'dotnet' $testArgs
                    $test.Output | Add-Content $logFile
                    $failedAsms = @()
                    if ($test.Code -ne 0) { $failedAsms = @(Get-FailedAssemblies $test.Output) }
                    if ($failedAsms.Count -gt 0) {
                        # Flaky retry: rerun each failing assembly once before reporting it.
                        foreach ($asm in $failedAsms) {
                            $projName = $asm.Substring(0, $asm.Length - 4)
                            $proj = Join-Path (Join-Path $root 'tests') "$projName\$projName.csproj"
                            if (-not (Test-Path -LiteralPath $proj)) { $proj = $sln[0].FullName }
                            Say "[TEST] flaky retry: $projName ..."
                            $retry = Invoke-Tool 'dotnet' @('test', $proj, '--no-build', '--nologo', '-v', 'q')
                            $retry.Output | Add-Content $logFile
                            if ($retry.Code -eq 0) {
                                $warnings.Add("[TEST] $projName passed on flaky retry; consider stabilizing the timing-sensitive test.")
                            }
                            else {
                                $probs = @(Select-Problems $retry.Output '^\s*(Failed|X) |Error Message|Expected|Actual|But |Failed!|: error ' 20)
                                if ($probs.Count -eq 0) { $probs = @($retry.Output | Select-Object -Last 15) }
                                $errors.Add("[TEST] failed on retry for $projName (full log: $logFile):`n    " + ($probs -join "`n    "))
                            }
                        }
                    }
                    elseif ($test.Code -ne 0) {
                        # Assembly names were not detected above: rerun the whole suite once.
                        Say '[TEST] flaky retry: whole suite ...'
                        $retry = Invoke-Tool 'dotnet' $testArgs
                        $retry.Output | Add-Content $logFile
                        if ($retry.Code -ne 0) {
                            $probs = @(Select-Problems $retry.Output '^\s*(Failed|X) |Error Message|Expected|Actual|But |Failed!|: error ' 40)
                            if ($probs.Count -eq 0) { $probs = @($retry.Output | Select-Object -Last 15) }
                            $errors.Add("[TEST] failed on retry (full log: $logFile):`n    " + ($probs -join "`n    "))
                        }
                        else {
                            $warnings.Add('[TEST] suite passed on flaky retry; consider stabilizing timing-sensitive tests.')
                        }
                    }
                }

                $format = Invoke-Tool 'dotnet' @('format', $sln[0].FullName, '--verify-no-changes', '--no-restore', '-v', 'q')
                $format.Output | Add-Content $logFile
                if ($format.Code -ne 0) {
                    $probs = @(Select-Problems $format.Output ': error ')
                    if ($probs.Count -eq 0) { $probs = @($format.Output | Select-Object -Last 15) }
                    $errors.Add("[FORMAT] dotnet format found issues, run 'dotnet format' (full log: $logFile):`n    " + ($probs -join "`n    "))
                }
            }
        }
    }
    else {
        Say 'Build, tests and format skipped: fix the rule violations first.'
    }

    # ---------------------------------------------------------------- report
    [pscustomobject]@{
        branch   = $branch
        head     = (Invoke-Git rev-parse HEAD | Select-Object -First 1)
        time     = (Get-Date).ToString('s')
        passed   = ($errors.Count -eq 0)
        errors   = $errors.Count
        warnings = $warnings.Count
        highRisk = @($highRisk)
    } | ConvertTo-Json | Set-Content (Join-Path $gitDir 'gate-report.json')

    if ($warnings.Count -gt 0) {
        Say "WARNINGS ($($warnings.Count)):"
        $warnings | Select-Object -First 10 | ForEach-Object { Say "  $_" }
    }
    if ($highRisk.Count -gt 0) {
        Say "HIGH-RISK paths touched (needs strong reviewer + human): $($highRisk -join ', ')"
    }
    if ($errors.Count -gt 0) {
        Say "ERRORS ($($errors.Count)):"
        $errors | ForEach-Object { Say "  $_" }
        Say 'GATE FAILED'
        exit 1
    }
    Say 'GATE PASSED'
    exit 0
}
catch {
    Say "GATE FAILED (internal error): $($_.Exception.Message)"
    exit 2
}
