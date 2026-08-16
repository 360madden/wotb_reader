# OD-RECOVERY-102 — Oasis batch position re-verdict (offline)

**Date:** 2026-08-16 (UTC)

**Status:** Hit (offline re-verdict); no live session, no promotion. The
item-8 offline evidence lane (`next-10-actions.md`) is closed.

## What happened

Item 8 re-verdicts the stored Oasis batch positions from the item-7 live
cluster (`OD-RECOVERY-100`) with a bounded bidirectional per-dump clock
window, the same class of re-verdict the yaw (`OD-089`) and HP (`OD-091`)
lanes used when a one-directional/symmetric-2s window hid an opposite-sign
label skew.

Ground truth and capture:

- Capture: immutable `.data/cluster-batch-retry-20260814-2123.json`
  (schema `wotbtreader.od.batch-rehearsal.dumps.v1`, managed-launch label
  `01a00228-024c-7e6e-afb0-2dc12e52b061`, 3 passes at 199.53 / 220.70 /
  250.88 s).
- Decoded ground truth: Oasis Palms session
  `019fee20-9315-70b7-a92c-379f41f69532` (14-tank roster, entity ids
  3760565..3760578, 26,822 position samples).

## Result

| Window | Verdict | Note |
|---|---|---|
| ±2 s (G2 same-decoded-clock bound) | 33/41 (1 `EntityNotFound` skipped) | Reproduces `OD-RECOVERY-100` exactly; 8 moving samples at 10.25–29.61 m from nearest |
| ±5 s bounded bidirectional per-dump | **41/41 PASS** | 16 window-rescued matches all at **0.00 m**, implied per-dump offset **−4.24 s, spread 0.11 s** (memory-behind) |

The 8 prior misses are the documented Oasis memory-apply lag (yaw measured
+4.8 s median, `OD-RECOVERY-088`), now reproduced byte-exact on the batch
POSITION field (ring-record `+0x10` float32 triple). Because the batch reads
all 14 entities in one `/discover/entity-regions` call under one G2 clock
attestation, the per-dump skew is essentially uniform (0.11 s spread) — a
tighter, more decisive version of the per-dump yaw spread (13.1 s across
separate single-entity calls).

## What it does and does not establish

- The honest negative is resolved: the 8 misses were label skew, not read
  failures; the batch surface's position read is byte-exact once the ~4.2 s
  lag is admitted.
- No position verdict is promoted (a cross-check, not a publication); the
  ±2 s same-decoded-clock bound remains the strict alignment gate.
- The lag is measured, not eliminated; `HardwareAtomicReadProven` stays
  false.

## Tooling change

`scripts/python/batch-rehearsal-crosscheck.py --compare`:

- `--session` is now the authoritative decoded ground-truth session. When
  the dumps carry a different `sessionId` (a managed-launch label), the tool
  warns and still cross-checks against the named decoded session. This closes
  a latent footgun where `--session` validated one DB row while the queries
  used the dumps' `sessionId`.
- Window-rescued matches now report their implied per-dump offset median +
  spread (memory-behind negative, memory-ahead positive).
- Defaults unchanged (tolerance 2 m, window 2 s), so the `OD-086`/`OD-100`
  reproductions stay identical.
- Self-test extended with a window-rescue and a session-mismatch case;
  `python scripts/python/batch-rehearsal-crosscheck.py --self-test` passes.

## Reproduce

```
python scripts/python/batch-rehearsal-crosscheck.py \
  --session 019fee20-9315-70b7-a92c-379f41f69532 \
  --dumps .data/cluster-batch-retry-20260814-2123.json \
  --tolerance 2.0 --window 5.0
```

## Validation

- `python scripts/python/batch-rehearsal-crosscheck.py --self-test` PASS.
- ±2 s reproduction = 33/41 (matches `OD-RECOVERY-100`); ±5 s re-verdict =
  41/41 PASS, implied offset median −4.24 s / spread 0.11 s.
- Full `scripts/validate.ps1` gate run as part of the merge.

## Unknowns / next

- The post-contract two-replay batch witness (item 7 Branch B) is unchanged:
  approved launches only, `ConsistentDoubleRead=true`, `RegionReadAttempts=1`,
  both tear flags false, `HardwareAtomicReadProven` stays false until then.
- The Oasis/Dead Rail memory-apply lag remains measured, not eliminated; a
  durable clock correction is out of scope for this re-verdict.
