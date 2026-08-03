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
