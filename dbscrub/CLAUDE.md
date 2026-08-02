# CLAUDE.md — working agreement for this repo

## What this is
DbScrub: a .NET 8 console tool that scrubs PII from a locally restored SQL
Server database (in-place, v0), so daily dev and AI-assisted work never touch
real personal data. `docs/SPEC.md` is the authoritative spec; `docs/DECISIONS.md`
explains why and holds the roadmap. Read both before proposing changes.

## How we work
- Small steps. One vertical slice per session, reviewed before the next.
  Slice order: (1) config model + validation + schema inventory + `report`
  command with the diff/UNCLASSIFIED output, (2) safety interlock + stamp +
  `status` command + rename + orphaned-user repair, (3) hygiene steps
  (CDC/temporal/truncate), (4) mask engine with the four strategies,
  (5) verify gate, (6) DbScrub.Guard micro-library (netstandard2.0;net8.0, READ-ONLY - see SPEC section 8 and DECISIONS D11). Don't jump ahead. Slice 6 is DEFERRED: build it only when the repo owner explicitly asks. Milestone for v0 is the cleaner working MANUALLY (slices 1-5). Make NO changes to aavsb.sln, its web.config, or any consuming application - the aavsb target framework is .NET Framework 4.8, recorded here for when slice 6 eventually starts.
- Teach while building: the repo owner wants to understand every line.
  Prefer boring, readable code over clever code. Explain SQL Server behaviors
  (temporal versioning dance, TRUNCATE vs DELETE + FKs, SINGLE_USER rename)
  in code comments where they occur.
- Challenge the spec when it's wrong, but propose the change in
  DECISIONS.md rather than silently diverging.

## Hard guardrails (never violate)
- Never weaken the safety interlock: localhost-only allowlist with no CLI
  override flag; typed database-name confirmation; refuse already-stamped DBs.
- Never print PII values in logs, errors, verify output, or tests that use
  realistic-looking seed data.
- Audit/log/history tables are truncated, not masked (see DECISIONS.md D5).
- Stamp and rename happen ONLY after a clean verify pass.
- No new NuGet dependencies beyond: Microsoft.Data.SqlClient,
  System.CommandLine, test packages (xunit, FluentAssertions, Testcontainers
  when we get there) without discussing first.

## Conventions
- .NET 8, nullable enabled, implicit usings, file-scoped namespaces.
- No ORM. Parameterized SQL only; identifiers quoted via QUOTENAME-style
  helper (table/column names come from config = user input).
- Projects: src/DbScrub.Core, src/DbScrub.Cli, tests/DbScrub.Tests.
- Exit codes per SPEC.md section 2 — CI depends on them.
- Every schema query goes through one SchemaInventory class so sys.* access
  is testable and in one place.

## Definition of done for any slice
- Unit tests for the new logic.
- `dbscrub report` still runs clean against the sample config.
- README/SPEC updated if behavior changed.
