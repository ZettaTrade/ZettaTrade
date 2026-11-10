# Alpha program

ZettaTrade ships as a public alpha. This document is the canon of the tester
program: how a tester joins, how feedback flows into the board, and how the
public numbers are produced. It exists for one reason — the traction gap:
features ship, but without a channel between them and real humans any honest
conversation about the project is impossible. The program is lightweight and
rides the public surface only.

## The absolute promise

The app is local-first and telemetry-free: **nothing in the binary phones
home**. Everything this program consumes comes from public GitHub data, and
every number it shows must be re-derivable by anyone clicking the cited link.
If a number cannot be re-derived from a public source, it does not belong on
the metrics page.

## What testers sign up for

- An alpha build from the
  [Releases page](https://github.com/ZettaTrade/ZettaTrade/releases) — the only
  download door the project endorses. Paper and testnet adapters only; real
  exchange keys are never requested (see the trading-safety guidance in the
  README).
- An educational product that is explicitly **not investment advice** (the
  first-run disclaimer repeats this inside the app).
- Breaking changes between alphas without migration promises: the board moves
  faster than the compatibility contracts.
- The feedback loop described below — a GitHub account is the only requirement.

## The tester route

1. **Download** an alpha from the Releases page (the landing's download
   section links it).
2. **Run it**: pick a theme and an adapter in the first-run wizard, trade paper
   or testnet, explore the workspace.
3. **Report** through one of the two issue templates (GitHub Issues is the
   intake; blank issues are disabled so every report arrives structured):
   - **Bug report** — something broke. The template asks for the build version
     (the status bar shows it), the OS, the adapter/mode, reproduction steps
     and the log tail (T-101's log viewer window copies it; the files live
     under the state directory's `logs/`).
   - **Feedback** — nothing broke, but something feels wrong, slow, missing or
     great. One free-form template for impressions, friction and ideas.
4. **Watch the board**: the report's lifecycle is public (see triage below).

## Triage pipeline

Every new issue lands with the `triage` label (the templates apply it). The
triage walk, run on each intake batch:

| Step | Action | Label transition |
| --- | --- | --- |
| 1. Reproduce | follow the report; a non-reproducible or missing-detail report asks the reporter once, then parks | stays `triage` |
| 2. Classify | bug vs feedback vs docs gap; add the area note in the body | `triage` → `bug` or `feedback` stays |
| 3. Promote | real work items become a `docs/TASKS.md` row (canonical `T-0NN`), the issue links the row | + `board` |
| 4. Ship | the row lands through the usual workflow; the issue closes when the feature ships in a release | `board` row done |

The label set is deliberately lean:

| Label | Meaning | Color |
| --- | --- | --- |
| `bug` | something is broken (GitHub default) | `d73a4a` |
| `feedback` | an opinion, friction report or idea | `0e8a16` |
| `triage` | new intake, not yet walked | `fbca04` |
| `board` | promoted onto `docs/TASKS.md` | `1d76db` |
| `good first issue` | small, scoped, welcoming (GitHub default) | `7057ff` |
| `help wanted` | real work a contributor can take (GitHub default) | `008672` |

The labels are GitHub-side state; `scripts/labels-sync.ps1` recreates and
updates exactly this list idempotently (the `gh` CLI authenticated to the
repository is the only requirement — `gh auth login` once):

```powershell
pwsh scripts/labels-sync.ps1
```

## Public metrics

The metrics page (`docs-site/public/metrics/index.html`, served at `/metrics/`)
is **generated, never hand-edited**: `scripts/generate-metrics.ps1` pulls the
public GitHub REST API and renders every number with the link that re-derives
it. The counted numbers, and only these:

| Metric | Source | Re-derivation link |
| --- | --- | --- |
| Releases published | `/repos/{repo}/releases` (public) | the Releases page |
| Release downloads (total) | sum of each release asset's `download_count` (the same number the Releases page shows) | the Releases page |
| Stars / forks / watchers | `/repos/{repo}` | the repository page |
| Contributors | `/repos/{repo}/contributors` | the contributors graph |
| Issues resolved | search: `repo:{repo} is:issue is:closed` | the cited search URL |

The generator's failure policy follows the project's refused-not-defaulted
spirit: an API failure or a rate limit fails the run loudly and leaves the
previous page standing — **no number is ever fabricated or carried stale
without a date**. One legitimate non-failure state exists: while the
repository is not visible to the public API yet (before the public flip), the
generator renders an explicit **pending page** — a page carrying no numbers at
all, never a fabricated zero. The page stamps the generation UTC time; every
number carries its source link inline.

The page rebuilds through CI: `.github/workflows/metrics.yml` (scheduled
weekly, also manual-dispatch) runs the generator and commits the rendered page,
which rides the docs-site deploy.

## Verification record

The route acceptance is verified by a dry run: open one `feedback` issue
through the template, watch it arrive labeled `triage`, walk it to a board row
and close it with the shipped row linked. The template routing itself is
checked by loading the new-issue page and confirming the two forms render with
their labels pre-applied (blank issues stay disabled).
