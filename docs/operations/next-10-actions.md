# Next 10 actions (roadmap-anchored)

**Purpose:** the durable, sequenced top-10 follow-up list for continued
development. Anchored to `docs/operations/product-roadmap.md` (the forward
plan), the ledger's *Next planned session* row, and the newest handoff.
Refreshed after the launch-marker ACL confirmation hardening on 2026-08-14.

**Sequencing rule:** live items are clustered to share ONE approved launch
(one game start = max evidence); offline hardening runs in parallel with any
live work; owner-gated items sit behind the evidence they consume.

| # | Action | Roadmap anchor | Why now | Gate |
|---|---|---|---|---|
| 1 | **Live-verify the hardened clean-run completion marker** (driver exit 0 -> marker file -> chain re-run exits 7 fast) | OD-099 durable fix (`scripts/od-replay-completion.ps1`) | The clean-run path still needs an end-to-end proof before more launch clusters rely on it | 1 approved launch |
| 2 | **Batch rehearsal re-run** — `invoke-batch-rehearsal.ps1 -LiveAcquire -EnumerateLive -Times 60,150,220 -FailOnMiss`, absolute replay path; retain only dumps with all three validated read-pass measurements | Item 7 Branch B step 3 + X2/X2b rehearsal | The driver now preserves the endpoint measurement instead of discarding it; the live run can finally deliver the claimed evidence | same launch as #1 |
| 3 | **Branch B step-4 camera measurement** — run the pre-staged CAM-001 v7 driver and require every camera-pose probe resolved, identity-gated, module-rooted, byte-identical, with zero `pose-double-read` failures | Item 7 Branch B step 4 | Coordinator discipline and privacy-bounded aggregation are offline-complete; the live witness is the last unclaimed half | same launch as #1 |
| 4 | **Live-frame `DamageDealt` E2E** — mid-battle `/live/frame`, own row is a real value with exact decoded joins | X4 G2 consumption | Proves the published avatar-stats chain at the shared frame seam | same launch as #1 |
| 5 | **Approve + apply the `ConsistentDoubleRead` flag-flip proposal** | Item 7 Branch B step 2 | Converts the witnessed batch/camera measurements into the shared contract flag | owner approval + gate |
| 6 | **Owner ship review for the PN prototype** — approve the evidence-backed badge, documented limits, and staged release diff | Phase 6 PN-4/PN-5; repeat proof: `2026-08-14-pn4-second-replay-regression.md` | Two content-distinct live replays now pass the aim-source regression; remaining work is review/package, not construction | owner review |
| 7 | **T1 turret-facing + lock-on discovery** — live-behavioral traversal with camera-yaw correlation | Phase 2 T1 (`t1-turret-traversal-design.md`) | Optional exact gun-lock research; not a blocker for the CAM-013-based pen badge | 1 approved launch |
| 8 | **L4 replayTime session** — chained clock and first-hit instruction snapshot | Phase 2 L4 | Unclaimed discovery lane after the overlay/PN proof work | 1 approved launch |
| 9 | **Attacker-side damage write trace** (runtime write-interceptor/instruction snapshot) | Phase 2 L3 residual | Closes the remaining write-path evidence after the published damage-dealt consumption lane | 1 approved launch |
| 10 | **Minimap terrain-alignment smoke** — run the HUD against a decoded replay for a texture-bearing map | Phase 4 V4 follow-up | Arena `1` transport is install-proven, but dot-versus-terrain alignment needs a compatible replay frame | compatible texture-bearing replay |

## Wait-list (deliberately outside the top 10)

- **P1 velocity `+0x28` promotion** (Phase 3; G0 record says NOT promoted) —
  revisit only if a live velocity consumer needs the field.
- **Phase 5 live overlay policy work** — X5 spotting model etc.; requires its
  policy gate, not just engineering.
- **G3+ publication generalization of `rehearse-offset-apply.py`** — only when
  a new publication package appears.
- **Exact per-plate armor thickness mapping** — the accessible install data
  does not map XML armor groups to collision faces; the badge stays honest
  nominal/fail-closed.
- **Loaded-shell resolution** — the replay signature is an effect-entity id;
  the manual stock-gun shell selector remains the honest product behavior.

## Refresh procedure (session end)

1. Check off completed items; drop them from the table (history lives in the
   ledger + handoffs — this list is forward-looking only).
2. Re-anchor against the roadmap: any newly completed roadmap row may open a
   new action; any newly approved launch may collapse live items.
3. Keep the sequencing rule: cluster live items per launch, run offline
   hardening in parallel, and gate owner-gated items behind their evidence.
4. If a session is live-launch-gated (no game running / no approval), say so in
   the table — the list stays the offline-eligible subset.
