# Addendum: Linear MCP root cause + what "mapped the same" means

**Supersedes nothing.** Read `2026-08-06-0903-main.md` first — this only replaces
its §3 "Open question" and its §2 claim about why Linear was unavailable.
Everything else in that handoff still stands.

**Date:** 2026-08-06
**Branch:** `main`
**HEAD:** `6a4179a` Merge pull request #17 from jimsowers/handoff-linear-transition
**Working tree:** clean apart from the untracked `.mcp.json` created below.
**Code work done:** none. dbscrub is still at a clean stopping point.

---

## 1. Why Linear was unavailable — the previous diagnosis was wrong

The 0903 handoff said "Linear MCP is NOT connected... `search_mcp_registry`
returned empty." The second half is true and **irrelevant**. The registry lists
Anthropic connectors; it will never show a project-scoped HTTP MCP server, so an
empty result there says nothing about Linear. Do not re-chase it.

The actual cause: Linear is declared **per project**, in `.mcp.json` at the repo
root. `C:\development\git\storydeck\.mcp.json` (tracked in git there, alongside a
duplicate at `.claude\.mcp.json`) contains:

```json
{ "mcpServers": { "linear": { "type": "http", "url": "https://mcp.linear.app/mcp" } } }
```

dbscrub had no such file, so it got no Linear tools. Nothing was ever broken —
the config simply lives per-repo and had only ever been set up in storydeck.

**Fix applied:** `C:\development\git\dbscrub\.mcp.json` created with byte-identical
content. Currently **untracked**. Whether it should be committed (storydeck commits
its copy) is an open call for Jim — see §4.

**Unverified:** that the connection actually succeeds. `.mcp.json` is read at
session startup only, so the session that wrote it could not test it. Expect a
project-scoped-server trust prompt and possibly a Linear OAuth flow on first use.

**First action next session: confirm `mcp__linear__*` tools are callable.** If they
are not, stop and report — do not proceed on the assumption.

---

## 2. What "mapped the same as storydeck" means concretely

Derived from storydeck's own repo, without the MCP. Verify the Linear-side rows
against the live workspace before creating anything — the state list below is
from a doc dated 2026-05-11 and is ~3 months stale.

| Layer | storydeck today | Cost to mirror |
|---|---|---|
| Workspace | `linear.app/storydeck`, team key `STO` | Reuse. Not a decision. |
| Team | States: Triage / Backlog / Todo / In Progress / **Waiting For** / Done / Canceled. No "In Review" — see `storydeck/.claude/skills/pr-prep/SKILL.md:130` for the reasoning and the tradeoff accepted. | Minutes |
| Project | Several already live under the one team (e.g. "Financial project in Linear", `storydeck/CLAUDE.md:507`) | Trivial |
| Repo-side conventions | `pr-prep` skill Step 5 (extract `STO-\d+` from branch name + commit subjects, flip status, plus a start-of-run Done-flip catch-up pass); `handoff` skill's `**Ticket:**` / `**Sub-tickets spun off:**` header fields; `security-audit` skill logging findings to Linear | **The actual work** |

### Two corrections to the 0903 handoff's sizing

**Option (b) is far cheaper than that handoff feared.** It assumed a new team means
recreating "states/labels/templates, possibly partly in the Linear UI." In fact
storydeck's states are Linear's defaults plus a single addition (`Waiting For`).

**The bulk of the job is not in Linear at all.** What makes storydeck's mapping
function is repo-side: skills that read and write ticket state. dbscrub has a
`handoff` skill but no `pr-prep` skill and no Linear wiring anywhere.

### Load-bearing constraint, inherited

**There is no GitHub↔Linear integration** (verified 2026-05-10, recorded at
`storydeck/.claude/skills/pr-prep/SKILL.md:118`). Claude via MCP is the only thing
keeping Linear status truthful. Anything built for dbscrub inherits this — status
will drift unless a skill flips it.

---

## 3. Recommendation, and the one decision that sizes the job

**Recommendation: a new Linear team for dbscrub, not the `STO` team.**

- The issue-ID namespace is the one choice that is expensive to reverse. Moving
  issues between Linear teams renumbers them, which rots every link written into
  `docs/`. In a repo whose entire culture is durable back-references to
  `DECISIONS.md`, a shared ID space is the wrong trade.
- The usual argument for one team — a single unified backlog — is weak here.
  storydeck is an active product; dbscrub is a finished v0 tool plus a v1/v2
  roadmap. Shared team-level cycles would be noise in both directions.
- With (b) costing minutes rather than an afternoon, the reason to compromise
  is gone.

**Not yet decided by Jim.** Put this to him before creating anything.

**Not verified:** which write operations the Linear MCP actually exposes (team
creation in particular may be UI-only). Confirm against the live tools before
promising a team can be created from here.

---

## 4. Remaining work

### Next actions
1. Confirm `mcp__linear__*` tools are callable. Stop if not.
2. Confirm the live `STO` team's states match §2 (the list is 3 months stale).
3. Get Jim's call on §3 (new team vs `STO`), then create team → project.
4. Seed issues from `docs/HANDOFF.md` "What is left" (5 items, prioritised) and
   the Roadmap section of `docs/DECISIONS.md` (v0.x / v1 quarantine pipeline /
   v2 subsetting). **Issues point BACK to `DECISIONS.md`; they never restate it.**
   Strongest candidate for issue #1, per the 0903 handoff §9: `config/aavsb.masking.json`
   still does not exist — "the thing that actually blocks value."
5. Only after the above: consider porting a `pr-prep`-style skill to dbscrub. That
   is the piece that keeps status truthful, and it is code-adjacent work — feature
   branch + PR.

### Open call for Jim
- **Commit `.mcp.json`?** storydeck tracks its copy. If yes: feature branch + PR,
  never a push to `main`.

---

## 5. Gates that still apply

Unchanged from the 0903 handoff §4, and none were touched this session:

- **NEVER push to `main`.** Feature branch + PR. Gate the push with a command that
  EXITS non-zero on `main`, not one that merely prints the branch.
- **No new dependencies** without Jim's explicit approval — including anything a
  Linear integration might tempt. The `.mcp.json` above adds no package; it points
  at a hosted HTTP endpoint. Nothing was installed.
- **Never execute anything against a database** other than `DbScrubTest` on
  `localhost\MSSQLSERVER02`. It is currently STAMPED; rebuild the fixture first if
  raw data is needed.
- **Never claim the live-SQL integration tier passed.** It does not exist.
- **Jim works this repo in parallel in his own terminal.** Re-check branch and
  remote state immediately before staging or pushing; do not trust session state.
