# Next 10 actions (roadmap-anchored)

**Purpose:** the durable, sequenced top-10 follow-up list for continued
development. Anchored to `docs/operations/product-roadmap.md` (the forward
plan), the ledger's *Next planned session* row, and the newest handoff.
Refreshed after the penetration v0.3 fidelity audit, safe operability slice,
exact-build hard-joint negative, bounded exact-input source verdict, managed
capture contract implementation, BLK-0027 registration, the serialized HUD empty/replay
smoke check, HUD game-window diagnostics, HUD render-health/export work, replay
playback-continuity hardening, the game-window startup diagnosis, and the
HUD v0.7.0-alpha visual + interaction ship (frame-driven combat animations,
scrubbable in-HUD playback bar, low-HP killable-target pulse, and the 20-round
fail-closed hardening pass), the Oasis batch position re-verdict
(OD-RECOVERY-102, bounded bidirectional per-dump clock window), and the
rendezvous capability-file ACL hardening (explicit protected owner-only
`web.json` ACL with post-move reparse/DACL verification), and the penetration
owner-census evidence source (hash-bound `VehicleGun`/`VehicleGunRotator`/
`AvatarGunAgent` vftable RVAs + gated census + `pen-capture` endpoint), and
the first live owner-census capture (43 candidates, `vehicleGun=42 /
vehicleGunRotator=1 / avatarGunAgent=1`, stable; honest ambiguous-ownership
negative) on 2026-08-16.

**Sequencing rule:** live items are clustered to share ONE approved launch
(one game start = max evidence); offline hardening runs in parallel with any
live work; owner-gated items sit behind the evidence they consume.

| # | Action | Roadmap anchor | Why now | Gate |
|---|---|---|---|---|
| 1 | **Isolate gun elevation and join a decoded viewpoint shot** to the published marker ray (two-replay hull-independent marker yaw is already live) | Phase 6 v0.3 G1 items 2/5; BLK-0027 | Two content-distinct replays now agree (`2026-08-16-pen-semantic-fields-live.md`): published marker finite/unit/stable; yaw bins move while hull stays in one bin (8 then 10 independent windows). Reload enum is not a shell. Elevation and shot join are the remaining G1 item-5 proofs | one approved launch can collect both; no promotion until those pass |
| 2 | **Adjudicate the capture verdict and promote only proven exact fields** | Phase 6 v0.3 G2/G4 | The first census result is an honest ambiguous-ownership negative (43 candidates, nothing promoted); no shared weapon/aim contract or colored badge may be enabled until the walk + shell/aim/ray proofs land | two content-distinct positive repeats |
| 3 | **Choose whether to fund a deeper exact-build `ArmorComponent`/`ArmorConfiguration` producer trace**; do not revisit the rejected hard-joint visualization path | Phase 6 v0.3 item 1/7; BLK-0027 | Bounded RTTI triage found no authoritative producer; another live read would be speculative without a producer hypothesis | offline producer evidence or explicit no-go |
| 4 | **Implement exact weapon, aim, and armor ports only for proven cohorts** | Phase 6 v0.3 G4 | Provenance gates already prevent nominal/manual/camera data from masquerading as exact | G1/G2 pass |
| 5 | **Build the immutable representative penetration corpus** — at least 12 replays, 500 eligible shots, and 30 rows per important cohort | Phase 6 v0.3 G5/G6 | Coverage and accuracy cannot be claimed from the old two-replay confounded sample | exact inputs implemented |
| 6 | **Run primary cohort coverage/accuracy/ricochet/interval gates with exact unknown accounting** | Phase 6 v0.3 items 3/4/8/9 | Release thresholds are frozen in `penetration-v0.3-plan.md` | #5 complete |
| 7 | **Post-contract two-replay batch witness** with attempts 1 and both tear flags false | Item 7 Branch B | Direct no-transient-tear wire evidence remains the item-7 gate | 2 approved launches |
| 8 | **Complete the HUD live visual ship review** (code ship is done at v0.7.0-alpha) | HUD M0/M1; Phase 6 v0.3 G6 for penetration release | Code-level visuals, combat animations, scrubbing, and low-HP cues are merged; exclusive fullscreen, a second DPI scale, scene contrast, and penetration fidelity still need one owner-supervised live pass | one owner-supervised visual pass across actual fullscreen/DPI/scenes; #1-6 and full gate remain required for penetration release |

## Wait-list (deliberately outside the top 10)

- **P1 velocity `+0x28` promotion** (Phase 3; G0 record says NOT promoted) —
  revisit only if a live velocity consumer needs the field.
- **Phase 5 live overlay policy work** — X5 spotting model etc.; requires its
  policy gate, not just engineering.
- **G3+ publication generalization of `rehearse-offset-apply.py`** — only when
  a new publication package appears.
- **Exact per-plate armor thickness mapping** is now BLK-0027, not an accepted
  nominal fallback. The hard-joint visualization hypothesis is rejected.
- **Loaded-shell and exact gun-ray resolution** remain G1 blockers; manual
  shell selection and CAM-013 are diagnostics-only in v0.3.

## Refresh procedure (session end)

1. Check off completed items; drop them from the table (history lives in the
   ledger + handoffs — this list is forward-looking only).
2. Re-anchor against the roadmap: any newly completed roadmap row may open a
   new action; any newly approved launch may collapse live items.
3. Keep the sequencing rule: cluster live items per launch, run offline
   hardening in parallel, and gate owner-gated items behind their evidence.
4. If a session is live-launch-gated (no game running / no approval), say so in
   the table — the list stays the offline-eligible subset.
