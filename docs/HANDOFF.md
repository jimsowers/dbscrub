# Handoff — end of slice 1

Last updated 2026-08-02, after slice 1 was verified against a real SQL Server.

Slice 1 per CLAUDE.md — config model + validation, schema inventory, verdict
resolution + diff, and the `report` command — is complete and **verified end to
end against a live database**. `clean`, `status`, masking, and renaming are
deliberately absent, as is `DbScrub.Guard` (slice 6, deferred until explicitly
asked). The tool currently cannot modify anything.

```
src/DbScrub.Core/
  Configuration/   MaskingConfig, ConfigError, ConfigInvalidException,
                   JsonPositionIndex, MaskingConfigLoader
  Schema/          DatabaseSchema, ISchemaReader, SchemaInventory
  Verdicts/        ScrubPlan, VerdictResolver
  Reporting/       PlanReport, UnclassifiedFormatter
src/DbScrub.Cli/   Program (System.CommandLine wiring), ReportCommand, ExitCode
tests/             71 tests, none of which need a database
scripts/           create-test-db.sql — the DbScrubTest fixture
config/            masking.sample.json, dbscrubtest.masking.json
```

## The local environment

- **SQL Server 2025 Developer Edition**, named instance
  **`localhost\MSSQLSERVER02`**. There is NO default instance on this box, so a
  bare `localhost` will not connect.
- `sqlcmd` 16.0 is on PATH (`Client SDK/ODBC/170/Tools/Binn`).
- .NET SDK 9 and 10 installed; **no .NET 8 SDK**. The 8.0.28 runtime is
  present, so net8.0 output runs. A test asserts the target framework because
  an accidental retarget would build here and fail elsewhere.

## Verified

- `dotnet build` — 0 warnings, 0 errors (warnings are errors).
- `dotnet test` — 71 passed.
- **`SchemaInventory` executed against real SQL Server 2025 and was correct on
  first run.** No cast errors. Cross-checked against `sys.tables` /
  `sys.columns`: 4 tables, 30 columns, `temporal_type` 2 and 1,
  `is_tracked_by_cdc` on `app.Enrollment`, `max_length = -1` on the
  `nvarchar(max)` column. The `cdc.*` capture tables were correctly excluded,
  and `dbo.PersonHistory` correctly stayed out of the UNCLASSIFIED list.
- Exit codes, against the real binary: clean run `0`; unclassified columns in
  fail mode `3`; invalid config `5` with a `DBSnnn` code and line number;
  schema-vs-config problems `5`; unreachable server `1`.

## The one bug the live run found

`sys.columns` reports temporal period columns with **`is_computed = 0` AND
`is_identity = 0`**. The original `IsWritable` was `!IsComputed &&
!IsIdentity`, so `ValidFrom` / `ValidTo` looked like ordinary writable
`datetime2` columns. A config could have asked to scramble one, passed every
validation, and failed inside slice 4's mask engine partway through a
destructive run.

Fixed by reading `generated_always_type` (NOT `is_hidden` — a period column
declared without the `HIDDEN` keyword is visible and still unwritable). Also
covers SQL Server 2022+ ledger columns. 8 regression tests in
`SystemGeneratedColumnTests`, and `SchemaBuilder.PeriodStart/PeriodEnd` carry
the exact flag combination SQL Server reports.

Worth remembering as a method point: the unit-test double was built from the
same mental model as the code, so it shared the blind spot. Only a real server
disagreed.

## The fixture

`scripts/create-test-db.sql` builds `DbScrubTest`: four tables chosen so each
kills a specific unknown — a system-versioned temporal table with history, a
CDC-tracked table in a non-`dbo` schema, identity and computed columns, a NOT
NULL column, and an `nvarchar(max)` column. All seed data is fake
(`example.invalid`, `555-01xx`, never-issued SSN ranges) but shaped to match
the verify patterns slice 5 will sweep for.

Rebuild and re-run any time:

```
sqlcmd -S "localhost\MSSQLSERVER02" -E -i scripts\create-test-db.sql
dotnet run --project src/DbScrub.Cli -- report --server "localhost\MSSQLSERVER02" --database DbScrubTest --config config/dbscrubtest.masking.json
```

Expect exit `0` and 6 UNCLASSIFIED columns. Five of them are genuinely
unclassified; the sixth is `dbo.Person.FullName`, a computed column left in the
list on purpose (see below).

## Open questions for slice 2

1. **Should the localhost allowlist also gate `report`?** It does not today.
   `report` is strictly read-only, so the interlock's stated purpose (SPEC
   section 3, "before mutating") does not cover it — but it still opens a
   connection, and pointing it at production reads production metadata. There
   is a comment in `ReportCommand` warning against settling this by copy-paste.

2. **The default allowlist does not cover this machine.** SPEC section 3.1
   defaults to `["localhost", ".", "(local)", "127.0.0.1"]` and says named
   instances count only if listed verbatim. This box has only
   `localhost\MSSQLSERVER02`, so once the interlock ships, `clean` will refuse
   to run here unless the config lists it. `config/dbscrubtest.masking.json`
   already does. The real AAVSB config will need the same treatment — worth a
   deliberate decision rather than a surprise on first run.

3. **`report`'s exit code 3 is an interpretation.** SPEC section 2 gives
   `--fail-on-unclassified` to `clean` only, and section 4 says fail mode
   "exits 3 before mutating anything". Since `report` never mutates, a strict
   reading says it should always exit 0. It was implemented to honor
   `defaults.unclassifiedColumns: "fail"` and exit 3, because a report that
   always exits 0 cannot be the CI gate DECISIONS.md D6 asks for.
   `ReportCommand.ResolveExitCode` is the one place to change it.

4. **Static value type-checking is not implemented.** SPEC section 4 says a
   `static` value is "type-checked against column type". Config load cannot do
   it (no schema) and the verdict pass does not yet. Deferred to slice 4, where
   the mask engine builds the actual T-SQL. Doing it half-right earlier would
   produce confident wrong answers about type compatibility.

5. **Computed columns stay in the UNCLASSIFIED list, on purpose.** A computed
   column can expose PII derived from its inputs — `FullName` leaks if
   `FirstName` and `LastName` are not masked — so it earns a human glance, and
   `keep` is a legal answer that pastes cleanly. Revisit only if the list
   becomes noisy enough that people stop reading it, which is the failure mode
   the exemptions exist to prevent.

## Starter prompt for the next session

> Read CLAUDE.md, docs/SPEC.md, docs/DECISIONS.md, and docs/HANDOFF.md.
> Slice 1 is complete and verified against a live SQL Server 2025 instance at
> `localhost\MSSQLSERVER02` using the DbScrubTest fixture. Start slice 2:
> safety interlock + stamp + `status` command + rename + orphaned-user repair.
> Begin with open questions 1 and 2 in HANDOFF.md — whether the allowlist
> gates `report`, and how named instances reach the allowlist — since both
> change the interlock's shape before any of it is written.
