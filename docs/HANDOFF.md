# Handoff — end of step 4

Last updated 2026-08-02. Steps 1–4 are done. `clean` exists, runs both safety
checks, the hygiene pass and the mask engine — and has now been run for real
against `DbScrubTest`, which is the first time this repository has modified a
database at all.

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
- **`clean` ran end to end against `DbScrubTest`, and the post-run checks below
  passed.** Jim ran it — dry run, then the real thing with the typed
  confirmation — after rebuilding the fixture. This is the first time anything
  in this repository has modified a database, so it is the first evidence that
  the versioning dance, the keyset walk, and the keyless fallback work against a
  real server rather than against a recording double.

  Caveat on how much that proves: one pass, one small fixture, one run. It says
  the statements are valid and the ordering holds. It does not exercise a table
  large enough to take more than one batch, so the LOOP boundaries — the second
  iteration's keyset predicate especially — are still only covered by unit tests
  and by the row-count reconciliation.

## NOT verified — read this before trusting anything below

- **Multi-batch walks.** Every fixture table fits in one batch of 5000, so no
  run has yet taken the `WHERE key > @lo0` path in anger. To force it, set
  `"batchSize": 2` in `config/dbscrubtest.masking.json` and re-run: `dbo.Person`
  has 4 rows, so that is 2 batches plus the terminating empty read. Worth doing
  early in step 5 — it is cheap, and the row-count reconciliation (D21) turns a
  skipped row into a failed run rather than a silent one.
- **Composite primary keys.** The generated lexicographic predicate is
  unit-tested for 1, 2 and 3 key columns, but `DbScrubTest` has only
  single-column keys, so no composite key has ever reached SQL Server.
- `history: "mask"` is implemented and unit-tested but has no fixture. The
  fixture uses the default, `truncate`.
- The integration test tier still does not exist (CLAUDE.md "Testing tiers").
  Everything above was run by hand.

## Re-running against the fixture

Repeat testing against `DbScrubTest` is fine now — the first-run approval has
happened. Rebuild the fixture to get back to a known state, then:

```
src\DbScrub.Cli\bin\Debug\net8.0\dbscrub.exe clean --server "localhost\MSSQLSERVER02" --database DbScrubTest --config config\dbscrubtest.masking.json --dry-run
```

Drop `--dry-run` and type `DbScrubTest` at the prompt for the real thing. The
by-hand checks afterwards — the fixture's seed data is shaped to match the
step 5 verify patterns on purpose:

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
- **The first destructive run against a database is Jim's to approve.** That has
  happened for `DbScrubTest`, so repeat testing against THAT database is now
  fine, which is what `.claude/settings.local.json` permits. It covers nothing
  else — not `sqlcmd`, not another database, and not a first run of any command
  step 5 adds that writes a stamp or renames.
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
> mask engine, and has been run successfully against the DbScrubTest fixture.
> It deliberately does not verify, stamp, or rename. 323 tests pass.
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
