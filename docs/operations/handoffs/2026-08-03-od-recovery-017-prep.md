# Session handoff — 2026-08-03: OD-RECOVERY-017 prep (tooling + tool-state correction)

**Author:** Codex Agent

**Branch:** `main` (head `e9b2bd9`; working tree clean at write time)

## Outcome

OD-RECOVERY-017 prep is committed. The two gaps that ended OD-RECOVERY-016
(no pre-armed debugger, no rolling driver before the 120s research lease
expired) now have repo tooling, and the OD-016 "CE not found" observation is
corrected in the ledger.

### Machine-state corrections (registry-backed probe, 2026-08-03)

- **Cheat Engine 7.7 IS installed** at
  `C:\Program Files\Cheat Engine\cheatengine-x86_64.exe` (installer root is
  `Cheat Engine`, not `Cheat Engine 7.7`). OD-RECOVERY-016's
  `defaultInstallPathFound: false` was a probe-path miss, not absence —
  recorded as an append-only ledger amendment.
- **x64dbg IS installed** at `C:\work\tools\x64dbg\release\x64\x64dbg.exe`
  (matches `docs/operations/offset-discovery-guide.md`).
- Replay folder still holds exactly one content unit (Churchill sha12
  `0FAE5612491E`); **BLK-0019 (content-distinct second replay) remains open**.
- A stale `EvidenceStale` Host.Web was running on 9182 (PID 14544) at probe
  time; the canonical launch helper stops/restarts it with the research lease.

### New files

| File | Purpose |
|---|---|
| `scripts/pre-arm-debugger.ps1` | Registry-backed CE 7.7 / x64dbg discovery; `-AutoAttach` launches the debugger attached to `wotblitz.exe`; writes `%TEMP%\od-prearmed-debugger.json`. `-PreferX64Dbg` switches the default (CE first). |
| `scripts/roll-replay-time-increased.ps1` | Rolling replayTime Double `increased` campaign: Double snapshot (8-byte aligned) → per-round compare with `rollingBaseline=true` via Host.Web `/api/v1/game/discover`; aggregate counts only; stops at `-TargetRetained` (default 10) or gate loss; discards the scanner session. `-AutoSpace` is an explicit opt-in pulse loop; `-AddressFile` writes the final compare's candidate addresses to a local (untracked) file for the pre-armed debugger, with a `WARN` on count mismatch vs `retainedCount`. |
| `tools/cheat-engine/prearm-attach.lua` | CE pre-arm: attaches to `wotblitz.exe` via native Lua, polls for the rolling driver's address file (`%TEMP%\od-survivors.txt` default), and stages survivor addresses into CE's address list for one-click Find-what-writes. Replaces the version-dependent `cheatengine-x86_64.exe -p <pid>` attach. |

### Changed files

- `docs/operations/offset-discovery-ledger.md` — append-only amendment
  (2026-08-03, pre-OD-RECOVERY-017) correcting the CE/x64dbg presence record;
  header `Last updated` line updated.
- `docs/operations/offset-discovery-workflow.md` — "After gate green" now
  references the pre-arm/rolling scripts plus the Lua pre-arm and
  `-AddressFile`; `Last updated` bumped to 2026-08-03.

## Validation run

- PowerShell parse (`Parser.ParseFile`): `PARSE_OK` on both scripts.
- `scripts/pre-arm-debugger.ps1` live dry-run: found both tools
  (`ce=cheatengine-x86_64.exe x64dbg=x64dbg.exe`), marker written, exit 0.
- `scripts/roll-replay-time-increased.ps1` against the stale host: correctly
  failed closed with `FAILED_gate=EvidenceStale` / exit 3.
- Lua structural check (no interpreter on machine): balanced blocks, CE API
  surface (openProcess / getProcessIDFromProcessName / getOpenedProcessID /
  getAddressList / createMemoryRecord / sleep) consistent with
  `discover-offsets.lua`; `TEMP` nil-guarded.
- Code review (deepseek-flash) fixes applied and re-validated: missing
  rendezvous now maps to documented exit 2; snapshot/compare API error bodies
  are surfaced before field access; CE `-p <pid>` attach flagged as
  version-dependent (Lua pre-arm is the robust path); `TEMP` nil guard in the
  Lua; survivor-count cross-check (`WARN` on candidates vs `retainedCount`);
  workflow `-AddressFile` example uses `$env:TEMP` (PowerShell).

## Assumptions and unknowns

- CE 7.7 `-p <pid>` open-process support is version-dependent; the Lua
  pre-arm (`tools/cheat-engine/prearm-attach.lua`) is the robust attach path.
- The compare response candidate list is not contractually guaranteed to
  equal the retained survivor set; the driver logs a `WARN` on count mismatch
  and the Lua prints the same caution.
- Rolling driver uses the engine snapshot ceiling (no `-MaxBytes`), matching
  the large initial survivor counts seen in OD-013/015/016.
- The operator owns the Space pause/resume transition; `-AutoSpace` exists but
  is opt-in only.

## Integration risks

- The live launch stops the running Host.Web (EvidenceStale) and any CE —
  expected canonical behavior, but it interrupts a manually started host.
- The 120s research lease remains the hard budget: pre-arm must run during
  managed-launch settle, rolling must start immediately post-verify.

## Recommended next steps

1. Run the live OD-RECOVERY-017 session:
   `launch-offline-replay-for-od.ps1` → `pre-arm-debugger.ps1 -AutoAttach`
   (or Lua pre-arm) → `roll-replay-time-increased.ps1 -TargetRetained 10
   -AddressFile "$env:TEMP\od-survivors.txt"`; then interactive
   Find-what-writes on the ≤10 survivors (operator-owned).
2. Place a content-distinct second `.wotbreplay` in the game folder for
   BLK-0019 when available.
3. Push `03ff3a4` + `e9b2bd9` to origin/main when asked.
