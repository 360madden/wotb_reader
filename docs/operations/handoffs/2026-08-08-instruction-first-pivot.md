# Instruction-first player-position pivot (2026-08-08)

Outcome: implemented and synthetically validated. No game, replay, private
database, or live process was accessed during this implementation milestone.
No offset was promoted.

## Why the workflow changed

FRESH44 proved that a transient viewpoint-X correlation repeats across two
independent replays. FRESH45 then read all 12 floats for four proposed
`candidate-0x1C` bases but found zero complete XYZ matches. Repeating that
path, shortening its 102.2 ms completion gap, or re-running the delayed trace
would not add object provenance.

The new path begins at the durable FRESH43 static/dynamic anchor:
`wotblitz.exe+0x7C39AB`, bytes `8B83A0000000`. At that instruction, EBX is the
candidate transform object and the position members are at EBX+`0x1C/+0x20/
+0x24`. An execute breakpoint captures EBX before the instruction continues
and performs one contiguous 12-byte read while the debug event is held.

## What landed

- A separate x86 `WotBTreader.InstructionSnapshotHelper` binary; the existing
  PAGE_GUARD binary is unchanged and its raw-PID/write-capable mode is not
  compiled into the production snapshot helper.
- Per-thread hardware execute-breakpoint management with exact DR0-DR3/DR6/DR7
  preservation, CREATE_THREAD arming, owned-event filtering, resume-flag
  handling, max-hit/timeout/cancellation cleanup, and fail-closed detach.
- Production identity validation for PID creation time, canonical executable,
  version/hash, unique module, PE image size/executable section, `MEM_IMAGE`,
  RVA, and exact instruction bytes. The initial post-attach process event is
  revalidated before any thread is armed.
- An inherited anonymous-pipe protocol bound to the independently verified
  parent PID/start plus build-pinned Host.Web apphost and managed-assembly
  hashes. Production command line has no raw PID/address/module/register/
  displacement inputs, and caller-created pipes from another parent fail
  before target access.
- A coordinator-authorized `IGameMemoryScanner` operation and loopback
  `POST /api/v1/game/discover/instruction-snapshot` endpoint. Authorization
  generation remains live; cleanup failure denies the session and terminates
  the exact managed child.
- A GameHarness `discover-instruction-snapshot` operator command.
- Privacy projection from heap addresses to per-capture `object-NN` keys, plus
  a server-owned sampling interval so the result can contain short trajectories
  instead of an immediate ungrouped burst.
- `launch-offline-replay-for-od.ps1 -EnableInstructionSnapshot`, which requires
  a fresh helper publish pinned to the exact Release Host.Web EXE and DLL,
  validates an independently written identity manifest plus a fresh
  mode-specific nonce response, starts that apphost directly, and removes the
  helper path/hash environment before the game child can inherit it.

## Evidence limits

The implementation can prove a register-derived object pointer and one
same-debug-event contiguous XYZ read at the pinned instruction. It deliberately
reports hardware atomicity, decoded-clock identity, viewpoint identity, and
stable-root proof as false. Public output contains no PID, heap address, full
path, instruction bytes, register dump, replay identity, capability, account,
player, chat, screenshot, or raw replay bytes.

`memory-offsets/11.19.0.10.json` remains unchanged: player-position fields are
zero/Unknown and no promotion count or approval changes.

## Validation completed

- Full `scripts/validate.ps1`: pass (Release build, 630 tests passed, 2 local
  installed-game tests skipped, repository scan, PowerShell analyzer, offline
  pack/link/ledger checks).
- Release solution build: 0 warnings/errors.
- Focused GameIntegration tests: pass (two installed-game tests remain opt-in).
- Focused Host.Web endpoint tests: pass.
- Separate self-contained win-x86 helper publish: pass.
- `tmpwotb-e2e/test-execute-snapshot-interceptor.ps1`: pass; four changing,
  finite XYZ hits; max-hit and timeout cleanup/detach proven; raw-PID/legacy
  production modes and a non-pinned caller-created pipe plan rejected.

The security/privacy source audit found no remaining Critical, High, or Medium
blocker after the separate-helper, parent/assembly pin, owner-only manifest,
post-attach identity, cancellation, and cleanup hardening.

## Next live session: OD-RECOVERY-063

1. Publish and pass the synthetic helper test.
2. Start a new managed offline replay with
   `launch-offline-replay-for-od.ps1 -EnableInstructionSnapshot`.
3. Run one `GameHarness discover-instruction-snapshot --seconds 5 --max-hits
   64` capture.
4. Group by object key and compare each short XYZ trajectory to decoded ground
   truth.
5. Stop after the result. Do not revert to broad scans, candidate-minus-offset
   guesses, delayed PAGE_GUARD tracing, or latency-only tuning.
6. Only a matching object-key trajectory permits the same proof on the other
   replay/fresh process. Publication still requires a stable resolver/root and
   the full evidence/approval checklist.

Full contract:
[`docs/superpowers/specs/2026-08-08-instruction-first-position-snapshot.md`](../../superpowers/specs/2026-08-08-instruction-first-position-snapshot.md).
