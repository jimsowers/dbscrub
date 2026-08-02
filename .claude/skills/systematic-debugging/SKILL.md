---
description: Use when investigating a bug, test failure, production incident, unexpected behavior, performance issue, or build failure — particularly under time pressure or after a quick fix has already failed. Forces four-phase root-cause investigation before any fix attempt. No fixes without root cause first.
allowed-tools: Read, Grep, Glob, Bash, Edit
user-invocable: true
---

# Systematic Debugging

Adapted from [obra/superpowers/skills/systematic-debugging](https://github.com/obra/superpowers/tree/main/skills/systematic-debugging).

## The rule

**NO FIXES WITHOUT ROOT CAUSE INVESTIGATION FIRST.**

Treating symptoms instead of causes is how bugs come back with interest — the first plausible-looking fix that isn't the actual fix buys weeks of silent breakage. When you feel pressure to jump straight to a fix, that's exactly when this skill matters most.

## Four-phase methodology

### Phase 1: Root cause investigation

Before touching any code:

1. **Reproduce the bug consistently.** If you can't reproduce it on demand, you can't verify the fix. Browser bugs: drive the failing flow in the preview and capture console/network/server logs. Backend bugs: find the failing test or the failing journey; check the app's own audit/log surfaces (see "Project-specific debugging assets" below).
2. **Read the full error.** Stack trace, inner exceptions, exit codes. Don't truncate.
3. **Review recent changes.** `git log -p -- <affected-file>` for the file, `git log --oneline -20` for the branch. The change that caused the bug is almost always in the last few commits.
4. **Add instrumentation at component boundaries.** For multi-stage flows, log inputs/outputs at each boundary so the failure point is unambiguous before you guess.

Output of Phase 1: a one-sentence statement of what fails, when it fails, and what specific evidence proves the failure point.

### Phase 2: Pattern analysis

Don't fix yet. First understand the surrounding pattern:

1. **Find similar working code.** If sibling A works and sibling B doesn't, the diff between them often *is* the bug.
2. **Consult the reference implementation.** Architectural tests and ADRs name the contracts. Read the contract before guessing.
3. **Identify the specific difference.** What does the working version do that the broken one doesn't? Be concrete — name the line, not "the approach."
4. **Map all dependencies.** What does the broken code touch — DB queries, decorators, injected services, framework lifecycle? Bugs often hide in DI registration or decorator order.

### Phase 3: Hypothesis and testing

Scientific method, not vibes:

1. **Formulate one specific hypothesis** about the root cause. Concrete and falsifiable.
2. **Test minimally.** Change one variable. If you change three things and the bug goes away, you don't know which fix mattered, and the other two might break something else.
3. **Verify the hypothesis was right** before moving to implementation. If the test still fails after your minimal change, your hypothesis was wrong — go back to Phase 1, don't add a second guess on top.

### Phase 4: Implementation

1. **Write a failing test first** that reproduces the bug. This is the regression lock — it fails before the fix, passes after.
2. **Apply a single fix that addresses the root cause**, not a symptom band-aid. If your fix is "catch the exception higher up" or "add a null check at the call site," you're treating a symptom — go back to Phase 1 and find what's producing the null/exception.
3. **Verify the failing test now passes** AND **all previously-passing tests still pass.** Run the affected suite, not just the one test.
4. **The three-attempt rule.** If you've tried three fixes and the bug persists, **stop fixing**. The architecture is probably wrong. Write down what you've tried and why each failed, and reconsider whether the design itself is the problem. No fourth attempt without that reset.

## When to apply

- Test failures (especially flaky ones — flakiness is a debugging signal, not noise)
- Production bugs, unexpected behavior ("works locally, breaks on staging")
- Performance regressions
- Build failures that aren't an obvious typo
- "It used to work" reports

Particularly important when: you're under time pressure; a quick fix is tempting and "obviously right"; the bug has had a previous failed fix attempt; multiple components are involved.

## Red flags that mean you're skipping the method

- Proposing a code change in the first message about the bug
- Trying multiple fixes simultaneously to "see what works"
- Skipping the failing test ("I'll add the test after the fix")
- Continuing past three failed attempts without architectural review
- Saying "it's probably X" without producing evidence that it's X
- Fixing it without being able to explain *why* the original code failed

## Project-specific debugging assets

<!-- CUSTOMIZE: list this project's answer-carrying surfaces as they come to exist.
     Examples from the source project: an admin notification-log page, an audit
     table for AI decisions, hosted-platform logs, a dev mail-drop folder,
     architectural tests whose failure messages explain the contract, ADRs that
     document why things are the way they are. -->

- {surface} — {what questions it answers}
