# Handoff — end of step 5

Last updated 2026-08-04. **The v0 milestone is MET.** `clean` masks a database,
sweeps it, and marks it clean only if the sweep comes back empty. Steps 1–5 are
done, less rename and orphaned-user repair, which are deferred indefinitely
(DECISIONS.md D25).

421 unit tests, 0 warnings (warnings are errors).

## The local environment

- **SQL Server 2025 Developer Edition**, named instance
  **`localhost\MSSQLSERVER02`**. There is NO default instance, so a bare
  `localhost` will not connect.
- `sqlcmd` 16.0 on PATH.
- .NET SDK 9 and 10; **no .NET 8 SDK**. The 8.0.28 runtime is present, so
  net8.0 output runs. A test asserts the target framework.
- Fixture database `DbScrubTest`:
  `sqlcmd -S "localhost\MSSQLSERVER02" -E -i scripts\create-test-db.sql`
  It drops and recreates, so it is the way back to a known state.
- If it ever refuses to connect with "Server is in single user mode", an
  interrupted fixture rebuild left it that way:
  `sqlcmd -S "localhost\MSSQLSERVER02" -E -Q "ALTER DATABASE DbScrubTest SET MULTI_USER;"`

## What works

| Command | State |
|---|---|
| `dbscrub report` | Read-only. Schema facts, the three phases in execution order, the mask plan with each table's mode, the summary, and the columns with no rule in paste-into-config form. |
| `dbscrub status` | Exit 0 clean, 2 not, 4 refused. |
| `dbscrub clean` | Safety checks, hygiene, mask, verify, stamp. `--yes`, `--dry-run`, `--fail-on-unclassified`. |

Two configs for the fixture, and **the pair is the demo**:
- `config/dbscrubtest.masking.json` — deliberately incomplete, so `report` has
  something to list and `clean` FAILS verify on the two free-text `Notes`
  columns that hide a phone number and an email.
- `config/dbscrubtest.complete.json` — every column resolved, so a run passes
  and earns the stamp. Uses `email` and `scramble`+`unique`, on both a
  single-column key (`dbo.Person`) and a composite one (`app.Membership`).

## VERIFIED against the live database

All by hand, on SQL Server 2025, `localhost\MSSQLSERVER02`.

- `SchemaInventory`, including the primary-key read and the unique-index read.
- `report`, repeatedly, including the plan for both fixture configs (exit 0).
- **The verify → stamp path, end to end.** `clean` with
  `dbscrubtest.complete.json` exits 0 and `status` reports SANITIZED. This had
  never completed before 2026-08-04.
- **The `email` strategy and `scramble`+`unique`**, single-column and composite
  keys: `fakeemail1@notreal.invalid` beside `Xxxxxx#1` in `dbo.Person`, and
  `fakeemail1-100@notreal.invalid` beside `xxx#1-100` in `app.Membership`.
- **Multi-batch walks**, at `"batchSize": 2`, in all three mask modes. The
  masked data is identical to the single-batch run, which is the assertion that
  matters — a broken keyset predicate would show up as a skipped row.
- **A composite primary key**, through a batch boundary. `app.Membership` seeds
  MemberNumber 100 under two organizations on purpose, so a predicate comparing
  the second key column alone would step straight past `(2, 100)`; all five rows
  come back masked and reconciled 5/5.
- **The unique-index refusal** (D27): `static` on `app.Membership.Username`
  exits 5 naming `UQ_Membership_Username`, before anything is modified.

## NOT verified — read this before trusting anything

- **`history: "mask"`** is implemented and unit-tested with no fixture.
- **A filtered unique index** has never reached the inventory. D27 treats one
  like any other, which is conservative and may refuse a `null` strategy that
  would in fact have been legal under a `WHERE col IS NOT NULL` filter.
- **A key type that reads as bytes has never reached SQL Server.** `binary`,
  `varbinary` and the CLR user-defined types are refused at plan time (D28) and
  unit-tested, with no fixture — building a binary-keyed table would cost more
  than the bug is worth.
- **The live-SQL integration tier does not exist** (CLAUDE.md "Testing tiers").
  Everything above was run by hand.

## What is left, in the order I would do it

Item 1 (unique-index reading) is DONE — see D27, and D28 for the collision it
turned up in `scramble`+`unique`.

1. **Computed columns are reported as having no rule.** They cannot be written
   and hold whatever their sources hold, so there is one legal answer —
   `FullName` needed an explicit `keep` in the complete config. System-generated
   columns are already exempt on identical reasoning (`VerdictResolver.
   MakeVerdict`). On AAVSB every computed column sits in that list permanently,
   training people to stop reading it, which is the failure the list exists to
   prevent.
2. **Doc sync.** README and `docs/getting-started.html` still say "the
   unclassified list" and "reported as UNCLASSIFIED" in about six places. The
   OUTPUT now says "columns with no rule" (D-less change, in the voice commit),
   so the docs disagree with the tool. `docs/getting-started.html` also predates
   the `email` strategy and the `unique` modifier; README caught up with both in
   the D27/D28 change.
3. **Uncomment step 3 of `scripts/refresh-local.sample.ps1`.** It is commented
   out because stamping did not exist. It does now.
4. **"Keep only N rows" at table level.** Discussed, not designed in full.
   Wanted because a table with thousands of rows is usually only useful with 50.
   Two things make it non-trivial: trimming a table with inbound foreign keys
   requires trimming the whole graph (that is FK-graph subsetting, on the v2
   roadmap), and `TOP (n)` without `ORDER BY` picks different rows every run.
   The tractable subset is: allow it only on tables nothing references, keep the
   HIGHEST primary keys so it is deterministic and recent, and run it in the
   pre-mask phase so masking then has 50 rows to do instead of 50,000. Needs
   foreign-key reading — the same shape of work as D27's unique-index read, in
   the same place in `SchemaInventory`.
5. **D23's `phone`/`ssn` generators**, if wanted. The verify gate knows three
   shapes; `email` now has a generator for one of them. Nothing needs the other
   two yet.

## The thing that actually blocks value

**`config/aavsb.masking.json` does not exist.** The tool is finished enough to
use and has nothing to use it on. Building it is the report → paste → report
loop against a fresh AAVSB restore, plus the dbatools scan in PROMPT.md section
3 to draft the inventory. Three open questions in DECISIONS.md feed into it: TDE
on prod backups, which free-text columns are known dirty, and the real
temporal/CDC/audit inventory.

That is Jim's work, not code work, and it is the long pole.

## Decisions made in steps 4 and 5

All in `docs/DECISIONS.md` with the rejected alternatives.

- **D19** — no table-valued parameter; per-row UPDATEs bounded by SQL Server's
  2100-parameter limit; `scramble` on a keyless table REFUSED rather than
  approximated with `TRANSLATE`, which silently leaks non-ASCII letters.
- **D20** — a primary key column is never masked.
- **D21** — every table's row count is reconciled before and after, because a
  skipped row is otherwise silent.
- **D22** — (superseded by D25's milestone change) `clean` shipped before verify
  and said so rather than stamping.
- **D23** — uniqueness is a modifier seeded from the primary key, overwriting
  the tail so length survives.
- **D24** — no `CHECKPOINT`, no log shrink. They mitigate nothing and implied a
  cleanup they do not perform.
- **D25** — rename and orphaned-user repair deferred indefinitely; v0's
  definition of done changed to exclude them.
- **D26** — `email` is a GENERATED strategy, because the tool must own the shape
  in order to recognise it later.
- **D27** — unique indexes are read and a strategy that cannot promise a
  distinct value per row is refused at plan time, conservatively: composite and
  filtered indexes are refused on the same terms rather than reasoned about.
- **D28** — the row key is spliced behind a `#` delimiter. Plain concatenation
  was ambiguous — a scrambled digit is a `9` and so is a key digit — and
  produced duplicate values on ~2% of digit-bearing rows. Also: a per-row
  strategy now requires a key that renders as a value, because a `binary` key
  reads as `System.Byte[]` for every row and verify would not have caught it.

## Bugs found in earlier steps, all fixed

1. **`history: "mask"` truncated instead of masking.** `TablePlan` never carried
   the config's history mode.
2. **Static integer narrowing silently did not happen.** A switch expression
   with `byte`/`short`/`int`/`long` arms unifies to `long`.
3. **A correctly masked database could not pass verify.** Both configs replace
   Email with `static "dev@example.invalid"`; the email pattern matches it and
   nothing excused it, so the gate reported the column it had just masked as a
   leak. The run now hands verify the exact values it wrote.
4. **`DisableChangeTracking` named the wrong SQL Server feature.** The code
   disables Change Data Capture; Change Tracking is different and untouched.

The method point behind 2 and 3: both were invisible to every existing test
because the tests asserted the wrong thing — 2 compared values rather than CLR
types, and 3 checked that the report does not ECHO a static value while never
asking what the verify gate thinks of it afterwards.

## Standing rules that bite here

- **NEVER push to `main`.** Feature branch and PR, always. Check the branch with
  a command that GATES the push, not one that merely prints it first:
  `if [ "$(git branch --show-current)" = "main" ]; then exit 1; fi`. This was
  violated twice in one session because a merge mid-session left the checkout on
  `main`.
- **Squash to ONE commit before pushing a branch.** GitHub only pre-populates a
  PR's title and body from the commit message when the branch has a single
  commit; with more it falls back to a slug title and an empty body. Check with
  `git rev-list --count origin/main..HEAD`.
- **Never print PII values**, including in tests. Fixture data uses reserved
  ranges only (`example.invalid`, `555-01xx`, never-issued SSN prefixes) and is
  shaped to match the verify patterns on purpose.
- **First destructive runs are Jim's to approve.** `clean` against `DbScrubTest`
  is already approved for repeat testing; nothing else is.
- Claude never claims the integration tier passed. It does not exist.

## Starter prompt for the next session

> Read CLAUDE.md, docs/SPEC.md, docs/DECISIONS.md, and docs/HANDOFF.md in full
> before writing any code.
>
> v0 is done: `dbscrub clean` masks a database, verifies every string column,
> and stamps it only if that sweep is clean. 421 tests pass.
>
> The whole pipeline is now proven against the live fixture — stamp, `email`,
> `scramble`+`unique`, multi-batch walks and a composite key — so "VERIFIED
> against the live database" in HANDOFF.md is the short list and "NOT verified"
> is the one to read.
>
> "What is left" lists the work in the order I would do it, and none of it is a
> known bug.
>
> The bigger lever is not code: `config/aavsb.masking.json` still does not
> exist, and the tool has nothing to be useful on until it does.
>
> Work on a feature branch, one squashed commit, never push to main.
