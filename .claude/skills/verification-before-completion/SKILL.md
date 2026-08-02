---
description: Use before claiming work is complete, fixed, or passing — and before committing or creating PRs. Forces fresh verification evidence (real command output, real test runs, real preview snapshots) before any success-adjacent language. Evidence before assertions, always.
allowed-tools: Read, Bash, Grep, Glob
user-invocable: true
---

# Verification Before Completion

Adapted from [obra/superpowers/skills/verification-before-completion](https://github.com/obra/superpowers/tree/main/skills/verification-before-completion).

## The rule

**NO COMPLETION CLAIMS WITHOUT FRESH VERIFICATION EVIDENCE.**

This applies to every kind of "done":
- "Tests pass" — only after running them in this session and reading the output
- "Build succeeds" — only after a fresh build in this session
- "Fix works" — only after reproducing the bug, applying the fix, re-running the original repro, and seeing it succeed
- "UI looks right" — only after a preview snapshot/screenshot in this session, not the last one
- "Migration applies cleanly" — never claim if DB mutations are a human-only action on this project
- "{Manually-gated suite} passes" — **never claim**; the human runs gated suites, not Claude

Confidence is not evidence. Partial proves nothing. "Should work" / "probably fine" / "looks correct" are red flags — not conclusions.

## Five-step verification protocol

Before any completion claim, do all five steps in order:

1. **Identify the command that proves the claim.** What concrete command, run now, would demonstrate the work is done? If you can't name one, you can't claim done.
2. **Execute the full command freshly in this session.** Not "I ran it earlier." Not "the test file exists." Run it now.
3. **Read the complete output and the exit code.** Don't skim. Check the actual numbers — passed/failed counts, error lines, exit code.
4. **Verify the output confirms the claim.** A passing test count alone isn't proof — verify it tested the thing you changed (e.g. the new test you added is in the count). For UI work, verify the snapshot shows the expected element with the expected text.
5. **Only then make the claim.** Quote the relevant output line in the message so the user can verify your verification.

## What counts as evidence by work type

<!-- CUSTOMIZE: adjust commands/rows to this project's stack once it's known. -->

| Work type | Evidence required |
|---|---|
| Code change with unit tests | Test-runner output for the affected tests, run this session |
| Code change without tests | Build output + the closest existing suite's output |
| Architectural/source-scan test added or changed | The full test class's output, not just one method |
| UI / view change | Preview snapshot showing the changed element + matching text |
| CSS / layout change | Screenshot (before + after when feasible) |
| Background service / startup logic | Fresh-start logs showing the expected init line |
| Database query / schema change | Build output + describe what the human should observe; never run against the DB if DB writes are human-only |
| Manually-gated suites (integration, E2E) | Never claim. Hand to the human: "this touches {surface} — please run the {suite}." |
| Refactor with no behavior change | Build + the full affected test suites + a diff narrative confirming no behavior change |

## Red-flag phrases to catch yourself using

If you find yourself typing any of these, stop and run the verification:

- "should work"
- "probably fine"
- "looks correct"
- "seems to pass"
- "tests should still pass"
- "the fix is straightforward, so…"
- "I believe this resolves it"
- "no other changes needed"

Replace with the verification step and quote the output.

## Three common failure modes

1. **Trusting a previous run.** "I ran the tests earlier and they passed" is not current evidence. Code has changed since then. Re-run.
2. **Partial substituting for whole.** Linter passing doesn't mean build passes. Unit tests passing doesn't mean integration scenarios work. One file compiling doesn't mean the solution builds.
3. **Trusting agent reports.** If you dispatched an Agent and it said "done," verify the actual diff or the actual test output. Agents summarize their intent, not always their outcomes.

## The handoff posture

When you genuinely can't verify (manually-gated suites, human-only DB writes, preview-blocked changes), don't simulate verification. Say so explicitly:

- "Unit tests pass (output below). Integration tests **not run** — this touches {surface}, please run the suite when you're ready."
- "Build passes. DB migration generated but **not applied** — please review and run when you're ready."

Honest "not verified" beats false "complete" every time.

## When the user invokes this skill directly

If they type `/verification-before-completion` or ask "verify this is really done," run through the protocol explicitly for the current piece of work and report each step's evidence. Don't shortcut.
