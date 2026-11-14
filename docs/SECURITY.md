# ZettaTrade — repository security posture (secret scanning)

This document is created by **T-117** (secret scanner: gitleaks in CI + one-shot full-history
audit). The sibling tasks reference it: T-118 links it from the README, the user manual
(T-115) points to it for the secrets posture, and no other doc duplicates its content.

## Why the repository itself is a trust surface

ZettaTrade is an open-source terminal whose users put API keys into `ISecretStore` on their
own machines. The repository must therefore prove a negative: that it never carries real
credentials — neither in the working tree nor in the young commit history (267 commits at the
audit instant; the alpha posture makes a full history audit cheap to redo before beta).

## The scanning layers

| Layer | Where | Scope | Fails on |
|---|---|---|---|
| CI push/PR scan | `.github/workflows/security.yml` | the pushed commit range / the PR commit range | any finding (job failure) |
| CI full-history re-scan | the same workflow, `schedule` + `workflow_dispatch` | every commit reachable from the default branch | any finding |
| One-shot machine audit | recorded below | the whole history at the audit instant | recorded verdict |
| Local gate guard (optional) | `scripts/gate.ps1` | the commits above the merge base | any finding, when gitleaks is on `PATH` |

### CI workflow contract

- Events: `push` (all branches), `pull_request`, `schedule: 0 3 * * *` (daily full-history
  re-scan of all reachable commits), `workflow_dispatch` (one-shot full audit on demand).
- The official action `gitleaks/gitleaks-action@v2` runs on `ubuntu-latest` after
  `actions/checkout@v4` with `fetch-depth: 0` (a full clone, so the scheduled/dispatch scans
  see complete history).
- Per-event mapping (the action's own logic): push events scan
  `--no-merges --first-parent <base>^..<head>` over the pushed commit range; pull_request
  events scan the PR's commit range; `schedule`/`workflow_dispatch` pass no range option,
  which means full history.
- Config: the environment variable `GITLEAKS_CONFIG=gitleaks.toml` points the CLI at the
  committed root config (the CLI honors `GITLEAKS_CONFIG` natively; the action only runs the
  CLI in a child process).
- Verdict wiring: the action runs the scanner with `--exit-code=2`, redacted verbose output,
  a SARIF report, and turns exit code 2 into a job failure — the build fails on any finding.
  Findings surface as inline PR review comments (needs `pull-requests: write`, granted in
  the workflow's `permissions`) and as a job-summary table plus a downloadable SARIF
  artifact.
- CLI version: the action installs its own hard-coded gitleaks CLI (8.24.3 at the time of
  this note). Local verification (below) reproduced the verdicts on both 8.24.3 and 8.30.1;
  the action's pinned pairing of CLI version and invocation syntax is its own supported
  default.
- Operator note: a gitleaks action on a repository owned by a GitHub **Organization**
  requires the free `GITLEAKS_LICENSE` secret (obtained at gitleaks.io) or the job exits
  with an error before scanning; personal accounts scan without a license. The workflow
  already wires `GITLEAKS_LICENSE`, so an org-owned repo only needs the secret added.

## The `gitleaks.toml` allowlist contract

The committed config extends the default gitleaks ruleset (`[extend] useDefault = true`)
and carries exactly one carve-out: a global allowlist covering **only** the known dummy
test fixtures identified during the 2026-11-06 audit:

| Value | Shape | Fixture (explicitly named) | Role |
|---|---|---|---|
| `secret://<name>` | `secret://` reference URI | `tests/ZettaTrade.App.Tests/Mcp/McpServerConfigListTests.cs`, `McpClientServiceTests.cs`, `McpClientManagerTests.cs`; doc example in `src/ZettaTrade.Core/Interfaces/IMcpClient.cs` | MCP auth-secret **references** — pointers into `ISecretStore`, never a raw value |
| `live-secret` | literal | `tests/ZettaTrade.App.Tests/Mcp/McpClientServiceTests.cs` (`StoredSecret`) | MCP client round-trip test fixture |
| `t-049-master-passphrase` | literal | `tests/ZettaTrade.Infrastructure.Tests/Secrets/EncryptedFileSecretStoreRulesTests.cs` | T-049 harness passphrase, temp-dir store |
| `Tape.A11yWithSymbol` | literal (localization template key) | `src/ZettaTrade.UI/Charting/WidgetAccessibilityNames.cs` (the `TapeWithSymbolKey` constant) | the trade tape's a11y name template; the default `generic-api-key` rule trips on the const-assignment shape (an identifier ending in `Key` followed by a string), not on a credential (the 2026-11-14 nightly triage) |
| `api_key=abc123def456` | fake query string | `tests/ZettaTrade.App.Tests/Services/LogTailServiceTests.cs` (the redaction-before-parse fixture) | the synthetic log line's fake key — exactly the shape the redactor must scrub |
| `ghp_0123…` / `AIzaSyA…` and the four sibling probe literals | placeholder token shapes (the T-117 probe set, digits only) | `tests/ZettaTrade.App.Tests/Services/LogRedactorTests.cs` (`AnIndustryTokenShape_Redacts`) | the redactor's own test tokens: real vendor shapes carrying placeholder digits; `github-pat` and `gcp-api-key` fired the 2026-11-14 nightly, the remaining shapes ride defensively |

Rules for changing the allowlist:

1. Sample-shaped strings only, never real credentials; a suspected real value is never
   allowlisted — it is a finding (see the remediation policy).
2. Every entry names its fixture file and purpose inside the description; a new entry needs
   the same.
3. No path-based entries: a global allowlist combines `paths` and `regexes` with OR, so a
   `paths` entry would become a blanket anything-inside-that-directory carve-out. The
   entries are bounded by tightly-anchored regexes on the fixture literals instead.
4. No `.gitleaksignore` and no `gitleaks:allow` comments as a mechanism for fixtures:
   entitlement stays in one committed file with one documented contract (a `.gitleaksignore`
   may still be used for a transient per-branch false positive while a finding is triaged —
   it is a quarantine, not a policy).

Honest note on rule sensitivity: many default rules are entropy-gated (the AWS token rule,
for example, requires Shannon entropy ≥ 3). Low-entropy samples such as the fixtures above
do not trip the default rules today; the allowlist entries are therefore *defensive* — they
keep those named fixtures green even if a future default ruleset starts matching their
shapes — and their value comes from being explicit, named and bounded.

## One-shot full-history audit (2026-11-06)

Method (machine-audit, performed on the agent worktree, reports kept in the pre-approved
temp directory — nothing but this document entered the repository):

- Scanners: official gitleaks release binaries, both the CI-pinned **8.24.3** and a control
  **8.30.1**; `gitleaks git <repo>` full-history mode over the repository.
- Configs: the committed `gitleaks.toml` and, as a control, the stock built-in default
  config — both produced the same verdict.
- Scope at the audit instant: the develop line carries 267 commits at the audit start
  (234 patch-bearing non-merge + 33 merge commits, ~3.1 MB of diff text) and 269 once
  T-117's own two commits are included — the final pass re-scans the whole of both, 236
  patch-bearing non-merge commits at the final instant. The all-refs view additionally
  included the sibling in-flight feature branches of other agent sessions (239 non-merge
  commits at the audit instant). The task text's estimate of "~260 commits" matches the
  measured 267.
- Pre-audit probe (recorded in the task origin): a regex probe of HEAD for
  `sk-`/`AKIA`/`ghp_`/`AIza`/PEM markers was clean; this audit settles the full history
  with the scanner.

**Result: zero findings.** The one-shot full-history pass over the repository's entire
history is clean on both configurations and both CLI versions. No revoke-and-rotate
obligations were triggered. The daily `schedule` scan repeats the full-history pass
automatically from now on.

## Local verification of the scanner contract (2026-11-06)

The task session cannot push to CI, so the workflow mapping was verified locally layer by
layer; the final CI-green link of the chain is confirmed on the first pushed run:

1. **Current tree** + committed `gitleaks.toml`: `gitleaks git` full history → exit 0, no
   leaks, on both 8.24.3 and 8.30.1 (the allowlist regression: the scanner passes with the
   fixtures present in the tree).
2. **Commit-range mapping** — the acceptance check that a *seeded dummy-key commit is
   caught by the workflow logic*: gitleaks was run with the exact invocation semantics the
   official action uses for push/PR scans
   (`gitleaks git --log-opts='--no-merges --first-parent <sha>^..<sha>'`) against a seed SHA
   inside a **disposable clone** (never this worktree). The seeded commit held three
   high-entropy DUMMY markers in the AWS / GitHub / OpenAI token shapes plus the in-repo
   fixture literals; the scan exited 1 with exactly the three expected rule findings
   (`aws-access-token`, `github-pat`, `openai-api-key`) on both CLI versions.
3. **Allowlist semantics** — a throwaway probe config (the same allowlist entries as
   `gitleaks.toml` plus an extra rule that flags the fixture-family shapes and a seeded
   non-fixture marker) was run over (a) the current tree → green with the fixtures live,
   and (b) the seeded clone → the fixture literals were suppressed while every non-fixture
   marker was caught. Both directions hold: the allowlist allows exactly the named shapes
   and nothing else.
4. **Hygiene**: all disposable seeds and branches lived only inside the temp-dir clone and
   were deleted with it; the thrown-away dummy commits never entered the ZettaTrade object
   database, and this repository's history carried no dummy keys at any instant.

Verification lesson worth keeping: red-team markers must be high-entropy to trip the
entropy-gated default rules; low-entropy dummies (all-zero padding, say) do not fire them.
That property is a scanner feature (it suppresses obvious samples), not a coverage gap —
real credentials are, in practice, high-entropy.

## Remediation policy

Revoke-and-rotate by default; history rewrite only by an explicit supervisor decision.

1. **Triage first.** A credential-shaped value that is confirmed to be a sample-shaped
   fixture enters `gitleaks.toml`'s allowlist only with the fixture file and purpose named
   (contract above). Everything else is treated as real until proven otherwise.
2. **Revoke and rotate before anything else.** The exposed credential is deactivated at its
   vendor and replaced by a fresh one that lives only in `ISecretStore` / user configuration
   — never in the repository, never on a command line (`IConfirmationService` and
   `ISecretStore` keep applying to the app's own flows). Rotation precedes any history
   action: removing a still-alive credential from history leaves it working.
3. **Record the event.** Append a dated line to the audit log below: the finding day, the
   rule that fired, the file + commit, the real-or-fixture verdict, and the taken action.
4. **History rewrite is a supervisor-level decision only.** It invalidates every clone and
   fork's history assumption and disturbs the shared landing flow on `develop`; the alpha
   posture keeps the history young and cheap to redo, which is exactly why the decision is
   reserved for an explicit supervisor call rather than agent-side remediation. Agents and
   contributors never rewrite history on their own.
5. **Scanner discipline afterwards.** After rotation-and-removal, the scans (push/PR range,
   nightly full history, local guard) must pass before the branch can land again.

## Release artifacts (T-125)

`.github/workflows/release.yml` publishes a tagged release: on a `v*` tag the tag's own tree is
built self-contained per OS and attached to a GitHub Release as `zettatrade-<version>-<rid>`
archives — zip for win-x64, osx-x64 and osx-arm64, tar.gz for linux-x64. The version string
comes only from the `Directory.Build.props` committed at the tag and is cross-checked against
the tag name: a tag that does not match the version commit fails the pipeline instead of
shipping, and the release notes are the verbatim `## [0.1.0-alpha.NN]` CHANGELOG.md section of
the same tree (a missing section fails the workflow — tags ride the release commit that adds
the bump).

What the artifacts contain — and nothing else:

- the application itself with the .NET runtime bundled in (self-contained publish), plus the
  four built-in theme JSON files that ship next to the executable;
- no user data, no configuration, no credentials: user secrets live only in `ISecretStore` on
  the user's machine and never enter any artifact; the release is built in CI straight from
  the tag's tree, never from a local worktree, and the T-117 layers above keep the sources honest.

Integrity is by hash, not by signature, until beta: every Release carries
`zettatrade-<version>-sha256.txt` in the `<SHA-256>  <archive-name>` two-space convention
`sha256sum -c` accepts. Verify after download:

- Linux: `sha256sum -c zettatrade-<version>-sha256.txt`
- macOS: `shasum -a 256 -c zettatrade-<version>-sha256.txt`
- Windows (PowerShell): compare `(Get-FileHash <archive> -Algorithm SHA256).Hash` against the
  sums line for that archive.

Nothing in the pipeline is code-signed or notarized yet — a deliberate alpha posture, pairing
with the T-119 first-run disclaimer: macOS Gatekeeper and Windows SmartScreen can warn about
the unsigned archives, and a cautious user verifies the sums and runs first from the paper
lane. The beta flip is a board decision and brings, in one change: Authenticode signing for
the Windows archive, a hardened-runtime + notarization flow for the macOS ones, signing
credentials held in machine/CI secret storage of the `ISecretStore` class (never plaintext,
never in the repository) and this note updated to the signed reality.

## Profile backup (T-215)

The profile backup (`ZettaTrade.Infrastructure/Backup/ProfileBackupService.cs`) writes and
restores a user-chosen archive of the stored profile — the settings file and the six SQLite
stores. Its security posture is structural, not conventional:

- **Secrets are absent by construction.** The archive is built from an explicit entry list —
  seven named files, each resolved through `ConfigPathResolver`. There is no directory walk
  that could sweep in anything else, and `secrets.enc` (the `ISecretStore` backing file) is
  not on that list and never enters a backup. The service takes no `ISecretStore` dependency
  at all. API keys therefore need re-entry after a restore; the restore's confirmation text
  says so.
- **The archive is user data, not a trust boundary.** It is written to a user-picked path and
  the user is free to share it; treat a received archive like any received file. On restore
  it is untrusted input: the manifest is an allowlist (no traversal, no foreign entries, a
  byte-size cross-check, a bidirectional zip-vs-manifest match), the schema and version must
  match exactly what this build ships, and every mismatch is a typed refusal that changes
  nothing.
- **The restore replaces whole files only** behind a typed human confirmation
  (`IConfirmationService`, key `Backup.RestoreConfirm`), after an exclusive-access pre-flight
  on every store it would replace. Each landed file is whole; a mid-flight I/O failure is
  reported as a failure, never masked as success.

## Audit log

- 2026-11-06 — T-117 one-shot full-history audit: gitleaks 8.24.3 and 8.30.1, committed
  `gitleaks.toml` + stock defaults; the develop line re-scanned before (267 commits) and
  after (269 commits, including T-117's own scanner commits) this task's work, plus the
  sibling in-flight branches in the all-refs view (239 non-merge commits). **Result: zero
  findings throughout; no remediation required.** Local verification protocol documented
  above; CI-green confirmation pending on the first pushed run of
  `.github/workflows/security.yml` (workflow lint clean via `actionlint`).
- 2026-11-14 — T-224 nightly triage: every nightly the repository has recorded
  (4 `schedule` runs, 2026-11-11 through 2026-11-14, 03:45 UTC on `main`, gitleaks
  8.24.3) failed with the same 4 findings — the first nightlies after T-101
  (2c05545) and T-110 (8129cae) landed. The finds: `generic-api-key` at
  `src/ZettaTrade.UI/Charting/WidgetAccessibilityNames.cs:48` (the TapeWithSymbolKey
  localization template constant), `github-pat` + `gcp-api-key` + `generic-api-key`
  in the LogRedactor / LogTailService test fixtures (the T-117 probe shapes and the
  synthetic redaction log line). The push scans stayed green throughout because they
  scan only the pushed ranges — the fixtures ride in older commits the ranges never
  covered. Verdict: all four are sample-shaped dummy fixtures (placeholder digits
  and a template key); no real credential ever existed in the history, so no
  rotation is owed and no history rewrite is contemplated. Action: the literals
  entered the `gitleaks.toml` allowlist with file + purpose named (the contract
  above), the remaining probe shapes ride defensively. Locally verified with the
  CI's own invocation (8.24.3, full history over the develop line, 831 commits):
  exactly 4 findings on the base config, zero with the fixed one, and a seeded
  non-fixture token in a disposable temp repo still fires — the allowlist stays
  bounded. The nightly goes green once the fix is reachable from the scan's
  checkout branch (`main`).
