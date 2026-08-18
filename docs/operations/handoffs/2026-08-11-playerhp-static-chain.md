# 2026-08-11 — playerHP static chain: HP is int16 at [entity+0xB8]

## Summary

The playerHP discovery target's static side is now **pinned and
hash-bound**: current health is a **signed int16 at `[entity+0xB8]`** on the
**entity base record**, with the alive byte at `[entity+0xBA]` and the
healing int16 at `[entity+0x11E]`. This **refutes the L1 plan's prior
expectation** (an int32 HP inside the tank record at `[entity+0x3C]`) and
required a small, well-tested product change: the correlator gained an
int16 candidate pass and the region-read seam gained an `entity-base`
anchor. The live HP session is now decisions-only, with the exact field
location known before the first dump.

## Follow-up same day: the full health block (16/16 verifier)

Extending the same setter family (the string-anchored `VehicleGameLogic::set_*`
setters from `replay-entity-bridges.txt`) pinned the whole health block on
the entity base record:

| Offset | Width | Field | Setter evidence |
|---|---|---|---|
| `+0x7E` | int16 | gun angles packed (2 × 6-bit) | `set_gunAnglesPacked` `FUN_016ee230`: `MOVZX ECX,word ptr [EAX+0x7e]` then `&0x3f` / `>>6` |
| `+0xB8` | int16 | current health | `set_health` `FUN_016ee450` |
| `+0xBA` | byte | alive flag | `set_isAlive` `FUN_016ee990`: `CMP byte ptr [EAX+0xba],0x0` + `CMP word ptr [EAX+0xb8],0x0` |
| `+0x11C` | int16 | **max health** | `set_maxHealth` `FUN_016eeb70`: `MOVSX EDI,word ptr [EAX+0x11c]` |
| `+0x11E` | int16 | healing health | `set_healingHealth` `FUN_016ee350` |

The state-sync writer `FUN_0166b9f0` stores every one of these (word→
`+0x7E`/`+0xB8`/`+0x11C`/`+0x11E`, byte→`+0xBA`), so the block is
read/write-consistent. **`VerifyPlayerHpChain` is now 16/16 checks.** The
max-health field at `+0x11C` is the piece the overlay's HP fraction needs
(it currently defaults to 1.0 when the tank took no damage because exact max
HP is not in the decoded data) — the L1 live session confirms it, and the
overlay can then render true HP fractions per tank.

## What was done (all offline, hash-bound)

### 1. The listener trail led to the wrong class, then to the right one

- `FindVftableRefs` + `ResolveVftableClass` traced the single .text
  reference to the `AvatarHealthListener` vftable (0x32daad0): it is
  installed at `[+0x178]` by the **`BattleUILayer` ctor** (`FUN_01da0d40`),
  a HUD UI layer (bases incl. `VehicleDamageListener`, `AvatarHealthListener`).
  The listener route is UI-side and generic (`ListenerHolder` notify has 841
  callers) — a dead end for the memory location.
- The `replay-entity-bridges.txt` string anchors (already in evidence)
  pointed to the real owner: **`VehicleGameLogic::set_health`**
  (`FUN_016ee450`, RVA 0x12ee450) and `set_healingHealth`
  (`FUN_016ee350`).

### 2. The read side: set_health / set_healingHealth

`set_health` reads the OLD value through the entity getter (VehicleGameLogic
vftable slot 1 = `0x31b560`, byte-verified `MOV EAX,[ECX+0x4]; RET`):

```
MOV EAX,[EDI]            ; this->vftable
CALL [EAX+0x4]           ; slot 1 = entity getter -> EAX = [this+0x4] = the entity
MOVSX EDI,word ptr [EAX+0xb8]   ; old health = (int16)[entity + 0xB8]
```

`set_healingHealth` reads `(int16)[entity+0x11e]` the same way. The
decompiled tail also shows the death check:
`*(short *)(entity+0xb8) < 1 → destroy path`, and `[entity+0xba]` as a byte
flag. **HP is int16 — not int32.**

### 3. The write side: state-sync + diff-notify pair

- `FUN_0166b9f0` (RVA 0x126b9f0) — the state-sync writer: copies a virtual
  property source into the record, storing `int16 → +0xB8` (health),
  `byte → +0xBA` (alive), `int16 → +0x11E` (healing).
- `FUN_01675f60` (RVA 0x1275f60) — the diff-notify twin: reads old/new via
  the same property getters and dispatches property-changed listeners
  (vtable +0x68) when a field differs — including +0xB8 and +0x11E.
- A 16-bit-store scan (`FindHealthFieldStores.java`) found 11 stores to
  `+0xB8`; the remaining BW-region ones are ctors copying a template default
  (`MOV AX,[0x04479978]`), consistent with DAVA object-template init.

### 4. Verifier

`VerifyPlayerHpChain.java` — hash-bound, **16/16 checks pass, verdict
`player-hp-chain-verified`** on sha256 `1cda5c31…1760307d`: vftable slot 1
target + getter bytes (`8b 41 04 c3`), the health/healing/maxHealth/alive/
gun-angles setter reads, both writers' stores, and the listener dispatch
all byte-verified. Run log has zero `SCRIPT ERROR`/`error:` lines, report
fresh.

## Why this changes the L1 plan

The plan anchored HP dumps at the **tank record** `[entity+0x3C]` and
correlated **int32** fields (`+0x48` was a synthetic fixture). The static
chain shows HP is **int16 at `[entity+0xB8]`** — 0x7C bytes past the
transform pointer, on the **entity base record**. An int32-only scan of the
tank record could never find it: the int32 at +0xB8 folds health + alive
byte + padding into a value whose drops don't match damage sums (and the
destroy window's alive flip makes it fail Strict).

## Product changes (green)

- **`HpDamageCorrelator`**: new `includeInt16Candidates` parameter — a
  2-byte-aligned int16 pass alongside the int32 pass. 4 new core tests:
  int32-only never emits int16 candidates; the int16 HP ranks first under
  Strict (destroy-window alive flip discriminates against the coincidental
  int32 read at the same offset); a magnitude-mismatched int16 decoy is
  excluded; the increment/damage-dealt int32 counter still ranks first with
  the pass enabled.
- **`EntityRecordRegionAnchor.EntityBase`** (new value 2): the seam reads
  the region directly at the resolved entity base — no pointer deref.
  Coordinator switch, web parser (`entity-base`), ApiContracts doc, driver
  `ValidateSet` all updated. 1 coordinator test + 1 web endpoint test.
- **`hp-diff --int16 <bool>`** (CLI option; default true for the
  HP/decrement direction, false for increment/damage-dealt), emitted
  automatically by `invoke-hp-diffing-session.ps1` and the extractor's
  `--hp-delta` command template.
- **`invoke-hp-diffing-session.ps1`**: defaults `-RegionAnchor entity-base`
  and `-RegionLength 320` (≥ 0x120 covers +0x11E healing).

Docs updated: `record-diffing-groundwork.md` (anchor correction + fixture
note superseded), `product-roadmap.md` (L0/L1 rows).

## Next step (approval-gated)

The L1 live session: qualify the victim (savanna 3760578 / medvedkovo
2549399), dump the **entity-base** region at the event-bound schedule,
`hp-diff --int16 true` → expect the verdict HIT at **+0xB8 int16** on both
replays, then publish the offset + chain through the operator gate.
