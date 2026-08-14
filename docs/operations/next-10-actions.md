# Next 10 actions (roadmap-anchored)

**Purpose:** the durable, sequenced top-10 follow-up list for continued
development. Anchored to `docs/operations/product-roadmap.md` (the forward
plan), the ledger's *Next planned session* row, and the newest handoff.
Refreshed after the minimap arena-folder mapping closed on 2026-08-14.

**Sequencing rule:** live items are clustered to share ONE approved launch
(one game start = max evidence); offline hardening runs in parallel with any
live work; owner-gated items sit behind the evidence they consume.

| # | Action | Roadmap anchor | Why now | Gate |
|---|---|---|---|---|
| 1 | **Live-verify the hardened clean-run completion marker** (driver exit 0 -> marker file -> chain re-run exits 7 fast) | OD-099 durable fix (`scripts/od-replay-completion.ps1`) | The clean-run path still needs an end-to-end proof before more launch clusters rely on it | 1 approved launch |
| 2 | **Batch rehearsal re-run** — `invoke-batch-rehearsal.ps1 -LiveAcquire -EnumerateLive -Times 60,150,220 -FailOnMiss`, absolute replay path | Item 7 Branch B step 3 + X2/X2b rehearsal | Re-establishes the clean live verdict and delivers the step-3 read-pass measurements | same launch as #1 |
| 3 | **Branch B step-4 camera double-reads** — extend the camera verify tool to measure double-read stability | Item 7 Branch B step 4 | Closes the last unclaimed live lane in the hardware-atomicity plan | same launch as #1 |
| 4 | **Live-frame `DamageDealt` E2E** — mid-battle `/live/frame`, own row is a real value with exact decoded joins | X4 G2 consumption | Proves the published avatar-stats chain at the shared frame seam | same launch as #1 |
| 5 | **Approve + apply the `ConsistentDoubleRead` flag-flip proposal** | Item 7 Branch B step 2 | Converts the witnessed batch/camera measurements into the shared contract flag | owner approval + gate |
| 6 | **Owner ship review for the PN prototype** — approve the evidence-backed badge, documented limits, and staged release diff | Phase 6 PN-4/PN-5; repeat proof: `2026-08-14-pn4-second-replay-regression.md` | Two content-distinct live replays now pass the aim-source regression; remaining work is review/package, not construction | owner review |
| 7 | **T1 turret-facing + lock-on discovery** — live-behavioral traversal with camera-yaw correlation | Phase 2 T1 (`t1-turret-traversal-design.md`) | Optional exact gun-lock research; not a blocker for the CAM-013-based pen badge | 1 approved launch |
| 8 | **L4 replayTime session** — chained clock and first-hit instruction snapshot | Phase 2 L4 | Unclaimed discovery lane after the overlay/PN proof work | 1 approved launch |
| 9 | **Attacker-side damage write trace** (runtime write-interceptor/instruction snapshot) | Phase 2 L3 residual | Closes the remaining write-path evidence after the published damage-dealt consumption lane | 1 approved launch |
| 10 | **Launcher pre-flight reorder** — check the persisted completion marker before the CLI version probe | OD-099 lifecycle hardening | Avoids unnecessary install probing for a replay already known complete; the remaining independent offline item | offline |

## Wait-list (deliberately outside the top 10)

- **P1 velocity `+0x28` promotion** (Phase 3; G0 record says NOT promoted) —
  revisit only if a live velocity consumer needs the field.
- **Phase 5 live overlay policy work** — X5 spotting model etc.; requires its
  policy gate, not just engineering.
- **Minimap terrain-alignment smoke** — run the HUD against a decoded replay
  for a texture-bearing map; arena `1` transport is proven, but the two current
  ground-truth replays intentionally remain dots-only because their textures
  are absent from this exact install.
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
