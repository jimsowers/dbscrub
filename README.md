# DbScrub

A .NET console tool that scrubs PII from a locally restored SQL Server database,
so day-to-day development (including AI-assisted development) never touches real
personal data.

**Status:** slice 1 of 5 complete — config validation, schema inventory, verdict
resolution, and the read-only `report` command. `clean`, `status`, masking, and
renaming are not implemented yet; the tool cannot currently modify anything.
See `docs/SPEC.md` for the target and `docs/HANDOFF.md` for what is verified.

```bash
dotnet run --project src/DbScrub.Cli -- report \
  --server localhost --database AAVSB --config config/masking.sample.json
```

`report` is read-only. It prints the schema-vs-config plan and every
UNCLASSIFIED column in paste-into-config form. Exit `0` clean, `3` unclassified
columns in fail mode, `5` invalid config, `1` anything else.

## The problem

Production `.bak` files get restored locally for development and prod support.
Most of the time the real PII in that copy is a liability: it can leak into
emails, screenshots, AI prompts, and test output. Sometimes (prod support) the
raw data is genuinely needed. DbScrub makes "clean" the default and "raw" a
deliberate, visible exception.

## Daily workflow (the ritual)

```
1. Run the existing team restore script (unchanged): restores .bak directly
   as AAVSB, recreates the aavsbweb login/user, sets SIMPLE recovery.
2. Run:  dbscrub clean --database AAVSB --config config/aavsb.masking.json
     - hygiene: disable CDC, truncate temporal history + audit tables
     - mask configured columns (null / static / scramble / truncate)
     - verify: regex sweeps for emails, SSNs, phones -> fail on hits
     - stamp: extended property Sanitized=true + dbo.__SanitizationLog row
3. Develop against AAVSB (no connection strings, scripts, or teammate
   workflows change - dbscrub is a purely additive step)
```

The `Sanitized` stamp is the clean/dirty signal; `dbscrub status --database
AAVSB` answers "is my current copy clean?" in one command (exit 0/2, script-
friendly). Between the restore script finishing and `clean` finishing, AAVSB
is raw — close that window with a personal wrapper that runs both as one
motion (see `scripts/refresh-local.sample.ps1`). Prod-support mode: run the
restore script and simply don't run dbscrub; `status` will say unstamped.

An optional stricter ritual (restore as `AAVSB_RAW`, tool renames to `AAVSB`
after a clean verify) is supported via `--rename-to` and remains the plan for
the v1 quarantine pipeline — but the default local flow is in-place, matching
the team's existing script.

## Non-negotiable safety rules

- The tool refuses to connect to any server not on the explicit allowlist
  (default: localhost only).
- The tool requires the database name to be typed to confirm, terraform-style.
- Audit/log/history tables are truncated, never "masked".
- A database is never stamped or renamed unless the verify pass is clean.

## Repo map

- `docs/SPEC.md` — full v0 specification (build from this)
- `docs/DECISIONS.md` — why things are the way they are, plus roadmap (v1 quarantine pipeline, Bogus, determinism, subsetting)
- `docs/HANDOFF.md` — current state: what is verified, what has never run, open questions
- `config/masking.sample.json` — config file format
- `CLAUDE.md` — working agreement + guardrails for Claude Code sessions
- `PROMPT.md` — kickoff prompt for the first Claude Code session
