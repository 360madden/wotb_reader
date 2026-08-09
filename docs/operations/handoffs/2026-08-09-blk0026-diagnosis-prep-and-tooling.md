# BLK-0026 diagnosis prep, grill-me adoption, and agent toolchain cleanup

Date: 2026-08-09
Status: milestone recorded; diagnosis plan encoded; **no live testing performed**
Scope: offline/static verification, read-only lifecycle forensics, agent tooling,
and documentation only

## Session summary

Three pushed commits plus one uncommitted diagnosis milestone:

- `72d0b3f` — Cursor toolchain removed (subscription ended): `.cursor/`,
  `.cursorignore`, `scripts/invoke-cursor-agent.ps1` deleted; delegation index,
  model stance, and `tools.lock.json` (11 → 10 tools) reconciled; offline pack
  regenerated.
- `f4c1777` — `grill-me` skill adopted (verified against actual content across
  the ecosystem survey; only skill that survived content-verification, active
  source, and repo-fit screening): `.agents/skills/grill-me/` + required
  `grilling/` core, adapted from `mattpocock/skills` (MIT, attributed), wired
  into the AGENTS.md task decision tree.
- Uncommitted — BLK-0026 launch diagnosis: hypothesis (b) refuted offline, plan
  encoded, read-only wrapper built (see below).

## BLK-0026 diagnosis milestone (no live testing)

Per the blocker's standing decision (diagnose without memory access first, then
exactly one unchanged bounded poll), the launch path was diagnosed using only
offline decode and read-only lifecycle evidence:

1. **Hypothesis (b) refuted.** `scripts/invoke-replay-crosscheck.ps1` on all
   three real `.data/launch` replays: exit 0 for each (13–17 s). The
   content-distinct replay is neither corrupt nor an unsupported version.
2. **Gate-before-binding order confirmed.** `od-073-entity-position-poll.ps1`
   waits for `OfflineReplayVerified` (180 s) before `Get-LaunchArtifactId`; a
   binding failure alone cannot explain the observed `session.initial`. The
   original failure was pre-gate, in launch/evidence establishment.
3. **Marker fail-closed rules mapped.** Absent / not-owner-only / stale
   (>20 min) / malformed → `FAILED_launch_artifact_binding`. The current marker
   is structurally valid but **767.8 min stale** (wrapper `-StaticOnly`
   check): any poll against it fails binding. The eventual unchanged poll must
   run within 20 minutes of a fresh import.
4. **Evidence-loss root cause found.** `Write-Od` is console-only; the failed
   attempts' lifecycle stream was never persisted. The scratch wrapper tees it.
5. **Launcher exit-path inventory** (pre-gate): `FAILED_launch_marker_directory_acl`,
   `FAILED_launch_marker_acl`, `FAILED_launch_http`, `FAILED_launch=<msg>`,
   `FAILED_no_window`, `FAILED_host_denied_before_watch_restart_required`.

Surviving hypotheses: (a) launcher-side pre-game failure (leading), (c)
gate/timing (battle boundary or evidence-lifetime expiry), (d) Host
attach/identification.

## Artifacts

- Plan (tracked): `docs/operations/blk-0026-diagnosis-plan.md` — settled design
  tree, verified facts, execution steps, stop conditions, privacy rules.
- Wrapper (scratch, gitignored): `.data/diagnose-blk0026-launch.ps1` — read-only
  wrap of the canonical launcher: tees the `od_launch:` stream, samples Host
  state every 5 s (embedded owner-only loopback rendezvous reader), records
  marker state before/after + launcher exit code. `-StaticOnly` verified.
- Blocker record updated: `docs/operations/blocker-log.md` BLK-0026.

## Uncommitted at session end

`docs/operations/blk-0026-diagnosis-plan.md` (staged), `docs/operations/blocker-log.md`,
`offline/file-tree.md`, `AGENTS.md` (where-we-are-now pointer), ledger
Next-planned-session row.

## Next steps (require live-testing approval)

1. Wrapper launch (one launch) → branch on the tee'd `od_launch:` stream.
2. Reproduce the failing signature (twice if intermittent).
3. Controlled resolved run → `OfflineReplayVerified` reached once.
4. **Exactly one** unchanged bounded OD-075 poll on the content-distinct replay,
   within 20 minutes of a fresh import. Resolver/read-surface/offsets frozen.
5. Ledger result + dated handoff; update BLK-0026.

## Privacy

No replay path, artifact UUID, process address, PID, raw byte, player/account
data, or other private value is copied into tracked documentation.
