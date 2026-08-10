# Handoff — 2026-08-10: L1 HP-diffing driver wired to the L0 region-read seam

**Branch:** `main` — clean tree before this phase, gate green after (`565e2f0`).

## What landed

### `scripts/invoke-hp-diffing-session.ps1` — the live DUMP seam is now real

The L1 session driver's step 2 (DUMP) previously exited 3 with the contract.
It now performs live acquisition through the L0 seam end-to-end:

- **`-LiveAcquire`** — for every scheduled dump time (before + after each
  damage event from the extractor's `dump_schedule`, plus `-ControlTimes` for
  the flat controls), POSTs `/api/v1/game/discover/entity-region` with the
  target entity, `-RegionLength` (default 256 — covers the ring record at
  +0x38 plus the +0x48 HP candidate), and the session id.
- **Fail-closed clock attestation** — every dump requires
  `sameDecodedClockProven = true`; a false attestation aborts the session
  rather than labeling a dump with an unproven clock.
- **Snapshots file** — writes the `wotbtreader.od.hp-diff.snapshots.v1`
  schema with strictly increasing replay times (live clocks can jitter, so
  the driver re-sorts by the response's `replayTimeSeconds` and drops
  non-increasing duplicates fail-closed).
- **No-host behavior preserved** — without a reachable rendezvous the driver
  still exits 3 with the contract; offline verdict mode (`-SnapshotsPath`
  with an existing file) is unchanged and still works.

### StrictMode crash fix

`ConvertFrom-Json` can drop null-valued members, so under
`Set-StrictMode -Version Latest` the old `$null -ne $VerdictData.topCandidate`
guard threw `PropertyNotFoundException` whenever the verdict had no candidate
(hit=False). The driver now probes `$VerdictData.PSObject.Properties['topCandidate']`
and reads through `$TopCandidate.Value`.

## Validation (offline, real data)

Full end-to-end rehearsal against the real Oasis Palms session
(`019fdff7-8dcf-7426-8547-9fb8cc3eb07b`, victim `3760578`):

- **Qualify** → 9-hit dump schedule from `replay-delta-extractor.py`.
- **Verdict (decrement)** on a synthetic region with the HP field at `+0x48`
  dropping by each damage sum → **HIT**: offset `0x48`, score 1.0,
  flatness 1.0, 9/9 windows matched.
- **Verdict (increment)** on a synthetic damage-dealt counter at `+0x48`
  (attacker defaults to the session viewpoint) → **HIT**: 5/5 windows.
- **No-hit** synthetic → `hit=False`, and with `-FailOnNoHit` the driver
  exits 1 cleanly (no crash — the StrictMode fix).
- **No dump + no live** → exits 3 with the contract; **`-LiveAcquire` with
  no host** → fails fail-closed on `rendezvous_unavailable`.

### Test-fixture insight (worth recording)

The first synthetic dump failed flatness (0) because its change windows were
built as `(t-0.2, t]` from the extractor's *rounded* event times, while the
DB stores sub-centisecond ticks (90.447754 vs 90.45). Events 4–7 and 9 fell
just outside their rounded windows, were dropped from attribution, and the
residual windows looked like "changed controls." The real driver dumps at
`before=t−0.2` and `after=t+0.2`, so every event lands inside its window —
the rehearsal must mirror that shape (`(before, after]`, ~0.4 s wide).

## Files changed

- `scripts/invoke-hp-diffing-session.ps1` — live acquisition + guards +
  StrictMode fix + docstring/examples updated.
- `offline/file-tree.md` — regenerated (includes the L0 handoff).

## Validation

- Offline gate (`offline_check.py --refresh`) — all links, blockers, ledger
  consistent.
- `scripts/validate.ps1` — PASS (all offset files valid, all tool suites).

## Next

The L1 live session is now code-ready: run
`powershell -File scripts/invoke-hp-diffing-session.ps1 -SessionId <id>
-VictimEntityId <victim> -LiveAcquire -ControlTimes 30,230` with the web
host serving the verified offline replay. That remains gated on operator
approval — nothing here touches the live game.
