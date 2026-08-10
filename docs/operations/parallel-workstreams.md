# Parallel workstreams — coordinator runbook

The repo is built to run several agents at once on **offline** work, then funnel
every result into one **serialized, gated live queue**. This doc is the
coordination contract: what can run in parallel, what cannot, and how results
are accepted.

## The funnel

```
  ┌─ Agent A: overlay / UI workstream (offline) ─────────┐
  ├─ Agent B: Ghidra static pass — ring-record + clock ──┤
  ├─ Agent C: Ghidra static pass — camera / VP matrix ───┤  (after B)
  ├─ Agent D: replay telemetry extraction (offline) ──────┤
  └─ all write tests first, docs last ───────────────────┘
                            │
                            ▼
              single coordinator (this lane)
              ┌────────────────────────────────┐
              │  acceptance check per artifact │
              │  docs + handoff (single-writer)│
              │  serialized gated live queue   │
              └────────────────────────────────┘
```

## Serialization rules (never concurrent)

| Resource | Why single-writer | Lock name |
|---|---|---|
| Ghidra project DB (`WotBlitz.rep`) | headless sessions lock the project; concurrent runs block/fail | `ghidra-project` |
| `docs/` + handoffs | concurrent edits clobber each other; append-only log | `docs` |
| Live session queue | one launcher/game window; gate keeps evidence clean | `live-session` |

Everything else is parallel-safe: distinct .NET projects, the SQLite replay DBs
(read-only), python analysis scripts, and each project's own test suite.

## Lock helper

```bash
python scripts/workstream-lock.py acquire ghidra-project --purpose "ring-record write site"
# ... do the work ...
python scripts/workstream-lock.py release ghidra-project
python scripts/workstream-lock.py status          # see all three lanes
```

- Acquire **before** touching the resource; release in a `finally`-style path
  (or immediately after the work, before writing docs).
- A lock whose owner pid is dead is **stale** — `break` it (or `--force`).
- Never release another agent's lock (the helper refuses).
- `.build/locks/` is transient state; never commit it.

## Acceptance criteria (before a live session is approved)

An offline artifact may only consume live-queue capacity if it meets **all** of:

1. **Offline-verified** — checked against replay ground truth (packet data or
   decoded frames), not guessed. State the replay(s) and the match metric.
2. **Anchored** — names the concrete object/RVA/field and its evidence trail
   (FRESH campaign, write-site, or region dump reference).
3. **Fail-closed** — states what happens when the expected value is absent, and
   that no product surface broadens as a result.
4. **Tests green** — the project gate (`validate.ps1`) passes; new behavior is
   pinned by unit tests.
5. **Handoff appended** — the day's handoff records what was done, what was
   proven, and what the next session must verify.

## Live queue rules

- One session at a time; each is approved, launched, polled, and logged under
  the existing gate (BLK-style approval packets, OD/BLK ledger entries).
- A session is planned to **confirm** an offline hypothesis — not to explore.
  If a session would explore, the exploration must be re-scoped offline first
  (static pass or replay analysis).
- Every session ends by filling its pre-staged evidence template; the next
  session may not start until the previous one's handoff is appended.

## Workstream boundaries (who touches what)

| Area | Owner | Conflict note |
|---|---|---|
| `src/WotBTreader.Overlay` + its tests | Agent A | none (own project) |
| `tools/ghidra-scripts/` + scan logs | Agent B then C | serialize on `ghidra-project` |
| `scripts/python/*` analysis | Agent D | read-only DBs |
| `docs/`, `handoffs/`, `AGENTS.md` | coordinator only | serialize on `docs` |
| `scripts/invoke-g1-live-poll.ps1` + evidence templates | coordinator only | serialize on `live-session` |
