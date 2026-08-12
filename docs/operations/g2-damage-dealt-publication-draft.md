# G2 — Damage-dealt publication (APPLIED 2026-08-12, OD-RECOVERY-097)

> **STATUS: APPLIED (2026-08-12, OD-RECOVERY-097, operator-approved).** The
> schema decision (`vftableScan` hop kind + `damageDealt` field) was
> approved and §4 steps 1–5 were executed (this document's corrected step 2
> included). The real gates also surfaced two extensions the draft did not
> enumerate: `tools/report-offset-evidence.ps1` (knownFields + optional set
> + the G1-era GameHarness-kind acceptance — pre-existing drift, also fixed
> playerHP's missing StaticAnalysis evidence) and
> `OffsetTableReader.KnownFieldNames` (pre-staged pitch/roll too). All §5
> post-edit gates green (`offset_check --check-schema` 6 chains + fidelity
> 6/6, report exit 0, ChainedFields exclusion test, `validate.ps1` exit 0).
> **Consumption IMPLEMENTED (2026-08-12, post-apply):** the live frame's
> own-row `DamageDealt` is no longer honest-0 — `LiveFrameReadRequest`
> gained `OwnEntityId` (the decoded session's viewpoint participant's
> entity id, derived by the endpoint before the read), the coordinator
> reads the own Avatar's battle-stats dword0 via the gated vftable scan +
> quad read (fail-closed: any failure leaves the row null, never guessed),
> and the projector maps it for the OWN row only (all other rows stay 0).
> Tests: coordinator (attached + fail-closed), projector (own-only),
> endpoint (own-id forwarded).
> **APPLY REHEARSED (2026-08-12):** the full §4 apply (steps 1–5, corrected
> step 2 below) was run end-to-end on SCRATCH copies with a scratch validator
> and PASSES — `11.19.0.10.json: chains validated (6 field(s))`, walkable
> draft 8 fields, fidelity 6 fields, `PASS: All offset files are valid.`
> (exit 0). The rehearsal caught that the originally drafted step 2 was
> INCOMPLETE: it would have failed the gate with 11 issues (missing
> `FIELD_DEFS`/`OPTIONAL_FIELDS`/shape-check/fidelity-branch extensions);
> step 2 below is the CORRECTED, rehearsal-proven spec.
> This is the operator-facing spec + checklist for publishing `damageDealt`
> as `Verified`, mirroring the G1 HP/yaw packages
> (`docs/operations/g1-hp-publication-draft.md` /
> `g1-yaw-publication-draft.md`). The Phase-4 gates are CLOSED
> (**OD-RECOVERY-095** Oasis + **OD-RECOVERY-096** Dead Rail,
> `twoReplayRepeatability = true`, offset agrees at `0x0` on both replays).
> **One material difference from G1:** this publication adds `damageDealt`
> to the offset-table schema AND introduces a new chain hop kind
> (`vftableScan`) — the scan-based anchor has no representation in the
> current hop taxonomy. That is a data-contract decision for the operator;
> Section 2 spells out exactly what the schema edit is. Consumption (the
> live frame's `DamageDealt` field) is NOT part of this package — the read
> surface stays untouched, exactly as in G0/G1.

## 1. What gets published

| Field | Live evidence | Chain | offsets |
|---|---|---|---|
| `damageDealt` (cumulative own damage dealt, uint32) | OD-RECOVERY-095 (Oasis Palms, session `019ff5f1`, 20 region dumps): the avatar-stats quad dword0 increments 1:1 with the decoded own-attacker events — re-verdict with the bounded lag path (`hp-diff --lag-tolerance`, the OD-087-established class): **offset 0x0, score 1.0, matched 5/5 damage windows with EXACT sums (152/144/151/170/1), flatness 1.0 (0/0 control windows), Strict 5/5 → HIT**; d0 final **752 = decoded `damageDealt` 752**; the at-session lag-0 verdict was an honest-negative from the +2.3–4.1 s memory-apply lag (control-window changes were real events). OD-RECOVERY-096 (Dead Rail, session `019ff6f0`, 38 dumps, clock labels 158.0–276.9 s): **offset 0x0, score 1.0, matched 9/9 windows with EXACT sums (146/162/145/162/140/178/181/171/168), flatness 1.0, Strict ≥ 2 → HIT**; d0 final **1598 = decoded `damageDealt` 1598** (all 10 decoded own-attacker events map 1:1; the first 145 at 154.5 s predates the earliest dump label). **Offsets agree across both replays (0x0) → `twoReplayRepeatability = true`** | The **gated vftable AOB scan for the entity-factory Avatar** (the reach path CORRECTED 2026-08-12: the camera chain's `avatarAddress` anchors `AvatarControllerReplay` vftable `0x3677e8c` — a DIFFERENT object; the stats quad lives on the entity-factory Avatar, vftable `0x36752a4` = RVA `0x032752a4`, 0x128-byte object) with the terminal hop `recordOffset 280` (`0x118`) = the uint32 battle-stats quad base, **dword0 = `damageDealt`**. The scan rides the existing guarded reader lease + ONE G2 attestation, identity re-gates the chosen candidate's vftable dword, max 4 candidates, alignment 4 (the `avatar-stats` region anchor, `EntityRecordRegionAnchor.AvatarStats`, already shipped + 6 tests) | `offsets.damageDealt` stays **0** (chained field — the runtime computes `moduleBase + offset` and the Avatar object is battle-scoped heap) |

The quad at `[avatar+0x118]` is, in order (property indices 0xA–0xD via the
property-change dispatcher `FUN_01670de0`):
`[damageDealt, damageBlocked, damageAssisted1, damageAssisted2]` — layout
refined 2026-08-12: Dead Rail finals d0 1598 / d1 140 / d3 228 == decoded
`damageDealt` 1598 / `damageBlocked` 140 / `damageAssisted2` 228; Oasis
(OD-095) d2 126 == `damageAssisted1`. Only dword0 (`damageDealt`) is
published here; the other three dwords stay honest-unknown in the table
(measured, not gate-closed to the Phase-4 standard for THEIR consumers —
documented in the chain note, not published).

## 2. The one schema-contract decision (operator approval point)

Unlike G1 (where `playerHP`/`playerYaw` already existed in the schema), the
offset table has NO `damageDealt` field and the `chains` hop taxonomy has
NO scan hop. Publishing therefore requires a small, explicit contract
addition — the ONLY material difference from the G1 apply:

1. `memory-offsets/schema.json`:
   - `properties.chains.propertyNames.enum` += `"damageDealt"` (and the
     `properties.offsets` / `properties.fieldValidation` property objects
     gain `damageDealt` — `offsets` stays `0` by design, description notes
     the chained form).
   - `properties.chains.additionalProperties.items.properties.kind.enum` +=
     `"vftableScan"` — semantics: scan the module's Private+Mapped regions
     for an object whose vftable dword == `moduleBase + value` (value = the
     vftable RVA); the hop carries the RVA (value), the identity re-gate is
     implicit (the chosen candidate's vftable dword must equal the target),
     and scan bounds (max 4 candidates, alignment 4) go in the hop note.
     Update the hop-family description to document it.
2. `docs/operations/g0-walkable-position-chains.draft.json` — add the
   canonical `chains.damageDealt` (see §4) so the walkable draft and the
   published table stay identical (fidelity enforced).
3. `scripts/python/offset_check.py` — extend the chains validator to accept
   `vftableScan` (shape check: kind + value = RVA; no deref walk exists —
   the fidelity check validates shape + identity, NOT a resolver walk,
   because no resolver reads `damageDealt` today).

If the operator prefers NOT to grow the hop taxonomy, the alternative is to
keep `damageDealt` out of the published table (evidence stays in the ledger
+ L3 plan only, frame stays honest-0) — that is the fail-closed default.

## 3. Pre-flight facts

| Fact | Value |
|---|---|
| Executable identity | `wotblitz.exe` v11.19.0.10 = `1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d` (re-measured, G0) |
| G1 (hardware-atomic read) | CLOSED — stored v4 aggregate 24/24 `stable-resolver-positive`, `allConsistentDoubleRead=true` |
| G2 (same-decoded-clock) | CLOSED — `sameDecodedClockProven=true`, live confirmations (every 095/096 dump attested via the guarded lease + ONE G2 attestation) |
| G3 (repeatability) | position-family closed (OD-075/076/078/081/082); damage-dealt repeatability = **OD-RECOVERY-096 CLOSED HIT** (`twoReplayRepeatability = true`, offset 0x0 on Oasis 5/5 + Dead Rail 9/9, score 1.0 / flatness 1.0 each) |
| Live evidence | OD-RECOVERY-095 + OD-RECOVERY-096 ledger rows (both replays, opposite-sign memory-clock skew handled by the bounded bidirectional lag path), rehearsal `invoke-avatar-stats-rehearsal.ps1` (offline, real decoded session — candidate-0 HIT, flat candidates NOT hit, PHASE-4 two-replay simulation offsets agree) |
| Static map | Avatar object vftable `0x36752a4` (RVA `0x032752a4`, 0x128 bytes, entity-factory case 1 `FUN_01669c90`); stats quad `+0x118..+0x124` (indices 0xA–0xD, dispatcher `FUN_01670de0`); reachability CORRECTED — do NOT reuse the camera anchor (`AvatarControllerReplay`) |
| Read seam | `/discover/entity-region` `avatar-stats` anchor (gated AOB scan + identity re-gate + quad at candidate+0x118, `avatarCandidateIndex` 0..3, `avatarCandidateCount`, fail-closed `AvatarAnchorNotFound`/`AvatarIdentityMismatch`) — shipped + rehearsed + live-proven 2026-08-12 (sweep 4: `status='Resolved' candidates=1` every probe, sessions 019ff5cc/019ff5dc/019ff5f1) |

## 4. Apply steps (ONLY after operator approval)

> **NOT pre-staged** (unlike G1): the schema hop-kind addition, the checker
> extension, and the walkable-draft chain are deliberately deferred to the
> operator's go-ahead on Section 2. The apply commit is: the schema edit +
> checker + walkable draft + table edit + `offline/memory-offsets.md` +
> evidence; nothing else (no read-surface / resolver / frame change).

1. `memory-offsets/schema.json` — Section 2 additions (`damageDealt` field +
   `vftableScan` hop kind + description update).
2. `scripts/python/offset_check.py` — `vftableScan` hop acceptance + the
   `damageDealt` fidelity iteration (shape/identity, no resolver walk).
   **CORRECTED 2026-08-12 by the apply rehearsal** — the bare wording was
   incomplete; the concrete extensions are all four of:
   - `FIELD_DEFS` gains `("damageDealt", "uint32", "Cumulative own damage
     dealt (avatar-stats quad dword0)")` (the master field list;
     `FIELD_NAMES` derives from it and the schema cross-check requires it).
   - `OPTIONAL_FIELDS` gains `{"damageDealt"}` (pre-staged like
     `playerPitch`/`playerRoll`, so the version tables' `required` list
     does NOT grow — damageDealt is optional in the table).
   - Chain **shape check**: the first hop may be `vftableScan` in addition
     to `rootRva` (`first not in ("rootRva", "vftableScan")`).
   - `walkable_fidelity_issues`: a `field == "damageDealt"` special case
     mirroring the `playerHP` branch — expected kinds
     `["vftableScan", "recordOffset"]`, published identical to the
     canonical draft (signature per hop).
3. `docs/operations/g0-walkable-position-chains.draft.json` — add
   `chains.damageDealt` (copy-verbatim form, valid JSON):

   ```json
   [
     {
       "kind": "vftableScan",
       "value": 52908708,
       "note": "gated AOB scan for the entity-factory Avatar vftable dword == moduleBase + 0x032752a4 (0x128-byte object); identity re-gated; max 4 candidates, alignment 4; own counter discriminated by increment correlation (OD-RECOVERY-095/096)"
     },
     {
       "kind": "recordOffset",
       "value": 280,
       "note": "uint32 battle-stats quad base [avatar+0x118]; dword0 = cumulative own damageDealt; quad = [damageDealt, damageBlocked, damageAssisted1, damageAssisted2] (indices 0xA-0xD); OD-RECOVERY-095/096 live-verified (finals 752 / 1598 = decoded damageDealt)"
     }
   ]
   ```
4. `memory-offsets/11.19.0.10.json`:
   - `fieldValidation.damageDealt` → `status: "Verified"`, APPEND the
     OD-RECOVERY-095 + 096 evidence entries (live dumps, scores, lag
     windows, exact-sum tracks, final==decoded cross-checks),
     `independentProcessLaunches`/`independentReplays` ≥ the position
     family's, `harnessInvariantsPassed: true`, approvals set by the
     operator (lead + decoder-auditor).
   - New `chains.damageDealt` per §4 step 3 (identical to the draft).
   - `offsets.damageDealt` stays **0**. `notes` + G0-style summary.
5. `offline/memory-offsets.md` — document the damage-dealt chain (mirror
   the position-chains section; note the scan-based anchor + quad layout +
   the three non-published sibling dwords).

## 5. Post-edit gates (in order; stop on any failure)

```text
python scripts/python/offset_check.py --check-schema        # PASS expected, damageDealt chain validated
tools/report-offset-evidence.ps1 -GameVersion 11.19.0.10     # runs clean
python scripts/python/offline_check.py --refresh             # file-tree + links
dotnet test tests/WotBTreader.GameIntegration.Tests -c Release --filter "FullyQualifiedName~ChainedFields"   # exclusion test passes
scripts/validate.ps1                                         # full gate, exit 0
```

Spot-checks: `offset_check.py` logs "chains validated (6 field(s))";
`ChainedFields_AreExcludedFromObservationReads` still passes (chained
damageDealt never read as `moduleBase + 0`); `validate.ps1` exit 0.

## 6. Commit scope (ONE change)

Files: `memory-offsets/schema.json`, `memory-offsets/11.19.0.10.json`,
`scripts/python/offset_check.py`, `offline/memory-offsets.md`,
`offline/file-tree.md` (regenerated),
`docs/operations/g0-walkable-position-chains.draft.json`,
`docs/operations/offset-discovery-ledger.md` (the publication row), and a
handoff. Message: conventional commit, e.g. `feat(od): publish damageDealt
as Verified via the module-rooted avatar vftable-scan chain
(OD-RECOVERY-095/096)`. Do NOT include velocity, `replayTime`,
`cameraPitch`, `aliveTankCount`, pitch/roll publication, the live-frame
wiring, or any resolver/read-surface change in the same change.

## 7. NOT in scope (own gates)

- **Live-frame consumption** — COMMITTED 2026-08-12 after this apply: the
  live frame's own-row `DamageDealt` reads the published chain via the
  coordinator's avatar-stats anchor (own id from the decoded viewpoint join;
  fail-closed null on any failure, never guessed; projector own-row only).
  This publication was evidence + table record; the consumption landed as a
  separate read-surface commit.
- **Enemy/teammate per-row damage** — stays honest-unknown (their stats
  objects are not in the player's memory map); the live scoreboard is
  own-row-only.
- **The other three quad dwords** (`damageBlocked`, `damageAssisted1/2`) —
  measured (Dead Rail finals 140 / 0 / 228 == decoded), NOT published
  (their consumers lack Phase-4 gate-closed evidence).
- **Pitch/roll publication** — READY in parallel
  (`docs/operations/g1-pitch-roll-publication-draft.md`), operator approval
  only.
- **Item 7 (hardware atomicity)** — stays LAST by design. **Branch A quad
  sub-proof DONE (2026-08-12, hash-bound `1cda5c31…`):** width-complete
  census of every write to `+0x118/+0x11C/+0x120/+0x124` (MOV + RMW —
  ADD/SUB/XOR/INC/DEC — all widths, because damageDealt INCREMENTS): 1688
  byte-scan candidates → **1646 confirmed at real instruction boundaries,
  4 register-only misattributions rejected (semantic filter)** → **ZERO
  64-bit/128-bit writes to any quad dword**; d0 (`damageDealt`) = 401× dword
  (10 in-place RMW) + 10× byte, all aligned 32-bit-or-narrower → a 32-bit
  read of d0 cannot tear; bounded live by OD-RECOVERY-095/096 exact
  increment reads. Census opcode-complete for write families: MOV + RMW
  (ADD/SUB/XOR reg+imm, INC/DEC) + XADD (`0F C1`) + CMPXCHG (`0F B1`) — the
  XADD/CMPXCHG addition re-ran with unchanged results (zero such writes).
  Tooling: `ScanAvatarStatsQuadStoreWidths.java` +
  `ConfirmAvatarStatsQuadSites.java` (new, `tools/ghidra-scripts/`); evidence
  `.build/ghidra-evidence-avatar-quad/`. The write-path classification
  (which function is the live damage increment) stays open — the 10
  confirmed in-place RMWs to d0 are all FIXED increments (INC/ADD-imm), so
  the variable damage sum is applied via a LOAD-ADD-STORE (store half: one
  of the 163 register-source `MOV [..+0x118], reg` sites); pinning the exact
  function needs dataflow tracing. The census bounds the atomicity
  statically (all ≤32-bit), the live reads bound the semantics.
