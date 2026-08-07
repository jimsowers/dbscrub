# DbScrub

Replaces real personal data in a locally restored SQL Server database with
obvious fakes, so day-to-day development — including anything you paste into an
AI tool — never touches real people's information.

### 📄 New here? Start with the one-page guide

**[docs/getting-started.html](docs/getting-started.html)** — eleven steps from
`git clone` to a masked database, then how the code works, with class names.
Assumes no prior knowledge of this tool. Everything a new developer needs, on
one page.

**Read it here:** https://jimsowers.github.io/dbscrub/

Or clone the repo and open `docs/getting-started.html` in a browser — it is
fully self-contained, so it works offline with no build step. (GitHub shows the
*source* of `.html` files rather than rendering them, which is why the hosted
link above exists.)

<details>
<summary>Turning the hosted link on, if it is not live yet</summary>

Settings → Pages → **Source: Deploy from a branch** → branch `main`, folder
`/docs` → Save. It takes a minute or two to publish, then the URL above serves
the guide.

Everything else is already in place: `docs/index.html` redirects the root URL to
the guide, and `docs/.nojekyll` stops GitHub trying to run the folder through
Jekyll, which would otherwise treat `SPEC.md` and friends as blog posts.

</details>

---

> **Working end to end.** `report` and `status` only ever read. `clean` masks the
> database, sweeps every string column looking for anything that still resembles
> personal data, and marks the copy as cleaned **only if that sweep comes back
> empty**. If it doesn't, you get the list of columns that failed and no mark —
> so `dbscrub status` can never call a database safe on the strength of a run
> that didn't check.
>
> Renaming the database after a clean, and reconnecting restored users to local
> logins, are deliberately not built — nothing uses them, and rename is the most
> destructive thing this tool could do.

---

## The problem it solves

You restore a copy of the production database onto your machine to develop
against realistic data. That copy contains real names, emails, and Social
Security numbers. It ends up in screenshots, in support emails, in test output,
and in AI prompts.

DbScrub overwrites those values with obvious fakes. Connection strings, restore
scripts, and teammates' workflows do not change — it is one extra command after
the restore.

The fakes are deliberately unconvincing. `Dev`, `xxxxx`, `999-99-9999`. A value
that could be mistaken for real is a value that ends up in an email.

## Requirements

- **The .NET 10 SDK.** Not just the runtime — there is no installable package
  yet, so you always build from source. Check with `dotnet --version`; you want
  a number starting with `10.`
- **SQL Server 2019 or later, running on the same machine.**
- **Windows authentication.** `report` and `status` need only to read the
  database structure. `clean` additionally writes to the tables it masks and
  runs `ALTER DATABASE` and `ALTER TABLE` — in practice, `db_owner` on the copy.

## Install

```bash
git clone https://github.com/jimsowers/dbscrub.git
cd dbscrub
dotnet build
```

dbscrub is not installed onto your system and is not on your PATH. The
repository ships a small launcher at its root instead. **Run it from the
repository root** — the folder containing `DbScrub.sln`:

```bash
.\dbscrub report --server localhost --database MyDb --config my-config.json
```

The leading `.\` is PowerShell's way of saying "the file here in this folder",
and PowerShell requires it. In the older `cmd` prompt, drop it and type
`dbscrub …`.

The launcher just forwards to the .NET toolchain. You can bypass it, but mind
the bare `--`:

```bash
dotnet run --project src/DbScrub.Cli -- report --server localhost --database MyDb
```

Everything before that `--` belongs to `dotnet`; everything after it goes to
dbscrub. Write `--report` instead of `-- report` — one missing space — and you
get a screenful of "Unrecognized command or argument". That trap is why the
launcher exists.

## Find your SQL Server's exact name

dbscrub matches server names character for character, so get this right before
anything else. In PowerShell:

```bash
Get-Service | Where-Object { $_.Name -eq 'MSSQLSERVER' -or $_.Name -like 'MSSQL$*' }
```

- `MSSQLSERVER` — a **default** instance. Your name is `localhost`.
- `MSSQL$SQLEXPRESS` — a **named** instance. Your name is `localhost\SQLEXPRESS`.

`localhost` does **not** cover a named instance. That is the safety check
working as designed, and there is no flag to loosen it.

---

## Quick start

**1. Copy the starter config.** You do not write one from scratch and you do not
need to know your schema in advance.

```bash
copy config\starter.masking.json config\mydb.masking.json
```

Open your copy and change **one line** — put your server name from above into
`allowedServers`:

```json
"allowedServers": ["localhost"]
```

> **A named instance needs two backslashes inside JSON.** `"localhost\\SQLEXPRESS"`
> is correct; `"localhost\SQLEXPRESS"` is not valid JSON and fails with
> `'S' is an invalid escapable character`. On the command line, type it normally
> with one backslash — the doubling is a JSON rule, not a dbscrub rule.

**2. Ask what it would do.** This only reads, so run it as often as you like:

```bash
.\dbscrub report --server localhost --database MyDb --config config/mydb.masking.json
```

**3. Paste the answer back.** The report ends with every column nobody has
classified yet, already formatted as JSON. Paste it into the `tables` list,
change `keep` to a real strategy wherever the column actually holds personal
data, and run `report` again.

Repeat until it says every column has a rule. That loop is the intended way to
build a config — nobody can list every column from memory. On a large database
the list is summarised; add `--review-all` for every line of it.

**4. Rehearse it.** Same plan, but reached through `clean`'s own safety checks,
so it also proves a real run would be allowed to start. Still changes nothing:

```bash
.\dbscrub clean --server localhost --database MyDb --config config/mydb.masking.json --dry-run
```

**5. Run it.** The same command **without** `--dry-run`. It prints the plan, then
asks you to type the database name before it touches anything:

```bash
.\dbscrub clean --server localhost --database MyDb --config config/mydb.masking.json
```

**6. Confirm.** Exit code `0` means the copy is clean, `2` means it is not:

```bash
.\dbscrub status --server localhost --database MyDb --config config/mydb.masking.json
```

`--config` is optional for `status` and is read only for the allowed-servers
list — but you need it if your SQL Server is a named instance, since without it
only the built-in defaults apply and those cover default instances only.

For what happens between steps 5 and 6 — the order of operations, why temporal
history needs special handling, and what the verification sweep checks — see the
[one-page guide](docs/getting-started.html).

---

## Commands

> Run these from the repository root, as in the quick start above.

### `report` — what would happen

```bash
.\dbscrub report --server <server> --database <name> --config <path>
```

Reads the database, compares it to your config, and prints the plan: which
tables get emptied, which columns get masked and how, plus everything still
unclassified. Changes nothing.

It also carries a meaningful exit code, so it works as a CI gate: `5` if the
config asks for something the schema cannot do, and `3` if columns are
unclassified while `unclassifiedColumns` is set to `fail`. A report that always
returned `0` would gate nothing.

### `clean` — actually change the data

```bash
.\dbscrub clean --server <server> --database <name> --config <path> [--yes] [--dry-run] [--fail-on-unclassified]
```

Prints the same plan `report` does, asks you to type the database name, then
runs it: empties the tables you marked `truncate`, masks the columns you
configured, and handles the hidden history tables around both.

Then it **checks its own work**. Every string column in the database is swept
for values that still look like an email address, a Social Security number or a
phone number — not just the columns you configured, because the ones nobody
classified are where something is most likely to have been missed. Only if that
sweep finds nothing does the database get marked as cleaned.

A sweep that finds something prints the columns and how many values matched,
never the values themselves, and exits `2` with no mark written.

| Flag | What it does |
|---|---|
| `--dry-run` | Print the plan and stop. Never prompts, so it is safe in a script |
| `--yes` | Skip typing the database name. Only allowed for `localhost`, `.`, `127.0.0.1` (with or without an instance name) |
| `--fail-on-unclassified` | Refuse to run while any column is still unclassified, whatever the config says |

It refuses to start — before changing anything — if the server is not on your
allowed list, if the database has already been cleaned, if the config asks for
something the schema cannot do, or if you type the wrong name.

`--rename-to` and `--replace` are not available yet. They are deliberately
absent rather than accepted and ignored.

### `status` — is this copy safe?

```bash
.\dbscrub status --server <server> --database <name> [--config <path>]
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

| `strategy` | What happens | Checked before the run |
|---|---|---|
| `static` | Every row gets the fixed `value` you supply | The value must fit the column's type and length |
| `scramble` | Replaced in place — letters become `x`, digits become `9`, so length and shape survive | Text columns only, and the table needs a primary key |
| `email` | A generated address per row, `fakeemail15@notreal.invalid`, where 15 is the row's key | Text column, a primary key to vary by, and room for the address |
| `null` | Emptied | Only if the column allows nulls |
| `keep` | Left alone. You looked, there is no personal data here | — |

Add `"unique": "key"` to a `scramble` entry when every row needs a *different*
fake value — otherwise everyone called Smith becomes `Xxxxx` and the copy is hard
to test against. The row's primary key is written onto the end of the masked
value behind a `#`, so `Lovelace` in row 42 becomes `Xxxxx#42`: still eight
characters, and no two rows alike. It needs a whole-number primary key and one
extra character of column width for the `#`.

`email` is a strategy rather than a `static` value on purpose. DbScrub defines
that address shape, so the verify gate can recognise a million distinct
generated addresses without being handed a list — and `.invalid` is reserved by
RFC 2606, so it can never be registered and dev mail aimed at it never leaves
the building.

Everything in that last column is verified against the real database *before*
anything is modified. A `"value": "not-a-social-security-number"` aimed at a
`char(11)` is a refusal with a line number, not an error halfway through a run.

`keep` matters more than it looks: it records that a decision *was made*, so the
column stops appearing in the unclassified list. Use `reason` to say why.

**`scramble` keeps a value's shape, which is sometimes the wrong thing.** A
scrambled SSN is `999-99-9999` — still unmistakably SSN-shaped, and still
recognisable as "this column holds Social Security numbers". That is deliberate:
shape is what makes the copy usable, because forms still validate and columns
still fit. But where the *shape itself* is the sensitive part, reach for
`static` instead. The sample config uses `static` for `Email` for exactly this
reason.

Three things you cannot mask, all refused before anything runs:

- **The primary key.** DbScrub rewrites a table by walking it in key order, so
  changing the key underneath that walk would skip rows — and a skipped row keeps
  its real values. Masked keys would also collide with each other and orphan
  every row referencing them. Use `keep`.
- **A `scramble` on a table with no primary key.** Scrambling turns each value
  into a same-shaped fake, which means writing a different value to every row,
  which means being able to point at one row. Without a key there is nothing to
  point with. Add a key, or use `static`/`null` — those write the same value
  everywhere and need no key.
- **A repeated value in a column that must be unique.** If a column carries a
  unique index or UNIQUE constraint, `static` and `null` write the same value to
  every row and `scramble` collides whenever two values share a shape. SQL Server
  enforces uniqueness *while the UPDATE runs*, so unchecked this fails partway
  through and leaves the database half masked. DbScrub reads those indexes and
  refuses up front, naming the index. Use `email`, or `scramble` with
  `"unique": "key"` — both give every row a different value.

### Other settings

| Key | Default | Meaning |
|---|---|---|
| `allowedServers` | `localhost`, `.`, `(local)`, `127.0.0.1` | Servers DbScrub will connect to. See [Safety](#safety) |
| `unclassifiedColumns` | `warn` | `warn` prints unclassified columns and continues; `fail` stops instead |
| `batchSize` | `5000` | Rows per transaction, 1 to 1,000,000. An upper bound, not a promise — see below |
| `history` (per table) | `truncate` | `truncate` empties the hidden history table; `mask` applies the same column strategies to it instead |

`batchSize` is capped internally when a table is masked row by row, because SQL
Server refuses any single command carrying more than 2100 parameters. A table
with a two-column key and three scrambled columns spends five parameters per
row, so batches there are smaller than 5000 no matter what you write.

`history: "mask"` only works when that table's strategies are all `static` or
`null`. SQL Server gives a history table a clustered index rather than a primary
key, so there is no way to address one of its rows — and that is what `scramble`
needs. If you hit that refusal, the answer is the default: `truncate`. Old
history rows are worth very little in a dev database.

**Two keys are parsed but not acted on.** Both are deliberately deferred, not
unfinished. Setting them today does nothing:

| Key | Will do | Blocked on |
|---|---|---|
| `renameTo` | Rename the database after a clean run | Deferred — nothing uses it, and it is the most destructive code in the tool |
| `repairUsers` | Reconnect restored database users to your local logins | Deferred — your restore script already does this |

### Comments

Any object in the config accepts a `$comment` key, which the loader ignores.
Useful because JSON has no comment syntax and a masking decision usually
deserves an explanation:

```json
{
  "$comment": "A heap — no primary key, so scramble is refused here.",
  "name": "dbo.ContactImport",
  "columns": [
    { "name": "Email", "strategy": "static", "value": "dev@example.invalid" }
  ]
}
```

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

Stable by design — CI depends on them, so they are never renumbered, only added
to.

| Code | Meaning |
|---|---|
| `0` | Success. For `status`, the database is clean. For `clean`, also returned when the database was already clean and was skipped |
| `1` | Something unexpected went wrong — including a run that failed partway, or one that could not rewrite every row |
| `2` | The verification sweep found values that still look like personal data, so nothing was marked — or, for `status`, the database is not clean |
| `3` | Unclassified columns while set to `fail`, or `clean --fail-on-unclassified` |
| `4` | A safety check refused: server not on the allowed list, wrong database name typed, or `--yes` used on a server that is not unambiguously local |
| `5` | The config is invalid, **or** it asks for something the schema cannot do — a `static` value that will not fit, a `scramble` on a table with no primary key, a column that no longer exists |

---

## Trying it without a real database

`scripts/create-test-db.sql` builds a small database called `DbScrubTest` with
fake data shaped like the real thing — a temporal table, change tracking, an
audit table, computed and identity columns, and a table with no primary key.

```bash
sqlcmd -S "localhost\MSSQLSERVER02" -E -i scripts\create-test-db.sql
```

It drops and recreates, so it is also how you get back to a known state after a
destructive run. `config/dbscrubtest.masking.json` is a matching config —
deliberately incomplete, so the unclassified list has something in it and the
paste-it-back loop is worth doing.

```bash
.\dbscrub report --server "localhost\MSSQLSERVER02" --database DbScrubTest --config config/dbscrubtest.masking.json
```

Substitute your own server name from the section above — `localhost\MSSQLSERVER02`
is one particular machine's named instance, not a default.

Then try `clean --dry-run`, then `clean` for real. Nothing in there is a real
person: the domains, phone numbers, and SSN prefixes are all ranges reserved by
standards bodies precisely so they can never belong to one.

---

## Going deeper

| File | What it is for |
|---|---|
| [docs/getting-started.html](docs/getting-started.html) | The one-page guide. How to run it, and how the code fits together |
| [docs/SPEC.md](docs/SPEC.md) | The authoritative specification |
| [docs/DECISIONS.md](docs/DECISIONS.md) | Why each design choice was made, and what was rejected. Read this before changing anything load-bearing |
| [docs/HANDOFF.md](docs/HANDOFF.md) | Current state of play: what works, what is unverified, what is next |
| [CLAUDE.md](CLAUDE.md) | The working agreement for this repo |

---

## Status

Built and working: config validation, schema inventory, the report, the safety
checks, the hygiene pass, the mask engine, the verification sweep, and the
`Sanitized` mark.

Deliberately not built: database rename after a clean run (`renameTo`,
`--rename-to`, `--replace`) and orphaned-user repair (`repairUsers`). Nothing
uses either — cleaning happens in place, and your restore script already sets up
logins — and rename is the most destructive code the tool could contain. They
stay designed and unbuilt until something actually needs them.

---

## License

Internal tool. Not currently published.
