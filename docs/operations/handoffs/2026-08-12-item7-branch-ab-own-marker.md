# 2026-08-12 — item-7 Branch A COMPLETE + Branch B step 1; own-tank edge marker shipped

Binary: wotblitz.exe 11.19.0.10 (hash `1cda5c31…1760307d`). Replays: savanna
Palms + medvedkovo. Read-only; resolver, read surface, and
`memory-offsets/11.19.0.10.json` untouched (HP/yaw publications remain
operator-gated). Evidence: ledger rows, item-7 plan
`docs/operations/item7-hardware-atomicity-proof-plan.md` (Branch A
COMPLETE, Branch B step 1 DONE), hash-bound Ghidra evidence in
`.build/ghidra-evidence-hp-writesite/` + `.build/ghidra-evidence-ring-writer/`
(local, gitignored). Commits: `7c41bb3` (code + records); the three
2026-08-11 handoffs (CAM-013, HP Phase-4/OD-091, live-frame X4-E2E) and
this handoff land together in the handoff commit.

## Item-7 Branch A — COMPLETE for every consumed field (offline static)

### HP write-site census (`+0xB8` current HP / `+0x11E` healing)

Hash-bound (`1cda5c31…`) listing-confirmed census across the whole binary:
**426 real instruction stores** — 360 dword + 13 word + 53 byte to `+0xB8`,
2 word + 1 byte to `+0x11E` — and **zero 64-bit or 128-bit stores to either
field anywhere**. The health setters `FUN_0166b9f0` and `FUN_01675f60`
write BOTH fields as single `MOV word ptr [reg+0xb8/0x11e], AX` — aligned
16-bit stores, atomic within a cache line on x86, so the resolver's
concurrent int16 read cannot tear. The 360 dword / 53 byte stores target
other object families (immediates like `0x3f800000` = 1.0f, `0x1388` =
5000 are not HP semantics); the residual vehicle-targeting risk is bounded
by the OD-087/091 live byte-exact reads (132 dumps, every drop equals its
damage sum, zero torn values) — stated honestly in the plan.

**Methodology lesson (locked in the ledger's do-not-repeat list):** the
first pass used an unaligned byte-walker that scanned *through* instruction
interiors — random bytes parsed as fake stores (a bogus 360 "dword" census,
and even the pre-existing `FindHealthFieldStores.java`'s 13 sites needed
confirmation). The honest chain: `FindHealthFieldStores.java` (raw-byte
candidates) → `ScanHealthFieldStoreWidths.java` (width-complete census,
66-prefix XMM handled) → `ConfirmHealthFieldStores.java` (each candidate
checked at its REAL instruction boundary in the analyzed listing, image
base subtracted). Two address bugs in my own confirmation script were found
and fixed (opcode position vs instruction start; missing image-base
subtraction) before the verdict was trusted.

### Position + rotation ring-writer reconciliation (`FUN_0270df40`)

The ring writer is chain-anchored on the 40-check semantic chain
(`type10-movement-position-trace.txt`: type-10 handler → engine → entity
resolver → apply → movement filter → avatar filter apply → avatar-helper
vtable slot 2 `0x230DF40`). Full-body disassembly reconciles every consumed
field (record = `helper + 0x08 + slot*0x38`; `RingRecordSize = 0x38`
enforced by the layout check; index `helper+0x1C8` masked `&7`,
slot = `(index−1)&7`):

| Consumed field | Store | Width |
|---|---|---|
| x,y at +0x10/+0x14 | `MOVQ [ECX],XMM0` (0x270DFD7) | **one 8-byte aligned store** |
| z at +0x18 | `MOV [ECX+8],EAX` (0x270DFDE) | 4-byte |
| roll,pitch at +0x28/+0x2C | `MOVQ [ESI+EDX*8+0x30],XMM0` (0x270DFFC) | **one 8-byte aligned store** |
| yaw at +0x30 | `MOV [ESI+EDX*8+0x38],EAX` (0x270E005) | 4-byte |

All aligned → atomic within a cache line → the resolver's float32 reads
cannot tear. This independently confirms the live-verified layout (the
slot-4 readback `0x230DBE1` reads the same `record+0x10`). The handoff
story is now precise: the index advances BEFORE the fill (0x270DFAA)
behind a monotonic-time stale guard (0x270DF95), so a reader can only catch
a half-filled slot inside the sub-microsecond write window — the resolver's
double-read discipline retries over it, zero tears live. The plan's
"writes slot then advances" assumption is corrected to advance-then-fill.

**Sequencing amendment:** Branch A is static proof and touches no live
surface — it ran ahead of the operator-gated publication applies (which the
plan previously staged before item-7 work). Branch B's live half still
comes after the applies.

## Item-7 Branch B step 1 — DONE (offline half)

The batch surface's Phase-2 reads were single-shot; each **region span and
each entity-base span is now read TWICE per attempt**, requiring
byte-identical reads (`SequenceEqual` — the ring record's leading time
field sits inside the span, so any ring advance or mid-write changes the
bytes and retries; the stability witness), with a bounded retry
(`layout.MaxAttempts` = 3) and **fail-closed exhaustion**:
`region-unstable-snapshot` / `entity-base-unstable-snapshot` — an item that
never settles fails only itself; the batch stays resolved, never a silent
single read. `ConsistentDoubleRead` still travels false (no overlay
consumer — verified) — the flag flip and per-entity span measurement fields
remain the owner-gated shared-contract proposal (Branch B step 2).

Tests: 2 new — `RegionTearRetriesAndSucceeds` (torn first read → stable
re-read wins) and `RegionAlwaysTornFailsRegionOnly` (never settles across 3
attempts → item fails closed, batch resolved) — plus the three existing
batch tests' read-count/order assertions updated to document the
double-read. The design doc's "region dumps do not double-collect" note is
superseded.

## Own-tank edge marker — shipped (the other half of the honest self marker)

`OwnEntityId` suppression (live-verified on both launches, exact joins +
real HP) was half of the name-join design's "self marker"; this session
shipped the other half: when the own tank projects **off-viewport** (the
chase eye's hull lands below the rect, as in the real captures), the HUD
draws a **clamped edge chevron** pointing back at the hull.

- `OwnMarkerItem` + `OwnMarkerMath` (pure clamp/angle, `Margin = 28`,
  fail-closed on degenerate viewports), ViewModel `OwnMarkers` collection,
  `W2sHudView.BuildOwnMarker` chevron, `MainWindow` plumbing.
- **8 new tests** (5 math + 3 ViewModel: off-viewport marker, on-viewport
  omission, replay/unknown-id omission).
- The full gate caught a **fixture inconsistency in my own test**: I copied
  the capture's 640x360 numbers (`screenY 500, inViewport false`) but
  called `RefreshOverlayFrameAsync(1920, 1080)`, where 500 is *inside* the
  rect — the code was right (an in-rect point is left untouched by the
  clamp), the fixture was wrong. Fixed by mirroring the real 640x360
  capture viewport; all 110 Overlay tests pass.

## Docs consistency fixes (verified against sources before acting)

- `resolver-path-consolidation.md`: the item-5 L3 row re-presented the
  synthetic `+0x48` rehearsal as "HIT" without the OD-RECOVERY-090 live
  honest-negative that superseded it (fixed + footnote — same trap class as
  the yaw `+0x2C`); the item-6 X3 bullet claimed OD-RECOVERY-086 "still
  needs an approved live launch" — it ran (fixed).
- `product-roadmap.md`: the V1 row's "once the yaw offset is discovered"
  condition — yaw is discovered, live-verified, and the live frame already
  carries `ScreenHeadingDegrees` (fixed); the X2b row gained its X4-E2E
  supersession pointer.
- The 08-11-era records also land in this push: CAM-013 aim-point handoff,
  HP Phase-4 (OD-091) handoff + lead-side matcher, live-frame X4-E2E
  handoff, playerHP entity-base chain (g0 draft + `offset_check.py`
  fidelity check), blocker supersessions (BLK-0019/21/22), publication
  drafts (HP + yaw READY), L3 plan, ledger rows.

## Verification

Full `scripts/validate.ps1` gate: **1045 passed / 3 local opt-in skips /
0 failed**, repo scan clean, offline pack fresh (new files staged before
`--refresh`, per the known gotcha), offset schema + chains PASS. Canonical
counts updated in AGENTS.md / knowledge.md / offline pack.

## What remains (gated)

- **Operator-gated publication applies** (HP then yaw) — both packages
  READY; each lands as ONE commit with the G0 post-edit gate.
- **Branch B steps 3–4** — the live half needs approved bounded sessions.
- **Contract flag flip** (ConsistentDoubleRead + per-entity span
  measurement fields) — owner-gated shared-contract proposal.
- **L3 damage-dealt** — needs a NEW object family (avatar/player-stats),
  not the entity records; discovery plan pre-staged.
- **V4 minimap texture** — data-blocked (only savanna/medvedkovo replays and
  no usable texture in the install).

## Next planned (ledger)

Operator approval → HP publication apply → yaw publication apply → item-7
Branch B live steps → L3 avatar-family discovery.
