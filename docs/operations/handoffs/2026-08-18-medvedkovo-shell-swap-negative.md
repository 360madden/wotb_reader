# Penetration v0.3 — medvedkovo shell-swap live attempt (G1 item 2)

**Date:** 2026-08-18 (UTC)
**Result:** honest negative — no shell-swap transition in the medvedkovo replay.
**Build:** hash-bound `1cda5c31…` (exact-build, re-verified by the launcher probe).

## What ran

Launched `medvedkovo-20260802.wotbreplay` (formerly `deadrail-20260802.wotbreplay`) through the canonical
`launch-offline-replay-for-od.ps1` (first attempt — no Watch Offline flake),
reached `OfflineReplayVerified` / `session.offline_replay_verified`, then
polled the `shell-state` entity-region anchor with
`scripts/capture-pen-shell-state.ps1 -PollSeconds 600`.

Battle session: `01a01610-b9f0-7f3e-892a-35f2a7bddf32`.

## Live observations

- **Map:** `medvedkovo` (the file was formerly the mislabeled `deadrail-*.wotbreplay`; the real Dead Rail map is `desert_train`), battle started
  `18:11:21Z`, game mode regular. Player vehicle `2549401`.
- **Shell-state read resolved stably** across the whole battle window
  (`18:11:21Z → 18:15:54Z`):
  - `index=0`, `identity0=5`, `identity1=71`, two-pass `stable=True`.
  - `identity0=5` = a status/tier discriminator, `identity1=71` = the component
    id (per the corrected decoding in handoff
    `2026-08-18-shell-identity-holder-writer.md`) — the same fingerprint the
    Churchill I / savanna session returned, consistent with the same vehicle/loadout.
  - **0 transitions** (`samples=147, distinct_states=1, transitions=0`):
    the index and identity fingerprint never changed, so no shell swap occurred.
- **Playback did not end cleanly.** The player's vehicle left the world at
  `18:15:54Z` (frame 25947, `AvatarGameLogic::onBecomeNonPlayer`), and the game
  then hit the known `ListenerHolderBase` `!listeners.size()` assert
  (`18:15:57Z`, `[assert,base]`) — the recurring replay playback crash the
  launcher already documents. The gate reverted to
  `Denied / evidence.monitor_unhealthy` after the crash; the remaining poll
  window returned `discover.gate_not_satisfied` (expected, not a read fault).

## Conclusion

**The medvedkovo replay contains no shell-swap transition.** Both available replays —
Churchill I / savanna (2026-08-18, 87 samples / 0 transitions) and medvedkovo
(this run, 147 samples / 0 transitions) — are swap-free. The G1 item 2
promotion gate is therefore **not closed**: it requires a controlled
shell-swap with a known transition order, which neither replay provides and
which cannot be synthesized from a passive replay.

## Remaining for G1 item 2

A freshly recorded **controlled swap** (manual gameplay: fire a shell, switch
to a second shell kind, fire again) read through the `shell-state` anchor.
That is an owner-run scenario; nothing further is actionable offline. Until
then BLK-0027 stays open and the badge stays honest `NotReady` /
`WeaponStateUnavailable`.

Nothing was promoted.
