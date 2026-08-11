# X1 policy memo — live-game overlay track (DRAFT for explicit approval)

**Date:** 2026-08-11
**Status:** DRAFT — nothing in this memo is in effect until the operator
(project owner) explicitly approves it. It is the Phase-5 gate document the
roadmap requires before any live-online work (roadmap X1: "Policy memo:
ADR-0002 relaxation decision, ToS risk, scope — gate: explicit user
approval").

## What this memo does NOT do

- It does **not** authorize live testing. All live sessions remain gated on
  separate approvals (OD-RECOVERY-086/087/088 — and those run against
  **offline replay playback**, not online matches).
- It does **not** change the code-enforced gate. The
  `OfflineReplayVerified` requirement on every scanner/observation path is
  unchanged by ADR 0002's 2026-08-09 amendment and stays in force for the
  replay work.
- It does **not** propose match automation, input injection, or any action
  inside an online battle beyond reading memory.

It is the decision document: whether the *long-term* ambition (live online
matches, the reason this whole replay-first path exists) proceeds, and under
what scope.

## Context

The project exists to build a W2S overlay/HUD for World of Tanks Blitz:
nameplates, HP bars, markers, beacons over the game window. Replays are the
reliable, safe test bed (that is why every discovery is replay-first). The
end goal the user stated: eventually the same overlay on **live online
games**. ADR 0002 (2026-07-26, amended 2026-08-09) governs evidence and
offline-only automation; the amendment removed the agent-facing
"offline-only, never automate online matches" hard constraint, keeping the
code-enforced offline gate. That amendment did **not** itself approve live
work — it cleared the way for this memo to decide it explicitly.

## The decision (three options)

| Option | What it means | Trade-off |
|---|---|---|
| **A. Live read-only overlay (recommended)** | The overlay reads the same fields already proven on replays (position, hull yaw, HP after L1/L2) from the user's own live client during the user's own match. Read-only memory observation. No automation, no input, no match participation beyond the user playing normally. | Delivers the end goal; risk is bounded to read-only observation of the user's own process (see Risks). |
| **B. Live read-only + record** | A, plus optionally saving observed telemetry locally for the user's own analysis. | Adds a data-handling surface; privacy/evidence discipline must extend to live data. |
| **C. Replays only, permanently** | The overlay never touches a live match; live ambition dropped. | Zero new risk; abandons the stated end goal. |

## ToS / legal risk analysis (honest, not lawyer-grade)

1. **Wargaming ToS on third-party software.** Reading the game's memory
   while an online match runs is the classic "third-party software that
   modifies/reads the client" territory. Mitigations that materially reduce
   but do not eliminate risk: (a) the tool never writes the game's memory,
   never injects, never automates input, never modifies the install (all
   hard repo constraints already); (b) it is a personal project used by the
   owner on the owner's own account; (c) it exposes no competitive advantage
   beyond what a HUD renders — though an overlay showing all tanks' HP/position
   in a live match IS a competitive-information tool, which is exactly what
   any overlay is; the risk is inherent to the feature, not an implementation
   artifact.
2. **What a memory READ alone risks.** Read-only observation is
   categorically harder to detect and ban than writes/injection, but it is
   still detectable by an anti-cheat that reads process memory
   fingerprints. WoTB's anti-cheat posture and enforcement history are
   outside this repo's knowledge; the honest statement is "unknown,
   non-zero, and the user's decision to bear."
3. **Account risk is the real exposure.** Worst realistic case is a
   client/account sanction on the user's own account, not a legal action
   against a personal tool. The user owns that risk.

## Scope proposal (if A/B approved)

- **Field scope:** only fields proven on replays first — position, hull yaw
  (after L2), HP (after L1). Nothing new is read live that was not first
  discovered offline; the replay-first pipeline becomes the live-field
  pipeline unchanged.
- **Session scope:** the user's own live matches only, on the user's own
  install/account. No spectating, no other players' clients, no online
  automation.
- **Behavior scope:** the overlay renders; it never writes game memory,
  injects, sends input, or automates the match. The existing
  `MutationProtectionMiddleware` + loopback-capability model carries over.
- **Evidence scope (option B):** live telemetry saved under the same
  evidence discipline (append-only, no fabrication), but with the privacy
  boundary extended: live data is never published, never uploaded, never
  shared — local only.
- **Sequencing:** this memo's approval unlocks the Phase-5 design work
  (live-mode gate relaxation on the scanner path, live session drivers).
  It does NOT skip OD-RECOVERY-086/087/088, and the hardware-atomicity
  proof (item 7) still comes LAST.

## Risks table

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| ToS violation / account sanction | unknown (WoTB enforcement posture outside repo knowledge) | account loss | read-only, no writes/injection/automation; owner's own account; owner decision |
| Anti-cheat detection of memory reads | low for reads alone, non-zero | same as above | reads only; no driver/kernel work; no signature-prone patterns |
| Accidental automation of a live match | low (all input paths are allowlisted replay controls) | ban | keep every input path replay-only; live mode = observation only, zero input |
| Data hygiene (option B) | low | privacy | local-only, append-only, never published; extend the privacy scan to live artifacts |
| Scope creep into "always-on cheat HUD" | medium (feature is inherently competitive-info) | ToS exposure | field scope pinned to replay-proven fields; spot/see-through features (X5) stay explicitly out |

## Recommendation

**Option A**, with the caveat that it is a personal, owner-borne risk
decision: the tool stays read-only and replay-first, and every live field
must first be proven offline. Approve A (or A+B) explicitly to unlock
Phase-5 design; reject to keep the project replay-only (option C). The
memo records the decision either way.

## Approval

- [ ] **Approved — Option A** (read-only live overlay, replay-proven fields only)
- [ ] **Approved — Option A+B** (read-only + local-only live record)
- [ ] **Rejected — Option C** (replays only, live ambition dropped)
- Decision date: ____
- Approver: project owner
