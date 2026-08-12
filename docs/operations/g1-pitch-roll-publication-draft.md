# G1 — Hull pitch/roll publication (PRE-STAGED 2026-08-12)

> **STATUS: PRE-STAGED 2026-08-12 — operator approval pending.** The
> rotation-triple Phase-4 reconciliation is CLOSED: pitch `+0x2C` and roll
> `+0x28` agree on both replays (Oasis 48/48 + Dead Rail 56/56, score 1.0,
> flatness 1.0) via `yaw-diff --field pitch|roll` over the SAME immutable
> OD-088/089 dumps. `twoReplayRepeatability = true` for the full rotation
> triple roll `+0x28` / pitch `+0x2C` / yaw `+0x30`. Applying publishes
> `playerPitch`/`playerRoll` `Verified` via the module-rooted ring-record
> chains (the identical position walk + `recordOffset 44` / `recordOffset
> 40`); `offsets` stay 0 by design. Apply ONLY after operator approval, as
> a single conventional commit, mirroring the G1 yaw apply
> **APPLY REHEARSED (2026-08-12):** the Section 3 table edit was run on a
> scratch copy with a scratch validator (real canonical draft + pack doc
> cross-checks retained) and PASSES first run — `11.19.0.10.json: chains
> validated (7 field(s))`, walkable draft 7 fields, fidelity 7 fields,
> `PASS: All offset files are valid.` (exit 0), matching the post-edit
> expectation exactly. The pre-staging was complete — no gaps found (unlike
> the G2 apply, whose first rehearsal caught an incomplete checker spec;
> see `g2-damage-dealt-publication-draft.md`).
> (`docs/operations/g1-yaw-publication-draft.md`, OD-RECOVERY-092).

## 1. What gets published

| Field | Live evidence | Chain | offsets |
|---|---|---|---|
| `playerPitch` | **2026-08-12 rotation-triple reconciliation** (offline, same immutable OD-088/089 dumps): `yaw-diff --field pitch` → ring-record **`+0x2C`** float32, Oasis 48/48 + Dead Rail 56/56, score 1.0, flatness 1.0, `twoReplayRepeatability = true` | The **identical module-rooted walk as `playerPositionX`** (same root RVA, same entity lookup, same ring-index hop — the rotation triple was proven on the SAME record as position `+0x10`) with the final hop `recordOffset 44` (`0x2C`) instead of `recordOffset 16` (`0x10`) | `offsets.playerPitch` stays **0** (chained field) |
| `playerRoll` | Same reconciliation: `yaw-diff --field roll` → ring-record **`+0x28`** float32, Oasis 48/48 + Dead Rail 56/56, score 1.0, flatness 1.0, `twoReplayRepeatability = true` (record-span 0x38-trimmed verdicts — see §2 methodology) | Same prefix + final hop `recordOffset 40` (`0x28`) | `offsets.playerRoll` stays **0** (chained field) |

The chains are NOT duplicated here: the canonical walkable form lives in
`docs/operations/g0-walkable-position-chains.draft.json`
(`playerPositionX` hops 1..11). When applying, the published
`chains.playerPitch` / `chains.playerRoll` must be that prefix plus the
`recordOffset 44` / `recordOffset 40` hop (already staged in the draft).
The `fidelity` check (same shape as `offset_check.py`'s walkable-draft
check) must prove both chains walk to the resolver's ring-record record.

## 2. Pre-flight facts

| Fact | Value |
|---|---|
| Executable identity | `wotblitz.exe` v11.19.0.10 = `1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d` (re-measured, G0) |
| G1 (hardware-atomic read) | CLOSED — stored v4 aggregate 24/24 `stable-resolver-positive`, `allConsistentDoubleRead=true` |
| G2 (same-decoded-clock) | CLOSED — `sameDecodedClockProven=true`, 4+ live confirmations (every 088 dump attested) |
| G3 (repeatability) | rotation-triple Phase-4 **CLOSED 2026-08-12** — pitch/roll re-verdict the SAME immutable OD-088/089 dumps with `yaw-diff --field pitch\|roll` (`twoReplayRepeatability = true` for roll `+0x28` / pitch `+0x2C` / yaw `+0x30`) |
| Live evidence | OD-RECOVERY-088 (Oasis 48 dumps) + OD-RECOVERY-089 (Dead Rail 56 dumps) — the dumps themselves are the pitch/roll evidence (each 256-byte region covers the triple) |
| Static map | `RingRecordRegion` — `RollOffset 0x28` / `PitchOffset 0x2C` / `YawOffset 0x30` (already corrected and live-frame wired) |
| Schema | `memory-offsets/schema.json` already carries OPTIONAL `playerPitch`/`playerRoll` offset slots + chains/fieldValidation enums (pre-staged 2026-08-12) |

**Methodology lesson (recorded 2026-08-12):** Oasis's first roll verdict
matched `0x60` instead of `0x28`. Byte inspection proved region `0x60` is
the **next ring entry's `+0x28`** (stride 0x38) — the ring holds consecutive
position updates, so the sibling carries byte-near-identical values and
ties under the per-dump lag path. Re-verdicting the same dumps trimmed to
the single-record span (0x38) removes the decoy and both replays agree at
`+0x28`. The full-region verdict is retained in the ledger as the honest
first-pass; the trimmed verdict is the published evidence.

## 3. Apply steps (ONLY after operator approval)

> **Pre-staged 2026-08-12:** step 1's checker extension (the chains/fidelity
> validation already iterates `playerPitch`/`playerRoll` — skipped until the
> table gains the chains), step 3's schema slots (optional offset properties
> + both enums), and step 4's canonical draft chains are ALREADY staged and
> validator-clean. The apply commit is therefore: the table edit below +
> `offline/memory-offsets.md` + evidence; nothing else.

1. `memory-offsets/11.19.0.10.json`:
   - `fieldValidation.playerPitch` / `fieldValidation.playerRoll` →
     `status: "Verified"`, APPEND the reconciliation evidence entries
     (both replays, scores, flatness, the record-span trim), set
     `independentProcessLaunches`/`independentReplays` and approvals per
     the operator.
   - New `chains.playerPitch` (prefix + `recordOffset 44`) and
     `chains.playerRoll` (prefix + `recordOffset 40`).
   - `offsets.playerPitch` / `offsets.playerRoll` stay **0**. `notes` +
     G0-style summary.
2. `scripts/python/offset_check.py` — already extended (fidelity iterates
   the two new fields; the generic validator handles any `chains` field).
   Verify the log line "chains validated (7 field(s))" post-apply.
3. `offline/memory-offsets.md` — document the pitch/roll chains (mirror the
   yaw-chains section; note the rotation triple is fully published).
4. `docs/operations/g0-walkable-position-chains.draft.json` — chains
   already staged (fidelity check enforces walkable == published).

## 4. Post-edit gates (in order; stop on any failure)

```text
python scripts/python/offset_check.py --check-schema        # PASS expected, 7 chains validated + fidelity 7/7
tools/report-offset-evidence.ps1 -GameVersion 11.19.0.10     # runs clean
python scripts/python/offline_check.py --refresh             # file-tree + links
dotnet test tests/WotBTreader.GameIntegration.Tests -c Release --filter "FullyQualifiedName~ChainedFields"   # exclusion test passes
scripts/validate.ps1                                         # full gate, exit 0
```

Spot-checks: `offset_check.py` logs "chains validated (7 field(s))";
`ChainedFields_AreExcludedFromObservationReads` still passes (chained
pitch/roll never read as `moduleBase + 0`); `validate.ps1` exit 0.

## 5. Commit scope (ONE change)

Files: `memory-offsets/11.19.0.10.json`, `offline/memory-offsets.md`,
`offline/file-tree.md` (regenerated), `docs/operations/offset-discovery-ledger.md`
(the publication row), and a handoff. Message: conventional commit, e.g.
`feat(od): publish playerPitch/playerRoll as Verified via the module-rooted
ring-record chains (rotation triple, 2026-08-12 reconciliation)`. Do NOT
include velocity, `replayTime`, `cameraPitch`, `aliveTankCount`, the L3
damage-dealt lane, or any resolver/read-surface change in the same change.

## 6. NOT in scope (own gates)

- **L3 damage-dealt** — live session pending (approved launches;
  seam + rehearsal pre-staged, plan
  `docs/operations/l3-damage-dealt-avatar-family-plan.md`).
- **Velocity, replayTime, cameraPitch, aliveTankCount** — untouched.
- **Item 7 (hardware atomicity)** — stays LAST by design.
