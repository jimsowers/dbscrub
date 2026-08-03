# Handoff — end of step 3

Last updated 2026-08-02. Steps 1, 2 (mostly), and 3 are done. The tool can
READ a database and tell you exactly what it would change, and it can now
GENERATE every destructive statement — but **nothing has ever executed one**.

## The local environment

- **SQL Server 2025 Developer Edition**, named instance
  **`localhost\MSSQLSERVER02`**. There is NO default instance, so a bare
  `localhost` will not connect.
- `sqlcmd` 16.0 on PATH.
- .NET SDK 9 and 10; **no .NET 8 SDK**. The 8.0.28 runtime is present, so
  net8.0 output runs. A test asserts the target framework.
- Fixture database `DbScrubTest` exists. Rebuild it any time with
  `sqlcmd -S "localhost\MSSQLSERVER02" -E -i scripts\create-test-db.sql` —
  it drops and recreates, so it is the way back to a known state once
  destructive testing starts.

## What works

| Command | State |
|---|---|
| `dbscrub report` | Done. Read-only. Prints schema facts, the exact hygiene SQL, the mask plan, the summary, exclusions, and the UNCLASSIFIED list in paste-into-config form. |
| `dbscrub status` | Done. Exit 0 stamped, 2 unstamped, 4 refused. |
| `dbscrub clean` | **Does not exist.** Nothing can modify a database. |

218 unit tests, 0 warnings (warnings are errors).

## Verified against the live database

- `SchemaInventory` — correct on first execution against SQL Server 2025.
  Cross-checked against `sys.tables`/`sys.columns`.
- The server allowlist, both directions: same server and database, two configs,
  and only the config naming `localhost\MSSQLSERVER02` was allowed to connect.
- `status` — refused without a config (exit 4); "NOT SANITIZED" with one
  (exit 2).
- Table-level `keep` — excluding `app.Enrollment` dropped UNCLASSIFIED from 6
  to 3 and listed the table with its reason.
- `report` prints all 5 hygiene statements for `DbScrubTest` with correct
  bracketing, including the non-`dbo` schema case.

## NOT verified

- **No hygiene statement has ever run.** `HygienePlanner` builds them; nothing
  calls the output. The SYSTEM_VERSIONING dance in particular is unproven
  against a real temporal table.
- **The `SANITIZED` path of `status`** is unit-tested only, because nothing can
  write a stamp yet.

## Bugs the live database found (both fixed)

1. **`GENERATED ALWAYS` columns looked writable.** `sys.columns` reports
   temporal period columns with `is_computed = 0` AND `is_identity = 0`, so the
   original `IsWritable` classified `ValidFrom` as an ordinary `datetime2`. A
   config could have asked to scramble it, passed validation, and failed inside
   the mask engine mid-run. Fixed by reading `generated_always_type`.
2. **Combining accents survived scrambling.** In the decomposed encoding, the
   accent on "é" is a Unicode MARK, not a letter, so `char.IsLetter` missed it —
   leaking that a name was accented. Found by a test.

The method point behind both: a unit-test double built from the same mental
model as the code shares its blind spots. The first needed a real server; the
second needed a test written to be awkward.

## Start here: what step 4 needs first

**`SchemaInventory` does not read primary keys.** The mask engine batches
updates on ordered PK ranges (SPEC 5.3), so this is the first concrete task and
it is easy to miss because everything else about the inventory is finished.
Add it to the existing `SchemaInventory` — CLAUDE.md requires all `sys.*`
access to live in that one class.

Tables with no PK fall back to a single set-based UPDATE per column with a
warning (SPEC 5.3). `DbScrubTest` has a PK on every table, so that fallback has
no fixture — worth adding one.

## Traps waiting in steps 4 and 5

1. **Masking runs with SYSTEM_VERSIONING OFF and reattaches only at the end.**
   The hygiene pass detaches and reattaches as three adjacent steps today,
   which is correct for emptying history but WRONG once masking sits between
   them. `clean` must interleave: detach all -> hygiene -> mask -> reattach all.
   `HygienePlanner` will need splitting, and its ordering test rewritten to
   assert the interleaved sequence.

2. **Verify must ignore all-placeholder values** (D17), or a correctly scrubbed
   database can never be stamped. `Scrambler.LooksScrambled` exists for this and
   has a property test proving every scrambler output satisfies it.

3. **Static values need type-checking against the column** (SPEC section 4),
   still not implemented. Deferred on purpose — the mask engine is where the
   actual T-SQL conversion happens.

4. **`--yes` is required for any non-interactive run.** It now works on named
   local instances (D18). Without it `clean` prompts, which no shell-driven
   test can satisfy.

## Standing rules that bite here

- **NEVER push to `main`.** Feature branch and PR, always. "Push it" means open
  a PR.
- **The first destructive run against a database is Jim's to approve**, even
  though `.claude/settings.local.json` now permits the `dbscrub` binary against
  `DbScrubTest`. That permission is for repeat testing after the first run, and
  covers only that database — not `sqlcmd`, not any other database.
- **Never print PII values**, including in tests. Fixture data uses reserved
  ranges only (`example.invalid`, `555-01xx`, never-issued SSN prefixes) and is
  shaped to match the verify patterns on purpose.
- Rename and orphaned-user repair are still unbuilt. They were deliberately
  left out of step 2 because their only caller is `clean`; they ship with it.

## Decisions made since the spec was written

D12 hand-rolled config validation · D13 xunit Assert, no assertion library ·
D14 the allowlist gates every command · D15 `status` takes an optional
`--config` · D16 fail-safe stamp reading · D17 verify ignores all-placeholder
values · D18 `--yes` compares the host portion. All in `docs/DECISIONS.md` with
the reasoning and the rejected alternatives.

## Starter prompt for the next session

> Read CLAUDE.md, docs/SPEC.md, docs/DECISIONS.md, and docs/HANDOFF.md in full
> before writing any code.
>
> Steps 1-3 are done and merged. The tool reads a database, reports exactly what
> it would change, and generates every destructive statement — but nothing has
> executed one yet. 218 tests pass.
>
> This session is step 4: the mask engine, and the `clean` command that ties it
> together with the hygiene pass and the two safety checks.
>
> Start by adding primary-key reading to SchemaInventory — the mask engine
> batches on ordered PK ranges and the inventory does not read them yet. Then
> build the batched UPDATE generation for the four strategies, then wire
> `clean`.
>
> Read "Traps waiting in steps 4 and 5" in HANDOFF.md first: the hygiene pass
> currently detaches and reattaches temporal versioning as three adjacent steps,
> which becomes wrong once masking sits between them.
>
> Do NOT run `clean` against DbScrubTest until I approve the first run. After
> that first run, testing it freely is fine.
>
> Work on a feature branch. Never push to main.
