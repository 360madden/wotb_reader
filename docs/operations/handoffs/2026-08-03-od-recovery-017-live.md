# Session handoff — 2026-08-03: OD-RECOVERY-017 live session (partial)

**Author:** Codex Agent

**Branch:** `main` (head `63845d7`; working tree: `scripts/roll-replay-time-increased.ps1` modified — driver field fix, to be committed with this handoff)

## Outcome

OD-RECOVERY-017 ran **Partial**. Three live managed launches were attempted;
the first two exposed a root-cause defect in the Watch Offline clicker
(replay-HUD orange false-positive), which was fixed and proven on attempt #3
(gate green, `watch_exit=0`). Pre-arm and rolling then hit a second defect in
the new rolling driver (it read `retainedCount` instead of `increasedCount`),
which is now fixed. The 120s lease expired before a full rolling sequence ran
with the fixed driver; attempt #4 was not run (operator stopped the session).

## Session record

Session ID: `OD-RECOVERY-017`. Status: **Partial**.
Prior: OD-RECOVERY-016 (`Partial`; rolling RT to 8, lease lost before
interactive root). BLK-0019 remains open — only the single Churchill replay
(sha12 `0FAE5612491E`, `import duplicate=True`) exists in the game folder;
`independentReplays` still 0.

### Attempt #1 — launch helper hung past 420s

- Managed launch reached `lifecycle_evidence outcome=verified` (game launched
  and correlated), rendezvous published.
- Watch script wrote a 1-byte exit file then something stalled the helper past
  420s; the game process exited; gate flipped to
  `Denied / evidence.monitor_unhealthy`.
- Likely the known PrintWindow capture-freeze path (same failure the earlier
  `fix(ops): restore PrintWindow capture` commits fought).

### Attempt #2 — gate green but dialogGone never set

- Gate reached `OfflineReplayVerified` post-watch; blitz-log shows
  `startReplay=True` — **the replay was actually playing**.
- But `dialogGone=False` (`final_orange_pixels=63876`), `loginOnReplay=True`;
  watch reported failure (exit 3) after retries; the game died and the gate
  flipped `Denied / evidence.monitor_unhealthy`. Lease expired before rolling.

### Root cause (attempts #1–#2)

The watch clicker requires **both** `gateOk` AND the orange dialog blob to
vanish. After dismissal, the **replay HUD itself renders orange inside the
dialog ROI** (`orangePx` grew 8276 → ~64,000 after the dialog was gone), so
`dialogGone` never turned true. The script clicked all 5 rounds at shifting
coordinates; the extra clicks hit in-game UI and killed the game
(`monitor_unhealthy`), and the 120s lease died before rolling could start.

Key evidence: `OfflineReplayVerified` **proves** the replay started — the
lifecycle monitor requires a fresh `START_REPLAY_LOCAL` / `Start replay event`
marker — which **proves the WATCH OFFLINE dialog is gone**. Requiring the blob
to vanish was both redundant and harmful.

### Fix 1 — `scripts/click-watch-offline.ps1` (committed `63845d7`)

Once the gate is verified, stop clicking and treat dismissal as satisfied:
loop-break on `gateOk` and final `verified → dialogGone = $true`. Header and
exit-code docs updated. Validated: parse OK, both logic anchors present.

### Attempt #3 — green with the fixed clicker; driver field bug found

- Gate `OfflineReplayVerified`, `watch_exit=0`,
  `SUCCESS_gate_and_dialog_dismissed`.
- Pre-arm: **CE 7.7 launched attached** (`C:\Program Files\Cheat Engine`,
  PID 50152) — first live pre-arm in repo history; marker written.
- Rolling driver immediately exposed a semantics bug: `retained=0` on round 1
  while `increased=1041003`, so it stopped instantly.
- Contract (`GameSessionContracts.cs`): **`RetainedCount` is only "unreadable
  prior chunks carried forward", not survivors. The survivor count is
  `IncreasedCount`** — matching the OD-013/015/016 sequences
  (628320→…→8 etc.), which were all IncreasedCount values.
- The `-AddressFile` dump from the buggy run was a raw 500-candidate round-1
  sample (`0x9CB320…`), not a narrowed set — discarded.

### Fix 2 — `scripts/roll-replay-time-increased.ps1` (uncommitted at write time)

- Tracks `$survivors = $cmp.increasedCount` (was `$retainedCount`).
- `-AddressFile` writes candidates from `$lastCmp` only (the final compare),
  not a mid-roll sample; count-mismatch WARN now vs `survivors`.
- Sequence logging unchanged (`increased=… retained=…` both printed);
  `TARGET survivors=…` replaces `TARGET retained=…`.
- Validated: parse OK; fail-closed still exit 3 on `EvidenceStale`.

### Attempt #4 — not run

Operator stopped the live session. Gate expired (`EvidenceStale /
evidence.expired`) before the fixed driver could run a full sequence.

## Validation commands run

- `Parser.ParseFile` on all touched `.ps1`: `PARSE_OK`.
- `roll-replay-time-increased.ps1 -MaxRounds 1` against stale host: exit 3
  `FAILED_gate=EvidenceStale` (fail-closed preserved after both fixes).
- `pre-arm-debugger.ps1` live: found CE 7.7 + x64dbg, launched CE attached,
  marker written, exit 0.
- `git diff` review of the driver field fix (19 insertions / 16 deletions).
- Code review (deepseek-flash) on the clicker fix before commit `63845d7`.

## Changed files this session (campaign commits `e9b2bd9` → `6be2f64` → `63845d7`)

- `scripts/click-watch-offline.ps1` — **committed `63845d7`**: trust verified
  gate over dialog blob; stop clicking once verified.
- `scripts/roll-replay-time-increased.ps1` — **uncommitted fix**: survivor
  count is `increasedCount`, not `retainedCount`; `-AddressFile` from final
  compare only.
- `docs/operations/offset-discovery-ledger.md` — OD-017 row + YAML record
  (this unit).
- `docs/operations/offset-discovery-workflow.md` — next-session protocol →
  OD-018; clicker gate-trust behavior (this unit).
- `docs/operations/handoffs/2026-08-03-od-recovery-017-live.md` — this handoff.

Prior prep commits: `e9b2bd9` (pre-arm + rolling driver scripts),
`6be2f64` (CE Lua pre-arm attach + `-AddressFile` + prep handoff).

## Assumptions and unknowns

- Attempt #1's hang is attributed to the PrintWindow capture-freeze path but
  was not directly observed in a captured log; attempt #3's clean green run
  suggests the clicker fix is the dominant factor.
- The fixed rolling driver has not yet run a full post-verify sequence to ≤10;
  the `increasedCount` semantics are contract-backed, not yet live-proven.
- CE PID 50152 was left running attached to a dead game; the launch helper
  stops stale CE at the next launch, or the operator may close it.
- BLK-0019 (content-distinct second replay) still open.

## Integration risks

- Next launch must use the **fixed clicker + fixed driver together**; using
  the old clicker repeats the game-kill path.
- The 120s research lease remains the hard budget; pre-arm must run during
  managed-launch settle (or concurrent), rolling immediately post-verify.

## Recommended next steps

1. Run OD-RECOVERY-018: `launch-offline-replay-for-od.ps1` →
   `pre-arm-debugger.ps1 -AutoAttach` (or Lua pre-arm) →
   `roll-replay-time-increased.ps1 -TargetRetained 10 -AutoSpace
   -AddressFile "$env:TEMP\od-survivors.txt"` → interactive Find-what-writes
   on the ≤10 survivors (operator-owned, lease margin reserved).
2. Place a content-distinct second `.wotbreplay` in the game folder for
   BLK-0019 when available.
3. Push `03ff3a4`, `e9b2bd9`, `6be2f64`, `63845d7` (+ this unit) to
   origin/main when asked.
