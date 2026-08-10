# Handoff — 2026-08-10: HP correlator hardened — flatness rank + Strict confirmation

## What changed

1. **Flatness rank in `HpDamageCorrelator`.** The Lenient mode had a real
   false-positive surface: a monotonic drain (ammo/fuel/energy) that drops
   ≥ every window's damage sum matches every damage window — score 1.0 AND
   precision 1.0 (precision counts damage windows only), so it TIED HP and
   won on offset. The candidate model now carries `Flatness` (fraction of
   zero-damage control windows in which the field was unchanged; 1.0 when
   there are none), and the ranking is **score → flatness → precision →
   offset**. HP is flat except when hit; drains keep dropping through
   control windows — proven by
   `Correlate_Lenient_DrainingDecoy_RanksBelowHp_OnFlatness`.

2. **Strict confirmation documented as load-bearing.** A magnitude-mismatched
   decoy (another victim's HP, or a heavy drain) that is flat in control
   windows still ties Lenient on score AND flatness — flatness cannot
   separate it. The discriminator is STRICT: HP's drops equal the exact
   sums (non-overkill windows); the decoy's never do. The verdict contract
   now requires: **(a)** ≥ 2 damage windows score 1.0 in Lenient, **(b)**
   flatness 1.0 across the control dumps, **(c)** ≥ 2 windows confirmed
   under Strict (exact-sum drops), **(d)** matched offsets agree across the
   two independent replays. Proven by
   `Correlate_Strict_ExcludesMagnitudeMismatchedDecoy_ConfirmsHp` (Lenient
   ties → decoy wins on offset; Strict excludes it, HP confirmed 2/2).

## Why it matters for the live session

The verdict contract was "score 1.0 in Lenient over ≥ 2 windows" — on a
real tank-record dump (0x100 = 64 aligned int32 slots), a draining or
heavy-dropping field could have produced a false HIT. Now the three
independent discriminators (Lenient score, flatness, Strict exactness) are
all required, and the control dumps the plan already mandates are
load-bearing. The session flow's "Lenient first — overkill" step is
unchanged; the verdict step adds the Strict re-run.

## Status

- HP offline side: **complete and hardened** — ground truth, correlator
  (Strict/Lenient + flatness), compose proof, entity-base exposure,
  mechanism proven on the published table, victims qualified for both
  replays (Oasis Palms 3760578, Dead Rail 2549399), and a
  false-positive-hardened verdict contract.
- Remaining: the gated live session (one bounded
  `EntityRecordRegionReadRequest` addition + one run), fully pre-staged.

## Gates

- `scripts/validate.ps1` exit 0 — Core.Tests 121 → 123 (2 new), all 12
  projects green; offset validator PASS.
- No new files added in this phase's code change; file tree stable.
