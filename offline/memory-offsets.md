# Memory-offset evidence

How versioned game-memory offsets are stored, validated, and gated.
Canonical detail: [`memory-offsets/README.md`](../memory-offsets/README.md)
(discovery pipeline + confidence levels) and
[`docs/operations/offset-discovery-guide.md`](../docs/operations/offset-discovery-guide.md)
(4-phase tool workflow).

## What lives in `memory-offsets/`

| File | Purpose |
|------|---------|
| `schema.json` | JSON schema (draft 2020-12) every version file must satisfy |
| `<gameVersion>.json` | One file per game version, e.g. `11.19.0.10.json` |
| `scanner-state.json` | Runtime scanner state — **gitignored, never committed** |

## Version-file format (`schema.json` + `OffsetFileJson`)

```json
{
  "schemaVersion": 1,
  "gameVersion": "11.19.0.10",
  "executableSha256": "<sha256 of wotblitz.exe>",
  "discoveredAtUtc": "2026-07-30T…Z",
  "fieldValidation": {
    "playerYaw": {
      "status": "Candidate",
      "evidence": [],
      "independentProcessLaunches": 0,
      "independentReplays": 0,
      "harnessInvariantsPassed": false,
      "leadApproved": false,
      "decoderAuditorApproved": false
    }
  },
  "offsets": { "replayTime": 0, "playerHP": 0, "playerPositionX": 0,
               "playerPositionY": 0, "playerPositionZ": 0, "playerYaw": 0,
               "playerPitch": 0, "playerRoll": 0,
               "cameraPitch": 0, "aliveTankCount": 0, "damageDealt": 0 },
  "confidence": "none",
  "notes": ""
}
```

Required: `schemaVersion`, `gameVersion`, `offsets` (all 11 declared
fields — 8 required plus the three OPTIONAL chained fields
`playerPitch`/`playerRoll`/`damageDealt` —
`additionalProperties: false`). `executableSha256` is required for candidate
or promoted evidence. An intentional placeholder has `confidence: "none"`, an
empty hash, `discoveredAtUtc: null`, and all offsets set to `0`;
placeholders are never runtime-supported. `fieldValidation` is optional; when
present, its keys are the known fields and each entry records promotion
evidence. `0` = unknown.

| Field | Type | Semantics |
|-------|------|-----------|
| `replayTime` | double | Replay timeline seconds |
| `playerHP` | int32 | Current hit points |
| `playerPositionX/Y/Z` | float | World units (Y = height) |
| `playerYaw` | float | Radians (ring-record `+0x30`, published) |
| `playerPitch` | float | Radians (ring-record `+0x2C`, published) |
| `playerRoll` | float | Radians (ring-record `+0x28`, published) |
| `damageDealt` | uint32 | Cumulative own damage dealt (avatar-stats quad dword0 `[avatar+0x118]`, published) |
| `cameraPitch` | float | Radians |
| `aliveTankCount` | int32 | Tanks still in battle |

## Confidence levels

| Level | Meaning |
|-------|---------|
| `none` | Placeholder, nothing discovered |
| `low` | Candidates found, unverified |
| `medium` | 1–3 candidates, matches game behaviour in one battle |
| `high` | Verified across multiple battles and game restarts |

## Reader + validation (`OffsetTableReader`)

`Application/Replay/OffsetTableReader.cs` loads `<gameVersion>.json` and fails
with a specific code when anything is off:

| Check | Failure code |
|-------|--------------|
| File missing | returns `null` (no table — callers treat as unsupported) |
| Unreadable / bad JSON | `offset.read_failed` / `offset.empty_file` |
| `schemaVersion` ≠ 1 | `offset.unsupported_schema` |
| `gameVersion` ≠ file name | `offset.version_mismatch` |
| Declared `executableSha256` missing or malformed | `offset.hash_missing` |
| Observed executable hash missing or malformed | `offset.invalid_observed_hash` |
| Declared `executableSha256` ≠ observed exe hash | `offset.hash_mismatch` |

Per-field: `OffsetField(Offset == 0 ? Unknown : Candidate)` unless a
`Verified` declaration also contains at least two independent process launches,
two independent replays, passing GameHarness invariants, lead approval, decoder
auditor approval, and both static-analysis and GameHarness provenance. Only then
is the field `Verified`; runtime reads reject candidate fields. The file-level
`confidence` field maps to the table's overall `OffsetConfidence`. Domain model:
[`src/WotBTreader.Core/OffsetModels.cs`](../src/WotBTreader.Core/OffsetModels.cs)
(`OffsetField`, `OffsetTable`, `OffsetFieldStatus`, `OffsetProvenanceKind`).

The directory is resolved by `Application` DI
(`ApplicationServiceCollectionExtensions`): `AppContext.BaseDirectory` →
`memory-offsets`, falling back to repo-root discovery.

## Runtime gating (`GameSessionCoordinator`)

- `LoadOffsetTable(process)` requires an exact version + executable-hash match.
- The observation read path (`ReadMemoryAsync`) reads only fields where
  `Offset != 0 && Status == Verified` (direct module-relative reads:
  `moduleBase + offset`). Chained fields (offset 0) are excluded **by
  construction**; `Candidate`/`Unknown`/`Stale` are never read. With no
  known fields the observation is `Available` with all-null fields — the
  endpoint never refuses a verified session (`GET /api/v1/game/memory`
  returns `Availability: Available|Unknown`, HTTP 200).
- Live observation pushes only happen after the `OfflineReplayVerified` gate.

Full trace: `docs/operations/legacy-observation-surface.md`.

## Chains (pointer-chain verification)

Since OD-RECOVERY-083 (2026-08-10), version files may carry a top-level
`chains` object mapping a field name to an ordered array of hops, each
`{ "kind": "rootRva" | "memberOffset" | "inlineOffset" | "recordOffset" | "ringIndex" | "entityLookup" | "vftableScan", "value": <non-negative int>, "note": <text> }`.
Semantics (2026-08-10 walker rework, clean object model): `rootRva`
dereferences the root slot (`moduleBase + value`); `memberOffset` dereferences
a pointer at (object + value); `inlineOffset` adds value WITHOUT dereferencing
(an inline member, e.g. the entities map embedded in the connection object);
`ringIndex` selects an INLINE ring entry at (object + value + index·stride)
using the Int32 index field at (object + `indexOffset`) — no ring pointer
dereference; `entityLookup` resolves an entity-map lookup (cached fast path +
ALTERNATIVE tree roots, node layout in its descriptor) with the target entity
id supplied per walk — never carried by the chain. `ringIndex` requires
`indexOffset` and `stride`; `entityLookup` requires its descriptor fields.
The chain is the module-relative dereference path the resolver walks to the
field (verified against
`Type10EntityPositionResolver.TryResolveOnce`/`FindEntity`). The runtime
parses `chains` into the model (`OffsetTableReader`, additive — the legacy
observation path is unchanged) and `OffsetChainWalker` walks chains
fail-closed.The published 11.19.0.10 position chains ARE mechanically walkable since
OD-RECOVERY-084 (2026-08-10): they use `inlineOffset` (entities map, no
deref), `entityLookup` (cache fast path + three alternative entity-tree map
roots, node layout in the descriptor, target entity id supplied per walk), and
INLINE `ringIndex` — the same walk the OD-RECOVERY-083 evidence verified,
re-expressed with correct semantics. `Walk_PublishedTableChains_*` proves the
walker's walk of the PUBLISHED chains equals the resolver's traversal on
identical memory (cache/tree/alternative-root/not-found). The canonical form
lives in `docs/operations/g0-walkable-position-chains.draft.json`; the
pre-publication memberOffset-spelled form remains in git history (commit
`0e6bdba`) and the ledger. The resolver remains the authoritative position
reader; the walker is a proven-equivalent consumer of the published table.

Chained fields keep their `offsets` value `0` **by design**: the runtime
observation path computes `moduleBase + field.Offset` (no chain concept) and
the ring record is battle-scoped heap (never publishable), so a non-zero
`offsets` entry would make the legacy observation path read a bogus address.
The resolver reads position through its own hash-bound layout; chained fields
are excluded from observation reads (pinned by
`ChainedFields_AreExcludedFromObservationReads`).

The position family — `playerPositionX/Y/Z` (float32 triple at record
`+0x10/+0x14/+0x18`) — is `Verified` via the module-rooted chain
(`GameCoreRootRva 0x04095C88` = 67722376), published in
`11.19.0.10.json`. `scripts/python/offset_check.py` validates the `chains`
object (chained field offsets must be 0; hops must be non-empty with valid
`kind`/non-negative `value`; the first hex literal in each hop's `note` must
match its decimal `value` — catches hex↔decimal transcription drift, e.g.
the G0 grill's `0x04095C88` ≠ 67518856 error); absent `chains` is a no-op.

### HP and hull-yaw chains (G1, 2026-08-12)

Two more fields joined the walkable `chains` family under G1
(OD-RECOVERY-087/088/089/091, operator-approved 2026-08-12):

- **`playerHP`** — `Verified` via the **entity-base** chain: the module-rooted
  walk of the resolver's entity-map family through the `entityLookup` hop
  ONLY (`GameCoreRootRva` → … → `entityLookup`, the position chain's hops
  1..8 — HP does NOT take the movement-filter/ring path) with final hop
  `recordOffset 184` = signed int16 current health at `[entity+0xB8]`
  (OD-RECOVERY-087 savanna 74 dumps Strict 8/8 + OD-RECOVERY-091 Dead
  Rail 58 dumps Strict 4/4 via the lead-side window —
  `twoReplayRepeatability = true`). Sibling fields on the same entity-base
  record: max `+0x11C`, alive `+0xBA`, healing `+0x11E` (decoded by the pure
  `EntityBaseRegion` reader; no resolver/read-surface change).
- **`playerYaw`** — `Verified` via the ring-record chain: the IDENTICAL
  module-rooted walk as `playerPositionX` (position `+0x10` and yaw `+0x30`
  were proven on the SAME ring record) with final hop `recordOffset 48` =
  float32 hull yaw at `+0x30` (OD-RECOVERY-088 savanna 48/48 + OD-RECOVERY-089
  medvedkovo 56/56 per-dump bidirectional lag — `twoReplayRepeatability =
  true`). The rotation triple is roll `+0x28` / pitch `+0x2C` / yaw `+0x30`;
  this resolves-by-supersession the quarantined static yaw candidate
  (ring-record `RingRecordRegion.YawOffset = 0x30` is live-verified).
- **`playerPitch` / `playerRoll`** (2026-08-12, OD-RECOVERY-098) — `Verified`
  via the SAME ring-record chain with final hops `recordOffset 44`
  (float32 hull pitch at `+0x2C`) / `recordOffset 40` (float32 hull roll at
  `+0x28`). Rotation-triple reconciliation: `yaw-diff --field pitch|roll`
  re-verdicts the SAME immutable OD-088/089 dumps — savanna 48/48 + medvedkovo
  56/56 each, score 1.0, flatness 1.0 (record-span 0x38-trimmed;
  `--record-span` excludes the next ring entry's byte-near-identical
  sibling decoy), `twoReplayRepeatability = true`. With this apply the
  rotation triple roll `+0x28` / pitch `+0x2C` / yaw `+0x30` is FULLY
  published.

Both keep `offsets` 0 by design (battle-scoped heap, same rationale as the
position family); the canonical walkable forms are the SAME hops as the
published table (fidelity-enforced by `offset_check.py`).

### Damage-dealt chain (G2, 2026-08-12)

- **`damageDealt`** — `Verified` via a NEW **scan-based** anchor:
  `vftableScan` (FIRST hop; semantics: scan the module's Private+Mapped
  regions for an object whose vftable dword == `moduleBase + value`, value =
  the vftable RVA `0x032752a4` = 52908708 of the entity-factory Avatar,
  0x128-byte object; the identity re-gate is implicit — the chosen
  candidate's vftable dword must equal the target; scan bounds max 4
  candidates / alignment 4 ride the hop note) with final hop
  `recordOffset 280` = uint32 battle-stats quad base `[avatar+0x118]`,
  **dword0 = cumulative own `damageDealt`** (OD-RECOVERY-095 savanna 5/5
  exact sums via the bounded lag path + OD-RECOVERY-096 medvedkovo 9/9 —
  `twoReplayRepeatability = true`, finals 752 / 1598 = decoded
  `damageDealt`). Quad layout: `[damageDealt, damageBlocked,
  damageAssisted1, damageAssisted2]` (property indices 0xA-0xD via the
  property-change dispatcher `FUN_01670de0`); the three sibling dwords stay
  honest-unknown (measured, not Phase-4-closed for their consumers).
  `offsets.damageDealt` stays 0 (the Avatar object is battle-scoped heap).
  Reachability note: the camera chain's `avatarAddress` anchors
  `AvatarControllerReplay` (a DIFFERENT object) — the scan targets the
  entity-factory Avatar, never the camera anchor. **Consumption committed
  (2026-08-12):** the live frame's own row `DamageDealt` now reads this
  published chain via the coordinator's avatar-stats anchor (own id from the
  decoded viewpoint join; fail-closed null on any failure, never guessed;
  projector own-row only — enemy/teammate rows stay honest-0); the read path
  was proven live in-session by OD-RECOVERY-099 (every avatar-stats probe
  `Resolved candidates=1`, verdict HIT at default lag).

## Validation tooling

- `scripts/python/offset_check.py` — schema compliance: `schemaVersion` = 1,
  placeholder-aware sha256/date rules, filename↔`gameVersion`, all 8 fields present, offset
  plausibility (not too small / > 2 GB), no extra fields, valid confidence,
  `discoveredAtUtc` present. Run with `--check-schema` to also cross-verify
  this page's documented contract against `schema.json` and the validator's
  own constants (CROSS-CHECK issues) plus each version file's keys and
  confidence value (DOC-CHECK issues). Output:
  `.build/offset-check-<timestamp>.log`.

## Hard rules

- Evidence-first: never fabricate offsets; `0` stays unknown until discovered.
- `confidence: "none"` placeholders must be updated before they are treated as real.
- Never commit `scanner-state.json`, scan files, memory dumps, pointer maps,
  absolute paths, or machine-specific paths in notes.
- Cheat Engine / Ghidra are approved for **offline replay sessions only**.
