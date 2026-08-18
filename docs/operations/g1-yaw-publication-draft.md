# G1 — Hull-yaw publication (APPLIED 2026-08-12, OD-RECOVERY-092)

> **STATUS: APPLIED 2026-08-12 (operator-approved, OD-RECOVERY-092).**
> `playerYaw` is published `Verified` via the module-rooted ring-record
> chain (12 hops: the identical position walk + `recordOffset 48` = float32
> hull yaw at `+0x30`); `offsets.playerYaw` stays 0 by design. Gates were:
> **OD-RECOVERY-089** CLOSED HIT 2026-08-11 (`+0x30` on medvedkovo, 56/56,
> score 1.0, flatness 1.0 — `twoReplayRepeatability = true`) + operator
> approval. `fieldValidation.playerYaw` is now `Verified` (the quarantined
> Ghidra hypothesis is retained as the first evidence entry, superseded).
> This file was the operator-facing spec + checklist; the apply followed
> the G0 procedure (`docs/operations/g0-operator-checklist.md`). Section 3
> below documents exactly what was done.

## 1. What gets published

| Field | Live evidence | Chain | offsets |
|---|---|---|---|
| `playerYaw` | OD-RECOVERY-088 (savanna, 48 dumps): ring-record **`+0x30`** hull-yaw float32, score 1.0, flatness 1.0, 48/48 matched, median per-dump lag +4.8 s, median per-dump error 0.000°; OD-RECOVERY-089 (medvedkovo, 56 dumps): **`+0x30` AGREES** — score 1.0, flatness 1.0, 56/56 matched, median per-dump lag −2.5 s (spread 5.6 s; per-dump bounded bidirectional path — the G2 label skew is opposite in sign per replay) | The **identical module-rooted walk as `playerPositionX`** (same root RVA, same entity lookup, same ring-index hop — position `+0x10` and yaw `+0x30` were proven on the SAME record) with the final hop `recordOffset 48` (`0x30`) instead of `recordOffset 16` (`0x10`) | `offsets.playerYaw` stays **0** (chained field — the runtime computes `moduleBase + offset` and the ring record is battle-scoped heap) |

The chain is NOT duplicated here: the canonical walkable form lives in
`docs/operations/g0-walkable-position-chains.draft.json` (`playerPositionX`
hops 1..11). When applying, the published `chains.playerYaw` must be that
prefix plus `{ "kind": "recordOffset", "value": 48, "note": "float32 hull
yaw at +0x30 (OD-RECOVERY-088 live-verified; rotation triple roll +0x28 /
pitch +0x2C / yaw +0x30)" }`. A `fidelity` check (same shape as
`offset_check.py`'s walkable-draft check) must prove the yaw chain walks to
the resolver's ring-record record.

## 2. Pre-flight facts

| Fact | Value |
|---|---|
| Executable identity | `wotblitz.exe` v11.19.0.10 = `1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d` (re-measured, G0) |
| G1 (hardware-atomic read) | CLOSED — stored v4 aggregate 24/24 `stable-resolver-positive`, `allConsistentDoubleRead=true` |
| G2 (same-decoded-clock) | CLOSED — `sameDecodedClockProven=true`, 4+ live confirmations (every 088 dump attested) |
| G3 (repeatability) | position-family closed (OD-075/076/078/081/082); facing repeatability = **OD-RECOVERY-089 CLOSED HIT** (`twoReplayRepeatability = true`) |
| Live evidence | OD-RECOVERY-088 + OD-RECOVERY-089 filled templates + ledger sections (both replays, opposite-sign label skew, per-dump path) |
| Static map | `RingRecordRegion.YawOffset = 0x30` (+ `RollOffset 0x28` / `PitchOffset 0x2C`) — already corrected and live-frame wired |

## 3. Apply steps (ONLY after operator approval)

> **Pre-staged 2026-08-11 (dry-run validator-clean):** step 2's checker
> extension and step 4's canonical draft chain are ALREADY staged —
> `offset_check.py` fidelity now iterates `playerYaw` (skips it until the
> published table gains the chain), and the walkable draft carries
> `chains.playerYaw` (position prefix + `recordOffset 48`). A scratch
> dry-run of the full table edit passed `validate_offset_file` (chains
> validated 4 field(s)) with zero fidelity issues and no regression on the
> 3 position fields. The apply commit is therefore: the table edit below +
> `offline/memory-offsets.md` + evidence; nothing else.

1. `memory-offsets/11.19.0.10.json`:
   - `fieldValidation.playerYaw` → `status: "Verified"`, APPEND the
     OD-RECOVERY-088 + 089 evidence entries (live dumps, scores, lag,
     per-dump errors), `independentProcessLaunches`/`independentReplays` ≥
     the position family's, `harnessInvariantsPassed: true`, approvals set
     by the operator (lead + decoder-auditor).
   - New `chains.playerYaw` per §1 (position prefix + `recordOffset 48`).
   - `offsets.playerYaw` stays **0**. `notes` + G0-style summary.
2. `scripts/python/offset_check.py` — extend the chains/fidelity validation
   to cover `playerYaw` (the existing validator already handles any
   `chains` field generically; add the fidelity expectation).
3. `offline/memory-offsets.md` — document the yaw chain (mirror the
   position-chains section).
4. `docs/operations/g0-walkable-position-chains.draft.json` — add the
   canonical `playerYaw` chain so the walkable form and the published form
   stay identical (fidelity check enforced).

## 4. Post-edit gates (in order; stop on any failure)

```text
python scripts/python/offset_check.py --check-schema        # PASS expected, yaw chain validated
tools/report-offset-evidence.ps1 -GameVersion 11.19.0.10     # runs clean
python scripts/python/offline_check.py --refresh             # file-tree + links
dotnet test tests/WotBTreader.GameIntegration.Tests -c Release --filter "FullyQualifiedName~ChainedFields"   # exclusion test passes
scripts/validate.ps1                                         # full gate, exit 0
```

Spot-checks: `offset_check.py` logs "chains validated (4 field(s))";
`ChainedFields_AreExcludedFromObservationReads` still passes (chained yaw
never read as `moduleBase + 0`); `validate.ps1` exit 0.

## 5. Commit scope (ONE change)

Files: `memory-offsets/11.19.0.10.json`, `scripts/python/offset_check.py`
(if the validator needed a yaw-specific case), `offline/memory-offsets.md`,
`offline/file-tree.md` (regenerated), `docs/operations/g0-walkable-position-chains.draft.json`,
`docs/operations/offset-discovery-ledger.md` (the publication row), and a
handoff. Message: conventional commit, e.g. `feat(od): publish playerYaw
as Verified via the module-rooted ring-record chain (OD-RECOVERY-088/089)`.
Do NOT include velocity, HP, `replayTime`, `cameraPitch`,
`aliveTankCount`, or any resolver/read-surface change in the same change.

## 6. NOT in scope (own gates)

- **HP publication** — APPLIED 2026-08-12 (OD-RECOVERY-092) alongside
  this package (both G1 applies landed together); the Phase-4 evidence
  (OD-RECOVERY-087/091) is recorded in the ledger; the draft is the
  historical pre-apply record.
- **Velocity, replayTime, cameraPitch, aliveTankCount** — untouched.
- **Item 7 (hardware atomicity)** — stays LAST by design.
