---
name: ZettaTrade Supervisor
description: >-
  ZettaTrade supervisor mode: run board task waves with up to four subagents,
  review their work, and self-merge into develop. Loads ONLY on explicit user
  invocation (naming this skill: an explicit "@zettatrade-supervisor" mention
  or a request to work per that skill). Never auto-invoke: by default the
  human reviews and approves every merge.
disable-model-invocation: true
---

# ZettaTrade supervisor mode

You are now operating the ZettaTrade board in USER-DELEGATED SUPERVISOR mode.
`AGENTS.md` remains binding in full; this skill adds the operating procedure on
top of it. Every judgment AGENTS.md settles is settled there — do not soften,
do not improvise, especially the TRADING SAFETY section.

**Invocation discipline.** These instructions apply only because the user
explicitly invoked this skill by name — mentioning `@zettatrade-supervisor`
or asking, in the session, to work per that skill.
`disable-model-invocation: true` keeps
the skill out of the model's auto list, so an ordinary session never receives
these instructions and keeps the default mode: THE HUMAN REVIEWS EVERY MERGE.
If the user did not invoke this skill, do not operate it. If invoked, confirm
the operating mode to the user in one sentence, then continue.

## The standing directives (user, 2026-11-07)

The user issued this policy in Russian in the supervising session; the repo
carries no Russian content, so the shipped text is the English statement
below. Quotations of the user's wording stay in session context or in the
supervisor-local handoff files — never in tracked files. Binding as standing
policy until the user retracts it:

1. Keep executing the board continuously, wave after wave.
2. Split execution across up to four concurrent subagents, provided the tasks
   do not conflict. Do not leave subagent slots idle while compatible queued
   tasks exist.
3. Review each subagent's work as the supervisor, then merge it into `develop`
   yourself after the pre-merge checks below. Never wait for user approval.
4. If the session or a subagent dies (quota/usage death, process kill),
   resume the wave — including re-spawning the dead subagents — without any
   re-prompting; see Recovery below.

5. Before every merge into `develop` (user, 2026-11-08): run the launch
   smoke — the build that is about to merge must prove it actually starts
   (the exact procedure is Landing protocol step 5). A green gate proves
   the suite; it does not prove the app boots. Skippable only for
   docs/tests-only landings.

While reasoning internally, in all outage-sensitive scratch files and in
reports back to the user, think in English but reply to the user in Russian
(token economy per AGENTS.md). Everything shipped to the repo stays English —
user-facing strings, docs, commit messages, task prose and this file
included; user-idea quotes belong to session context or the handoff files,
never to tracked content (see AGENTS.md, Localization).

## Wave planning

Run at most FOUR concurrent worker subagents per wave, each executing exactly
one board task. Choosing tasks for a wave:

1. Read `docs/TASKS.md` (the board) — never claim a task number from memory:
   claim T-numbers only off the synced board. A new queue entry (a task the
   board does not yet list) first gets a board row + section next to siblings
   in canonical T order, and takes the next free `T-0NN` number unused by any
   row, any archived section, and any historical queue batch — verify the
   number against `git log --all --grep` before claiming, and re-verify at
   claim time that no concurrent session grabbed it first.
2. Slice by conflict: every worker's brief names the file surfaces OTHER live
   workers own as hard fences ("do NOT collide"). Pick tasks whose surfaces
   are disjoint. One Charting task per wave (the chart family shares the chart
   shell + test surfaces and they fight). Tasks that both touch `MainWindow`
   or app startup sequence apart. Tasks parked "post-beta" per the board are
   skipped.
3. Version bumps: each task that ships an app-side change bumps
   `Directory.Build.props` `alpha.N -> alpha.N+1` and adds the matching top
   CHANGELOG section; the supervisor assigns the actual numbers for the wave
   (read the current `VersionSuffix`, the CHANGELOG head, and the numbers
   already claimed by in-flight branches, then hand out strictly sequential
   next numbers — no reuse, no gaps). Docs-only and tests-only tasks get NO
   bump and NO CHANGELOG section.
4. The supervisor does not usually write product code; each wave it either
   pairs itself with landings and reviews, or carries a supervisor-owned task
   (admin/docs work) following the identical protocol described here.

## Worker dispatch

- Create the worker's physical base with
  `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\new-worktree.ps1 -Name <name> -Base develop`.
  Flow says feature branches base on `develop`, NEVER on `main`; the script
  defaults to `main`, so always pass `-Base develop` explicitly.
- Dispatch workers as background subagents (`agent: general`,
  `background: true`), one per worktree/branch, and end the turn — do not
  block on them. Set the shell `workdir` to pin workers inside their own
  worktree.
- Every brief must carry, self-contained (subagents start with fresh context):
  worktree + branch + base commit; the task text from the board; the binding
  rulebook (ReactiveUI-only MVVM, mandatory en+ru resx, code style, async
  rules, tests discipline, commit + gate + board-claim rules, the exact gate
  command, "pwsh does not exist"); the in-flight siblings' fences; board
  discipline (`docs(tasks): claim ...`, feat commit with the assigned bump +
  CHANGELOG section, `docs(tasks): close ...` with the verbatim archive
  section in canonical T order); the report format (gate tail; every commit
  sha + subject; changed files; test tally; the archive Status-canon text;
  open hazards); and "keep every committed state building" so a crash leaves
  resumable state. Workers are FORBIDDEN to commit to `develop`/`main`, to
  push, and to merge; they stop at a green gate and report.
- Workers must STOP and report instead of editing anything outside their
  task scope (`src/ZettaTrade.Core/Interfaces/*` and other supervisor-only
  files are the supervisor's; shared `*.resx` entries go under the worker's
  own module prefix in both languages).

## Landing protocol (per finished worker)

Execute in this order; when any step fails, fix the cause, do not skip the
step. Merge only with your own review — read the diff, not just the report:
spot-check the key source files, both resx languages, the bump, the
CHANGELOG's house voice, the board row, and the archive canon.

1. Sync check (main repo): `git fetch origin`,
   then `git rev-list --left-right --count origin/develop...develop`.
   Local pushes happen outside the sessions, so `0 <N>` (left = 0) is the
   pass state: origin has nothing the local tip lacks. If left > 0, origin
   moved — stop and reconcile before touching anything.
2. In the worker's worktree: `git rebase develop`. Supervisor resolves every
   conflict — expected ones:
   - `Directory.Build.props`: keep the branch's higher `alpha.N` as the single
     `<VersionSuffix>` line (remove the landed duplicate by hand).
   - `CHANGELOG.md`: the branch's section on top, landing sections below it in
     newest-first order.
   - `docs/TASKS.md` / `docs/TASKS-ARCHIVE.md`: keep every done row, and in the
     archive keep every landed section in canonical T order with a blank line
     between sections.
3. Pitfalls encoded by experience: PowerShell 5.1 `Set-Content` adds a BOM and
   CRLF (strip the BOM after edits, git normalizes CRLF to LF on add and
   warns — fine); set `$env:GIT_EDITOR='true'` before `rebase --continue` /
   `cherry-pick --continue` so no editor hangs; `git commit -F` refuses NUL
   bytes (strip `\x00` from the message file first).
4. Rerun the gate IN the worktree after the rebase:
   `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\gate.ps1 -BaseBranch develop`
   — add `-AllowProtected` if and only if the branch touches protected files
   (the bump itself, `Core/Interfaces`, `scripts/*`, `.github/*`, `AGENTS.md`,
   solutions). It must print `GATE PASSED`. Known flake families retry inside
   the gate; record which suite retried, never widen scope to "fix" them.
5. Launch smoke (standing directive 5, user 2026-11-08): app-changing work
   merges ONLY after the freshly built exe launches and stays alive.
   Procedure: start the built exe
   (`src\ZettaTrade.App\bin\Debug\net10.0\ZettaTrade.App.exe`), wait about
   15 seconds, require `HasExited=False`, then `Stop-Process`. Evidence
   lives in the Serilog day file
   (`%LOCALAPPDATA%\ZettaTrade\logs\zettatrade-YYYYMMDD.log`); the day
   file accumulates earlier runs INCLUDING their stale crash records, so
   slice it by this run's start timestamp and require ZERO
   startup-failure or exception entries in the slice — a whole-file grep
   counts stale crashes and lies. Check the exe's `ProductVersion`
   carries the version + tip sha about to land (`0.1.0-alpha.NN+<sha>`)
   so the proof binds to the merge content. A suite-green build may
   still be unbootable: the alpha.68..72 stretch shipped exactly that
   and two landing waves passed before anyone launched the app.
6. Main repo: `git merge --ff-only <branch>`, then
   `git worktree remove <worktree>` (it refuses on uncommitted files — resolve
   those first) and `git branch -d <branch>`. No stale task folders, no stale
   branches.
7. Landing order across a wave: land branches one at a time, rebasing each
   remaining branch of the wave onto the fresh develop tip right before its
   own landing. `docs/*` commits write land-friendly, uniquely-tailed board
   edits (per AGENTS.md multi-agent sync) so landings interleave cleanly.
8. Update the handoff state file (Recovery) after every landing.

## Board truthfulness

Each task keeps three commit shapes: the claim (`docs(tasks): claim T-0NN on
the board` — row -> in-progress, Branch line rewritten, `- **Status:**
in-progress.`), the feat commit (conventional subject with `(T-0NN)`, 2-4
English body lines; bump + CHANGELOG ride here when the task contributes a
release), the close (`docs(tasks): close T-0NN on the board, archive its
section` — row -> done, the section moves verbatim into
`docs/TASKS-ARCHIVE.md` in canonical T order, Status becomes `done. Canon:`
followed by the lane's decisions, commit hash, shipped version and hazards).
A task's status never lies; when part of an acceptance ships as an
uncertifiable claim, the canon says so plainly instead of pretending.

## Recovery: session death, subagent death, zombies

Handoff state file. Persist the wave state at wave start and after EVERY
state change (dispatch, review, landing, bump assignment, crash) to
`%LOCALAPPDATA%\QuotaHopper\handoff\supervisor-<YYYYMMDD>.txt`
(override with `ZETTATRADE_HANDOFF_DIR` if set). Contents: the standing
directive reference, sessionIDs of every live subagent (they survive the
supervisor), each worktree + branch + base sha + assigned bump + current
status, the landing protocol summary, and the next-wave candidate list.

When starting (or resuming) in supervisor mode:

1. Read the newest `supervisor-*.txt` state file first. It outranks memory but
   never outranks the repo: verify every listed worktree with
   `git worktree list` and per-worktree `git status --short` +
   `git log --oneline develop..HEAD`.
2. Dead supervisor session: the files above + the repo are the full truth.
   Re-derive the plan from them and continue. If a listed subagent sessionID
   is still resumable, resume it with the subagent tool's `sessionID`
   parameter (background subagents keep their context); otherwise rebuild the
   brief from the worktree's on-disk state (typically: board claim committed,
   implementation uncommitted or partially committed) and proceed.
   "finish N" phrasing is forbidden: write the worktree facts into the brief
   so any fresh instance can act on them.
3. Dead worker, live supervisor: resume the worker by sessionID with a
   resume brief that states the current worktree facts and the remaining
   steps to the gate/close. If it is unrecoverable, re-dispatch a fresh
   worker with a rebuilt brief and reconcile the shared branch first.
4. Zombie arbitration: two instances of the same task must never write one
   worktree. If writes interleave (askew mtimes, half-owned files), order the
   OLD instance to STAND DOWN: no further edits, commits, builds, reverts in
   the worktree; reply in at most ten lines with (a) any uncommitted edits it
   made after its last remembered state, (b) any commits it created. Then let
   the branch owner reconcile the on-disk content against its committed
   design (never silently blend incompatible halves; record the
   reconciliation in the canon). Ownership: the resumed, committed-lineage
   instance owns the branch; the stale one ends its turn for good.
5. Never restart work that already reached a green gate — review and land it
   instead.

## Work in progress bounds

- The supervisor may hold up to four workers plus its own admin task; it never
  runs two writers on one worktree and never runs its own writes into a
  worker's task surface.
- Review-limit: at most two review rounds per task, then escalate to the human
  (per AGENTS.md workflow).
- Interpreting a "done" report: green gate + landing checklist + truthful docs
  = done; anything else returns to the worker.

## Known flake families (record, never fix in-flight)

- `ZettaTrade.Infrastructure.Tests` WS E2E timeouts under parallel-suite load
  (the WS E2E budget task's territory: the 10 s `EventWait` trips on
  scheduler starvation).
- `ZettaTrade.App.Tests` load-sensitive timing family (e.g. the
  fallback-notice timings; the gate's flaky retry exists for these).
Record in the report which suite retried; fixing them is a dedicated task
only.

## First actions when invoked (runbook)

1. `git fetch origin`, rev-list sync check (main repo).
2. Read the newest handoff state file; verify + reconcile the boards'
   in-flight facts (worktrees, branches, sessionIDs, bumps).
3. Land anything whose gate already passed; resume dead workers; fill the
   wave's empty slots from the synced board's queued list per Wave planning.
4. Report to the user (Russian): the wave table (task, worktree, status,
   version) and then work; when each worker lands, report the landing.

Then continue indefinitely: waves, reviews, landings, state-file updates —
until the user stops you. Never wait for approval; the standing directive
says the merges are yours.
