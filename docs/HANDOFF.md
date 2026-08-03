# Handoff — end of step 4

Last updated 2026-08-02. Steps 1–4 are done. `clean` exists and can modify a
database: it runs both safety checks, the hygiene pass, and the mask engine.

**It does not stamp.** The verify gate that earns a stamp is step 5, and
CLAUDE.md allows a stamp only after a clean verify pass (DECISIONS.md D22). So a
successful `clean` exits 0, says plainly that the database is NOT stamped, and
`dbscrub status` keeps answering "not sanitized". That is correct, not unfinished
business to paper over.

## The local environment

- **SQL Server 2025 Developer Edition**, named instance
  **`localhost\MSSQLSERVER02`**. There is NO default instance, so a bare
  `localhost` will not connect.
- `sqlcmd` 16.0 on PATH.
- .NET SDK 9 and 10; **no .NET 8 SDK**. The 8.0.28 runtime is present, so
  net8.0 output runs. A test asserts the target framework.
- Fixture database `DbScrubTest`. **The fixture script changed in step 4** — it
  now creates a fifth table, the heap `dbo.ContactImport`. Rebuild before doing
  anything else:
  `sqlcmd -S "localhost\MSSQLSERVER02" -E -i scripts\create-test-db.sql`
  It drops and recreates, so it is also the way back to a known state after a
  destructive run.

## What works

| Command | State |
|---|---|
| `dbscrub report` | Read-only. Prints the three phases in execution order, the mask plan with each table's mode, the summary, exclusions, and the UNCLASSIFIED list in paste-into-config form. |
| `dbscrub status` | Exit 0 stamped, 2 unstamped, 4 refused. The stamped path is still unit-tested only — nothing writes a stamp yet. |
| `dbscrub clean` | Safety checks, hygiene, mask. `--yes`, `--dry-run`, `--fail-on-unclassified`. No verify, no stamp, no rename, no user repair. |

323 unit tests, 0 warnings (warnings are errors).

## Verified against the live database

- `SchemaInventory`'s new primary-key query — correct on first execution against
  SQL Server 2025. `report` resolves `dbo.Person` to "row by row, batched on the
  primary key", which only happens if `PersonId` was read in key order.
- `report` prints all five pre-mask statements and the single post-mask reattach,
  in the right places relative to the Mask section.
- The stale-entry check: pointing the updated config at the not-yet-rebuilt
  fixture correctly refused with DBS005 and exit 5.

## NOT verified — read this before trusting anything below

- **`clean` has never run.** Not once, not with `--dry-run`. Every statement it
  would send is unit-tested as a string, and the ordering is unit-tested against
  a recording double, but nothing has been executed by SQL Server. The next
  session's first job is the sequence in "First run" below, and the first
  destructive run is Jim's to approve (CLAUDE.md).
- The batch LOOPS in `SqlCleanSession` (keyset walk, transaction per batch,
  parameter binding) are the part unit tests cannot reach. The row-count
  reconciliation (D21) is the designed backstop: if the walk skips rows, the run
  fails loudly instead of reporting success.
- `history: "mask"` is implemented and unit-tested but has no fixture. The
  fixture uses the default, `truncate`.

## First run — the sequence to use

Rebuild the fixture first, then:

```
src\DbScrub.Cli\bin\Debug\net8.0\dbscrub.exe clean --server "localhost\MSSQLSERVER02" --database DbScrubTest --config config\dbscrubtest.masking.json --dry-run
```

Then drop `--dry-run` and type `DbScrubTest` at the prompt. Afterwards, check by
hand — the fixture's seed data is shaped to match the step 5 verify patterns on
purpose, so the interesting queries are:

- `SELECT * FROM dbo.Person` — FirstName all `Dev`, Email all
  `dev@example.invalid`, Ssn `999-99-9999`, Phone `999-999-9999`, LastName all
  x/X. `Notes` is UNCLASSIFIED, so it should still hold its original text — that
  is the warn-mode gap made visible, not a bug.
- `SELECT COUNT(*) FROM dbo.PersonHistory` — 0, and **not** 4. A non-zero count
  means the versioning dance failed and masking refilled history: the exact bug
  the pre/post split exists to prevent.
- `SELECT COUNT(*) FROM dbo.LoginAudit` — 0.
- `SELECT * FROM dbo.ContactImport` — the keyless path. Email replaced, Phone
  NULL, Notes `[redacted]`, and still **four** rows: two of them are identical
  by design, because a heap has nothing to tell them apart.
- `SELECT is_cdc_enabled FROM sys.databases WHERE name = 'DbScrubTest'` — 0.
- `SELECT temporal_type_desc FROM sys.tables WHERE name = 'Person'` —
  `SYSTEM_VERSIONED_TEMPORAL_TABLE`. Anything else means versioning was left off.

After that first run, testing `clean` freely against `DbScrubTest` is fine.

## What step 5 needs

The verify gate (SPEC 5.4), then stamp and rename (5.5), then user repair (5.6).

1. **Verify must ignore all-placeholder values** or a correctly scrubbed database
   can never be stamped (DECISIONS.md D17). `Scrambler.LooksScrambled` exists for
   exactly this and has a property test proving every scrambler output satisfies
   it. `999-99-9999` matches the SSN pattern; that is not a bug in either piece.
2. **The stamp is the whole clean/dirty signal** since D10 removed the naming
   distinction. Writing one without the verify pass that earns it makes `status`
   — and later the read-only Guard — confidently wrong.
3. **`--rename-to` and `--replace` are not wired.** Deliberately: SPEC 5.5
   renames only after a clean verify pass. Add the options in the same step that
   makes them legal.
4. **`CleanCommand.Confirm` passes `renameTo: null`** to the confirmation
   summary. `TypedConfirmation.BuildSummary` already handles a rename target;
   wire it when rename ships, or the operator confirms a run without seeing the
   rename in the summary.
5. **The order after masking is fixed by CLAUDE.md**: verify → stamp → rename.
   `CleanRunner` returns a `CleanOutcome`; verify slots in after it and before
   anything writes a stamp.

## Design decisions made in step 4

All in `docs/DECISIONS.md` with the rejected alternatives:

- **D19** — no table-valued parameter (it would need DDL in the target database);
  per-row parameterized UPDATEs bounded by SQL Server's 2100-parameter limit;
  constant strategies never read a row; `scramble` on a keyless table is REFUSED
  rather than approximated with `TRANSLATE`, which silently leaks non-ASCII
  letters.
- **D20** — a primary key column is never masked. It breaks the walk, collides,
  and orphans references.
- **D21** — every table's row count is reconciled before and after. A skipped row
  is otherwise silent, and silence here means unmasked PII.
- **D22** — `clean` ships before verify and says so rather than stamping.

## Bugs step 4 found in earlier steps (both fixed)

1. **`history: "mask"` truncated instead of masking.** `HygienePlanner` emptied
   temporal history unconditionally, because `TablePlan` never carried the
   config's history mode. The keyword read as intent and did the opposite.
2. **Static integer narrowing silently did not happen.** A switch expression with
   `byte`/`short`/`int`/`long` arms has no common type, so C# unified them to
   `long` and every arm was widened back before boxing. Caught by a test that
   asserted the CLR type rather than the value; the `(object)` casts in
   `StaticValue` are load-bearing.

The method point behind both: the second one was invisible to any test that
compared values, and only showed up because the test asserted the type. The
first was invisible to every test because nothing exercised that keyword at all.

## Standing rules that bite here

- **NEVER push to `main`.** Feature branch and PR, always. "Push it" means open
  a PR.
- **The first destructive run against a database is Jim's to approve**, even
  though `.claude/settings.local.json` permits the `dbscrub` binary against
  `DbScrubTest`. That permission is for repeat testing after the first run, and
  covers only that database — not `sqlcmd`, not any other database.
- **Never print PII values**, including in tests. Fixture data uses reserved
  ranges only (`example.invalid`, `555-01xx`, never-issued SSN prefixes) and is
  shaped to match the verify patterns on purpose.
- Claude never claims the integration tier passed. That tier does not exist yet;
  when a diff touches SQL generation or execution, name the suite Jim should run.

## Starter prompt for the next session

> Read CLAUDE.md, docs/SPEC.md, docs/DECISIONS.md, and docs/HANDOFF.md in full
> before writing any code.
>
> Steps 1–4 are done. `clean` runs the safety checks, the hygiene pass and the
> mask engine against a real database, but it deliberately does not verify,
> stamp, or rename. 323 tests pass.
>
> This session is step 5: the verify gate, then stamp and rename, then orphaned
> user repair — in that order, because a stamp is only ever written after a clean
> verify pass.
>
> Read "What step 5 needs" in HANDOFF.md first, especially the point about verify
> ignoring all-placeholder values (DECISIONS.md D17) — without it a correctly
> scrubbed database can never be stamped.
>
> Work on a feature branch. Never push to main.
