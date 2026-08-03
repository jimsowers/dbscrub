# DbScrub v0 Specification

Goal: after restoring a full production `.bak` locally, run one command that
makes the copy safe for daily development and AI-assisted work. Deliberately
dumb masking — obviously-fake data is a feature, because it can't be mistaken
for real data in an email or a prompt.

Out of scope for v0 (see DECISIONS.md roadmap): realistic fakes (Bogus),
deterministic masking, bulk-copy-and-swap writer, subsetting, the quarantine
container pipeline, fail-closed-by-default.

## 1. Tech stack

- .NET 8 console app, packaged as a dotnet tool later (v0: plain console app)
- `System.CommandLine` for the CLI
- `Microsoft.Data.SqlClient` for data access (no ORM)
- Solution layout:
  - `src/DbScrub.Core` — config model, schema inventory, diff, strategies, hygiene steps, verify, stamp/rename. net8.0. All the smarts; Cli is a thin shell.
  - `src/DbScrub.Cli` — command wiring, console UX, exit codes. net8.0.
  - `src/DbScrub.Guard` — READ-ONLY micro-library for consuming apps (see section 8). Multi-targets `netstandard2.0;net8.0` so legacy .NET Framework web apps (web.config era) can reference it. Depends on nothing but System.Data.SqlClient/Microsoft.Data.SqlClient. Contains zero write logic by design.
  - `tests/DbScrub.Tests` — unit tests (strategy logic, config parsing, diff). Integration tests via Testcontainers are a v0.x follow-up, not required to ship v0.

## 2. CLI surface

```
dbscrub clean  --server localhost --database AAVSB --config <path> [--yes] [--rename-to <name>] [--replace] [--dry-run] [--fail-on-unclassified]
dbscrub report --server localhost --database AAVSB --config <path>
dbscrub status --server localhost --database AAVSB
```

- `clean` — full pipeline (section 5). Default is IN-PLACE: mask, verify,
  stamp, done. Renaming only happens when `--rename-to` (or config
  `renameTo`) is present AND differs from the current database name — this
  fits the team's existing restore script, which restores directly as AAVSB.
- `report` — read-only: prints the schema-vs-config diff (every column with its
  verdict, and every UNCLASSIFIED column), detected temporal/CDC/audit surfaces,
  and what `clean` WOULD do. Also serves as `--dry-run`'s implementation.
- `status` — read-only: prints whether the database carries the `Sanitized`
  stamp (timestamp, config hash, tool version from the extended properties /
  `__SanitizationLog`). Exit `0` if stamped, `2` if not. Purpose: answer
  "is my current AAVSB copy clean?" in one command — the machine check for
  mode confusion when raw prod-support copies share the app-expected name.
- Exit codes: `0` success · `2` verify pass found PII hits (or `status`:
  unstamped) · `3` unclassified columns while `--fail-on-unclassified` ·
  `4` safety checks refused · `5` config invalid · `1` anything else.

**Built so far (step 4).** `clean` runs the safety checks, the hygiene pass and
the mask engine, and supports `--yes`, `--dry-run` and `--fail-on-unclassified`.
It does NOT verify, stamp, rename, or repair users — those are step 5, and a
stamp is only ever written after a clean verify pass. `--rename-to`/`--replace`
are therefore not wired yet; see DECISIONS.md D22.

## 3. Safety checks (build this first)

1. **Server allowlist.** Config `defaults.allowedServers` (default
   `["localhost", ".", "(local)", "127.0.0.1"]` — note `(local)` is a common
   legacy alias in web.config connection strings — matched against the DataSource
   case-insensitively, named instances allowed only if explicitly listed,
   e.g. `localhost\\SQL2022`). Any other server: print refusal, exit 4.
   There is no override flag. If someone needs another server, they edit the
   config, which is in source control.
2. **Typed confirmation.** Before mutating, print a summary (server, database,
   table count, row estimates, rename target) and require the user to type the
   database name exactly. `--yes` flag skips this for scripted use, but only
   when the server matched `localhost`/`.`/`127.0.0.1` literally.
3. **Never mutate a stamped database.** If the target already has the
   `Sanitized` extended property, `clean` exits 0 with "already sanitized"
   (idempotence guard against double runs and against re-running on an
   already-clean AAVSB).

## 4. Config file format

See `config/masking.sample.json`. JSON, schema-validated at load (fail fast,
exit 5, with line-level errors).

Column strategies (v0 closed set):

| strategy   | behavior |
|------------|----------|
| `null`     | `SET col = NULL` (column must be nullable; validated up front) |
| `static`   | fixed value from `value` (type-checked against column type) |
| `scramble` | same-length replacement: letters->x/X preserving case, digits->9, punctuation preserved. Preserves length and rough shape for UI/validators |
| `keep`     | explicit verdict: no PII here, leave it (recorded so diff is silent) |

Table-level:

| key        | behavior |
|------------|----------|
| `strategy: "truncate"` | `DELETE`/`TRUNCATE` the whole table (audit, logs, queues, message history) |
| `history: "truncate" \| "mask"` | temporal tables only; default `truncate` (see 5.2) |

Defaults block: `allowedServers`, `unclassifiedColumns: "warn" | "fail"`
(v0 default `warn`), `batchSize` (default 5000), `renameTo` (OPTIONAL; when
absent or equal to the current database name, clean runs in-place with no
rename — the default for the AAVSB workflow, whose restore script restores
directly as AAVSB; see DECISIONS.md D8/D10), `repairUsers` (OPTIONAL, default
empty; SQL database users to remap to same-named local logins per 5.6 — empty
for AAVSB because the team's existing restore script already recreates the
aavsbweb login and users, and dbscrub must not duplicate that responsibility).
The config never contains passwords — `repairUsers` assumes the server login
already exists locally.

Unclassified handling: every column in the live schema must resolve to a
verdict (a column strategy, membership in a truncated table, or `keep`).
Anything else is UNCLASSIFIED: listed loudly at the end of every run;
`warn` mode proceeds, `fail` mode exits 3 before mutating anything.
System schemas (`sys`, `cdc`, `INFORMATION_SCHEMA`) and the tool's own
`dbo.__SanitizationLog` are exempt.

## 5. The clean pipeline, in order

### 5.1 Preflight
- Safety checks (section 3), config validation, schema inventory
  (`sys.tables` / `sys.columns` / `sys.extended_properties` /
  `temporal_type` / `is_tracked_by_cdc`), diff vs config, print plan.
- Set database `RECOVERY SIMPLE` if not already (restore point: this is a
  disposable local copy by definition).

### 5.2 Hygiene (PII copies hide here — these SURROUND masking)
The pass runs in two phases, with 5.3 in between. Re-enabling versioning
adjacent to disabling it would put masking back inside the window it exists to
close.

**Before masking:**
1. **CDC:** if `sys.databases.is_cdc_enabled`, run `EXEC sys.sp_cdc_disable_db`
   (drops all capture tables/jobs).
2. **Temporal tables:** for each table with `temporal_type = 2`:
   `ALTER TABLE ... SET (SYSTEM_VERSIONING = OFF)`; if `history: "truncate"`
   (default) truncate the history table; if `"mask"` leave its rows in place for
   5.3 to rewrite. NOTE: masking a temporal table without this dance COPIES the
   unmasked row into history — that is the bug this section exists to prevent.
3. **Configured truncates:** run table-level `truncate` strategies
   (use DELETE when FKs prevent TRUNCATE; disable/re-enable FKs as needed).

**After masking:** re-enable versioning on every table it was disabled on, with
the original history table name, `DATA_CONSISTENCY_CHECK = OFF`. This runs even
when masking fails or is cancelled — a table left detached silently records no
history from then on, and nothing about the database looks wrong.

### 5.3 Mask
Three modes, chosen per table by whether any replacement depends on the row's
current value, and whether the table has a primary key (DECISIONS.md D19):

- **Row by row** — any `scramble` column. `SELECT TOP (n)` in key order past the
  last key seen (a keyset seek, never OFFSET), transform in memory, write the
  batch back as per-row `UPDATE ... WHERE <key> = @k` statements in one command
  and one transaction. Rows per command are capped by SQL Server's 2100-parameter
  limit, so `batchSize` is an upper bound rather than the answer.
- **Batched constant** — only `null`/`static` columns, table has a key. One
  set-based UPDATE per key range; no rows are read. The walk exists only to
  bound each transaction.
- **Whole table** — only `null`/`static` columns, no key. One UPDATE, one
  transaction, reported in the plan because it is unbounded.

`scramble` on a table with no primary key is REFUSED at plan time, not
approximated (DECISIONS.md D19). A primary key column is never masked
(DECISIONS.md D20).

Every table's rewritten row count is reconciled against a count taken
immediately before its walk; a mismatch fails the run (DECISIONS.md D21).
Progress output per table (rows done / total).

### 5.4 Verify (gate — nothing below runs if this fails)
- Regex/LIKE sweeps across ALL string columns (not just masked ones):
  email pattern, SSN `###-##-####`, 10-digit phone patterns.
- Configurable extra patterns later; v0 hard-codes those three.
- Sample-based for very wide tables is acceptable v0.x; v0 scans fully.
- Any hit: print table.column + hit count (never the value itself),
  exit 2, no stamp, no rename.

### 5.5 Stamp + rename
The stamp is BUILT. The rename is DEFERRED indefinitely (DECISIONS.md D25):
nothing uses it, and it is the most destructive code in the tool. Kept here
because the v1 quarantine pipeline needs it.
- Write extended property `Sanitized = true`, `SanitizedUtc`, `ConfigHash`
  (SHA-256 of the config file) and insert a row into `dbo.__SanitizationLog`
  (created if missing): run timestamp, tool version, config hash, tables
  touched, rows updated, duration.
- NO log cleanup is attempted. In-place cleaning leaves pre-images of the
  original values in both the data file (ghost records) and the transaction log,
  and nothing dbscrub can run removes them reliably. An earlier version of this
  spec called for `CHECKPOINT` + a log shrink; that was removed because it
  implied a cleanup it does not perform (DECISIONS.md D24). The v1 quarantine
  pipeline is the real fix.
- Rename (ONLY when a rename target is set and differs from the current
  name — the AAVSB default flow skips this entirely):
  `ALTER DATABASE [<current>] SET SINGLE_USER WITH ROLLBACK IMMEDIATE`
  -> `MODIFY NAME = [<target>]` -> `SET MULTI_USER`. If the target name
  exists (a stale clean copy), refuse unless `--replace` is passed, in which
  case drop it first.

### 5.6 Repair orphaned SQL users (skipped when `repairUsers` is empty)
DEFERRED indefinitely (DECISIONS.md D25) — the team restore script already owns
login and user setup, so `repairUsers` is empty for AAVSB and this never fires.
For each user in `repairUsers`: a restored `.bak` carries the database USER
but maps it to the production login's SID, which doesn't exist locally
("orphaned user" — SQL-auth logins fail after every fresh restore). If a
same-named server login exists: `ALTER USER [u] WITH LOGIN = [u]`. If not:
print a warning with the one-time `CREATE LOGIN` command for the developer to
run manually (the tool never creates logins and never handles passwords).
For AAVSB this list is EMPTY — the team's existing restore script already
drops/recreates the login and users, and dbscrub must not duplicate or fight
that. The feature exists for other databases and for the v1 quarantine
container, where the team script won't run.

## 6. Console UX

- Plan first, then confirmation, then per-step progress, then a summary block:
  tables truncated, columns masked, rows updated, verify result, stamp, rename.
- UNCLASSIFIED column report at the end of every run, formatted so it can be
  pasted straight into the config as `keep` entries.
- No PII values ever printed, including in errors and verify output.

## 7. v0 definition of done

- Rename and orphaned-user repair are NOT part of v0 (DECISIONS.md D25).
- `report` and `clean` work end-to-end against a local SQL Server 2019+ with a
  config covering: null, static, scramble, keep, table truncate, a temporal
  table, and a CDC-enabled database.
- Safety checks refuses a non-allowlisted server; stamped DBs are skipped.
- Verify gate demonstrably blocks stamp/rename when a planted email survives.
- Unit tests for: scramble logic, config validation, schema diff, verdict
  resolution.

## 8. DbScrub.Guard — app-side integration (read-only, by design)

Purpose: let a consuming app (e.g. aavsb.sln, .NET Framework) refuse to run
against an unscrubbed local database — WITHOUT compiling any scrubbing
capability into a binary that deploys to test/prod.

API (all of it):

```csharp
// Throws SanitizationRequiredException (message includes the exact
// `dbscrub clean` command to run) when the appSetting/flag says the check is
// required and the database lacks the Sanitized stamp. No-ops otherwise.
SanitizationGuard.AssertSanitized(string connectionString, bool required);

// Non-throwing variant for warning banners / health checks.
SanitizationStatus SanitizationGuard.GetStatus(string connectionString);
// -> { IsSanitized, SanitizedUtc?, ConfigHash?, ToolVersion? }
```

Rules:
- Reads the extended property / `dbo.__SanitizationLog` only. The assembly
  contains no UPDATE/DELETE/DDL. This is a hard guarantee, enforced in review.
- Fail-safe polarity: the consuming app wires `required` from an appSetting
  (e.g. `RequireSanitizedDb`) that is ABSENT/false by default and set true
  only in dev configs. A missed config transform on prod/test means the check
  silently does nothing — never that anything runs.
- The guard NEVER triggers a scrub. "Auto-clean" convenience lives outside
  the shipped binary: the personal wrapper script, or an MSBuild dev-only
  target that shells out to `dbscrub clean` / `dbscrub status` (exit codes
  are designed for this). Rationale: DECISIONS.md D11.
- Works with minimal DB permissions (reading extended properties needs no
  elevated rights), so the app's own login can perform the check.

Consuming from .NET Framework (aavsb.sln): reference the netstandard2.0
build, call AssertSanitized from Global.asax Application_Start with the
"aavsb" connection string and the appSetting flag. ~3 lines in the app.
