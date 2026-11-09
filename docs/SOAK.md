# ZettaTrade — soak harness and budget gates

This document is created by **T-109** (soak harness and budget gates). It explains
how the harness runs, what the report means, and which lane enforces which budget.

## Why a soak

The perf targets in AGENTS.md are asserted ad-hoc today: a green test suite proves
behavior, not multi-hour stability. The soak keeps the terminal alive for a whole
run (the standard acceptance: **4 hours**) against the deterministic **demo
adapter** (no network, no keys) and samples the process on a fixed cadence, so a
leak shows up as a slope long before it shows up as a crash.

## Running the harness

```
powershell -File scripts/soak.ps1                     # the 4-hour acceptance run
powershell -File scripts/soak.ps1 -Minutes 5          # the budget gate window
powershell -File scripts/soak.ps1 -Minutes 0          # the half-minute floor: at least one sample
```

- The driver builds the solution first (`-SkipBuild` reuses a build the caller
  made — the gate does this).
- **Process hygiene:** leftover `ZettaTrade*` processes are killed before the run
  and the post-run check demands zero survivors.
- **Cold start** is measured as spawn → first main window (a 60 s cap bounds the
  measurement; a start past the cap is reported as unmeasured).
- After the samples, the `ZettaTrade.Soak` tool reduces the series into
  `summary-*.json` and the driver renders `soak-report-*.md` in the output
  directory (default `.soak/<timestamp>/`, gitignored).

## The report

| Field | Meaning |
|---|---|
| `WorkingSetMb` / `PrivateMb` | the memory distributions: mean, max, P95, P99 over the run |
| `WorkingSetSlopeMbPerHour` / `PrivateSlopeMbPerHour` | the least-squares growth slope — the leak indicator; a sloping private line is the one to chase (the working set flutters with paging) |
| `Gc` | the per-generation GC tally over the run; `Sampled: false` means the lane could not observe the .NET perf counters (the legacy GUI counters do not cover .NET 10) — reported, never invented |
| `CpuPercent` | the sampled CPU share of one core-equivalent (informational) |
| `ColdStartMs` | the measured cold start |
| `Verdicts` | one row per enforced budget |

## Budget semantics

`scripts/soak-budgets.json` carries the AGENTS targets; `scripts/soak-ci-budgets.json`
scopes the CI lane:

| Key | Landing lane | CI lane | Source |
|---|---|---|---|
| `MemoryMeanMb` | 400 (enforced) | 400 (enforced) | "typical memory < 500 MB" — the mean is held under 400 as the peak's headroom |
| `MemoryPeakMb` | 500 (enforced) | 500 (enforced) | the perf target itself |
| `ColdStartMs` | 3000 (enforced) | null → **advisory** | "cold start < 3 s" |

A budget key written **in** the file is enforced: its verdict drives the tool's
exit code (0 = pass, 3 = breach) and, for the run, the driver's `SOAK PASSED`
vs `SOAK FAILED: budget breach`. A key written **null** is advisory: the
measurement still appears in every summary, but nothing gates on it.

- `gate.ps1 -Budgets` (opt-in, `-BudgetsSoakMinutes` defaults to 5): runs the
  soak against the **full** budgets and fails the gate on any breach.
- The CI `soak` workflow (weekly schedule + manual dispatch): the 4-hour run
  against the CI budgets file — memory enforced, cold start reported; the
  `soak-report` artifact carries the full summary.

## The cold start record (honest)

The 2026-11-10 harness shakedown measured the Debug lane's cold start around
**3.9 s warm** (spawn → window; the Serilog startup timeline attributes ~1.6 s to
the runtime before the first log line, ~1.7 s to the service assembly, the rest to
the window build). That breaches the 3 s target with the budget **enforced** in
the landing lane: `gate.ps1 -Budgets` will report the breach until the startup
path's own optimization pass lands (a dedicated follow-up task; recorded in the
T-109 canon). The CI lane deliberately reports, never fails on it — a runner's
cold start is runner-noise.

## Limits

- The harness is a **Windows lane** (the window handle, the perf counters, the
  demo adapter's cadence). CI runs on `windows-latest`.
- GC per-generation counts ride the Windows perf counters; .NET 10 does not
  surface them there, so the tally typically reports `Sampled: false`. The
  in-app T-108 performance HUD remains the manual GC/fps lens; a future harness
  could speak the diagnostics IPC protocol if the lane ever needs a programmatic
  GC read.
- The soak measures the process from the outside; it does not drive the UI.
  It proves memory stability and cold start, not fps — that number needs the
  HUD enabled for the run and stays a manual read for now.
