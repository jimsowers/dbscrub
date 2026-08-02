# Handoff — end of slice 1

Written 2026-08-02 on a machine with **no SQL Server installed**. Slice 1 is
code-complete and unit-tested, but one part of it has never been executed.
Read the "Not verified" section before trusting anything.

## Where things stand

Slice 1 per CLAUDE.md: config model + validation, schema inventory, verdict
resolution + diff, and the `report` command. All four exist. 63 unit tests
pass. `clean`, `status`, `mask`, and `rename` are deliberately absent, as is
`DbScrub.Guard` (slice 6, deferred until explicitly asked).

```
src/DbScrub.Core/
  Configuration/   MaskingConfig, ConfigError, ConfigInvalidException,
                   JsonPositionIndex, MaskingConfigLoader
  Schema/          DatabaseSchema, ISchemaReader, SchemaInventory
  Verdicts/        ScrubPlan, VerdictResolver
  Reporting/       PlanReport, UnclassifiedFormatter
src/DbScrub.Cli/   Program (System.CommandLine wiring), ReportCommand, ExitCode
tests/             63 tests, no database required
```

## Verified on this machine

- `dotnet build` — 0 warnings, 0 errors (warnings are errors; net8.0 asserted
  by a test because only SDK 9/10 are installed here).
- `dotnet test` — 63 passed.
- `dbscrub report --help` renders.
- Exit codes, run against the real binary:
  - no server reachable -> `1`
  - config with an unqualified table name -> `5`, with `DBS005` and a line number
  - config file missing -> `5`

## NOT verified — do this first on the DB machine

**`SchemaInventory` has never run.** Its three queries compiled but were never
executed. Everything else in slice 1 was testable through `ISchemaReader`,
which is why the rest carries real evidence and this does not.

Specific things likely to break on first contact:

1. **Reader type mismatches.** `temporal_type` is `tinyint` -> `GetByte`;
   `is_tracked_by_cdc` / `is_nullable` / `is_computed` / `is_identity` are
   `bit` -> `GetBoolean`; `max_length` is `smallint` -> `GetInt16`. A wrong
   choice throws `InvalidCastException` at runtime, not compile time.
2. **`sys.databases` permissions.** Reading it needs `VIEW ANY DATABASE` or
   ownership. A locked-down instance may refuse.
3. **The `history_table_id` LEFT JOIN** has never met a real temporal table.
4. **`max_length` is -1 for the `(max)` types** — handled in the model, unproven.

First commands to run:

```bash
dotnet build
dotnet test
dotnet run --project src/DbScrub.Cli -- report --server localhost --database AAVSB --config config/masking.sample.json
```

Expect the third to produce a large UNCLASSIFIED list — that is correct and is
the intended starting point (DECISIONS.md D6). The sample config knows about
three tables that may not even exist in AAVSB, which will show up as `DBS005`
"does not exist" problems and exit `5`. That is the tool working, not failing.

## Open questions for slice 2

1. **Should the localhost allowlist also gate `report`?** It does not today.
   `report` is strictly read-only, so the interlock's stated purpose (SPEC
   section 3: "before mutating") does not cover it. But it still opens a
   connection, and pointing it at production reads production metadata.
   Decide deliberately in slice 2; there is a comment in `ReportCommand`
   warning against settling it by copy-paste.

2. **`report`'s exit code 3 is an interpretation.** SPEC section 2 gives
   `--fail-on-unclassified` to `clean` only, and section 4 says fail mode
   "exits 3 before mutating anything". Since `report` never mutates, a strict
   reading says it should always exit 0. It was implemented to honor
   `defaults.unclassifiedColumns: "fail"` and exit 3, because a report that
   always exits 0 cannot be the CI gate D6 asks for. If that reading is wrong,
   `ReportCommand.ResolveExitCode` is the one place to change.

3. **Static value type-checking is not implemented.** SPEC section 4 says a
   `static` value is "type-checked against column type". Config load cannot do
   it (no schema) and the verdict pass does not yet. Deferred to slice 4, where
   the mask engine builds the actual T-SQL. Doing it half-right earlier would
   produce confident wrong answers about type compatibility.

4. ~~Line endings.~~ Closed: `.gitattributes` now pins `* text=auto eol=lf`,
   so moving between machines does not produce whole-file diffs.

## What the report looks like

Rendered from a synthetic schema, so the shape is real even though the data is not:

```
dbscrub report (read-only — nothing is modified)

  Server    localhost
  Database  AAVSB
  Config    config/masking.sample.json

Schema
  Tables              5
  Columns             20
  CDC enabled         yes
  CDC-tracked tables  1
  Temporal tables     1

Hygiene (runs before masking)
  Disable CDC on AAVSB (sys.sp_cdc_disable_db) — drops all capture tables and jobs
  dbo.Person: SYSTEM_VERSIONING OFF -> handle dbo.PersonHistory -> ON

Plan
  MASK      dbo.Person  (4 of 6 columns)
              FirstName  static    fixed replacement value
              LastName   scramble  letters->x, digits->9, length preserved
              Email      static    fixed replacement value
              Ssn        scramble  letters->x, digits->9, length preserved
  TRUNCATE  dbo.LoginAudit

Summary
  Tables truncated    1
  Tables masked       1
  Columns masked      4
  Columns kept        1
  UNCLASSIFIED        4

UNCLASSIFIED columns (4)
Every one of these is unprotected. Paste the blocks below into your config,
changing "keep" to a real strategy wherever the column actually holds PII.

// dbo.Person is already in your config — add these to its "columns":
  { "name": "Nickname", "strategy": "keep", "reason": "TODO: classify" }

// dbo.Enrollment is not in your config — add this to "tables":
  {
    "name": "dbo.Enrollment",
    "columns": [
      { "name": "EnrollmentId", "strategy": "keep", "reason": "TODO: classify" },
      { "name": "PersonId",     "strategy": "keep", "reason": "TODO: classify" },
      { "name": "Notes",        "strategy": "keep", "reason": "TODO: classify" }
    ]
  },
```

Note what is absent: `dbo.__SanitizationLog` and `dbo.PersonHistory` are real
tables in that schema and are correctly NOT in the UNCLASSIFIED list. A list
padded with rows nobody can act on is a list people learn to skip.

## Starter prompt for the next session

> Read CLAUDE.md, docs/SPEC.md, docs/DECISIONS.md, and docs/HANDOFF.md.
> Slice 1 is complete but SchemaInventory has never run against a real
> database. First: run `dbscrub report` against the local AAVSB restore and fix
> whatever SchemaInventory gets wrong. Then start slice 2 (safety interlock +
> stamp + `status` + rename + orphaned-user repair), beginning with the open
> question about whether the allowlist should gate `report`.
