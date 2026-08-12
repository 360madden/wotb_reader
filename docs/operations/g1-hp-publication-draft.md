# G1 — Player-HP publication (APPLIED 2026-08-12, OD-RECOVERY-092)

> **STATUS: APPLIED 2026-08-12 (operator-approved, OD-RECOVERY-092).**
> `playerHP` is published `Verified` via the module-rooted entity-base chain
> (9 hops: position hops 1..8 through the entity lookup + `recordOffset 184`
> = signed int16 current health at `[entity+0xB8]`); `offsets.playerHP`
> stays 0 by design. Gates were: **OD-RECOVERY-091** CLOSED HIT 2026-08-11
> (`+0xB8` on Dead Rail, score 1.0, flatness 1.0, Strict 4/4 exact sums —
> `twoReplayRepeatability = true`) + operator approval. This file was the
> operator-facing spec + checklist; the apply followed the G0 procedure
> (`docs/operations/g0-operator-checklist.md`). Section 3 below documents
> exactly what was done.

## 1. What gets published

| Field | Live evidence | Chain | offsets |
|---|---|---|---|
| `playerHP` (current health) | OD-RECOVERY-087 (Oasis Palms, victim 3760578, 74 dumps): entity-base **`+0xB8`** signed int16, score 1.0, flatness 1.0, Strict 8/8 exact sums (drops == damage sums, max `+0x11C` 1550 constant, alive `+0xBA` 1, healing `+0x11E` 0; variable ~1–3.4 s memory-apply lag measured); OD-RECOVERY-091 (Dead Rail, victim 2549395, 58 dumps): **`+0xB8` AGREES** — score 1.0, flatness 1.0, Strict 4/4 exact sums (drops 140 = 36+104, 76 = 76, 172 = 94+78, 55 = 55; max `+0x11C` 520 constant; Dead Rail memory LEADS the decoded clock ~2.5 s — the bounded lead-side window `--lag-lead-seconds`, default 0 = unchanged) | The **module-rooted walk of the resolver's entity-map family through the entity lookup** (the `playerPositionX` chain hops 1..8: GameCoreRootRva → … → entityLookup — hop 9+ in the position chain is the movement-filter/ring path, which HP does NOT take) with the final hop `{ "kind": "recordOffset", "value": 184, "note": "signed int16 current health at [entity+0xB8] (OD-RECOVERY-087/091 live-verified; max +0x11C, alive +0xBA siblings)" }` — the health field lives on the ENTITY BASE record itself, not the ring record | `offsets.playerHP` stays **0** (chained field — the runtime computes `moduleBase + offset` and the entity record is battle-scoped heap) |

The chain is NOT duplicated here: the canonical walkable form lives in
`docs/operations/g0-walkable-position-chains.draft.json` (hops 1..8 through
the entityLookup hop + `recordOffset 184` — PRE-STAGED 2026-08-11). When
applying, the published `chains.playerHP` must be that exact chain. A
`fidelity` check (the new entity-base shape branch in `offset_check.py`,
PRE-STAGED 2026-08-11) proves the HP chain walks to the resolver's entity
record. Companion reads (`+0x11C` max, `+0xBA` alive, `+0x11E` healing)
are documented in the chain note + `offline/memory-offsets.md`; they are
already decoded by the pure `EntityBaseRegion` reader shipped with the X4
L1 wiring (no resolver/read-surface change).

## 2. Pre-flight facts

| Fact | Value |
|---|---|
| Executable identity | `wotblitz.exe` v11.19.0.10 = `1cda5c31919c9784a41bee7f3270ec1b4536b124c51e8b36f2221b381760307d` (re-measured, G0) |
| G1 (hardware-atomic read) | CLOSED — stored v4 aggregate 24/24 `stable-resolver-positive`, `allConsistentDoubleRead=true` |
| G2 (same-decoded-clock) | CLOSED — `sameDecodedClockProven=true`, live confirmations (every 087/091 dump attested) |
| G3 (repeatability) | position-family closed (OD-075/076/078/081/082); HP repeatability = **OD-RECOVERY-091 CLOSED HIT** (`twoReplayRepeatability = true`) |
| Live evidence | OD-RECOVERY-087 + OD-RECOVERY-091 filled templates + ledger sections (both replays, opposite-sign label skew, bounded bidirectional attribution) |
| Static map | `EntityBaseRegion` decoders live (`+0xB8` current / `+0x11C` max / `+0xBA` alive) — `VerifyPlayerHpChain` static map confirmed live 087; `LiveFrameTankState.hpCurrent/hpMax/alive` wired into the X4 frame |

## 3. Apply steps (ONLY after operator approval)

> **Pre-staged 2026-08-11 (dry-run validator-clean):** step 2's checker
> extension and step 4's canonical draft chain are ALREADY staged —
> `offset_check.py` fidelity now iterates `playerHP` with the entity-base
> (9-hop) shape branch (skipped until the published table gains the chain),
> and the walkable draft carries `chains.playerHP` (entityLookup prefix +
> `recordOffset 184`). A scratch dry-run of the full check passed
> `validate_offset_file` (draft chains validated 5 field(s)) with zero
> fidelity issues and no regression on the 3 position fields. The apply
> commit is therefore: the table edit below + `offline/memory-offsets.md`
> + evidence; nothing else.

1. `memory-offsets/11.19.0.10.json`:
   - `fieldValidation.playerHP` → `status: "Verified"`, APPEND the
     OD-RECOVERY-087 + 091 evidence entries (live dumps, scores, lag/lead
     skew, exact-sum tracks), `independentProcessLaunches`/`independentReplays` ≥
     the position family's, `harnessInvariantsPassed: true`, approvals set
     by the operator (lead + decoder-auditor).
   - New `chains.playerHP` per §1 (entity-lookup prefix + `recordOffset 184`).
   - `offsets.playerHP` stays **0**. `notes` + G0-style summary.
2. `scripts/python/offset_check.py` — PRE-STAGED (entity-base shape branch
   + fidelity iteration; nothing to do at apply time).
3. `offline/memory-offsets.md` — document the HP chain (mirror the
   position-chains section; note the `+0x11C`/`+0xBA`/`+0x11E` siblings).
4. `docs/operations/g0-walkable-position-chains.draft.json` — PRE-STAGED
   (the canonical `playerHP` chain is already in the file; the walkable
   form and the published form stay identical — fidelity enforced).

## 4. Post-edit gates (in order; stop on any failure)

```text
python scripts/python/offset_check.py --check-schema        # PASS expected, HP chain validated
tools/report-offset-evidence.ps1 -GameVersion 11.19.0.10     # runs clean
python scripts/python/offline_check.py --refresh             # file-tree + links
dotnet test tests/WotBTreader.GameIntegration.Tests -c Release --filter "FullyQualifiedName~ChainedFields"   # exclusion test passes
scripts/validate.ps1                                         # full gate, exit 0
```

Spot-checks: `offset_check.py` logs "chains validated (4 field(s))";
`ChainedFields_AreExcludedFromObservationReads` still passes (chained HP
never read as `moduleBase + 0`); `validate.ps1` exit 0.

## 5. Commit scope (ONE change)

Files: `memory-offsets/11.19.0.10.json`, `scripts/python/offset_check.py`
(if the validator needed an HP-specific case), `offline/memory-offsets.md`,
`offline/file-tree.md` (regenerated), `docs/operations/g0-walkable-position-chains.draft.json`,
`docs/operations/offset-discovery-ledger.md` (the publication row), and a
handoff. Message: conventional commit, e.g. `feat(od): publish playerHP as
Verified via the module-rooted entity-base chain (OD-RECOVERY-087/091)`.
Do NOT include velocity, yaw, `replayTime`, `cameraPitch`,
`aliveTankCount`, or any resolver/read-surface change in the same change.

## 6. NOT in scope (own gates)

- **Yaw publication** — READY in parallel
  (`docs/operations/g1-yaw-publication-draft.md`), operator approval only.
- **Velocity, replayTime, cameraPitch, aliveTankCount** — untouched.
- **Item 7 (hardware atomicity)** — stays LAST by design.
