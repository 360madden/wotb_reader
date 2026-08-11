# OD-RECOVERY-089 live-run evidence — L2 facing Phase-4 repeat on Dead Rail (COMPLETE)

**Status: COMPLETE (2026-08-11) — `twoReplayRepeatability = true` for yaw.**

The Phase-4 rule requires the yaw offset found live on Oasis Palms
(OD-RECOVERY-088: ring-record `+0x30`) to **agree on a second,
content-distinct replay** — Dead Rail, whose 5 seam crossings exercise the
wrap-aware matcher (yaw Δ p90 48.5° vs Oasis 24.4°).

**Verdict: HIT at ring-record `+0x30` — score 1.0, flatness 1.0, matched
56/56 dumps, median per-dump lag −2.5 s (spread 5.6 s).** The facing
candidate is publication-ready per the pre-staged gate; the yaw publication
package (`docs/operations/g1-yaw-publication-draft.md`) moves from PENDING
to READY, pending operator approval.

## Session record

- **Launch:** launcher reached `OK OfflineReplayVerified`;
  `battleSession=019ff209-a4cb-7c2b-88e3-2b0eaf65c490` anchored at the G2
  clock moment (uncertainty 1 s ≤ the 2 s bound). One guarded lease; no
  other host/game processes.
- **QUALIFY (offline):** Dead Rail replay; target entity **2549408**,
  27 turn segments with |packet yaw delta| ≥ 0.1 rad.
- **DUMP (live):** 56 ring-record dumps (`regionLength 256`, anchor
  `ring-record`) via `/discover/entity-region`, every dump attested
  `sameDecodedClockProven=true` (G2 bound ≤ 2 s) →
  `.data/facing-snapshots-089.json`.
- **VERDICT (live, at-session):** `yaw-diff` with the then-current
  **one-directional shared lag path** (memory-behind-packet only, 0..8 s) →
  **HONEST-NEGATIVE**: top candidate `0xA0`, score 0.304, flatness 0.2.
  Recorded as a no-hit per the template branch; chain field untouched.
- **VERDICT (corrected, offline on the SAME immutable dumps):** root cause
  of the negative was a matcher limitation, not a wrong offset — the G2
  replay-clock LABEL skew is **opposite in sign per replay** (Oasis: memory
  LAGS the label ~3–5 s; Dead Rail: memory LEADS ~2–5 s) and **per-dump
  variable**. The one-directional shared search cannot see the Dead Rail
  sign. The additive per-dump bounded bidirectional lag path
  (`--per-dump-lag --memory-lead-seconds`, HeadingCorrelator) re-verdicts
  the identical evidence:

| Evidence | Path | Offset | Score | Flatness | Matched | Median lag | Spread |
|---|---|---|---|---|---|---|---|
| Dead Rail 089 | per-dump bidirectional | `+0x30` | 1.0 | 1.0 | 56/56 | **−2.5 s** | 5.6 s |
| Oasis 088 (re-run) | per-dump bidirectional | `+0x30` | 1.0 | 1.0 | 48/48 | **+4.8 s** | 13.1 s |
| Dead Rail 089 | shared bidirectional (no per-dump) | `+0x30` | 1.0 | 1.0 | 56/56 | −2.5 s | — |

The shared-bidirectional path also hits on Dead Rail because the stationary
dumps match at any lag; the per-dump path additionally EXPOSES the 5.6 s
skew spread (the structure the shared path hides). Unit tests
(`HeadingCorrelatorTests`) prove per-dump on synthetic variable-skew data
where the shared path caps at 0.5, both signs, and the flatness-based decoy
demotion under per-dump lag.

## Root-cause finding (G2 label skew)

- The ring record's yaw field is **byte-exact against the decoded packet
  yaw in BOTH replays** once the label skew is accounted for (median error
  0.000°). The automated verdict previously failed because the lag path was
  one-directional + shared.
- The skew is a property of the **replay-clock LABEL**, not of the ring
  record: the memory value at label-time t equals the packet yaw at
  t − lag_i, where lag_i is per-dump variable and opposite in sign between
  replays. This is the same class of finding as the OD-RECOVERY-082 G2
  same-decoded-clock proof — the label and the memory are coherent once the
  bounded shift is allowed.
- A zero/constant-filled field cannot fake this: the fixture discipline
  (fill decoys with a value the packet timeline never contains) and the
  control-dump flatness both demote drifters. **Uniqueness audit (2026-08-11,
  independent Python scan of the SAME immutable dumps): `+0x30` is the
  UNIQUE 4-byte offset matching all dumps at ≤ 0.05 rad on BOTH replays;
  the nearest competitor is > 0.5 rad off on at least one dump, despite
  7.8 % (Oasis) / 14.1 % (Dead Rail) zero-filled float32 slots in the raw
  evidence — the degenerate zero-fill decoy cannot occur because the
  tank's yaw never sits at 0.0 in the stationary stretches.**

## Ledger section — OD-RECOVERY-089 (filled)

```yaml
sessionId: OD-RECOVERY-089
status: CLOSED
replay: Dead Rail (content-distinct, 5 seam crossings)
verdict: Hit at +0x30
score: 1.0
matchedDumps: 56/56
medianPerDumpLagSeconds: -2.5
lagSpreadSeconds: 5.6
flatness: 1.0
twoReplayRepeatability: true
```

## After this session

- `twoReplayRepeatability = true` for yaw: the facing candidate is
  publication-ready. Apply ONLY via `docs/operations/g1-yaw-publication-draft.md`
  (operator-approved numeric publication if promoted); the offset table
  stays frozen, `offsets.playerYaw` stays 0, `fieldValidation.playerYaw`
  stays `Stale` until then.
- The Phase-4 two-replay HP rule (Dead Rail victim 2549399, `hp-diff`
  session) still gates HP publication separately.
- CAM-001 v7 remains the next camera workstream; item 7 (hardware
  atomicity) stays LAST by design.
