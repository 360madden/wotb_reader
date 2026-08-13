# Next 10 actions (roadmap-anchored)

**Purpose:** the durable, sequenced top-10 follow-up list for continued
development. Anchored to `docs/operations/product-roadmap.md` (the forward
plan), the ledger's *Next planned session* row, and the newest handoff.
Refreshed at every session end — the session ritual closes with this list,
so the next session (human or agent) starts from a current, ordered plan
instead of re-deriving one.

**Sequencing rule:** live items are clustered to share ONE approved launch
(one game start = max evidence); offline hardening runs in parallel with
any live work; owner-gated items sit behind the evidence they consume.

| # | Action | Roadmap anchor | Why now | Gate |
|---|---|---|---|---|
| 1 | **Live-verify the hardened clean-run completion marker** (driver exit 0 → marker file → chain re-run exits 7 fast) | OD-099 durable fix (`scripts/od-replay-completion.ps1`) | `623b9df` changed the driver's write condition; the clean-run path (schedule completes, no teardown) must be proven live end-to-end before building on it | 1 approved launch |
| 2 | **Batch rehearsal re-run** — `invoke-batch-rehearsal.ps1 -LiveAcquire -EnumerateLive -Times 60,150,220 -FailOnMiss`, absolute replay path | Item 7 Branch B step 3 + X2/X2b rehearsal | Re-establishes the clean live verdict AND delivers the step-3 read-pass measurements (100% byte-identical, zero `region-unstable-snapshot`) | same launch as #1 |
| 3 | **Branch B step-4 camera double-reads** — extend the camera verify tool (vision-only today) to measure double-read stability, capture live | Item 7 Branch B step 4 | Closes the last unclaimed live lane in the hardware-atomicity plan | same launch as #1 |
| 4 | **Live-frame `DamageDealt` E2E** — mid-battle `GET /live/frame`, own-row is a real value (not honest-0) with exact decoded joins | X4 (G2 consumption) | Proves the published `vftableScan` chain read surface end-to-end | same launch as #1 |
| 5 | **Approve + apply the `ConsistentDoubleRead` flag-flip proposal** | Item 7 Branch B step 2 (owner-gated shared contract, drafted) | Lets #2–3's measurements claim the flag; unblocks item-7 DoD | owner approval + gate |
| 6 | **Pester smoke tests for the completion-marker helper** (never-throw, fail-open, clean-run contracts under the gate, not scratch harnesses) | OD-099 durable fix hardening | Makes `623b9df`'s fixes regression-proof; offline, no launch | full `validate.ps1` |
| 7 | **Adversarial review of launcher pre-flight / clicker ready-gate / chain log-polling** (same fresh-eyes throw + state-machine tracing that found the marker defects) | script tooling quality | The review technique just caught 2 real bugs; the launcher/clicker/chain are the same risk class | offline; fix only genuine defects |
| 8 | **Pen-chance HUD (PN) — PN-4 validation loop next** (score the model vs decoded type-8/type-32 shot outcomes). PN-1/2/3 DONE: armor/shell/gun parsers + install data service, pure pen math, and the reticle-centered badge wired end-to-end (frame → response → WPF HUD). Honest limits: front-only armor, stock AP shell, nominal thickness | Phase 6 (`docs/operations/pen-chance-design.md`) | The remaining proof is scoring, gated on two offline prerequisites: a plate-slope `.scg` polygon-geometry parser (PN-5) and/or the live camera aim, plus the type-8 flag-byte / type-32 decode-lane surface | offline; PN-4 no launch |
| 9 | **L4 replayTime session** — chained clock (`GameCore 0x04095c88 → … → [BWServerConnection+0x58]+0x90`), `-ArmSourceOnFirstHit` (expected first hit = CRT copy site) | Phase 2 L4 (next discovery lane) | Unclaimed discovery lane; needs its own approved launch | 1 approved launch |
| 10 | **T1 turret-facing + lock-on discovery** — live-behavioral per the pre-staged design (camera-yaw correlation as the discriminator) | Phase 2 T1 (last Phase-2 session type) | Closes the roadmap's discovery table; pre-staged `docs/operations/t1-turret-traversal-design.md` | 1 approved launch |

## Wait-list (deliberately outside the top 10)

- **P1 velocity `+0x28` promotion** (Phase 3; G0 record says NOT promoted —
  revisit only if a live velocity consumer needs the field).
- **Phase 5 live overlay (policy-gated)** — X5 spotting model etc.; requires
  the policy gate, not just engineering.
- **Launcher pre-flight reorder** (move the marker check before the CLI
  version probe — out-of-scope finding, trivial, fold into #7).
- **Attacker-side damage write trace** (runtime write-interceptor /
  instruction-snapshot on the d0 increment sites — closes the L3 residual;
  needs a launch, sits behind #1–4).
- **G3+ publication generalization of `rehearse-offset-apply.py`** — only
  when a new publication package appears.
- **Minimap texture mapping (arena-id → name-based folder)** (Phase 4 V4 gap,
  pinned by tests) — re-enabling texture-under-dots; now shares the PN-1
  install game-data extraction capability, so it folds into the same
  offline static-data lane.

## Refresh procedure (session end)

1. Check off completed items; drop them from the table (history lives in the
   ledger + handoffs — this list is forward-looking only).
2. Re-anchor against the roadmap: any newly completed roadmap row may open a
   new action; any newly approved launch may collapse live items.
3. Keep the sequencing rule: cluster live items per launch, run offline
   hardening in parallel, gate owner-gated items behind their evidence.
4. If a session is live-launch-gated (no game running / no approval), say so
   in the table — the list stays the offline-eligible subset.
