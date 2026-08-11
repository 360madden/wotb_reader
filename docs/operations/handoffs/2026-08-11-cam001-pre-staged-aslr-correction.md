# CAM-001 pre-staged + ASLR correction for the camera anchor

Date: 2026-08-11. Binary: wotblitz.exe 11.19.0.10 (hash `1cda5c31…1760307d`).
Static-only; nothing promoted, resolver/read surface untouched.

## What changed

1. **Pre-staged the offline verification session** —
   `scripts/invoke-camera-state-verify.ps1` (CAM-001). It implements the
   plan in `record-diffing-groundwork.md` end-to-end, read-only, against the
   server-owned endpoints:
   - waits for the `OfflineReplayVerified` gate (bounded);
   - binds ground truth to the launch artifact and the single viewpoint
     entity (same binding as od-073);
   - locates the avatar controller by vftable-dword scan, walks
     `[avatar+0x154] → [br+0x2C] → [cam+0x28]`, reads yaw/pitch, the
     `+0xAC` basis rows, and the `+0x11C` position triple;
   - correlates camera yaw vs the decoded frame camera yaw at the same
     replay time (expect ~1:1) and the camera position vs the nearest
     trajectory sample (offset norm 1-30 m = third-person offset);
   - writes a privacy-safe aggregate
     `wotbtreader.cam001.camera-state-verify.v1` (booleans, counts, one yaw
     delta, one offset norm — no coordinates, addresses, raw bytes,
     entity ids, or capability values).

2. **ASLR correction (new static fact).** Parsed the PE headers of
   `C:\Games\World_of_Tanks_Blitz\wotblitz.exe`:
   `ImageBase=0x400000`, `DllCharacteristics=0x8140` → **DYNAMIC_BASE is
   set, so ASLR is enabled**. The Ghidra "abs" vftable values
   (`0x3677e8c`/`0x3677da4`) are preferred-base absolutes (= 0x400000 +
   RVA), so the runtime vftable pointer is `(runtime module base + RVA)`,
   not a fixed constant. The session script therefore learns the runtime
   module base from the pattern-scan response's `baseAddress` (reported
   even with zero candidates) and rescans for `base + 0x3277e8c`
   (replay) / `base + 0x3277da4` (live), little-endian. RVAs are confirmed:
   `0x3277e8c` replay / `0x3277da4` live.

3. **Endianness + PS 5.1 fixes.** `observedValueHex` from
   `/discover/read` is memory-order raw bytes — chain pointers are decoded
   with `BitConverter.ToUInt32(FromHexString(...), 0)`, not
   `Convert.ToUInt32(hex, 16)` (which would parse big-endian and corrupt
   every hop). `[double]::IsFinite` is .NET Core 3.0+ only, so a
   `Test-Finite` helper replaces it (the script still runs under Windows
   PowerShell 5.1). Frame correlation uses the real query param
   (`timeSeconds`, verified against `ReadApiEndpoints.cs`).

## Evidence trail

- PE header dump (this session): machine 0x14c, 6 sections, magic 0x10b,
  ImageBase 0x400000, DllCharacteristics 0x8140 (ASLR enabled).
- Route check: `GET /api/v1/sessions/{battleSessionId:guid}/frame?timeSeconds=`
  (`ReadApiEndpoints.cs` line 41) returns `cameraYawRadians` /
  `cameraPitchRadians` via the overlay projector.
- Contract check: `/discover/pattern` accepts `fieldName`,
  `expectedValueHex`, `maxCandidates`, `minRegionSize`, `alignment`
  (pattern length 1-64, alignment ∈ {1,2,4,8}); `/discover/read` accepts
  `addresses` (hex strings), `valueKind` (Float/UInt32/...), `valueSize`
  matching the kind; responses carry `absoluteAddress`,
  `observedValueHex` (memory-order), `readOk`, and `baseAddress`.
- Script hygiene: `invoke-scriptanalyzer.ps1` gate passed.

## Files touched

- `scripts/invoke-camera-state-verify.ps1` (new pre-staged session script)
- `docs/operations/record-diffing-groundwork.md` (plan: base-relative
  vftable scan + CAM-001 procedure)

## FOV / projection static hunt — time-boxed result (2026-08-11)

A further static pass chased the projection FOV (the overlay's last W2S
unknown). Findings:

- `FlexFOVCamera` class exists: RTTI name `. ?AVFlexFOVCamera@@` at file
  0x3e4d060 (RVA `0x3e4e460`). Tuning-property keys cluster near the
  camera vftables: `default fov`/`min fov`/`max fov`/`camo fov`/
  `showcase fov` at RVA `0x32de268..0x32de439`; `Movement FOV offset
  (deg)`/`Movement FOV multiplier` at RVA `0x326c4f1` (ReplayCameraController
  region); `cam.fov`/`cc.fov` property names near the DAVA::Camera region.
- Conclusion: FOV is a **config/res-driven runtime quantity** (values in
  the game's DAVAProject settings), not a constant in the exe — the
  numeric per-mode FOV cannot be statically extracted. It is measured
  in-session (fit the projection of a known-world tank to its screen
  position) or read from the live camera's FOV field in a later session.
- RTTI note: this binary's RTTI is a modified layout — per-class .data
  "name" slots share one type-info pointer (RVA `0x037f3054`), COL
  `pSelf` is zero, and reverse name→vftable resolution is unreliable;
  forward COL→vftable resolution from a known vftable (the method used
  for the camera family) remains valid. Recorded so future sessions do
  not re-spend the hunt.

## Next steps

- Approved offline session: one replay launch, then
  `pwsh -File scripts/invoke-camera-state-verify.ps1 -WaitVerifiedSeconds 240`.
  Positive `camera-state-consistent` → wire the true camera into
  `ReplayFrameSource.BuildCamera` (integration design staged in
  `record-diffing-groundwork.md`); the `+0xAC` basis cross-check
  (recomputed yaw×pitch) is the follow-up session.
- FOV: measured in-session (or from the live camera object later) — the
  overlay's `verticalFovRadians` stays the tunable parameter until then.
- The SessionController's process-global anchor (the last hop before a
  fixed chain) is still a targeted-scan item; the vftable scan used here
  is the approved session anchor.
