#Requires -Version 5.1
<#
.SYNOPSIS
    ZettaTrade soak harness and budget gate (T-109). Windows lane: it starts
    the freshly built terminal once against the deterministic demo adapter,
    measures the cold start, samples memory/GC/CPU on a fixed cadence for
    the whole soak window, then answers the budgets via the Soak tool.

    powershell -File scripts/soak.ps1                       (4 h acceptance)
    powershell -File scripts/soak.ps1 -Minutes 5            (the budget gate:
    cold start + a 5-minute memory window; -Minutes 0 collapses to the
    half-minute minimum so the run always carries at least one sample)

.DESCRIPTION
    0. Process hygiene: leftover ZettaTrade processes are killed before and
       after the run (zero-tolerance: a trading terminal never lingers).
    1. dotnet build of the solution (skippable when the caller built).
    1b. Profile sandbox (T-141): the child runs against a per-run sandbox
        profile - the app's own ZETTATRADE_PROFILE_DIR pin redirects every
        profile root (config, data, state) into .soak/<stamp>/profile,
        seeded with a pinned demo-adapter selection, so the run never rides
        the user's persisted exchanges (nor sees their keys) and a soak can
        run beside a live user instance (the single-instance lock and pipe
        derive from the pinned profile). ZETTATRADE_ADAPTERS=demo keeps the
        registered catalog demo-only: the palette warm-up and the balances
        aggregation walk the facade's Registered catalog and fetch every
        entry's symbol catalog at startup, so the adapter allowlist is what
        makes the run network-free. A bare APPDATA override could not do
        this: GetFolderPath resolves through SHGetKnownFolderPath on
        desktop Windows and only falls back to the environment.
    2. Cold start: process spawn -> first main window (a wall-clock cap
       bounds unmeasurable starts; the summary then reports the gap).
    3. Sampling every SampleSeconds: working set, private memory, CPU
       percentage and the per-generation GC counts from the .NET perf
       counters (DOTNET_EnablePerfCounters is armed for the child);
       counters a lane cannot observe are reported as unsampled, never
       invented.
    4. The Soak tool (src/ZettaTrade.Soak) reduces the samples into the
       summary and the budget verdicts (exit code carries the verdict).
    4b. Isolation census (T-141): the run's own log inside the sandbox must
        show the seeded demo restore and zero real-exchange host strings;
        any leak fails the run (exit 1, a harness fault, not a budget).
    5. OutDir samples-*.json / summary-*.json / soak-report-*.md; the
       sandbox profile is discarded after the census.

    NOTES: ASCII-only file (Windows PowerShell 5.1 reads a BOM-less file as
    ANSI). Nothing but the app writes logs; this script's stdout is its
    report channel and is never machine-parsed.
#>
[CmdletBinding()]
param(
    [double]$Minutes = 240,
    [int]$SampleSeconds = 30,
    [string]$OutDir = '',
    [string]$BudgetsFile = '',
    [switch]$SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The cold start's wall-clock cap: a start that shows no main window within
# it is reported as unmeasured (the memory evidence still accrues).
$ColdStartCapSeconds = 60.0

function Say([string]$Text) { [Console]::Out.WriteLine($Text) }

function Invoke-Git {
    $ErrorActionPreference = 'Continue'
    $out = & git -c core.quotepath=false @args 2>&1
    if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') failed: $($out -join ' ')" }
    @($out | ForEach-Object { $_.ToString() })
}

function Invoke-Tool([string]$Exe, [string[]]$Arguments) {
    $ErrorActionPreference = 'Continue'
    $out = & $Exe @Arguments 2>&1 | ForEach-Object { $_.ToString() }
    [pscustomobject]@{ Code = $LASTEXITCODE; Output = @($out) }
}

# A trading terminal never lingers: leftover processes die here, and the
# post-run check demands the empty result explicitly.
function Clear-AppProcesses {
    $before = @(Get-Process -Name 'ZettaTrade*' -ErrorAction SilentlyContinue)
    if ($before.Count -gt 0) {
        $before | Stop-Process -Force
        Start-Sleep -Seconds 2
    }
    $after = @(Get-Process -Name 'ZettaTrade*' -ErrorAction SilentlyContinue)
    if ($after.Count -gt 0) {
        throw 'ZettaTrade processes survived the forced cleanup.'
    }
}

# The CPU share the soak samples: the process's CPU delta over the wall
# window against the machine's TOTAL core count - a 0..100 share of the
# whole machine's capacity, not of one core (the docs table carries the
# same wording; the T-141 review folded the "one core-equivalent" nit).
function Get-AppCpuPercent([System.Diagnostics.Process]$Proc, [double]$WallSeconds, [System.TimeSpan]$PreviousCpu, [int]$Cores) {
    if ($WallSeconds -le 0) { return $null }
    $cpuDelta = ($Proc.TotalProcessorTime - $PreviousCpu).TotalSeconds
    return [math]::Round($cpuDelta * 100.0 / ($WallSeconds * [math]::Max(1, $Cores)), 2)
}

try {
    # StrictMode discipline: the catch-all touches these, so they exist
    # from the first line even when the throw precedes their assignment.
    $profileRoot = $null
    $root = Invoke-Git rev-parse --show-toplevel | Select-Object -First 1
    Set-Location $root

    $exe = Join-Path $root 'src\ZettaTrade.App\bin\Debug\net10.0\ZettaTrade.App.exe'
    $soakDll = Join-Path $root 'src\ZettaTrade.Soak\bin\Debug\net10.0\ZettaTrade.Soak.dll'
    if (-not $BudgetsFile) { $BudgetsFile = Join-Path $root 'scripts\soak-budgets.json' }

    foreach ($artifact in @($BudgetsFile)) {
        if (-not (Test-Path -LiteralPath $artifact)) {
            throw "required artifact missing: $artifact"
        }
    }

    # ---------------------------------------------------------- 0. hygiene
    Clear-AppProcesses

    # ---------------------------------------------------------- 1. build
    if (-not $SkipBuild) {
        $solutions = @(Get-ChildItem -LiteralPath $root -File | Where-Object { $_.Extension -in '.sln', '.slnx' })
        if ($solutions.Count -eq 0) {
            throw 'No .sln/.slnx in the repository root: nothing to build for the soak.'
        }
        $sln = $solutions[0]
        Say "BUILD: $($sln.Name) ..."
        $build = Invoke-Tool 'dotnet' @('build', $sln.FullName, '-nodeReuse:false', '-warnaserror', '--nologo', '-v', 'q', '-clp:NoSummary')
        if ($build.Code -ne 0) {
            throw "dotnet build failed: $($build.Output | Select-Object -Last 10)"
        }
    }

    # The app and the summary tool must exist by now: a built solution puts
    # them here, a caller that skipped the build owed them itself.
    foreach ($artifact in @($exe, $soakDll)) {
        if (-not (Test-Path -LiteralPath $artifact)) {
            throw "required artifact missing (build it or drop -SkipBuild): $artifact"
        }
    }

    $stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
    if (-not $OutDir) { $OutDir = Join-Path $root ".soak\$stamp" }
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    $samplesPath = Join-Path $OutDir "samples-$stamp.json"
    $summaryPath = Join-Path $OutDir "summary-$stamp.json"
    $reportPath = Join-Path $OutDir "soak-report-$stamp.md"

    # ------------------------------------------------- 1.5 the profile sandbox (T-141)
    # The child never sees the user's real profile: the app's own
    # ZETTATRADE_PROFILE_DIR pin (ConfigPathResolver) redirects config, data
    # and state into this per-run sandbox - a bare APPDATA override could
    # not do it (GetFolderPath resolves through SHGetKnownFolderPath on
    # desktop Windows). The seed pins the demo adapter; the profile is
    # discarded after the census. The seed is written through .NET so no
    # BOM lands in the JSON (PS 5.1 Set-Content would add one).
    $profileRoot = Join-Path $OutDir 'profile'
    $profileConfig = Join-Path $profileRoot 'config'
    New-Item -ItemType Directory -Force -Path $profileConfig | Out-Null
    [System.IO.File]::WriteAllText(
        (Join-Path $profileConfig 'appsettings.json'),
        '{"Workspace": {"ExchangeId": "demo"}}',
        (New-Object System.Text.UTF8Encoding($false)))

    # ---------------------------------------------------------- 2. start + cold start
    # The .NET perf counters must be armed for the CHILD process (the
    # GC observation lane); lanes where the service is off fall back to
    # memory-only sampling and say so in the summary.
    $env:DOTNET_EnablePerfCounters = '1'

    # The profile pin rides the same seam: the child inherits the driver's
    # environment. Cleared again at the boundary and in the catch-all. The
    # adapter allowlist rides with it: the palette warm-up and the balances
    # aggregation walk the facade's Registered catalog and fetch every
    # entry's symbol catalog at startup, so a demo-only catalog is what
    # makes the run network-free (the persisted demo pin alone is not).
    $env:ZETTATRADE_PROFILE_DIR = $profileRoot
    $env:ZETTATRADE_ADAPTERS = 'demo'

    Say "START: ZettaTrade for $Minutes minute(s), sampling every $SampleSeconds s..."
    $spawned = Get-Date
    $proc = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -WindowStyle Minimized -PassThru
    $coldStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $coldStartMs = $null
    while ($true) {
        $proc.Refresh()
        if ($proc.MainWindowHandle -ne 0) {
            $coldStartMs = $coldStopwatch.Elapsed.TotalMilliseconds
            break
        }
        if ($coldStopwatch.Elapsed.TotalSeconds -gt $ColdStartCapSeconds) {
            break   # wall-clock cap; the summary reports the cold start as unmeasured
        }
        Start-Sleep -Milliseconds 100
    }
    if ($null -ne $coldStartMs) {
        Say ("COLD START: {0:N0} ms" -f $coldStartMs)
    }
    else {
        Say 'COLD START: not measured (the main window never appeared) - the run stays for the memory evidence.'
    }

    # ---------------------------------------------------------- 3. sampling
    $cores = [Environment]::ProcessorCount
    $deadline = $spawned.AddMinutes([math]::Max(0.5, $Minutes))
    $samples = [System.Collections.Generic.List[object]]::new()

    # The GC counters: one instance per process name; absent instances turn
    # the tally into an unsampled lane note.
    $gcInst = $null
    $gcCounters = @{}
    try {
        $category = New-Object System.Diagnostics.PerformanceCounterCategory('.NET CLR Memory')
        $candidates = @($category.GetInstanceNames() | Where-Object { $_ -like 'ZettaTrade*' })
        if ($candidates.Count -gt 0) {
            $gcInst = $candidates[0]
            foreach ($gen in 'Gen 0', 'Gen 1', 'Gen 2') {
                $gcCounters[$gen] = New-Object System.Diagnostics.PerformanceCounter('.NET CLR Memory', ("# $gen Collections"), $gcInst)
            }
        }
    }
    catch {
        $gcInst = $null
    }
    $gcSampled = $false
    if ($gcInst) {
        try {
            foreach ($gen in 'Gen 0', 'Gen 1', 'Gen 2') { [void]$gcCounters[$gen].NextValue() }
            $gcSampled = $true
        }
        catch {
            $gcSampled = $false
            $gcInst = $null
        }
    }

    $sampleIndex = 0
    $previousCpu = $proc.TotalProcessorTime
    $previousWall = [System.Diagnostics.Stopwatch]::StartNew()
    while ($true) {
        $proc.Refresh()
        if ($proc.HasExited) {
            throw ("app exited early after {0:N0} sample(s); the soak lane lost its subject" -f $sampleIndex)
        }

        $wallSeconds = $previousWall.Elapsed.TotalSeconds
        $cpuPercent = Get-AppCpuPercent $proc $wallSeconds $previousCpu $cores
        $gen0 = $null; $gen1 = $null; $gen2 = $null
        if ($gcSampled) {
            $gen0 = [long]$gcCounters['Gen 0'].NextValue()
            $gen1 = [long]$gcCounters['Gen 1'].NextValue()
            $gen2 = [long]$gcCounters['Gen 2'].NextValue()
        }

        $samples.Add([ordered]@{
            ElapsedSeconds = [math]::Round($previousWall.Elapsed.TotalSeconds, 1)
            WorkingSetMb   = [math]::Round($proc.WorkingSet64 / 1MB, 2)
            PrivateMb      = [math]::Round($proc.PrivateMemorySize64 / 1MB, 2)
            Gen0           = $gen0
            Gen1           = $gen1
            Gen2           = $gen2
            CpuPercent     = $cpuPercent
        })
        $sampleIndex++

        $remaining = ($deadline - (Get-Date)).TotalSeconds
        if ($remaining -le $SampleSeconds) { break }
        Start-Sleep -Seconds ([int][math]::Min($SampleSeconds, [math]::Max(1, [math]::Truncate($remaining))))
        $previousCpu = $proc.TotalProcessorTime
        $previousWall.Restart()
    }

    # ---------------------------------------------------------- 4. boundary + payload
    Stop-Process -Id $proc.Id -Force
    Start-Sleep -Seconds 1
    Clear-AppProcesses

    # ------------------------------------------------- 4.5 the isolation census (T-141)
    # The run's own log (inside the sandbox - the first run there, so the
    # file carries ONLY this run) must show the pinned state held: the
    # sandbox settings loaded (the profile pin held, the seeded demo choice
    # read), no fallback to a default or an unregistered choice, and zero
    # real-exchange hosts anywhere in the run. A leak or a fallback is a
    # harness fault (exit 1), never a budget. NOTE: the demo-only catalog
    # makes a persisted demo restore a SILENT no-op (the facade starts on
    # the catalog's first adapter) - the restore line is not expected; when
    # it appears anyway, it must name 'Demo'.
    $profileLogs = Join-Path $profileRoot 'logs'
    $runLogs = @(Get-ChildItem -LiteralPath $profileLogs -Filter 'zettatrade-*.log' -ErrorAction SilentlyContinue)
    if ($runLogs.Count -eq 0) {
        throw 'the isolation census found no run log inside the sandbox profile (the app never started inside it)'
    }
    $censusText = [string]::Join("`n", @($runLogs | ForEach-Object { [System.IO.File]::ReadAllText($_.FullName) }))
    # T-233: the list covers every shipped real-exchange host, not just the two
    # whose construction lines the first catch showed — a venue's URL in a
    # demo-only run's log is a leak regardless of which venue it names.
    $hostLeaks = @(@('bybit.com', 'binance.com', 'alpaca.markets', 'okx.com') | Where-Object { $censusText -match [regex]::Escape($_) })
    if ($hostLeaks.Count -gt 0) {
        throw ("the isolation census caught real-exchange traffic inside the run: {0}" -f ($hostLeaks -join ', '))
    }
    if (-not ($censusText -match [regex]::Escape("Settings loaded from $profileRoot"))) {
        throw 'the isolation census did not find the sandbox settings load (the profile pin did not hold)'
    }
    $fallbackHits = @([regex]::Matches($censusText, 'is not registered|No persisted exchange choice'))
    if ($fallbackHits.Count -gt 0) {
        throw 'the isolation census found a fallback warning inside the run (the pinned choice did not hold)'
    }
    $restoreNames = @([regex]::Matches($censusText, "Restored persisted exchange '([^']+)'") | ForEach-Object { $_.Groups[1].Value })
    if (@($restoreNames | Where-Object { $_ -ne 'Demo' }).Count -gt 0) {
        throw ("the isolation census found a non-demo exchange restore inside the run: {0}" -f ($restoreNames -join ', '))
    }
    Say 'ISOLATION CENSUS: sandbox settings load confirmed; no fallback warnings; 0 real-exchange host hits in the sandbox log.'

    # The sandbox is discarded after the census: the evidence lives in the
    # report files, the profile never outlives the run.
    Remove-Item -LiteralPath $profileRoot -Recurse -Force
    Remove-Item Env:\ZETTATRADE_PROFILE_DIR -ErrorAction SilentlyContinue
    Remove-Item Env:\ZETTATRADE_ADAPTERS -ErrorAction SilentlyContinue

    $doneRow = "DONE: {0} sample(s) over {1:N0} s; cleanup clean (zero ZettaTrade processes)." -f $samples.Count, ($deadline - $spawned).TotalSeconds
    Say $doneRow
    $runSeconds = [math]::Round(($deadline - $spawned).TotalSeconds, 1)
    $payload = [ordered]@{
        GeneratedAt = (Get-Date).ToUniversalTime().ToString('o')
        RunSeconds  = $runSeconds
        ColdStartMs = $null
        Samples     = $samples
    }
    if ($null -ne $coldStartMs) { $payload.ColdStartMs = [math]::Round($coldStartMs, 1) }
    $payload | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $samplesPath -Encoding UTF8

    $soak = Invoke-Tool 'dotnet' @('exec', $soakDll, $samplesPath, $BudgetsFile, $summaryPath)
    if ($soak.Code -notin 0, 3) {
        # The tool prints nothing; its error detail self-writes into the summary.
        throw "soak tool failed (exit $($soak.Code)); error detail in $summaryPath"
    }

    # ---------------------------------------------------------- 5. report
    $summary = ConvertFrom-Json (Get-Content -LiteralPath $summaryPath -Raw)

    $gcLine = 'not sampled (the lane has no .NET perf counters)'
    if ($summary.Gc -and $summary.Gc.Sampled) {
        $gcLine = 'gen0 {0}, gen1 {1}, gen2 {2}' -f $summary.Gc.Gen0, $summary.Gc.Gen1, $summary.Gc.Gen2
    }
    $coldLine = 'not measured (the window never appeared within the cap)'
    if ($null -ne $summary.ColdStartMs) {
        $coldLine = '{0:N0} ms' -f $summary.ColdStartMs
    }

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("# ZettaTrade soak report - $stamp")
    $lines.Add('')
    $runRow = "- Run: {0:N0} s, {1} sample(s)" -f $summary.RunSeconds, $summary.SampleCount
    $lines.Add($runRow)
    $lines.Add("- Cold start: $coldLine")
    if ($summary.WorkingSetMb) {
        $wsRow = "- Working set MB: mean {0:N1}, max {1:N1}, p95 {2:N1}, p99 {3:N1}" -f $summary.WorkingSetMb.Mean, $summary.WorkingSetMb.Max, $summary.WorkingSetMb.P95, $summary.WorkingSetMb.P99
        $lines.Add($wsRow)
    }
    if ($summary.PrivateMb) {
        $privRow = "- Private MB: mean {0:N1}, max {1:N1}, p95 {2:N1}, p99 {3:N1}" -f $summary.PrivateMb.Mean, $summary.PrivateMb.Max, $summary.PrivateMb.P95, $summary.PrivateMb.P99
        $lines.Add($privRow)
    }
    $slopeRow = "- Leak slope MB/h: working set {0:N2}, private {1:N2}" -f $summary.WorkingSetSlopeMbPerHour, $summary.PrivateSlopeMbPerHour
    $lines.Add($slopeRow)
    $lines.Add("- GC over the run: $gcLine")
    if ($summary.ToolStatus -ne 'ok') {
        $lines.Add("- Tool status: $($summary.ToolStatus)")
    }
    $lines.Add('')
    $lines.Add('## Budget verdicts')
    $lines.Add('')
    $lines.Add('| Budget | Observed | Threshold | Verdict |')
    $lines.Add('|---|---|---|---|')
    foreach ($verdict in $summary.Verdicts) {
        $call = 'OK'
        if (-not $verdict.Ok) { $call = 'BREACH' }
        $verdictRow = '| {0} | {1:N1} | {2:N1} | {3} |' -f $verdict.Name, $verdict.Observed, $verdict.Threshold, $call
        $lines.Add($verdictRow)
    }
    $lines.Add('')
    $lines | Set-Content -LiteralPath $reportPath -Encoding UTF8

    Say ('=' * 60)
    $runRow = "RUN: {0:N0} s, {1} sample(s)" -f $summary.RunSeconds, $summary.SampleCount
    Say $runRow
    if ($summary.WorkingSetMb) {
        $wsRow = "WORKING SET MB: mean {0:N1}, max {1:N1}, p95 {2:N1}, p99 {3:N1}" -f $summary.WorkingSetMb.Mean, $summary.WorkingSetMb.Max, $summary.WorkingSetMb.P95, $summary.WorkingSetMb.P99
        Say $wsRow
    }
    if ($summary.PrivateMb) {
        $privRow = "PRIVATE MB: mean {0:N1}, max {1:N1}, p95 {2:N1}, p99 {3:N1}" -f $summary.PrivateMb.Mean, $summary.PrivateMb.Max, $summary.PrivateMb.P95, $summary.PrivateMb.P99
        Say $privRow
    }
    Say ("COLD START: $coldLine")
    $slopeRow = "LEAK SLOPE MB/h: working set {0:N2}, private {1:N2}" -f $summary.WorkingSetSlopeMbPerHour, $summary.PrivateSlopeMbPerHour
    Say $slopeRow
    Say ("GC TALLY: $gcLine")
    foreach ($verdict in $summary.Verdicts) {
        $call = 'OK'
        if (-not $verdict.Ok) { $call = 'BREACH' }
        $verdictRow = "BUDGET {0}: observed {1:N1} of {2:N1} -> {3}" -f $verdict.Name, $verdict.Observed, $verdict.Threshold, $call
        Say $verdictRow
    }
    Say ('=' * 60)

    if ($soak.Code -eq 3) {
        # A breach is a VERDICT, not an internal error: report it and carry
        # exit 3 straight out — the catch-all below is for harness faults
        # only (exit 1), never for a budget call.
        Say 'SOAK BREACH: a budget is not held (the verdicts above name it).'
        exit 3
    }
    Say "SOAK PASSED (summary: $summaryPath)"
    exit 0
}
catch {
    # The catch-all: the app must never linger behind it, and a SOAK FAILED
    # (internal error) always means the harness itself broke, exit 1.
    Get-Process -Name 'ZettaTrade*' -ErrorAction SilentlyContinue | Stop-Process -Force
    Remove-Item Env:\ZETTATRADE_PROFILE_DIR -ErrorAction SilentlyContinue
    Remove-Item Env:\ZETTATRADE_ADAPTERS -ErrorAction SilentlyContinue
    if ($profileRoot -and (Test-Path -LiteralPath $profileRoot)) {
        Remove-Item -LiteralPath $profileRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    Say "SOAK FAILED (internal error): $($_.Exception.Message)"
    exit 1
}
