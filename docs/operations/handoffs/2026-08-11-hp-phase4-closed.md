# 2026-08-11 — HP Phase-4 repeat CLOSED HIT (OD-091); lead-side matcher fix shipped

Binary: wotblitz.exe 11.19.0.10 (hash `1cda5c31…1760307d`). Replays: Oasis
Palms + Dead Rail. Read-only; resolver, read surface, and
`memory-offsets/11.19.0.10.json` untouched (HP/yaw publications remain
operator-gated). Evidence: ledger row + result section `OD-RECOVERY-091`,
`docs/operations/od-recovery-091-evidence-template.md` (filled),
`docs/operations/g1-hp-publication-draft.md` (READY). Working tree state
at end: branch `main`, changes below uncommitted (commit on request).

## Outcome — Phase-4 two-replay HP rule CLOSED

`[entity+0xB8]` (current-health signed int16) agrees on Dead Rail with the
Oasis Palms live hit (OD-RECOVERY-087): **score 1.0, flatness 1.0, Strict
4/4 exact-sum matches, `twoReplayRepeatability = true`.** HP publication
is READY (operator approval + gate run only) — same standing as yaw.

### Session facts

- 4 approved live launches on Dead Rail; the first 3 were honest aborts on
  the planned victim 2549399 (`EntityNotFound` at 30/60/113 s) — root cause
  STRUCTURAL: 2549399 (vandal13) is team 2 and the resolver's entity-map
  trees are the movement-filter family (player's own team only). Probes:
  2549399 `EntityNotFound entity-maps` while 2549408 `Resolved` at the same
  instant. Re-scoped to the only damaged team-1 victim **2549395
  (dudster_2015, Pz.Kpfw. II)** — 7 hits / 520 dmg / 4 windows at
  92.7–104.7 s.
- Completed run: `battleSession=019ff2f7-b958-7cc4-8a9d-5761ba4a55f9`,
  control times 130,200, **58 dumps** (`.data/hp-phase4-091-snapshots.json`),
  every dump `sameDecodedClockProven=true`.
- Byte-level track (immutable dumps): 520 → 443 → 303 → 227 → 55 → 0;
  drops 140 = 36+104, 76 = 76, 172 = 94+78, 55 = 55 — every drop equals
  its damage subset exactly (443 = 520 − 77 pre-window); max `+0x11C` 520
  constant, alive `+0xBA` flips exactly at 0, healing `+0x11E` 0.

### The at-session honest negative and its root cause (OD-089 class)

At-session verdict `hit=False` — lenient top `+0xB8` score 1.0 flatness 1.0
(4/4; the int32-at-0xB8 candidate subsumes the killing window's alive-byte
flip), but the Strict pass nominated 0x4E (1/8 — byte-level noise at
15.6–15.9k, no pattern). Root cause: Dead Rail's memory clock **LEADS** the
decoded clock by ~2.5 s (the OD-089 yaw finding, reproduced on the HP
field). The destroying hit is exactly 55 (no overkill); every drop IS an
exact subset sum — but an event whose WRITE landed in a dump pair has a
decoded time POSTDATING that pair's `To`, which the one-directional
(From − lag, To] attribution window cannot see.

## Fix — additive bounded lead-side attribution (default 0 = unchanged)

- `HpDamageCorrelator.Correlate` gained `eventLagLeadSeconds` (default 0):
  attribution window becomes (From − lag, To + lead].
- CLI `hp-diff --lag-lead-seconds` (validated: requires
  `--lag-tolerance > 0`; added to `CliInvocation.OptionRequiresValue`;
  JSON echoes `lagToleranceSeconds`/`lagLeadSeconds`).
- Driver `invoke-hp-diffing-session.ps1` `-LagLeadSeconds` (default 4 — the
  measured Dead Rail lead + margin), threaded through the verdict command.
- 3 new correlator tests: lead exact match in Strict, default-zero
  unchanged, no fabricated match for a larger event.
- Re-verdict on the SAME immutable dumps: `--lag-tolerance 4
  --lag-lead-seconds 4` → **hit=True, top 0xB8 score 1.0 flatness 1.0,
  Strict 0xB8 4/4**. Oasis regression on the same parameters: unchanged
  HIT (8/8, Strict 8/8, flatness 1.0 with 9 control windows).

## Changed files (uncommitted)

- `src/WotBTreader.Core/Discovery/RecordDiffing.cs` — lead-side attribution.
- `src/WotBTreader.Host.Cli/Cli/CliCommandRouter.cs` — `--lag-lead-seconds`
  parse/validate/echo.
- `src/WotBTreader.Host.Cli/Cli/CliInvocation.cs` — `OptionRequiresValue`.
- `scripts/invoke-hp-diffing-session.ps1` — `-LagLeadSeconds`.
- `tests/WotBTreader.Core.Tests/RecordDiffingTests.cs` — 3 new tests.
- Docs: `docs/operations/offset-discovery-ledger.md` (row 091 + result
  section + header + decision register), `offset-discovery-workflow.md`
  (091 DONE + next), `product-roadmap.md` (L1 row), `AGENTS.md` (headline),
  `od-recovery-091-evidence-template.md` (filled),
  `docs/operations/g1-hp-publication-draft.md` (NEW, READY),
  this handoff.

## Verification

- `dotnet build src/WotBTreader.Host.Cli -c Debug` — 0 errors.
- `dotnet test tests/WotBTreader.Core.Tests -c Release --filter
  "FullyQualifiedName~RecordDiffingTests"` — 32/32 passed (29 + 3 new).
- Offline re-verdicts on both replays' immutable dumps — Dead Rail HIT,
  Oasis HIT (regression-free).
- PSScriptAnalyzer on the edited ps1: run in the validate.ps1 gate.

## Next steps

- Operator approval for the **HP publication apply**
  (`g1-hp-publication-draft.md`) and the **yaw publication apply**
  (`g1-yaw-publication-draft.md`); each runs the G0 post-edit gate and
  lands as ONE commit.
- Item 7 (hardware atomicity proof) stays LAST by design.
- Damage-dealt L3 stays honest-negative: further discovery needs a new
  object family (avatar/player-stats), not the entity records.
