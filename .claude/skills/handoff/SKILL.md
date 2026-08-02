---
description: Create a compact, actionable handoff for continuing this Claude Code session in a fresh context. Use when context is nearly full, work is paused, or the next session needs a clean restart.
argument-hint: "Optional: next-session focus, e.g. finish tests, review auth flow, implement controller changes"
allowed-tools: Read, Write, Bash(git:*), Bash(pwd:*), Bash(ls:*), Bash(mkdir:*), Bash(date:*)
user-invocable: true
---

# Handoff Skill

Create a handoff document that lets a fresh Claude Code session continue safely and efficiently.

<!-- CUSTOMIZE: sections §0 (human-only actions), §4 (hard gates), and §5 (soft
     gates) reference this project's CLAUDE.md rules — update them as the project
     earns gates and defines its human-only action list. -->

## User-provided next-session focus

$ARGUMENTS

If `$ARGUMENTS` is empty, infer the likely next focus from the conversation and repo state.

## Pre-flight: settle the working tree

Before writing the handoff, decide what to do with uncommitted work:

    git status --short

- If the changes are worth keeping but not ready for a real commit, propose a WIP commit on the current branch (`git commit -m "WIP: <one-line>"`) so the next session starts from a clean tree. Wait for user approval before committing.
- If the changes are throwaway, say so in the handoff under "Repo state" and leave them.
- A dirty working tree is the worst thing for a cold restart — surface it explicitly either way.

## Ground the handoff in current repo state

Inspect:

    pwd
    git branch --show-current
    git status --short
    git log --oneline -5
    git diff --stat

Do not paste large diffs. Summarize.

Also count commits ahead of the main branch — if the branch has 2+ commits and no PR yet, capture the squash-or-push-as-is decision in the handoff (see §7).

## Output path

Save the handoff to a predictable, versioned location:

    .claude/handoffs/YYYY-MM-DD-HHMM-<branch-slug>.md

Create the directory if it doesn't exist. Use the current branch with `/` replaced by `-` as the slug. The next session finds the file by listing `.claude/handoffs/`.

If `.claude/handoffs/` is not yet in `.gitignore`, suggest adding it — handoffs are per-session ephemera, not project artifacts.

## Handoff document requirements

Write a compact Markdown document with the structure below.

# Handoff: <short task name>

**Ticket:** <tracker ID + link, or "none">
**Sub-tickets spun off this session:** <IDs, or "none">
**ADR(s) touched / relevant:** `docs/decisions/00X-...` (or "none")
**Branch:** <branch name>
**Commits ahead of main:** N (squash-before-push decision in §7 if >1 and no PR)
**PR:** <PR URL if open, else "not yet created">
**Date:** YYYY-MM-DD

## 0. Human-only actions status

**Did this session involve any action on the project's human-only list (DB mutations, deploys, GUI steps, manually-gated test runs)?**

- [ ] None proposed or executed.
- [ ] Proposed (steps written for the user to run). The user has NOT run them yet. Files: <list>.
- [ ] Proposed and the user confirmed running them at <time>.

**Rule for the next session:** never execute items on the human-only list. Write the steps; the user runs them.

## 1. Current goal

State the user's actual goal in 2–4 sentences. Include the strategic goal, not just the tactical task — the bigger goal frames the right approach.

## 2. Current status

If a todo list or plan is active, copy it verbatim. Otherwise:

- **Done:**
- **In progress:**
- **Not started:**

## 3. Important decisions already made

List decisions the next agent should not re-litigate. Include:

- User preferences voiced this session
- Architecture / naming / UI-behavior choices
- "Do not do X" constraints
- Rejected alternatives (so the next session doesn't re-propose them)
- **Open dependency questions** — any package/SaaS/CDN considered but not approved, marked "DO NOT ADD without explicit user approval."

Do not restate facts already in memory or `CLAUDE.md` — reference them by file/section.

## 4. CLAUDE.md hard gates touched

If the in-flight work touches a gated area, name each gate so the next session re-reads it before editing. If none, write "None."

## 5. Soft gates touched (re-read source doc before editing)

Voice/UX/style docs whose rules bind the touched surfaces (e.g. `docs/VOICE.md` for user-facing copy, `docs/UX-GUIDELINES.md` for UI patterns). If none, write "None."

## 6. Files and artifacts to inspect

List only files that matter. For each: why it matters, what likely needs to change or be checked. Don't duplicate content already in PRDs, ADRs, tickets, or diffs.

## 7. Repo state at handoff

- **Branch:**
- **Commits ahead of main:** N — if N > 1 and no PR exists, record the squash-before-push recommendation (single-commit branches auto-populate the PR from the commit message).
- **Recent commits:** (last 3–5, one line each)
- **Modified/untracked files:** (or "clean working tree")
- **Tests / build:** ran / not run / failing — only what was actually observed this session
- **Manually-gated suites:** **NOT RUN** (default) unless the user explicitly ran them and reported back. Never assert "passed" without their confirmation.

## 8. Remaining work

### Next 1–3 actions
1.
2.
3.

### Defer to next session
Work that needs a clean context, or is too risky to continue here.

## 9. Risks and gotchas

Fragile paths touched, unverified assumptions, possibly-broken behavior, tests to run before next push (and who runs them), files not to edit casually, in-flight changes that break if reverted/rebased.

## 10. Memory candidates

Anything that emerged this session that should become a durable memory (preference, feedback rule, project fact). Do NOT write to memory in this turn — let the next session decide. If nothing, write "None."

## 11. Starter prompt for next Claude Code session

A fenced code block, ready to paste:

```
Read .claude/handoffs/<this-file-name>.md first.

Then, in order:
1. Confirm `git branch --show-current`, `git status --short`, and `git log --oneline -5` match the handoff's "Repo state" section.
2. If §0 flags pending human-only actions the user hasn't run, leave them alone — write any new steps, do not execute.
3. Pull the tracker ticket(s) in the header via MCP for the latest comments/state.
4. If a PR is open, check its current state (CI, review comments).
5. Re-read every CLAUDE.md gate listed in §4 and every soft-gate doc in §5 before editing affected files.
6. If an ADR is listed in the header, re-read it before making changes that might unwind the decision.

Immediate next goal: <one sentence>

Hard rules:
- Decisions in §3 are settled — do not re-debate; rejected alternatives are off the table.
- Never execute items on the human-only action list. Write the steps for the user.
- Never claim a manually-gated suite passed unless the user ran it and said so.
- Never add a new dependency without explicit approval.

At a checkpoint, follow the standard PR workflow (CLAUDE.md Workflow section):
single-commit branch preferred, bare PR URL outside any code fence, subject ≤65 chars.
```

## Style rules

- Be concise. Exact paths, commands, and next actions over prose.
- Do not invent test results, build results, or decisions.
- Mark uncertainty clearly ("appears to", "likely", "not verified").
- Do not paste secrets, tokens, credentials, env files, or connection strings.
- Do not paste large diffs — summarize.
- Do not claim work is complete unless the repo or conversation clearly supports it.
- Do not duplicate facts already in memory or `CLAUDE.md` — reference them.
