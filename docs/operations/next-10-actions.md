# Next 10 actions (roadmap-anchored)

**Purpose:** the durable, sequenced top-10 follow-up list for continued
development. Anchored to `docs/operations/product-roadmap.md` (the forward
plan), the ledger's *Next planned session* row, and the newest handoff.
Refreshed after the two-replay Item-7 cluster, batch witness contract apply,
batch-driver live-support hardening, PN workflow re-anchor, and the corrected
Sol-only Codex reasoning policy on 2026-08-14. The agent-policy work changes
how the list is executed, not the product ordering below.

**Sequencing rule:** live items are clustered to share ONE approved launch
(one game start = max evidence); offline hardening runs in parallel with any
live work; owner-gated items sit behind the evidence they consume.

| # | Action | Roadmap anchor | Why now | Gate |
|---|---|---|---|---|
| 1 | **Owner ship review for the `v0.2.0-alpha` PN prototype** — approve the evidence-backed badge, documented limits, and committed release diff; then run the five-step manual HUD smoke | Phase 6 PN-4/PN-5; repeat proof: `2026-08-14-pn4-second-replay-regression.md` | Two content-distinct live replays pass the aim-source regression; the versioned baseline makes subsequent fixes and capability increments traceable | owner review |
| 2 | **Re-verdict the stored Oasis batch positions with a bounded bidirectional per-dump clock window** | X2 cross-check / OD-RECOVERY-100 honest negative | The read witness passed, but 8/41 moving samples exceeded the old +/-2 s matcher window; diagnose label skew without another launch | offline |
| 3 | **Normalize completion-marker timestamp comparison across PowerShell 5.1 and 7** | OD-099 durable fix follow-up | The exact Windows-first chain correctly exits 7, but direct pwsh inspection auto-deserializes ISO UTC into `DateTime` and currently fails open | offline |
| 4 | **Fix the batch driver's literal `{0}` progress placeholders and pin the operator output** | X2 rehearsal operability | The live cluster exposed PowerShell formatting-precedence mistakes that obscure counts/times without affecting artifacts | offline |
| 5 | **Post-contract two-replay batch witness** — require every resolved item `ConsistentDoubleRead=true`, `RegionReadAttempts=1`, `RegionTearObserved=false`, `EntityBaseTearObserved=false` | Item 7 Branch B step 3 | Two-replay timing and camera proof are complete; direct no-transient-tear wire evidence is the remaining item-7 gate | 2 approved launches after #2-4 |
| 6 | **T1 turret-facing + lock-on discovery** — live-behavioral traversal with camera-yaw correlation | Phase 2 T1 (`t1-turret-traversal-design.md`) | Optional exact gun-lock research; not a blocker for the CAM-013-based pen badge | 1 approved launch |
| 7 | **L4 replayTime session** — chained clock and first-hit instruction snapshot | Phase 2 L4 | Unclaimed discovery lane after the overlay/PN proof work | 1 approved launch |
| 8 | **Attacker-side damage write trace** (runtime write-interceptor/instruction snapshot) | Phase 2 L3 residual | Closes the remaining write-path evidence after the published damage-dealt consumption lane | 1 approved launch |
| 9 | **Minimap terrain-alignment smoke** — run the HUD against a decoded replay for a texture-bearing map | Phase 4 V4 follow-up | Arena `1` transport is install-proven, but dot-versus-terrain alignment needs a compatible replay frame | compatible texture-bearing replay |
| 10 | **Phase-5 live-overlay policy gate** — define the evidence and fail-closed rules for any spotting/visibility model before implementation | Phase 5 X5 | Prevents engineering a visibility claim whose policy/evidence boundary is still undecided | owner design review |

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
