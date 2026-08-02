# Getting started

## 1. Put this folder under source control

From the folder root:

```bash
git init
git add .
git commit -m "DbScrub v0: spec, decisions, sample config, working agreement"
gh repo create dbscrub --private --source . --push
```

(If you don't use the GitHub CLI: create an empty private repo named `dbscrub`
on github.com, then `git remote add origin <url> && git push -u origin main`.)

## 2. First Claude Code session — paste this prompt

---

Read CLAUDE.md, docs/SPEC.md, and docs/DECISIONS.md in full before writing any
code — they encode decisions from a prior design session and are authoritative.

This session is slice 1 only (per CLAUDE.md): scaffold the solution
(src/DbScrub.Core, src/DbScrub.Cli, tests/DbScrub.Tests, .NET 8), then
implement:

1. The config model with JSON schema validation per SPEC section 4, loading
   config/masking.sample.json successfully and failing helpfully on bad input.
2. SchemaInventory: query sys.tables/sys.columns/temporal_type/
   is_tracked_by_cdc into a model (one class, parameterized SQL, no ORM).
3. Verdict resolution + diff: every live column resolves to a strategy /
   truncate / keep, or is UNCLASSIFIED.
4. The `report` command wired through System.CommandLine, printing the plan
   and the UNCLASSIFIED list in paste-into-config form, with exit codes per
   SPEC section 2.

Include unit tests for config validation, verdict resolution, and the
UNCLASSIFIED formatting. Do NOT implement clean/mask/rename yet. Walk me
through the structure as you go — I want to understand every line, and stop
for my review at natural checkpoints.

---

## 3. Homework alongside coding (15 minutes, huge payoff)

Against a fresh local restore, run dbatools once to draft your PII inventory:

```powershell
Install-Module dbatools -Scope CurrentUser
Invoke-DbaDbPiiScan -SqlInstance localhost -Database AAVSB | Out-GridView
```

Fold the hits into your real config (copy masking.sample.json ->
config/aavsb.masking.json). Also note which tables are temporal/CDC/audit —
`dbscrub report` will confirm once slice 1 lands.
