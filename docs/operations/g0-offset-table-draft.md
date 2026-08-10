# G0 — Offset-table publication draft (operator-approved change, ready to apply)

Prepared 2026-08-09 after OD-RECOVERY-082 (G1 + G3 closed, G2 closed) and the
G0 review verdict **PROMOTE-READY (conditional)**. This document is the
**draft** — nothing here is applied. The operator approves, then the change
below is applied as ONE commit with `numericOffsetPublication: true`.

## 0. The grill's decisive finding (why the chain is not a single integer)

The G0 review's schema-representation decision item is resolved by a
grilling pass that settled the runtime facts from code:

- `GameSessionCoordinator.ReadMemoryAsync` treats `OffsetField.Offset` as a
  **direct module-relative offset**: `absoluteAddress = moduleBase +
  (nint)field.Offset` (`GameSessionCoordinator.cs`). It reads `size` bytes
  there for every `Offset != 0 && Status == Verified` field.
- The position is the end of a **pointer chain** (root RVA → controllers →
  BWEntities → entity → filter → helper → ring record `+0x10`). There is no
  single module-relative value that IS the position.
- The ring record itself is **battle-scoped heap** (live addresses like
  `0x3557E888` are ASLR runtime addresses) — publishing it is explicitly
  forbidden by the G0 review (no absolute/heap addresses).
- Therefore: setting a non-zero `offsets.playerPositionX/Y/Z` would make the
  legacy observation path read a **bogus address** (`base + value`), and no
  single integer can represent the chain honestly.

**Decision:** `offsets.playerPositionX/Y/Z` **stay 0** (runtime-safe — the
legacy path never misreads; position is read by the resolver via its own
hash-bound layout, not the table). The verification is recorded in
`fieldValidation` (`status: Verified` + evidence) and the chain is expressed
explicitly in a new additive `chains` section. `schemaVersion` stays 1 (the
runtime reader hard-requires 1; the additive key is backward compatible —
`OffsetTableReader` uses System.Text.Json defaults and ignores unknown keys,
but `schema.json`'s `additionalProperties: false` needs the key declared for
any real JSON-Schema validation).

## 1. `memory-offsets/schema.json` — add the `chains` property

Add to `properties` (top-level; `schemaVersion` stays 1):

```json
"chains": {
  "type": "object",
  "description": "Module-relative pointer chain for chained fields (position family). offsets.<field> stays 0 for chained fields by design: the runtime observation path computes moduleBase + offset, which cannot represent a chain, and the ring record itself is battle-scoped heap. The resolver reads chained fields via its own hash-bound layout.",
  "propertyNames": { "enum": ["replayTime", "playerHP", "playerPositionX", "playerPositionY", "playerPositionZ", "playerYaw", "cameraPitch", "aliveTankCount"] },
  "additionalProperties": {
    "type": "array",
    "minItems": 1,
    "items": {
      "type": "object",
      "required": ["kind", "value"],
      "properties": {
        "kind": { "type": "string", "enum": ["rootRva", "memberOffset", "recordOffset"] },
        "value": { "type": "integer", "minimum": 0, "maximum": 2147483647 },
        "note": { "type": ["string", "null"] }
      },
      "additionalProperties": false
    }
  }
}
```

Also extend the `offsets.playerPositionX/Y/Z` descriptions:
"Chained field: see `chains`; the integer offset stays 0 (the position is
reached through the module-relative chain, not a direct base-relative
offset)."

## 2. `memory-offsets/11.19.0.10.json` — the publication edit

- `confidence`: `"none"` → `"high"` (verified across multiple sessions and
  restarts — 4 positive runs, 5 fresh launches, 2 distinct replays).
- `discoveredAtUtc`: update to the apply time (ISO 8601).
- `notes`: append the G0 verification summary (exe hash exact, RVA chain
  verified, OD-RECOVERY-082).
- `offsets`: **unchanged** (all 0 — runtime-safe by design; see §0).
- `fieldValidation.playerPositionX/Y/Z` → the target state below (X shown;
  Y = record `+0x14`, Z = record `+0x18`). **`playerPositionY` gets a NEW
  entry** (today the offsets dict has it but `fieldValidation` does not).
- New top-level `chains` section (below).
- `playerYaw`, `replayTime`, `playerHP`, `cameraPitch`, `aliveTankCount`:
  untouched.

### 2a. Target `fieldValidation.playerPositionX` (Y/Z same shape, offsets +0x14/+0x18)

**Append, do not replace:** the target keeps the existing OD-004…011 scan
history (7 evidence entries documenting the non-promoted candidate paths)
and ADDS the three verification entries below. Second-opinion pass
(2026-08-09) corrected the draft: a replace would discard valuable
negative-scan provenance. `independentProcessLaunches` is 4 (OD-075 Dead
Rail + OD-076/081/082 Oasis Palms — four fresh processes with positive
polls), `independentReplays` 2 (the two content-distinct replays).

```json
"playerPositionX": {
  "status": "Verified",
  "evidence": [
    << existing OD-004..011 entries (7), unchanged >>

    {
      "provenanceKind": "StaticAnalysis",
      "sourceTool": "Ghidra 12.1.2 (hash-bound 1cda5c31...) + Type10EntityPositionResolver layout",
      "notes": "OD-RECOVERY-075/082: module-rooted position-ring chain verified hop-by-hop against Type10EntityPositionResolver (GameCore 0x04095C88 -> app 0x0C -> session 0x124 -> account 0x118 -> playback 0x128 -> connection 0x120 -> BWEntities 0x04 -> [conditional cache 0x48 | tree roots 0x1C/0x40/0x34] -> filter 0x38 -> avatar helper 0x08 -> ring 0x08/0x1C8 stride 0x38 -> position record +0x10). Identity vtable RVAs (module-relative): app 0x0323D61C, session 0x0323D9BC, account 0x0323EAE4, playback 0x03253AA4, filter [0x0325654C, 0x032565AC, 0x03442520], helper [0x0325656C, 0x0325658C, 0x034424A4]. Chained field: offsets value stays 0; see 'chains'."
    },
    {
      "provenanceKind": "GameHarness",
      "sourceTool": "WotBTreader.Host.Web gated discover/entity-position (loopback)",
      "notes": "24/24 stable-resolver-positive polls with allConsistentDoubleRead=true across OD-075 (Dead Rail), OD-076 (Oasis Palms), OD-081/082 (Oasis Palms live, un-armed corrected procedure); resolver reads the position triple through the verified chain."
    },
    {
      "provenanceKind": "StaticAnalysis",
      "sourceTool": "OD-RECOVERY-080 root-cause (interceptor PAGE_GUARD corrupts poll reads)",
      "notes": "The write-observation mechanism was abandoned: arming the ring-record page fails the poll's own reads (ERROR_PARTIAL_COPY 299). The G1 acceptance is the poll's per-read byte-identical double-read branch (allConsistentDoubleRead)."
    }
  ],
  "independentProcessLaunches": 4,
  "independentReplays": 2,
  "harnessInvariantsPassed": true,
  "leadApproved": false,
  "decoderAuditorApproved": false
}
```

(`leadApproved` / `decoderAuditorApproved` are set by the operator at apply
time; the schema's `Verified` `allOf` requires them present, and the fields
are already present with `false`.)

### 2b. New `chains` section

The chain is the resolver's dereference path (verified 2026-08-09 against
`Type10EntityPositionResolver.TryResolveOnce`/`FindEntity`). The walk is
linear through the controllers, then **branches** at the entities map:
`CachedEntityOffset 0x48` is a CONDITIONAL fast path (used only when its id
matches), and the three `EntityTreeObjectOffsets` are ALTERNATIVE map roots
tried in order (primary → tertiary → secondary). The notes carry that
semantics; the vtable identity RVAs (validation constants, not dereference
hops) are listed in the fieldValidation StaticAnalysis evidence.

```json
"chains": {
  "playerPositionX": [
    { "kind": "rootRva",   "value": 67722376, "note": "GameCoreRootRva 0x04095C88 (module base + RVA -> GameCore pointer)" },
    { "kind": "memberOffset", "value": 12,    "note": "GameCoreAppControllerOffset 0x0C (vtable-validated: 0x0323D61C)" },
    { "kind": "memberOffset", "value": 292,   "note": "AppControllerSessionControllerOffset 0x124 (vtable: 0x0323D9BC)" },
    { "kind": "memberOffset", "value": 280,   "note": "SessionControllerAccountControllerOffset 0x118 (vtable: 0x0323EAE4)" },
    { "kind": "memberOffset", "value": 296,   "note": "AccountControllerActiveControllerOffset 0x128 (vtable: 0x03253AA4)" },
    { "kind": "memberOffset", "value": 288,   "note": "PlaybackControllerConnectionOffset 0x120" },
    { "kind": "memberOffset", "value": 4,     "note": "ConnectionEntitiesOffset 0x04 -> BWEntities map" },
    { "kind": "memberOffset", "value": 72,    "note": "CachedEntityOffset 0x48 - CONDITIONAL fast path (used only when its entity id matches; otherwise fall through)" },
    { "kind": "memberOffset", "value": 28,    "note": "EntityTreeObjectOffsets[0] 0x1C - ALTERNATIVE map root 1 (primary)" },
    { "kind": "memberOffset", "value": 64,    "note": "EntityTreeObjectOffsets[1] 0x40 - ALTERNATIVE map root 2 (tertiary)" },
    { "kind": "memberOffset", "value": 52,    "note": "EntityTreeObjectOffsets[2] 0x34 - ALTERNATIVE map root 3 (secondary)" },
    { "kind": "memberOffset", "value": 56,    "note": "EntityMovementFilterOffset 0x38 (vtable subtype: [0x0325654C, 0x032565AC, 0x03442520])" },
    { "kind": "memberOffset", "value": 8,     "note": "AvatarFilterHelperOffset 0x08 (helper vtable matches the filter subtype: [0x0325656C, 0x0325658C, 0x034424A4])" },
    { "kind": "memberOffset", "value": 8,     "note": "AvatarHelperRingOffset 0x08 (eight-entry ring, stride 0x38; record = helper + ring + index*stride)" },
    { "kind": "memberOffset", "value": 456,   "note": "AvatarHelperCurrentIndexOffset 0x1C8 (stable before/middle/after the double-read)" },
    { "kind": "recordOffset", "value": 16,    "note": "PositionRecordOffset 0x10 (float32 X; Y at +0x14, Z at +0x18; read consecutively - TryExtractPosition reads x, x+4, x+8)" }
  ]
}
```

Y and Z share the same chain with `recordOffset` 0x14 / 0x18 respectively —
the published JSON carries three chain entries, `playerPositionX` (above),
`playerPositionY` (same hops, final hop `{ "kind": "recordOffset", "value":
20, "note": "PositionRecordOffset 0x14 (float32 Y)" }`), and
`playerPositionZ` (final hop `{ "kind": "recordOffset", "value": 24,
"note": "PositionRecordOffset 0x18 (float32 Z)" }`).

## 3. `offline/memory-offsets.md` — document `chains`

Add a short section to the pack doc: the `chains` object (field → array of
`{kind: rootRva|memberOffset|recordOffset, value, note}` hops), why chained
fields keep `offsets` 0 (runtime reads `moduleBase + offset`; the record is
battle-scoped heap), and that the position family (playerPositionX/Y/Z) is
`Verified` via the module-rooted chain. Keep the confidence-levels table and
the field list unchanged (the offline_check cross-check requires them
consistent).

## 4. `scripts/python/offset_check.py` — validate `chains`

Extend `validate_offset_file` (defense-in-depth; the gate does not run real
JSON-Schema validation today):

- For every field present in `chains`: the corresponding `offsets` value
  must be 0 (fail if non-zero — a chained field must not be read as a
  direct offset), and the chain must be a non-empty array of hops with
  `kind ∈ {rootRva, memberOffset, recordOffset}` and non-negative `value`.
- Keep the existing structural checks untouched (they already pass with the
  current table: schemaVersion 1, hex SHA present once confidence != none,
  offsets all 0).

## 5. Post-edit gates (after the operator approves)

1. `python scripts/python/offset_check.py --check-schema` — PASS expected.
2. `tools/report-offset-evidence.ps1 -GameVersion 11.19.0.10` — runs clean
   (note: the report counts non-zero `offsets` as "known", so the chained
   position fields still report offset 0 / unknown there; the verification
   lives in `fieldValidation` + `chains`).
3. Regenerate `offline/file-tree.md` (`python scripts/python/offline_check.py
   --refresh`).
4. `scripts/validate.ps1` — full gate (build identity validation against the
   table must not regress: `OffsetTableReader` loads the table keyed by
   version + SHA; the additive `chains` key is ignored by System.Text.Json,
   schemaVersion stays 1).
5. One commit: table + `chains` + schema.json + pack doc + validator +
   ledger row (`numericOffsetPublication: true`) + handoff.

## 6. What the promotion does NOT do

- Does NOT set a non-zero `offsets` value for the position fields (runtime
  safety, §0).
- Does NOT promote `playerYaw` (stays Stale/Quarantined), velocity
  (`+0x28` — the poll reads the position only), `replayTime`, `playerHP`,
  `cameraPitch`, or `aliveTankCount`.
- Does NOT publish any absolute/heap address — the chain is module-relative;
  the ring record is resolved at runtime by the resolver.
