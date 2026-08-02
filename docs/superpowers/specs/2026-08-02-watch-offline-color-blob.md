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
2. Search ROI roughly **x 20–55%**, **y 40–68%** (left/center dialog band;
   excludes green **LOG IN AND WATCH** on the right).
3. Orange pixel heuristic: high R, mid G, low B, R≫B (excludes green).
4. If blob area ≥ minimum → click window-relative centroid (once per round).
5. Poll gate; re-capture; success only if gate verified **and** post blob area
   is below dismiss threshold (or ≪ pre-click area).
6. Always write `%TEMP%\wotb-watch-offline-verify.png`.
7. Exit `0` only on dual success; `3` if retries exhausted.

## Non-goals

Pause/resume, other dialogs, accessibility/UIA, committed button templates.

## Verification

Live managed launch: script exit 0; screenshot shows no login dialog; agent may
still spot-check the PNG.
