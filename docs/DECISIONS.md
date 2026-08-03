# Decisions & Roadmap

Short ADR-style log so future sessions (and future me) know WHY, not just what.

## D1 — Build our own tool, but steal proven designs
Surveyed: dbatools (`Invoke-DbaDbPiiScan`, `New-DbaDbMaskingConfig`,
`Invoke-DbaDbDataMasking` — mature, PowerShell, JSON config, deterministic
support), Steveiwonder/DataMasker (.NET + Bogus, small personal repo),
Neosync (excellent design, acquired and no longer maintained), Greenmask
(Postgres only), Microsoft Static Data Masking (killed before GA — died on
constraints/uniqueness/referential integrity, which is the lesson).
Decision: own the tool (the safety checks, stamp/rename ritual, CDC/temporal
hygiene, and the verify gate are the point, and none come in a box), but model
the config on dbatools' and use its PII scanner to draft the column inventory.

## D2 — v0 is "keep me out of trouble", not "realistic test data"
Blunt strategies only (null/static/scramble/truncate). Obviously-fake data is
safer for this tier: it can't be mistaken for real in an email or AI prompt.
Bogus/realism deferred to v1.

## D3 — Mode confusion is the top risk of a raw+clean workflow (naming part superseded by D10)
Sometimes the raw copy is legitimately needed (prod support). Countermeasures:
naming ritual (restore as `AAVSB_RAW`; only the tool produces the
app-expected name `AAVSB` — see D8), apps keep pointing at `AAVSB` unchanged
so they physically can't see raw copies,
`Sanitized` extended property as the machine-checkable stamp, and (future) a
dev-environment startup guard in apps that refuses unstamped databases.

## D4 — The safety checks are non-negotiable
Localhost allowlist with no CLI override, typed database-name confirmation,
refuse already-stamped databases. A masking tool with a mistyped connection
string is a production incident.

## D5 — Audit/log tables are truncated, never masked
Old values hide in JSON/XML payloads no column mask can reach. Dev value of
stale audit rows ~ zero. Same default for temporal history tables (with the
SYSTEM_VERSIONING OFF/ON dance, because masking a temporal table otherwise
copies unmasked rows INTO history). CDC is disabled outright
(`sys.sp_cdc_disable_db`).

## D6 — Warn-mode first, fail-closed later
Column inventory is partial today. Every run prints UNCLASSIFIED columns in
paste-into-config form; flip `unclassifiedColumns` to `fail` once the
inventory is complete, and keep it there — new prod columns must then break
the run instead of leaking.

## D7 — In-place cleaning is an accepted compromise for this tier
MDF/log still contain pre-images after masking (log before-images, ghost
records). Threat model here is accidental sharing of query results, not disk
forensics. Mitigation: SIMPLE recovery + checkpoint + log shrink. Real fix is
the v1 quarantine pipeline.

## D8 — The app-expected name is reserved for sanitized copies (local flow superseded by D10; pattern retained for v1 pipeline)
Constraint discovered: a consuming solution's web.config points at
`Initial Catalog=AAVSB` and other devs' workflow cannot change. Resolution:
flip the rename target — raw restores are `AAVSB_RAW`, and the scrubber is
the only thing allowed to produce a database named `AAVSB` (clean -> verify ->
stamp -> rename). Apps and teammates change nothing; while a raw copy exists
it is invisible to every connection string. Running the app against raw data
requires deliberately restoring AS `AAVSB`; `dbscrub status` is the
machine check for whether the current `AAVSB` is stamped.

## D9 — The tool repairs orphaned SQL users, but never touches passwords
The consuming app uses SQL auth (`aavsbweb`). Restored backups carry the DB
user mapped to the prod login SID -> orphaned user on every fresh restore.
The tool remaps users listed in `repairUsers` to same-named local logins
(`ALTER USER ... WITH LOGIN = ...`) so a scrubbed DB is ready-to-use. It never
creates logins and the config never contains passwords; if the login is
missing it prints the one-time CREATE LOGIN command and moves on. (Separate
known issue, out of scope: shared dev password committed in web.config —
legacy pattern, revisit someday, not a PII concern.)

## D10 — dbscrub fits AFTER the existing team restore script, unchanged
The team has a shared T-SQL restore script that restores the .bak directly as
`AAVSB`, drops/recreates the `aavsbweb` login and users, sets SIMPLE recovery,
and has its own server-name prod guard. That script is load-bearing for other
devs and does NOT change. Consequences:
- Default clean mode is IN-PLACE against `AAVSB` (no rename). The rename
  ritual (D8) survives as an opt-in `--rename-to` and as the design for the
  v1 quarantine pipeline, where the team script won't run.
- `repairUsers` is empty for AAVSB — the script owns login/user setup, and
  two tools sharing one responsibility is how weird bugs are born.
- The clean/dirty signal is now carried ENTIRELY by the `Sanitized` stamp +
  `dbscrub status` (exit 0/2), since naming no longer distinguishes modes.
- The window between "script finished" and "clean finished" is raw-PII-under-
  the-app-name. Closed by a PERSONAL wrapper (scripts/refresh-local.sample.ps1)
  that runs script + dbscrub as one motion — additive, no shared-script edits.
- The script's `@@SERVERNAME` guard (server-side) and dbscrub's allowlist
  check (client-side) are complementary layers; keep both.

## D11 — Consuming apps get a read-only guard; scrubbing never ships in app binaries
Proposal considered: aavsb.sln references the scrub engine and auto-cleans on
startup when a web.config flag is on and the DB is unstamped. REJECTED:
prod is never stamped, so on prod the trigger condition is permanently true,
held back only by config-transform correctness — and the app would scrub with
its own credentials and connection string, bypassing the allowlist check
and typed confirmation entirely. One bad transform = the product destroys
production. Also: Application_Start is a terrible host for long destructive
work (IIS recycles mid-scrub = half-masked DB), and .NET Framework can't
reference net8.0 anyway.
Resolution: `DbScrub.Guard` (netstandard2.0;net8.0), read-only by hard rule —
checks the stamp, throws/warns in dev when `RequireSanitizedDb` is true.
Fail-safe polarity: flag absent everywhere by default, true only in dev
configs, so transform mistakes produce a missing check, never a destructive
act. Auto-clean convenience lives in the personal wrapper and/or a dev-only
MSBuild target invoking the CLI (build tooling doesn't deploy). Core/Cli stay
app-agnostic and reusable for other databases.

## D12 — Config validation is hand-rolled; no JSON Schema library
SPEC section 4 says "schema-validated at load". Taken literally that means a
JSON Schema library (`JsonSchema.Net`, `NJsonSchema`). Considered and REJECTED
for v0, even after the no-new-dependency guardrail was explicitly waived for
this decision.

Reasons, in order of weight:
1. **It covers a minority of the rules.** JSON Schema can express shape, the
   closed strategy set, `batchSize`, and the `unclassifiedColumns` enum. It
   cannot express: no-duplicate table/column names; `strategy: "null"` requires
   a NULLABLE column; `strategy: "static"` requires a value type-compatible with
   the column. The last two need the live schema from SchemaInventory, so a
   semantic validation pass gets written either way — the library would only
   replace the handful of checks a `switch` already handles.
2. **Two sources of truth.** A schema document plus the C# model drift the day
   someone adds a strategy, and nothing fails when they disagree.
3. **Worse errors.** Validators emit `Value at /tables/0/columns/3 does not
   match schema`. `System.Text.Json` exposes `LineNumber`/`BytePositionInLine`,
   so hand-rolled checks produce `masking.sample.json(16,9): DBS005 —
   dbo.Person.Email uses strategy 'static' but has no "value"` plus the exact
   JSON to paste. That IS SPEC section 4's "line-level errors" requirement;
   the library would move us away from it.

Consequence: "schema-validated" in SPEC section 4 means "validated against the
config model and, once available, against the live schema" — not "validated by
a JSON Schema document". Exit code 5 and fail-fast behavior are unchanged.
Revisit if the config format ever needs to be validated by something other
than this tool (an editor, a CI linter, another language) — that is the case
where a published schema document earns its keep.

## D13 — Test assertions use xunit's built-in Assert; no assertion library
CLAUDE.md's dependency allowlist named FluentAssertions. FluentAssertions v8
moved to a paid commercial license (Xceed); this is company work, so that
applies. Options were: pin 7.2.2 (last Apache-2.0), switch to Shouldly
(BSD-3, actively maintained, good failure messages), or use plain `Assert`.

Chose plain `Assert`. The config, verdict, and inventory models are C#
`record` types, so they have structural equality — which means
`Assert.Equal(expected, actual)` works correctly on whole objects and
collections of them. Deep-object-graph comparison is the main thing an
assertion library buys, and records remove the need for it. Where a failure
needs explaining, a custom message naming the fix beats any auto-generated one.

CLAUDE.md's allowlist has been corrected so a future session doesn't install
FluentAssertions v8 and quietly create a license obligation.

## D14 — The server allowlist gates every command, including read-only ones
SPEC section 3 frames the safety checks as protecting mutations ("before
mutating"), which would leave `report` and `status` ungated. Decided the
opposite: every command that opens a connection checks the allowlist first.

Reasons: a rule with an exception is a rule people have to remember, and the
exception is the shape a mistake takes. `report` reads production's structure
if pointed at production. And the cost is near zero — the check is a string
comparison that happens before any socket is opened, proven by a test that
fails if the reader is touched on a refused server.

Matching is EXACT, case-insensitive, whitespace-trimmed. Explicitly not prefix
matching: a hosts-file entry, an SSH tunnel, or a SQL Server alias can all make
a `localhost`-shaped name resolve elsewhere. `localhost` does not cover
`localhost\SQL2022`, `localhost,1433`, or `localhost.corp.example`.

Consequence, accepted deliberately: a machine whose only instance is named
(the dev box is `localhost\MSSQLSERVER02`, with no default instance) is refused
by the built-in defaults until its config names the instance in full. That is
friction on first run, once, in exchange for the check meaning exactly what it
says. `--yes` remains narrower still (SPEC 3.2): only literal `localhost`,
`.`, and `127.0.0.1` may skip typed confirmation — not `(local)`, and no named
instance however it is spelled.

## D15 — `status` takes an OPTIONAL --config
SPEC section 2 gives `status` no `--config`, which conflicts with D14: the
allowlist lives in the config, so a gated `status` needs one. Rather than make
it required (and make the simplest command need a file) or leave `status`
ungated (and reintroduce the exception D14 removed), `--config` is optional.

Without it, the built-in defaults apply, which covers a plain localhost box.
With it, only the allowlist is read — never the tables — so pointing `status`
at any config is safe. On a named-instance machine the config is required in
practice, which is the same friction D14 already accepted.

## D16 — ToolVersion is an extended property, not only a log row
SPEC 5.5 puts tool version in the `dbo.__SanitizationLog` row. It is also
written as a database-level extended property alongside `Sanitized`,
`SanitizedUtc`, and `ConfigHash`, so `status` — and later the read-only Guard
in SPEC section 8 — answers the whole question in one cheap query that needs no
elevated rights and no table to exist. The log row remains the audit trail; the
extended properties are the fast check.

Stamp reading is fail-safe: only an explicit `true` or `1` counts as sanitized.
A missing, empty, misspelled, or half-written value reads as NOT sanitized.
The dangerous error is calling a dirty database clean; the reverse costs a
re-run. An unparseable `SanitizedUtc` leaves the database sanitized with an
unknown date, because the flag is the load-bearing part.

## D17 — Verify ignores all-placeholder values, rather than changing scramble
Conflict found while building the mask engine: the sample config scrambles
`Ssn`, scramble turns `123-45-6789` into `999-99-9999`, and the verify gate
(SPEC 5.4) sweeps every string column for `###-##-####`. `999-99-9999` matches
it. As specified, a correctly-scrubbed database could never pass verify and so
could never be stamped.

This is inherent, not a bug in either piece: scramble preserves shape ON PURPOSE
so forms still validate and column widths still hold, and shape is exactly what
a pattern detector looks for.

Options considered:
1. Stop preserving shape in scramble — rejected, it is the whole reason
   scramble exists rather than writing "REDACTED" everywhere.
2. Have verify skip columns that were masked — rejected, SPEC 5.4 deliberately
   sweeps ALL string columns, because the columns nobody configured are the
   ones most likely to leak.
3. Have verify ignore values composed entirely of scramble output. CHOSEN.

A real SSN cannot be all nines; scrambler output always is. `Scrambler.
LooksScrambled` is deliberately conservative — a value must contain no letter
other than x/X and no digit other than 9, and must have had something actually
replaced, so punctuation alone ("---") does not qualify. A real value that
merely contains a 9 is never mistaken for masked output.

Consequence for config authors: `scramble` keeps a value's shape, so a
scrambled email still looks like an email to anything shape-based. Where the
shape itself is the sensitive part, `static` is the better strategy. The sample
config already uses `static` for `Email` for this reason.

## D18 — `--yes` compares the HOST portion, not the whole server string
SPEC 3.2 permits `--yes` to skip typed confirmation "only when the server
matched `localhost`/`.`/`127.0.0.1` literally". Implemented literally, that
banned every named instance — including `localhost\MSSQLSERVER02`, the only
instance on the dev machine. Consequences: `clean` became interactive-only
there, which breaks the restore-then-scrub wrapper (D10), any MSBuild or CI
step, and automated testing of `clean` itself.

The spec listed strings where the intent was a property: "is this
unambiguously my own machine?" A named instance is resolved by the SQL Browser
on the same host, so `localhost\ANYTHING` is exactly as local as `localhost`.

Resolution: split on the instance separator and match the host portion exactly
against `localhost`, `.`, `127.0.0.1`. Still an exact match, still never a
prefix — `localhost.corp.example`, `notlocalhost`, `10.0.0.5\SQL2022`, and
`prod-sql-01\localhost` are all refused. `--yes` remains NARROWER than the
allowlist: `(local)` is allowlisted by default and still cannot use `--yes`.

This widens what `--yes` accepts, so it deserves the scrutiny a safety change
gets. What it does NOT change: the allowlist itself (unchanged, exact, no
override flag) and the typed confirmation for every interactive run. `--yes`
was always an escape hatch for scripted local use; this makes the escape hatch
work on the machines that actually exist.

## D19 — The mask engine writes per-row UPDATEs, not a table-valued parameter,
## and refuses `scramble` on a table with no primary key

SPEC 5.3 specifies "single UPDATE ... FROM @tvp per batch". Two parts of the
mask engine ended up elsewhere. Both are recorded here rather than silently
diverging.

**No TVP.** A table-valued parameter needs a user-defined TABLE TYPE created in
the target database, one per distinct key-and-column shape. That is DDL the tool
otherwise never performs, it persists after a crashed run, and it needs rights
beyond the ones masking itself needs. What replaced it: a batch is one command
containing N single-row `UPDATE ... WHERE <key> = @k` statements, all
parameterized, sent in one round trip inside one transaction. The cost is real —
SQL Server refuses a command carrying more than 2100 parameters, so the
configured `batchSize` is capped by (key columns + computed columns) per row,
which `MaskSql.RowsPerCommand` computes. The gain is that the tool creates no
objects in the database it is cleaning, and that every statement it sends is a
string a test can assert on.

**Constant strategies never read a row.** `null` and `static` write the same
value to every row, so a table whose masked columns are all constant is rewritten
set-based, with the key walk kept only to bound each transaction. This is why
there are three modes rather than one; the report names the mode per table.

**`scramble` requires a key, and is refused without one.** SPEC 5.3 offers a
keyless fallback of "a single set-based UPDATE per column", noting that
"scramble/static/null are all expressible in T-SQL". That is true of `null` and
`static` and NOT true of `scramble`. The closest T-SQL equivalent is `TRANSLATE`
over an explicit alphabet, which handles ASCII and leaves every accented and
non-Latin letter untouched — it would report success while preserving exactly the
characters most likely to identify someone. That is the same class of bug the
fixture caught in the C# scrambler once already (combining accents surviving,
HANDOFF step 3).

Options considered:
1. `TRANSLATE` — rejected, silently leaks non-ASCII.
2. Rewrite by distinct value (`UPDATE t SET c = @new WHERE c = @old`) — works
   without a key and is correct, but is one unindexed scan per distinct value.
   On a high-cardinality column that is quadratic, and a destructive tool with a
   silent performance cliff is worse than one that refuses.
3. Refuse at plan time. CHOSEN.

The refusal names both fixes: add a primary key, or use `static`/`null`. It
lands before anything is modified, which is the point of a separate planning
pass — the same problem found by the executor would surface on table seven of
twelve, leaving a database that is neither raw nor clean.

Consequence for temporal history: SQL Server gives a history table a clustered
index, never a primary key, so `history: "mask"` works only when the parent's
strategies are all constant. Where it isn't, the answer is the default —
`truncate` (D5).

Revisit if a real database turns up a keyless table that genuinely needs
`scramble`; option 2 becomes reasonable once there is a row count to size it
against.

## D20 — A primary key column is never masked

Not in the spec, added while building the engine. Any strategy other than `keep`
on a key column is a plan-time refusal, for three independently fatal reasons:

1. The engine walks a table in key order to batch it. Rewriting the key
   underneath that walk moves rows relative to the cursor — some get visited
   twice, some never. "Never" means a row that keeps its real values while the
   run reports success.
2. Masking collapses distinct values onto shared ones. Every scrambled 9-digit
   key becomes `999999999`, and the second row violates the key.
3. Anything with a foreign key pointing at it is orphaned.

A natural key made of personal data (an SSN primary key) is therefore refused
rather than masked. That is the correct answer for this tool: it cannot fix that
schema, and pretending to would produce a broken database instead of a clean one.
The honest fix is a surrogate key, which is out of scope for a masking tool.

Because an IDENTITY column is usually also the key, the key check runs first and
returns, so one mistake produces one error rather than two.

## D21 — A run that reconciles row counts, because a skipped row is silent

The batching predicate is the one piece of generated SQL whose bug would not
raise an error. A wrong comparison skips rows; a skipped row is not an exception,
it is a row that keeps its real values.

So each table is counted immediately before its walk begins, and the rows the
walk rewrote are compared against that count afterwards. A mismatch fails the
run (exit 1) with the table named. It costs one `COUNT_BIG(*)` per masked table
and converts the worst available failure mode — silent partial masking — into a
loud one.

`COUNT_BIG` rather than `COUNT` because `COUNT` returns `int` and overflows past
2.1 billion rows, and an error inside the check that proves a run was complete is
still a failed run.

## D22 — `clean` ships before verify, and says so instead of stamping

Step 4 delivers `clean` with the hygiene pass, the mask engine, and both safety
checks. It does NOT verify, stamp, rename, or repair users — those are step 5,
and CLAUDE.md permits a stamp only after a clean verify pass.

The tempting alternative was to stamp anyway, since the masking is real and a
`clean` that leaves the database "not sanitized" looks unfinished. Rejected: the
stamp is the entire clean/dirty signal since D10 removed the naming distinction,
and a stamp written without the check that earns it makes `dbscrub status` — and
later the read-only Guard — confidently wrong. An unstamped database costs a
re-run; a wrongly stamped one is a database everybody believes is safe.

So a successful `clean` exits 0, prints what it rewrote, and states plainly that
the database is NOT stamped and why. `--rename-to` and `--replace` are not wired
at all rather than accepted and ignored, since SPEC 5.5 renames only after a
clean verify pass.

Also deliberately absent from this step: `history: "mask"` is now honored rather
than silently truncated. The hygiene pass emptied history unconditionally, so
that config keyword had been reading as intent and doing the opposite.

## D23 — Uniqueness is a MODIFIER on a strategy, seeded from the primary key

Requirement raised after step 4: masking every surname to `Xxxxx` makes a dev
database hard to test against, because nothing distinguishes one row from
another. Wanted: `Xxxxx1`-style values, configurable per column.

Recorded BEFORE step 5 is built, so the verify gate is designed with it in view
rather than retrofitted (see "Consequence for verify" below).

**It surfaced a real bug, which is the more urgent half.** DbScrub does not read
unique constraints — `SchemaInventory` reads primary keys and nothing else. So
`static` on a column with a unique index sets every row to the same value and
violates the index, and `scramble` collides whenever two values share a shape.
Today that is discovered when SQL Server raises it, mid-run, leaving a
half-masked database. Uniqueness is therefore not only a test-data-quality
feature; the planner must also REFUSE a constant strategy on a unique column,
the same way it refuses a scramble with no key (D19) and a masked key
column (D20).

### Shape: a modifier, not a strategy

    { "name": "LastName", "strategy": "scramble", "unique": "key" }

Rejected: `"strategy": "unique"`. Uniqueness is orthogonal to HOW a value is
masked — a unique scramble and a unique static are both meaningful — so folding
it into the strategy name multiplies a deliberately closed set (D2) by every
combination.

Rejected for now: a template syntax, `"value": "Xxxxx{key}"`. More flexible, and
the natural v1 shape, but it turns the config into a small language with its own
escaping, validation and error messages, and we do not yet know which patterns
AAVSB actually needs. The flag answers the stated requirement; a template can
supersede it once a real database has shown what is missing.

### Seeded from the primary key, not a counter or a random number

- Keys are already unique, so there is no collision-retry loop, no RNG state and
  no seed to manage.
- It is deterministic, so it is unit-testable — the property this repo keeps
  choosing (D12, D13).
- It is STABLE ACROSS RUNS. A developer who bookmarks person 4172 gets the same
  fake name after every refresh. Random values break that on every restore, and
  that is a daily cost paid to save a day of implementation.

### The discriminator overwrites the tail; it does not append

`Xxxxxxx` with key 42 becomes `Xxxxx42`, NOT `Xxxxxxx42`.

Appending breaks the length guarantee that `scramble` exists for and overflows
fixed-width columns — a `char(11)` SSN has no spare room, and neither does most
of a legacy schema. Overwriting the tail keeps the length contract exactly
intact, which is the whole reason `scramble` is not simply "REDACTED" (D2).

Where the discriminator cannot fit — a `char(3)` column across 10,000 rows —
that is a plan-time refusal with the arithmetic in the message, matching how a
too-long `static` value is already handled.

### Known limits, accepted

- Needs a primary key, exactly as `scramble` does (D19). A heap gets no
  uniqueness, and the refusal says so.
- Composite keys need a concatenation rule; the value is the key columns joined,
  shortened to a base-36 token when it does not fit.
- Uniqueness of the OUTPUT is only guaranteed while the discriminator fits. The
  planner proves that up front rather than hoping.

### Consequence for verify (this is why the entry is written now)

The verify gate ignores values composed entirely of scramble output (D17), via
`Scrambler.LooksScrambled` — no letter but x/X, no digit but 9. `Xxxxx42`
satisfies neither. Built naively, the verify gate would flag every uniquely
masked column and no correctly scrubbed database could ever be stamped, which is
precisely the trap D17 already documented once.

So the placeholder rule must be an extension point from the start, not a
hardcoded test for scramble output. Step 5 builds it that way.

### Sequencing

Step 5 first, this after it, and both before the AAVSB config is written in
anger — so that config is authored once against the final strategy set. Rationale
for the order: `clean` currently has no check on its own output, so adding
masking modes before the verify gate means shipping new ways to be wrong with
nothing that would catch them. D2 also puts test-data realism in v1, and this is
realism-adjacent; finishing the safety milestone first keeps that priority
honest.

The one piece pulled FORWARD into step 5: reading unique indexes in
`SchemaInventory`, because it is a few lines beside the primary-key query and it
converts a mid-run failure into a plan-time refusal whether or not the rest of
this ships.

## Roadmap

- **v0** (this repo, now): spec in SPEC.md.
- **v0.x**: Testcontainers integration tests; app startup guard snippet;
  configurable verify patterns; `dotnet tool` packaging; run from MSBuild/CI.
- **v1 — quarantine pipeline**: ephemeral SQL Server Docker container
  (Developer Edition, `WITH MOVE` restore, TDE cert handling if prod backups
  are encrypted); mask inside the container; publish a born-clean sanitized
  `.bak` as the only artifact devs/AI ever touch; docker compose wrapper.
- **v1 features**: Bogus-backed `fake` strategy with per-column generators;
  `pattern` strategy (format-preserving: SSN/license/phone shapes);
  deterministic masking (seed from hash of original value; unique-constraint
  collision handling); fail-closed default; bulk-copy-and-swap writer for
  large tables.
- **v2**: FK-graph subsetting ("20% of Persons plus everything they touch");
  scheduled publishing of versioned sanitized backups for the team; config
  lives next to schema migrations so schema PRs force config PRs.

## Open questions (answer before/while building)
- Are prod backups TDE-protected or `WITH ENCRYPTION`? (Affects v1 container
  restore; harmless for v0 local restores that already work.)
- Which free-text/narrative columns are known dirty? (They become the first
  `static`/`null` entries and the first verify test cases.)
- Exact temporal/CDC/audit table inventory (run `dbscrub report` + dbatools
  `Invoke-DbaDbPiiScan` against a fresh restore).
