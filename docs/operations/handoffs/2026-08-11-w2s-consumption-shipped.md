# 2026-08-11 — W2S overlay consumption shipped (CAM-012 locked; OD-090 closed; item 7 staged)

Binary: wotblitz.exe 11.19.0.10 (hash `1cda5c31…1760307d`). Replays: Oasis
Palms + Dead Rail. Read-only; resolver, read surface, and
`memory-offsets/11.19.0.10.json` untouched (yaw/HP publications remain
operator-gated). Evidence: ledger rows `CAM-012` + `OD-RECOVERY-090
re-attempt` + `item7-hardware-atomicity-proof.md`; commits `b6f560c`.

## Camera / W2S truth (COMPLETE — all live-verified)

| Claim | Status |
|---|---|
| Chain walk + identity gates (ReplayCameraController `base+0x326dd0c` / GameCamera `base+0x32dafa0`) | valid |
| posA `+0x38` stored **(x, z, y)** — world Y/Z swapped | proven (yz-swap ⇒ 2.1–3.6 m from decoded tank, sub-meter, on v7b/v7c) |
| posA yz-swapped IS the render eye | proven (CAM-011: single camera-family instance; ring `+0x28` IS the GameCamera; eye distance state-dependent: 20.0–20.3 m intro orbit / ~2 m attached) |
| **Orientation convention** | **LOCKED (CAM-012)**: basis rows are the camera's world axes — **forward = −row1, up = row2**; camera aims at the tank's TURRET-LEVEL point (look-at 0.4–6.7°, avg 1.7°, session `019ff290`) |
| CAM-004's "23.57 m third-person offset" | SUPERSEDED — `√2·|z−y|` artifact (z−y = 16.7 m at that read moment) |
| Memory yaw/pitch fields (`+0x50/+0x54/+0x58`) | DAVA left-handed, NOT the packet convention — no sign combo reproduces the aim direction; the basis is authoritative |

## W2S overlay consumption — SHIPPED (commit `b6f560c`, gate green)

`LiveFrameProjector.BuildCamera` (the CAM-001 cameraOverride seam) now
consumes the verified pose correctly:

- **eye = yz-swap(posA) = (X, Z, Y)** — mandatory (CAM-010).
- **Orientation derived from the basis**: forward = −row1 → packet
  yaw/pitch (`fy = sin pitch`, `yaw 0 → +Z`, `+π/2 → +X`); raw yaw/pitch
  is the documented legacy fallback only.
- **Coordinator bug fixed**: the view-basis region (`+0x80..0xB0`) is a
  row-major **stride-4 3×4** matrix, but the code read 9 *contiguous*
  floats. It now reads all 12 and compacts row0/row1/row2 (pads at
  `+0x8C/+0x9C/+0xAC` dropped). Verified: C# math reproduces the CAM-012
  look-at (0.56° vs the real aim point); the Python validator mirrors the
  seam (`_orientation_from_basis`, both 12-float stride-4 and 9-float
  compacted layouts); real v7c aggregate still honest `non-chase`.
- Tests: `LiveFrameProjectorTests` (+3: yz-swap, basis orientation,
  legacy fallback), `GameSessionCoordinatorTests` stride-4 fixture.
  Full `scripts/validate.ps1` green (1013+ tests).

## OD-RECOVERY-090 re-attempt — CLOSED honest-negative (3 sweeps)

Damage-dealt is NOT reachable via the entity records. Three independent
sweeps, all negative:

1. 320-byte entity-base (session `019ff250`): top candidate `+0x3C`
   demoted (flatness 0.091 — the known position-copy float).
2. 4096-byte entity-base (`019ff250` wide): candidate `+0x7EC` demoted
   (moving-float decoy).
3. Sibling `entity-tank-record` anchor (sessions `019ff2ab` +
   `019ff2b0`): **live-verified dead** — `[entity+0x3C]` is not a stable
   pointer (`+0x3C` = `0x36CE3AE8` at 32.8 s, `0xC36046AA` at 59.8 s);
   the coordinator's `tank_record_unresolved` is correct.

Further damage-dealt discovery would need a NEW object family
(avatar/player stats object), not the entity records.

## Item 7 (hardware atomicity) — DESIGNED, staged LAST

`docs/operations/item7-hardware-atomicity-proof.md`: Step A measure the
batch read windows (one live batch-rehearsal session, `Measurement`
contract already ships), Step B offline torn-read analysis, Step C the
atomicity-free consumer contract. Not started by design.

## Yaw publication — READY, operator approval only

Re-verified the dry-run stays clean: `offset_check.py` validates 4 chains
(3 published + `playerYaw` pre-staged with the identical 12-hop walk,
final hop `recordOffset 48`). Applying = table edit + evidence only
(`docs/operations/g1-yaw-publication-draft.md`). NOT applied — operator
gate.

## HP Phase-4 — BLOCKED on one relaunch (parameter, not bug)

Dead Rail session `019ff2be` launched, gate OK, qualified (victim
2549399, 5 hit windows / 520 dmg / 19 dump targets), but the **30 s
control probe returned `EntityNotFound`** and the driver failed closed
(correctly — the victim is not resolvable that early in this replay).
Next attempt: use later control times (e.g. `60,220`) or derive them from
the qualified hit windows.

## Session procedure that works (unchanged)

1. `scripts/launch-offline-replay-for-od.ps1 -ReplayPath
   .data/reimport-<replay>.wotbreplay`, poll for `OK
   OfflineReplayVerified` + `battleSession=` anchor.
2. **Gate-timed probes only** — the anchor scan dies when the replay ends.
3. **Re-read the rendezvous per API call** — the launcher rotates the Host
   capability mid-session (stale → 401).
4. Stop leftover `WotBTreader.Host.Web` before `validate.ps1` (MSB3021).
5. `-DataRoot` from bash must use `$LOCALAPPDATA` (a PowerShell
   `$env:LOCALAPPDATA` inside a bash double-quoted string expands to
   empty and breaks the qualification DB path).

## Next planned (ledger)

HP Phase-4 relaunch (Dead Rail victim 2549399, later control times) →
yaw publication apply (operator approval) → item 7 (LAST). The overlay
can now ship the W2S consumption end-to-end; the CAM-007 screen
cross-check validates it at ship time.
