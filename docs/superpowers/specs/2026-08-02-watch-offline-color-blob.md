# Design: Reliable WATCH OFFLINE dismiss (color blob)

Date: 2026-08-02  
Status: approved (owner: color-blob approach)  
Scope: **only** dismissing the WoT Blitz “You are not logged in” → **WATCH OFFLINE** dialog.

## Problem

Blind percentage clicks miss the custom-drawn orange button. Gate
`OfflineReplayVerified` can also flip from lifecycle evidence while the dialog
is still visible, so gate-only success is insufficient.

## Approach

Screenshot → find largest orange/amber blob in a dialog ROI → click centroid →
require (1) gate `OfflineReplayVerified` and (2) orange blob gone / shrunk.

Rejected: template matching (DPI/resolution brittle); fixed % grids (current failure mode).

## Behavior (`scripts/click-watch-offline.ps1`)

1. Capture game window (`PrintWindow` / screen blit fallback).
2. **Sync-dim ready gate:** poll dialog mean luminance + orange blob until the
   dialog is bright and interactive (see
   [`2026-08-02-watch-offline-sync-ready-gate.md`](2026-08-02-watch-offline-sync-ready-gate.md))
   — not a blind timer. Requires stable bright+orange samples (≥3 at ~500ms),
   optionally after sync dim was observed or a grace period elapsed, then hold
   ~2s before click.
3. Search ROI roughly **x 20–55%**, **y 40–68%** (left/center dialog band;
   excludes green **LOG IN AND WATCH** on the right).
4. Orange pixel heuristic: high R, mid G, low B, R≫B (excludes green).
5. Click window-relative centroid; confirming jitter click.
6. Poll gate with a **fresh rendezvous capability each call**; re-capture;
   success only if gate verified **and** post blob area is below dismiss
   threshold (or ≪ pre-click area).
7. Always write `%TEMP%\wotb-watch-offline-verify.png`.
8. Exit `0` only on dual success; `3` if retries exhausted; `5` if ready gate
   never satisfied; `6` if host is already `Denied` (re-run
   `scripts/launch-offline-replay-for-od.ps1`).

Prerequisite: managed OD launch via `scripts/launch-offline-replay-for-od.ps1`
(folder `.wotbreplay` → import → managed launch → visual Watch Offline).
File-association playback alone does not satisfy the gate.

## Non-goals

Pause/resume, other dialogs, accessibility/UIA, committed button templates.

## Verification

`scripts/launch-offline-replay-for-od.ps1` exit 0; screenshot shows replay HUD
(not garage) and no login dialog; agent may still spot-check the PNG.
