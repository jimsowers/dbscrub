# CLAUDE.md — working agreement for this repo

## What this is
DbScrub: a .NET 10 console tool that scrubs PII from a locally restored SQL
Server database (in-place, v0), so daily dev and AI-assisted work never touch
real personal data. `docs/SPEC.md` is the authoritative spec; `docs/DECISIONS.md`
explains why and holds the roadmap. Read both before proposing changes.

## How we work
- Small steps. One step per session, reviewed before the next.
  Step order: (1) config model + validation + schema inventory + `report`
  command with the diff/UNCLASSIFIED output, (2) safety checks + stamp READING
  + `status` command, (3) hygiene steps (CDC/temporal/truncate), (4) mask
  engine with the four strategies + `clean` wiring the above together,
  (5) verify gate + stamp WRITING,
  (6) DbScrub.Guard micro-library (netstandard2.0;net10.0, READ-ONLY - see SPEC section 8 and DECISIONS D11). Don't jump ahead. Step 6 is DEFERRED: build it only when the repo owner explicitly asks. Make NO changes to aavsb.sln, its web.config, or any consuming application - the aavsb target framework is .NET Framework 4.8, recorded here for when step 6 eventually starts.
  The stamp splits across two steps — step 2 reads it (to refuse an
  already-clean database), step 5 writes it (once verify has earned it).
- Rename and orphaned-user repair are DEFERRED INDEFINITELY (DECISIONS.md D25).
  They were step 5's back half. Nothing uses either: cleaning happens in place
  (D10) and the team restore script owns login setup (D9), and rename is the
  most destructive code the tool could contain. They stay designed and unbuilt.
- Milestone for v0 is therefore the cleaner working MANUALLY: steps 1-5 less
  those two. That milestone is now MET — v0 is done.
- Teach while building: the repo owner wants to understand every line.
  Prefer boring, readable code over clever code. Explain SQL Server behaviors
  (temporal versioning dance, TRUNCATE vs DELETE + FKs, SINGLE_USER rename)
  in code comments where they occur.
- Challenge the spec when it's wrong, but propose the change in
  DECISIONS.md rather than silently diverging.

## Hard guardrails (never violate)
- NEVER push to `main`. Every change arrives via a feature branch and a pull
  request. "Push it" / "ship it" from the repo owner means open a PR, never
  push to main. There is no exception for small or safe-looking changes.
- Never weaken the safety checks: localhost-only allowlist with no CLI
  override flag; typed database-name confirmation; refuse already-stamped DBs.
- Never print PII values in logs, errors, verify output, or tests that use
  realistic-looking seed data.
- Audit/log/history tables are truncated, not masked (see DECISIONS.md D5).
- Stamp and rename happen ONLY after a clean verify pass.
- No new NuGet dependencies beyond: Microsoft.Data.SqlClient,
  System.CommandLine, test packages (xunit, Testcontainers when we get there)
  without discussing first. NOT FluentAssertions — v8 moved to a paid
  commercial license; we use xunit's built-in Assert (DECISIONS.md D13).
  All versions live in Directory.Packages.props so the list stays auditable.

## Conventions
- .NET 10, nullable enabled, implicit usings, file-scoped namespaces.
- No ORM. Parameterized SQL only; identifiers quoted via QUOTENAME-style
  helper (table/column names come from config = user input).
- Projects: src/DbScrub.Core, src/DbScrub.Cli, tests/DbScrub.Tests.
- Exit codes per SPEC.md section 2 — CI depends on them.
- Every schema query goes through one SchemaInventory class so sys.* access
  is testable and in one place.

## Definition of done for any step
- Unit tests for the new logic.
- `dbscrub report` still runs clean against the sample config.
- README/SPEC updated if behavior changed.

## Human decision points (Claude prepares, Jim executes or approves)
Claude may draft, explain, and stage any of the following, but never runs them
unattended:
- Any statement that mutates a database — DDL, DML, TRUNCATE, sp_rename,
  ALTER DATABASE. This holds even after the safety checks ship: they
  protect against the wrong database, not against the wrong intent.
- The first run of any destructive command against a newly restored database.
- Global git config changes, force-push, or anything rewriting pushed history.
- Adding a permission rule that lets a database-touching command run without a
  prompt. Revisit only after the safety checks (SPEC section 3) exist and
  have a passing test; scope it to a concrete command shape, never a wildcard.
- Running the live-SQL test tier (below).

## Testing tiers
| Tier | Runs | Gating |
|---|---|---|
| Unit | every `dotnet test`, CI | none — the net everything else leans on |
| Integration (live SQL Server) | manually, by Jim, on demand | `const bool Enabled = false` in the test project + skip attributes + an always-on unit test that source-scans the const and fails if it was committed as `true` |

Claude never claims the integration tier passed. When a diff touches SQL
generation or execution, Claude stops and names the suite Jim should run
before reporting the work done.
