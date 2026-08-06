---
description: Run the DbScrub pre-push checklist before handing Jim a PR URL. Refuses to push from main, checks commit count + squash decision, PR title length, CLAUDE.md gates touched in the diff, SQL surfaces that need manual verification, flips touched Linear tickets to In Progress, and outputs the bare PR URL ready to hand over. Use when work is checkpoint-ready or the user asks for a PR.
argument-hint: "Optional: PR title override or 'squash' to force squash-before-push"
allowed-tools: Read, Grep, Glob, Bash(git:*), mcp__linear__get_issue, mcp__linear__save_issue
user-invocable: true
---

# PR Prep — Pre-Push Checklist

DbScrub-specific pre-push checklist. Ported from the Storydeck skill of the same
name; the gate tables, the SQL-surface list and Step 0 are rewritten for this
repo. The point is that Jim's PR-create flow depends on a few specific
conditions — never on `main`, single-commit branch, title under 65 chars, gates
surfaced, SQL touched flagged for manual verification — so this enforces them
once and we stop drifting.

## Step 0 — Refuse to push from main

**This runs first and it is not advisory.** `CLAUDE.md` puts "NEVER push to
`main`" at the top of the hard guardrails. "Push it" and "ship it" from Jim mean
*open a PR*, never push to main. There is no exception for small or safe-looking
changes.

Gate with a command that **exits non-zero** on main:

```bash
test "$(git rev-parse --abbrev-ref HEAD)" != main
```

Chain it to anything that writes, so a failed gate stops the write rather than
merely warning:

```bash
test "$(git rev-parse --abbrev-ref HEAD)" != main && git push -u origin HEAD
```

Do **not** substitute a command that prints the branch and rely on reading it.
This has already fired for real: Jim merged a PR mid-session in his own
terminal, which moved the checkout back to `main` underneath an in-progress
edit. A printed branch name gets skimmed; a non-zero exit stops the pipeline.

**Re-run this immediately before every stage and every push**, not once at the
start. Jim works this repo in parallel in his own terminal and the checkout can
move under you.

## Step 1 — Commit count + squash decision

```bash
git rev-list --count main..HEAD
git log --oneline main..HEAD
```

**The load-bearing fact:** GitHub auto-populates PR title (from commit subject)
AND PR body (from commit message body) ONLY on a single-commit branch. URL
pre-fill via `?title=`/`?body=` does NOT work on `/pull/new/<branch>`. So a
populated PR requires a single commit with a rich message.

- **If count is 1**: title + body will auto-fill. Continue. (Confirm the message
  has a real body, not just a one-line subject. If it's bare, offer to amend
  with a richer body before push.)
- **If count is 2+ AND no PR exists yet**: default to squash. Compose the
  squashed message with this shape:

  ```
  <subject ≤65 chars — descriptive, not ticket-prefixed; see Step 2>
  <blank line>
  ## Summary
  - 1-3 bullets — what changed in plain English

  ## Why
  - 1-2 sentences — the problem this solves; cite the DECISIONS.md entry

  ## Test plan
  - [x] dotnet build — 0 warnings 0 errors
  - [x] dotnet test — N passed / 0 failed
  - [x] dbscrub report runs clean against the sample config
  - [ ] Manual verification against DbScrubTest — if SQL surfaces touched

  ## Touched gates (eyeball before merge)
  - <CLAUDE.md gate sections touched, or "none">

  ## Linear
  - DBS-N → flipped to In Progress

  Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
  ```

  Then:

  ```bash
  git reset --soft HEAD~N
  git commit -F <message-file>
  test "$(git rev-parse --abbrev-ref HEAD)" != main && git push --force-with-lease
  ```

  **Ask before force-push.** Never force-push without confirmation, even on a
  private feature branch.

- **If each commit is genuinely load-bearing history**: push as-is. The title
  falls back to the branch-name slug; hand Jim the bare URL and tell him plainly
  the body will be empty because of the multi-commit shape.

- **If count is 2+ AND a PR already exists**: just push the new commits. The
  title is set, and force-pushing now risks orphaning review comments. Don't
  squash an open PR's branch unless Jim explicitly asks.

## Step 2 — PR title length and voice

The PR title is the latest commit message's first line (single-commit
branches). Count it:

- **≤ 65 characters**: pass
- **66–70**: warn
- **> 70**: fail. Recommend a shorter title, amend or re-squash.

Count em-dashes and Unicode separators — each takes a column.

**This repo does not prefix commit subjects with the ticket.** The established
style is a descriptive sentence that says what changed and why — "A key that
reads as bytes is refused, because every row would mask to one value". Keep it.
A `DBS-N:` prefix would eat the character budget and clash with the voice.
Ticket linkage comes from the branch name and a trailer (Step 5), not the
subject.

## Step 3 — CLAUDE.md gates touched

```bash
git diff main..HEAD --name-only
git diff main..HEAD --stat
```

Report each gate touched and point at its `CLAUDE.md` section. Don't try to
verify the gate was *satisfied* — surface that it applies so Jim can eyeball it
before merge.

| Gate | Paths / patterns to look for |
|---|---|
| **Safety checks** — localhost allowlist with no override flag, typed confirmation, refuse already-stamped DBs | `src/DbScrub.Core/Safety/ServerAllowlist.cs`, `src/DbScrub.Core/Safety/TypedConfirmation.cs`, `src/DbScrub.Core/Stamp/StampReader.cs` |
| **Stamp only after a clean verify pass** | `src/DbScrub.Core/Stamp/StampWriter.cs`, `src/DbScrub.Core/Verify/SqlVerifier.cs`, `src/DbScrub.Core/Execution/CleanRunner.cs` |
| **Never print PII** — logs, errors, verify output, or tests with realistic seed data | `src/DbScrub.Core/Reporting/**`, `src/DbScrub.Core/Verify/VerifyReport.cs`, any new `Console.`/exception string, `tests/DbScrub.Tests/**` seed data |
| **Audit/log/history truncated, not masked** (D5) | `src/DbScrub.Core/Hygiene/HygienePlanner.cs` |
| **No new NuGet dependencies** without discussing first | `Directory.Packages.props`, any new `<PackageReference>` in a `*.csproj` |
| **Identifier quoting** — table/column names come from config, i.e. user input | `src/DbScrub.Core/Hygiene/SqlIdentifier.cs`, any new interpolated identifier in SQL |
| **`DbScrub.Guard` is step 6 and DEFERRED** — build only when Jim explicitly asks; no changes to `aavsb.sln`, its `web.config`, or any consuming app | `src/DbScrub.Guard/**`, `aavsb.sln`, `web.config` |
| **Rename and orphaned-user repair are deferred indefinitely** (D25) | any new `sp_rename`, `SINGLE_USER`, or login-repair code |

Soft gate — there is no `docs/VOICE.md`, but user-facing output follows a
deliberate plain-English convention: say what things mean, not what SQL Server
calls them. Flag it when `src/DbScrub.Cli/**` or `src/DbScrub.Core/Reporting/**`
change any console string.

## Step 4 — SQL surfaces and definition of done

**The live-SQL integration tier does not exist.** `CLAUDE.md` "Testing tiers"
records the design — `const bool Enabled = false`, skip attributes, an always-on
unit test that source-scans the const — but it was never built (tracked as
`DBS-5`). **Never claim it passed.** There is nothing to pass.

So when the diff touches SQL generation or execution, the honest output is not
"integration tests recommended" but "this needs manual verification and here is
what to run". SQL surfaces:

- `src/DbScrub.Core/Masking/MaskSql.cs`
- `src/DbScrub.Core/Execution/SqlCleanSession.cs`
- `src/DbScrub.Core/Hygiene/HygienePlanner.cs`
- `src/DbScrub.Core/Schema/SchemaInventory.cs`
- `src/DbScrub.Core/Stamp/StampReader.cs`, `StampWriter.cs`
- `src/DbScrub.Core/Verify/SqlVerifier.cs`
- `scripts/create-test-db.sql`

If any are touched, surface this and let Jim run it:

```
Manual verification needed — no automated tier exists.
  Only DbScrubTest on localhost\MSSQLSERVER02 is approved. Nothing else, ever.
  The fixture may be STAMPED from a previous run; rebuild it first:
    sqlcmd -S "localhost\MSSQLSERVER02" -E -i scripts\create-test-db.sql
```

Never run a mutating statement unattended. `CLAUDE.md` "Human decision points"
makes every DDL/DML/TRUNCATE/`sp_rename`/`ALTER DATABASE` Jim's to execute —
the safety checks protect against the wrong database, not the wrong intent.

Also confirm the definition of done from `CLAUDE.md`:

- [ ] Unit tests for the new logic
- [ ] `dbscrub report` still runs clean against the sample config
- [ ] README/SPEC updated if behavior changed

## Step 5 — Linear status flip

There is no GitHub↔Linear integration. Claude via MCP is the only thing keeping
Linear status truthful. Don't skip this step.

dbscrub is tracked in Linear team **`DbScrub` (key `DBS`)**, project
**"DbScrub"**. Extract `DBS-\d+` ticket IDs from:

- the branch name (`jimsowers/dbs-3-docs-disagree...` → `DBS-3`; Linear's
  suggested branch names already carry it)
- `DBS-\d+` anywhere in a commit message in `git log main..HEAD` — subject or
  body. Prefer a trailer line, `Linear: DBS-3`, which keeps the subject
  descriptive per Step 2.

Union the IDs. For each:

1. `mcp__linear__get_issue` to check current status.
2. If status is **Backlog** or **Todo**: `mcp__linear__save_issue` with
   `state: "In Progress"`.
3. If status is already **In Progress / Waiting For / Done / Canceled**: skip.
   Don't reopen completed work, don't disturb a ticket already in flight.

> **Why "In Progress" and not "In Review"?** The DbScrub team has Backlog /
> Todo / In Progress / Waiting For / Done / Canceled / Duplicate — no In Review,
> and no Triage (verified 2026-08-06 against the live workspace). This mirrors
> Storydeck deliberately. The tradeoff is inherited too: "In Progress" collapses
> *actively coding* and *PR open pending merge*, so you can't tell them apart
> from Linear alone — look at GitHub for that. Acceptable for solo-dev; revisit
> if the distinction starts mattering.

**Done-flip catch-up (start-of-run, not end):** at the START of every pr-prep
invocation, before anything else, search recent merges for ticket references:

```bash
git log --merges --oneline main -20
```

For each `DBS-\d+` found: `mcp__linear__get_issue` → if status is **In
Progress**, flip to **Done**. Anything else, skip.

This catches up tickets from the *previous* pr-prep run that Jim has since
merged — no per-merge hook, and nothing for Jim to remember.

If no ticket IDs can be extracted, surface it plainly:
`Linear: no DBS-N ticket found in branch or commits — flip manually if applicable.`

## Step 6 — Hand off

On a single-commit branch the PR is already fully populated by the commit
message — Jim opens the URL and clicks Create. Nothing to paste.

Output:

```
Pre-push checks passed (or: passed with N warnings, listed above).

Pushed: <branch> @ <sha>

Open PR:
  https://github.com/jimsowers/dbscrub/pull/new/<branch>

Touched gates (eyeball before merge):
  - Safety checks (...)
  - No new NuGet dependencies (...)

Manual verification needed:
  - Yes / No, because <reason>

Linear status flipped to In Progress:
  - DBS-3
Already In Progress / Done (skipped):
  - DBS-1
Done-flip catch-up (from previous merges):
  - DBS-4 → Done
```

**Important — do NOT:**

- Push to `main`, ever. Step 0 is the gate; run it before every write.
- Append `?title=` or `?body=` params to a `/pull/new/<branch>` URL. They don't
  work on that endpoint. The populated PR comes from the commit message. If
  multi-commit history is load-bearing and you've decided not to squash, hand
  over the bare URL and say plainly the body will be empty.
- Wrap the PR URL in a code fence — bare URL on its own line, so it stays
  clickable.
- Run `gh pr create` without checking it's authenticated first.
- Force-push without explicit confirmation.
- Claim the live-SQL integration tier passed. It does not exist.

## Standing rules this skill enforces

- Never push to `main` — gated by a non-zero exit, not by reading a printed
  branch name.
- Title ≤ 65 chars, bare URL, single-commit preferred.
- Mutating SQL is Jim's to run, against `DbScrubTest` on
  `localhost\MSSQLSERVER02` and nothing else.
- Never claim the integration tier passed — it does not exist.
- No new NuGet dependency without explicit approval.
- Standard `Co-Authored-By` trailer on the commit.
