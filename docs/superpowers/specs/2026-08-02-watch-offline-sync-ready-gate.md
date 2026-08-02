# Design: WATCH OFFLINE ready gate (sync dim / overlay)

Date: 2026-08-02  
Status: approved-to-implement (owner: dim+sync screenshots)  
Scope: Reliable click of **WATCH OFFLINE** only when the dialog is interactive.

## Problem

The orange button appears before the client finishes account sync. A few seconds
later the dialog **dims** and shows **“Synchronizing account…”**. Clicks during
that window often do not register (dialog may dismiss while Host stays
`awaiting_evidence`). Clicking on first orange sighting is too early; a long
blind settle is too late (dialog error).

## Visual evidence (owner screenshots)

| State | Dialog mean L | Orange px (existing heuristic) | Notes |
|-------|---------------:|-------------------------------:|-------|
| Ready (bright) | ~59 | ~8287 | Full brightness, no sync text |
| Syncing (dim) | ~31 | ~63 | Dim + “Synchronizing account…” |

Primary **do-not-click** signal: **dimming** (luminance collapse). Secondary:
orange blob count collapses while dimmed. White-pixel counts alone are unreliable
(button labels are also white when ready).

## State machine

```text
LookingForDialog
  → (orange present) WaitingForReady
       → (lum low / orange collapsed) SeenSyncing
            → (lum high AND orange strong, stable N samples) Ready
                 → hold → click
       → (grace elapsed without dim, still bright+orange stable) Ready
                 → hold → click
```

**Ready** requires all of:
1. Orange blob ≥ strong threshold (default 2000 px in dialog ROI).
2. Dialog-ROI mean luminance ≥ ready floor (default 45).
3. Either `SeenSyncing` was observed, **or** grace seconds elapsed since first
   dialog sighting (covers “sync already finished before we looked”).
4. Conditions stable for N samples (~3 × 500ms), then ReadyHoldSeconds (~2s).

Never click green **LOG IN AND WATCH**.

## Implementation

`scripts/click-watch-offline.ps1` — extend capture analysis with dialog mean
luminance; replace “stable orange then click” with the state machine above.
`scripts/launch-offline-replay-for-od.ps1` — keep calling the clicker; no blind
long settle.

## Timeouts

- AppearTimeoutSeconds: wait for first dialog (orange or dim dialog).
- ReadyTimeoutSeconds: wait for ready after dialog (covers sync duration).
- Exit 5 if ready never reached; exit 3 if click rounds fail gate dual-check.

## Non-goals

OCR of “Synchronizing account…”, template matching, UIA.
