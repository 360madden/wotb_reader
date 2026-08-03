# Session handoff — 2026-08-03: OD-RECOVERY-016 Partial

**Author:** Codex Agent

**Branch:** `main`

## Outcome

`OD-RECOVERY-016` is **Partial** (launch green; rolling to 8; EvidenceStale
before interactive root).

### Live pulse

- Release rebuild of `WotBTreader.sln` succeeded (0 warnings/errors) before
  launch; binaries had been stale vs staging path work.
- `powershell -File scripts/launch-offline-replay-for-od.ps1` exit 0 →
  `OfflineReplayVerified` (Watch Offline sync-dim → click; gate+dialog dismissed).
- Game-folder source: single original `.wotbreplay`; content sha12
  `0FAE5612491E`; artifact prefix `019fc45f`; import reported
  `duplicate=True` → `independentReplays` still 0 (BLK-0019 open).
- Rolling Double increased with `rollingBaseline=true` and Space pause/resume
  pulses: **628320→111860→24897→4851→787→339→180→64→50→45→29→25→20→12→8**
  (reached ≤10 at 8). Sample address kind overwhelmingly `private-mapping` (one
  early 100-candidate round had `mapped-mapping=1`).
- Immediately after ≤10, gate flipped to `EvidenceStale` / `evidence.expired`
  before interactive CE/x64dbg Find-what-writes could run.
- Default Cheat Engine 7.7 install path not found on this machine in a quick
  probe; no CE process was running.
- Session discarded; no promotion; offset remains 0.

### Deferred hygiene (out of scope)

Uncommitted hangar amendment (`already_on_replays` harden, workflow
playback-only docs, untracked hangar handoff) remains local — not part of this
closeout.

## Next move

1. `OD-RECOVERY-017`: pre-arm interactive CE/x64dbg **before or during** managed
   launch; start Double rolling immediately post-verify; aim to reach ≤10 with
   lease margin for Find-what-writes on survivors.
2. Place a content-distinct second `.wotbreplay` in the game folder for
   BLK-0019 when available.
3. Commit this closeout when asked.
