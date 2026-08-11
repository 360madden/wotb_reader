# 2026-08-11 — CAM-010/CAM-011: the camera truth (posA yz-swapped; eye located)

Binary: wotblitz.exe 11.19.0.10 (hash `1cda5c31…1760307d`). Replays: Oasis
Palms (sessions `019ff26b` v7b, `019ff276` v7c, `019ff28a` eye-probe).
Read-only; resolver, read surface, and `memory-offsets/11.19.0.10.json`
untouched. Full evidence: `cam010-yz-swap-position-convention.md` +
ledger `CAM-010`/`CAM-011` rows + `cam001-v7-evidence-template.md`.

## State of camera truth (as of this handoff)

| Claim | Status |
|---|---|
| Chain walk + identity gates (ReplayCameraController `base+0x326dd0c` / GameCamera `base+0x32dafa0`) | **valid** |
| GameCamera pose fields: posA `+0x38`, yaw cos/sin `+0x50/+0x54`, pitch `+0x58`, basis `+0x80..0xB0` (stride-4 3x4) | **valid** |
| posA is stored **(x, z, y)** — world Y/Z swapped | **proven** (yz-swap ⇒ 2.1–3.6 m from decoded tank on all v7b/v7c rounds, sub-meter; as-read 113–206 m = `√(dx² + 2·(tank.z − tank.y)²)`) |
| CAM-004's "23.57 m third-person offset" | **SUPERSEDED** — it was `√2·\|z − y\|` with z−y = 16.7 m at the read moment, not a chase eye |
| posA yz-swapped is the **render eye** | **proven** (CAM-011: single camera-family instance, `+0x28` ring IS the GameCamera, eye distance is state-dependent — 20.3 m battle-intro orbit / 2.1–3.6 m attached mid-battle) |
| Camera is yaw-locked to the tank | **false** — 101–177° gaps on the flipped phase (non-chase state) |
| Orientation convention (yaw/pitch/basis → world space) | **OPEN — the last camera question (CAM-012)** |

## W2S seam (what the overlay must do)

`eye = (posA.x, posA.z, posA.y)` — the yz-swap is REQUIRED. Orientation:
consume the basis rows / yaw-pitch as stored, with the world mapping
locked by CAM-012 (two-sample intro-orbit sweep: the camera yaw must
rotate with the eye→tank direction in the correct convention; or a
chase-state launch). posB `+0x44` = previous-frame interpolation (~1 m
behind posA); posC `+0xB0` is unrelated.

## Session procedure that works

1. Launcher: `scripts/launch-offline-replay-for-od.ps1 -ReplayPath
   .data/reimport-oasis.wotbreplay -WindowWaitSeconds 240`, poll the log
   for `OK OfflineReplayVerified` + the `battleSession=` anchor.
2. **The anchor scan only works while the replay is in its readable
   window** — run any probe right at the gate; a late probe finds 0
   candidates (the replay has ended). Gate-timed probes only.
3. **The launcher rotates the Host + capability mid-session** — API calls
   must re-read the rendezvous per call (a stale capability → 401).
4. Relaunching is safe: the launcher stops stale game + Host itself.
   Stop leftover `WotBTreader.Host.Web` before `validate.ps1` (it locks
   the build outputs → MSB3021).

## Next planned (ledger)

CAM-012 — lock the orientation convention via the intro-orbit two-sample
sweep. Then: OD-090 wider-region damage-dealt sweep, the HP Phase-4 rule
(Dead Rail victim 2549399), yaw publication apply (operator approval
only), item 7 (hardware atomicity) LAST. The overlay `cameraOverride`
seam can ship the yz-swap once CAM-012 lands.
