# DbScrub

Replaces real personal data in a locally restored SQL Server database with
obvious fakes, so day-to-day development — including anything you paste into an
AI tool — never touches real people's information.

> **Not finished yet.** `report` and `status` work and only ever read. `clean`
> now masks data, but it does **not** yet mark the database as cleaned — the
> verification step that earns that mark is still being built, and nothing may
> call a database safe without it. So `dbscrub status` keeps answering "not
> clean" even after a successful `clean`, on purpose.

---

## The problem it solves

You restore a copy of the production database onto your machine to develop
against realistic data. That copy contains real names, emails, and Social
Security numbers. It ends up in screenshots, in support emails, in test output,
and in AI prompts.

DbScrub overwrites those values with obvious fakes and then marks the database
as cleaned, so you can always tell which copy you are looking at. Connection
strings, restore scripts, and teammates' workflows do not change — it is one
extra command after the restore.

## Requirements

- .NET 8 runtime
- SQL Server 2019 or later, running locally
- Windows authentication (your own login needs permission to read the database
  structure)

## Install

```bash
git clone https://github.com/jimsowers/dbscrub.git
cd dbscrub
dotnet build
```

The commands below assume you run from the repo root.

---

## Quick start

**1. Write a config** describing what to do with each column. Start with almost
nothing:

```json
{
  "defaults": { "allowedServers": ["localhost"] },
  "tables": []
}
```

**2. Ask what it would do.** This only reads:

```bash
dotnet run --project src/DbScrub.Cli -- report --server localhost --database MyDb --config my-config.json
```

**3. Paste the answer back.** The report ends with every column nobody has
classified yet, already formatted as JSON. Paste it into your config, change
`keep` to a real strategy wherever the column actually holds personal data, and
run `report` again.

Repeat until the unclassified list is empty. That loop is the intended way to
build a config — nobody can list every column from memory.

---

## Commands

### `report` — what would happen

```bash
dbscrub report --server <server> --database <name> --config <path>
```

Reads the database, compares it to your config, and prints the plan: which
tables get emptied, which columns get masked and how, plus everything still
unclassified. Changes nothing.

### `clean` — actually change the data

```bash
dbscrub clean --server <server> --database <name> --config <path> [--yes] [--dry-run] [--fail-on-unclassified]
```

Prints the same plan `report` does, asks you to type the database name, then
runs it: empties the tables you marked `truncate`, masks the columns you
configured, and handles the hidden history tables around both.

| Flag | What it does |
|---|---|
| `--dry-run` | Print the plan and stop. Never prompts, so it is safe in a script |
| `--yes` | Skip typing the database name. Only allowed for `localhost`, `.`, `127.0.0.1` (with or without an instance name) |
| `--fail-on-unclassified` | Refuse to run while any column is still unclassified, whatever the config says |

It refuses to start — before changing anything — if the server is not on your
allowed list, if the database has already been cleaned, if the config asks for
something the schema cannot do, or if you type the wrong name.

**It does not mark the database as clean yet.** That happens once the
verification step exists; until then `status` still reports "not clean" after a
successful run. See the note at the top.

### `status` — is this copy safe?

```bash
dbscrub status --server <server> --database <name> [--config <path>]
```

Answers "has this database been cleaned?" Exit code `0` means yes, `2` means
no, so scripts and build steps can branch on it.

`--config` is optional here; it is read only for the list of allowed servers
(see [Safety](#safety)). You need it if your SQL Server is a named instance.

---

## Writing a config

A config is one JSON file. The part that matters is `tables`.

```json
{
  "defaults": {
    "allowedServers": ["localhost"],
    "unclassifiedColumns": "warn",
    "batchSize": 5000
  },
  "tables": [
    {
      "name": "dbo.Person",
      "history": "truncate",
      "columns": [
        { "name": "FirstName", "strategy": "static",   "value": "Dev" },
        { "name": "LastName",  "strategy": "scramble" },
        { "name": "Email",     "strategy": "static",   "value": "dev@example.invalid" },
        { "name": "Nickname",  "strategy": "null" },
        { "name": "PersonId",  "strategy": "keep",     "reason": "surrogate key" }
      ]
    },
    { "name": "dbo.LoginAudit", "strategy": "truncate" }
  ]
}
```

### Whole tables

Two things you can say about an entire table in one line.

**Empty it:**

```json
{ "name": "dbo.LoginAudit", "strategy": "truncate" }
```

Deletes every row. Use this for audit trails, email logs, and message queues —
anywhere old values hide inside JSON or XML that no column-level rule could
reach into.

**Declare it clean:**

```json
{ "name": "dbo.StateCode", "strategy": "keep", "reason": "reference data" }
```

Covers every column at once, so lookup and reference tables stop filling up the
unclassified list. `reason` is required — an exclusion should be a decision
someone recorded, not a way to make the report quiet.

Be aware of the trade: this covers columns added to that table *in future*, too.
The report lists every table excluded this way, under **Excluded by a
table-level "keep"**, so a blanket exclusion never becomes invisible.

A table entry is exactly one of: `truncate`, `keep`, or a `columns` list. Never
a combination — `keep` plus `columns` is rejected, because "keep everything
except these" would silently cover new columns as well.

### Individual columns

| `strategy` | What happens |
|---|---|
| `static` | Every row gets the fixed `value` you supply |
| `scramble` | Replaced in place — letters become `x`, digits become `9`, so length and shape survive |
| `null` | Emptied (only valid if the column allows nulls) |
| `keep` | Left alone. You looked, there is no personal data here |

`keep` matters more than it looks: it records that a decision *was made*, so the
column stops appearing in the unclassified list. Use `reason` to say why.

Two columns you cannot mask, both refused before anything runs:

- **The primary key.** DbScrub rewrites a table by walking it in key order, so
  changing the key underneath that walk would skip rows — and a skipped row keeps
  its real values. Masked keys would also collide with each other and orphan
  every row referencing them. Use `keep`.
- **A `scramble` on a table with no primary key.** Scrambling turns each value
  into a same-shaped fake, which means writing a different value to every row,
  which means being able to point at one row. Without a key there is nothing to
  point with. Add a key, or use `static`/`null` — those write the same value
  everywhere and need no key.

### Other settings

| Key | Default | Meaning |
|---|---|---|
| `allowedServers` | `localhost`, `.`, `(local)`, `127.0.0.1` | Servers DbScrub will connect to. See [Safety](#safety) |
| `unclassifiedColumns` | `warn` | `warn` prints unclassified columns and continues; `fail` stops instead |
| `batchSize` | `5000` | Rows updated per transaction |
| `history` (per table) | `truncate` | For tables that keep a hidden history of every past row |
| `renameTo` | none | Rename the database after a successful clean |
| `repairUsers` | none | Database users to reconnect to your local logins after a restore |

### Config errors

Mistakes are reported with a line number and a suggested fix:

```
config/masking.json(16,9): error DBS006: dbo.Person.Email uses strategy "static" but has no "value".
  { "name": "Email", "strategy": "static", "value": "[redacted]" }
```

Unrecognized keys are errors, not warnings — `"stragety": "scramble"` would
otherwise mask nothing at all, silently.

---

## Safety

DbScrub is designed on the assumption that someone will eventually point it at
the wrong database.

**It only connects to servers you have listed.** Every command checks
`allowedServers` before opening a connection. There is no override flag and no
environment variable — if you need another server, you edit the config, and
that edit shows up in a diff.

Matching is exact. `localhost` does **not** cover `localhost\SQL2022`,
`localhost,1433`, or `localhost.corp.example`. This is deliberate: a hosts-file
entry or an SSH tunnel can make a localhost-shaped name resolve somewhere else
entirely. If your SQL Server is a named instance, spell it out:

```json
"allowedServers": ["localhost\\MSSQLSERVER02"]
```

**Nothing is ever printed that could be personal data.** Not in the report, not
in errors, not in verification output. The report describes what a column will
become, never what it currently contains.

**You have to type the database name.** `clean` prints what it is about to do,
then asks you to reproduce the database name exactly. Pressing a key is muscle
memory; typing a name from the summary is not. `--yes` skips it, but only on a
server that is unambiguously your own machine — narrower than the allowed list.

**A database that has already been cleaned is skipped**, not cleaned twice.

**A run that could not rewrite every row fails.** Each table's row count is
checked before and after. If they disagree, rows were left holding real values,
so DbScrub reports failure rather than success — restore a fresh copy and run
again.

---

## Exit codes

| Code | Meaning |
|---|---|
| `0` | Success — or, for `status`, the database is clean |
| `1` | Something unexpected went wrong |
| `2` | Verification found personal data — or, for `status`, the database is not clean |
| `3` | Unclassified columns while set to `fail` |
| `4` | Refused: the server is not on the allowed list |
| `5` | The config file is invalid |

---

## Trying it without a real database

`scripts/create-test-db.sql` builds a small database called `DbScrubTest` with
fake data shaped like the real thing — a temporal table, change tracking, an
audit table, computed and identity columns, and a table with no primary key.

```bash
sqlcmd -S "localhost\MSSQLSERVER02" -E -i scripts\create-test-db.sql
```

Then point `report` at it using `config/dbscrubtest.masking.json`.

---

## License

Internal tool. Not currently published.
