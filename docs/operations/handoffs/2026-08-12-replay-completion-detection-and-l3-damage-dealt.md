# Handoff — Replay-completion detection + L3 damage-dealt Phase-4 CLOSED + G2 publication draft (2026-08-12)

**Ledger:** OD-RECOVERY-095 / OD-RECOVERY-096 · **Docs:**
`docs/operations/live-match-gate-design.md`,
`docs/operations/offset-discovery-workflow.md` (completion contract),
`docs/operations/l3-damage-dealt-avatar-family-plan.md` (lane CLOSED),
`docs/operations/g2-damage-dealt-publication-draft.md` (NEW — operator
approval required).

## What happened

Two lanes closed plus one package drafted, all verified against the current
tree (fresh full `validate.ps1` exit 0).

### 1. Replay-completion detection (user-driven: "recognize when a replay starts and completes")

The gap that started this: a replay finished while the chain sat deadlocked and
nothing recognized it. The game **never writes `STOP_REPLAY_LOCAL`** — the only
completion evidence is the post-battle controller transition. Detection is now
log-based, terminal, fail-closed, and distinguishable:

- **Parser** (`BlitzReplayLifecycleParser.cs`): the marker allowlist maps the
  two results-screen controller lines (`Controller activated:
  BattleResultsController` / `BattleResultsPersonalPageController`, real
  shapes from the live log) to `OfflineReplayStopped`. Start detection was
  already solid (`Start replay event` / `START_REPLAY_LOCAL` →
  `OfflineReplayVerified`).
- **Distinct reason code** (`GameSessionCoordinator.cs`): the stopped-marker
  terminal path now denies with `evidence.replay_completed` instead of the
  generic `evidence.monitor_unhealthy`. Tooling can tell three cases apart:
  `evidence.replay_completed` = finished normally, `evidence.monitor_unhealthy`
  = monitor fault, `EvidenceStale` = ended without a completion marker
  (expiry fallback — still fail-closed).
- **Final-end-only semantics, evidence-checked**: the 2026-08-06 blitz-log
  fixture proves auto-loop battles chain WITHOUT a results screen
  (`LoadGameScene begins` follows `onLeaveWorld` directly, zero
  `BattleResults*` between battles) — so the results controller fires only at
  playback end. A negative regression guard feeds the real auto-loop boundary
  lines post-baseline and asserts NO `OfflineReplayStopped` surfaces.
- **Negative finding recorded**: `HangarLoadingController` fires ~2 s before
  the results controllers at replay end BUT ALSO at game startup (before the
  start marker) — adding it would false-complete the session before playback
  begins. Rejected, decision + line evidence in the workflow doc.
- **Driver pre-flight** (`invoke-hp-diffing-session.ps1`): before building any
  dump schedule, the live path reads `GET /api/v1/game/state` once; if the
  gate is already `Denied` with `evidence.replay_completed`, it reports
  "replay already completed … no capture performed" and exits **4** (clean,
  distinguishable; 1/2/3 in use).
- **Uniform gate-reader matrix (2026-08-12, after the write):** the SAME
  completion/fault distinction is now recognized by EVERY gate-reading
  component — launcher pre-watch (exit 2, `FAILED_replay_already_completed`),
  launcher post-watch (exit 4), clicker pre-click (exit 6), and the new
  generalized chain tool (`scripts/invoke-od-replay-chain.ps1`, exit 7).
  Every script header exit-code map + the workflow doc enumerate the same
  token; `evidence.replay_completed` = finished, never a fault.
- **Generalized chain tool (NEW, tracked):** `scripts/invoke-od-replay-chain.ps1`
  — one-command launcher → clicker → driver orchestration generalized from the
  `.data` scratch chains, with the deadlock-free pattern built in (Start-Process
  + `FileShare.ReadWrite` log polling; the driver is `*>`-safe because its
  python calls are synchronous). Runtime-verified against the real scripts:
  it keys on the launcher's `battleSession=` (line 867) and
  `OK OfflineReplayVerified` (line 878) tokens, and all six driver params it
  passes exist (`-SessionId/-Track/-DataRoot/-LiveAcquire/-RegionAnchor/
  -FailOnNoHit`).
- **`.ps1` ASCII hygiene (2026-08-12, after the write):** every tracked `.ps1`
  is now ASCII-clean — launcher comment em-dashes + clicker mojibake (the
  `aEUR"` corrupted-em-dash class, 5 instances) + camera/G1-poll comment
  em-dashes (11 + 2, the "33 + 6" byte count) all → `--`. Zero non-ASCII
  bytes repo-wide in `scripts/` + `tools/`; the latent PS 5.1 mojibake risk is
  gone.
- **Durable deadlock guard** (AGENTS.md gotchas): the PS `& script *> log`
  handle-inheritance trap — the launched host/game grandchildren inherit the
  redirect handle so the redirect-wait never sees EOF and the chain hangs
  while the replay plays out unwatched. Rule: never block on it; poll the
  log/state instead (the Dead Rail chains now use `Start-Process
  -RedirectStandardOutput` + log polling).

Tests: parser suite +3 (2 DataRows + 1 completion-line test), lifecycle feed
suite +2 (end-to-end start→results through the real parser→feed→events path;
auto-loop boundary negative guard) — lifecycle suite 25/25 green, full
GameIntegration green in the gate.

### 2. L3 damage-dealt Phase-4 CLOSED (OD-RECOVERY-095/096)

The avatar-stats quad dword0 IS the own damage-dealt counter, proven to the
Phase-4 standard:

- **OD-RECOVERY-095 (Oasis)**: the at-session lag-0 verdict was an
  honest-negative from the OD-087 memory-apply lag class (+2.3–4.1 s —
  "control-window" changes were real events). Re-verdict with the bounded lag
  path: offset 0x0, 5/5 exact sums (152/144/151/170/1), flatness 1.0, Strict
  5/5; d0 final 752 = decoded `damageDealt`.
- **OD-RECOVERY-096 (Dead Rail)**: live capture (deadlock-free chain, 4
  launches 019ff6d6/019ff6de/019ff6ea/019ff6f0; run-4 persisted 38 dumps,
  labels 158.0–276.9 s) re-verdicts offset 0x0, 9/9 exact sums
  (146/162/145/162/140/178/181/171/168), score 1.0, flatness 1.0, Strict ≥ 2;
  d0 final 1598 = decoded `damageDealt`. **Offsets agree across both replays →
  `twoReplayRepeatability = true`.**
- **Quad layout refined**: `[damageDealt, damageBlocked, damageAssisted1,
  damageAssisted2]` at `[avatar+0x118]` (property indices 0xA–0xD) — Dead Rail
  finals d0 1598 / d1 140 / d3 228 == decoded `damageDealt` / `damageBlocked`
  / `damageAssisted2`; Oasis d2 126 == `damageAssisted1`.
- **Driver fixes shipped with the session** (all evidence-backed): PS 5.1
  `break :label` from a NESTED loop does NOT exit the labeled foreach (flag +
  guard pattern); new teardown status `AvatarIdentityMismatch`; probe status
  check before the informational print (StrictMode missing-member hazard);
  `[ordered]@{}` int-key index assignment → plain hashtable (the REAL root
  cause of the 2026-08-12 `ArgumentOutOfRangeException`, previously
  misattributed); diagnostic trap; deadlock-free chain pattern.

### 3. G2 damage-dealt publication package (DRAFT — operator approval required)

`docs/operations/g2-damage-dealt-publication-draft.md` — the operator-facing
spec mirroring the G1 HP/yaw packages. **One material difference:** publishing
`damageDealt` requires a small data-contract addition — the `damageDealt`
field + a new **`vftableScan`** chain hop kind (the avatar-stats anchor is a
gated vftable AOB scan, not a resolver walk; the current hop taxonomy has no
scan hop). The apply stays operator-gated; the fail-closed default (keep it
out of the table, evidence stays in the ledger) is documented. Consumption
(the live frame's `DamageDealt` field, currently honest-0) is explicitly NOT
in scope — a separate read-surface workstream, exactly as G0/G1.

## What changed

- `src/WotBTreader.GameIntegration/Logs/BlitzReplayLifecycleParser.cs` —
  results-screen markers → `OfflineReplayStopped`; final-end-only semantics in
  the comment.
- `src/WotBTreader.GameIntegration/Session/GameSessionCoordinator.cs` —
  `evidence.replay_completed` distinct reason on the stopped-marker path.
- `tests/WotBTreader.GameIntegration.Tests/BlitzReplayLifecycleParserTests.cs`
  (+2 DataRows + 1 test), `BlitzReplayLifecycleFeedTests.cs` (+2: end-to-end
  completion feed test, auto-loop boundary negative guard).
- `scripts/invoke-hp-diffing-session.ps1` — completion pre-flight (exit 4),
  definitive-teardown schedule stop (flag pattern), `AvatarIdentityMismatch`
  teardown, status-check-before-print, hashtable fix, diagnostic trap, lag
  args on both directions.
- `scripts/launch-offline-replay-for-od.ps1` — EAP probe-guard fix (the
  Error-126 pre-flight probe + client-version mismatch fail-fast; the replay's
  CLIENT VERSION, not its location, is the discriminator) + completion
  recognition in BOTH gate checks (pre-watch exit 2 / post-watch exit 4 →
  `FAILED_replay_already_completed`) + header exit-code map + comment
  em-dashes → `--`.
- `scripts/click-watch-offline.ps1` — pre-click completion recognition (exit
  6, `FAILED_replay_already_completed`) + header exit-code map + 5 mojibake
  fixes (`aEUR"` corrupted em-dash).
- `scripts/invoke-od-replay-chain.ps1` — NEW generalized deadlock-free chain
  (launcher → driver, exit codes 0–7, completion-aware).
- `scripts/invoke-camera-state-verify.ps1`, `scripts/invoke-g1-live-poll.ps1`
  — ASCII hygiene only (comment em-dashes → `--`; parse-verified, semantics
  untouched).
- `docs/operations/offset-promotion-checklist.md` — damage-dealt row updated
  to the OD-095/096 HIT (offset 0x0, `twoReplayRepeatability = true`);
  awaiting-approval paragraph now accurately lists yaw/HP applied (OD-092),
  G2 drafted, pitch/roll pre-staged.
- `docs/operations/live-match-gate-design.md`, `docs/operations/
  offset-discovery-workflow.md` — completion contract, reason codes,
  HangarLoading negative finding, launch-flow completion note.
- `docs/operations/offset-discovery-ledger.md` — OD-RECOVERY-095/096 rows,
  current-status + Next planned session (G2 draft pointer).
- `docs/operations/l3-damage-dealt-avatar-family-plan.md` — lane CLOSED
  (OD-096), quad layout refined, driver-fix record.
- `docs/operations/g2-damage-dealt-publication-draft.md` — NEW (operator spec).
- `AGENTS.md` — L3 closure + G2 draft pointer + the `*>` deadlock gotcha row.
- `offline/file-tree.md` — regenerated (G2 draft + handoff listed).

## Verified

- Avatar-stats rehearsal (regression, current tree): PASS — primary 7/7,
  Phase-4 5/5, offset 0x0 agrees across both replays, flat candidates NOT hit.
- Batch rehearsal re-verdict: PASS — 34/34 compared (8 EntityNotFound skips =
  42 expected pairs, zero misses) on the cleanest Aug-11 capture (019ff172).
- Fresh full `scripts/validate.ps1` — **exit 0** (format → build → all tests →
  scan → analyzers → offline pack → offset tables: 5 published chains, 7
  walkable-draft chains, fidelity clean). Baseline 1045 passed / 3 local
  opt-in skips at the last documented count; this tree adds the new lifecycle
  tests, all green.
- `offline_check.py --refresh` — links resolve, blocker numbering contiguous,
  ledger 69 result sections / 91 index rows consistent.

## What remains

- **Live completion-loop verification — RESOLVED with a correction
  (2026-08-12, OD-RECOVERY-099).** The next approved launch ran the FULL loop
  live: gate `OfflineReplayVerified` → battle played → battle end recognized
  IN-SESSION (`AvatarAnchorNotFound` teardown; the driver stopped the dump
  schedule cleanly and ran the verdict on the captured dumps) → chain exit 0.
  The live observation CORRECTED the design: **the game exits on its own
  ~1–2 min after the Battle Results screen** (blitz log stops mid-results-page
  texture load, no crash event, no shutdown lines, replay file untouched;
  launcher/clicker/driver/chain audited — zero game-kill paths post-launch).
  The cross-session `Denied`/`evidence.replay_completed` re-run signal is
  IN-MEMORY and dies with the process → effectively unobservable. The
  RELIABLE completion signal is the in-session teardown statuses
  (`AvatarAnchorNotFound`/`GateNotSatisfied`/session-inactive) after a
  verified start. **DURABLE FIX IMPLEMENTED (2026-08-12, offline):** a
  persisted completion marker (`scripts/od-replay-completion.ps1`, dot-sourced
  by all four tools) is keyed to the replay's immutable fingerprint (path +
  size + LastWriteTimeUtc) under `%LOCALAPPDATA%\WotBTreader\od-completion\`
  with owner-only ACLs (icacls, the BLK-0026 pattern). The driver persists it
  when the in-session DEFINITIVE teardown fires (the ≤40 s near-end fallback
  deliberately does NOT mark — only a provably-gone anchor proves the replay
  finished); the launcher persists it on an in-window gate denial. Pre-flights
  consult it FIRST and fail fast with `FAILED_replay_already_completed`
  (launcher exit 2, clicker exit 6, driver exit 4, chain exit 7) without
  touching the game. A replaced/re-imported replay (fingerprint mismatch) or a
  deleted replay is treated as fresh (fail-open); a corrupt marker is ignored.
  The in-window `evidence.replay_completed` matrix stays as the
  belt-and-suspenders check. Offline-verified: write → fresh-process
  pre-flight reads (chain/launcher both see completed) → replaced replay
  invalidates to fresh; marker helper behavior covered by a scratch harness
  (init/write/validate/ACL/stale/corrupt/missing/other-path).
### 4. Both publication applies REHEARSED on scratch copies (2026-08-12)

Before either operator-gated apply, each package's apply path was proven on
scratch copies with a scratch validator (harnesses in `.build/`,
gitignored; reproducible from the corrected specs in the drafts):

- **G2 damage-dealt — rehearsal FAILED first, then fixed.** The drafted §4
  step 2 was INCOMPLETE: the first run hit 11 validator issues (the checker
  also enforces `FIELD_DEFS`, `OPTIONAL_FIELDS`, the chain SHAPE check
  `rootRva`-first, and a hardcoded fidelity shape). The corrected step 2
  (FIELD_DEFS + OPTIONAL_FIELDS + shape-check + a `damageDealt` fidelity
  branch mirroring `playerHP`) re-ran to PASS — `11.19.0.10.json: chains
  validated (6 field(s))`, fidelity 6 fields, `PASS` exit 0. The corrected
  four-point spec is recorded in `g2-damage-dealt-publication-draft.md` §4
  step 2 + the header's APPLY REHEARSED status.
- **G1 pitch/roll — PASSED first run.** The §3 table edit (chains verbatim
  from the canonical draft, recordOffset 44/40, offsets 0, fieldValidation
  mirroring playerYaw) validated clean: `chains validated (7 field(s))`,
  fidelity 7 fields, exit 0 — confirming the draft's pre-staged claim; no
  gaps. Recorded in `g1-pitch-roll-publication-draft.md`'s header.

Both packages are now rehearsal-proven: operator approval = low-risk
mechanical execution.

### 5. G2 publication APPLIED (2026-08-12, OD-RECOVERY-097)

The operator-approved G2 apply landed (schema + checker + walkable draft +
table + pack doc + ledger row). The REAL gates surfaced three extensions
the draft did not enumerate — caught and fixed during the apply:
- `tools/report-offset-evidence.ps1`: `$knownFields` + a new `$optionalFields`
  set (mirroring the schema's OPTIONAL_FIELDS) + the GameHarness-kind
  acceptance in BOTH the structure check and `Get-FieldStatus` (pre-existing
  G1 drift: yaw/HP record `DynamicScan` with a GameHarness sourceTool, the
  tool required the `GameHarness` kind — it had been failing since
  e79a6bc); also `playerHP` gained its missing StaticAnalysis evidence
  (the item-7 Branch A census — exists, was never recorded).
- `src/WotBTreader.Application/Replay/OffsetTableReader.cs`: `KnownFieldNames`
  gained damageDealt (+ playerPitch/playerRoll pre-staged) — the C# reader
  rejects unknown fieldValidation keys (`Walk_PublishedTableChains_*` were
  failing).
- Post-apply: `offset_check --check-schema` chains-validated 6 fields +
  fidelity 6/6; `report-offset-evidence.ps1` verified=6 exit 0;
  ChainedFields exclusion test; `validate.ps1` exit 0.

Six fields are now `Verified` via chains. `damageDealt` is published with
`offsets 0`; the read surface stays untouched (own frame row honest-0 until
the consumption workstream).

### 6. G1 pitch/roll publication APPLIED (2026-08-12, OD-RECOVERY-098)

`playerPitch` / `playerRoll` published `Verified` via the ring-record chain
(the identical position walk, `recordOffset 44` / `40`); offsets 0.
Evidence: the rotation-triple reconciliation (`yaw-diff --field pitch|roll`
re-verdicts the SAME OD-088/089 dumps — Oasis 48/48 + Dead Rail 56/56,
score 1.0, flatness 1.0, record-span trimmed) + the item-7 Branch A
rotation sub-proof as StaticAnalysis evidence. Post-apply: `offset_check
--check-schema` 8 chains + fidelity 8/8 (the draft's "7" expectation
predates the G2 apply), report verified=8 exit 0, ChainedFields exclusion
test, `validate.ps1` exit 0. **Eight fields are now `Verified` via
chains** — the rotation triple is fully published.

### 7. Damage-dealt consumption IMPLEMENTED (2026-08-12)

The live frame's own-row `DamageDealt` is no longer honest-0:
- `LiveFrameReadRequest` gained `OwnEntityId`; `LiveFrameTankState` gained
  `long? DamageDealt` (additive, nullable — no existing consumer breaks).
- The endpoint derives the own entity id from the decoded session's
  viewpoint participant (before the frame read) and forwards it.
- The coordinator reads the own Avatar's battle-stats dword0 via the gated
  vftable scan + identity re-gate + quad read (the OD-095/096-proven seam);
  fail-closed — any failure leaves the row's DamageDealt null, never
  guessed; the frame still succeeds.
- The projector maps it for the OWN row only; all other rows keep 0.
- Tests: 2 coordinator (attached-to-own-row + scan-not-found fail-closed),
  1 projector (own-only), 1 endpoint (own-id forwarded into the request).
  Full `validate.ps1` exit 0.

### 8. OD-099 live session: damage-dealt in-session HIT + completion detection live (2026-08-12)

Operator go-ahead executed the remaining live items. The chain launch
(`scripts/invoke-od-replay-chain.ps1` → launcher → clicker → hp-diffing
driver `-LiveAcquire -Track damage-dealt -RegionAnchor avatar-stats`)
played the ground-truth Oasis replay (session `019ff74f-fd4c-7a30-8686-
f71c18db4b22`, own viewpoint 3760577) end-to-end:

- **Damage-dealt lane re-proven live IN-SESSION at DEFAULT lag (no explicit
  override — the fixed driver passes lag args on both directions; default
  tolerance 12 s):** 20 region dumps, every probe `status='Resolved'
  candidates=1` across the whole battle (the gated AOB scan + identity
  re-gate — the EXACT read path the live-frame consumption seam uses —
  proven live); verdict **offset 0x0, score 1.0, flatness 1.0, 5/5 exact
  sums (152/144/151/170/1; the first 134 at 177.82 s predates the formable
  capture span — same class as OD-095/096), Strict ≥ 2 → HIT**. Snapshots:
  `.data/hp-snapshots-019ff74f-*-cand0.json`.
- **Completion detection verified live end-to-end:** battle end recognized
  in-session via `AvatarAnchorNotFound` at dump target 259.3 s → dump
  schedule stopped → verdict ran on the captured dumps → chain exit 0 (no
  error, no hang — the exact failure mode the user hit earlier, now
  handled).
- **The game-exit finding (durable):** after the results screen the game
  exits on its own (forensics above in What remains). This is consistent
  with the driver's pre-existing `gate_not_satisfied`-on-game-exit comment.
  The `evidence.replay_completed` re-run gate denial was never observed live
  and is now understood to be unreachable by design — record it as
  SUPERSEDED by the in-session teardown detection + the IMPLEMENTED persisted
  completion marker (`scripts/od-replay-completion.ps1`).

- **Follow-on launch attempt (2026-08-12, same session):** the operator
go-ahead also covered the batch `-LiveAcquire` rehearsal re-run (Branch B
step 3 live measurement) — the launcher was re-invoked pinned to the
ground-truth replay, but the attempt FAILED at the replay-path argument
(`FAILED_replay_path_missing`): the launch wrapper passed the literal
`$env:LOCALAPPDATA\wotblitz\DAVAProject\replays\…` string instead of the
resolved path (bash→PowerShell argument escaping — the `$env:` expansion
never happened). The launcher fail-closed correctly (exit with the
`FAILED_replay_path_missing` token, nothing launched). Durable lesson: pass
the ABSOLUTE literal path (`C:\Users\mrkoo\AppData\Local\wotblitz\DAVAProject\replays\…`),
never `$env:`-relative text, when invoking the launcher from bash. The batch
rehearsal re-run + Branch B steps 3–4 remain open on the next approved
launch.

- **Tree state at handoff end:** the G2 apply + OD-097 record are committed
  as `feat(od): publish damageDealt via vftableScan chain (OD-097)`; the
  pitch/roll apply (OD-RECOVERY-098, table-only) is committed as its own
  conventional commit; the damage-dealt consumption workstream is committed
  as its own conventional commit; the apply-rehearsal tool is committed as
  `feat(od): tracked apply-rehearsal tool for offset publications`. **This
  turn's pending commit (docs-only, 4 files):** `AGENTS.md` (OD-097/098/099 +
  consumption + completion-loop correction), the ledger (OD-099 row +
  updated Next planned session), this handoff, and the refreshed
  `offline/file-tree.md` — commit as one docs conventional commit after the
  gate.
- **Batch rehearsal re-run (`-LiveAcquire`)** — re-establish the clean 42/42
  live verdict with the current driver (also Branch B step 3's live
  read-pass measurement: 100% byte-identical double-reads / zero
  `region-unstable-snapshot` acceptance). Next approved launch; the last
  attempt was blocked by the `FAILED_replay_path_missing` argument bug
  above — use the absolute replay path.
- **Item-7 Branch B step 4** — camera pose double-read treatment remains for
  the live half (the camera verify tool is vision-only today).
- **Item 7 (hardware atomicity)** — stays LAST; **Branch A quad sub-proof
  DONE (2026-08-12)** — width-complete census (MOV + RMW + **XADD/CMPXCHG**,
  all widths) of every write to the Avatar battle-stats quad: 1688 candidates
  → 1646 boundary-confirmed → 1642 real memory writes (42 off-boundary + 4
  register-only misattributions rejected), **ZERO 64/128-bit writes to any
  quad dword**, d0 = 401× dword (10 RMW) + 10× byte → a 32-bit read cannot
  tear (bounded live by OD-095/096). Tooling
  `ScanAvatarStatsQuadStoreWidths.java` + `ConfirmAvatarStatsQuadSites.java`
  (new); evidence `.build/ghidra-evidence-avatar-quad/`. Refinements after the
  write: (a) XADD/CMPXCHG coverage added to both scripts — zero targets the
  quad, and the binary contains 25,875 XADD + 545 CMPXCHG sequences so the
  parser was exercised, not dead; (b) the 10 d0 RMW sites are all FIXED
  increments (INC/ADD-imm) — the variable damage path is load-add-store, one
  of the 163 register-source `MOV dword [..+0x118],reg` sites (documents
  corrected accordingly); (c) the one apparent 64-bit write to the quad in
  the decompiled victim decoder is a DECOMPILER MISLABEL — the instruction
  listing shows `LEA [ESI+0x128]` + `MOVSD`, `+0x128` OUTSIDE the quad
  (trust disassembly over decompiled C for field offsets — documented
  gotcha); (d) the vehicle-update dispatcher is statically unreachable — the
  Vehicle vtable's only references are the two installers (no direct
  dispatcher; virtual dispatch reads `CALL [reg+0x30]` through the object
  pointer, invisible to reference analysis) — both identification paths
  (caller-walk + vtable-ref) proven exhausted and documented. Branch B live
  steps 3–4 need approved launches.
