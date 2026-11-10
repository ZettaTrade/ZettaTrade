## Multi-Agent Repository Sync (develop hygiene)

- **Before ANY touch of `develop`**: `git fetch origin` and verify the tip has not moved (`git rev-list --left-right --count origin/develop...develop`). Multiple agent sessions land on the shared `develop` concurrently — a local top read yesterday says nothing.
- **On an unexpected shift**: rebase the local commits onto `origin/develop` and resolve conflicts preserving the landed content — never stack local commits onto a stale top and never re-edit what another session already shipped.
- **Claim task numbers (T-0NN) only after syncing**: the next free number is read off the synced board. Two sessions picking "the next number" independently collide (the 2026-11-06 case: two different tasks took T-116; the later commit renumbered its content, not the landed one).
- Docs-only commits stay land-friendly: insert rows at the table tail and sections at the file end, with unique anchors that other sessions' edits do not touch.
- Machine-local absolute paths never enter repository text: board rows and task sections carry branches, worktree names (`zt-<name>`) and base hashes only; a drive-letter path is one machine's layout, and the gate's `[MACHINE-PATH]` lint fails the branch on it.
